using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using MuninMap.Util;

namespace MuninMap.Live
{
    // Where players go: each one's trail over the last hours, and over all time how long players
    // have spent in each 64 m zone and how many died there. Only players who share their position
    // on the map are recorded (the same rule as the live dots). One sample every 10 s: a few numbers
    // per player. The zone totals are kept in activity.txt beside the world's map data.
    internal static class Activity
    {
        private const float Every = 10f;            // seconds between samples
        private const int TrailSeconds = 24 * 3600; // how far back a trail goes (the page asks for the last 3 or 24 hours)
        private const float Moved = 3f;             // metres: a player standing still adds no trail points

        private sealed class Trail { public string name; public readonly List<int> t = new List<int>(); public readonly List<float> x = new List<float>(), z = new List<float>(); }
        private static readonly Dictionary<string, Trail> trails = new Dictionary<string, Trail>();
        private static readonly Dictionary<long, int> heat = new Dictionary<long, int>();     // zone -> seconds spent there
        private static readonly Dictionary<long, int> deaths = new Dictionary<long, int>();   // zone -> deaths
        // zone -> player name -> {seconds there, unix time last there} (counted from 3.2.2 on: the
        // zone totals above go back further)
        private static readonly Dictionary<long, Dictionary<string, int[]>> heatBy = new Dictionary<long, Dictionary<string, int[]>>();
        private static readonly object sync = new object();
        private static DateTime lastSample;
        private static bool dirty;
        private static string path;
        private static readonly Dictionary<int, string> cache = new Dictionary<int, string>();     // hours -> json
        private static readonly Dictionary<int, DateTime> cacheAt = new Dictionary<int, DateTime>();

