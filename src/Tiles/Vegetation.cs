using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using MuninMap.Util;
using MuninMap.World;

namespace MuninMap.Tiles
{
    // Trees, bushes and rocks, per zone.
    //
    // The world sweep hands every ZDO without a creator to Observe; the ones
    // that are vegetation are classified by prefab name (cached per prefab
    // hash, so the name lookup happens once per kind, not once per tree) and
    // collected per zone. When a sweep finishes the fresh zone lists replace
    // the published ones, and zones whose contents changed are reported so
    // the tiles over them can be re-rendered: a felled wood shows up as a
    // clearing on the next render.
    //
    // The renderer bakes these into the map tiles as shaded canopies, and the
    // 3D view fetches them per chunk as a compact binary (see Chunk).
    internal static class Vegetation
    {
        public struct Point
        {
            public float x, y, z;
            public Palette.Veg kind;
            public float size;                // 1.0 = the palette's default radius / height
        }

        internal struct Class { public Palette.Veg kind; public float size; }

        private static readonly Dictionary<int, Class> classCache = new Dictionary<int, Class>();
        // every object name the sweeps met, how many of each and what it was taken for, written to
        // vegetation-kinds.txt beside the cache: the way to spot a tree or bush the classifier misses
        private static readonly Dictionary<int, string> className = new Dictionary<int, string>();
        private static readonly Dictionary<int, int> classSeen = new Dictionary<int, int>();
        private static int classDumped = -1;

        // published (renderer + HTTP threads read; swapped atomically per zone)
        private static readonly ConcurrentDictionary<long, Point[]> zones = new ConcurrentDictionary<long, Point[]>();
        private static readonly ConcurrentDictionary<long, int> zoneHash = new ConcurrentDictionary<long, int>();

        // being built (main thread, during a sweep)
        private static Dictionary<long, List<Point>> building;

        public static int ZoneCount => zones.Count;
        public static int LastTrees { get; private set; }
        public static int LastRocks { get; private set; }

        public static Point[] Zone(int zx, int zz)
        {
            zones.TryGetValue(TileMath.ZoneKey(zx, zz), out var pts);
            return pts;
        }

        // the sweep's lists, kept from one sweep to the next and only emptied, so a sweep over a
        // million trees and rocks doesn't leave a million points' worth of garbage behind
        private static readonly Dictionary<long, List<Point>> pool = new Dictionary<long, List<Point>>();

        public static void Begin()
        {
            foreach (var l in pool.Values) l.Clear();
            building = pool;
        }

        // Main thread. Returns true when the prefab is vegetation (so callers can stop classifying it).
        public static bool Observe(int prefabHash, Vector3 pos)
        {
            var c = Classify(prefabHash);
            if (building != null) { classSeen.TryGetValue(prefabHash, out int seenN); classSeen[prefabHash] = seenN + 1; }
            if (c.kind == Palette.Veg.None) return false;
            if (building == null) return true;
            long key = TileMath.ZoneKey(TileMath.ZoneCoord(pos.x), TileMath.ZoneCoord(pos.z));
            if (!building.TryGetValue(key, out var list)) building[key] = list = new List<Point>(64);
            list.Add(new Point { x = pos.x, y = pos.y, z = pos.z, kind = c.kind, size = c.size });
            return true;
        }

