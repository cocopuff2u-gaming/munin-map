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
            public float x, y, z, yaw, health, maxHealth, stamina, eitr, wisp, staminaTop, eitrTop;
            public bool hasPos, tracked, hidden, dead, pvp, inBed, hasWeather;
            public Weather.Now weather;
            public string[] gear;   // right, left, chest, helmet, legs, shoulder, utility, trinket, rightBack, leftBack (prefab names, "" when empty)
            public int[] quality;   // the item level, for the slots the game sends one (hands, back, cape); 0 unknown
            public int[] variant;   // the item's style (shields, capes: the game's variant number), 0 the first
            public Look look;
            public int baseValue;   // the game's count of base pieces around them (fires, beds, workbenches): 3 or more is a base
            public bool hasNet; public int ping; public float quality01, inBps, outBps; public int sendQueue, queueMs, rateBps;   // hasNet: the socket answered (a ping of 0 is real: on the same network)
            public string version, platform; public uint network; public bool crossplay;   // their connection, from the server's socket
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
        private static readonly int[] hashGear = { "RightItem".GetStableHashCode(), "LeftItem".GetStableHashCode(), "ChestItem".GetStableHashCode(), "HelmetItem".GetStableHashCode(), "LegItem".GetStableHashCode(), "ShoulderItem".GetStableHashCode(), "UtilityItem".GetStableHashCode(), "TrinketItem".GetStableHashCode(),
            "RightBackItem".GetStableHashCode(), "LeftBackItem".GetStableHashCode() };   // (back: weapon and shield put away, e.g. while sailing)
        public static readonly string[] GearSlots = { "right", "left", "chest", "helmet", "legs", "shoulder", "utility", "trinket", "rightBack", "leftBack" };
        private const int Utility = 6;
        // the item level, which the game sends only for the hands, the back and the cape (per slot above; 0 none)
        private static readonly int[] hashQuality = { "RightItemQuality".GetStableHashCode(), "LeftItemQuality".GetStableHashCode(), 0, 0, 0, "ShoulderItemQuality".GetStableHashCode(), 0, 0,
            "RightBackItemQuality".GetStableHashCode(), "LeftBackItemQuality".GetStableHashCode() };
        // the item's style, for the slots that have one (per slot above; 0 none)
        private static readonly int[] hashVariant = { 0, "LeftItemVariant".GetStableHashCode(), 0, 0, 0, "ShoulderItemVariant".GetStableHashCode(), 0, 0, 0, "LeftBackItemVariant".GetStableHashCode() };
        private static readonly int hashModel = "ModelIndex".GetStableHashCode(), hashSkin = "SkinColor".GetStableHashCode(), hashHairColor = "HairColor".GetStableHashCode();
        private static readonly int hashHair = "HairItem".GetStableHashCode(), hashBeard = "BeardItem".GetStableHashCode(), hashBase = "baseValue".GetStableHashCode();
        private static Dictionary<int, string> itemNames;

        // The last weapon held, for a while: players often put theirs away (back in the bag) to run, and a card,
        // or the one kept for when they're offline, would then show empty hands. With nothing in the hands or on
        // the back, the weapon (and shield) last held is shown put away on the back, as the game draws a sheathed
        // one, until they've been online ten minutes without holding a weapon. (Tools and torches aren't
        // remembered: a hammer is in the hands for building, not carried.)
        private const int Right = 0, Left = 1, RightBack = 8, LeftBack = 9;
        private const float RememberFor = 600f;
        private sealed class Held { public string right = "", left = ""; public int rightQ, leftQ, leftV; public float unarmed = RememberFor, last; }
        private static readonly Dictionary<string, Held> held = new Dictionary<string, Held>();
        private static readonly Dictionary<string, bool> isWeapon = new Dictionary<string, bool>();

        private static bool IsWeapon(string item)
        {
            if (string.IsNullOrEmpty(item)) return false;
            if (isWeapon.TryGetValue(item, out bool w)) return w;
            try
            {
                var shared = ObjectDB.instance.GetItemPrefab(item)?.GetComponent<ItemDrop>()?.m_itemData?.m_shared;
                if (shared == null) return false;
                var t = shared.m_itemType;
                w = t == ItemDrop.ItemData.ItemType.OneHandedWeapon || t == ItemDrop.ItemData.ItemType.TwoHandedWeapon || t == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft
                    || t == ItemDrop.ItemData.ItemType.Bow || t == ItemDrop.ItemData.ItemType.Shield;
            }
            catch { return false; }
            return isWeapon[item] = w;
        }

        private static void RememberWeapon(ref Snapshot s)
        {
            float now = Time.realtimeSinceStartup;
            if (!held.TryGetValue(s.key, out var h)) held[s.key] = h = new Held { last = now };
            float dt = Mathf.Clamp(now - h.last, 0f, 10f); h.last = now;   // (online time only: a gap while away doesn't count)
            var g = s.gear;
            bool hands = IsWeapon(g[Right]) || IsWeapon(g[Left]);
            if (hands)
            {
                // held now: remembered as it is
                h.right = IsWeapon(g[Right]) ? g[Right] : ""; h.rightQ = h.right.Length > 0 ? s.quality[Right] : 0;
                h.left = IsWeapon(g[Left]) ? g[Left] : ""; h.leftQ = h.left.Length > 0 ? s.quality[Left] : 0; h.leftV = h.left.Length > 0 ? s.variant[Left] : 0;
                h.unarmed = 0f;
                return;
            }
            h.unarmed += dt;
            bool empty = g[Right].Length == 0 && g[Left].Length == 0 && g[RightBack].Length == 0 && g[LeftBack].Length == 0;
            if (!empty || h.unarmed > RememberFor || (h.right.Length == 0 && h.left.Length == 0)) return;
            if (h.right.Length > 0) { g[RightBack] = h.right; s.quality[RightBack] = h.rightQ; }
            if (h.left.Length > 0) { g[LeftBack] = h.left; s.quality[LeftBack] = h.leftQ; s.variant[LeftBack] = h.leftV; }
        }

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

        public static void Forget(ZNetPeer peer) { if (peer != null) { keyByPeer.Remove(peer.m_uid); tops.Remove(peer.m_uid); } }

        // How full their stamina and eitr bars are drawn. The game shares only what they have now, not
        // their most (that comes from the food they ate, which stays in their own game), so the page
        // shows just that number, and fills the bar against the most they've had since they last ate.
        // Food's share shrinks as it wears off, health's with it, so it's kept per point of food health
        // (25 health, 50 stamina and no eitr is what they have with no food), and forgotten when they
        // eat (their max health goes up).
        private static readonly Dictionary<long, float[]> tops = new Dictionary<long, float[]>();   // peer -> max health, stamina and eitr per food health
        private static void Tops(ref Snapshot s)
        {
            if (!tops.TryGetValue(s.id, out var t) || s.maxHealth > t[0] + 0.5f) tops[s.id] = t = new[] { s.maxHealth, 0f, 0f };
            t[0] = s.maxHealth;
            float food = s.maxHealth - 25f;
            if (food < 1f) { s.staminaTop = Mathf.Max(50f, s.stamina); s.eitrTop = Mathf.Max(0f, s.eitr); return; }
            t[1] = Mathf.Max(t[1], (s.stamina - 50f) / food);
            t[2] = Mathf.Max(t[2], s.eitr / food);
            s.staminaTop = Mathf.Max(s.stamina, 50f + t[1] * food);
            s.eitrTop = Mathf.Max(s.eitr, t[2] * food);
        }


        // Everyone connected, with or without a character in the world right now (between dying and
        // coming back they have none): their visit goes on
        public static readonly HashSet<string> Connected = new HashSet<string>();

        // Main thread.
        public static void Refresh(List<ZNetPeer> peers)
        {
            MuninConfig.ReadSpeeds();
            current.Clear();
            Connected.Clear();
            if (peers != null)
            {
                foreach (var peer in peers)
                {
                    if (peer == null || peer.m_server || string.IsNullOrEmpty(peer.m_playerName)) continue;
                    Connected.Add(KeyOf(peer));
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
                    Tops(ref s);
                    s.gear = new string[hashGear.Length];
                    s.quality = new int[hashGear.Length];
                    s.variant = new int[hashGear.Length];
                    for (int g = 0; g < hashGear.Length; g++)
                    {
                        try { s.gear[g] = ItemName(zdo.GetInt(hashGear[g], 0)); } catch { s.gear[g] = ""; }
                        if (s.gear[g].Length == 0) continue;
                        if (hashQuality[g] != 0) try { s.quality[g] = zdo.GetInt(hashQuality[g], 0); } catch { }
                        if (hashVariant[g] != 0) try { s.variant[g] = zdo.GetInt(hashVariant[g], 0); } catch { }
                    }
                    try { s.wisp = WispRange(zdo.GetInt(hashGear[Utility], 0)); } catch { }
                    if (!s.dead) RememberWeapon(ref s);
                    try
                    {
                        s.look.model = zdo.GetInt(hashModel, 0);
                        s.look.skin = zdo.GetVec3(hashSkin, Vector3.one); s.look.hair = zdo.GetVec3(hashHairColor, Vector3.one);
                        s.look.hairItem = ItemName(zdo.GetInt(hashHair, 0)); s.look.beardItem = ItemName(zdo.GetInt(hashBeard, 0));
                        s.baseValue = zdo.GetInt(hashBase, 0);
                    }
                    catch { }
                    try { ReadConnection(peer.m_socket, ref s); } catch { }
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

        // Their connection as Steam measures it. The game's own GetConnectionQuality asks the Steam client
        // interface, which a dedicated server doesn't run (it always fails there), so on a dedicated server
        // the game-server interface is asked, as the game itself does for sending. Steam's own count of
        // what's waiting is used too: network mods report a smaller send queue to the game on purpose.
        private static void ReadConnection(ISocket socket, ref Snapshot s)
        {
            if (socket == null) return;
            if (socket is ZSteamSocket steam && ZNet.instance != null && ZNet.instance.IsDedicated())
            {
                var st = default(Steamworks.SteamNetConnectionRealTimeStatus_t);
                var lanes = default(Steamworks.SteamNetConnectionRealTimeLaneStatus_t);
                if (Steamworks.SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(steam.m_con, ref st, 0, ref lanes) != Steamworks.EResult.k_EResultOK) return;
                if (st.m_nPing < 0) return;   // not measured yet
                s.hasNet = true; s.ping = st.m_nPing; s.quality01 = Quality(st.m_flConnectionQualityLocal, st.m_flConnectionQualityRemote);
                s.inBps = st.m_flInBytesPerSec; s.outBps = st.m_flOutBytesPerSec;
                s.sendQueue = st.m_cbPendingReliable + st.m_cbPendingUnreliable;
                s.queueMs = (int)Math.Min(60000L, (long)st.m_usecQueueTime / 1000L);
                s.rateBps = st.m_nSendRateBytesPerSecond;
                return;
            }
            socket.GetConnectionQuality(out float localQ, out float remoteQ, out int ping, out float outB, out float inB);
            if (ping > 0) { s.hasNet = true; s.ping = ping; s.quality01 = Quality(localQ, remoteQ); }
            s.inBps = inB; s.outBps = outB;
            s.sendQueue = socket.GetSendQueueSize();
        }
        // the share of packets that arrive, the worse direction (Steam: -1 while it doesn't know yet)
        private static float Quality(float local, float remote)
        {
            float q = local >= 0f && remote >= 0f ? Mathf.Min(local, remote) : local >= 0f ? local : remote >= 0f ? remote : 1f;
            return Mathf.Clamp01(q);
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
                if (s.stamina >= 0f) j.Prop("stamina", (int)s.stamina).Prop("staminaTop", (int)Mathf.Ceil(s.staminaTop));
                if (s.eitr >= 0f) j.Prop("eitr", (int)s.eitr).Prop("eitrTop", (int)Mathf.Ceil(s.eitrTop));
                if (s.gear != null)
                {
                    j.Key("gear").BeginObject();
                    for (int g = 0; g < s.gear.Length; g++) if (!string.IsNullOrEmpty(s.gear[g])) j.Prop(GearSlots[g], s.gear[g]);
                    j.End();
                    j.Key("levels").BeginObject();
                    for (int g = 0; g < s.gear.Length; g++) if (s.quality != null && s.quality[g] > 0) j.Prop(GearSlots[g], s.quality[g]);
                    j.End();
                    j.Key("variants").BeginObject();
                    for (int g = 0; g < s.gear.Length; g++) if (s.variant != null && s.variant[g] > 0) j.Prop(GearSlots[g], s.variant[g]);
                    j.End();
                }
                WriteLook(j, s.look);
                var gear = s.gear; var variant = s.variant;
                if (gear != null) j.Prop("portrait", Models.Portraits.Id(s.look,
                    slot => { int g = Array.IndexOf(GearSlots, slot); return g >= 0 && g < gear.Length ? gear[g] : ""; },
                    slot => { int g = Array.IndexOf(GearSlots, slot); return g >= 0 && variant != null && g < variant.Length ? variant[g] : 0; }));
                j.Prop("atBase", s.baseValue >= 3);
                if (s.hasNet)
                {
                    j.Prop("ping", s.ping).Prop("netQ", s.quality01, 2).Prop("sendQ", s.sendQueue).Prop("inBps", s.inBps, 0).Prop("outBps", s.outBps, 0);
                    if (s.queueMs > 0) j.Prop("queueMs", s.queueMs);
                    if (s.rateBps > 0) j.Prop("rateBps", s.rateBps);
                }
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
