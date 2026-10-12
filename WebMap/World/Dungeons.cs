using System;
using System.Collections.Generic;
using UnityEngine;
using WebMap.Util;

namespace WebMap.World
{
    // What the world save tells about each dungeon's inside, for its marker's popup: the game builds
    // a dungeon's rooms ~5000 m above its entrance, at the entrance's x/z, the first time a player
    // comes near. So every object up there belongs to the nearest entrance below, and from them:
    // chests and how many are empty, what is still there to pick up or mine (by the game's names:
    // bone remains, crystals, muddy scrap piles...), locked gates and
    // whether they are open, monsters still alive in there, how many rooms it has, and any sign a
    // player was inside (an emptied chest, something built, a grave).
    // A dungeon with nothing up there hasn't been generated yet: nobody has come near it.
    internal static class Dungeons
    {
        public sealed class Inside
        {
            public int chests, emptied, picked, gates, opened, built, graves, monsters, rooms;
            public readonly SortedDictionary<string, int> left = new SortedDictionary<string, int>();   // name -> how many still there
            // (not the items lying about, nor the monsters: the game wakes a dungeon's spawners when a
            // player is anywhere above it, and what they drop proves nobody went in)
            public bool Visited => emptied > 0 || picked > 0 || opened > 0 || built > 0 || graves > 0;
        }

        private enum Kind { None, Chest, Pickable, Gate, Monster, Generator, Mining }
        private static readonly Dictionary<int, Kind> kinds = new Dictionary<int, Kind>();
        private static readonly Dictionary<int, string> names = new Dictionary<int, string>();   // pickables and mining rocks: the game's name
        private static readonly int tombstoneHash = "Player_tombstone".GetStableHashCode();
        private const float REACH = 160f;   // how far from its entrance (x/z) a dungeon's rooms may reach

        private static Dictionary<long, List<Vector3>> entrances;   // zone -> dungeon entrances in it
        private static Dictionary<string, Inside> building, done = new Dictionary<string, Inside>();

        public static string KeyOf(Vector3 p) => Mathf.RoundToInt(p.x) + "," + Mathf.RoundToInt(p.z);
        public static Inside Get(Vector3 entrance) { done.TryGetValue(KeyOf(entrance), out var d); return d; }

        // Main thread, start of a sweep: where the dungeons are (names: Markers.DungeonName)
        public static void Begin(Func<string, bool> isDungeon)
        {
            entrances = new Dictionary<long, List<Vector3>>();
            building = new Dictionary<string, Inside>();
            try
            {
                var zs = ZoneSystem.instance;
                if (zs == null || zs.m_locationInstances == null) return;
                foreach (var li in zs.m_locationInstances.Values)
                {
                    if (!isDungeon(li.m_location?.m_prefabName)) continue;
                    long k = ZoneKey(li.m_position.x, li.m_position.z);
                    if (!entrances.TryGetValue(k, out var l)) entrances[k] = l = new List<Vector3>(1);
                    l.Add(li.m_position);
                }
            }
            catch (Exception e) { if (WebMapConfig.DEBUG) ZLog.LogWarning("MuninMap: dungeons: " + e.Message); }
        }

        private static long ZoneKey(float x, float z) => ((long)Mathf.FloorToInt(x / 64f) << 32) ^ (uint)Mathf.FloorToInt(z / 64f);

