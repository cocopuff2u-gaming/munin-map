using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using MuninMap.Tiles;
using MuninMap.Util;
using MuninMap.World;

namespace MuninMap.Live
{
    // Player and server statistics, persisted to stats.json beside the world's
    // map data.
    //
    // Per player (keyed by their platform id, with the name as a fallback):
    // first and last seen, time played, sessions, deaths, distance walked,
    // portal trips, biomes visited, cells of fog they personally lifted. Per
    // server: online history for the last day, the in-game day, explored
    // percentage, and whatever the world sweep counted.
    //
    // Fed once a second from the player snapshot on the main thread; all the
    // JSON is built there too and only read elsewhere.
    internal static class Stats
    {
        public sealed class PlayerStat
        {
            public string key, name;
            public string firstSeen, lastSeen;
            public double playtime;          // seconds
            public int sessions, deaths, portalTrips, revealed;
            public int bossKills, raids, finds;  // credited to whoever was there (Live/WorldEvents)
            public long playerId;                // the game's id for the player: building pieces carry it
            public int built;                    // building pieces standing in the world, from the last sweep
            public double distance;          // metres
            public HashSet<string> biomes = new HashSet<string>();
            public float lastX, lastZ; public bool hasLast;
            public bool online;
            public string lastBiome = "";
            // when they played: the last sessions (unix start, end), the one going on, and time per day (UTC)
            public List<long[]> recent = new List<long[]>();
            public long sessionStart;
            public Dictionary<string, double> days = new Dictionary<string, double>();
            public Dictionary<string, string> gear = new Dictionary<string, string>();   // what they wore when last seen (slot -> prefab)
            public List<string> bossesSeen = new List<string>();   // bosses they were there for, first time each
            public List<string> found = new List<string>();        // places they were first to find (the last 30)
            public long deathAt; public float deathX, deathZ; public string deathBiome = "";   // their last death (unix, where)
            public double deathPlay = -1;                                                   // their playtime then (-1: not known)
            public Dictionary<string, int> levels = new Dictionary<string, int>();   // the level of what they wore, where the game says (slot -> level)
            public Dictionary<string, int> variants = new Dictionary<string, int>(); // the style of what they wore, where it has one (slot -> variant)
            public bool hasLook; public Players.Look look;                          // how they look, last seen
            // more to their story (counted from when this was added; position-based ones only while the map
            // can follow them)
            public Dictionary<string, double> biomeTime = new Dictionary<string, double>();   // seconds in each biome
            public Dictionary<string, int> biomeDeaths = new Dictionary<string, int>();
            public double afk, atBase, sailing, sailed, dungeon;   // seconds (sailed: metres)
            public int closeCalls; public float lowHealth = -1f;    // dips under 10% health and lived; the least health they've had
            public float highest = float.NaN, farthest;             // height above the sea; metres from the middle of the world
            public Dictionary<string, int> builtDays = new Dictionary<string, int>();   // pieces standing at the end of each day (UTC), the last 8
            public double ping = -1;                               // their usual ping (ms, slowly averaged)
            public int nights, chats;                              // nights slept through, chat messages sent
            public double longest;                                 // their longest session (seconds)
            public double[] week = new double[168];                // seconds played in each hour of the week (UTC, Monday 00:00 first)
            public double counted;                                 // the play the stats above (from 3.2.2) cover, seconds
            public bool body;                                      // (not saved) their character is in the world
            // (not saved) the AFK clock and the close-call latch
            public double idleFor; public float idleX, idleZ, idleYaw; public bool closeArmed = true;
        }

        // EnvMan.RescaleDayFraction
        internal static double DayFraction(double f) =>
            f >= 0.15 && f <= 0.85 ? 0.25 + (f - 0.15) / 0.7 * 0.5 : f < 0.5 ? f / 0.15 * 0.25 : 0.75 + (f - 0.85) / 0.15 * 0.25;

        private static long Unix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private static void SessionStarts(PlayerStat s) { s.sessionStart = Unix(); }
        private static void SessionEnds(PlayerStat s)
        {
            if (s.sessionStart == 0) return;
            long end = Unix();
            if (end - s.sessionStart >= 30) { s.recent.Add(new[] { s.sessionStart, end }); if (s.recent.Count > 20) s.recent.RemoveAt(0); }
            if (end - s.sessionStart > s.longest) s.longest = end - s.sessionStart;
            s.sessionStart = 0;
        }

        private static readonly Dictionary<string, PlayerStat> players = new Dictionary<string, PlayerStat>();
        private static readonly List<KeyValuePair<long, int>> onlineHistory = new List<KeyValuePair<long, int>>();   // unix ts, count
        private static long lastHistoryBucket;
        private static bool dirty;
        private static double lastTick;
        private static volatile string json = "{}";
        private static string serverStartedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        // world totals, counted from when this was added (kept in stats.json)
        private static int raidsTotal, nightsSlept, bossKillsTotal, peakOnline;
        private static string peakOnlineUtc = "";

        public static string Json => json;

        // The stats text is made only when someone wants it: it was remade from scratch every player
        // tick (every half second, world keys, raids, forecast, every player ever seen), page open or
        // not. Now a change only marks it stale; a web thread that wants it asks, and the game's thread
        // remakes it on its next tick (Serve) while the web thread waits, at most 1.5 s.
        private static volatile bool stale = true, asked;
        private static readonly object gate = new object();

        // any thread: the stats, remade first when they changed
        public static string FreshJson()
        {
            if (!stale) return json;
            // made in the last few seconds: that, at once, and made again on the next tick for the next ask
            // (waiting for the game's tick made opening the Stats or Players tab take up to a second)
            if (built.IsRunning && built.Elapsed.TotalSeconds < 5) { asked = true; return json; }
            lock (gate)
            {
                if (!stale) return json;
                asked = true;
                System.Threading.Monitor.Wait(gate, 1500);
            }
            return json;
        }

        // the game's thread, every player tick: remake the stats if someone is waiting for them
        public static void Serve()
        {
            if (!asked) return;
            Rebuild(-1);
            lock (gate) { asked = false; System.Threading.Monitor.PulseAll(gate); }
        }

        // old key -> new key for entries moved over on load (Live/WorldEvents keeps the same keys)
        private static readonly Dictionary<string, string> rekeyed = new Dictionary<string, string>();
        public static string Rekey(string key) => key != null && rekeyed.TryGetValue(key, out string k) ? k : key;

