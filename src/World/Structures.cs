using System;
using System.IO;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using UnityEngine;
using MuninMap.Tiles;
using MuninMap.Util;

namespace MuninMap.World
{
    // Player-built pieces as vector data.
    //
    // Every placed piece becomes a small record -- position, yaw, footprint,
    // height, material, prefab -- grouped into 256 m chunks. The browser
    // draws the chunks it can see: as material-coloured footprints on the
    // 2D map, and as extruded boxes in 3D. Nothing is rasterised here, so a
    // base can be drawn at any zoom and every piece can say what it is when
    // hovered.
    //
    // Footprints come from the prefab name, which in Valheim is descriptive
    // enough ("stone_wall_4x2", "wood_floor_1x1", "blackmarble_arch"): the
    // classifier parses the size token when there is one and falls back to
    // sensible defaults per piece family. It is a map, not a physics engine.
    internal static class Structures
    {
        internal struct Piece
        {
            public float x, y, z;         // world position
            public short yaw;             // degrees
            public float sx, sz, h;       // footprint (metres) and height
            public Palette.Material mat;
            public int prefab;            // prefab hash
            public long creator;
            public byte site;             // world structures: what kind of place it stands in (Ruins.Sites)
        }

        internal sealed class Shape
        {
            public float sx, sz, h; public Palette.Material mat; public string name; public bool skip;
        }

        private static readonly Dictionary<int, Shape> shapeCache = new Dictionary<int, Shape>();

        // published per chunk
        private sealed class Chunk { public int rev, count; public string json; public Piece[] pieces; }   // pieces: null when loaded from the cache
        private static readonly ConcurrentDictionary<int, Chunk> chunks = new ConcurrentDictionary<int, Chunk>();
        private static readonly ConcurrentDictionary<int, int> chunkHash = new ConcurrentDictionary<int, int>();

        // being built
        private static Dictionary<int, List<Piece>> building;
        private static Dictionary<int, int> byPrefab;
        private static Dictionary<int, int> byMaterial;
        private static Dictionary<long, int> byCreator;
        private static int total;

        private static volatile string statsJson = "{\"total\":0,\"materials\":[],\"prefabs\":[],\"chunks\":[]}";
        private static volatile string indexJson = "{\"rev\":0,\"chunks\":[]}";
        private static int indexRev, indexExplored = -1;

        public static int Total { get; private set; }
        // building pieces per creator (a player's id) from the last sweep: the stats' "pieces built"
        public static Dictionary<long, int> Builders { get; private set; } = new Dictionary<long, int>();
        // the crafting stations each player has standing (by creator), a sign of how far they've come
        public static Dictionary<long, HashSet<string>> Stations { get; private set; } = new Dictionary<long, HashSet<string>>();
        private static Dictionary<long, HashSet<string>> byCreatorStations;
        private static readonly HashSet<string> stationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "piece_workbench", "forge", "piece_stonecutter", "piece_artisanstation", "blackforge", "piece_magetable",
            "piece_cauldron", "piece_MeadCauldron", "piece_preptable", "piece_oven", "smelter", "blastfurnace", "charcoal_kiln",
            "eitrrefinery", "piece_spinningwheel", "windmill", "fermenter", "piece_beehive", "piece_FrostKiln" };

        // kept from one sweep to the next and only emptied: less garbage, fewer collection pauses
        private static readonly Dictionary<int, List<Piece>> pool = new Dictionary<int, List<Piece>>();

        public static void Begin()
        {
            foreach (var l in pool.Values) l.Clear();
            building = pool;
            byPrefab = new Dictionary<int, int>();
            byMaterial = new Dictionary<int, int>();
            byCreator = new Dictionary<long, int>();
            byCreatorStations = new Dictionary<long, HashSet<string>>();
            total = 0;
        }

        private static int ChunkKey(int cx, int cz) => cx * 4096 + cz;