        // Main thread, every object of the sweep (cheap for the ones on the ground)
        public static void Observe(ZDO zdo, int prefab, Vector3 p, long creator)
        {
            if (entrances == null || entrances.Count == 0) return;
            float reach = REACH;
            if (p.y < 3000f)
            {
                // on the ground only a dungeon's own locked gate (a sunken crypt's is at its entrance)
                if (creator != 0L || KindOf(prefab) != Kind.Gate) return;
                reach = 40f;
            }
            Vector3? best = null;
            float bestD = reach * reach;
            int r = Mathf.CeilToInt(reach / 64f), zx = Mathf.FloorToInt(p.x / 64f), zz = Mathf.FloorToInt(p.z / 64f);
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (!entrances.TryGetValue(((long)(zx + dx) << 32) ^ (uint)(zz + dz), out var l)) continue;
                    foreach (var e in l)
                    {
                        float d = (e.x - p.x) * (e.x - p.x) + (e.z - p.z) * (e.z - p.z);
                        if (d < bestD) { bestD = d; best = e; }
                    }
                }
            if (best == null) return;
            string key = KeyOf(best.Value);
            if (!building.TryGetValue(key, out var s)) building[key] = s = new Inside();
            if (prefab == tombstoneHash) { s.graves++; return; }
            if (creator != 0L) { s.built++; return; }
            switch (KindOf(prefab))
            {
                case Kind.Chest:
                    s.chests++;
                    if (ItemCount(zdo) == 0) s.emptied++;
                    break;
                case Kind.Pickable:   // most vanish when taken; some stay, marked picked
                    if (zdo.GetBool(ZDOVars.s_picked)) s.picked++;
                    else Count(s, prefab);
                    break;
                case Kind.Mining:     // gone once mined out
                    Count(s, prefab);
                    break;
                case Kind.Gate:
                    s.gates++;
                    if (zdo.GetInt(ZDOVars.s_state) != 0) s.opened++;
                    break;
                case Kind.Monster:
                    if (!zdo.GetBool(ZDOVars.s_tamed)) s.monsters++;
                    break;
                case Kind.Generator:
                    s.rooms = Math.Max(s.rooms, RoomCount(zdo));
                    break;
            }
        }

        // Main thread, end of the sweep (before Markers.Finish, which shows these)
        public static void Finish()
        {
            if (building != null) done = building;
            building = null; entrances = null;
        }

        // items in a chest (Inventory.Save: version, then the count); -1 if it never had any
        // (its contents are made by the first player to load it)
        private static int ItemCount(ZDO zdo)
        {
            byte[] raw = zdo.GetByteArray(ZDOVars.s_items);
            if (raw == null || raw.Length < 6) return zdo.GetBool(ZDOVars.s_addedDefaultItems) ? 0 : -1;
            var pkg = new ZPackage(raw);
            int version = pkg.ReadInt();
            return version >= 108 ? pkg.ReadUShort() : pkg.ReadInt();
        }

        private static void Count(Inside s, int prefab)
        {
            string n = names.TryGetValue(prefab, out var v) ? v : "?";
            s.left.TryGetValue(n, out int c);
            s.left[n] = c + 1;
        }

        // DungeonGenerator.Save: the room list (an int count first); older saves kept a "rooms" int
        private static int RoomCount(ZDO zdo)
        {
            byte[] raw = zdo.GetByteArray(ZDOVars.s_roomData);
            if (raw != null && raw.Length >= 4) return BitConverter.ToInt32(raw, 0);
            return zdo.GetInt(ZDOVars.s_rooms);
        }

        private static Kind KindOf(int prefab)
        {
            if (kinds.TryGetValue(prefab, out var k)) return k;
            k = Kind.None;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
                if (go != null)
                {
                    var door = go.GetComponent<Door>();
                    if (go.GetComponent<Container>() != null) k = Kind.Chest;   // (player-built ones have a creator: counted as built)
                    else if (go.GetComponent<Pickable>() != null) k = Kind.Pickable;
                    else if (door != null && door.m_keyItem != null) k = Kind.Gate;
                    else if (go.GetComponent<MonsterAI>() != null) k = Kind.Monster;
                    else if (go.GetComponent<MineRock5>() != null || go.GetComponent<MineRock>() != null) k = Kind.Mining;
                    if (k == Kind.Pickable || k == Kind.Mining)
                    {
                        string n = k == Kind.Pickable ? go.GetComponent<Pickable>().GetHoverName()
                                 : go.GetComponent<MineRock5>() != null ? go.GetComponent<MineRock5>().m_name : go.GetComponent<MineRock>().m_name;
                        names[prefab] = Live.WorldEvents.Localize(n, go.name.Replace("Pickable_", "").Replace('_', ' '));
                    }
                    else if (go.GetComponent<DungeonGenerator>() != null) k = Kind.Generator;
                }
            }
            catch { }
            kinds[prefab] = k;
            return k;
        }
    }
}
