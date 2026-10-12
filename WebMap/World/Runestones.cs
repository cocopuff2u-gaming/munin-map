using System;
using System.Collections.Generic;
using System.Reflection;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace WebMap.World
{
    // Runestones and Vegvisirs, with the text a player reads on each.
    //
    // A runestone isn't a networked object: it's part of a location (Runestone_Meadows,
    // Vegvisit_Eikthyr, ...), which the world saves as a LocationProxy object holding the location's
    // hash, at the location's spot and turn. So the location prefabs that hold a RuneStone are
    // looked at once (where each stone sits in it, and its texts), and each proxy the sweep meets
    // places them. A stone with several texts picks one by its position the way RuneStone does
    // (Random seeded with (int)x * (int)z), so the page shows the same text the game would.
    internal static class Runestones
    {
        internal struct Stone { public float x, y, z; public string name, topic, text; public bool vegvisir; }

        private sealed class Kind
        {
            public string name;            // the game's name token ($piece_vegvisir...)
            public Vector3 local;          // the stone's spot in the location, the location unturned
            public string topic, text;     // the one text, or
            public List<KeyValuePair<string, string>> random;   // several: one picked by position
            public bool vegvisir;
        }

        private static Dictionary<int, List<Kind>> byLocation;   // location hash -> its stones
        private static readonly int proxyHash = "LocationProxy".GetStableHashCode();
        private static readonly Regex tags = new Regex("<[^>]+>", RegexOptions.Compiled);

        public static bool IsProxy(int prefabHash) => prefabHash == proxyHash;

        // Main thread, a location at a time over frames (WebMap start): the location prefabs with a
        // RuneStone in them. Vegvisirs stand inside all kinds of locations, so every one is looked
        // at, each loaded in the background, read and let go. The answer is kept on disk
        // (map_data/runestones.txt), so later starts only read that.
        public static IEnumerator PrepareRoutine(string cacheDir)
        {
            var zs = ZoneSystem.instance;
            for (int i = 0; i < 600 && (zs == null || zs.m_locations == null || zs.m_locations.Count == 0); i++) { yield return null; zs = ZoneSystem.instance; }
            if (zs == null || zs.m_locations == null) yield break;
            var names = new List<string>();
            foreach (var loc in zs.m_locations) if (!string.IsNullOrEmpty(loc.m_prefabName)) names.Add(loc.m_prefabName);
            names.Sort(StringComparer.Ordinal);
            string key = "v2 " + names.Count + " " + string.Join(",", names).GetStableHashCode();
            string path = Path.Combine(cacheDir, "runestones.txt");
            var map = Load(path, key);
            if (map != null) { byLocation = map; ZLog.Log("MuninMap: runestones in " + map.Count + " kinds of location (cached)"); yield break; }

            map = new Dictionary<int, List<Kind>>();
            var found = new List<string>();
            var started = DateTime.UtcNow;
            foreach (var loc in zs.m_locations.ToArray())
            {
                string n = loc.m_prefabName;
                if (string.IsNullOrEmpty(n)) continue;
                object sref = null; Type t = null;
                try
                {
                    sref = typeof(ZoneSystem.ZoneLocation).GetField("m_prefab").GetValue(loc);
                    t = sref.GetType();
                    if (!(bool)t.GetProperty("IsValid").GetValue(sref)) continue;
                    t.GetMethod("HoldReference", Type.EmptyTypes).Invoke(sref, null);
                    t.GetMethod("LoadAsync", Type.EmptyTypes).Invoke(sref, null);
                }
                catch (Exception e) { ZLog.LogWarning("MuninMap: runestones, " + n + ": " + e.Message); continue; }
                var loaded = t.GetProperty("IsLoaded");
                var list = new List<Kind>();
                var spawners = new List<KeyValuePair<Transform, object>>();
                Transform locRoot = null;
                for (int f = 0; f < 600 && !(bool)loaded.GetValue(sref); f++) yield return null;   // loads in the background
                try
                {
                    var go = t.GetProperty("Asset").GetValue(sref) as GameObject;
                    if (go != null)
                    {
                        var root = locRoot = go.transform;
                        Collect(go, (p) => root.InverseTransformPoint(p), list);
                        // Vegvisirs and some stones come in through a spawner (SoftReferencePrefabSpawner):
                        // its prefab is put under the spawner's parent, so a stone at p in the prefab
                        // stands at parent.TransformPoint(p). Only spawners whose asset says so are loaded.
                        foreach (var sp in go.GetComponentsInChildren<SoftReferencePrefabSpawner>(true))
                        {
                            object r = typeof(SoftReferencePrefabSpawner).GetField("m_prefab", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)?.GetValue(sp);
                            string an = r?.GetType().GetProperty("Name")?.GetValue(r) as string ?? "";
                            string al = an.ToLowerInvariant();
                            if (al.Contains("vegvis") || al.Contains("rune")) spawners.Add(new KeyValuePair<Transform, object>(sp.transform.parent, r));
                        }
                    }
                }
                catch (Exception e) { ZLog.LogWarning("MuninMap: runestones, " + n + ": " + e.Message); }
                foreach (var sp in spawners)
                {
                    var st = sp.Value.GetType();
                    try { st.GetMethod("HoldReference", Type.EmptyTypes).Invoke(sp.Value, null); st.GetMethod("LoadAsync", Type.EmptyTypes).Invoke(sp.Value, null); }
                    catch { continue; }
                    var sl = st.GetProperty("IsLoaded");
                    for (int f = 0; f < 600 && !(bool)sl.GetValue(sp.Value); f++) yield return null;
                    try
                    {
                        var sgo = st.GetProperty("Asset").GetValue(sp.Value) as GameObject;
                        var parent = sp.Key;
                        if (sgo != null && parent != null && locRoot != null) Collect(sgo, (p) => locRoot.InverseTransformPoint(parent.TransformPoint(p)), list);
                    }
                    catch (Exception e) { ZLog.LogWarning("MuninMap: runestones, " + n + " spawner: " + e.Message); }
                    finally { try { st.GetMethod("Release", Type.EmptyTypes).Invoke(sp.Value, null); } catch { } }
                }
                try { t.GetMethod("Release", Type.EmptyTypes).Invoke(sref, null); } catch { }
                if (list.Count > 0) { map[n.GetStableHashCode()] = list; found.Add(n + (list.Count > 1 ? " x" + list.Count : "")); }
                yield return null;
            }
            byLocation = map;
            Save(path, key, map);
            ZLog.Log($"MuninMap: runestones in {found.Count} kinds of location ({(DateTime.UtcNow - started).TotalSeconds:0} s): " + string.Join(", ", found));
        }

        // the stones in a prefab (a location, or what a spawner in one brings), placed in location space
        private static void Collect(GameObject go, Func<Vector3, Vector3> place, List<Kind> list)
        {
            foreach (var rs in go.GetComponentsInChildren<RuneStone>(true))
            {
                var k = new Kind
                {
                    name = rs.m_name, local = place(rs.transform.position),
                    topic = rs.m_topic ?? "", text = rs.m_text ?? "", vegvisir = false,
                };
                if (rs.m_randomTexts != null && rs.m_randomTexts.Count > 0)
                {
                    k.random = new List<KeyValuePair<string, string>>();
                    foreach (var r in rs.m_randomTexts) k.random.Add(new KeyValuePair<string, string>(r.m_topic ?? "", r.m_text ?? ""));
                }
                list.Add(k);
            }
            // a Vegvisir has no text: the game's hover shows its name and the name on it (the boss)
            foreach (var vg in go.GetComponentsInChildren<Vegvisir>(true))
                list.Add(new Kind { name = vg.m_name, local = place(vg.transform.position), topic = vg.m_hoverName ?? "", text = "", vegvisir = true });
        }

        // runestones.txt: the key line, then one line a stone, tab separated (tabs and line breaks escaped)
        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "");
        private static string Unesc(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length) { char c = s[++i]; sb.Append(c == 't' ? '\t' : c == 'n' ? '\n' : c); }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }
        private static void Save(string path, string key, Dictionary<int, List<Kind>> map)
        {
            try
            {
                var sb = new StringBuilder(key).Append('\n');
                var ci = CultureInfo.InvariantCulture;
                foreach (var kv in map)
                    foreach (var k in kv.Value)
                    {
                        sb.Append(kv.Key).Append('\t').Append(Esc(k.name)).Append('\t').Append(k.local.x.ToString("R", ci)).Append('\t').Append(k.local.y.ToString("R", ci)).Append('\t').Append(k.local.z.ToString("R", ci))
                          .Append('\t').Append(k.vegvisir ? 1 : 0).Append('\t').Append(Esc(k.topic)).Append('\t').Append(Esc(k.text));
                        if (k.random != null) foreach (var r in k.random) sb.Append('\t').Append(Esc(r.Key)).Append('\t').Append(Esc(r.Value));
                        sb.Append('\n');
                    }
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: runestones cache: " + e.Message); }
        }
        private static Dictionary<int, List<Kind>> Load(string path, string key)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var lines = File.ReadAllLines(path);
                if (lines.Length == 0 || lines[0] != key) return null;
                var ci = CultureInfo.InvariantCulture;
                var map = new Dictionary<int, List<Kind>>();
                for (int i = 1; i < lines.Length; i++)
                {
                    var f = lines[i].Split('\t');
                    if (f.Length < 8) continue;
                    var k = new Kind
                    {
                        name = Unesc(f[1]), local = new Vector3(float.Parse(f[2], ci), float.Parse(f[3], ci), float.Parse(f[4], ci)),
                        vegvisir = f[5] == "1", topic = Unesc(f[6]), text = Unesc(f[7]),
                    };
                    if (f.Length > 9) { k.random = new List<KeyValuePair<string, string>>(); for (int j = 8; j + 1 < f.Length; j += 2) k.random.Add(new KeyValuePair<string, string>(Unesc(f[j]), Unesc(f[j + 1]))); }
                    int h = int.Parse(f[0], ci);
                    if (!map.TryGetValue(h, out var list)) map[h] = list = new List<Kind>();
                    list.Add(k);
                }
                return map;
            }
            catch { return null; }
        }

        // Main thread: a LocationProxy. Adds its location's stones, placed and with their text.
        public static void Observe(ZDO zdo, Vector3 pos, List<Stone> into)
        {
            if (byLocation == null || byLocation.Count == 0) return;
            int hash = zdo.GetInt(ZDOVars.s_location, 0);
            if (hash == 0 || !byLocation.TryGetValue(hash, out var kinds)) return;
            Quaternion rot = zdo.GetRotation();
            foreach (var k in kinds)
            {
                Vector3 w = pos + rot * k.local;
                string topic = k.topic, text = k.text;
                if (k.random != null)
                {
                    // RuneStone.GetRandomText
                    var state = UnityEngine.Random.state;
                    UnityEngine.Random.InitState((int)w.x * (int)w.z);
                    var pick = k.random[UnityEngine.Random.Range(0, k.random.Count)];
                    UnityEngine.Random.state = state;
                    topic = pick.Key; text = pick.Value;
                }
                into.Add(new Stone
                {
                    x = w.x, y = w.y, z = w.z, name = Live.WorldEvents.Localize(k.name, k.vegvisir ? "Vegvisir" : "Runestone"), vegvisir = k.vegvisir,
                    topic = Clean(Live.WorldEvents.Localize(topic, "")),
                    text = Clean(Live.WorldEvents.Localize(text, "")),
                });
            }
        }

        // the game's rich-text tags out; line breaks kept
        private static string Clean(string s) => string.IsNullOrEmpty(s) ? "" : tags.Replace(s, "").Trim();
    }
}
