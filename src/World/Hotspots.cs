using System;
using System.Collections.Generic;
using UnityEngine;
using MuninMap.Util;

namespace MuninMap.World
{
    // What makes a place heavy for the players' computers (their frame rate, not the server's): per
    // 64 m zone, the game's own grid, how many build pieces, lights and fires, creatures and items
    // lying on the ground. Counted by the sweep as it passes each object; a prefab's kind is looked
    // up once. Only zones with a fair load are published.
    internal static class Hotspots
    {
        private const byte None = 0, Light = 1, Creature = 2, Item = 3;
        private static readonly Dictionary<int, byte> kindOf = new Dictionary<int, byte>();
        private static Dictionary<long, int[]> building;   // zone -> pieces, lights, creatures, items
        private static long lastKey = long.MinValue;
        private static int[] last;
        private static volatile string json = "{\"cell\":64,\"zones\":[],\"pending\":true}";   // (until the first sweep is done)

        public static string Json => json;

        public static void Begin()
        {
            building = new Dictionary<long, int[]>(4096);
            lastKey = long.MinValue; last = null;
        }

        // Main thread, from the sweep: one object (creator: who built it, 0 if nobody did)
        public static void Observe(int prefab, long creator, Vector3 p)
        {
            if (building == null) return;
            byte kind = KindOf(prefab);
            if (creator == 0L && (kind == None || kind == Light)) return;
            int zx = Mathf.FloorToInt((p.x + 32f) / 64f), zz = Mathf.FloorToInt((p.z + 32f) / 64f);   // ZoneSystem.GetZone
            long key = ((long)zx << 32) ^ (uint)zz;
            if (key != lastKey)
            {
                if (!building.TryGetValue(key, out last)) building[key] = last = new int[4];
                lastKey = key;
            }
            if (creator != 0L) last[0]++;
            // lights: only built ones (the world's glowing plants and lava rocks are everywhere alike)
            if (kind != None && (kind != Light || creator != 0L)) last[kind]++;
        }

        private static byte KindOf(int prefab)
        {
            if (kindOf.TryGetValue(prefab, out byte k)) return k;
            k = None;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
                if (go != null)
                {
                    if (go.GetComponent<Character>() != null && go.GetComponent<Player>() == null) k = Creature;
                    else if (go.GetComponent<ItemDrop>() != null) k = Item;
                    else if (go.GetComponent<Fireplace>() != null || go.GetComponentInChildren<UnityEngine.Light>(true) != null) k = Light;
                }
            }
            catch { }
            kindOf[prefab] = k;
            return k;
        }

        // Main thread, after the sweep: the zones worth showing, heaviest first
        public static void Finish()
        {
            var b = building;
            building = null; last = null; lastKey = long.MinValue;
            if (b == null) return;
            BuildHistory.Record(b);
            var list = new List<KeyValuePair<long, int[]>>();
            foreach (var kv in b)
            {
                var c = kv.Value;
                if (c[0] >= 150 || c[1] >= 8 || c[2] >= 12 || c[3] >= 60) list.Add(kv);
            }
            list.Sort((x, y) => Score(y.Value).CompareTo(Score(x.Value)));
            var j = new JsonWriter(64 + list.Count * 40);
            j.BeginObject().Prop("cell", 64).Key("zones").BeginArray();   // [zx, zz, pieces, lights, creatures, items]
            int n = 0;
            foreach (var kv in list)
            {
                if (++n > 3000) break;
                int zx = (int)(kv.Key >> 32), zz = (int)(uint)kv.Key;
                var c = kv.Value;
                j.BeginArray().Value(zx).Value(zz).Value(c[0]).Value(c[1]).Value(c[2]).Value(c[3]).End();
            }
            j.End().End();
            json = j.ToString();
        }

        // a rough weight: a light (shadows, flicker, smoke) or a creature (its AI runs on a player's
        // computer) costs about as much as a dozen or more pieces
        public static int Score(int[] c) => c[0] + c[1] * 15 + c[2] * 12 + c[3] * 2;
    }
}
