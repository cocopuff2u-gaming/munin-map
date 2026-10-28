using System;
using System.Collections.Generic;
using UnityEngine;
using MuninMap.Tiles;
using MuninMap.Util;
using MuninMap.World;

namespace MuninMap.Live
{
    // The live player snapshot.
    //
    // Built on the main thread once per update interval from ZNet's peer list
    // and each player's ZDO; every other thread only ever reads the JSON
    // string this produces. A player who has not enabled "visible to other
    // players" on the in-game map is listed but sent without a position
    // (unless the server is configured to ignore that), and the fog is still
    // lifted where they walk when `always_map` is on, as before.
    internal static class Players
    {
        public struct Snapshot
        {
            public long id, playerId; public string key, name, biome;
            public float x, y, z, yaw, health, maxHealth, stamina, eitr, wisp;
            public bool hasPos, tracked, hidden, dead, pvp, inBed, hasWeather;
            public Weather.Now weather;
            public string[] gear;   // right, left, chest, helmet, legs, shoulder, utility, trinket (prefab names, "" when empty)
            public int[] quality;   // the item level, for the slots the game sends one (right, left, shoulder); 0 unknown
            public Look look;
            public int baseValue;   // the game's count of base pieces around them (fires, beds, workbenches): 3 or more is a base
            public int ping; public float quality01, inBps, outBps; public int sendQueue;
            public string version, platform; public uint network; public bool crossplay;   // their connection, from the server's socket (ping 0: not known)
        }

        // how the character looks, as their game syncs it for others to draw them
        public struct Look
        {
            public int model;                    // 0 male, 1 female
            public Vector3 skin, hair;           // colours (0-1)
            public string hairItem, beardItem;   // prefab names
        }

        private static readonly List<Snapshot> current = new List<Snapshot>();
        private static volatile string json = "{\"count\":0,\"players\":[]}";
        private static readonly Dictionary<long, string> keyByPeer = new Dictionary<long, string>();
        private static readonly int hashPvp = "pvp".GetStableHashCode();
        private static readonly int hashInBed = "inBed".GetStableHashCode();
        private static readonly int hashDead = "dead".GetStableHashCode();
        private static readonly int hashHealth = "health".GetStableHashCode();
        private static readonly int hashMaxHealth = "max_health".GetStableHashCode();
        private static readonly int hashStamina = "stamina".GetStableHashCode();
        private static readonly int hashEitr = "eitr".GetStableHashCode();
        // what the player has equipped, as the visual-equipment sync stores it: item name hashes
        private static readonly int[] hashGear = { "RightItem".GetStableHashCode(), "LeftItem".GetStableHashCode(), "ChestItem".GetStableHashCode(), "HelmetItem".GetStableHashCode(), "LegItem".GetStableHashCode(), "ShoulderItem".GetStableHashCode(), "UtilityItem".GetStableHashCode(), "TrinketItem".GetStableHashCode() };
        public static readonly string[] GearSlots = { "right", "left", "chest", "helmet", "legs", "shoulder", "utility", "trinket" };
        private const int Utility = 6;
        // the item level, which the game sends only for what's in the hands and the cape (per slot above; 0 none)
        private static readonly int[] hashQuality = { "RightItemQuality".GetStableHashCode(), "LeftItemQuality".GetStableHashCode(), 0, 0, 0, "ShoulderItemQuality".GetStableHashCode(), 0, 0 };
        private static readonly int hashModel = "ModelIndex".GetStableHashCode(), hashSkin = "SkinColor".GetStableHashCode(), hashHairColor = "HairColor".GetStableHashCode();
        private static readonly int hashHair = "HairItem".GetStableHashCode(), hashBeard = "BeardItem".GetStableHashCode(), hashBase = "baseValue".GetStableHashCode();
        private static Dictionary<int, string> itemNames;

        // item name hash -> prefab name, from the object database (built once, lazily)
        private static string ItemName(int hash)
        {
            if (hash == 0) return "";
            if (itemNames == null)
            {
                var d = new Dictionary<int, string>();
                try { foreach (var go in ObjectDB.instance.m_items) if (go != null) d[go.name.GetStableHashCode()] = go.name; } catch { }
                if (d.Count == 0) return "";
                itemNames = d;
            }
            return itemNames.TryGetValue(hash, out string n) ? n : "";
        }

        // How far a utility item (the Wisplight) clears the Mistlands mist around its wearer: its
        // status effect's ball (SE_Demister) has a particle force field, and the mist stays out of its
        // end range (ParticleMist). 0 for anything else. Read off the prefab by name, as the server
        // build doesn't reference Unity's particle module.
        private static readonly Dictionary<int, float> wispRange = new Dictionary<int, float>();
        public static float WispRange(int itemHash)
        {
            if (itemHash == 0) return 0f;
            if (wispRange.TryGetValue(itemHash, out float r)) return r;
            r = 0f;
            try
            {
                var go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(itemHash) : null;
                var se = go != null ? go.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_equipStatusEffect as SE_Demister : null;
                if (se != null)
                {
                    r = se.m_ballPrefab != null ? Demisters.RangeOf(se.m_ballPrefab) : 0f;
                    if (r <= 0f) r = 6f;
                    ZLog.Log($"MuninMap: {go.name} clears the mist for {r:F1} m");
                }
            }
            catch { }
            wispRange[itemHash] = r;
            return r;
        }

