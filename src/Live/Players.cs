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
            public string[] gear;   // right, left, chest, helmet, legs, shoulder, utility (prefab names, "" when empty)
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
        private static readonly int[] hashGear = { "RightItem".GetStableHashCode(), "LeftItem".GetStableHashCode(), "ChestItem".GetStableHashCode(), "HelmetItem".GetStableHashCode(), "LegItem".GetStableHashCode(), "ShoulderItem".GetStableHashCode(), "UtilityItem".GetStableHashCode() };
        public static readonly string[] GearSlots = { "right", "left", "chest", "helmet", "legs", "shoulder", "utility" };
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
                    r = 6f;
                    if (se.m_ballPrefab != null)
                        foreach (var c in se.m_ballPrefab.GetComponentsInChildren<Component>(true))
                        {
                            if (c == null || c.GetType().Name != "ParticleSystemForceField") continue;
                            var p = c.GetType().GetProperty("endRange");
                            if (p != null) r = Convert.ToSingle(p.GetValue(c, null));
                            break;
                        }
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
                    for (int g = 0; g < hashGear.Length; g++) { try { s.gear[g] = ItemName(zdo.GetInt(hashGear[g], 0)); } catch { s.gear[g] = ""; } }
                    try { s.wisp = WispRange(zdo.GetInt(hashGear[hashGear.Length - 1], 0)); } catch { }
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

        private static bool hasWorldWind;
        private static Weather.Now worldWind;

        private static string Build()
        {
            var j = new JsonWriter(256 + current.Count * 160);
            j.BeginObject();
            j.Prop("count", current.Count);
            if (hasWorldWind) j.Prop("windDeg", worldWind.windDeg, 0);
            j.Key("players").BeginArray();
            foreach (var s in current)
            {
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
                }
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
