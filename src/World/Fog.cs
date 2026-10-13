using System;
using System.IO;
using UnityEngine;
using MuninMap.Tiles;
using MuninMap.Util;

namespace MuninMap.World
{
    // The shared explored mask ("fog of war").
    //
    // One byte per cell on the same 2048 x 12 m grid the mod has always used,
    // so an existing fog.png keeps every metre players have already uncovered
    // when upgrading. Row 0 is the SOUTH edge of the world (that is how the
    // old Texture2D was laid out); the PNG written to disk and served to the
    // browser has north at the top, like any map.
    //
    // Revealing runs on the main thread from player positions. Everything
    // else -- the tile store asking whether a square is explored, the HTTP
    // thread serving the mask -- only reads the byte array, which is safe.
    internal static class Fog
    {
        private static byte[] mask;
        private static int size, pixelSize, half;
        private static int exploredCount;
        private static volatile byte[] pngCache;
        public static bool Dirty { get; private set; }

        public static int Size => size;
        public static int PixelSize => pixelSize;

        public static void Init(int textureSize, int pixel)
        {
            size = textureSize; pixelSize = pixel; half = size / 2;
            mask = new byte[size * size];
            exploredCount = 0;
            pngCache = null;
            Dirty = false;
        }

        // Main thread: the legacy fog.png is decoded by Unity (it may be any PNG flavour the old code wrote).
        public static bool Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConv.LoadImage(tex, File.ReadAllBytes(path))) return false;
                if (tex.width != size || tex.height != size)
                {
                    ZLog.LogWarning($"MuninMap: fog.png is {tex.width}x{tex.height}, expected {size}x{size}; starting a fresh fog");
                    UnityEngine.Object.Destroy(tex);
                    return false;
                }
                Color32[] px = tex.GetPixels32();      // row 0 = bottom = south
                int n = 0;
                for (int i = 0; i < px.Length; i++) { bool e = px[i].r > 127; mask[i] = e ? (byte)255 : (byte)0; if (e) n++; }
                exploredCount = n;
                UnityEngine.Object.Destroy(tex);
                pngCache = null;
                return true;
            }
            catch (Exception e)
            {
                ZLog.LogWarning("MuninMap: could not read fog.png: " + e.Message);
                return false;
            }
        }

        public static void Save(string path)
        {
            try
            {
                File.WriteAllBytes(path, Png());
                Dirty = false;
            }
            catch (Exception e)
            {
                ZLog.LogError("MuninMap: FAILED TO WRITE FOG FILE! " + e.Message);
            }
        }

        private static byte[] revealedPng;

        // PNG with north at the top. Cached until the mask changes.
        public static byte[] Png()
        {
            if (MuninConfig.REVEAL_ALL)
            {
                // reveal_all: the whole world counts as explored, so the map shows no fog at all
                if (revealedPng == null) { var all = new byte[size * size]; for (int i = 0; i < all.Length; i++) all[i] = 255; revealedPng = Util.Png.Encode(all, size, size, Util.Png.Format.Gray8, fast: true); }
                return revealedPng;
            }
            byte[] c = pngCache;
            if (c != null) return c;
            byte[] flipped = new byte[mask.Length];
            for (int y = 0; y < size; y++)
                Buffer.BlockCopy(mask, (size - 1 - y) * size, flipped, y * size, size);
            c = Util.Png.Encode(flipped, size, size, Util.Png.Format.Gray8, fast: true);
            pngCache = c;
            return c;
        }

        // Main thread. Reveals a disc and reports newly uncovered cells to the tile store.
        public static int Reveal(float wx, float wz, float radius)
        {
            int r = (int)Math.Ceiling(radius / pixelSize);
            int r2 = r * r;
            int cx = Mathf.RoundToInt(wx / pixelSize + half);
            int cy = Mathf.RoundToInt(wz / pixelSize + half);
            int revealed = 0;
            for (int y = cy - r; y <= cy + r; y++)
            {
                if (y < 0 || y >= size) continue;
                for (int x = cx - r; x <= cx + r; x++)
                {
                    if (x < 0 || x >= size) continue;
                    int dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy > r2) continue;   // the game's own test (Minimap.Explore): the edge ring counts
                    int i = y * size + x;
                    if (mask[i] != 0) continue;
                    mask[i] = 255;
                    exploredCount++;
                    revealed++;
                    TileStore.OnExplored((x - half) * pixelSize, (y - half) * pixelSize);
                }
            }
            if (revealed > 0) { Dirty = true; pngCache = null; }
            return revealed;
        }

        // Zones the game has generated only exist where a player (or their ship) came within a few
        // zones, so the saved list is a record of everywhere anyone has been, including long before
        // this mod was installed. Reveal them, eroded by a margin so the edge lands near what the
        // players actually saw. Each zone is handled once per server run. Main thread.
        private static readonly System.Collections.Generic.HashSet<Vector2s> visitedDone = new System.Collections.Generic.HashSet<Vector2s>();
        // ---- cartography tables: the game's own explored map, as players recorded it ("Record" on a
        // table). The table keeps it in its ZDO (s_data): compressed, then int version, int cell
        // count, one bool per cell of the game's minimap grid, row by row from the south, the same
        // grid as this mask when the sizes match (2048 cells of 12 m by default). Exactly what the
        // players who recorded there had uncovered, merged in once per new version of each table.
        private static readonly System.Collections.Generic.Dictionary<ZDOID, int> tableSeen = new System.Collections.Generic.Dictionary<ZDOID, int>();
        public static int MapTablesRead { get; private set; }
        // A table's recorded map changed since it was last merged (raw: its stored bytes, still packed)
        public static bool TableChanged(ZDO zdo, byte[] raw)
        {
            if (mask == null || zdo == null || raw == null || raw.Length == 0) return false;
            int sig = raw.Length * 31 + raw[raw.Length / 2] * 7 + raw[raw.Length - 1];
            if (tableSeen.TryGetValue(zdo.m_uid, out int old) && old == sig) return false;
            tableSeen[zdo.m_uid] = sig;
            return true;
        }

        // Merges a table's recorded map (data: unpacked), a slice per frame: 4 million cells at once
        // held the game up for over 100 ms on the first sweep after a start
        public static System.Collections.IEnumerator MergeMapTable(ZDO zdo, byte[] data)
        {
            if (mask == null || data == null || data.Length < 8) yield break;
            int n = BitConverter.ToInt32(data, 4);
            int tsize = (int)Math.Round(Math.Sqrt(n));
            if (tsize * tsize != n || data.Length < 8 + n) { ZLog.LogWarning($"MuninMap: map table has {n} cells, not a square map"); yield break; }
            // the game's grid: tsize cells, each (world diameter / tsize) wide, centred on the origin
            float tpx = (half * 2f * pixelSize) / tsize;
            int revealed = 0;
            if (tsize == size)
            {
                for (int i = 0; i < n; i++)
                {
                    if ((i & 0xFFFF) == 0 && WorldSweep.Due) yield return null;
                    if (data[8 + i] == 0 || mask[i] != 0) continue;
                    mask[i] = 255; exploredCount++; revealed++;
                    TileStore.OnExplored((i % size - half) * pixelSize, (i / size - half) * pixelSize);
                }
            }
            else
            {
                // a different grid size: each of its explored cells clears the matching area here
                for (int i = 0; i < n; i++)
                {
                    if ((i & 0x3FFF) == 0 && WorldSweep.Due) yield return null;
                    if (data[8 + i] == 0) continue;
                    float wx = (i % tsize - tsize / 2) * tpx, wz = (i / tsize - tsize / 2) * tpx;
                    revealed += Reveal(wx, wz, tpx * 0.75f);
                }
            }
            if (revealed > 0) { Dirty = true; pngCache = null; }
            if (MuninConfig.DEBUG && tsize == size)   // the recorded map as is, for comparing against the zone guess
                try { var cells = new byte[n]; Buffer.BlockCopy(data, 8, cells, 0, n); File.WriteAllBytes(Path.Combine(global::MuninMap.MuninMap.worldDataPath, $"maptable_{zdo.GetPosition().x:F0}_{zdo.GetPosition().z:F0}.bin"), cells); } catch { }
            MapTablesRead++;
            ZLog.Log($"MuninMap: cartography table at {zdo.GetPosition().x:F0}, {zdo.GetPosition().z:F0}: {revealed} newly explored cells from the recorded map");
        }

        // ---- player traces: everything players built, sailed, drove or left (pieces, ships, carts,
        // portals, tombstones) marks a spot a player stood on, so the explore radius around it is
        // ground they saw. Exact, from the world save, and it covers places the zone guess is unsure
        // of (a boat moored at the end of a sea route). Gathered by the sweep, one per 24 m.
        private static readonly System.Collections.Generic.HashSet<int> traces = new System.Collections.Generic.HashSet<int>();
        private static readonly System.Collections.Generic.HashSet<int> tracesDone = new System.Collections.Generic.HashSet<int>();
        public static void AddTrace(Vector3 p)
        {
            int x = Mathf.RoundToInt(p.x / 24f), z = Mathf.RoundToInt(p.z / 24f);
            if (x < -512 || x > 512 || z < -512 || z > 512) return;   // dungeon interiors sit high above the world, but at a world x/z; this only bounds the key
            if (p.y > 3000f) return;                                 // inside a dungeon: the entrance is the trace that counts
            lock (traces) traces.Add((x + 1024) * 4096 + (z + 1024));
        }
        public static int RevealTraces()
        {
            if (!MuninConfig.REVEAL_VISITED || mask == null) return 0;
            int cells = 0, n = 0;
            lock (traces)
            {
                foreach (int k in traces)
                {
                    if (!tracesDone.Add(k)) continue;
                    float x = (k / 4096 - 1024) * 24f, z = (k % 4096 - 1024) * 24f;
                    cells += Reveal(x, z, MuninConfig.EXPLORE_RADIUS);
                    n++;
                }
                traces.Clear();
            }
            if (cells > 0) ZLog.Log($"MuninMap: revealed around {n} new player traces (builds, boats, carts, portals, tombstones): {cells} new cells");
            return cells;
        }

        public static int RevealVisitedZones()
        {
            if (!MuninConfig.REVEAL_VISITED) return 0;
            System.Collections.Generic.HashSet<Vector2s> gen;
            try { gen = ZoneSystem.instance?.m_generatedZones; } catch { return 0; }
            if (gen == null || gen.Count == 0) return 0;
            int margin = Mathf.Clamp(MuninConfig.REVEAL_VISITED_MARGIN, 0, 5);
            // How deep each built zone sits inside the built area: 1 at its edge, counting inward. The
            // game builds about 5 zones out from where a player goes, so the deeper a zone, the closer
            // to where someone actually walked.
            var depth = ZoneDepth(gen);
            if (MuninConfig.DEBUG)   // every built zone and its depth, for comparing against the recorded maps
                try { var sb = new System.Text.StringBuilder(); foreach (var kv in depth) sb.Append(kv.Key.x).Append(' ').Append(kv.Key.y).Append(' ').Append(kv.Value).Append('\n'); File.WriteAllText(Path.Combine(global::MuninMap.MuninMap.worldDataPath, "zones-depth.txt"), sb.ToString()); } catch { }
            int zones = 0, cells = 0;
            foreach (var z in gen)
            {
                if (visitedDone.Contains(z)) continue;
                int d = depth[z];
                bool take = d > margin;   // every zone within `margin` of it was built too
                if (!take) continue;
                visitedDone.Add(z);
                zones++;
                // Measured against maps players recorded to cartography tables (the game's own explored
                // map): at margin 4 a 64 m circle around each zone uncovers 98.7% of what they had
                // explored with the least ground they hadn't; filling the zones (46 m) left more out.
                cells += Reveal(z.x * 64f, z.y * 64f, margin >= 4 ? 64f : 46f);
            }
            if (zones > 0) ZLog.Log($"MuninMap: revealed {zones} zones players had already visited ({cells} new cells)");
            return cells;
        }

        // Chebyshev distance of each built zone to the nearest zone not built (1 = on the edge)
        private static System.Collections.Generic.Dictionary<Vector2s, int> ZoneDepth(System.Collections.Generic.HashSet<Vector2s> gen)
        {
            var depth = new System.Collections.Generic.Dictionary<Vector2s, int>(gen.Count);
            var queue = new System.Collections.Generic.Queue<Vector2s>();
            foreach (var z in gen)
            {
                bool edge = false;
                for (int dy = -1; dy <= 1 && !edge; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (!gen.Contains(new Vector2s((short)(z.x + dx), (short)(z.y + dy)))) { edge = true; break; }
                if (edge) { depth[z] = 1; queue.Enqueue(z); }
            }
            while (queue.Count > 0)
            {
                var z = queue.Dequeue(); int d = depth[z];
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var n = new Vector2s((short)(z.x + dx), (short)(z.y + dy));
                        if (gen.Contains(n) && !depth.ContainsKey(n)) { depth[n] = d + 1; queue.Enqueue(n); }
                    }
            }
            return depth;
        }

        public static bool IsExplored(float wx, float wz)
        {
            if (mask == null) return false;
            int x = Mathf.RoundToInt(wx / pixelSize + half);
            int y = Mathf.RoundToInt(wz / pixelSize + half);
            if (x < 0 || y < 0 || x >= size || y >= size) return false;
            return mask[y * size + x] != 0;
        }

        // Any explored cell inside a world rectangle? (Used to decide whether a close-zoom tile is worth rendering.)
        public static bool AnyExplored(float minX, float minZ, float maxX, float maxZ)
        {
            if (mask == null) return false;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(minX / pixelSize + half), 0, size - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(maxX / pixelSize + half), 0, size - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(minZ / pixelSize + half), 0, size - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(maxZ / pixelSize + half), 0, size - 1);
            for (int y = y0; y <= y1; y++)
            {
                int row = y * size;
                for (int x = x0; x <= x1; x++) if (mask[row + x] != 0) return true;
            }
            return false;
        }

        // Walk every explored cell (startup: queue the close-zoom tiles that should already exist).
        public static void ForEachExplored(Action<float, float> action, int stride = 1)
        {
            if (mask == null) return;
            for (int y = 0; y < size; y += stride)
                for (int x = 0; x < size; x += stride)
                    if (mask[y * size + x] != 0) action((x - half) * pixelSize, (y - half) * pixelSize);
        }

        public static float ExploredPercent()
        {
            // cells inside the 10 km world circle
            double worldCells = Math.PI * Math.Pow(10000.0 / pixelSize, 2);
            return (float)(100.0 * exploredCount / worldCells);
        }

        public static int ExploredCells => exploredCount;
    }
}
