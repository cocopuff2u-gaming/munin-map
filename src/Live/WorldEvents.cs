using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MuninMap.Tiles;
using UnityEngine;
using MuninMap.Util;

namespace MuninMap.Live
{
    // Raids and boss kills for the page's events.
    //
    // Raids: the server picks and runs them (RandEventSystem.m_randomEvent), so a loop here sees
    // one start and end. Boss kills: when a boss dies, the game of the player who owns it sends
    // the server "SetGlobalKey defeated_<boss>" (every kill, not just the first), which the routed
    // RPC observer in MuninMap.cs hands to OnGlobalKey.
    internal static class WorldEvents
    {
        public static readonly int SetGlobalKeyHash = "SetGlobalKey".GetStableHashCode();

        private static readonly Dictionary<string, string> Bosses = new Dictionary<string, string>
        {
            { "defeated_eikthyr", "Eikthyr" }, { "defeated_gdking", "The Elder" }, { "defeated_bonemass", "Bonemass" },
            { "defeated_dragon", "Moder" }, { "defeated_goblinking", "Yagluth" }, { "defeated_queen", "The Queen" },
            { "defeated_fader", "Fader" },
        };
        private static readonly Dictionary<string, float> lastKill = new Dictionary<string, float>();

        // HTTP-free, main thread (the RPC observer runs there)
        public static void OnGlobalKey(ZRoutedRpc.RoutedRPCData data)
        {
            try
            {
                var pkg = new ZPackage(data.m_parameters.GetArray());   // a copy: the game still reads the original
                string key = (pkg.ReadString() ?? "").Trim().ToLowerInvariant();
                int sp = key.IndexOf(' ');
                if (sp > 0) key = key.Substring(0, sp);                    // "key value" on newer versions
                if (!key.StartsWith("defeated_", StringComparison.Ordinal)) return;
                // one kill can reach the server more than once (several players own parts of the fight)
                float now = Time.realtimeSinceStartup;
                if (lastKill.TryGetValue(key, out float t) && now - t < 60f) return;
                lastKill[key] = now;
                string boss = Bosses.TryGetValue(key, out string b) ? b : Pretty(key.Substring("defeated_".Length));
                ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(data.m_senderPeerID) : null;
                var there = peer != null ? NearbyPeers(peer.m_refPos, peer) : new List<ZNetPeer>();
                string who = Names(there);
                Stats.OnBossKill(there);
                if (peer != null) Events.Add("boss", boss, "was defeated" + (who.Length > 0 ? " by " + who : ""), peer.m_refPos.x, peer.m_refPos.z);
                else Events.Add("boss", boss, "was defeated");
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: boss event: " + e.Message); }
        }

        // the raid on now, for the map's raid circle like the game draws: {"name","x","z","r"}, or null
        public static volatile string RaidJson = "null";

        public static IEnumerator RaidLoop()
        {
            string current = null;
            string endText = null;
            while (true)
            {
                try
                {
                    var rs = RandEventSystem.instance;
                    var ev = rs != null ? rs.m_randomEvent : null;
                    string name = ev != null ? ev.m_name : null;
                    if (name != current)
                    {
                        if (current != null) { Events.Add("raid", "Raid", endText ?? "is over"); global::MuninMap.MuninMap.PostWorldEvent("⚔️ Raid: " + (endText ?? "is over")); }
                        RaidJson = "null";
                        if (ev != null)
                        {
                            Vector3 p = ev.m_pos;
                            string start = Localize(ev.m_startMessage, Pretty(ev.m_name));
                            var there = NearbyPeers(p, null, 150f);
                            string who = Names(there);
                            Stats.OnRaid(there);
                            Events.Add("raid", "Raid", start + (who.Length > 0 ? " (near " + who + ")" : ""), p.x, p.z);
                            global::MuninMap.MuninMap.PostWorldEvent("⚔️ Raid: " + start + (who.Length > 0 ? " (near " + who + ")" : ""), p.x, p.z);
                            endText = Localize(ev.m_endMessage, "is over");
                            RaidJson = new JsonWriter(160).BeginObject().Prop("name", start).Prop("x", p.x, 1).Prop("z", p.z, 1).Prop("r", ev.m_eventRange, 0).End().ToString();
                        }
                        current = name;
                    }
                }
                catch (Exception e) { if (MuninConfig.DEBUG) ZLog.LogWarning("MuninMap: raid check: " + e.Message); }
                try { CheckInvasions(); }
                catch (Exception e) { if (MuninConfig.DEBUG) ZLog.LogWarning("MuninMap: invasion check: " + e.Message); }
                yield return new WaitForSeconds(2f);
            }
        }

        // ---- invasions: the game's other kind of event (PersistentEventSystem), not a raid on a base
        // but a site that stays for an hour or two. The game's map draws each like a raid: a red area
        // its radius wide, the pulsing "!", and its name under it. [{"id","name","x","z","r","left"}]
        // ("left": seconds to go, absent when it doesn't end on its own)
        public static volatile string InvasionsJson = "[]";
        private static readonly Dictionary<int, string> invasionNames = new Dictionary<int, string>();   // id -> name, the ones on now

        private static void CheckInvasions()
        {
            var ps = PersistentEventSystem.instance;
            var list = ps != null && ps.m_activePersistentEvents != null ? ps.m_activePersistentEvents.list : null;
            double now = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0;
            var j = new JsonWriter(256).BeginArray();
            var on = new HashSet<int>();
            if (list != null)
                foreach (var ev in list)
                {
                    on.Add(ev.eventId);
                    if (!invasionNames.TryGetValue(ev.eventId, out string name))
                    {
                        string token = null;
                        try { token = ev.Source.mapTokenString; } catch { }
                        string fallback = null;
                        try { fallback = Pretty(ev.internalName); } catch { }
                        name = Localize(token, fallback ?? "Invasion");
                        invasionNames[ev.eventId] = name;
                        // new: started in the last two minutes (this looks every 2 s). Not one carried over
                        // from before a restart, nor one met before the world's clock is loaded (no age yet)
                        double age = now - ev.startTime;
                        if (now > 1 && age >= 0 && age < 120)
                        {
                            string who = Names(NearbyPeers(ev.position, null, ev.radius + 150f));
                            Events.Add("invasion", name, "has begun" + (who.Length > 0 ? " (near " + who + ")" : ""), ev.position.x, ev.position.z);
                            global::MuninMap.MuninMap.PostWorldEvent("🔥 " + name + " has begun" + (who.Length > 0 ? " (near " + who + ")" : ""), ev.position.x, ev.position.z);
                        }
                    }
                    j.BeginObject().Prop("id", ev.eventId).Prop("name", name).Prop("x", ev.position.x, 1).Prop("z", ev.position.z, 1).Prop("r", ev.radius, 0);
                    if (ev.duration >= 0) j.Prop("left", Math.Max(0, ev.startTime + ev.duration - now), 0);
                    j.End();
                }
            foreach (int id in new List<int>(invasionNames.Keys))
                if (!on.Contains(id))
                {
                    Events.Add("invasion", invasionNames[id], "is over");
                    global::MuninMap.MuninMap.PostWorldEvent("🔥 " + invasionNames[id] + " is over");
                    invasionNames.Remove(id);
                }
            InvasionsJson = j.End().ToString();
        }

        // ---- found: boss altars and traders, the moment their ground is explored (World/Markers).
        // ---- first time in a biome, per player.
        // Both remembered in world-events.txt beside the world's map data, so a restart doesn't
        // announce them again. The first time this runs (no file yet), what is already explored
        // counts as found and players the map already knows count as having been everywhere the
        // world has been explored: only what happens from now on is news.
        private static List<World.Markers.Findable> findables;
        private static readonly HashSet<string> found = new HashSet<string>();
        private static readonly HashSet<string> seededKinds = new HashSet<string>();   // kinds of find already tracked: a new kind (an update added it) starts quietly too
        private static readonly Dictionary<string, string[]> foundInfo = new Dictionary<string, string[]>();   // key -> [when (UTC), who]; none for what was found before tracking
        private static readonly Dictionary<string, HashSet<string>> biomes = new Dictionary<string, HashSet<string>>();
        private static HashSet<string> worldBiomes;   // explored somewhere in the world, at first run
        private static bool loaded, seeding;
        private static string StatePath => Path.Combine(global::MuninMap.MuninMap.worldDataPath ?? "", "world-events.txt");

        private static void Load()
        {
            loaded = true;
            seeding = !File.Exists(StatePath);
            if (seeding) return;
            try
            {
                foreach (var line in File.ReadAllLines(StatePath))
                {
                    var t = line.Split('\t');
                    if (t.Length >= 2 && t[0] == "found") { found.Add(t[1]); if (t.Length >= 4) foundInfo[t[1]] = new[] { t[2], t[3] }; }
                    else if (t.Length == 2 && t[0] == "kind") seededKinds.Add(t[1]);
                    else if (t.Length == 3 && t[0] == "biomes") biomes[t[1]] = new HashSet<string>(t[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                }
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: world events state: " + e.Message); }
        }

        private static void Save()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var f in found)
                {
                    sb.Append("found\t").Append(f);
                    if (foundInfo.TryGetValue(f, out var info)) sb.Append('\t').Append(info[0]).Append('\t').Append(info[1]);
                    sb.Append('\n');
                }
                foreach (var k in seededKinds) sb.Append("kind\t").Append(k).Append('\n');
                foreach (var kv in biomes) sb.Append("biomes\t").Append(kv.Key).Append('\t').Append(string.Join(",", kv.Value)).Append('\n');
                File.WriteAllText(StatePath, sb.ToString());
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: world events state: " + e.Message); }
        }

        // the biomes on explored ground (a coarse scan of the fog), for the first run's seeding
        private static HashSet<string> ExploredBiomes()
        {
            var set = new HashSet<string>();
            try
            {
                for (float x = -10400; x <= 10400; x += 48)
                    for (float z = -10400; z <= 10400; z += 48)
                        if (x * x + z * z < 10500f * 10500f && World.Fog.IsExplored(x, z))
                            set.Add(Palette.BiomeName((int)WorldGenerator.instance.GetBiome(x, z)));
            }
            catch { }
            return set;
        }

        // the boss altars and traders found so far, for the stats: what, where, and when and by whom
        // when that was seen (not for what was already found when tracking began)
        public static string DiscoveriesJson()
        {
            var j = new Util.JsonWriter(1024);
            j.BeginArray();
            if (findables != null)
                foreach (var f in findables)
                {
                    if (!found.Contains(f.key)) continue;
                    j.BeginObject().Prop("label", f.label).Prop("kind", f.kind).Prop("x", f.pos.x, 0).Prop("z", f.pos.z, 0);
                    if (foundInfo.TryGetValue(f.key, out var info)) j.Prop("when", info[0]).Prop("who", info[1]);
                    j.End();
                }
            j.End();
            return j.ToString();
        }

        public static IEnumerator DiscoveryLoop()
        {
            yield return new WaitForSeconds(20f);   // the world, fog and first sweep come first
            while (true)
            {
                try { Discover(); } catch (Exception e) { if (MuninConfig.DEBUG) ZLog.LogWarning("MuninMap: discovery: " + e.Message); }
                yield return new WaitForSeconds(3f);
            }
        }

        private static void Discover()
        {
            if (!loaded) Load();
            bool changed = false;
            if (findables == null || findables.Count == 0) findables = World.Markers.Findables();
            bool anyFound = false;
            foreach (var f in findables)
            {
                if (found.Contains(f.key) || !World.Fog.IsExplored(f.pos.x, f.pos.z)) continue;
                found.Add(f.key); changed = true; anyFound = true;
                // quiet for a kind of find this world hasn't tracked before (what was already explored when
                // it was added is not news). Files from before kinds were recorded tracked altars and
                // traders already, so only the mini bosses are new to them.
                bool newKind = !seededKinds.Contains(f.kind) && (seededKinds.Count > 0 || f.kind == "miniboss");
                if (seeding || newKind) continue;
                var there = NearbyPeers(f.pos, null, 200f);
                string who = Names(there);
                foundInfo[f.key] = new[] { DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture), who };
                Stats.OnFound(there);
                string what = f.kind == "boss" ? f.label + "'s altar" : f.kind == "trader" ? f.label + "'s camp" : f.label;
                Events.Add("found", what, "found" + (who.Length > 0 ? " by " + who : ""), f.pos.x, f.pos.z);
            }
            foreach (var f in findables) if (seededKinds.Add(f.kind)) changed = true;
            if (anyFound && !seeding) { World.Markers.Refresh(); MapDataServer.getInstance()?.BroadcastWorldRevision(); }

            // biomes: players who share their position only (the rest stay private)
            if (seeding && worldBiomes == null)
            {
                worldBiomes = ExploredBiomes();
                foreach (var k in Stats.Keys()) if (!biomes.ContainsKey(k)) biomes[k] = new HashSet<string>(worldBiomes);
                changed = true;
            }
            foreach (var p in Players.Current)
            {
                if (!p.hasPos || p.dead || string.IsNullOrEmpty(p.key)) continue;
                string b = p.biome;
                if (string.IsNullOrEmpty(b) || b == "Ocean" || b == "Unknown") continue;
                if (!biomes.TryGetValue(p.key, out var seen)) { biomes[p.key] = seen = new HashSet<string>(); changed = true; }
                if (seen.Contains(b)) continue;
                seen.Add(b); changed = true;
                // a new player's first steps are in the Meadows: no news
                if (!seeding && b != "Meadows") Events.Add("biome", p.name, "entered the " + b + " for the first time", p.x, p.z);
            }
            seeding = false;
            if (changed) Save();
        }

        // ---- everyone slept (EnvMan.SkipToMorning runs on the server when all players are in bed)
        [HarmonyLib.HarmonyPatch(typeof(EnvMan), nameof(EnvMan.SkipToMorning))]
        private class SkipToMorningPatch
        {
            private static void Postfix()
            {
                try
                {
                    double t = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0;
                    float dayLen = EnvMan.instance != null ? EnvMan.instance.m_dayLengthSec : 1800f;
                    // the day it skips to: the next one when slept before midnight, this one after (the
                    // same count as the stats' "day")
                    int day = (int)(t / dayLen) + (t % dayLen > dayLen * 0.5 ? 1 : 0);
                    Events.Add("sleep", "Everyone slept", "through the night, day " + day + " begins");
                    Stats.OnNightSlept();
                }
                catch (Exception e) { ZLog.LogWarning("MuninMap: sleep event: " + e.Message); }
            }
        }

        // players within 100 m of a spot, closest first (the sender too, if given)
        private static List<ZNetPeer> NearbyPeers(Vector3 pos, ZNetPeer sender, float within = 100f)
        {
            var near = new List<KeyValuePair<float, ZNetPeer>>();
            try
            {
                foreach (var p in ZNet.instance.GetPeers())
                {
                    if (string.IsNullOrEmpty(p.m_playerName)) continue;
                    float d = Vector3.Distance(p.m_refPos, pos);
                    if (p == sender) d = -1f;
                    if (d <= within) near.Add(new KeyValuePair<float, ZNetPeer>(d, p));
                }
            }
            catch { }
            near.Sort((a, b) => a.Key.CompareTo(b.Key));
            return near.ConvertAll((kv) => kv.Value);
        }

        // "A", "A and B", "A, B and C"
        private static string Names(List<ZNetPeer> peers)
        {
            var list = peers.ConvertAll((p) => p.m_playerName);
            return list.Count <= 1 ? (list.Count == 1 ? list[0] : "") : string.Join(", ", list.GetRange(0, list.Count - 1)) + " and " + list[list.Count - 1];
        }

        private static string Nearby(Vector3 pos, ZNetPeer sender, float within = 100f) => Names(NearbyPeers(pos, sender, within));

        internal static string Localize(string s, string fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            try
            {
                string t = LocalizeFn(s);
                if (!string.IsNullOrEmpty(t) && !t.StartsWith("$", StringComparison.Ordinal) && !t.StartsWith("[", StringComparison.Ordinal)) return t;
            }
            catch { }
            return fallback;
        }

        // the game's Localization lives in assembly_guiutils, which the plugin doesn't reference:
        // found by name at run time (null if a headless server has none)
        private static Func<string, string> localizer;
        private static bool localizerLooked;
        private static string LocalizeFn(string s)
        {
            if (!localizerLooked)
            {
                localizerLooked = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = asm.GetType("Localization", false);
                    if (type == null) continue;
                    var inst = type.GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var loc = type.GetMethod("Localize", new[] { typeof(string) });
                    if (inst == null || loc == null) continue;
                    localizer = (x) => { var o = inst.GetValue(null, null); return o == null ? x : (string)loc.Invoke(o, new object[] { x }); };
                    break;
                }
            }
            return localizer != null ? localizer(s) : s;
        }

        // "army_theelder" -> "Army theelder": readable enough for an event without a message
        private static string Pretty(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Something";
            s = s.Replace('_', ' ').Trim();
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
