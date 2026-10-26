using System;
using System.IO;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using MuninMap.Tiles;
using MuninMap.Util;

namespace MuninMap.World
{
    // World-generated structures -- abandoned houses, fuling villages, shipwrecks, mountain and
    // castle ruins -- as vector data for the 2D map. Same chunk format as the player-built
    // Structures ([x, z, y, yaw, sx, sz, h, mat, prefabIdx] per piece) and the same name-based
    // footprints, so the 2D map gets them without the 3D object data or the model export.
    // Like Structures, a chunk is only advertised once it is under explored ground.
    internal static class Ruins
    {
        private sealed class Chunk { public int rev; public string json; public int count; }
        private static readonly ConcurrentDictionary<int, Chunk> chunks = new ConcurrentDictionary<int, Chunk>();
        private static readonly ConcurrentDictionary<int, int> chunkHash = new ConcurrentDictionary<int, int>();
        private static readonly Dictionary<int, bool> wanted = new Dictionary<int, bool>();
        private static Dictionary<int, List<Structures.Piece>> building;
        private static int total;
        private static volatile string indexJson = "{\"rev\":0,\"chunks\":[]}";
        private static int indexRev, indexExplored = -1;

        // structural pieces that are not Piece/WearNTear prefabs but still read as buildings
        private static readonly Regex kits = new Regex("^(mountainkit|castlekit|goblin|dvergr|dverger|ashland|charred|blackmarble|startplatform|bossstone_|cloth_hanging|fenrirhide|shipwreck|ruin)", RegexOptions.Compiled);

        public static int Total { get; private set; }
        public static string IndexJson => indexJson;

        private static int ChunkKey(int cx, int cz) => cx * 4096 + cz;

        // kept from one sweep to the next and only emptied: less garbage, fewer collection pauses
        private static readonly Dictionary<int, List<Structures.Piece>> pool = new Dictionary<int, List<Structures.Piece>>();

        public static void Begin()
        {
            foreach (var l in pool.Values) l.Clear();
            building = pool;
            total = 0;
            BeginSites();
        }

