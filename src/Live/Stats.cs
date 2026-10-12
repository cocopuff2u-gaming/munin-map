using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        private static string StatsPath => Path.Combine(MuninMap.worldDataPath ?? "", "stats.json");

        public static void Load()
        {
            players.Clear();
            onlineHistory.Clear();
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
                        if (!string.IsNullOrEmpty(s.key)) players[s.key] = s;
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
            s.online = true;
            s.lastSeen = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            dirty = true;
        }

        public static void OnLeave(string key, string name)
        {
            var s = Get(key, name);
            s.online = false;
            s.lastSeen = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            dirty = true;
        }

        public static void OnDeath(string name)
        {
            foreach (var s in players.Values)
                if (s.name == name && s.online) { s.deaths++; dirty = true; return; }
            foreach (var s in players.Values)
                if (s.name == name) { s.deaths++; dirty = true; return; }
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
                if (!s.online) { s.online = true; s.sessions++; }
                s.playtime += dt;
                s.lastSeen = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
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
            foreach (var s in players.Values) if (s.online && !seen.Contains(s.key)) s.online = false;

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
            Rebuild(online.Count);
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
            Rebuild(-1);
        }

        // world events (Live/WorldEvents), each credited to the players who were there
        public static void OnRaid(List<ZNetPeer> there) { raidsTotal++; foreach (var p in there) Get(Players.KeyOf(p), p.m_playerName).raids++; dirty = true; Rebuild(-1); }
        public static void OnBossKill(List<ZNetPeer> there) { bossKillsTotal++; foreach (var p in there) Get(Players.KeyOf(p), p.m_playerName).bossKills++; dirty = true; Rebuild(-1); }
        public static void OnFound(List<ZNetPeer> there) { foreach (var p in there) Get(Players.KeyOf(p), p.m_playerName).finds++; dirty = true; Rebuild(-1); }
        public static void OnNightSlept() { nightsSlept++; dirty = true; Rebuild(-1); }

        // the bosses in the order the game has them, and whether the world has beaten each
        // (the game's own "defeated_" keys), with the discoveries Live/WorldEvents keeps
        private static readonly string[][] BossOrder =
        {
            new[] { "defeated_eikthyr", "Eikthyr" }, new[] { "defeated_gdking", "The Elder" }, new[] { "defeated_bonemass", "Bonemass" },
            new[] { "defeated_dragon", "Moder" }, new[] { "defeated_goblinking", "Yagluth" }, new[] { "defeated_queen", "The Queen" },
            new[] { "defeated_fader", "Fader" },
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
            j.PropRaw("discoveries", WorldEvents.DiscoveriesJson());
            int deaths = 0; foreach (var s in players.Values) deaths += s.deaths;
            j.Key("totals").BeginObject().Prop("raids", raidsTotal).Prop("nightsSlept", nightsSlept).Prop("bossKills", bossKillsTotal)
             .Prop("deaths", deaths).Prop("players", players.Count).Prop("peakOnline", peakOnline).Prop("peakOnlineUtc", peakOnlineUtc).End();
        }

        private static int lastOnlineCount;
        private static void Rebuild(int onlineCount)
        {
            if (onlineCount >= 0) lastOnlineCount = onlineCount;
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
                j.Key("onlineHistory").BeginArray();
                int from = Math.Max(0, onlineHistory.Count - 288);
                for (int i = from; i < onlineHistory.Count; i++) j.BeginArray().Value(onlineHistory[i].Key).Value(onlineHistory[i].Value).End();
                j.End();
                j.Key("players").BeginArray();
                var list = new List<PlayerStat>(players.Values);
                list.Sort((a, b) => b.playtime.CompareTo(a.playtime));
                foreach (var s in list) WritePlayer(j, s, full: false);
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
