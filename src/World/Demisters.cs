using System;
using System.Collections.Generic;
using UnityEngine;
using MuninMap.Util;

namespace MuninMap.World
{
    // What keeps the Mistlands mist away in one spot: the Dvergr lanterns, and anything else that
    // carries the game's Demister (a particle force field the mist stays out of). Found by the world
    // sweep; the page cuts their clearings out of its mist (data/mistclear.json). The Wisplight a
    // player wears is live and comes with the players instead (Live/Players).
    internal static class Demisters
    {
        private static readonly Dictionary<int, float> rangeByPrefab = new Dictionary<int, float>();
        private static List<Vector3> building;                        // x, z, radius
        private static List<Vector3> found = new List<Vector3>();
        private static int sweepRev;
        private static (int rev, int explored, DateTime at, string json) cache;
        private static readonly object cacheLock = new object();

        // How far a Demister on this object (or in its children) keeps the mist away: its force
        // field's end range, read by name as the server build doesn't reference Unity's particle
        // module. 0 when it has none, and for creatures.
        public static float RangeOf(GameObject go)
        {
            // a creature with one (the Gjall) moves about: it isn't a clearing in one spot
            if (go == null || go.GetComponent<Character>() != null) return 0f;
            var d = go.GetComponentInChildren<Demister>(true);
            if (d == null) return 0f;
            float r = 6f;
            foreach (var c in d.GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != "ParticleSystemForceField") continue;
                var p = c.GetType().GetProperty("endRange");
                if (p != null) r = Convert.ToSingle(p.GetValue(c, null));
                break;
            }
            return r;
        }

        private static float Range(int prefab)
        {
            if (rangeByPrefab.TryGetValue(prefab, out float r)) return r;
            r = 0f;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
                r = RangeOf(go);
                if (r > 0f) ZLog.Log($"MuninMap: {go.name} keeps the mist {r:F1} m away");
            }
            catch { }
            rangeByPrefab[prefab] = r;
            return r;
        }

        public static void Begin() => building = new List<Vector3>(found.Count + 16);

        // Main thread, every object of the sweep: a dictionary look-up for all but the first of a kind
        public static void Observe(int prefab, Vector3 p)
        {
            if (building == null) return;
            float r = Range(prefab);
            if (r > 0f) building.Add(new Vector3(p.x, p.z, r));
        }

        public static void Finish()
        {
            if (building == null) return;
            found = building; building = null;
            sweepRev++;
        }

        // [[x, z, r], ...]: on explored ground (unless reveal_all) where there is mist to clear,
        // worked out again when the sweep or the exploration has moved on (at most once a minute)
        public static string Json()
        {
            lock (cacheLock)
            {
                int explored = Fog.ExploredCells;
                var c = cache;
                if (c.json != null && c.rev == sweepRev && (MuninConfig.REVEAL_ALL || c.explored == explored || (DateTime.UtcNow - c.at).TotalSeconds < 60))
                    return c.json;
                var j = new JsonWriter(64 + found.Count * 24);
                j.BeginArray();
                foreach (var d in found)
                {
                    if (!BiomeMap.HasMist(d.x, d.y, d.z)) continue;
                    if (!MuninConfig.REVEAL_ALL && !Fog.IsExplored(d.x, d.y)) continue;
                    j.BeginArray().Value(d.x, 1).Value(d.y, 1).Value(d.z, 1).End();
                }
                j.End();
                cache = (sweepRev, explored, DateTime.UtcNow, j.ToString());
                return cache.json;
            }
        }
    }
}