        private static long Zone(float x, float z) => ((long)Mathf.FloorToInt((x + 32f) / 64f) << 32) ^ (uint)Mathf.FloorToInt((z + 32f) / 64f);
        private static int Now() => (int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;

        // Main thread, with every player snapshot (throttled here)
        public static void Sample(List<Players.Snapshot> players)
        {
            var now = DateTime.UtcNow;
            if ((now - lastSample).TotalSeconds < Every) return;
            float dt = lastSample == default ? Every : Mathf.Min((float)(now - lastSample).TotalSeconds, 30f);
            lastSample = now;
            int t = Now();
            lock (sync)
            {
                foreach (var s in players)
                {
                    if (!s.hasPos || s.dead || MuninConfig.IsHidden(s.name)) continue;
                    long zone = Zone(s.x, s.z);
                    heat.TryGetValue(zone, out int sec);
                    heat[zone] = sec + Mathf.RoundToInt(dt);
                    if (!heatBy.TryGetValue(zone, out var who)) heatBy[zone] = who = new Dictionary<string, int[]>();
                    if (!who.TryGetValue(s.name, out var me)) who[s.name] = me = new int[2];
                    me[0] += Mathf.RoundToInt(dt); me[1] = t;
                    dirty = true;
                    if (!trails.TryGetValue(s.key ?? s.name, out var tr)) trails[s.key ?? s.name] = tr = new Trail();
                    tr.name = s.name;
                    int n = tr.t.Count;
                    if (n > 0 && Mathf.Abs(tr.x[n - 1] - s.x) < Moved && Mathf.Abs(tr.z[n - 1] - s.z) < Moved) continue;
                    tr.t.Add(t); tr.x.Add(s.x); tr.z.Add(s.z);
                }
                // drop what has aged out, and players gone for longer than a trail lasts
                var gone = new List<string>();
                foreach (var kv in trails)
                {
                    var tr = kv.Value;
                    int cut = 0;
                    while (cut < tr.t.Count && t - tr.t[cut] > TrailSeconds) cut++;
                    if (cut > 0) { tr.t.RemoveRange(0, cut); tr.x.RemoveRange(0, cut); tr.z.RemoveRange(0, cut); }
                    if (tr.t.Count == 0) gone.Add(kv.Key);
                }
                foreach (var k in gone) trails.Remove(k);
            }
        }

        // a player died there (DeathWatch: a position only when the game sent one)
        public static void OnDeath(float x, float z)
        {
            lock (sync)
            {
                long zone = Zone(x, z);
                deaths.TryGetValue(zone, out int n);
                deaths[zone] = n + 1;
                dirty = true;
            }
        }

        // HTTP thread: trails over the last `hours` (3 or 24), time spent and deaths per zone. Remade at
        // most every 15 s.
        public static string Json(int hours = 3)
        {
            hours = hours >= 24 ? 24 : 3;
            lock (sync)
            {
                if (cache.TryGetValue(hours, out string c) && (DateTime.UtcNow - cacheAt[hours]).TotalSeconds < 15) return c;
                int from = Now() - hours * 3600;
                var j = new JsonWriter(1024 + heat.Count * 20 + trails.Count * 4096 * hours / 3);
                j.BeginObject().Prop("cell", 64).Prop("now", Now()).Prop("hours", hours);
                j.Key("heat").BeginArray();      // [zx, zz, seconds]
                foreach (var kv in heat) j.BeginArray().Value((int)(kv.Key >> 32)).Value((int)(uint)kv.Key).Value(kv.Value).End();
                j.End();
                j.Key("deaths").BeginArray();    // [zx, zz, deaths]
                foreach (var kv in deaths) j.BeginArray().Value((int)(kv.Key >> 32)).Value((int)(uint)kv.Key).Value(kv.Value).End();
                j.End();
                j.Key("trails").BeginArray();    // {name, pts: [x, z, unix seconds]...}
                foreach (var tr in trails.Values)
                {
                    int i0 = tr.t.BinarySearch(from);
                    if (i0 < 0) i0 = ~i0;
                    if (i0 >= tr.t.Count) continue;
                    j.BeginObject().Prop("name", tr.name).Key("pts").BeginArray();
                    for (int i = i0; i < tr.t.Count; i++) j.BeginArray().Value(tr.x[i], 0).Value(tr.z[i], 0).Value(tr.t[i]).End();
                    j.End().End();
                }
                j.End().End();
                cache[hours] = c = j.ToString();
                cacheAt[hours] = DateTime.UtcNow;
                return c;
            }
        }

        // HTTP thread: one zone's time spent, all told and per player (most first)
        // {"zx","zz","seconds","deaths","players":[[name, seconds, unix last there]...]}
        public static string ZoneJson(int zx, int zz)
        {
            long k = ((long)zx << 32) ^ (uint)zz;
            lock (sync)
            {
                heat.TryGetValue(k, out int sec);
                deaths.TryGetValue(k, out int dead);
                var j = new JsonWriter(256);
                j.BeginObject().Prop("zx", zx).Prop("zz", zz).Prop("seconds", sec).Prop("deaths", dead).Key("players").BeginArray();
                if (heatBy.TryGetValue(k, out var who))
                {
                    var list = new List<KeyValuePair<string, int[]>>(who);
                    list.Sort((a, b) => b.Value[0].CompareTo(a.Value[0]));
                    foreach (var kv in list) j.BeginArray().Value(kv.Key).Value(kv.Value[0]).Value(kv.Value[1]).End();
                }
                return j.End().End().ToString();
            }
        }

        public static void Load(string worldDir)
        {
            path = Path.Combine(worldDir, "activity.txt");
            lock (sync)
            {
                heat.Clear(); deaths.Clear(); heatBy.Clear(); trails.Clear(); cache.Clear();
                try
                {
                    if (!File.Exists(path)) return;
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var p = line.Split(' ');
                        if (p.Length >= 6 && p[0] == "p" && int.TryParse(p[1], out int px) && int.TryParse(p[2], out int pz) && int.TryParse(p[3], out int ps) && int.TryParse(p[4], out int pt))
                        {
                            long pk = ((long)px << 32) ^ (uint)pz;   // "p zx zz seconds last name" (the name may hold spaces)
                            if (!heatBy.TryGetValue(pk, out var who)) heatBy[pk] = who = new Dictionary<string, int[]>();
                            who[string.Join(" ", p, 5, p.Length - 5)] = new[] { ps, pt };
                            continue;
                        }
                        if (p.Length != 4 || !int.TryParse(p[1], out int zx) || !int.TryParse(p[2], out int zz) || !int.TryParse(p[3], out int v)) continue;
                        long k = ((long)zx << 32) ^ (uint)zz;
                        if (p[0] == "h") heat[k] = v; else if (p[0] == "d") deaths[k] = v;
                    }
                }
                catch (Exception e) { ZLog.LogWarning("MuninMap: activity.txt not readable: " + e.Message); }
            }
        }

        public static void Save()
        {
            if (path == null || !dirty) return;
            var sb = new StringBuilder();
            lock (sync)
            {
                foreach (var kv in heat) sb.Append("h ").Append((int)(kv.Key >> 32)).Append(' ').Append((int)(uint)kv.Key).Append(' ').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
                foreach (var kv in heatBy)
                    foreach (var w in kv.Value)
                        sb.Append("p ").Append((int)(kv.Key >> 32)).Append(' ').Append((int)(uint)kv.Key).Append(' ').Append(w.Value[0]).Append(' ').Append(w.Value[1]).Append(' ').Append(w.Key.Replace('\n', ' ')).Append('\n');
                foreach (var kv in deaths) sb.Append("d ").Append((int)(kv.Key >> 32)).Append(' ').Append((int)(uint)kv.Key).Append(' ').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
                dirty = false;
            }
            try { File.WriteAllText(path, sb.ToString()); }
            catch (Exception e) { ZLog.LogWarning("MuninMap: could not write activity.txt: " + e.Message); }
        }
    }
}
