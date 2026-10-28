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
        }

        private static long Unix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private static void SessionStarts(PlayerStat s) { s.sessionStart = Unix(); }
        private static void SessionEnds(PlayerStat s)
        {
            if (s.sessionStart == 0) return;
            long end = Unix();
            if (end - s.sessionStart >= 30) { s.recent.Add(new[] { s.sessionStart, end }); if (s.recent.Count > 20) s.recent.RemoveAt(0); }
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
                        s.deathAt = (long)JsonParser.Num(d, "deathAt"); s.deathBiome = JsonParser.Str(d, "deathBiome") ?? "";
                        s.deathX = (float)JsonParser.Num(d, "deathX"); s.deathZ = (float)JsonParser.Num(d, "deathZ");
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
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: stats.json not readable: " + e.Message); }
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
                j.Prop("savedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
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
            j.Key("bossesSeen").BeginArray(); foreach (var b in s.bossesSeen) j.Value(b); j.End();
            j.Key("found").BeginArray(); foreach (var f in s.found) j.Value(f); j.End();
            if (s.deathAt > 0)
            {
                j.Prop("deathAt", s.deathAt).Prop("deathBiome", s.deathBiome);
                if (s.deathX != 0f || s.deathZ != 0f) j.Prop("deathX", s.deathX, 1).Prop("deathZ", s.deathZ, 1);
            }
            if (s.playerId != 0L && Structures.Stations.TryGetValue(s.playerId, out var stations) && stations.Count > 0)
            {
                j.Key("stations").BeginArray(); foreach (var n in stations) j.Value(n); j.End();
            }
            j.End();
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
            s.sessions++;
            if (!s.online) SessionStarts(s);
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
            who.deathX = who.deathZ = 0f; who.deathBiome = "";
            if (x.HasValue && z.HasValue)
            {
                who.deathX = x.Value; who.deathZ = z.Value;
                try { who.deathBiome = Tiles.Palette.BiomeName((int)WorldGenerator.instance.GetBiome(x.Value, z.Value)); } catch { }
            }
            dirty = true;
        }

        // Main thread, once per snapshot. `visible` players report a position.
        public static void OnTick(List<Players.Snapshot> online)
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
                if (p.tracked)
                {
                    if (s.hasLast)
                    {
                        double d = Math.Sqrt((p.x - s.lastX) * (p.x - s.lastX) + (p.z - s.lastZ) * (p.z - s.lastZ));
                        if (dt > 0 && d / dt > 60.0 && d > 150.0) s.portalTrips++;   // faster than any boat: a portal
                        else s.distance += d;
                    }
                    s.lastX = p.x; s.lastZ = p.z; s.hasLast = true;
                    if (!string.IsNullOrEmpty(p.biome)) { s.lastBiome = p.biome; if (s.biomes.Add(p.biome)) dirty = true; }
                }
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
            foreach (var s in players.Values)
            {
                int n = s.playerId != 0L && b.TryGetValue(s.playerId, out int c) ? c : 0;
                if (n != s.built) { s.built = n; dirty = true; }
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
        public static void OnNightSlept() { nightsSlept++; dirty = true; stale = true; }

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
        private static void Rebuild(int onlineCount)
        {
            if (onlineCount >= 0) lastOnlineCount = onlineCount;
            stale = false;
            try
            {
                var j = new JsonWriter(4096);
                j.BeginObject();
                j.Key("server").BeginObject();
                j.Prop("startedUtc", serverStartedUtc);
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
                double frac = (t % dayLen) / dayLen;
                j.Prop("worldTime", t, 0).Prop("day", day).Prop("dayFraction", frac, 3);
                j.Prop("night", frac < 0.15 || frac > 0.85);
            }
            catch { }
        }
    }
}