        // ---- what kind of place each piece belongs to, for the map's switches per kind: the game
        // location it stands in (an abandoned house, a fuling village...), by the location's name.
        // Index = the number sent with each piece (web: layers/shapes.js RUIN_SITES); 0 none of these.
        public static readonly string[] Sites = { "Other", "Houses", "Stone ruins", "Fuling villages", "Dvergr sites", "Ashlands ruins", "Shipwrecks", "Camps", "Boss altars", "Deep North", "Dungeons" };
        internal static byte SiteOf(string location)
        {
            if (string.IsNullOrEmpty(location)) return 0;
            string n = location.ToLowerInvariant();
            if (n == "eikthyrnir" || n == "gdking" || n == "bonemass" || n == "dragonqueen" || n == "goblinking" || n.StartsWith("mistlands_dvergrbossentrance", StringComparison.Ordinal) || n.Contains("fader") || n == "dn_bossroom") return 8;
            if (n.StartsWith("shipwreck", StringComparison.Ordinal) || n.StartsWith("frozenship", StringComparison.Ordinal)) return 6;
            if (n.StartsWith("crypt", StringComparison.Ordinal) || n.StartsWith("sunkencrypt", StringComparison.Ordinal) || n.StartsWith("trollcave", StringComparison.Ordinal) || n.StartsWith("mountaincave", StringComparison.Ordinal) || n.StartsWith("hildir_cave", StringComparison.Ordinal) || n.StartsWith("hildir_crypt", StringComparison.Ordinal) || n.StartsWith("hildir_plainsfortress", StringComparison.Ordinal)
                || n == "bearcave" || n == "halfburried_forestcrypt" || n.StartsWith("thehole", StringComparison.Ordinal) || n == "thedarkesthole") return 10;
            if (n.StartsWith("dn_", StringComparison.Ordinal) || n.StartsWith("north", StringComparison.Ordinal) || n == "morkborg" || n.StartsWith("fimbul", StringComparison.Ordinal) || n.StartsWith("hotspring", StringComparison.Ordinal)) return 9;
            if (n.StartsWith("runestone", StringComparison.Ordinal) || n.StartsWith("waymarker", StringComparison.Ordinal) || n.StartsWith("dolmen", StringComparison.Ordinal) || n == "drakelorestone" || n == "starttemple" || n == "ancientupgradestation") return 2;
            if (n.StartsWith("vendor_", StringComparison.Ordinal)) return 7;
            if (n.StartsWith("woodhouse", StringComparison.Ordinal) || n.StartsWith("woodfarm", StringComparison.Ordinal) || n.StartsWith("woodvillage", StringComparison.Ordinal) || n.Contains("logcabin") || n.StartsWith("swamphut", StringComparison.Ordinal) || n.Contains("cabin")) return 1;
            if (n.StartsWith("goblin", StringComparison.Ordinal) || n.Contains("fuling")) return 3;
            if (n.StartsWith("mistlands", StringComparison.Ordinal) || n.Contains("dvergr")) return 4;
            if (n.StartsWith("charred", StringComparison.Ordinal) || n.StartsWith("ashland", StringComparison.Ordinal) || n.Contains("morgen") || n.Contains("placeofmystery") || n.Contains("volture") || n.Contains("lava")) return 5;
            if (n.Contains("camp")) return 7;
            if (n.StartsWith("stone", StringComparison.Ordinal) || n.StartsWith("ruin", StringComparison.Ordinal) || n.Contains("ruin") || n.Contains("well") || n.Contains("grave") || n.Contains("henge") || n.Contains("tower") || n.Contains("shipsetting")) return 2;
            return 0;
        }
        private struct Site { public Vector3 pos; public float r2; public byte kind; }
        private static Dictionary<long, List<Site>> sites;
        private static readonly HashSet<string> loggedOther = new HashSet<string>();
        private static long SiteZone(int zx, int zz) => ((long)zx << 32) ^ (uint)zz;
        private static int sitesFrom = -1;   // how many locations the map was made from: made again only when that changes
        private static void BeginSites()
        {
            try
            {
                var zs = ZoneSystem.instance;
                if (zs == null || zs.m_locationInstances == null) { sites = new Dictionary<long, List<Site>>(); return; }
                if (sites != null && sitesFrom == zs.m_locationInstances.Count) return;
                sitesFrom = -1;
                sites = new Dictionary<long, List<Site>>();
                foreach (var li in zs.m_locationInstances.Values)
                {
                    var loc = li.m_location;
                    if (loc == null) continue;
                    byte kind = SiteOf(loc.m_prefabName);
                    if (kind == 0 && MuninConfig.DEBUG && loggedOther.Add(loc.m_prefabName)) ZLog.Log("MuninMap: world structures: no kind for location " + loc.m_prefabName);
                    float r = Mathf.Max(loc.m_exteriorRadius, loc.m_interiorRadius, 12f) + 8f;
                    var site = new Site { pos = li.m_position, r2 = r * r, kind = kind };
                    int x0 = Mathf.FloorToInt((li.m_position.x - r) / 64f), x1 = Mathf.FloorToInt((li.m_position.x + r) / 64f);
                    int z0 = Mathf.FloorToInt((li.m_position.z - r) / 64f), z1 = Mathf.FloorToInt((li.m_position.z + r) / 64f);
                    for (int zz = z0; zz <= z1; zz++)
                        for (int zx = x0; zx <= x1; zx++)
                        {
                            long k = SiteZone(zx, zz);
                            if (!sites.TryGetValue(k, out var l)) sites[k] = l = new List<Site>(2);
                            l.Add(site);
                        }
                }
                sitesFrom = zs.m_locationInstances.Count;
            }
            catch (Exception e) { if (MuninConfig.DEBUG) ZLog.LogWarning("MuninMap: world structure kinds: " + e.Message); }
        }
        private static byte SiteAt(Vector3 p)
        {
            if (sites == null || !sites.TryGetValue(SiteZone(Mathf.FloorToInt(p.x / 64f), Mathf.FloorToInt(p.z / 64f)), out var l)) return 0;
            byte best = 0; float bestD = float.MaxValue;
            foreach (var s in l)
            {
                float d = (s.pos.x - p.x) * (s.pos.x - p.x) + (s.pos.z - p.z) * (s.pos.z - p.z);
                if (d <= s.r2 && d < bestD) { bestD = d; best = s.kind; }
            }
            return best;
        }

