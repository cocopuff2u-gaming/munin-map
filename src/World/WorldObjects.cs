using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using MuninMap.Models;
using MuninMap.Tiles;
using MuninMap.Util;

namespace MuninMap.World
{
    // Every visible object in the world, per 256 m chunk, for the 3D view:
    // player pieces, world-generated ruins and dungeon entrances, trees,
    // rocks, boats, carts, furniture -- anything with a mesh, whether a
    // player placed it or the world did. Creatures, items and effects are
    // left out. Each record is prefab + position + rotation + scale, which
    // together with the prefab's exported model reproduces the object
    // exactly as the game renders it.
    //
    // Fed by the world sweep on the main thread; chunk arrays are swapped
    // in at the end of a sweep and serialised lazily on the HTTP thread.
    internal static class WorldObjects
    {
        public enum Cat : byte { Skip = 0, Piece = 1, Tree = 2, Bush = 3, Rock = 4, Other = 5 }

        public struct Obj
        {
            public int prefab; public float x, y, z; public float qx, qy, qz, qw; public float sx, sy, sz; public bool creator;
        }

        private sealed class Chunk { public int rev; public Obj[] objs; public byte[] bytes; }

        private static readonly Dictionary<int, Cat> catCache = new Dictionary<int, Cat>();
        private static HashSet<string> enabledCats;
        private static readonly ConcurrentDictionary<int, Chunk> chunks = new ConcurrentDictionary<int, Chunk>();
        private static readonly ConcurrentDictionary<int, int> chunkHash = new ConcurrentDictionary<int, int>();
        private static Dictionary<int, List<Obj>> building;
        private static volatile string indexJson = "{\"rev\":0,\"chunks\":[]}";
        private static int indexRev, indexExplored = -1;
        private static readonly int hashScale = "scale".GetStableHashCode();
        private static readonly int hashScaleScalar = "scaleScalar".GetStableHashCode();

        public static int Total { get; private set; }
        public static string IndexJson => indexJson;

        public static string CatName(Cat c)
        {
            switch (c) { case Cat.Piece: return "piece"; case Cat.Tree: return "tree"; case Cat.Bush: return "bush"; case Cat.Rock: return "rock"; case Cat.Other: return "other"; default: return "skip"; }
        }

        public static void Begin() { building = new Dictionary<int, List<Obj>>(chunks.Count + 16); }

        private static bool Enabled(Cat c)
        {
            if (enabledCats == null)
            {
                enabledCats = new HashSet<string>();
                foreach (var part in (MuninConfig.OBJECT_CATEGORIES ?? "").Split(',')) { string t = part.Trim().ToLowerInvariant(); if (t.Length > 0) enabledCats.Add(t); }
                if (enabledCats.Count == 0) { enabledCats.Add("piece"); enabledCats.Add("other"); enabledCats.Add("rock"); }
            }
            return enabledCats.Contains(CatName(c));
        }

        private static int ChunkKey(int cx, int cz) => cx * 4096 + cz;

        // Main thread. Classifies the prefab once (cached) and records the object.
        public static void Observe(ZDO zdo, int prefabHash, Vector3 pos, long creator)
        {
            if (building == null) return;
            Cat cat = Classify(prefabHash);
            if (cat == Cat.Skip) return;
            int cx = TileMath.ChunkCoord(pos.x), cz = TileMath.ChunkCoord(pos.z);
            if (cx < 0 || cz < 0 || cx >= TileMath.ChunksPerSide || cz >= TileMath.ChunksPerSide) return;
            var o = new Obj { prefab = prefabHash, x = pos.x, y = pos.y, z = pos.z, qw = 1f, sx = 1f, sy = 1f, sz = 1f, creator = creator != 0L };
            try { Quaternion q = zdo.GetRotation(); o.qx = q.x; o.qy = q.y; o.qz = q.z; o.qw = q.w; } catch { }
            try
            {
                Vector3 s = zdo.GetVec3(hashScale, Vector3.one);
                if (s.x > 0 && s.y > 0 && s.z > 0) { o.sx = s.x; o.sy = s.y; o.sz = s.z; }
                float ss = zdo.GetFloat(hashScaleScalar, 1f);
                if (ss > 0 && Math.Abs(ss - 1f) > 0.001f) { o.sx *= ss; o.sy *= ss; o.sz *= ss; }
            }
            catch { }
            int key = ChunkKey(cx, cz);
            if (!building.TryGetValue(key, out var list)) building[key] = list = new List<Obj>(256);
            list.Add(o);
        }