        private static string StatsPath => Path.Combine(MuninMap.worldDataPath ?? "", "stats.json");

        public static void Load()
        {
            players.Clear();
            onlineHistory.Clear();
            rekeyed.Clear();
            try
            {
                if (!File.Exists(StatsPath)) return;
                var doc = JsonParser.ParseObject(File.ReadAllText(StatsPath));
                var ps = JsonParser.Arr(doc, "players");
                if (ps != null)
                    foreach (var po in ps)
                    {
                        var d = po as Dictionary<string, object>;
                        if (d == null) continue;
                        var s = new PlayerStat
                        {
                            key = JsonParser.Str(d, "key"), name = JsonParser.Str(d, "name"),
                            firstSeen = JsonParser.Str(d, "firstSeen"), lastSeen = JsonParser.Str(d, "lastSeen"),
                            playtime = JsonParser.Num(d, "playtime"), sessions = (int)JsonParser.Num(d, "sessions"),
                            deaths = (int)JsonParser.Num(d, "deaths"), distance = JsonParser.Num(d, "distance"),
                            portalTrips = (int)JsonParser.Num(d, "portalTrips"), revealed = (int)JsonParser.Num(d, "revealed"),
                            lastX = (float)JsonParser.Num(d, "lastX"), lastZ = (float)JsonParser.Num(d, "lastZ"),
                            hasLast = d.ContainsKey("lastX"),
                            bossKills = (int)JsonParser.Num(d, "bossKills"), raids = (int)JsonParser.Num(d, "raids"), finds = (int)JsonParser.Num(d, "finds"),
                            playerId = (long)JsonParser.Num(d, "playerId")
                        };
                        var bs = JsonParser.Arr(d, "biomes");
                        if (bs != null) foreach (var b in bs) if (b is string bn) s.biomes.Add(bn);
                        var rs = JsonParser.Arr(d, "recent");
                        if (rs != null) foreach (var ro in rs) if (ro is List<object> pr && pr.Count == 2 && pr[0] is double a0 && pr[1] is double a1) s.recent.Add(new[] { (long)a0, (long)a1 });
                        var ds = JsonParser.Obj(d, "days");
                        if (ds != null) foreach (var kv in ds) if (kv.Value is double sec) s.days[kv.Key] = sec;
                        var bsn = JsonParser.Arr(d, "bossesSeen");
                        if (bsn != null) foreach (var b in bsn) if (b is string bn2) s.bossesSeen.Add(bn2);
                        var fnd = JsonParser.Arr(d, "found");
                        if (fnd != null) foreach (var f in fnd) if (f is string fn) s.found.Add(fn);
                        s.deathPlay = d.ContainsKey("deathPlay") ? JsonParser.Num(d, "deathPlay") : -1;
                        s.deathAt = (long)JsonParser.Num(d, "deathAt"); s.deathBiome = JsonParser.Str(d, "deathBiome") ?? "";
                        s.deathX = (float)JsonParser.Num(d, "deathX"); s.deathZ = (float)JsonParser.Num(d, "deathZ");
                        var lv = JsonParser.Obj(d, "levels");
                        if (lv != null) foreach (var kv in lv) if (kv.Value is double q) s.levels[kv.Key] = (int)q;
                        var vr = JsonParser.Obj(d, "variants");
                        if (vr != null) foreach (var kv in vr) if (kv.Value is double v) s.variants[kv.Key] = (int)v;
                        var lk = JsonParser.Obj(d, "look");
                        if (lk != null)
                        {
                            s.hasLook = true;
                            s.look.model = (int)JsonParser.Num(lk, "model");
                            s.look.skin = FromHex(JsonParser.Str(lk, "skin")); s.look.hair = FromHex(JsonParser.Str(lk, "hairColor"));
                            s.look.hairItem = JsonParser.Str(lk, "hair") ?? ""; s.look.beardItem = JsonParser.Str(lk, "beard") ?? "";
                        }
                        var bt = JsonParser.Obj(d, "biomeTime");
                        if (bt != null) foreach (var kv in bt) if (kv.Value is double sec2) s.biomeTime[kv.Key] = sec2;
                        var bd = JsonParser.Obj(d, "biomeDeaths");
                        if (bd != null) foreach (var kv in bd) if (kv.Value is double n2) s.biomeDeaths[kv.Key] = (int)n2;
                        var bdy = JsonParser.Obj(d, "builtDays");
                        if (bdy != null) foreach (var kv in bdy) if (kv.Value is double n3) s.builtDays[kv.Key] = (int)n3;
                        s.afk = JsonParser.Num(d, "afk"); s.atBase = JsonParser.Num(d, "atBase"); s.sailing = JsonParser.Num(d, "sailing");
                        s.sailed = JsonParser.Num(d, "sailed"); s.dungeon = JsonParser.Num(d, "dungeon");
                        s.closeCalls = (int)JsonParser.Num(d, "closeCalls");
                        s.lowHealth = d.ContainsKey("lowHealth") ? (float)JsonParser.Num(d, "lowHealth") : -1f;
                        s.highest = d.ContainsKey("highest") ? (float)JsonParser.Num(d, "highest") : float.NaN;
                        s.farthest = (float)JsonParser.Num(d, "farthest");
                        s.ping = d.ContainsKey("ping") ? JsonParser.Num(d, "ping") : -1;
                        s.nights = (int)JsonParser.Num(d, "nights"); s.chats = (int)JsonParser.Num(d, "chats"); s.longest = JsonParser.Num(d, "longest");
                        var wk = JsonParser.Arr(d, "week");
                        if (wk != null && wk.Count == 168) for (int h = 0; h < 168; h++) s.week[h] = wk[h] is double wv ? wv : 0;
                        if (d.ContainsKey("counted")) s.counted = JsonParser.Num(d, "counted"); else foreach (var w in s.week) s.counted += w;   // (until 3.2.5 the hours of the week were that record)
                        var gs = JsonParser.Obj(d, "gear");
                        if (gs != null) foreach (var kv in gs) if (kv.Value is string item && item.Length > 0) s.gear[kv.Key] = item;
                        if (string.IsNullOrEmpty(s.key)) continue;
                        // up to 3.0.0 the key was the platform id alone, so every character on an
                        // account shared one entry: it goes to the character last seen with it
                        if (!s.key.StartsWith("name:") && s.key.IndexOf('/') < 0 && !string.IsNullOrEmpty(s.name))
                        {
                            string nk = s.key + "/" + s.name;
                            rekeyed[s.key] = nk;
                            s.key = nk;
                            dirty = true;
                        }
                        players[s.key] = s;
                    }
                raidsTotal = (int)JsonParser.Num(doc, "raids"); nightsSlept = (int)JsonParser.Num(doc, "nightsSlept");
                bossKillsTotal = (int)JsonParser.Num(doc, "bossKills"); peakOnline = (int)JsonParser.Num(doc, "peakOnline");
                peakOnlineUtc = JsonParser.Str(doc, "peakOnlineUtc") ?? "";
                var hist = JsonParser.Arr(doc, "onlineHistory");
                if (hist != null)
                    foreach (var ho in hist)
                        if (ho is List<object> pair && pair.Count == 2 && pair[0] is double ts && pair[1] is double n)
                            onlineHistory.Add(new KeyValuePair<long, int>((long)ts, (int)n));
                ZLog.Log($"MuninMap: stats loaded for {players.Count} players");
                int repaired = (int)JsonParser.Num(doc, "repaired");
                if (repaired < Repaired) RepairFromEvents(repaired);
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: stats.json not readable: " + e.Message); }
        }

        // The record from the events log, which has every join, leave, chat, night slept, raid, boss and
        // find since the map was installed, set right once for each fix (the "repaired" mark in stats.json):
        // 1 (3.2.4): a visit was counted twice (at the join, and again when their character appeared) and
        //   once more for each death, which also cut it in two; chats were counted only from 3.2.2.
        // 2 (3.2.5): a raid the world was saved with was counted again after each restart, and the Writhan
        //   and the Eyeless One as boss kills; the latest visits were still cut at deaths; the days, the
        //   hours of the week, nights slept, bosses seen and places found were only kept from 3.2.0-3.2.2;
        //   and a "player left" notice ended everyone's visit, not just theirs.
        // Players are set from it only when their name is theirs alone and the log saw them from their
        // first visit.
        private const int Repaired = 2;
        private static void RepairFromEvents(int done)
        {
            dirty = true;
            try
            {
                string path = Path.Combine(MuninMap.worldDataPath ?? "", "events.jsonl");
                if (!File.Exists(path)) return;
                var joins = new Dictionary<string, int>(); var chats = new Dictionary<string, int>();
                var visits = new Dictionary<string, List<long[]>>(); var onSince = new Dictionary<string, long>();
                var sleeps = new List<long>();
                var bosses = new Dictionary<string, List<string>>(); var found = new Dictionary<string, List<string>>();
                var notBoss = new Dictionary<string, int>(); var raidAgain = new Dictionary<string, int>();
                int notBossAll = 0, raidAgainAll = 0;
                string raidOn = null;   // the raid going on (begun, not ended)
                string first = null;
                void Add(Dictionary<string, int> m, string k) { m.TryGetValue(k, out int n); m[k] = n + 1; }
                foreach (var line in File.ReadAllLines(path))
                {
                    Dictionary<string, object> d = null;
                    try { d = JsonParser.Parse(line) as Dictionary<string, object>; } catch { }
                    if (d == null) continue;
                    string type = JsonParser.Str(d, "type"), name = JsonParser.Str(d, "name") ?? "", ts = JsonParser.Str(d, "ts"), text = JsonParser.Str(d, "text") ?? "";
                    if (first == null) first = ts;
                    if (!DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime dt)) continue;
                    long t = new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeSeconds();
                    void Ended(string who)
                    {
                        if (!onSince.TryGetValue(who, out long from)) return;
                        if (!visits.TryGetValue(who, out var l)) visits[who] = l = new List<long[]>();
                        l.Add(new[] { from, t });
                        onSince.Remove(who);
                    }
                    if (type == "join") { Add(joins, name); onSince[name] = t; }   // (joined again with no leave between: from the later one, the gap unknown)
                    else if (type == "leave") Ended(name);
                    else if (type == "server")
                    {
                        if (text == "online") foreach (var who in new List<string>(onSince.Keys)) Ended(who);   // (a restart: everyone was off)
                        else if (text.StartsWith("player _", StringComparison.Ordinal) && text.EndsWith("_ left", StringComparison.Ordinal)) Ended(text.Substring(8, text.Length - 14));
                    }
                    else if (type == "chat" || type == "shout" || type == "whisper")
                    {
                        if (type == "shout" && (text == "I have arrived!" || text == "$text_player_arrived")) continue;
                        Add(chats, name);
                    }
                    else if (type == "sleep") sleeps.Add(t);
                    else if (type == "boss")
                    {
                        var by = NamesAfter(text, "by ");
                        bool not = WorldEvents.NotBosses.Contains(name.ToLowerInvariant());
                        if (not) notBossAll++;
                        foreach (var who in by)
                        {
                            if (not) { Add(notBoss, who); continue; }
                            if (!bosses.TryGetValue(who, out var l)) bosses[who] = l = new List<string>();
                            if (!l.Contains(name)) l.Add(name);
                        }
                    }
                    else if (type == "found")
                        foreach (var who in NamesAfter(text, "found by "))
                        {
                            if (!found.TryGetValue(who, out var l)) found[who] = l = new List<string>();
                            l.Add(name);
                        }
                    else if (type == "raid")
                    {
                        int near = text.IndexOf(" (near ", StringComparison.Ordinal);
                        string what = near >= 0 ? text.Substring(0, near) : text;
                        if (!d.ContainsKey("x")) raidOn = null;   // (an end: only a beginning has a place)
                        else if (raidOn == what)   // the same raid again, after a restart
                        {
                            raidAgainAll++;
                            if (near >= 0) foreach (var who in NamesAfter(text.Substring(near), " (near ")) Add(raidAgain, who);
                        }
                        else raidOn = what;
                    }
                }
                if (first == null) return;
                if (done < 2)
                {
                    raidsTotal = Math.Max(0, raidsTotal - raidAgainAll);
                    bossKillsTotal = Math.Max(0, bossKillsTotal - notBossAll);
                }
                var names = new Dictionary<string, int>();
                foreach (var s in players.Values) Add(names, s.name ?? "");
                int fixedN = 0;
                foreach (var s in players.Values)
                {
                    if (string.IsNullOrEmpty(s.name) || names[s.name] > 1) continue;
                    if (done < 2)
                    {
                        if (raidAgain.TryGetValue(s.name, out int ra)) s.raids = Math.Max(0, s.raids - ra);
                        if (notBoss.TryGetValue(s.name, out int nb)) s.bossKills = Math.Max(0, s.bossKills - nb);
                        s.bossesSeen.RemoveAll(b => WorldEvents.NotBosses.Contains(b.ToLowerInvariant()));
                    }
                    // and the rest only for players the log saw from their first visit
                    if (string.CompareOrdinal(first, s.firstSeen ?? "") > 0) continue;
                    if (joins.TryGetValue(s.name, out int j) && j > 0) s.sessions = j;
                    if (chats.TryGetValue(s.name, out int c) && c > s.chats) s.chats = c;
                    if (visits.TryGetValue(s.name, out var vs) && vs.Count > 0)
                    {
                        // the latest visits, the longest, each day (the ones the map didn't keep) and the hours of the week
                        s.recent.Clear();
                        foreach (var v in vs) if (v[1] - v[0] >= 30) s.recent.Add(v);
                        if (s.recent.Count > 20) s.recent.RemoveRange(0, s.recent.Count - 20);
                        var days = new Dictionary<string, double>(); var week = new double[168];
                        int nights = 0;
                        foreach (var v in vs)
                        {
                            if (v[1] - v[0] > s.longest) s.longest = v[1] - v[0];
                            for (long a = v[0]; a < v[1];)
                            {
                                long hourEnd = (a / 3600 + 1) * 3600, b = Math.Min(v[1], hourEnd);
                                string day = DateTimeOffset.FromUnixTimeSeconds(a).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                                days.TryGetValue(day, out double sec); days[day] = sec + (b - a);
                                week[(int)((a / 3600 + 72) % 168)] += b - a;
                                a = b;
                            }
                            foreach (long n in sleeps) if (n >= v[0] && n <= v[1]) nights++;
                        }
                        foreach (var kv in days) if (!s.days.ContainsKey(kv.Key)) s.days[kv.Key] = kv.Value;
                        while (s.days.Count > 31) s.days.Remove(s.days.Keys.Min());
                        s.week = week;
                        if (nights > s.nights) s.nights = nights;
                    }
                    if (bosses.TryGetValue(s.name, out var bl))
                    {
                        foreach (var b in s.bossesSeen) if (!bl.Contains(b)) bl.Add(b);
                        s.bossesSeen = bl;
                    }
                    if (found.TryGetValue(s.name, out var fl))
                    {
                        foreach (var f in s.found) if (!fl.Contains(f)) fl.Add(f);
                        if (fl.Count > 30) fl.RemoveRange(0, fl.Count - 30);
                        s.found = fl;
                        if (fl.Count > s.finds) s.finds = fl.Count;
                    }
                    fixedN++;
                }
                ZLog.Log($"MuninMap: stats counted again from the events log for {fixedN} players ({raidAgainAll} raids counted twice, {notBossAll} kills that weren't bosses)");
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: stats repair: " + e.Message); }
        }

        // the names in "... by A, B and C" (after the marker given)
        private static List<string> NamesAfter(string text, string marker)
        {
            var list = new List<string>();
            int at = text.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return list;
            string s = text.Substring(at + marker.Length).TrimEnd(')');
            int and = s.LastIndexOf(" and ", StringComparison.Ordinal);
            if (and >= 0) { list.AddRange(s.Substring(0, and).Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)); list.Add(s.Substring(and + 5)); }
            else if (s.Length > 0) list.Add(s);
            return list;
        }