        // Main thread, end of sweep, a slice per frame (WorldSweep.Due). Publishes, and adds the zones
        // whose vegetation changed to `changedZones`.
        public static IEnumerator Finish(HashSet<long> changedZones)
        {
            if (building == null) yield break;
            // the zones' fingerprints and sorting on a worker thread: about a second of the game's thread
            // a sweep on a big world. It only touches Munin's own lists, which the sweep is done with
            // (they're cleared at the next sweep's start, after this), and the concurrent tables the web
            // threads already read
            var work = building;
            var crunch = System.Threading.Tasks.Task.Run(() => Crunch(work));
            while (!crunch.IsCompleted) yield return null;
            if (crunch.IsFaulted) { building = null; ZLog.LogWarning("MuninMap: trees: " + crunch.Exception?.GetBaseException().Message); yield break; }
            var changed = crunch.Result.changed;
            var seen = crunch.Result.seen;
            int trees = crunch.Result.trees, rocks = crunch.Result.rocks;
            // zones that emptied out entirely: only when there are any (every zone seen is in zones, so
            // none emptied when the counts match); listing all the zones' keys and checking each took
            // most of a frame on a big world, every sweep
            if (zones.Count != seen.Count)
            {
                int k = 0;
                foreach (var key in new List<long>(zones.Keys))
                {
                    if ((++k & 1023) == 0 && WorldSweep.Due) yield return null;
                    if (seen.Contains(key)) continue;
                    zones.TryRemove(key, out _);
                    zoneHash.TryRemove(key, out _);
                    changed.Add(key);
                }
            }
            LastTrees = trees; LastRocks = rocks;
            building = null;
            DumpKinds();
            if (changed.Count > 0) { version++; SaveCache(); }
            foreach (long z in changed) changedZones.Add(z);
        }

        private sealed class Crunched { public readonly List<long> changed = new List<long>(); public readonly HashSet<long> seen = new HashSet<long>(); public int trees, rocks; }

        // any thread: each zone's fingerprint; changed zones sorted and published
        private static Crunched Crunch(Dictionary<long, List<Point>> work)
        {
            var r = new Crunched();
            var changed = r.changed; var seen = r.seen;
            int trees = 0, rocks = 0;
            foreach (var kv in work)
            {
                var list = kv.Value;
                if (list.Count == 0) continue;   // nothing there this sweep
                long sum = 0;
                foreach (var p in list)
                {
                    sum += WorldSweep.Mix(unchecked((int)(p.x * 4) * 7 + (int)(p.z * 4) * 13 + (int)p.kind * 101 + (int)(p.size * 8)));
                    if (Palette.Base(p.kind) == Palette.Veg.Rock || Palette.Base(p.kind) == Palette.Veg.Ore) rocks++;
                    else if (!Palette.IsLowPlant(p.kind)) trees++;
                }
                int h = WorldSweep.Fold(sum, list.Count);
                seen.Add(kv.Key);
                if (zoneHash.TryGetValue(kv.Key, out int old) && old == h && zones.ContainsKey(kv.Key)) continue;
                changed.Add(kv.Key);
                list.Sort((a, b) => a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x));   // nicer draw order
                zones[kv.Key] = list.ToArray();
                zoneHash[kv.Key] = h;
            }
            r.trees = trees; r.rocks = rocks;
            return r;
        }

        // ---- cache on disk: every zone's points, served at once after a restart until the first
        // sweep (the page's trees, the tree overlay tiles). Binary: int zone count, then per zone
        // long key, int hash, int n, n x (float x, y, z, byte kind, float size).
        private static string cachePath;
        public static void LoadCache(string worldDataPath)
        {
            cachePath = Path.Combine(worldDataPath, "vegetation-cache.bin");
            try
            {
                if (!File.Exists(cachePath)) return;
                using (var br = new BinaryReader(File.OpenRead(cachePath)))
                {
                    int zc = br.ReadInt32();
                    for (int i = 0; i < zc; i++)
                    {
                        long key = br.ReadInt64(); int hash = br.ReadInt32(), n = br.ReadInt32();
                        var pts = new Point[n];
                        for (int k = 0; k < n; k++)
                            pts[k] = new Point { x = br.ReadSingle(), y = br.ReadSingle(), z = br.ReadSingle(), kind = (Palette.Veg)br.ReadByte(), size = br.ReadSingle() };
                        zones[key] = pts; zoneHash[key] = hash;
                    }
                }
                version++;
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: vegetation cache: " + e.Message); zones.Clear(); zoneHash.Clear(); }
        }

        private static void DumpKinds()
        {
            if (cachePath == null || classSeen.Count == classDumped) return;
            classDumped = classSeen.Count;
            try
            {
                var rows = new List<KeyValuePair<int, int>>(classSeen);
                rows.Sort((a, b) => b.Value.CompareTo(a.Value));
                var sb = new System.Text.StringBuilder();
                foreach (var kv in rows)
                {
                    className.TryGetValue(kv.Key, out string n);
                    sb.Append(kv.Value).Append('\t').Append(classCache[kv.Key].kind).Append('\t').Append(n ?? "?").Append('\n');
                }
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(cachePath), "vegetation-kinds.txt"), sb.ToString());
                classSeen.Clear();
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: vegetation kinds: " + e.Message); }
        }

