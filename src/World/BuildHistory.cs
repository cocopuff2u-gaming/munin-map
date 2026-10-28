using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using MuninMap.Util;

namespace MuninMap.World
{
    // Where people have been building: the build pieces per 64 m zone after each world sweep, kept as
    // a picture an hour for the last two days and one each six hours back to eight days, so the map
    // can show what changed over the last day or week. Kept in build-history.txt beside the world's
    // map data ("s <unix>" starts a picture, then "zx zz pieces" lines).
    internal static class BuildHistory
    {
        private sealed class Snap { public long t; public Dictionary<long, int> pieces; }

        private const long Hour = 3600, KeepSeconds = 8 * 24 * Hour;
        private static readonly List<Snap> snaps = new List<Snap>();   // oldest first
        private static Snap current;                                   // the last sweep
        private static readonly object sync = new object();
        private static readonly Dictionary<int, string> cache = new Dictionary<int, string>();
        private static string path;
        private static int saving;

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static void Load(string worldDir)
        {
            path = Path.Combine(worldDir, "build-history.txt");
            lock (sync)
            {
                snaps.Clear(); current = null; cache.Clear();
                try
                {
                    if (!File.Exists(path)) return;
                    Snap s = null;
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var p = line.Split(' ');
                        if (p.Length == 2 && p[0] == "s" && long.TryParse(p[1], out long t)) snaps.Add(s = new Snap { t = t, pieces = new Dictionary<long, int>() });
                        else if (s != null && p.Length == 3 && int.TryParse(p[0], out int zx) && int.TryParse(p[1], out int zz) && int.TryParse(p[2], out int n))
                            s.pieces[((long)zx << 32) ^ (uint)zz] = n;
                    }
                }
                catch (Exception e) { ZLog.LogWarning("MuninMap: build-history.txt not readable: " + e.Message); }
                // until the first sweep: the last picture kept stands for now (at most an hour older than
                // the last sweep before the restart), so the map has changes to show at once
                if (snaps.Count > 0) current = snaps[snaps.Count - 1];
            }
        }

        // Main thread, after a sweep (Hotspots.Finish): zone -> counts, build pieces first
        public static void Record(Dictionary<long, int[]> zones)
        {
            var pieces = new Dictionary<long, int>(zones.Count);
            foreach (var kv in zones) if (kv.Value[0] > 0) pieces[kv.Key] = kv.Value[0];
            long now = Now();
            bool save = false;
            lock (sync)
            {
                current = new Snap { t = now, pieces = pieces };
                cache.Clear();
                if (snaps.Count == 0 || now - snaps[snaps.Count - 1].t >= Hour)
                {
                    snaps.Add(current);
                    // older than two days: one each six hours; older than eight days: gone
                    for (int i = snaps.Count - 2; i >= 1; i--)
                        if (now - snaps[i].t > 48 * Hour && snaps[i + 1].t - snaps[i - 1].t < 6 * Hour) snaps.RemoveAt(i);
                    while (snaps.Count > 0 && now - snaps[0].t > KeepSeconds) snaps.RemoveAt(0);
                    save = true;
                }
            }
            if (save && path != null && Interlocked.Exchange(ref saving, 1) == 0)
                ThreadPool.QueueUserWorkItem(_ => { try { Save(); } finally { saving = 0; } });
        }

        private static void Save()
        {
            var sb = new StringBuilder();
            lock (sync)
                foreach (var s in snaps)
                {
                    sb.Append("s ").Append(s.t).Append('\n');
                    foreach (var kv in s.pieces) sb.Append((int)(kv.Key >> 32)).Append(' ').Append((int)(uint)kv.Key).Append(' ').Append(kv.Value).Append('\n');
                }
            try { File.WriteAllText(path + ".tmp", sb.ToString()); if (File.Exists(path)) File.Delete(path); File.Move(path + ".tmp", path); }
            catch (Exception e) { ZLog.LogWarning("MuninMap: could not write build-history.txt: " + e.Message); }
        }

        // HTTP thread: the zones whose build pieces changed over the last `days` (1 or 7), compared with
        // the picture from then (or the oldest there is: "since" says from when).
        // {"cell":64,"now":unix,"since":unix,"zones":[[zx, zz, pieces now, pieces then]...]}
        public static string Json(int days)
        {
            days = days >= 7 ? 7 : 1;
            lock (sync)
            {
                if (cache.TryGetValue(days, out string c)) return c;
                if (current == null) return "{\"cell\":64,\"zones\":[],\"pending\":true}";
                long target = current.t - days * 24 * Hour;
                Snap then = null;
                foreach (var s in snaps) { if (s.t <= target || then == null) then = s; if (s.t > target) break; }
                var j = new JsonWriter(4096);
                j.BeginObject().Prop("cell", 64).Prop("now", current.t).Prop("since", then.t).Key("zones").BeginArray();
                if (then != current)
                {
                    foreach (var kv in current.pieces)
                    {
                        then.pieces.TryGetValue(kv.Key, out int was);
                        if (Math.Abs(kv.Value - was) >= 3) j.BeginArray().Value((int)(kv.Key >> 32)).Value((int)(uint)kv.Key).Value(kv.Value).Value(was).End();
                    }
                    foreach (var kv in then.pieces)
                        if (!current.pieces.ContainsKey(kv.Key) && kv.Value >= 3) j.BeginArray().Value((int)(kv.Key >> 32)).Value((int)(uint)kv.Key).Value(0).Value(kv.Value).End();
                }
                j.End().End();
                return cache[days] = j.ToString();
            }
        }
    }
}