        // Main thread. Called for every ZDO that has a creator and is not a vehicle.
        public static void Observe(ZDO zdo, int prefabHash, Vector3 pos, long creator)
        {
            var shape = ShapeOf(prefabHash);
            if (shape.skip || building == null) return;
            short yaw = 0;
            try { yaw = (short)Math.Round(zdo.GetRotation().eulerAngles.y); } catch { }
            int cx = TileMath.ChunkCoord(pos.x), cz = TileMath.ChunkCoord(pos.z);
            if (cx < 0 || cz < 0 || cx >= TileMath.ChunksPerSide || cz >= TileMath.ChunksPerSide) return;
            int key = ChunkKey(cx, cz);
            if (!building.TryGetValue(key, out var list)) building[key] = list = new List<Piece>(128);
            list.Add(new Piece { x = pos.x, y = pos.y, z = pos.z, yaw = yaw, sx = shape.sx, sz = shape.sz, h = shape.h, mat = shape.mat, prefab = prefabHash, creator = creator });
            total++;
            byPrefab.TryGetValue(prefabHash, out int n); byPrefab[prefabHash] = n + 1;
            byMaterial.TryGetValue((int)shape.mat, out int m); byMaterial[(int)shape.mat] = m + 1;
            byCreator.TryGetValue(creator, out int c); byCreator[creator] = c + 1;
            if (stationNames.Contains(shape.name))
            {
                if (!byCreatorStations.TryGetValue(creator, out var set)) byCreatorStations[creator] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(shape.name);
            }
        }

        public static int LastChanged { get; private set; }   // chunks the last Finish changed