        private static void SaveCache()
        {
            if (cachePath == null) return;
            string path = cachePath;
            new System.Threading.Thread(() =>
            {
                try
                {
                    // copied here, off the game's thread (a big world's copy took a good part of a frame):
                    // both are concurrent dictionaries, and a zone's points are replaced, never changed
                    var snap = new List<KeyValuePair<long, Point[]>>(zones);
                    var hashes = new Dictionary<long, int>(zoneHash);
                    using (var bw = new BinaryWriter(File.Create(path + ".tmp")))
                    {
                        bw.Write(snap.Count);
                        foreach (var kv in snap)
                        {
                            bw.Write(kv.Key); bw.Write(hashes.TryGetValue(kv.Key, out int h) ? h : 0); bw.Write(kv.Value.Length);
                            foreach (var p in kv.Value) { bw.Write(p.x); bw.Write(p.y); bw.Write(p.z); bw.Write((byte)p.kind); bw.Write(p.size); }
                        }
                    }
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(path + ".tmp", path);
                }
                catch (Exception e) { ZLog.LogWarning("MuninMap: vegetation cache: " + e.Message); }
            }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
        }

        // ---------------------------------------------------------------- regions for the page
        // The 2D page draws every tree and rock itself (on the GPU), from data fetched 4x4 chunks
        // (1 km) to a request and cached for good: a region's rev hashes its zones' contents, so
        // only regions where trees were felled or grew are fetched again. Explored chunks only.
        public const int REGION = 4;
        private static volatile int version;
        private static string indexJson; private static long indexKey = -1;

        // a chunk's rev from its zones' content hashes; null when unexplored or empty
        private static int? ListedRev(int cx, int cz)
        {
            float minX = TileMath.ChunkMin(cx), minZ = TileMath.ChunkMin(cz);
            if (!MuninConfig.REVEAL_ALL && !Fog.AnyExplored(minX, minZ, minX + TileMath.CHUNK_SIZE, minZ + TileMath.CHUNK_SIZE)) return null;
            int zx0 = TileMath.ZoneCoord(minX), zx1 = TileMath.ZoneCoord(minX + TileMath.CHUNK_SIZE - 0.01f);
            int zz0 = TileMath.ZoneCoord(minZ), zz1 = TileMath.ZoneCoord(minZ + TileMath.CHUNK_SIZE - 0.01f);
            int h = 17; bool any = false;
            for (int zz = zz0; zz <= zz1; zz++)
                for (int zx = zx0; zx <= zx1; zx++)
                    if (zoneHash.TryGetValue(TileMath.ZoneKey(zx, zz), out int zh)) { h = unchecked(h * 31 + zh); any = true; }
            return any ? (int?)(h & 0x7fffffff) : null;
        }

        // {"rev":..,"regionSize":4,"regions":[[rx,rz,rev],...]}, rebuilt when vegetation or the fog changed
        public static string IndexJson()
        {
            long key = ((long)version << 32) | (uint)Fog.ExploredCells;
            var cached = indexJson;
            if (cached != null && key == indexKey) return cached;
            var j = new JsonWriter(4096);
            j.BeginObject().Prop("rev", unchecked(version * 1000003 + Fog.ExploredCells) & 0x7fffffff);
            Regions.WriteIndex(j, ListedRev, REGION);
            j.End();
            indexJson = j.ToString(); indexKey = key;
            return indexJson;
        }

        // Trees and rocks under the sea. Over half the rocks the world has sit on the seabed, out of
        // sight in game; the map hides those and draws the ones in shallow water faded (a rock
        // sticking out of the surf). Depth is from the object's base, so big rocks and cliffs, which
        // stand tall, may sit deeper. Swamp trees stand in water by nature and are left alone.
        public const int Dry = 0, Wet = 1, Sunk = 2;
        public static int WaterState(Point p)
        {
            float depth = TileJob.WaterLevel - p.y;
            if (depth <= 1f || p.kind == Palette.Veg.SwampTree) return Dry;
            float limit = Palette.Base(p.kind) == Palette.Veg.Rock ? (p.size >= 2f ? 10f : p.size >= 1.5f ? 6f : 3f) : 3f;
            return depth > limit ? Sunk : Wet;
        }

        // 'VGR3' (VGR2 plus the water flag), uint32 chunk count, then per chunk: uint8 cx, uint8 cz, uint32 point count, and per
        // point 6 bytes: int16 x*4, int16 z*4 (quarter metres from the chunk's corner), uint8 kind,
        // uint8 size*32. The kind has 0x80 set for one in shallow water (WaterState); sunk ones are
        // left out. The 2D map has no use for height, so it is left out (VEG1 has it, for 3D).
        // Null when the region has nothing listed.
        public static byte[] RegionBin(int rx, int rz, out int rev)
        {
            rev = Regions.Hash(rx, rz, ListedRev, REGION);
            if (rev == 0) return null;
            using (var ms = new MemoryStream(64 * 1024))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte)'V'); bw.Write((byte)'G'); bw.Write((byte)'R'); bw.Write((byte)'3');
                bw.Write(0);
                int n = 0;
                for (int cz = rz * REGION; cz < Math.Min(rz * REGION + REGION, TileMath.ChunksPerSide); cz++)
                    for (int cx = rx * REGION; cx < Math.Min(rx * REGION + REGION, TileMath.ChunksPerSide); cx++)
                    {
                        if (ListedRev(cx, cz) == null) continue;
                        var pts = ChunkPoints(cx, cz).FindAll((p) => WaterState(p) != Sunk);
                        bw.Write((byte)cx); bw.Write((byte)cz); bw.Write(pts.Count);
                        float minX = TileMath.ChunkMin(cx), minZ = TileMath.ChunkMin(cz);
                        foreach (var p in pts)
                        {
                            bw.Write((short)Math.Round((p.x - minX) * 4));
                            bw.Write((short)Math.Round((p.z - minZ) * 4));
                            bw.Write((byte)((byte)p.kind | (WaterState(p) == Wet ? 0x80 : 0)));
                            bw.Write((byte)Math.Max(1, Math.Min(255, Math.Round(p.size * 32))));
                        }
                        n++;
                    }
                bw.Flush();
                byte[] b = ms.ToArray();
                BitConverter.GetBytes(n).CopyTo(b, 4);
                return b;
            }
        }

        private static Class Classify(int prefabHash)
        {
            if (classCache.TryGetValue(prefabHash, out var cached)) return cached;
            string n = null;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
                if (go != null) n = go.name.ToLowerInvariant();
            }
            catch { }
            var c = ClassifyName(n);
            classCache[prefabHash] = c;
            className[prefabHash] = n;
            return c;
        }

        // Public for tests and for the structure classifier's "is this a plant" check.
        internal static Class ClassifyName(string n)
        {
            var c = new Class { kind = Palette.Veg.None, size = 1f };
            if (string.IsNullOrEmpty(n)) return c;
            bool small = n.Contains("small") || n.Contains("_sapling") || n.Contains("sapling");
            if (n.Contains("sapling")) return c;                              // player-planted saplings are pieces, and tiny
            // plants you can pick (and small growth) get their own kinds and colours
            switch (n)
            {
                case "pickable_mushroom": c.kind = Palette.Veg.Mushroom; return c;
                case "pickable_mushroom_yellow": c.kind = Palette.Veg.MushroomYellow; return c;
                case "pickable_mushroom_magecap": c.kind = Palette.Veg.Magecap; return c;
                case "pickable_mushroom_jotunpuffs": c.kind = Palette.Veg.JotunPuffs; return c;
                case "pickable_smokepuff": c.kind = Palette.Veg.SmokePuff; return c;
                case "pickable_thistle": c.kind = Palette.Veg.Thistle; return c;
                case "pickable_dandelion": c.kind = Palette.Veg.Dandelion; return c;
                case "pickable_fiddlehead": c.kind = Palette.Veg.Fiddlehead; return c;
                case "pickable_barley_wild": c.kind = Palette.Veg.BarleyWild; return c;
                case "pickable_flax_wild": c.kind = Palette.Veg.FlaxWild; return c;
                case "lingonberrybush": c.kind = Palette.Veg.Lingonberry; return c;
                case "vineash": c.kind = Palette.Veg.AshVine; return c;
                case "fernashlands": c.kind = Palette.Veg.AshFern; return c;
            }
            // dropped items lying on the ground (seeds, cones, picked berries): not plants
            if (n.Contains("seeds") || n.EndsWith("cone", StringComparison.Ordinal) || n == "raspberry" || n == "blueberries" || n == "cloudberry") return c;
            if (n.Contains("stub")) { c.kind = Palette.Veg.Stump; return c; }   // beech_stub, birchstub, oakstub, stubbe...
            if (n.Contains("_log") || n.Contains("oldlog") || n.EndsWith("logs", StringComparison.Ordinal) || n.Contains("_trunk")) return c;   // felled wood on the ground: not a canopy

            if (n.Contains("_dead") || n.Contains("deadtree") || n.Contains("dead_tree")) { c.kind = Palette.Veg.DeadTree; c.size = small ? 0.5f : 1f; return c; }
            if (n.StartsWith("beech", StringComparison.Ordinal)) { c.kind = Palette.Veg.Deciduous; c.size = small ? 0.45f : 1f; return c; }
            if (n.StartsWith("oak", StringComparison.Ordinal)) { c.kind = Palette.Veg.Oak; c.size = 1.7f; return c; }
            if (n.StartsWith("birch", StringComparison.Ordinal)) { c.kind = n.Contains("_aut") ? Palette.Veg.BirchAutumn : Palette.Veg.Birch; c.size = 0.8f; return c; }
            if (n.StartsWith("firtree", StringComparison.Ordinal)) { c.kind = Palette.Veg.Conifer; c.size = small ? 0.5f : 1f; return c; }
            if (n.StartsWith("pinetree", StringComparison.Ordinal) || n.StartsWith("pine", StringComparison.Ordinal)) { c.kind = Palette.Veg.Pine; c.size = 1.25f; return c; }
            if (n.StartsWith("swamptree", StringComparison.Ordinal)) { c.kind = Palette.Veg.SwampTree; c.size = 1f; return c; }
            if (n.StartsWith("yggashoot", StringComparison.Ordinal)) { c.kind = Palette.Veg.MistTree; c.size = small ? 0.5f : 1f; return c; }
            if (n.Contains("ashlandstree") || n.Contains("ashtree") || n.Contains("charredtree")) { c.kind = Palette.Veg.AshTree; return c; }
            if (n.Contains("deadtree") || n.Contains("dead_tree")) { c.kind = Palette.Veg.DeadTree; return c; }
            if (n.Contains("raspberry")) { c.kind = Palette.Veg.Raspberry; return c; }
            if (n.Contains("blueberry")) { c.kind = Palette.Veg.Blueberry; return c; }
            if (n.Contains("cloudberry")) { c.kind = Palette.Veg.Cloudberry; return c; }
            if (n.StartsWith("bush", StringComparison.Ordinal) || n.Contains("shrub")) { c.kind = Palette.Veg.Bush; return c; }
            if (n.Contains("silvervein") || n.Contains("mudpile") || n.Contains("_copper") || n.Contains("minerock") || n.Contains("_tin") || n.Contains("meteorite"))
            {
                c.kind = n.Contains("silvervein") ? Palette.Veg.Silver : n.Contains("mudpile") ? Palette.Veg.MuddyScrap : n.Contains("_copper") ? Palette.Veg.Copper
                       : n.Contains("_tin") ? Palette.Veg.Tin : n.Contains("obsidian") ? Palette.Veg.Obsidian : Palette.Veg.Ore;
                c.size = n.Contains("_tin") || n.Contains("mudpile") ? 0.4f : 1.2f;
                return c;
            }
            if (n.StartsWith("cliff", StringComparison.Ordinal)) { c.kind = Palette.Veg.Cliff; c.size = 2.2f; return c; }
            if (n.StartsWith("giant_", StringComparison.Ordinal)) { c.kind = Palette.Veg.GiantBones; c.size = 2.2f; return c; }
            if (n == "highstone" || n == "widestone" || n.StartsWith("heathrockpillar", StringComparison.Ordinal)) { c.kind = Palette.Veg.Rock; c.size = n.EndsWith("_frac", StringComparison.Ordinal) ? 0.35f : 1.6f; return c; }
            if (n.StartsWith("rock", StringComparison.Ordinal) || n.StartsWith("highrock", StringComparison.Ordinal) || n.StartsWith("rock_", StringComparison.Ordinal))
            {
                c.kind = Palette.Veg.Rock;
                // rock4 / rock_4 are the big walkable boulders; rock1..3 the small ones; "_destructible" chunks are tiny
                c.size = n.Contains("rock4") || n.Contains("rock_4") || n.Contains("highrock") ? 1.6f
                       : n.Contains("frac") || n.Contains("destructible") || n.Contains("_small") ? 0.35f : 0.8f;
                return c;
            }
            if (n.Contains("tree")) { c.kind = Palette.Veg.Conifer; c.size = small ? 0.5f : 1f; return c; }   // something new: draw it as a tree
            return c;
        }

        // the points inside one 256 m chunk
        private static List<Point> ChunkPoints(int cx, int cz)
        {
            float minX = TileMath.ChunkMin(cx), minZ = TileMath.ChunkMin(cz);
            float maxX = minX + TileMath.CHUNK_SIZE, maxZ = minZ + TileMath.CHUNK_SIZE;
            int zx0 = TileMath.ZoneCoord(minX), zx1 = TileMath.ZoneCoord(maxX - 0.01f);
            int zz0 = TileMath.ZoneCoord(minZ), zz1 = TileMath.ZoneCoord(maxZ - 0.01f);
            var pts = new List<Point>();
            for (int zz = zz0; zz <= zz1; zz++)
                for (int zx = zx0; zx <= zx1; zx++)
                {
                    var arr = Zone(zx, zz);
                    if (arr == null) continue;
                    foreach (var p in arr)
                        if (p.x >= minX && p.x < maxX && p.z >= minZ && p.z < maxZ) pts.Add(p);
                }
            return pts;
        }

        // Binary chunk for the 3D view and the vegetation vector layer.
        // Little-endian: uint32 magic 'VEG1', uint32 count, then per point:
        //   int16 x*4 (relative to chunk min x, quarter metres), int16 z*4, int16 y*4 (absolute), uint8 kind, uint8 size*32
        public static byte[] Chunk(int cx, int cz)
        {
            float minX = TileMath.ChunkMin(cx), minZ = TileMath.ChunkMin(cz);
            var pts = ChunkPoints(cx, cz);
            using (var ms = new MemoryStream(8 + pts.Count * 8))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte)'V'); bw.Write((byte)'E'); bw.Write((byte)'G'); bw.Write((byte)'1');
                bw.Write(pts.Count);
                foreach (var p in pts)
                {
                    bw.Write((short)Math.Round((p.x - minX) * 4));
                    bw.Write((short)Math.Round((p.z - minZ) * 4));
                    bw.Write((short)Math.Max(-32768, Math.Min(32767, Math.Round(p.y * 4))));
                    bw.Write((byte)p.kind);
                    bw.Write((byte)Math.Max(1, Math.Min(255, Math.Round(p.size * 32))));
                }
                bw.Flush();
                return ms.ToArray();
            }
        }
    }
}
