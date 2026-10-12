using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace MuninMap.World
{
    // What the sweep published, kept on disk so a restarted server serves it at once instead of
    // after its first sweep: one line per chunk, "key rev count<TAB>json". Written on a background
    // thread when something changed; the next sweep replaces it all with fresh data (same content,
    // same revs, so the browsers' cached copies stay valid).
    internal static class ChunkCache
    {
        public struct Line { public int key, rev, count; public string json; }

        public static void Save(string path, List<Line> lines)
        {
            if (path == null) return;
            new Thread(() =>
            {
                try
                {
                    var sb = new StringBuilder(lines.Count * 256);
                    sb.Append("grid ").Append(Tiles.TileMath.WORLD_SIZE).Append('\n');   // chunk keys only mean something on this grid
                    foreach (var l in lines) sb.Append(l.key).Append(' ').Append(l.rev).Append(' ').Append(l.count).Append('\t').Append(l.json).Append('\n');
                    File.WriteAllText(path + ".tmp", sb.ToString());
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(path + ".tmp", path);
                }
                catch (Exception e) { ZLog.LogWarning("MuninMap: cache " + Path.GetFileName(path) + ": " + e.Message); }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
        }

        public static List<Line> Load(string path)
        {
            var list = new List<Line>();
            try
            {
                if (path == null || !File.Exists(path)) return list;
                var all = File.ReadAllLines(path);
                if (all.Length == 0 || all[0] != "grid " + Tiles.TileMath.WORLD_SIZE) return list;   // another grid (or older): rebuilt by the first sweep
                foreach (string s in all)
                {
                    int tab = s.IndexOf('\t');
                    if (tab < 0) continue;
                    var h = s.Substring(0, tab).Split(' ');
                    if (h.Length != 3 || !int.TryParse(h[0], out int key) || !int.TryParse(h[1], out int rev) || !int.TryParse(h[2], out int count)) continue;
                    list.Add(new Line { key = key, rev = rev, count = count, json = s.Substring(tab + 1) });
                }
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: cache " + Path.GetFileName(path) + ": " + e.Message); list.Clear(); }
            return list;
        }
    }
}
