using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using WebMap.Util;

namespace WebMap.Live
{
    // The game's own console lines (joins, saves, raids, sleeping...), in the order Valheim wrote
    // them, each with the time it was written. The server's console output only goes to whatever
    // runs it (AMP's log has no time on the game's lines), so the plugin listens to Unity's log and
    // keeps its own copy: the last few thousand lines in memory for the page's admin "Server log"
    // view, and server-log.txt beside the world's map data so they survive a restart.
    internal static class ServerLog
    {
        private struct Line { public long n; public string ts, level, text; }

        private const int KEEP = 4000;
        private const long MAX_FILE = 8L * 1024 * 1024;   // then the file moves to server-log.1.txt
        private static readonly Line[] ring = new Line[KEEP];
        private static long count;                          // lines seen; line n sits at ring[n % KEEP]
        private static readonly object gate = new object();
        private static string path;
        private static StreamWriter file;

        // From the plugin's start: the game's startup lines are kept too, in memory until the
        // world (and so where the file goes) is known.
        private static bool listening;
        public static void Listen()
        {
            if (listening) return;
            listening = true;
            Application.logMessageReceivedThreaded += OnLog;
        }

        // the world is loaded: earlier runs' lines from the file go first, then this run's so far,
        // and from now on every line is also written to the file
        public static void Open(string worldDataPath)
        {
            lock (gate)
            {
                if (path != null) return;
                path = Path.Combine(worldDataPath, "server-log.txt");
                var now = new List<Line>();
                for (long n = Math.Max(0, count - KEEP); n < count; n++) now.Add(ring[n % KEEP]);
                count = 0;
                LoadTail();
                foreach (var l in now) Add(l.ts, l.level, l.text);
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > MAX_FILE)
                    {
                        string old = Path.Combine(worldDataPath, "server-log.1.txt");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    file = new StreamWriter(path, true) { AutoFlush = true };
                    foreach (var l in now) Write(l.ts, l.level, l.text);
                }
                catch (Exception e) { ZLog.LogWarning("MuninMap: server-log.txt: " + e.Message); }
            }
        }

        private static void Write(string ts, string level, string text)
        {
            try { file?.WriteLine(ts + "\t" + level + "\t" + text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "")); } catch { }
        }

        public static void Stop()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            lock (gate) { try { file?.Dispose(); } catch { } file = null; }
        }

        private static void OnLog(string message, string stack, LogType type)
        {
            if (string.IsNullOrEmpty(message)) return;
            string level = type == LogType.Error || type == LogType.Exception || type == LogType.Assert ? "error" : type == LogType.Warning ? "warn" : "info";
            string ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            string text = message.TrimEnd();
            lock (gate)
            {
                Add(ts, level, text);
                Write(ts, level, text);
            }
        }

        private static void Add(string ts, string level, string text)
        {
            ring[count % KEEP] = new Line { n = count, ts = ts, level = level, text = text };
            count++;
        }

        // the end of the file from earlier runs, so the view isn't empty after a restart
        private static void LoadTail()
        {
            try
            {
                if (!File.Exists(path)) return;
                var lines = File.ReadAllLines(path);
                for (int i = Math.Max(0, lines.Length - KEEP); i < lines.Length; i++)
                {
                    var t = lines[i].Split(new[] { '\t' }, 3);
                    if (t.Length == 3) Add(t[0], t[1], t[2].Replace("\\n", "\n").Replace("\\\\", "\\"));
                }
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: server-log.txt not readable: " + e.Message); }
        }

        // lines after number `after` (-1: the latest `limit`), oldest first
        public static string Json(long after, int limit)
        {
            var j = new JsonWriter(limit * 96 + 32);
            lock (gate)
            {
                long first = Math.Max(Math.Max(0, count - KEEP), after + 1);
                if (after < 0) first = Math.Max(0, count - limit);
                long last = Math.Min(count, first + limit);
                j.BeginObject().Prop("next", last - 1).Key("lines").BeginArray();
                for (long n = first; n < last; n++)
                {
                    var l = ring[n % KEEP];
                    j.BeginObject().Prop("n", l.n).Prop("ts", l.ts).Prop("level", l.level).Prop("text", l.text).End();
                }
                j.End().End();
            }
            return j.ToString();
        }
    }
}