        // Main thread. Called for objects nobody built that are neither markers nor vegetation.
        public static void Observe(ZDO zdo, int prefabHash, Vector3 pos)
        {
            if (building == null || !Wanted(prefabHash)) return;
            // dungeon interiors are built high above their entrance (y ~ 5000): skip them, they would
            // pinpoint every crypt and cave from the map (the same no-spoilers rule as the markers)
            if (pos.y > 1000f) return;
            var shape = Structures.ShapeOf(prefabHash);
            if (shape.skip) return;
            short yaw = 0;
            try { yaw = (short)Math.Round(zdo.GetRotation().eulerAngles.y); } catch { }
            int cx = TileMath.ChunkCoord(pos.x), cz = TileMath.ChunkCoord(pos.z);
            if (cx < 0 || cz < 0 || cx >= TileMath.ChunksPerSide || cz >= TileMath.ChunksPerSide) return;
            int key = ChunkKey(cx, cz);
            if (!building.TryGetValue(key, out var list)) building[key] = list = new List<Structures.Piece>(64);
            list.Add(new Structures.Piece { x = pos.x, y = pos.y, z = pos.z, yaw = yaw, sx = shape.sx, sz = shape.sz, h = shape.h, mat = shape.mat, prefab = prefabHash, site = SiteAt(pos) });
            total++;
        }