        // the server stopping: sessions going on end now (or they'd be lost from the recent list)
        public static void EndSessions() { foreach (var s in players.Values) if (s.online) SessionEnds(s); dirty = true; }

        public static void Save(bool force = false)
        {
            if (!dirty && !force) return;
            try
            {
                var j = new JsonWriter(4096);
                j.BeginObject();
                j.Prop("savedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Prop("repaired", Repaired);
                j.Prop("raids", raidsTotal).Prop("nightsSlept", nightsSlept).Prop("bossKills", bossKillsTotal).Prop("peakOnline", peakOnline).Prop("peakOnlineUtc", peakOnlineUtc);
                j.Key("players").BeginArray();
                foreach (var s in players.Values) WritePlayer(j, s, full: true);
                j.End();
                j.Key("onlineHistory").BeginArray();
                foreach (var kv in onlineHistory) j.BeginArray().Value(kv.Key).Value(kv.Value).End();
                j.End();
                j.End();
                string tmp = StatsPath + ".tmp";
                File.WriteAllText(tmp, j.ToString());
                if (File.Exists(StatsPath)) File.Delete(StatsPath);
                File.Move(tmp, StatsPath);
                dirty = false;
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: could not save stats.json: " + e.Message); }
        }

        private static void WritePlayer(JsonWriter j, PlayerStat s, bool full)
        {
            j.BeginObject();
            j.Prop("key", s.key).Prop("name", s.name).Prop("firstSeen", s.firstSeen).Prop("lastSeen", s.lastSeen);
            j.Prop("playtime", Math.Round(s.playtime), 0).Prop("sessions", s.sessions).Prop("deaths", s.deaths);
            j.Prop("distance", Math.Round(s.distance), 0).Prop("portalTrips", s.portalTrips).Prop("revealed", s.revealed);
            j.Prop("online", s.online);
            j.Prop("bossKills", s.bossKills).Prop("raids", s.raids).Prop("finds", s.finds).Prop("built", s.built);
            if (full) j.Prop("playerId", s.playerId);
            if (s.hasLast && (full || MuninConfig.ALWAYS_VISIBLE || MuninConfig.SHOW_LAST_SEEN_POSITION)) j.Prop("lastX", s.lastX, 1).Prop("lastZ", s.lastZ, 1);
            j.Prop("lastBiome", s.lastBiome);
            j.Key("biomes").BeginArray();
            foreach (var b in s.biomes) j.Value(b);
            j.End();
            j.Key("recent").BeginArray();
            foreach (var r in s.recent) j.BeginArray().Value(r[0]).Value(r[1]).End();
            j.End();
            if (s.online && s.sessionStart > 0) j.Prop("sessionStart", s.sessionStart);
            j.Key("days").BeginObject();
            foreach (var kv in s.days) j.Prop(kv.Key, Math.Round(kv.Value), 0);
            j.End();
            j.Key("gear").BeginObject();
            foreach (var kv in s.gear) j.Prop(kv.Key, kv.Value);
            j.End();
            j.Key("levels").BeginObject();
            foreach (var kv in s.levels) j.Prop(kv.Key, kv.Value);
            j.End();
            j.Key("variants").BeginObject();
            foreach (var kv in s.variants) j.Prop(kv.Key, kv.Value);
            j.End();
            if (s.hasLook)
            {
                Players.WriteLook(j, s.look);
                var gear = s.gear; var variants = s.variants;
                j.Prop("portrait", Models.Portraits.Id(s.look, slot => gear.TryGetValue(slot, out string it) ? it : "", slot => variants.TryGetValue(slot, out int v) ? v : 0));
            }
            j.Key("biomeTime").BeginObject(); foreach (var kv in s.biomeTime) j.Prop(kv.Key, Math.Round(kv.Value), 0); j.End();
            j.Key("biomeDeaths").BeginObject(); foreach (var kv in s.biomeDeaths) j.Prop(kv.Key, kv.Value); j.End();
            j.Key("builtDays").BeginObject(); foreach (var kv in s.builtDays) j.Prop(kv.Key, kv.Value); j.End();
            j.Prop("afk", Math.Round(s.afk), 0).Prop("atBase", Math.Round(s.atBase), 0).Prop("sailing", Math.Round(s.sailing), 0).Prop("sailed", Math.Round(s.sailed), 0).Prop("dungeon", Math.Round(s.dungeon), 0);
            j.Prop("closeCalls", s.closeCalls);
            if (s.lowHealth >= 0f) j.Prop("lowHealth", s.lowHealth, 0);
            if (!float.IsNaN(s.highest)) j.Prop("highest", s.highest, 0);
            j.Prop("farthest", s.farthest, 0);
            if (s.ping >= 0) j.Prop("ping", s.ping, 0);
            j.Prop("nights", s.nights).Prop("chats", s.chats).Prop("longest", Math.Max(s.longest, s.online && s.sessionStart > 0 ? Unix() - s.sessionStart : 0), 0);
            j.Prop("counted", Math.Round(s.counted), 0);   // (always: without it, the hours of the week are taken for it)
            bool anyWeek = false; foreach (var w in s.week) if (w > 0) { anyWeek = true; break; }
            if (anyWeek) { j.Key("week").BeginArray(); foreach (var w in s.week) j.Value(Math.Round(w), 0); j.End(); }
            j.Key("bossesSeen").BeginArray(); foreach (var b in s.bossesSeen) j.Value(b); j.End();
            j.Key("found").BeginArray(); foreach (var f in s.found) j.Value(f); j.End();
            if (s.deathAt > 0)
            {
                j.Prop("deathAt", s.deathAt).Prop("deathBiome", s.deathBiome);
                if (s.deathPlay >= 0) j.Prop("deathPlay", Math.Round(s.deathPlay), 0);
                if (s.deathX != 0f || s.deathZ != 0f) j.Prop("deathX", s.deathX, 1).Prop("deathZ", s.deathZ, 1);
            }
            if (s.playerId != 0L && Structures.Stations.TryGetValue(s.playerId, out var stations) && stations.Count > 0)
            {
                j.Key("stations").BeginArray(); foreach (var n in stations) j.Value(n); j.End();
            }
            j.End();
        }

        private static bool Same(Players.Look a, Players.Look b) => a.model == b.model && a.skin == b.skin && a.hair == b.hair && a.hairItem == b.hairItem && a.beardItem == b.beardItem;
        private static Vector3 FromHex(string h)
        {
            if (h == null || h.Length != 7) return Vector3.one;
            int v = int.Parse(h.Substring(1), NumberStyles.HexNumber);
            return new Vector3(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
        }

        // every player the map has ever seen (by key)
        public static List<string> Keys() => new List<string>(players.Keys);

        public static PlayerStat Get(string key, string name)
        {
            if (string.IsNullOrEmpty(key)) key = "name:" + name;
            if (!players.TryGetValue(key, out var s))
            {
                s = new PlayerStat { key = key, name = name, firstSeen = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
                players[key] = s;
                dirty = true;
            }
            if (!string.IsNullOrEmpty(name)) s.name = name;
            return s;
        }

        public static void OnJoin(string key, string name)
        {
            var s = Get(key, name);
            if (!s.online) { s.sessions++; SessionStarts(s); }   // (once a visit: the tick may have seen them first)
            s.online = true;
            s.lastSeen = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            dirty = true;
        }

        public static void OnLeave(string key, string name)
        {
            var s = Get(key, name);
            if (s.online) SessionEnds(s);
            s.online = false;
            s.lastSeen = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            dirty = true;
        }

        // the player whose game id this is (a building piece's creator), or null
        public static string NameOfPlayerId(long id)
        {
            if (id == 0L) return null;
            foreach (var s in players.Values) if (s.playerId == id && !MuninConfig.IsHidden(s.name)) return s.name;
            return null;
        }

        // a death: counted for the character of that name (the one online, if two share it), with
        // when and where (the place only when the game sent one)
        public static void OnDeath(string name, float? x = null, float? z = null)
        {
            PlayerStat who = null;
            foreach (var s in players.Values) if (s.name == name && s.online) { who = s; break; }
            if (who == null) foreach (var s in players.Values) if (s.name == name) { who = s; break; }
            if (who == null) return;
            who.deaths++;
            who.deathAt = Unix();
            who.deathPlay = who.playtime;
            who.hasLast = false;   // (where they come back isn't a portal trip)
            who.deathX = who.deathZ = 0f; who.deathBiome = "";
            if (x.HasValue && z.HasValue)
            {
                who.deathX = x.Value; who.deathZ = z.Value;
                try { who.deathBiome = Tiles.Palette.BiomeName((int)WorldGenerator.instance.GetBiome(x.Value, z.Value)); } catch { }
                if (who.deathBiome.Length > 0) { who.biomeDeaths.TryGetValue(who.deathBiome, out int n); who.biomeDeaths[who.deathBiome] = n + 1; }
            }
            dirty = true;
        }

        // Main thread, once per snapshot. `visible` players report a position.
        public static void OnTick(List<Players.Snapshot> online, HashSet<string> connected)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            double dt = lastTick > 0 ? Math.Min(now - lastTick, 10.0) : 0.0;
            lastTick = now;

            var seen = new HashSet<string>();
            foreach (var p in online)
            {
                var s = Get(p.key, p.name);
                seen.Add(s.key);
                if (p.playerId != 0L && s.playerId != p.playerId) { s.playerId = p.playerId; dirty = true; }
                if (!s.online) { s.online = true; s.sessions++; SessionStarts(s); }
                if (s.sessionStart == 0) SessionStarts(s);   // (online when the server started)
                s.playtime += dt;
                if (dt > 0)
                {
                    string day = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    s.days.TryGetValue(day, out double sec);
                    s.days[day] = sec + dt;
                    if (s.days.Count > 31) { string oldest = s.days.Keys.Min(); s.days.Remove(oldest); }
                }
                s.lastSeen = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                // what they're wearing, kept for when they're offline
                if (p.gear != null)
                    for (int g = 0; g < p.gear.Length && g < Players.GearSlots.Length; g++)
                    {
                        string slot = Players.GearSlots[g], item = p.gear[g] ?? "";
                        s.gear.TryGetValue(slot, out string had);
                        if ((had ?? "") == item) continue;
                        if (item.Length == 0) s.gear.Remove(slot); else s.gear[slot] = item;
                        dirty = true;
                    }
                if (p.quality != null)
                    for (int g = 0; g < p.quality.Length && g < Players.GearSlots.Length; g++)
                    {
                        string slot = Players.GearSlots[g];
                        int q = string.IsNullOrEmpty(p.gear?[g]) ? 0 : p.quality[g];
                        s.levels.TryGetValue(slot, out int had);
                        if (had == q) continue;
                        if (q == 0) s.levels.Remove(slot); else s.levels[slot] = q;
                        dirty = true;
                    }
                if (p.variant != null)
                    for (int g = 0; g < p.variant.Length && g < Players.GearSlots.Length; g++)
                    {
                        string slot = Players.GearSlots[g];
                        int v = string.IsNullOrEmpty(p.gear?[g]) ? 0 : p.variant[g];
                        s.variants.TryGetValue(slot, out int had);
                        if (had == v) continue;
                        if (v == 0) s.variants.Remove(slot); else s.variants[slot] = v;
                        dirty = true;
                    }
                if (!s.hasLook || !Same(s.look, p.look)) { s.look = p.look; s.hasLook = true; dirty = true; }
                if (p.hasNet) s.ping = s.ping < 0 ? p.ping : s.ping + (p.ping - s.ping) * Math.Min(1.0, dt / 600.0);   // (a ten-minute average)
                // a close call: under 10% and lived (counted again only once back over 30%); the least health had
                if (!p.dead && p.maxHealth > 0f && p.health > 0f)
                {
                    if (s.lowHealth < 0f || p.health < s.lowHealth) { s.lowHealth = p.health; dirty = true; }
                    float f = p.health / p.maxHealth;
                    if (f < 0.1f && s.closeArmed) { s.closeCalls++; s.closeArmed = false; dirty = true; }
                    else if (f > 0.3f) s.closeArmed = true;
                }
                else if (p.dead && !s.closeArmed && s.closeCalls > 0) { s.closeCalls--; s.closeArmed = true; }   // (that one didn't end well: it was a death)
                if (dt > 0 && p.baseValue >= 3) s.atBase += dt;
                if (dt > 0) { s.week[(int)((Unix() / 3600 + 72) % 168)] += dt; s.counted += dt; }   // (1 Jan 1970 was a Thursday)
                s.body = true;
                bool sailingStep = false;   // (this step was on a boat)
                if (p.tracked && dt > 0)
                {
                    bool inDungeon = p.y > 1000f;   // dungeons and caves are built far above the world
                    if (inDungeon) s.dungeon += dt;
                    else
                    {
                        if (!string.IsNullOrEmpty(p.biome)) { s.biomeTime.TryGetValue(p.biome, out double bt); s.biomeTime[p.biome] = bt + dt; }
                        float h = p.y - 30f;   // (the sea is at 30)
                        if (float.IsNaN(s.highest) || h > s.highest) s.highest = h;
                        float far = Mathf.Sqrt(p.x * p.x + p.z * p.z);
                        if (far > s.farthest) s.farthest = far;
                    }
                    // AFK: not moved or turned for five minutes (counted from the start of it)
                    float moved = Mathf.Sqrt((p.x - s.idleX) * (p.x - s.idleX) + (p.z - s.idleZ) * (p.z - s.idleZ));
                    if (moved < 0.5f && Mathf.Abs(Mathf.DeltaAngle(p.yaw, s.idleYaw)) < 2f && !p.inBed)
                    {
                        s.idleFor += dt;
                        if (s.idleFor >= 300) s.afk += s.idleFor >= 300 + dt ? dt : s.idleFor;
                    }
                    else { s.idleFor = 0; s.idleX = p.x; s.idleZ = p.z; s.idleYaw = p.yaw; }
                    // sailing: standing over deep water, out of it (swimming sits lower), and moving
                    if (!inDungeon && p.y > 29.6f && s.hasLast)
                    {
                        float ground = 30f;
                        try { ground = WorldGenerator.instance.GetHeight(p.x, p.z); } catch { }
                        double step = Math.Sqrt((p.x - s.lastX) * (p.x - s.lastX) + (p.z - s.lastZ) * (p.z - s.lastZ));
                        if (ground < 27f && step / dt > 1.0 && step / dt < 60.0) { s.sailing += dt; s.sailed += step; sailingStep = true; }
                    }
                }
                if (p.tracked)
                {
                    if (s.hasLast)
                    {
                        double d = Math.Sqrt((p.x - s.lastX) * (p.x - s.lastX) + (p.z - s.lastZ) * (p.z - s.lastZ));
                        if (dt > 0 && d / dt > 60.0 && d > 150.0) s.portalTrips++;   // faster than any boat: a portal
                        else if (!sailingStep) s.distance += d;   // (walked: not what the boat carried them)
                    }
                    s.lastX = p.x; s.lastZ = p.z; s.hasLast = true;
                    if (!string.IsNullOrEmpty(p.biome)) { s.lastBiome = p.biome; if (s.biomes.Add(p.biome)) dirty = true; }
                }
            }
            // still connected but with no character this moment (dead, waiting to come back): still on,
            // their time still counts, and where they come back isn't a trip
            if (connected != null)
                foreach (var key in connected)
                    if (!seen.Contains(key) && players.TryGetValue(key, out var s) && s.online)
                    {
                        seen.Add(key);
                        s.playtime += dt;
                        if (dt > 0)
                        {
                            string day = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                            s.days.TryGetValue(day, out double sec);
                            s.days[day] = sec + dt;
                            s.week[(int)((Unix() / 3600 + 72) % 168)] += dt;
                            s.counted += dt;
                        }
                        s.hasLast = false;
                        s.body = false;
                    }
            foreach (var s in players.Values) if (s.online && !seen.Contains(s.key)) { SessionEnds(s); s.online = false; }

            // online history, 5-minute buckets
            long bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300 * 300;
            if (bucket != lastHistoryBucket)
            {
                lastHistoryBucket = bucket;
                onlineHistory.Add(new KeyValuePair<long, int>(bucket, online.Count));
                while (onlineHistory.Count > 288 * 7) onlineHistory.RemoveAt(0);   // a week
                dirty = true;
            }
            else if (onlineHistory.Count > 0 && online.Count > onlineHistory[onlineHistory.Count - 1].Value)
            {
                onlineHistory[onlineHistory.Count - 1] = new KeyValuePair<long, int>(bucket, online.Count);
            }
            if (online.Count > peakOnline) { peakOnline = online.Count; peakOnlineUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); dirty = true; }
            if (dt > 0) dirty = true;
            lastOnlineCount = online.Count;
            stale = true;
        }

        public static void OnRevealed(string key, string name, int cells)
        {
            if (cells <= 0) return;
            var s = Get(key, name);
            s.revealed += cells;
            dirty = true;
        }

        public static void OnSweep()
        {
            var b = Structures.Builders;
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var s in players.Values)
            {
                int n = s.playerId != 0L && b.TryGetValue(s.playerId, out int c) ? c : 0;
                if (n != s.built) { s.built = n; dirty = true; }
                // what stands at the end of each day, for "built this week"
                if (s.playerId != 0L && (!s.builtDays.TryGetValue(today, out int was) || was != n))
                {
                    s.builtDays[today] = n; dirty = true;
                    while (s.builtDays.Count > 8) s.builtDays.Remove(s.builtDays.Keys.Min());
                }
            }
            stale = true;
        }

        // world events (Live/WorldEvents), each credited to the players who were there
        public static void OnRaid(List<ZNetPeer> there) { raidsTotal++; foreach (var p in there) Get(Players.KeyOf(p), p.m_playerName).raids++; dirty = true; stale = true; }
        public static void OnBossKill(List<ZNetPeer> there, string boss = null)
        {
            bossKillsTotal++;
            foreach (var p in there)
            {
                var s = Get(Players.KeyOf(p), p.m_playerName);
                s.bossKills++;
                if (!string.IsNullOrEmpty(boss) && !s.bossesSeen.Contains(boss)) s.bossesSeen.Add(boss);
            }
            dirty = true; stale = true;
        }
        public static void OnFound(List<ZNetPeer> there, string what = null)
        {
            foreach (var p in there)
            {
                var s = Get(Players.KeyOf(p), p.m_playerName);
                s.finds++;
                if (!string.IsNullOrEmpty(what)) { s.found.Add(what); if (s.found.Count > 30) s.found.RemoveAt(0); }
            }
            dirty = true; stale = true;
        }
        // the night was slept through: counted for everyone in the world, as the game skips only when every
        // character is in bed (Game.EverybodyIsTryingToSleep)
        public static void OnNightSlept()
        {
            nightsSlept++;
            foreach (var s in players.Values) if (s.online && s.body) s.nights++;
            dirty = true; stale = true;
        }

        // a chat message (said, shouted or whispered), counted for whoever online has that name
        public static void OnChat(string name)
        {
            foreach (var s in players.Values) if (s.online && s.name == name) { s.chats++; dirty = true; stale = true; return; }
        }

        // the bosses in the order the game has them, and whether the world has beaten each
        // (the game's own "defeated_" keys), with the discoveries Live/WorldEvents keeps
        private static readonly string[][] BossOrder =
        {
            new[] { "defeated_eikthyr", "Eikthyr" }, new[] { "defeated_gdking", "The Elder" }, new[] { "defeated_bonemass", "Bonemass" },
            new[] { "defeated_dragon", "Moder" }, new[] { "defeated_goblinking", "Yagluth" }, new[] { "defeated_queen", "The Queen" },
            new[] { "defeated_fader", "Fader" },
            new[] { "defeated_frozenking_p3", "Kall Fimbulbringer" },   // the Deep North boss: the key his last form sets
        };
        private static void World(JsonWriter j)
        {
            var keys = new HashSet<string>();
            var all = new List<string>();
            try { foreach (var k in ZoneSystem.instance.GetGlobalKeys()) { keys.Add(k.ToLowerInvariant().Split(' ')[0]); all.Add(k); } } catch { }
            // every world key as the game has it (progress like "KilledTroll", modifiers like
            // "deathpenalty veryeasy"): the page names the ones it knows
            all.Sort(StringComparer.OrdinalIgnoreCase);
            j.Key("globalKeys").BeginArray();
            foreach (var k in all) j.Value(k);
            j.End();
            j.Key("bosses").BeginArray();
            foreach (var b in BossOrder) j.BeginObject().Prop("name", b[1]).Prop("defeated", keys.Contains(b[0])).End();
            j.End();
            Raids(j, keys);
            SpawnerBosses.Write(j);
            j.PropRaw("discoveries", WorldEvents.DiscoveriesJson());
            int deaths = 0; foreach (var s in players.Values) deaths += s.deaths;
            j.Key("totals").BeginObject().Prop("raids", raidsTotal).Prop("nightsSlept", nightsSlept).Prop("bossKills", bossKillsTotal)
             .Prop("deaths", deaths).Prop("players", players.Count).Prop("peakOnline", peakOnline).Prop("peakOnlineUtc", peakOnlineUtc).End();
        }

        // The raids this world can have, by the game's own rules (RandEventSystem.HaveGlobalKeys):
        // the world keys each needs and the ones that end it (the Eikthyr army stops once the Elder is
        // down). With the "playerevents" key on, a raid with per-player conditions follows each
        // player's own progress (what they know, their own keys), which only their game knows.
        private static void Raids(JsonWriter j, HashSet<string> keys)
        {
            j.Prop("playerEvents", keys.Contains("playerevents"));
            j.Key("raids").BeginArray();
            try
            {
                var rs = RandEventSystem.instance;
                if (rs != null)
                    foreach (var ev in rs.m_events)
                    {
                        if (ev == null || !ev.m_enabled || ev.m_devDisabled || !ev.m_random) continue;
                        bool needs = ev.m_requiredGlobalKeys.TrueForAll(k => keys.Contains((k ?? "").ToLowerInvariant()));
                        bool over = ev.m_notRequiredGlobalKeys.Exists(k => keys.Contains((k ?? "").ToLowerInvariant()));
                        bool perPlayer = ev.m_altRequiredKnownItems.Count > 0 || ev.m_altRequiredNotKnownItems.Count > 0 || ev.m_altNotRequiredPlayerKeys.Count > 0
                            || ev.m_altRequiredPlayerKeysAny.Count > 0 || ev.m_altRequiredPlayerKeysAll.Count > 0;
                        j.BeginObject().Prop("id", ev.m_name).Prop("text", WorldEvents.Localize(ev.m_startMessage, ""))
                         .Prop("state", over ? "over" : needs ? "now" : "notyet").Prop("perPlayer", perPlayer).Prop("nearBase", ev.m_nearBaseOnly);
                        j.Key("needs").BeginArray(); foreach (var k in ev.m_requiredGlobalKeys) j.Value((k ?? "").ToLowerInvariant()); j.End();
                        j.Key("endsWith").BeginArray(); foreach (var k in ev.m_notRequiredGlobalKeys) j.Value((k ?? "").ToLowerInvariant()); j.End();
                        // the per-player conditions, for the page to word: items a player must know
                        j.Key("knows").BeginArray();
                        foreach (var it in ev.m_altRequiredKnownItems) if (it != null) j.Value(WorldEvents.Localize(it.m_itemData.m_shared.m_name, it.name));
                        j.End();
                        j.End();
                    }
            }
            catch (Exception e) { if (MuninConfig.DEBUG) ZLog.LogWarning("MuninMap: raids: " + e.Message); }
            j.End();
        }

        private static int lastOnlineCount;
        private static readonly System.Diagnostics.Stopwatch built = new System.Diagnostics.Stopwatch();
        private static void Rebuild(int onlineCount)
        {
            if (onlineCount >= 0) lastOnlineCount = onlineCount;
            stale = false;
            built.Restart();
            try
            {
                var j = new JsonWriter(4096);
                j.BeginObject();
                j.Key("server").BeginObject();
                j.Prop("startedUtc", serverStartedUtc);
                j.Prop("latestVersion", global::MuninMap.UpdateCheck.Latest).Prop("latestUrl", global::MuninMap.UpdateCheck.LatestUrl);   // (the release on GitHub, "" until known)
                try { j.Prop("gameVersion", global::Version.CurrentVersion.ToString()); } catch { }
                j.Prop("nowUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                j.Prop("online", lastOnlineCount);
                WorldTime(j);
                j.Prop("exploredPercent", Fog.ExploredPercent(), 2);
                j.Prop("exploredCells", Fog.ExploredCells);
                j.Prop("structures", Structures.Total);
                j.Prop("trees", Vegetation.LastTrees);
                j.Prop("rocks", Vegetation.LastRocks);
                j.Prop("terraformedZones", TerrainPatches.Count);
                j.Prop("objects", WorldSweep.LastScanned);
                j.Prop("sweeps", WorldSweep.Sweeps);
                j.Prop("lastSweepUtc", WorldSweep.LastSweepUtc == default ? "" : WorldSweep.LastSweepUtc.ToString("o", CultureInfo.InvariantCulture));
                j.Prop("lastSweepSeconds", WorldSweep.LastSweepSeconds, 1);
                j.PropRaw("tiles", TileStore.StatusJson());
                j.End();
                World(j);
                j.PropRaw("forecast", Weather.ForecastJson());
                j.Key("onlineHistory").BeginArray();
                int from = Math.Max(0, onlineHistory.Count - 288);
                for (int i = from; i < onlineHistory.Count; i++) j.BeginArray().Value(onlineHistory[i].Key).Value(onlineHistory[i].Value).End();
                j.End();
                // when the server is busy: the players online on average in each hour of the week (UTC,
                // Monday 00:00 first; -1: the server wasn't up then), from the week of 5-minute buckets
                var sum = new double[168]; var cnt = new int[168];
                foreach (var kv in onlineHistory) { int h = (int)((kv.Key / 3600 + 72) % 168); sum[h] += kv.Value; cnt[h]++; }   // (1 Jan 1970 was a Thursday)
                j.Key("busyHours").BeginArray();
                for (int h = 0; h < 168; h++) j.Value(cnt[h] > 0 ? sum[h] / cnt[h] : -1, 2);
                j.End();
                j.Prop("busySince", onlineHistory.Count > 0 ? onlineHistory[0].Key : 0L);
                j.Key("players").BeginArray();
                var list = new List<PlayerStat>(players.Values);
                list.Sort((a, b) => b.playtime.CompareTo(a.playtime));
                foreach (var s in list) if (!MuninConfig.IsHidden(s.name)) WritePlayer(j, s, full: false);
                j.End();
                j.End();
                json = j.ToString();
            }
            catch (Exception e)
            {
                if (MuninConfig.DEBUG) ZLog.LogWarning("MuninMap: stats rebuild failed: " + e.Message);
            }
        }

        private static void WorldTime(JsonWriter j)
        {
            try
            {
                double t = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0;
                float dayLen = 1800f;
                try { if (EnvMan.instance != null) dayLen = EnvMan.instance.m_dayLengthSec; } catch { }
                int day = (int)(t / dayLen);
                // the time of day as the game has it: the clock's fraction stretched so the day (0.15 to 0.85 of
                // the clock) runs from 0.25 to 0.75 (EnvMan.RescaleDayFraction), night below and above that
                // (EnvMan.CalculateNight). dayLength and worldTime let the page keep the clock going between updates.
                double frac = DayFraction((t % dayLen) / dayLen);
                j.Prop("worldTime", t, 0).Prop("dayLength", dayLen, 0).Prop("day", day).Prop("dayFraction", frac, 4);
                j.Prop("night", frac <= 0.25 || frac >= 0.75);
            }
            catch { }
        }
    }
}