        private static Cat Classify(int prefabHash)
        {
            if (catCache.TryGetValue(prefabHash, out var c)) return c;
            c = Cat.Skip;
            GameObject go = null;
            try { go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null; } catch { }
            if (go != null && PrefabExporter.IsVisibleThing(go))
            {
                string n = go.name.ToLowerInvariant();
                if (go.GetComponent("Piece") != null || go.GetComponent("WearNTear") != null) c = Cat.Piece;
                else if (n.Contains("_log") || n.EndsWith("logs", StringComparison.Ordinal) || n.Contains("_trunk") || n.Contains("_stub") || n.Contains("stubbe") || go.GetComponent("TreeBase") != null || go.GetComponent("TreeLog") != null) c = Cat.Tree;
                else
                {
                    var veg = Vegetation.ClassifyName(n);
                    switch (Palette.Base(veg.kind))
                    {
                        case Palette.Veg.None: c = Cat.Other; break;
                        case Palette.Veg.Rock: case Palette.Veg.Ore: c = Cat.Rock; break;
                        case Palette.Veg.Bush: case Palette.Veg.Berry: case Palette.Veg.Stump:
                        case Palette.Veg.Raspberry: case Palette.Veg.Blueberry: case Palette.Veg.Cloudberry: c = Cat.Bush; break;
                        default: c = Palette.IsPickPlant(veg.kind) ? Cat.Bush : Cat.Tree; break;   // picked plants: small, like bushes
                    }
                }
                if (c == Cat.Other && (n.Contains("_ragdoll") || n.Contains("smoke") || n.Contains("cloud") || n.Contains("_proxy") || n == "locationproxy")) c = Cat.Skip;
                if (c != Cat.Skip && !Enabled(c)) c = Cat.Skip;
                if (c != Cat.Skip) ModelStore.Request(prefabHash, CatName(c));
            }
            catCache[prefabHash] = c;
            return c;
        }

        public static int LastChanged { get; private set; }   // chunks the last Finish changed

        // Main thread, end of sweep, a slice per frame (WorldSweep.Due)
        public static IEnumerator Finish()
        {
            LastChanged = 0;
            if (building == null) yield break;
            int changed = 0, total = 0;
            var seen = new HashSet<int>();
            foreach (var kv in building)
            {
                if (WorldSweep.Due) yield return null;   // the rest next frame
                var list = kv.Value;
                total += list.Count;
                list.Sort((a, b) => a.prefab != b.prefab ? a.prefab.CompareTo(b.prefab) : a.x != b.x ? a.x.CompareTo(b.x) : a.z.CompareTo(b.z));
                int h = 17;
                foreach (var o in list)
                    h = unchecked(h * 31 + o.prefab + (int)(o.x * 10) * 7 + (int)(o.z * 10) * 13 + (int)(o.y * 10) * 3 + (int)(o.qy * 1000) * 101 + (int)(o.sx * 100));
                seen.Add(kv.Key);
                if (chunkHash.TryGetValue(kv.Key, out int old) && old == h) continue;
                chunkHash[kv.Key] = h;
                int rev = (chunks.TryGetValue(kv.Key, out var prev) ? prev.rev : 0) + 1;
                chunks[kv.Key] = new Chunk { rev = rev, objs = list.ToArray() };
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
            // the index lists only chunks under explored ground, so it also has to follow the fog
            int explored = Fog.ExploredCells;
            if (changed > 0 || explored != indexExplored) { indexRev++; indexExplored = explored; indexJson = BuildIndex(); }
            building = null;
            LastChanged = changed;
        }

        private static string BuildIndex()
        {
            var j = new JsonWriter(chunks.Count * 24 + 64);
            j.BeginObject().Prop("rev", indexRev).Prop("chunkSize", TileMath.CHUNK_SIZE).Key("chunks").BeginArray();
            foreach (var kv in chunks)
            {
                int cx = kv.Key / 4096, cz = kv.Key % 4096;
                float minX = TileMath.ChunkMin(cx), minZ = TileMath.ChunkMin(cz);
                if (!MuninConfig.REVEAL_ALL && !Fog.AnyExplored(minX, minZ, minX + TileMath.CHUNK_SIZE, minZ + TileMath.CHUNK_SIZE)) continue;
                j.BeginArray().Value(cx).Value(cz).Value(kv.Value.rev).Value(kv.Value.objs.Length).End();
            }
            j.End().End();
            return j.ToString();
        }

        // Any thread. Binary chunk, little-endian:
        //   'OBJ1', u32 count, u32 prefabCount, i32[prefabCount] prefab hashes,
        //   then per object: u16 prefabIdx, u8 flags (1 = player-built), u8 pad,
        //   f32 x y z, f32 qx qy qz qw, f32 sx sy sz            (44 bytes)
        public static byte[] ChunkBytes(int cx, int cz)
        {
            if (!chunks.TryGetValue(ChunkKey(cx, cz), out var c)) return null;
            byte[] cached = c.bytes;
            if (cached != null) return cached;
            var table = new Dictionary<int, int>();
            var order = new List<int>();
            foreach (var o in c.objs) if (!table.ContainsKey(o.prefab)) { table[o.prefab] = order.Count; order.Add(o.prefab); }
            using (var ms = new MemoryStream(16 + order.Count * 4 + c.objs.Length * 44))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte)'O'); bw.Write((byte)'B'); bw.Write((byte)'J'); bw.Write((byte)'1');
                bw.Write(c.objs.Length); bw.Write(order.Count);
                foreach (int p in order) bw.Write(p);
                foreach (var o in c.objs)
                {
                    bw.Write((ushort)table[o.prefab]); bw.Write((byte)(o.creator ? 1 : 0)); bw.Write((byte)0);
                    bw.Write(o.x); bw.Write(o.y); bw.Write(o.z);
                    bw.Write(o.qx); bw.Write(o.qy); bw.Write(o.qz); bw.Write(o.qw);
                    bw.Write(o.sx); bw.Write(o.sy); bw.Write(o.sz);
                }
                bw.Flush();
                c.bytes = ms.ToArray();
                return c.bytes;
            }
        }
    }
}
