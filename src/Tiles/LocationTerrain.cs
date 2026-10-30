using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MuninMap.Tiles
{
    // The ground the game shapes around its locations: villages, crypts, temples, camps and the rest flatten
    // a pad, smooth its edge and clear it to dirt (TerrainModifier in the location's prefab, applied by every
    // Heightmap it reaches, Heightmap.ApplyModifiers). Player terraforming (TerrainPatches) goes on top of it.
    //
    // A placed location leaves a LocationProxy in the world: which location, where, which way it faces. The
    // location's prefab gives its modifiers, each where it sits in the location; the ones under a RandomSpawn
    // (parts the game switches on or off by chance) are left out, so the always-there pads are what's shaped.
    // Worked out per zone like the game's heightmap: 65 x 65 vertices a metre apart, the modifiers in their
    // sort order, each levelling (to its height), smoothing (towards it, by its power) and painting dirt.
    internal static class LocationTerrain
    {
        private const int W = TerrainPatches.W;
        private const int VERSION = 1;   // (a new one re-renders every zone it shapes, once)
        private static readonly int proxyHash = "LocationProxy".GetStableHashCode();

        private sealed class Mod
        {
            public Vector3 local; public int sort;
            public bool level, square, smooth, paint; public float offset, levelRadius, smoothRadius, power, paintRadius, strength;
            public TerrainModifier.PaintType paintType;
        }
        private struct Placed { public int location; public Vector3 pos; public Quaternion rot; }
        public sealed class Patch { public float[] delta; public byte[] dirt; public int hash; }

        private static readonly Dictionary<int, List<Mod>> mods = new Dictionary<int, List<Mod>>();   // location hash -> its modifiers (main thread)
        private static List<Placed> building;
        private static int lastHash;
        private static readonly ConcurrentDictionary<long, Patch> patches = new ConcurrentDictionary<long, Patch>();
        public static int Count => patches.Count;

        public static bool Is(int prefab) => prefab == proxyHash;
        public static void Begin() => building = new List<Placed>(1024);

        // Main thread, during the sweep
        public static void Observe(ZDO zdo, Vector3 pos)
        {
            if (building == null) return;
            int loc = 0;
            try { loc = zdo.GetInt(ZDOVars.s_location, 0); } catch { }
            if (loc == 0) return;
            Quaternion rot = Quaternion.identity;
            try { rot = zdo.GetRotation(); } catch { }
            building.Add(new Placed { location = loc, pos = pos, rot = rot });
        }

        // Main thread, end of sweep: the patches made again when the locations changed (rarely: a new one
        // placed). Zones whose shape changed go into changedZones; on the first time with this VERSION every
        // zone shaped is re-rendered straight away (the first sweep after a start only sets the baseline).
        public static IEnumerator Finish(HashSet<long> changedZones, string worldDir)
        {
            var list = building; building = null;
            if (list == null) yield break;
            int h = list.Count;
            foreach (var p in list) h = h * 31 + p.location ^ (int)(p.pos.x * 8) ^ ((int)(p.pos.z * 8) << 7);
            if (h == lastHash && patches.Count > 0 && !list.Exists(p => !mods.ContainsKey(p.location))) yield break;
            lastHash = h;
            // each location's modifiers, from its prefab (loaded for the moment, then let go)
            var zones = new Dictionary<long, List<KeyValuePair<Mod, Placed>>>();
            // each kind of location's modifiers, its prefab loaded in the background the first time (one took
            // over a second to load at once, the server standing still)
            var kinds = new HashSet<int>();
            foreach (var p in list) if (!mods.ContainsKey(p.location)) kinds.Add(p.location);
            loadStats = new int[4];
            foreach (int loc in kinds) { var step = LoadMods(loc); while (step.MoveNext()) yield return step.Current; }   // (the sweep's stepper runs one level only)
            foreach (var p in list)
            {
                if (World.WorldSweep.Due) yield return null;
                mods.TryGetValue(p.location, out var ms);
                if (ms == null) continue;
                foreach (var m in ms)
                {
                    Vector3 at = p.pos + p.rot * m.local;
                    float r = Mathf.Max(m.level ? m.levelRadius * (m.square ? 1.42f : 1f) : 0f, m.smooth ? m.smoothRadius : 0f, m.paint ? m.paintRadius : 0f) + 1f;
                    int z0x = TileMath.ZoneCoord(at.x - r), z1x = TileMath.ZoneCoord(at.x + r), z0z = TileMath.ZoneCoord(at.z - r), z1z = TileMath.ZoneCoord(at.z + r);
                    for (int zx = z0x; zx <= z1x; zx++)
                        for (int zz = z0z; zz <= z1z; zz++)
                        {
                            long key = TileMath.ZoneKey(zx, zz);
                            if (!zones.TryGetValue(key, out var zl)) zones[key] = zl = new List<KeyValuePair<Mod, Placed>>();
                            zl.Add(new KeyValuePair<Mod, Placed>(m, p));
                        }
                }
            }
            // the zones worked out on a worker thread (the world generator's heights are safe off the game's
            // thread, as the tile renderer samples them): thousands of zones the first time, seconds of work
            var work = System.Threading.Tasks.Task.Run(() =>
            {
                var d = new Dictionary<long, Patch>();
                foreach (var kv in zones) { var patch = Shape(kv.Key, kv.Value); if (patch != null) d[kv.Key] = patch; }
                return d;
            });
            while (!work.IsCompleted) yield return null;
            if (work.IsFaulted) { ZLog.LogWarning("MuninMap: location ground: " + work.Exception?.GetBaseException().Message); yield break; }
            var next = work.Result;
            string marker = Path.Combine(worldDir ?? "", "location_terrain.txt");
            bool fresh = false;
            try { fresh = !File.Exists(marker) || File.ReadAllText(marker).Trim() != VERSION.ToString(); } catch { }
            foreach (var kv in next)
                if (!patches.TryGetValue(kv.Key, out var old) || old.hash != kv.Value.hash)
                {
                    changedZones.Add(kv.Key);
                    if (fresh) { TileMath.UnzoneKey(kv.Key, out int zx, out int zz); TileStore.OnZoneChanged(zx, zz); }
                }
            foreach (var key in new List<long>(patches.Keys))
                if (!next.ContainsKey(key)) { patches.TryRemove(key, out _); changedZones.Add(key); }
            foreach (var kv in next) patches[kv.Key] = kv.Value;
            if (fresh) try { File.WriteAllText(marker, VERSION.ToString()); } catch { }
            ZLog.Log($"MuninMap: location ground: {list.Count} locations, {next.Count} zones shaped; kinds {kinds.Count}: loaded {loadStats[0]}, failed {loadStats[1]}, still loading {loadStats[2]}, with ground {loadStats[3]}");
        }

        private static int[] loadStats = new int[4];
        private static IEnumerator LoadMods(int location)
        {
            mods[location] = null;
            ZoneSystem.ZoneLocation zl = null;
            try { zl = ZoneSystem.instance?.GetLocation(location); } catch { }
            if (zl?.m_prefab == null) yield break;
            bool done = false, ok = false;
            try { zl.m_prefab.LoadAsync((id, result) => { done = true; ok = (int)result == 0; }); }
            catch (Exception e) { ZLog.LogWarning("MuninMap: location ground: " + e.Message); yield break; }
            float waited = 0f;
            while (!done && waited < 30f) { waited += Time.unscaledDeltaTime; yield return null; }
            if (!done) { loadStats[2]++; mods.Remove(location); yield break; }
            loadStats[ok ? 0 : 1]++;   // (still loading: let it be; asked again at the next sweep)
            List<Mod> l = null;
            try
            {
                var root = ok ? zl.m_prefab.Asset : null;
                if (root != null)
                    foreach (var tm in root.GetComponentsInChildren<TerrainModifier>(false))
                    {
                        if (tm == null || !tm.enabled || tm.m_playerModifiction) continue;
                        if (tm.GetComponentInParent<RandomSpawn>(true) != null) continue;   // (there or not by chance)
                        if (!tm.m_level && !tm.m_smooth && !tm.m_paintCleared) continue;
                        (l ??= new List<Mod>()).Add(new Mod
                        {
                            local = root.transform.InverseTransformPoint(tm.transform.position), sort = tm.m_sortOrder,
                            level = tm.m_level, square = tm.m_square, smooth = tm.m_smooth, paint = tm.m_paintCleared,
                            offset = tm.m_levelOffset, levelRadius = tm.m_levelRadius, smoothRadius = tm.m_smoothRadius, power = tm.m_smoothPower,
                            paintRadius = tm.m_paintRadius, strength = tm.m_paintStrength, paintType = tm.m_paintType,
                        });
                    }
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: location ground: " + e.Message); }
            finally { try { zl.m_prefab.Release(); } catch { } }
            l?.Sort((a, b) => a.sort.CompareTo(b.sort));
            if (l != null) loadStats[3]++;
            mods[location] = l;
        }

        // One zone's heights as the game shapes them, less the plain ground: the delta, and the dirt painted
        private static Patch Shape(long key, List<KeyValuePair<Mod, Placed>> list)
        {
            var wg = WorldGenerator.instance;
            if (wg == null) return null;
            TileMath.UnzoneKey(key, out int zx, out int zz);
            float ox = zx * TileMath.ZONE_SIZE - 32f, oz = zz * TileMath.ZONE_SIZE - 32f;
            var bases = new float[W * W];
            for (int j = 0; j < W; j++)
                for (int i = 0; i < W; i++)
                {
                    float x = ox + i, z = oz + j;
                    bases[j * W + i] = wg.GetBiomeHeight(wg.GetBiome(x, z), x, z, out _);
                }
            var hs = (float[])bases.Clone();
            byte[] dirt = null;
            list.Sort((a, b) => a.Key.sort.CompareTo(b.Key.sort));
            foreach (var kv in list)
            {
                var m = kv.Key; var p = kv.Value;
                Vector3 at = p.pos + p.rot * m.local;
                float y = at.y + m.offset;
                int cx = Mathf.RoundToInt(at.x - ox), cz = Mathf.RoundToInt(at.z - oz);
                if (m.level)
                {
                    int r = Mathf.CeilToInt(m.levelRadius);
                    for (int j = cz - r; j <= cz + r; j++)
                        for (int i = cx - r; i <= cx + r; i++)
                        {
                            if (i < 0 || j < 0 || i >= W || j >= W) continue;
                            if (!m.square && Vector2.Distance(new Vector2(cx, cz), new Vector2(i, j)) > m.levelRadius) continue;
                            hs[j * W + i] = y;
                        }
                }
                if (m.smooth && m.smoothRadius > 0f)
                {
                    int r = Mathf.CeilToInt(m.smoothRadius);
                    for (int j = cz - r; j <= cz + r; j++)
                        for (int i = cx - r; i <= cx + r; i++)
                        {
                            if (i < 0 || j < 0 || i >= W || j >= W) continue;
                            float d = Vector2.Distance(new Vector2(cx, cz), new Vector2(i, j));
                            if (d > m.smoothRadius) continue;
                            float t = Mathf.Pow(d / m.smoothRadius, m.power);
                            hs[j * W + i] = Mathf.Lerp(hs[j * W + i], y, 1f - t);
                        }
                }
                if (m.paint && m.paintType == TerrainModifier.PaintType.Dirt && m.paintRadius > 0f)
                {
                    int r = Mathf.CeilToInt(m.paintRadius);
                    for (int j = cz - r; j <= cz + r; j++)
                        for (int i = cx - r; i <= cx + r; i++)
                        {
                            if (i < 0 || j < 0 || i >= W || j >= W) continue;
                            float a = Mathf.Pow(1f - Mathf.Clamp01(Vector2.Distance(new Vector2(cx, cz), new Vector2(i, j)) / m.paintRadius), 0.1f) * m.strength;
                            if (a <= 0f) continue;
                            dirt ??= new byte[W * W];
                            dirt[j * W + i] = (byte)Mathf.Max(dirt[j * W + i], Mathf.Clamp01(a) * 255f);
                        }
                }
            }
            var delta = new float[W * W];
            bool any = false;
            int hash = 17;
            for (int n = 0; n < W * W; n++)
            {
                delta[n] = hs[n] - bases[n];
                if (Math.Abs(delta[n]) > 0.01f) any = true;
                hash = hash * 31 + (int)(delta[n] * 16f) + (dirt != null ? dirt[n] : 0);
            }
            if (!any && dirt == null) return null;
            return new Patch { delta = any ? delta : null, dirt = dirt, hash = hash };
        }

        public static ICollection<long> Zones() => patches.Keys;

        // Renderer threads
        public static bool Has(int zx, int zz) => patches.ContainsKey(TileMath.ZoneKey(zx, zz));
        public static float DeltaAt(float wx, float wz)
        {
            int zx = TileMath.ZoneCoord(wx), zz = TileMath.ZoneCoord(wz);
            if (!patches.TryGetValue(TileMath.ZoneKey(zx, zz), out var p) || p.delta == null) return 0f;
            return TerrainPatches.BilinearAt(p.delta, wx - (zx * TileMath.ZONE_SIZE - 32), wz - (zz * TileMath.ZONE_SIZE - 32));
        }

        public static float DirtAt(float wx, float wz)
        {
            int zx = TileMath.ZoneCoord(wx), zz = TileMath.ZoneCoord(wz);
            if (!patches.TryGetValue(TileMath.ZoneKey(zx, zz), out var p) || p.dirt == null) return 0f;
            int i = Mathf.Clamp(Mathf.RoundToInt(wx - (zx * TileMath.ZONE_SIZE - 32)), 0, W - 1), j = Mathf.Clamp(Mathf.RoundToInt(wz - (zz * TileMath.ZONE_SIZE - 32)), 0, W - 1);
            return p.dirt[j * W + i] / 255f;
        }
    }
}