        public static string Json => json;
        public static List<Snapshot> Current => current;

        // A stable identity for stats: one per character, so the platform user id (when the socket
        // knows it) and the character's name, as one account can have several characters. Else
        // just the name.
        public static string KeyOf(ZNetPeer peer)
        {
            if (peer == null) return "";
            if (keyByPeer.TryGetValue(peer.m_uid, out string k)) return k;
            string key = null;
            try { key = peer.m_rpc?.GetSocket()?.GetHostName(); } catch { }
            key = string.IsNullOrEmpty(key) ? "name:" + peer.m_playerName : key + "/" + peer.m_playerName;
            keyByPeer[peer.m_uid] = key;
            return key;
        }

        public static void Forget(ZNetPeer peer) { if (peer != null) keyByPeer.Remove(peer.m_uid); }

        // Main thread.
        public static void Refresh(List<ZNetPeer> peers)
        {
            current.Clear();
            if (peers != null)
            {
                foreach (var peer in peers)
                {
                    if (peer == null || peer.m_server || string.IsNullOrEmpty(peer.m_playerName)) continue;
                    ZDO zdo = null;
                    try { zdo = ZDOMan.instance.GetZDO(peer.m_characterID); } catch { }
                    if (zdo == null) continue;
                    var s = new Snapshot { id = peer.m_uid, key = KeyOf(peer), name = peer.m_playerName };
                    Vector3 pos = zdo.GetPosition();
                    s.maxHealth = Mathf.Ceil(zdo.GetFloat(hashMaxHealth, 25f));
                    s.health = Mathf.Ceil(zdo.GetFloat(hashHealth, s.maxHealth));
                    if (s.maxHealth < s.health) s.maxHealth = s.health;
                    s.stamina = zdo.GetFloat(hashStamina, -1f);
                    s.eitr = zdo.GetFloat(hashEitr, -1f);
                    s.gear = new string[hashGear.Length];
                    s.quality = new int[hashGear.Length];
                    for (int g = 0; g < hashGear.Length; g++)
                    {
                        try { s.gear[g] = ItemName(zdo.GetInt(hashGear[g], 0)); } catch { s.gear[g] = ""; }
                        if (hashQuality[g] != 0 && s.gear[g].Length > 0) try { s.quality[g] = zdo.GetInt(hashQuality[g], 0); } catch { }
                    }
                    try { s.wisp = WispRange(zdo.GetInt(hashGear[Utility], 0)); } catch { }
                    try
                    {
                        s.look.model = zdo.GetInt(hashModel, 0);
                        s.look.skin = zdo.GetVec3(hashSkin, Vector3.one); s.look.hair = zdo.GetVec3(hashHairColor, Vector3.one);
                        s.look.hairItem = ItemName(zdo.GetInt(hashHair, 0)); s.look.beardItem = ItemName(zdo.GetInt(hashBeard, 0));
                        s.baseValue = zdo.GetInt(hashBase, 0);
                    }
                    catch { }
                    try
                    {
                        float localQ = 0f, remoteQ = 0f, outB = 0f, inB = 0f; int ping = 0;
                        peer.m_socket?.GetConnectionQuality(out localQ, out remoteQ, out ping, out outB, out inB);
                        if (ping > 0) { s.ping = ping; s.quality01 = Mathf.Clamp01(Mathf.Min(localQ, remoteQ)); }
                        s.inBps = inB; s.outBps = outB;
                        s.sendQueue = peer.m_socket?.GetSendQueueSize() ?? 0;
                    }
                    catch { }
                    // their game: version and network version (from when they joined), and the platform
                    // (Steam, or through crossplay: Steam, Xbox...). Never their address.
                    if (Patches.PeerInfo_Patch.TryGet(peer.m_rpc, out var pi)) { s.version = pi.version; s.network = pi.network; }
                    try
                    {
                        s.crossplay = peer.m_socket != null && peer.m_socket.GetType().Name.Contains("PlayFab");
                        string host = peer.m_socket?.GetHostName() ?? "";
                        int us = host.IndexOf('_');
                        s.platform = us > 0 ? host.Substring(0, us) : s.crossplay ? "" : "Steam";
                    }
                    catch { }
                    s.dead = zdo.GetBool(hashDead, false);
                    try { s.playerId = zdo.GetLong(ZDOVars.s_playerID, 0L); } catch { }
                    s.pvp = zdo.GetBool(hashPvp, false);
                    s.inBed = zdo.GetBool(hashInBed, false);
                    s.hidden = !peer.m_publicRefPos;
                    bool showPos = peer.m_publicRefPos || MuninConfig.ALWAYS_VISIBLE;
                    bool trackPos = showPos || MuninConfig.ALWAYS_MAP;
                    if (trackPos)
                    {
                        s.x = pos.x; s.y = pos.y; s.z = pos.z;
                        try { s.yaw = zdo.GetRotation().eulerAngles.y; } catch { }
                        try { s.biome = Palette.BiomeName((int)WorldGenerator.instance.GetBiome(pos.x, pos.z)); } catch { s.biome = ""; }
                        s.hasWeather = Weather.At(pos, out s.weather);
                    }
                    s.hasPos = showPos;
                    s.tracked = trackPos;
                    current.Add(s);
                }
            }
            // the wind's direction is the same everywhere (its strength is the weather's): for the page's
            // wind arrow, with or without anyone on
            hasWorldWind = Weather.At(Vector3.zero, out worldWind);
            json = Build();
        }

