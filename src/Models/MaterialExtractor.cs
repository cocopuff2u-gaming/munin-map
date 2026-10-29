using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using MuninMap.Models.Unity;

namespace MuninMap.Models
{
    // A material's saved colours, read from the game's own files. On a dedicated server the game's
    // shaders are stripped, and the engine then answers a material's colours (its glow colour among them)
    // with black: the values are still in the files, so they're read from there, on a background thread,
    // like the textures (TextureExtractor). Kept in the model library (models/materials2.txt: one material
    // a line, "name<TAB>_Prop=r,g,b,a;_Prop2=..."), so each is looked up once. A few numbers are kept too
    // (the cut-out level, a glow's scrolling), as "#_Prop=v,0,0,0" (materials.txt, before 3.2.6, had none).
    internal static class MaterialExtractor
    {
        public static bool Running { get; private set; }
        private static readonly string[] Numbers = { "_Cutoff", "_ScrollSpeed", "_TexScale", "_VertPush", "_PixelSize" };
        private static Thread thread;
        private static readonly object sync = new object();
        private static Dictionary<string, Dictionary<string, Color>> known;   // material name -> its colours
        private static readonly HashSet<string> gaveUp = new HashSet<string>();
        private static string FilePath(string modelsDir) => Path.Combine(modelsDir, "materials2.txt");

        // The saved colour, when this material has been read (false: not read yet, or no such colour)
        public static bool TryColor(string modelsDir, string material, string prop, out Color c, out bool read)
        {
            c = Color.black; read = false;
            lock (sync)
            {
                Load(modelsDir);
                if (!known.TryGetValue(material, out var props)) return false;
                read = true;
                return props.TryGetValue(prop, out c);
            }
        }

        public static bool Known(string modelsDir, string material) { lock (sync) { Load(modelsDir); return known.ContainsKey(material) || gaveUp.Contains(material); } }

        private static void Load(string modelsDir)
        {
            if (known != null) return;
            known = new Dictionary<string, Dictionary<string, Color>>();
            try
            {
                if (!File.Exists(FilePath(modelsDir))) return;
                foreach (var line in File.ReadAllLines(FilePath(modelsDir)))
                {
                    int tab = line.IndexOf('\t'); if (tab <= 0) continue;
                    var props = new Dictionary<string, Color>();
                    foreach (var kv in line.Substring(tab + 1).Split(';'))
                    {
                        int eq = kv.IndexOf('='); if (eq <= 0) continue;
                        var f = kv.Substring(eq + 1).Split(',');
                        if (f.Length != 4) continue;
                        float P(int i) => float.Parse(f[i], CultureInfo.InvariantCulture);
                        props[kv.Substring(0, eq)] = new Color(P(0), P(1), P(2), P(3));
                    }
                    known[line.Substring(0, tab)] = props;
                }
            }
            catch { }
        }

        // Kicks off a run for these material names when nothing is running and there is something new.
        public static bool Start(ICollection<string> wanted, string modelsDir, Action onDone)
        {
            if (Running || wanted.Count == 0) return false;
            var names = new HashSet<string>();
            lock (sync) { Load(modelsDir); foreach (var w in wanted) if (!known.ContainsKey(w) && !gaveUp.Contains(w)) names.Add(w); }
            if (names.Count == 0) return false;
            string dataDir = Application.dataPath;
            Running = true;
            thread = new Thread(() =>
            {
                try { Run(names, dataDir, modelsDir); }
                catch (Exception e) { ZLog.LogWarning("MuninMap: material lookup failed: " + e.Message); }
                finally { Running = false; try { onDone?.Invoke(); } catch { } }
            }) { IsBackground = true, Name = "MuninMap materials", Priority = System.Threading.ThreadPriority.BelowNormal };
            thread.Start();
            return true;
        }

