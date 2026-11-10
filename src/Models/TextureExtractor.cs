using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MuninMap.Models.Unity;

namespace MuninMap.Models
{
    // Pulls the textures the exported models want straight out of the game's own asset
    // files (StreamingAssets bundles and *.assets), on a background thread, and writes
    // them as PNGs into the model library. No helper programs: works wherever the
    // server runs, because every server has the game files next to it.
    //
    // The engine marks its textures unreadable, so this reads the files the way the
    // engine does: UnityFS container -> serialized file -> Texture2D object -> pixel
    // data (DXT1/DXT5/BC7 decoded here). Nothing leaves the machine.
    internal static class TextureExtractor
    {
        public static bool Running { get; private set; }
        public static string LastReport { get; private set; } = "";
        public static int LastFound { get; private set; }
        private static Thread thread;
        // names a full scan could not find or decode: not tried again this session (a game update means a restart anyway)
        private static readonly HashSet<string> gaveUp = new HashSet<string>();

        // Kicks off a run for the given texture names when nothing is running and there is something new to look for.
        public static bool Start(ICollection<string> wanted, string modelsDir, int maxSize, Action onDone)
        {
            if (Running || wanted.Count == 0) return false;
            var names = new HashSet<string>();
            lock (gaveUp) foreach (var w in wanted) if (!gaveUp.Contains(w)) names.Add(w);
            if (names.Count == 0) return false;
            string dataDir = UnityEngine.Application.dataPath;
            Running = true;
            thread = new Thread(() =>
            {
                try { Run(names, dataDir, modelsDir, maxSize); }
                catch (Exception e) { LastReport = "failed: " + e.Message; ZLog.LogWarning("MuninMap: texture extraction failed: " + e); }
                finally { Running = false; try { onDone?.Invoke(); } catch { } }
            }) { IsBackground = true, Name = "MuninMap textures", Priority = ThreadPriority.BelowNormal };
            thread.Start();
            return true;
        }

        private static void Run(HashSet<string> wanted, string dataDir, string modelsDir, int maxSize)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int total = wanted.Count, found = 0;
            var missing = FindTextures(dataDir, wanted, (name, rgba, w, h) =>
            {
                try { WritePng(rgba, w, h, maxSize, Path.Combine(modelsDir, PrefabExporter.TextureFileName(name))); found++; }
                catch (Exception e) { ZLog.LogWarning($"MuninMap: could not write texture {name}: {e.Message}"); }
            }, bundlesFirst: true);
            lock (gaveUp) foreach (var w in wanted) if (missing.Contains(w)) gaveUp.Add(w);
            int unsupported = 0; lock (index) foreach (var m in missing) if (indexBundles.TryGetValue(m, out var at) && !TextureDecoder.Supported(at.Format)) unsupported++;
            LastFound = found;
            LastReport = $"{found} of {total} textures extracted in {sw.Elapsed.TotalSeconds:F0}s" + (unsupported > 0 ? $", {unsupported} in formats not read here (e.g. crunched)" : "") + (missing.Count - unsupported > 0 ? $", {missing.Count - unsupported} not found" : "");
            ZLog.Log("MuninMap: " + LastReport);
        }

        // bundles first, biggest first (the world's assets live there), then the plain .assets files
        internal static IEnumerable<string> AssetFiles(string dataDir)
        {
            var list = new List<string>();
            string bundles = Path.Combine(Path.Combine(Path.Combine(dataDir, "StreamingAssets"), "SoftRef"), "Bundles");
            if (Directory.Exists(bundles)) list.AddRange(Directory.GetFiles(bundles, "*", SearchOption.AllDirectories));
            string aa = Path.Combine(Path.Combine(dataDir, "StreamingAssets"), "aa");
            if (Directory.Exists(aa)) foreach (var f in Directory.GetFiles(aa, "*.bundle", SearchOption.AllDirectories)) list.Add(f);
            list.Sort((a, b) => new FileInfo(b).Length.CompareTo(new FileInfo(a).Length));
            foreach (var f in Directory.GetFiles(dataDir))
            {
                string n = Path.GetFileName(f);
                if (n.EndsWith(".assets", StringComparison.Ordinal) || n == "globalgamemanagers") list.Add(f);
            }
            return list;
        }