        // {"model":0,"skin":"#rrggbb","hairColor":"#rrggbb","hair":"Hair3","beard":"Beard5"}
        public static void WriteLook(JsonWriter j, Look l)
        {
            j.Key("look").BeginObject().Prop("model", l.model).Prop("skin", Hex(l.skin)).Prop("hairColor", Hex(l.hair));
            if (!string.IsNullOrEmpty(l.hairItem)) j.Prop("hair", l.hairItem);
            if (!string.IsNullOrEmpty(l.beardItem)) j.Prop("beard", l.beardItem);
            j.End();
        }
        public static string Hex(Vector3 c) => "#" + B(c.x) + B(c.y) + B(c.z);
        private static string B(float v) => ((int)Mathf.Round(Mathf.Clamp01(v) * 255f)).ToString("x2");

        private static bool hasWorldWind;
        private static Weather.Now worldWind;

        private static string Build()
        {
            var j = new JsonWriter(256 + current.Count * 160);
            j.BeginObject();
            int shown = 0; foreach (var p in current) if (!MuninConfig.IsHidden(p.name)) shown++;
            j.Prop("count", shown);
            j.Prop("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());   // when these positions were taken: the page plays them back on this clock
            if (hasWorldWind) j.Prop("windDeg", worldWind.windDeg, 0);
            j.Key("players").BeginArray();
            foreach (var s in current)
            {
                if (MuninConfig.IsHidden(s.name)) continue;   // hidden_players
                j.BeginObject();
                j.Prop("id", s.id).Prop("name", s.name);
                j.Prop("health", (int)s.health).Prop("maxHealth", (int)s.maxHealth);
                if (s.stamina >= 0f) j.Prop("stamina", (int)s.stamina);
                if (s.eitr >= 0f) j.Prop("eitr", (int)s.eitr);
                if (s.gear != null)
                {
                    j.Key("gear").BeginObject();
                    for (int g = 0; g < s.gear.Length; g++) if (!string.IsNullOrEmpty(s.gear[g])) j.Prop(GearSlots[g], s.gear[g]);
                    j.End();
                    j.Key("levels").BeginObject();
                    for (int g = 0; g < s.gear.Length; g++) if (s.quality != null && s.quality[g] > 0) j.Prop(GearSlots[g], s.quality[g]);
                    j.End();
                }
                WriteLook(j, s.look);
                var gear = s.gear;
                if (gear != null) j.Prop("portrait", Models.Portraits.Id(s.look, slot => { int g = Array.IndexOf(GearSlots, slot); return g >= 0 && g < gear.Length ? gear[g] : ""; }));
                j.Prop("atBase", s.baseValue >= 3);
                if (s.ping > 0) j.Prop("ping", s.ping).Prop("netQ", s.quality01, 2).Prop("sendQ", s.sendQueue).Prop("inBps", s.inBps, 0).Prop("outBps", s.outBps, 0);
                if (!string.IsNullOrEmpty(s.version)) j.Prop("version", s.version).Prop("netVersion", (long)s.network);
                if (!string.IsNullOrEmpty(s.platform)) j.Prop("platform", s.platform);
                if (s.crossplay) j.Prop("crossplay", true);
                j.Prop("dead", s.dead).Prop("pvp", s.pvp).Prop("inBed", s.inBed).Prop("hidden", s.hidden);
                if (s.hasPos)
                {
                    j.Prop("x", s.x, 1).Prop("z", s.z, 1).Prop("y", s.y, 1).Prop("yaw", s.yaw, 0).Prop("biome", s.biome ?? "");
                    if (s.wisp > 0f) j.Prop("wisp", s.wisp, 1);
                    if (s.hasWeather) j.Prop("weather", s.weather.name).Prop("windDeg", s.weather.windDeg, 0).Prop("wind", s.weather.wind, 2);
                }
                j.End();
            }
            j.End();
            j.End();
            return j.ToString();
        }
    }
}
