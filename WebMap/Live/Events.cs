using System;
using System.Collections.Generic;
using System.IO;
using WebMap.Util;

namespace WebMap.Live
{
    // The event feed: joins, leaves, deaths, chat, pings, pins and server
    // notices, in one ordered stream. Kept in memory for the page's feed,
    // pushed live over the websocket, and appended to events.jsonl beside
    // the world data so history survives restarts.
    internal static class Events
    {
        public struct Ev
        {
            public long id; public string ts; public string type; public string name; public string text; public float x, z; public bool hasPos;
        }

        private static readonly List<Ev> recent = new List<Ev>();
        private static readonly object gate = new object();
        private static long nextId = 1;
        private static volatile string recentJson = "[]";
        private static readonly List<Ev> pending = new List<Ev>();

        public static string RecentJson => recentJson;

        public static void Add(string type, string name, string text, float? x = null, float? z = null)
        {
            var ev = new Ev
            {
                ts = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                type = type, name = name ?? "", text = text ?? "",
                x = x ?? 0f, z = z ?? 0f, hasPos = x.HasValue && z.HasValue
            };
            lock (gate)
            {
                ev.id = nextId++;
                recent.Add(ev);
                while (recent.Count > Math.Max(20, WebMapConfig.MAX_MESSAGES)) recent.RemoveAt(0);
                pending.Add(ev);
                recentJson = ToJson(recent);
            }
            if (WebMapConfig.EVENT_LOG) AppendLog(ev);
        }

        // Drained by the websocket broadcaster.
        public static string DrainPendingJson()
        {
            lock (gate)
            {
                if (pending.Count == 0) return null;
                string s = ToJson(pending);
                pending.Clear();
                return s;
            }
        }

        private static string ToJson(List<Ev> list)
        {
            var j = new JsonWriter(list.Count * 96 + 16);
            j.BeginArray();
            foreach (var e in list) Write(j, e);
            j.End();
            return j.ToString();
        }

        private static void Write(JsonWriter j, Ev e)
        {
            j.BeginObject();
            j.Prop("id", e.id).Prop("ts", e.ts).Prop("type", e.type).Prop("name", e.name).Prop("text", e.text);
            if (e.hasPos) j.Prop("x", e.x, 1).Prop("z", e.z, 1);
            j.End();
        }

        private static readonly object logGate = new object();
        private static void AppendLog(Ev e)
        {
            try
            {
                string dir = WebMap.worldDataPath;
                if (string.IsNullOrEmpty(dir)) return;
                var j = new JsonWriter(160);
                Write(j, e);
                lock (logGate) File.AppendAllText(Path.Combine(dir, "events.jsonl"), j.ToString() + "\n");
            }
            catch { }
        }

        // Older history for the page's "Show older": the `limit` events in events.jsonl just before
        // `before` (an ISO time, the oldest the page has), oldest first, as the lines are stored.
        // The log is in time order (a backfill from the server logs goes at its start).
        public static string OlderJson(string before, int limit)
        {
            try
            {
                string path = Path.Combine(WebMap.worldDataPath ?? "", "events.jsonl");
                if (!File.Exists(path)) return "[]";
                string[] lines;
                lock (logGate) lines = File.ReadAllLines(path);
                var keep = new List<string>();
                for (int i = lines.Length - 1; i >= 0 && keep.Count < limit; i--)
                {
                    string l = lines[i];
                    int k = l.IndexOf("\"ts\":\"", StringComparison.Ordinal);
                    if (k < 0) continue;
                    int end = l.IndexOf('"', k + 6);
                    if (end < 0) continue;
                    string ts = l.Substring(k + 6, end - k - 6);   // the whole stamp: a shortened one sorted before itself and came back again
                    if (string.CompareOrdinal(ts, before) < 0) keep.Add(l);
                }
                keep.Reverse();
                return "[" + string.Join(",", keep) + "]";
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: older events: " + e.Message); return "[]"; }
        }

        // Load the tail of the log on startup so the feed is not empty after a restart.
        public static void LoadTail()
        {
            try
            {
                string path = Path.Combine(WebMap.worldDataPath ?? "", "events.jsonl");
                if (!File.Exists(path)) return;
                var lines = File.ReadAllLines(path);
                int from = Math.Max(0, lines.Length - Math.Max(20, WebMapConfig.MAX_MESSAGES));
                lock (gate)
                {
                    for (int i = from; i < lines.Length; i++)
                    {
                        if (string.IsNullOrWhiteSpace(lines[i])) continue;
                        Dictionary<string, object> d = null;
                        try { d = JsonParser.Parse(lines[i]) as Dictionary<string, object>; } catch { }   // one bad line must not drop the whole tail
                        if (d == null) continue;
                        var ev = new Ev
                        {
                            id = nextId++, ts = JsonParser.Str(d, "ts"), type = JsonParser.Str(d, "type"),
                            name = JsonParser.Str(d, "name"), text = JsonParser.Str(d, "text"),
                            hasPos = d.ContainsKey("x"), x = (float)JsonParser.Num(d, "x"), z = (float)JsonParser.Num(d, "z")
                        };
                        recent.Add(ev);
                    }
                    recentJson = ToJson(recent);
                }
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: events.jsonl not readable: " + e.Message); }
        }
    }
}