        internal static bool IsBundle(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var b = new byte[8]; int n = fs.Read(b, 0, 8);
                    return n == 8 && b[0] == (byte)'U' && b[1] == (byte)'n' && b[2] == (byte)'i' && b[3] == (byte)'t' && b[4] == (byte)'y' && b[5] == (byte)'F' && b[6] == (byte)'S';
                }
            }
            catch { return false; }
        }

        // One texture by name, decoded to RGBA32 (rows bottom-up); null when it isn't anywhere.
        internal static byte[] FindTexture(string dataDir, string name, out int w, out int h)
        {
            byte[] got = null; int gw = 0, gh = 0;
            FindTextures(dataDir, new[] { name }, (n, rgba, tw, th) => { got = rgba; gw = tw; gh = th; });
            w = gw; h = gh;
            return got;
        }

        // Where every texture is (#30). Built in ONE pass over the game's files and the mods' bundles
        // the first time anything is looked up; after that a lookup opens only the file it needs, and a
        // name that is nowhere costs nothing. Before, every lookup that missed read all ~2 GB again,
        // and a modded server misses a lot (its items, pieces and gear are in the mods' files, often in
        // formats not read here), so the scans ran back to back for hours and the memory kept climbing.
        private sealed class At { public Source Src; public string Node; public int Format; }
        private static readonly Dictionary<string, At> index = new Dictionary<string, At>(StringComparer.Ordinal);          // .assets first (UI atlases)
        private static readonly Dictionary<string, At> indexBundles = new Dictionary<string, At>(StringComparer.Ordinal);   // the game's bundles first (world models)
        private static readonly object indexLock = new object();
        private static bool indexed;
        // the names of the game's own meshes (43) and materials (21), so their extractors can skip
        // names that are nowhere in the game's files (a mod's) instead of reading everything for them
        private static readonly HashSet<string> gameMeshes = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> gameMaterials = new HashSet<string>(StringComparer.Ordinal);
        private static bool meshNameMissed, materialNameMissed;   // a name that couldn't be read: don't filter that kind

        // the wanted names the game's files have (by kind: 43 mesh, 21 material); null when the index can't tell
        internal static HashSet<string> InGameFiles(string dataDir, int classId, IEnumerable<string> names)
        {
            try { EnsureIndex(dataDir); } catch { return null; }
            var set = classId == 43 ? gameMeshes : classId == 21 ? gameMaterials : null;
            if (set == null || (classId == 43 ? meshNameMissed : materialNameMissed)) return null;
            var r = new HashSet<string>();
            lock (index) foreach (var n in names) if (set.Contains(n)) r.Add(n);
            return r;
        }

        private static void EnsureIndex(string dataDir)
        {
            lock (indexLock)
            {
                if (indexed) return;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int files = 0, mods = 0;
                foreach (var src in Sources(dataDir, bundlesFirst: false))
                {
                    files++;
                    if (src.Mod) mods++;
                    try
                    {
                        if (src.Bundle)
                        {
                            using (var bundle = new BundleFile(src.Path, src.Offset))
                                foreach (var node in bundle.Nodes)
                                {
                                    if (!Wanted(node)) continue;
                                    byte[] data;
                                    try { data = bundle.ReadNode(node); } catch { continue; }   // (LZMA-packed mod bundles land here)
                                    if (SerializedFile.Looks(data)) Note(data, src, node.Name);
                                }
                        }
                        else Note(File.ReadAllBytes(src.Path), src, null);
                    }
                    catch { }
                }
                indexed = true;
                ZLog.Log($"MuninMap: {index.Count} textures, {gameMeshes.Count} meshes and {gameMaterials.Count} materials indexed in {files} files ({mods} from mods) in {sw.Elapsed.TotalSeconds:F0}s"
                    + (meshNameMissed || materialNameMissed ? " (some names unreadable: those kinds are searched in full)" : ""));
            }
        }

        // a named object's name, the first field, without decoding the rest
        internal static string PeekName(byte[] data, SerializedFile.ObjectInfo o)
        {
            try
            {
                int p = (int)o.Start;
                int len = data[p] | (data[p + 1] << 8) | (data[p + 2] << 16) | (data[p + 3] << 24);
                if (len < 0 || len > 512 || p + 4 + len > data.Length) return null;
                return System.Text.Encoding.UTF8.GetString(data, p + 4, len);
            }
            catch { return null; }
        }

        private static bool GameBundle(Source s) => s.Bundle && s.Offset == 0 && !s.Mod;

        private static bool Wanted(BundleFile.Node node) =>
            !node.Name.EndsWith(".resS", StringComparison.Ordinal) && !node.Name.EndsWith(".resource", StringComparison.Ordinal) && node.Size >= 48 && node.Size <= 512L * 1024 * 1024;

        // the first copy of a name wins (the game's files are listed before the mods'), except that for
        // the models a copy in one of the game's bundles beats one in a plain .assets file
        private static void Note(byte[] data, Source src, string node)
        {
            SerializedFile sf;
            try { sf = new SerializedFile(data); } catch { return; }
            foreach (var o in sf.Objects)
            {
                int cls = sf.ClassOf(o);
                if ((cls == 43 || cls == 21) && !src.Mod)
                {
                    string nm = PeekName(data, o);
                    lock (index)
                    {
                        if (nm != null) (cls == 43 ? gameMeshes : gameMaterials).Add(nm);
                        else if (cls == 43) meshNameMissed = true; else materialNameMissed = true;
                    }
                    continue;
                }
                if (cls != 28) continue;
                SerializedFile.Texture2D tex;
                try { tex = sf.ReadTexture2D(o); } catch { continue; }
                if (tex == null || string.IsNullOrEmpty(tex.Name) || tex.Width <= 0 || tex.Height <= 0) continue;
                var at = new At { Src = src, Node = node, Format = tex.Format };
                lock (index)
                {
                    if (!index.ContainsKey(tex.Name)) index[tex.Name] = at;
                    if (!indexBundles.TryGetValue(tex.Name, out var had) || (GameBundle(at.Src) && !had.Src.Bundle)) indexBundles[tex.Name] = at;
                }
            }
        }

        // Several textures by name: found(name, rgba, w, h) runs once per name decoded. Returns the
        // names that were nowhere or could not be decoded.
        internal static HashSet<string> FindTextures(string dataDir, IEnumerable<string> names, Action<string, byte[], int, int> found, bool bundlesFirst = false)
        {
            EnsureIndex(dataDir);
            var idx = bundlesFirst ? indexBundles : index;
            var left = new HashSet<string>(names);
            // group by file and node, so each is opened once
            var groups = new Dictionary<string, List<string>>();
            var where = new Dictionary<string, At>();
            lock (index)
                foreach (var n in left)
                {
                    if (!idx.TryGetValue(n, out var at) || !TextureDecoder.Supported(at.Format)) continue;
                    string key = at.Src.Path + "|" + at.Src.Offset + "|" + at.Node;
                    if (!groups.TryGetValue(key, out var g)) { groups[key] = g = new List<string>(); where[key] = at; }
                    g.Add(n);
                }
            foreach (var kv in groups)
            {
                var at = where[kv.Key];
                var want = new HashSet<string>(kv.Value);
                try
                {
                    if (at.Src.Bundle)
                    {
                        using (var bundle = new BundleFile(at.Src.Path, at.Src.Offset))
                        {
                            var node = bundle.Find(at.Node);
                            if (node != null) Decode(at.Src.Path, bundle, bundle.ReadNode(node), want, left, found);
                        }
                    }
                    else Decode(at.Src.Path, null, File.ReadAllBytes(at.Src.Path), want, left, found);
                }
                catch { }
            }
            return left;
        }

        private static void Decode(string path, BundleFile bundle, byte[] data, HashSet<string> want, HashSet<string> left, Action<string, byte[], int, int> found)
        {
            SerializedFile sf;
            try { sf = new SerializedFile(data); } catch { return; }
            foreach (var o in sf.Objects)
            {
                if (want.Count == 0) return;
                if (sf.ClassOf(o) != 28) continue;
                SerializedFile.Texture2D tex;
                try { tex = sf.ReadTexture2D(o); } catch { continue; }
                if (tex == null || tex.Name == null || !want.Remove(tex.Name)) continue;
                byte[] rgba = DecodeTexture(path, bundle, tex);
                if (rgba == null) continue;
                left.Remove(tex.Name);
                try { found(tex.Name, rgba, tex.Width, tex.Height); } catch { }
            }
        }

        // ---------------------------------------------------------------- where to look

        internal struct Source { public string Path; public long Offset; public bool Bundle; public bool Mod; }

        // The game's files (AssetFiles), then every asset bundle the installed mods carry: loose UnityFS
        // files anywhere under BepInEx/plugins, and bundles embedded in a mod's DLL (found by their
        // signature, once per DLL version).
        internal static IEnumerable<Source> Sources(string dataDir, bool bundlesFirst)
        {
            var game = AssetFiles(dataDir).ToList();
            if (!bundlesFirst) game.Sort((a, b) => IsBundle(a).CompareTo(IsBundle(b)));   // stable enough: .assets first
            foreach (var f in game) yield return new Source { Path = f, Offset = 0, Bundle = IsBundle(f) };
            foreach (var s in ModBundles()) yield return s;
        }

        private static readonly Dictionary<string, long[]> embedded = new Dictionary<string, long[]>();

        internal static List<Source> ModBundles()
        {
            var list = new List<Source>();
            string plugins;
            try { plugins = BepInEx.Paths.PluginPath; } catch { return list; }
            if (string.IsNullOrEmpty(plugins) || !Directory.Exists(plugins)) return list;
            string[] files;
            try { files = Directory.GetFiles(plugins, "*", SearchOption.AllDirectories); } catch { return list; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string f in files)
            {
                try
                {
                    var info = new FileInfo(f);
                    if (info.Length < 64) continue;
                    if (IsBundle(f)) { list.Add(new Source { Path = f, Offset = 0, Bundle = true, Mod = true }); continue; }
                    if (!f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || info.Length < 4096) continue;
                    string key = f + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
                    long[] offs;
                    lock (embedded) embedded.TryGetValue(key, out offs);
                    if (offs == null) { offs = FindSignatures(f); lock (embedded) embedded[key] = offs; }
                    foreach (long o in offs) list.Add(new Source { Path = f, Offset = o, Bundle = true, Mod = true });
                }
                catch { }
            }
            return list;
        }

        // offsets of "UnityFS\0" in a file, read in 1 MB pieces (a mod's DLL can be 200 MB)
        private static long[] FindSignatures(string file)
        {
            var sig = new byte[] { (byte)'U', (byte)'n', (byte)'i', (byte)'t', (byte)'y', (byte)'F', (byte)'S', 0 };
            var found = new List<long>();
            var buf = new byte[(1 << 20) + sig.Length];
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                long basePos = 0; int keep = 0;
                while (true)
                {
                    int n = fs.Read(buf, keep, buf.Length - keep);
                    if (n <= 0) break;
                    int len = keep + n;
                    for (int i = 0; i + sig.Length <= len; i++)
                    {
                        if (buf[i] != (byte)'U') continue;
                        int k = 1; while (k < sig.Length && buf[i + k] == sig[k]) k++;
                        if (k == sig.Length) found.Add(basePos + i);
                    }
                    keep = Math.Min(sig.Length - 1, len);
                    Buffer.BlockCopy(buf, len - keep, buf, 0, keep);
                    basePos += len - keep;
                }
            }
            return found.ToArray();
        }

        // A texture's pixels as RGBA32, rows bottom-up as Unity keeps them (also used for the game's map
        // icons, World/MapIcons); null when the data can't be found or decoded.
        internal static byte[] DecodeTexture(string path, BundleFile bundle, SerializedFile.Texture2D tex)
        {
            {
                byte[] pixels = tex.ImageData;
                if ((pixels == null || pixels.Length == 0) && !string.IsNullOrEmpty(tex.StreamPath) && tex.StreamSize > 0)
                {
                    string name = tex.StreamPath.Substring(tex.StreamPath.LastIndexOf('/') + 1);
                    try
                    {
                        if (bundle != null) { var rn = bundle.Find(name); if (rn == null) return null; pixels = bundle.ReadRange(rn.Offset + tex.StreamOffset, tex.StreamSize); }
                        else
                        {
                            string ext = Path.Combine(Path.GetDirectoryName(path) ?? "", name);
                            if (!File.Exists(ext)) return null;
                            using (var fs = File.OpenRead(ext)) { fs.Seek(tex.StreamOffset, SeekOrigin.Begin); pixels = new byte[tex.StreamSize]; int got = 0; while (got < tex.StreamSize) { int r = fs.Read(pixels, got, tex.StreamSize - got); if (r <= 0) break; got += r; } }
                        }
                    }
                    catch { return null; }
                }
                if (pixels == null || pixels.Length == 0) return null;
                try { return TextureDecoder.Decode(pixels, tex.Width, tex.Height, tex.Format); } catch { return null; }
            }
        }

        // Unity keeps rows bottom-up; flip, box-filter down to maxSize, drop a pointless alpha channel, write.
        private static void WritePng(byte[] rgba, int w, int h, int maxSize, string file)
        {
            int step = 1; while (w / step > maxSize || h / step > maxSize) step *= 2;
            int ow = Math.Max(1, w / step), oh = Math.Max(1, h / step);
            var outp = new byte[ow * oh * 4];
            bool anyAlpha = false; int n = step * step;
            for (int y = 0; y < oh; y++)
                for (int x = 0; x < ow; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0;
                    for (int yy = 0; yy < step; yy++)
                    {
                        int row = ((y * step + yy) * w + x * step) * 4;
                        for (int xx = 0; xx < step; xx++) { int q = row + xx * 4; r += rgba[q]; g += rgba[q + 1]; b += rgba[q + 2]; a += rgba[q + 3]; }
                    }
                    int o = ((oh - 1 - y) * ow + x) * 4;   // flip
                    outp[o] = (byte)(r / n); outp[o + 1] = (byte)(g / n); outp[o + 2] = (byte)(b / n); outp[o + 3] = (byte)(a / n);
                    if (outp[o + 3] < 250) anyAlpha = true;
                }
            byte[] png;
            if (anyAlpha) png = Util.Png.Encode(outp, ow, oh, Util.Png.Format.RGBA, fast: true);
            else
            {
                var rgb = new byte[ow * oh * 3];
                for (int i = 0, j = 0; i < outp.Length; i += 4, j += 3) { rgb[j] = outp[i]; rgb[j + 1] = outp[i + 1]; rgb[j + 2] = outp[i + 2]; }
                png = Util.Png.Encode(rgb, ow, oh, Util.Png.Format.RGB, fast: true);
            }
            string tmp = file + ".tmp";
            File.WriteAllBytes(tmp, png);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }
    }
}