        // Main thread, end of sweep, a slice per frame (WorldSweep.Due)
        public static IEnumerator Finish()
        {
            LastChanged = 0;
            if (building == null) yield break;
            int changed = 0;
            var seen = new HashSet<int>();
            foreach (var kv in building)
            {
                if (WorldSweep.Due) yield return null;   // the rest next frame
                var list = kv.Value;
                if (list.Count == 0) continue;   // nothing there this sweep
                long sum = 0;
                foreach (var p in list)
                    sum += WorldSweep.Mix(unchecked((int)(p.x * 10) * 7 + (int)(p.z * 10) * 13 + (int)(p.y * 10) * 3 + p.yaw * 101 + p.prefab));
                int h = WorldSweep.Fold(sum, list.Count);
                seen.Add(kv.Key);
                if (chunkHash.TryGetValue(kv.Key, out int old) && old == h) continue;
                list.Sort((a, b) => a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x));
                chunkHash[kv.Key] = h;
                int cx = kv.Key / 4096, cz = kv.Key % 4096;
                var pieces = list.ToArray();
                // rev is a hash of the content, not a counter, so it means the same bytes across
                // restarts and browsers may cache data/.../cx_cz.json?h=rev for good
                // built once, a slice at a time: a big base's chunk (13 000 pieces) took 360 ms in one frame
                var built = new string[1];
                var json = BuildChunkJson(cx, cz, pieces, built);
                while (json.MoveNext()) yield return null;
                int rev = (int)(TileStore.Fnv1a(Encoding.UTF8.GetBytes(built[0])) & 0x7fffffff);
                chunks[kv.Key] = new Chunk { rev = rev, count = pieces.Length, pieces = pieces, json = WithRev(built[0], rev) };
                changed++;
            }
            // chunks that emptied out: only when there are any (every chunk seen is in chunks, so none
            // emptied when the counts match), a slice at a time; listing every chunk's key and checking
            // each took most of a frame on a big world, every sweep
            if (chunks.Count != seen.Count)
            {
                int k = 0;
                foreach (int key in new List<int>(chunks.Keys))
                {
                    if ((++k & 1023) == 0 && WorldSweep.Due) yield return null;
                    if (seen.Contains(key)) continue;
                    chunks.TryRemove(key, out _);
                    chunkHash.TryRemove(key, out _);
                    changed++;
                }
            }
            Total = total;
            statsJson = BuildStats();
            Builders = new Dictionary<long, int>(byCreator);
            if (byCreatorStations != null) Stations = byCreatorStations;
            // the index lists only chunks under explored ground, so it also has to follow the fog
            int explored = Fog.ExploredCells;
            if (changed > 0 || explored != indexExplored)
            {
                // a slice at a time: every chunk is checked against the explored ground, and with players
                // exploring that's nearly every sweep (11-16 ms in one frame on a big world)
                indexRev++; indexExplored = explored;
                var index = new string[1];
                foreach (var _ in IndexParts(index, true)) yield return null;
                indexJson = index[0];
            }
            if (changed > 0) { piecesRev++; SaveCache(); }
            building = null;
            LastChanged = changed;
        }

        // ---- cache on disk (ChunkCache): served at once after a restart, until the first sweep
        private static string cachePath;
        public static void LoadCache(string worldDataPath)
        {
            cachePath = Path.Combine(worldDataPath, "structures-cache.txt");
            int n = 0;
            foreach (var l in ChunkCache.Load(cachePath)) { chunks[l.key] = new Chunk { rev = l.rev, count = l.count, json = l.json }; n += l.count; }
            if (chunks.Count == 0) return;
            Total = n; indexRev++; piecesRev++; indexExplored = Fog.ExploredCells; indexJson = BuildIndex();
        }

        private static void SaveCache()
        {
            ChunkCache.Save(cachePath, () =>
            {
                var lines = new List<ChunkCache.Line>(chunks.Count);
                foreach (var kv in chunks) lines.Add(new ChunkCache.Line { key = kv.Key, rev = kv.Value.rev, count = kv.Value.count, json = kv.Value.json });
                return lines;
            });
        }

        public static string ChunkJson(int cx, int cz)
        {
            return chunks.TryGetValue(ChunkKey(cx, cz), out var c) ? c.json : null;
        }

        public static string ChunkJson(int cx, int cz, out int rev)
        {
            rev = 0;
            if (ChunkJson(cx, cz) == null || !chunks.TryGetValue(ChunkKey(cx, cz), out var c)) return null;
            rev = c.rev;   // json and rev from the same object, so a hash never labels other content
            return c.json;
        }

        // the chunk's rev when it exists and the index lists it (explored ground), else null
        private static int? ListedRev(int cx, int cz)
        {
            if (!chunks.TryGetValue(ChunkKey(cx, cz), out var c)) return null;
            float minX = TileMath.ChunkMin(cx), minZ = TileMath.ChunkMin(cz);
            if (!MuninConfig.REVEAL_ALL && !Fog.AnyExplored(minX, minZ, minX + TileMath.CHUNK_SIZE, minZ + TileMath.CHUNK_SIZE)) return null;
            return c.rev;
        }

        // 8x8 chunks in one response (see Regions)
        public static string RegionJson(int rx, int rz, out int rev) =>
            Regions.Json(rx, rz, ListedRev, (cx, cz) => chunks.TryGetValue(ChunkKey(cx, cz), out var c) ? c.json : null, out rev);

        internal static Piece[] ChunkPieces(int cx, int cz)
        {
            return chunks.TryGetValue(ChunkKey(cx, cz), out var c) ? c.pieces : null;
        }

        public static string IndexJson => indexJson;

        public struct Base { public float x, y, z; public int pieces; public long builder; }   // builder: who placed most of it

        // Player bases: player-placed pieces binned into 64 m cells, neighbouring busy cells merged,
        // clusters of at least minPieces reported at their centroid. Cheap enough to run every sweep.
        // (kept until the pieces change: most sweeps change nothing, and on a big world this is tens of
        // milliseconds of the game's frame every time the markers are remade)
        private static int piecesRev;
        private static (int rev, int perCell, int min, List<Base> list) basesCache;

        public static List<Base> ComputeBases(int minPerCell = 6, int minPieces = 30)
        {
            var bc = basesCache;
            if (bc.list != null && bc.rev == piecesRev && bc.perCell == minPerCell && bc.min == minPieces) return bc.list;
            int rev = piecesRev;
            var list = Bases(minPerCell, minPieces);
            basesCache = (rev, minPerCell, minPieces, list);
            return list;
        }

        private static List<Base> Bases(int minPerCell, int minPieces)
        {
            const float CELL = 64f;
            var cells = new Dictionary<long, (int n, double sx, double sz, double sy)>();
            var cellBuilders = new Dictionary<long, Dictionary<long, int>>();
            foreach (var c in chunks.Values)
                foreach (var p in c.pieces ?? Array.Empty<Piece>())   // none in chunks from the cache: the next sweep fills them
                {
                    if (p.creator == 0L) continue;
                    long key = ((long)(int)Math.Floor((p.x + 10240f) / CELL) << 32) | (uint)(int)Math.Floor((p.z + 10240f) / CELL);
                    cells.TryGetValue(key, out var v);
                    cells[key] = (v.n + 1, v.sx + p.x, v.sz + p.z, v.sy + p.y);
                    if (!cellBuilders.TryGetValue(key, out var cb)) cellBuilders[key] = cb = new Dictionary<long, int>(2);
                    cb.TryGetValue(p.creator, out int cn); cb[p.creator] = cn + 1;
                }
            var busy = new HashSet<long>();
            foreach (var kv in cells) if (kv.Value.n >= minPerCell) busy.Add(kv.Key);
            var result = new List<Base>();
            var seen = new HashSet<long>();
            var stack = new Stack<long>();
            foreach (long start in busy)
            {
                if (seen.Contains(start)) continue;
                int n = 0; double sx = 0, sz = 0, sy = 0;
                var by = new Dictionary<long, int>();
                stack.Push(start); seen.Add(start);
                while (stack.Count > 0)
                {
                    long k = stack.Pop();
                    var v = cells[k]; n += v.n; sx += v.sx; sz += v.sz; sy += v.sy;
                    foreach (var cb in cellBuilders[k]) { by.TryGetValue(cb.Key, out int bn); by[cb.Key] = bn + cb.Value; }
                    int cx = (int)(k >> 32), cz = (int)(uint)(k & 0xffffffff);
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            long nk = ((long)(cx + dx) << 32) | (uint)(cz + dz);
                            if (busy.Contains(nk) && !seen.Contains(nk)) { seen.Add(nk); stack.Push(nk); }
                        }
                }
                long top = 0; int topN = 0;
                foreach (var kv in by) if (kv.Value > topN) { topN = kv.Value; top = kv.Key; }
                if (n >= minPieces) result.Add(new Base { x = (float)(sx / n), z = (float)(sz / n), y = (float)(sy / n), pieces = n, builder = top });
            }
            result.Sort((a, b) => b.pieces.CompareTo(a.pieces));
            return result;
        }
        public static string StatsJson => statsJson;

        // [x, z, y, yaw, sx, sz, h, mat, prefabIdx] per piece; prefab names once per chunk.
        // the chunk as built with rev 0 (what rev is a hash of), with the real rev written in
        private static string WithRev(string json, int rev)
        {
            const string zero = "\"rev\":0,";
            int at = json.IndexOf(zero, StringComparison.Ordinal);
            return at < 0 ? json : json.Substring(0, at) + "\"rev\":" + rev.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + json.Substring(at + zero.Length);
        }

        // the chunk's JSON with rev 0, into result[0]; pauses for the next frame when the sweep's share is used up
        private static System.Collections.IEnumerator BuildChunkJson(int cx, int cz, Piece[] pieces, string[] result)
        {
            var names = new Dictionary<int, int>();
            var nameList = new List<string>();
            var j = new JsonWriter(pieces.Length * 48 + 256);
            j.BeginObject();
            j.Prop("cx", cx).Prop("cz", cz).Prop("rev", 0).Prop("count", pieces.Length);
            j.Key("pieces").BeginArray();
            int i = 0;
            foreach (var p in pieces)
            {
                if ((++i & 127) == 0 && WorldSweep.Due) yield return null;
                if (!names.TryGetValue(p.prefab, out int idx))
                {
                    idx = nameList.Count; names[p.prefab] = idx;
                    nameList.Add(ShapeOf(p.prefab).name);
                }
                j.BeginArray();
                j.Value(p.x, 1).Value(p.z, 1).Value(p.y, 1).Value((int)p.yaw);
                j.Value(p.sx, 2).Value(p.sz, 2).Value(p.h, 2).Value((int)p.mat).Value(idx);
                j.End();
            }
            j.End();
            j.Key("prefabs").BeginArray();
            foreach (var n in nameList) j.Value(n);
            j.End();
            j.End();
            result[0] = j.ToString();
        }

        private static string BuildIndex() { var r = new string[1]; foreach (var _ in IndexParts(r, false)) { } return r[0]; }

        // the index of chunks under explored ground; sliced over frames from Finish
        private static IEnumerable<object> IndexParts(string[] result, bool slice)
        {
            int n = 0;
            var j = new JsonWriter(chunks.Count * 24 + 64);
            j.BeginObject();
            j.Prop("rev", indexRev);
            j.Prop("chunkSize", TileMath.CHUNK_SIZE);
            j.Key("chunks").BeginArray();
            foreach (var kv in chunks)
            {
                if (slice && (++n & 31) == 0 && WorldSweep.Due) yield return null;
                int cx = kv.Key / 4096, cz = kv.Key % 4096;
                // only chunks over explored ground are advertised; the fog is enforced here, not in the browser
                float minX = TileMath.ChunkMin(cx), minZ = TileMath.ChunkMin(cz);
                if (!MuninConfig.REVEAL_ALL && !Fog.AnyExplored(minX, minZ, minX + TileMath.CHUNK_SIZE, minZ + TileMath.CHUNK_SIZE)) continue;
                j.BeginArray().Value(cx).Value(cz).Value(kv.Value.rev).Value(kv.Value.count).End();
            }
            j.End();
            Regions.WriteIndex(j, ListedRev);
            j.End();
            result[0] = j.ToString();
        }

        private static string BuildStats()
        {
            var j = new JsonWriter(2048);
            j.BeginObject();
            j.Prop("total", total);
            j.Prop("distinct", byPrefab.Count);
            j.Key("materials").BeginArray();
            var mats = new List<KeyValuePair<int, int>>(byMaterial);
            mats.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var kv in mats)
                j.BeginObject().Prop("name", ((Palette.Material)kv.Key).ToString()).Prop("id", kv.Key).Prop("count", kv.Value).End();
            j.End();
            j.Key("prefabs").BeginArray();
            var top = new List<KeyValuePair<int, int>>(byPrefab);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            int n = 0;
            foreach (var kv in top)
            {
                if (n++ >= 40) break;
                j.BeginObject().Prop("name", ShapeOf(kv.Key).name).Prop("count", kv.Value).End();
            }
            j.End();
            j.Key("builders").BeginArray();
            var bl = new List<KeyValuePair<long, int>>(byCreator);
            bl.Sort((a, b) => b.Value.CompareTo(a.Value));
            n = 0;
            foreach (var kv in bl)
            {
                if (n++ >= 50) break;
                j.BeginObject().Prop("id", kv.Key.ToString(CultureInfo.InvariantCulture)).Prop("count", kv.Value).End();
            }
            j.End();
            j.End();
            return j.ToString();
        }

        // ---------------------------------------------------------------- classification

        internal static Shape ShapeOf(int prefabHash)
        {
            if (shapeCache.TryGetValue(prefabHash, out var s)) return s;
            string name = null;
            WearNTear.MaterialType? built = null;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
                if (go != null) { name = go.name; var wnt = go.GetComponent<WearNTear>(); if (wnt != null) built = wnt.m_materialType; }
            }
            catch { }
            s = ShapeOfName(name ?? ("#" + prefabHash));
            // what the piece is made of as the game has it (WearNTear.m_materialType: what its support and
            // damage go by), where the name gave no more than wood or nothing: the special looks the game
            // doesn't tell apart (thatch, fire, portals, cloth...) are kept from the name
            if (built.HasValue && (s.mat == Palette.Material.Wood || s.mat == Palette.Material.Misc))
                switch (built.Value)
                {
                    case WearNTear.MaterialType.Stone: s.mat = Palette.Material.Stone; break;
                    case WearNTear.MaterialType.Iron: s.mat = Palette.Material.Iron; break;
                    case WearNTear.MaterialType.HardWood: s.mat = Palette.Material.CoreWood; break;
                    case WearNTear.MaterialType.Marble: s.mat = Palette.Material.BlackMarble; break;
                    case WearNTear.MaterialType.Ashstone: s.mat = Palette.Material.Grausten; break;
                    case WearNTear.MaterialType.Wood: s.mat = Palette.Material.Wood; break;
                }
            shapeCache[prefabHash] = s;
            return s;
        }

        internal static Shape ShapeOfName(string name)
        {
            string n = name.ToLowerInvariant();
            var s = new Shape { sx = 1f, sz = 1f, h = 1f, mat = Palette.Material.Wood, name = name };

            // things a player "creates" that are not buildings
            if (n.Contains("sapling") || n.Contains("_planted") || n.Contains("vines") || (n.StartsWith("piece_", StringComparison.Ordinal) && (n.Contains("plant") || n.Contains("seed"))))
            { s.skip = true; return s; }
            if (n.Contains("tombstone") || n.StartsWith("player", StringComparison.Ordinal) || n.Contains("_ragdoll") || n.Contains("smokeball") || n.Contains("projectile")
                || n.StartsWith("vfx_", StringComparison.Ordinal) || n.StartsWith("sfx_", StringComparison.Ordinal) || n.StartsWith("fx_", StringComparison.Ordinal))
            { s.skip = true; return s; }

            // material
            if (n.Contains("portal")) s.mat = Palette.Material.Portal;
            else if (n.Contains("blackmarble")) s.mat = Palette.Material.BlackMarble;
            else if (n.Contains("grausten")) s.mat = Palette.Material.Grausten;
            else if (n.Contains("flametal")) s.mat = Palette.Material.Flametal;
            else if (n.Contains("stone") || n.Contains("marble")) s.mat = Palette.Material.Stone;
            else if (n.Contains("iron") || n.Contains("metal") || n.Contains("copper") || n.Contains("bronze") || n.Contains("silver")) s.mat = Palette.Material.Iron;
            else if (n.Contains("darkwood") || n.Contains("dark_wood")) s.mat = Palette.Material.DarkWood;
            else if (n.Contains("ashwood") || n.Contains("ash_wood")) s.mat = Palette.Material.Ashwood;
            else if (n.Contains("roof") || n.Contains("thatch") || n.Contains("straw")) s.mat = Palette.Material.Thatch;
            else if (n.Contains("fire") || n.Contains("hearth") || n.Contains("forge") || n.Contains("smelter") || n.Contains("kiln") || n.Contains("furnace") || n.Contains("brazier") || n.Contains("torch") || n.Contains("bonfire") || n.Contains("candle")) s.mat = Palette.Material.Fire;
            else if (n.Contains("crystal") || n.Contains("glass")) s.mat = Palette.Material.Crystal;
            else if (n.Contains("cloth") || n.Contains("banner") || n.Contains("rug") || n.Contains("carpet") || n.Contains("tent") || n.Contains("sail")) s.mat = Palette.Material.Cloth;
            else if (n.Contains("core") || n.Contains("log") || n.Contains("pole")) s.mat = Palette.Material.CoreWood;
            else if (n.Contains("wood")) s.mat = Palette.Material.Wood;
            else s.mat = Palette.Material.Misc;

            // size token like 2x2 / 4x2 / 1x1 (width x depth for floors, width x height for walls)
            int a = 0, b = 0;
            int xi = n.IndexOf('x');
            while (xi > 0 && xi < n.Length - 1)
            {
                if (char.IsDigit(n[xi - 1]) && char.IsDigit(n[xi + 1]))
                {
                    a = n[xi - 1] - '0'; b = n[xi + 1] - '0';
                    if (xi + 2 < n.Length && char.IsDigit(n[xi + 2])) b = b * 10 + (n[xi + 2] - '0');
                    if (xi - 2 >= 0 && char.IsDigit(n[xi - 2])) a = a + 10 * (n[xi - 2] - '0');
                    break;
                }
                xi = n.IndexOf('x', xi + 1);
            }
            bool stoneLike = s.mat == Palette.Material.Stone || s.mat == Palette.Material.BlackMarble || s.mat == Palette.Material.Grausten;
            float thick = stoneLike ? 0.5f : 0.3f;

            if (n.Contains("floor") || n.Contains("platform") || n.Contains("deck"))
            {
                s.sx = a > 0 ? a : 2f; s.sz = b > 0 ? b : s.sx; s.h = stoneLike ? 0.5f : 0.2f;
                if (n.Contains("large")) { s.sx = 4f; s.sz = 4f; }
                if (n.Contains("1x1") || n.Contains("quarter")) { s.sx = 1f; s.sz = 1f; }
            }
            else if (n.Contains("roof"))
            {
                s.sx = 2f; s.sz = 2f; s.h = n.Contains("45") ? 2f : 1f;
                if (n.Contains("corner") || n.Contains("top")) { s.h *= 0.8f; }
            }
            else if (n.Contains("wall"))
            {
                s.sx = a > 0 ? a : 2f; s.h = b > 0 ? b : 2f; s.sz = thick;
                if (n.Contains("half")) { s.h = 1f; }
                if (n.Contains("quarter")) { s.sx = 1f; s.h = 1f; }
                if (n.Contains("1x1")) { s.sx = 1f; s.h = 1f; }
                if (n.Contains("log")) { s.sz = 0.5f; }
            }
            else if (n.Contains("beam") || n.Contains("bar_") || n.Contains("railing"))
            {
                s.sx = n.Contains("1m") || n.Contains("_1") ? 1f : 2f; s.sz = 0.25f; s.h = n.Contains("26") ? 1f : n.Contains("45") ? 2f : 0.25f;
                if (n.Contains("railing")) { s.h = 1f; s.sz = 0.15f; }
            }
            else if (n.Contains("pole") || n.Contains("pillar") || n.Contains("column"))
            {
                s.sx = stoneLike ? 1f : 0.3f; s.sz = s.sx; s.h = n.Contains("1m") || n.Contains("_1") ? 1f : n.Contains("4") ? 4f : 2f;
            }
            else if (n.Contains("door") || n.Contains("gate"))
            {
                s.sx = n.Contains("double") || n.Contains("gate") ? 4f : 2f; s.sz = thick; s.h = 2f;
                if (n.Contains("gate") && n.Contains("stone")) { s.sx = 4f; s.h = 4f; }
            }
            else if (n.Contains("stair") || n.Contains("ladder"))
            {
                s.sx = n.Contains("ladder") ? 1f : 2f; s.sz = n.Contains("ladder") ? 0.3f : 2f; s.h = 2f;
            }
            else if (n.Contains("fence") || n.Contains("sharp") || n.Contains("stake"))
            {
                s.sx = 2f; s.sz = 0.2f; s.h = 1.2f;
            }
            else if (n.Contains("arch"))
            {
                s.sx = 2f; s.sz = thick * 2; s.h = 2f;
            }
            else if (n.Contains("portal"))
            {
                s.sx = 2.2f; s.sz = 0.6f; s.h = 3.2f;
            }
            else if (n.Contains("hearth"))
            {
                s.sx = 3f; s.sz = 3f; s.h = 0.5f;
            }
            else if (n.Contains("bonfire")) { s.sx = 2f; s.sz = 2f; s.h = 1.5f; }
            else if (n.Contains("fire_pit") || n.Contains("firepit")) { s.sx = 1.5f; s.sz = 1.5f; s.h = 0.5f; }
            else if (n.Contains("bed")) { s.sx = 1.2f; s.sz = 2.2f; s.h = 0.6f; }
            else if (n.Contains("chest")) { s.sx = n.Contains("reinforced") || n.Contains("blackmetal") ? 1.4f : 1f; s.sz = 0.7f; s.h = 0.9f; }
            else if (n.Contains("workbench") || n.Contains("forge") || n.Contains("cauldron") || n.Contains("artisan") || n.Contains("stonecutter") || n.Contains("blackforge") || n.Contains("galdr"))
            { s.sx = 2.2f; s.sz = 1.6f; s.h = 1.2f; }
            else if (n.Contains("smelter") || n.Contains("kiln") || n.Contains("furnace") || n.Contains("windmill") || n.Contains("spinning") || n.Contains("eitr"))
            { s.sx = 2.5f; s.sz = 2.5f; s.h = n.Contains("windmill") ? 8f : 3f; }
            else if (n.Contains("torch") || n.Contains("brazier") || n.Contains("candle") || n.Contains("lantern")) { s.sx = 0.4f; s.sz = 0.4f; s.h = 1.5f; }
            else if (n.Contains("banner") || n.Contains("shield") || n.Contains("trophy") || n.Contains("itemstand") || n.Contains("item_stand")) { s.sx = 0.6f; s.sz = 0.2f; s.h = 1f; }
            else if (n.Contains("rug") || n.Contains("carpet")) { s.sx = 2f; s.sz = 2f; s.h = 0.05f; }
            else if (n.Contains("table") || n.Contains("bench") || n.Contains("chair") || n.Contains("throne") || n.Contains("stool")) { s.sx = n.Contains("table") ? 2f : 0.8f; s.sz = 0.8f; s.h = 0.9f; }
            else if (n.Contains("wisp") || n.Contains("guard_stone") || n.Contains("ward")) { s.sx = 0.6f; s.sz = 0.6f; s.h = 1.5f; }
            else if (n.Contains("bee") || n.Contains("hive")) { s.sx = 1f; s.sz = 1f; s.h = 1f; }
            else if (n.Contains("sign")) { s.sx = 1f; s.sz = 0.1f; s.h = 0.6f; }
            else if (n.Contains("barrel") || n.Contains("pot") || n.Contains("cask")) { s.sx = 0.8f; s.sz = 0.8f; s.h = 1f; }
            else if (n.Contains("bridge") || n.Contains("dock")) { s.sx = 2f; s.sz = 4f; s.h = 0.4f; }
            return s;
        }
    }
}