        private static void Run(HashSet<string> wanted, string dataDir, string modelsDir)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int total = wanted.Count;
            var found = new Dictionary<string, Dictionary<string, Color>>();
            foreach (string path in TextureExtractor.AssetFiles(dataDir))
            {
                if (wanted.Count == 0) break;
                try
                {
                    if (TextureExtractor.IsBundle(path))
                        using (var bundle = new BundleFile(path))
                            foreach (var node in bundle.Nodes)
                            {
                                if (wanted.Count == 0) break;
                                if (node.Name.EndsWith(".resS", StringComparison.Ordinal) || node.Name.EndsWith(".resource", StringComparison.Ordinal) || node.Size < 48 || node.Size > 512L * 1024 * 1024) continue;
                                byte[] data;
                                try { data = bundle.ReadNode(node); } catch { continue; }
                                if (SerializedFile.Looks(data)) Scan(data, wanted, found);
                            }
                    else Scan(File.ReadAllBytes(path), wanted, found);
                }
                catch { }
            }
            lock (sync)
            {
                foreach (var kv in found) known[kv.Key] = kv.Value;
                foreach (var w in wanted) gaveUp.Add(w);
                try
                {
                    var sb = new StringBuilder();
                    foreach (var kv in known)
                    {
                        sb.Append(kv.Key).Append('\t');
                        bool first = true;
                        foreach (var p in kv.Value)
                        {
                            if (!first) sb.Append(';'); first = false;
                            sb.Append(p.Key).Append('=').Append(string.Join(",", new[] { p.Value.r, p.Value.g, p.Value.b, p.Value.a }.Select4()));
                        }
                        sb.Append('\n');
                    }
                    File.WriteAllText(FilePath(modelsDir), sb.ToString());
                }
                catch (Exception e) { ZLog.LogWarning("MuninMap: could not save material colours: " + e.Message); }
            }
            ZLog.Log($"MuninMap: {found.Count} of {total} materials read from the game files in {sw.Elapsed.TotalSeconds:F0}s");
        }

        private static string[] Select4(this float[] v)
        {
            var s = new string[v.Length];
            for (int i = 0; i < v.Length; i++) s[i] = v[i].ToString("0.####", CultureInfo.InvariantCulture);
            return s;
        }

        // every Material (class 21) in this file that's wanted: its saved colours
        private static void Scan(byte[] data, HashSet<string> wanted, Dictionary<string, Dictionary<string, Color>> found)
        {
            SerializedFile sf;
            try { sf = new SerializedFile(data); } catch { return; }
            foreach (var o in sf.Objects)
            {
                if (sf.ClassOf(o) != 21) continue;
                Dictionary<string, object> d;
                try { d = sf.ReadObject(o); } catch { continue; }
                if (d == null || !(d.TryGetValue("m_Name", out object nm) && nm is string name) || !wanted.Contains(name)) continue;
                var props = new Dictionary<string, Color>();
                if (d.TryGetValue("m_SavedProperties", out object spo) && spo is Dictionary<string, object> sp && sp.TryGetValue("m_Colors", out object co) && co is List<object> colors)
                    foreach (var e in colors)
                    {
                        if (!(e is Dictionary<string, object> pair)) continue;
                        string key = pair.TryGetValue("first", out object f) ? (f as string ?? ((f as Dictionary<string, object>)?.TryGetValue("name", out object fn) == true ? fn as string : null)) : null;
                        if (key == null || !(pair.TryGetValue("second", out object v) && v is Dictionary<string, object> c)) continue;
                        props[key] = new Color(SerializedFile.AsFloat(c, "r"), SerializedFile.AsFloat(c, "g"), SerializedFile.AsFloat(c, "b"), SerializedFile.AsFloat(c, "a"));
                    }
                if (d.TryGetValue("m_SavedProperties", out object spo2) && spo2 is Dictionary<string, object> sp2 && sp2.TryGetValue("m_Floats", out object fo) && fo is List<object> floats)
                    foreach (var e in floats)
                    {
                        if (!(e is Dictionary<string, object> pair)) continue;
                        string key = pair.TryGetValue("first", out object f) ? (f as string ?? ((f as Dictionary<string, object>)?.TryGetValue("name", out object fn) == true ? fn as string : null)) : null;
                        if (key == null || Array.IndexOf(Numbers, key) < 0 || !pair.TryGetValue("second", out object v)) continue;
                        try { props["#" + key] = new Color(Convert.ToSingle(v, CultureInfo.InvariantCulture), 0f, 0f, 0f); } catch { }
                    }
                found[name] = props;
                wanted.Remove(name);
            }
        }
    }
}