        private static bool Wanted(int prefabHash)
        {
            if (wanted.TryGetValue(prefabHash, out bool w)) return w;
            w = false;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
                if (go != null)
                {
                    string n = go.name.ToLowerInvariant();
                    // never spawners, pickables, loot or anything a player could farm off the map
                    bool excluded = n.StartsWith("dungeon_", StringComparison.Ordinal) || n.Contains("spawner") || n.StartsWith("pickable", StringComparison.Ordinal) || n.Contains("treasure") || n.Contains("loot")
                        || n.Contains("_ragdoll") || n.StartsWith("vfx_", StringComparison.Ordinal) || n.StartsWith("sfx_", StringComparison.Ordinal) || n.StartsWith("fx_", StringComparison.Ordinal);
                    // creatures (the fulings themselves match "goblin"), pickables, items and bones are not buildings
                    if (!excluded)
                        foreach (var comp in new[] { "Character", "Humanoid", "MonsterAI", "AnimalAI", "Pickable", "ItemDrop", "Tameable" })
                            if (go.GetComponent(comp) != null) { excluded = true; break; }
                    if (!excluded && (n.Contains("_ribs") || n.Contains("skull") || n.Contains("bones") || n.Contains("berries") || n.Contains("bush")))
                        excluded = true;
                    if (!excluded)
                        w = go.GetComponent("Piece") != null || go.GetComponent("WearNTear") != null || kits.IsMatch(n);
                }
            }
            catch { }
            wanted[prefabHash] = w;
            return w;
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
                    sum += WorldSweep.Mix(unchecked((int)(p.x * 10) * 7 + (int)(p.z * 10) * 13 + (int)(p.y * 10) * 3 + p.yaw * 101 + p.prefab + p.site * 977));
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
                chunks[kv.Key] = new Chunk { rev = rev, count = pieces.Length, json = WithRev(built[0], rev) };
                changed++;
            }
            foreach (int key in new List<int>(chunks.Keys))
            {
                if (seen.Contains(key)) continue;
                chunks.TryRemove(key, out _);
                chunkHash.TryRemove(key, out _);
                changed++;
            }
            Total = total;
            int explored = Fog.ExploredCells;
            if (changed > 0 || explored != indexExplored) { indexRev++; indexExplored = explored; indexJson = BuildIndex(); }
            if (changed > 0) SaveCache();
            building = null;
            LastChanged = changed;
        }

        // ---- cache on disk (ChunkCache): served at once after a restart, until the first sweep
        private static string cachePath;
        public static void LoadCache(string worldDataPath)
        {
            cachePath = Path.Combine(worldDataPath, "ruins-cache.txt");
            int n = 0;
            foreach (var l in ChunkCache.Load(cachePath)) { chunks[l.key] = new Chunk { rev = l.rev, count = l.count, json = l.json }; n += l.count; }
            if (chunks.Count == 0) return;
            Total = n; indexRev++; indexExplored = Fog.ExploredCells; indexJson = BuildIndex();
        }

        private static void SaveCache()
        {
            var lines = new List<ChunkCache.Line>(chunks.Count);
            foreach (var kv in chunks) lines.Add(new ChunkCache.Line { key = kv.Key, rev = kv.Value.rev, count = kv.Value.count, json = kv.Value.json });
            ChunkCache.Save(cachePath, lines);
        }

        // null when the chunk is unknown or not explored yet
        public static string ChunkJson(int cx, int cz)
        {
            float minX = TileMath.ChunkMin(cx), minZ = TileMath.ChunkMin(cz);
            if (!MuninConfig.REVEAL_ALL && !Fog.AnyExplored(minX, minZ, minX + TileMath.CHUNK_SIZE, minZ + TileMath.CHUNK_SIZE)) return null;
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

        // the chunk as built with rev 0 (what rev is a hash of), with the real rev written in
        private static string WithRev(string json, int rev)
        {
            const string zero = "\"rev\":0,";
            int at = json.IndexOf(zero, StringComparison.Ordinal);
            return at < 0 ? json : json.Substring(0, at) + "\"rev\":" + rev.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + json.Substring(at + zero.Length);
        }

        // the chunk's JSON with rev 0, into result[0]; pauses for the next frame when the sweep's share is used up
        private static System.Collections.IEnumerator BuildChunkJson(int cx, int cz, Structures.Piece[] pieces, string[] result)
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
                    nameList.Add(Structures.ShapeOf(p.prefab).name);
                }
                j.BeginArray();
                j.Value(p.x, 1).Value(p.z, 1).Value(p.y, 1).Value((int)p.yaw);
                j.Value(p.sx, 2).Value(p.sz, 2).Value(p.h, 2).Value((int)p.mat).Value(idx).Value((int)p.site);
                j.End();
            }
            j.End();
            j.Key("prefabs").BeginArray();
            foreach (var n in nameList) j.Value(n);
            j.End();
            j.End();
            result[0] = j.ToString();
        }

        private static string BuildIndex()
        {
            var j = new JsonWriter(chunks.Count * 24 + 64);
            j.BeginObject();
            j.Prop("rev", indexRev);
            j.Prop("chunkSize", TileMath.CHUNK_SIZE);
            j.Key("chunks").BeginArray();
            foreach (var kv in chunks)
            {
                int cx = kv.Key / 4096, cz = kv.Key % 4096;
                float minX = TileMath.ChunkMin(cx), minZ = TileMath.ChunkMin(cz);
                if (!MuninConfig.REVEAL_ALL && !Fog.AnyExplored(minX, minZ, minX + TileMath.CHUNK_SIZE, minZ + TileMath.CHUNK_SIZE)) continue;
                j.BeginArray().Value(cx).Value(cz).Value(kv.Value.rev).Value(kv.Value.count).End();
            }
            j.End();
            Regions.WriteIndex(j, ListedRev);
            j.End();
            return j.ToString();
        }
    }
}
