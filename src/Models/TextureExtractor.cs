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
            int total = wanted.Count, found = 0, unsupported = 0, files = 0;
            ZLog.Log($"MuninMap: extracting {total} textures from the game files in {dataDir}");
            foreach (string path in AssetFiles(dataDir))
            {
                if (wanted.Count == 0) break;
                files++;
                try
                {
                    if (IsBundle(path)) found += FromBundle(path, wanted, modelsDir, maxSize, ref unsupported);
                    else found += FromSerialized(path, null, File.ReadAllBytes(path), wanted, modelsDir, maxSize, ref unsupported);
                }
                catch (Exception e) { if (MuninConfig.DEBUG) ZLog.LogWarning($"MuninMap: {Path.GetFileName(path)} skipped: {e.Message}"); }
            }
            lock (gaveUp) foreach (var w in wanted) gaveUp.Add(w);
            LastFound = found;
            LastReport = $"{found} of {total} textures extracted from {files} files in {sw.Elapsed.TotalSeconds:F0}s" + (unsupported > 0 ? $", {unsupported} in unsupported formats" : "") + (wanted.Count > 0 ? $", {wanted.Count} not found" : "");
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

        private static int FromBundle(string path, HashSet<string> wanted, string modelsDir, int maxSize, ref int unsupported)
        {
            int found = 0;
            using (var bundle = new BundleFile(path))
            {
                foreach (var node in bundle.Nodes)
                {
                    if (wanted.Count == 0) break;
                    if (node.Name.EndsWith(".resS", StringComparison.Ordinal) || node.Name.EndsWith(".resource", StringComparison.Ordinal) || node.Size < 48 || node.Size > 512L * 1024 * 1024) continue;
                    byte[] data;
                    try { data = bundle.ReadNode(node); } catch { continue; }
                    if (!SerializedFile.Looks(data)) continue;
                    found += FromSerialized(path, bundle, data, wanted, modelsDir, maxSize, ref unsupported);
                }
            }
            return found;
        }

        private static int FromSerialized(string path, BundleFile bundle, byte[] data, HashSet<string> wanted, string modelsDir, int maxSize, ref int unsupported)
        {
            SerializedFile sf;
            try { sf = new SerializedFile(data); } catch { return 0; }
            int found = 0;
            foreach (var o in sf.Objects)
            {
                if (sf.ClassOf(o) != 28) continue;
                SerializedFile.Texture2D tex;
                try { tex = sf.ReadTexture2D(o); } catch { continue; }
                if (tex == null || tex.Name == null || !wanted.Contains(tex.Name)) continue;
                if (tex.Width <= 0 || tex.Height <= 0) continue;
                if (!TextureDecoder.Supported(tex.Format)) { unsupported++; wanted.Remove(tex.Name); lock (gaveUp) gaveUp.Add(tex.Name); ZLog.LogWarning($"MuninMap: texture {tex.Name} is in format {tex.Format}, not supported"); continue; }
                byte[] rgba = DecodeTexture(path, bundle, tex);
                if (rgba == null) continue;
                try
                {
                    WritePng(rgba, tex.Width, tex.Height, maxSize, Path.Combine(modelsDir, PrefabExporter.TextureFileName(tex.Name)));
                    wanted.Remove(tex.Name); found++;
                }
                catch (Exception e) { ZLog.LogWarning($"MuninMap: could not write texture {tex.Name}: {e.Message}"); }
            }
            return found;
        }

        // One texture by name, decoded to RGBA32 (rows bottom-up), from wherever it is in the game's
        // files: the plain .assets files first (the UI atlases live there), then the bundles.
        internal static byte[] FindTexture(string dataDir, string name, out int w, out int h)
        {
            w = h = 0;
            var files = AssetFiles(dataDir).ToList();
            files.Sort((a, b) => IsBundle(a).CompareTo(IsBundle(b)));
            foreach (string path in files)
            {
                try
                {
                    if (IsBundle(path))
                    {
                        using (var bundle = new BundleFile(path))
                            foreach (var node in bundle.Nodes)
                            {
                                if (node.Name.EndsWith(".resS", StringComparison.Ordinal) || node.Name.EndsWith(".resource", StringComparison.Ordinal) || node.Size < 48 || node.Size > 512L * 1024 * 1024) continue;
                                byte[] data;
                                try { data = bundle.ReadNode(node); } catch { continue; }
                                if (!SerializedFile.Looks(data)) continue;
                                var r = FindIn(path, bundle, data, name, out w, out h);
                                if (r != null) return r;
                            }
                    }
                    else
                    {
                        var r = FindIn(path, null, File.ReadAllBytes(path), name, out w, out h);
                        if (r != null) return r;
                    }
                }
                catch { }
            }
            return null;
        }

        private static byte[] FindIn(string path, BundleFile bundle, byte[] data, string name, out int w, out int h)
        {
            w = h = 0;
            SerializedFile sf;
            try { sf = new SerializedFile(data); } catch { return null; }
            foreach (var o in sf.Objects)
            {
                if (sf.ClassOf(o) != 28) continue;
                SerializedFile.Texture2D tex;
                try { tex = sf.ReadTexture2D(o); } catch { continue; }
                if (tex == null || tex.Name != name || !TextureDecoder.Supported(tex.Format)) continue;
                w = tex.Width; h = tex.Height;
                return DecodeTexture(path, bundle, tex);
            }
            return null;
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
