using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using MuninMap.Tiles;
using MuninMap.Util;

namespace MuninMap.World
{
    // The world's biomes as one picture, for the page's "Biomes" map style: SIZE x SIZE cells over the
    // map's square (TileMath.WORLD_SIZE), one solid colour per biome, north up. Worked out once from
    // the world's own generator (the seed decides it, so it never changes) a few rows per frame, and
    // kept as biomes.png beside the world's map data. The big regions get a name label at a spot well
    // inside them; the page shows a label only once its spot is explored.
    internal static class BiomeMap
    {
        public const int SIZE = 2048;                      // ~10.5 m a cell
        private const int FORMAT = 2;   // 2: + the alt biomes (biomes.alt, biomes.altnames)
        public static volatile byte[] Png;                  // the whole world, null until ready
        private static byte[] cells;                        // index into Order per cell (255: none), for masking by the fog
        private static byte[] mist;                         // GRID x GRID, how thick the Mistlands mist is (0..255)
        // The game's alt biomes (its biome variants: "Ruin Black Forest", "Root Black Forest"...):
        // per cell, 0 or 1 + an index into altNames, the game's own name for that variant
        private static byte[] alt;
        private static List<string> altNames = new List<string>();
        public const int ALT_BASE = 32;                     // biomes.grid: values from here are alt biomes
        private static byte[] mistPng; private static int mistAt = -1; private static DateTime mistTime;
        private static byte[] masked; private static int maskedAt = -1; private static DateTime maskedTime;
        private static readonly object maskLock = new object();
        private static List<Label> labels = new List<Label>();
        private struct Label { public string name; public float x, z, area; }

        public static readonly int[] Order = { Palette.B_MEADOWS, Palette.B_BLACKFOREST, Palette.B_SWAMP, Palette.B_MOUNTAIN, Palette.B_PLAINS,
                                               Palette.B_MISTLANDS, Palette.B_ASHLANDS, Palette.B_DEEPNORTH, Palette.B_OCEAN };
        // solid, clearly different colours (the page's key uses the same)
        public static Palette.Rgb Colour(int b)
        {
            switch (b)
            {
                case Palette.B_MEADOWS: return new Palette.Rgb(134, 186, 72);
                case Palette.B_BLACKFOREST: return new Palette.Rgb(46, 92, 56);
                case Palette.B_SWAMP: return new Palette.Rgb(120, 96, 62);
                case Palette.B_MOUNTAIN: return new Palette.Rgb(214, 220, 228);
                case Palette.B_PLAINS: return new Palette.Rgb(222, 196, 88);
                case Palette.B_MISTLANDS: return new Palette.Rgb(98, 104, 120);
                case Palette.B_ASHLANDS: return new Palette.Rgb(176, 64, 42);
                case Palette.B_DEEPNORTH: return new Palette.Rgb(170, 214, 236);
                case Palette.B_OCEAN: return new Palette.Rgb(32, 72, 118);
                default: return new Palette.Rgb(40, 40, 40);
            }
        }

        public static void Start(string worldDataPath)
        {
            string path = Path.Combine(worldDataPath, "biomes.png"), meta = Path.Combine(worldDataPath, "biomes.txt");
            try
            {
                if (File.Exists(path) && File.Exists(meta))
                {
                    var lines = File.ReadAllLines(meta);
                    if (lines.Length > 0 && lines[0] == $"{FORMAT} {SIZE} {TileMath.WORLD_SIZE}")
                    {
                        LoadLabels(lines);
                        string raw = Path.Combine(Path.GetDirectoryName(path), "biomes.cells");
                        byte[] c = File.Exists(raw) ? File.ReadAllBytes(raw) : null;
                        string mp = Path.Combine(Path.GetDirectoryName(path), "biomes.mist");
                        byte[] m = File.Exists(mp) ? File.ReadAllBytes(mp) : null;
                        string ap = Path.Combine(Path.GetDirectoryName(path), "biomes.alt"), an = Path.Combine(Path.GetDirectoryName(path), "biomes.altnames");
                        byte[] a = File.Exists(ap) ? File.ReadAllBytes(ap) : null;
                        if (c != null && c.Length == SIZE * SIZE && m != null && m.Length == GRID * GRID && a != null && a.Length == SIZE * SIZE && File.Exists(an))
                        {
                            cells = c; mist = m; alt = a; altNames = new List<string>(File.ReadAllLines(an)); Png = File.ReadAllBytes(path); Warm(); return;
                        }
                    }
                }
            }
            catch { }
            StaticCoroutine.Start(Build(path, meta));
        }

        private static IEnumerator Build(string path, string meta)
        {
            yield return new WaitForSeconds(5f);
            // the alt biomes are worked out by the game after the world loads: wait for them (a while at most)
            for (int i = 0; i < 120 && !AltReady(); i++) yield return new WaitForSeconds(1f);
            bool altOk = AltReady();
            if (!altOk) ZLog.LogWarning("MuninMap: the game's alt biomes were not ready: the biome map is built without them");
            var started = Time.realtimeSinceStartup;
            var alt = new byte[SIZE * SIZE];
            var altNames = new List<string>();
            var altOf = new Dictionary<BiomeSector, byte>();   // a sector's alt index + 1 (0: none)
            var cells = new byte[SIZE * SIZE];   // index into Order (255: none)
            var mist = new byte[GRID * GRID];
            int mf = SIZE / GRID;
            float span = (float)TileMath.WORLD_SIZE / SIZE, half = TileMath.WORLD_HALF;
            var index = new Dictionary<int, byte>();
            for (int i = 0; i < Order.Length; i++) index[Order[i]] = (byte)i;
            for (int row = 0; row < SIZE; row++)
            {
                float wz = half - (row + 0.5f) * span;   // row 0 is the north edge
                for (int col = 0; col < SIZE; col++)
                {
                    float wx = -half + (col + 0.5f) * span;
                    int b = Palette.B_NONE;
                    if (wx * wx + wz * wz < 10500f * 10500f)
                        try { b = (int)WorldGenerator.instance.GetBiome(wx, wz); } catch { }
                    cells[row * SIZE + col] = index.TryGetValue(b, out byte k) ? k : (byte)255;
                    if (altOk && b != Palette.B_NONE && b != Palette.B_OCEAN)
                        try { alt[row * SIZE + col] = AltOf(WorldGenerator.instance.GetBiomeSector(wx, wz), altOf, altNames); } catch { }
                    if (b == Palette.B_MISTLANDS && row % mf == mf / 2 && col % mf == mf / 2)
                        try { mist[(row / mf) * GRID + col / mf] = MistAt(wx, wz); } catch { }
                }
                if (row % 24 == 23) yield return null;   // a few rows a frame: the server keeps running smoothly
            }
            var px = new byte[SIZE * SIZE * 4];
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == 255) continue;
                var c = Colour(Order[cells[i]]);
                px[i * 4] = (byte)c.r; px[i * 4 + 1] = (byte)c.g; px[i * 4 + 2] = (byte)c.b; px[i * 4 + 3] = 255;
            }
            byte[] png = Util.Png.Encode(px, SIZE, SIZE, Util.Png.Format.RGBA);
            var found = FindLabels(cells, span, half);
            try
            {
                File.WriteAllBytes(path, png);
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path), "biomes.cells"), cells);   // the grid itself, for masking by the fog
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path), "biomes.mist"), mist);
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path), "biomes.alt"), alt);
                File.WriteAllLines(Path.Combine(Path.GetDirectoryName(path), "biomes.altnames"), altNames);
                var sb = new System.Text.StringBuilder();
                sb.Append(FORMAT).Append(' ').Append(SIZE).Append(' ').Append(TileMath.WORLD_SIZE).Append('\n');
                foreach (var l in found) sb.Append(l.name).Append('\t').Append(l.x.ToString("F0")).Append('\t').Append(l.z.ToString("F0")).Append('\t').Append(l.area.ToString("F0")).Append('\n');
                File.WriteAllText(meta, sb.ToString());
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: biomes.png: " + e.Message); }
            labels = found;
            BiomeMap.cells = cells;
            BiomeMap.mist = mist;
            BiomeMap.alt = alt; BiomeMap.altNames = altNames;
            Png = png;
            Warm();
            ZLog.Log($"MuninMap: biome map built ({SIZE}x{SIZE}, {found.Count} named regions, {altNames.Count} kinds of alt biome: {string.Join(", ", altNames)}) in {Time.realtimeSinceStartup - started:F1} s");
        }

        private static bool AltReady()
        {
            try { var d = WorldGenerator.instance?.m_world?.m_biomeData; return d != null && d.IsReady; } catch { return false; }
        }

        // a sector's alt biome, numbered by its name (sectors with the same variants share one)
        private static byte AltOf(BiomeSector sec, Dictionary<BiomeSector, byte> altOf, List<string> names)
        {
            if (sec == null || sec.AltBiomes == null || sec.AltBiomes.Count == 0) return 0;
            if (altOf.TryGetValue(sec, out byte id)) return id;
            string name = AltName(sec);
            int i = names.IndexOf(name);
            if (i < 0 && names.Count < 255 - ALT_BASE) { names.Add(name); i = names.Count - 1; }
            id = i < 0 ? (byte)0 : (byte)(i + 1);
            altOf[sec] = id;
            return id;
        }

        // the variant's name as the game's world tools list it ("Ruin Black Forest"; "Bones" gets its
        // biome: "Bones Swamp"), several on one sector joined: "Root Black Forest, Ruin Black Forest".
        // (The game's own GetName() adds words a dedicated server has no text for, so it gives just
        // the biome there.)
        private static string AltName(BiomeSector sec)
        {
            string biome = Palette.BiomeName((int)sec.Biome);
            var parts = new List<string>();
            foreach (var a in sec.AltBiomes)
            {
                string n = (a.m_name ?? "").Trim();
                if (n.Length == 0) continue;
                if (n.IndexOf(biome, StringComparison.OrdinalIgnoreCase) < 0) n += " " + biome;
                if (!parts.Contains(n)) parts.Add(n);
            }
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : biome;
        }

        // the big regions of each biome (not the ocean): connected cells on a coarser grid, each named at
        // the cell inside it farthest from its edge (well inside, not at a centroid that may fall outside)
        private static List<Label> FindLabels(byte[] cells, float span, float half)
        {
            const int C = 512; int f = SIZE / C; float cs = span * f;
            var g = new byte[C * C];
            for (int y = 0; y < C; y++) for (int x = 0; x < C; x++) g[y * C + x] = cells[(y * f + f / 2) * SIZE + x * f + f / 2];
            var seen = new bool[C * C]; var outList = new List<Label>();
            var q = new Queue<int>(); var comp = new List<int>();
            for (int s = 0; s < g.Length; s++)
            {
                if (seen[s] || g[s] == 255 || Order[g[s]] == Palette.B_OCEAN) { seen[s] = true; continue; }
                byte k = g[s]; comp.Clear(); q.Enqueue(s); seen[s] = true;
                while (q.Count > 0)
                {
                    int i = q.Dequeue(); comp.Add(i); int x = i % C, y = i / C;
                    if (x > 0 && !seen[i - 1] && g[i - 1] == k) { seen[i - 1] = true; q.Enqueue(i - 1); }
                    if (x < C - 1 && !seen[i + 1] && g[i + 1] == k) { seen[i + 1] = true; q.Enqueue(i + 1); }
                    if (y > 0 && !seen[i - C] && g[i - C] == k) { seen[i - C] = true; q.Enqueue(i - C); }
                    if (y < C - 1 && !seen[i + C] && g[i + C] == k) { seen[i + C] = true; q.Enqueue(i + C); }
                }
                float area = comp.Count * cs * cs;
                if (area < 1.2e6f) continue;   // only regions over ~1.2 km2 get a name
                // the cell farthest from the region's edge, from a distance-to-edge pass
                var inside = new HashSet<int>(comp); int best = comp[0], bestD = -1;
                var dist = new Dictionary<int, int>(); var bq = new Queue<int>();
                foreach (int i in comp)
                {
                    int x = i % C, y = i / C;
                    bool edge = x == 0 || y == 0 || x == C - 1 || y == C - 1 || !inside.Contains(i - 1) || !inside.Contains(i + 1) || !inside.Contains(i - C) || !inside.Contains(i + C);
                    if (edge) { dist[i] = 0; bq.Enqueue(i); }
                }
                while (bq.Count > 0)
                {
                    int i = bq.Dequeue(); int d = dist[i];
                    if (d > bestD) { bestD = d; best = i; }
                    foreach (int n in new[] { i - 1, i + 1, i - C, i + C })
                        if (inside.Contains(n) && !dist.ContainsKey(n)) { dist[n] = d + 1; bq.Enqueue(n); }
                }
                int bx = best % C, by = best / C;
                outList.Add(new Label { name = Palette.BiomeName(Order[k]), x = -half + (bx + 0.5f) * cs, z = half - (by + 0.5f) * cs, area = area });
            }
            return outList;
        }

        // The picture the page gets: only explored ground shows (the rest clear, under the fog anyway), so
        // the world's layout can't be read off the file. Remade when exploration grew, at most once a minute.
        public static byte[] PngFor()
        {
            if (Png == null) return null;
            if (MuninConfig.REVEAL_ALL || cells == null) return Png;
            lock (maskLock)
            {
                int now = Fog.ExploredCells;
                if (masked != null && (now == maskedAt || (DateTime.UtcNow - maskedTime).TotalSeconds < 60)) return masked;   // the web server's thread: no Unity clock here
                float span = (float)TileMath.WORLD_SIZE / SIZE, half = TileMath.WORLD_HALF;
                var px = new byte[SIZE * SIZE * 4];
                var col = new Palette.Rgb[Order.Length];
                for (int i = 0; i < Order.Length; i++) col[i] = Colour(Order[i]);
                for (int y = 0; y < SIZE; y++)
                {
                    float wz = half - (y + 0.5f) * span;
                    for (int x = 0; x < SIZE; x++)
                    {
                        int k = cells[y * SIZE + x];
                        if (k == 255 || !Fog.IsExplored(-half + (x + 0.5f) * span, wz)) continue;
                        int o = (y * SIZE + x) * 4; var c = col[k];
                        px[o] = (byte)c.r; px[o + 1] = (byte)c.g; px[o + 2] = (byte)c.b; px[o + 3] = 255;
                    }
                }
                masked = Util.Png.Encode(px, SIZE, SIZE, Util.Png.Format.RGBA);
                maskedAt = now; maskedTime = DateTime.UtcNow;
                return masked;
            }
        }

        // The same, as a grid of biome numbers for the page's "biome under the cursor": GRID x GRID
        // bytes, row 0 the north edge, each Order index + 1, or ALT_BASE + an alt biome (0: unexplored or off the world). The
        // page reads it as is (decoding the picture there meant a canvas read-back, slow in some
        // browsers); it compresses to a few tens of KB.
        public const int GRID = 1024;
        private static byte[] grid; private static int gridAt = -1; private static DateTime gridTime;
        public static byte[] GridFor()
        {
            if (cells == null) return null;
            lock (maskLock)
            {
                int now = Fog.ExploredCells;
                if (grid != null && (MuninConfig.REVEAL_ALL || now == gridAt || (DateTime.UtcNow - gridTime).TotalSeconds < 60)) return grid;
                int f = SIZE / GRID;
                float span = (float)TileMath.WORLD_SIZE / SIZE, half = TileMath.WORLD_HALF;
                var g = new byte[GRID * GRID];
                for (int y = 0; y < GRID; y++)
                {
                    int cy = y * f + f / 2;
                    float wz = half - (cy + 0.5f) * span;
                    for (int x = 0; x < GRID; x++)
                    {
                        int cx = x * f + f / 2, k = cells[cy * SIZE + cx];
                        if (k == 255 || (!MuninConfig.REVEAL_ALL && !Fog.IsExplored(-half + (cx + 0.5f) * span, wz))) continue;
                        int a = alt != null ? alt[cy * SIZE + cx] : 0;
                        g[y * GRID + x] = a > 0 ? (byte)(ALT_BASE + a - 1) : (byte)(k + 1);
                    }
                }
                grid = g; gridAt = now; gridTime = DateTime.UtcNow;
                return grid;
            }
        }

        // any mist on the game's map within r metres of (x, z)
        public static bool HasMist(float x, float z, float r)
        {
            var m = mist;
            if (m == null) return false;
            float cell = (float)TileMath.WORLD_SIZE / GRID, half = TileMath.WORLD_HALF;
            int x0 = Mathf.FloorToInt((x - r + half) / cell), x1 = Mathf.FloorToInt((x + r + half) / cell);
            int y0 = Mathf.FloorToInt((half - z - r) / cell), y1 = Mathf.FloorToInt((half - z + r) / cell);
            for (int y = Math.Max(0, y0); y <= Math.Min(GRID - 1, y1); y++)
                for (int xx = Math.Max(0, x0); xx <= Math.Min(GRID - 1, x1); xx++)
                    if (m[y * GRID + xx] != 0) return true;
            return false;
        }

        // the page's mist picture made ahead on a pool thread, so the first page to ask doesn't wait for it
        private static void Warm() => System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { MistPngFor(); } catch { } });

        // How thick the mist is on the game's own map at a Mistlands spot (Minimap.GetMaskColor, its
        // green): all of it, thinning to none where the forest factor runs from 1.1 to 1.3.
        private static byte MistAt(float wx, float wz)
        {
            float ff = WorldGenerator.GetForestFactor(new Vector3(wx, 0f, wz));
            float t = Mathf.Clamp01((ff - 1.1f) / 0.2f);
            return (byte)Mathf.RoundToInt((1f - t * t * (3f - 2f * t)) * 255f);
        }

        // The mist as a GRID x GRID gray picture for the page (row 0 the north edge), explored ground
        // only like the others, remade at most once a minute as exploration grows. Mostly black: a few KB.
        public static byte[] MistPngFor()
        {
            if (mist == null) return null;
            lock (maskLock)
            {
                int now = Fog.ExploredCells;
                if (mistPng != null && (MuninConfig.REVEAL_ALL || now == mistAt || (DateTime.UtcNow - mistTime).TotalSeconds < 60)) return mistPng;
                int f = SIZE / GRID;
                float span = (float)TileMath.WORLD_SIZE / SIZE, half = TileMath.WORLD_HALF;
                // explored cells, grown by two (~40 m) so the mist runs on under the fog's soft edge
                // instead of stopping in steps short of it
                var seen = new bool[GRID * GRID];
                for (int y = 0; y < GRID; y++)
                {
                    float wz = half - (y * f + f / 2 + 0.5f) * span;
                    for (int x = 0; x < GRID; x++)
                    {
                        if (mist[y * GRID + x] == 0 || (!MuninConfig.REVEAL_ALL && !Fog.IsExplored(-half + (x * f + f / 2 + 0.5f) * span, wz))) continue;
                        for (int dy = -2; dy <= 2; dy++)
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                int yy = y + dy, xx = x + dx;
                                if (yy >= 0 && xx >= 0 && yy < GRID && xx < GRID) seen[yy * GRID + xx] = true;
                            }
                    }
                }
                var g = new byte[GRID * GRID];
                for (int i = 0; i < g.Length; i++) if (seen[i]) g[i] = mist[i];
                mistPng = Util.Png.Encode(g, GRID, GRID, Util.Png.Format.Gray8);
                mistAt = now; mistTime = DateTime.UtcNow;
                return mistPng;
            }
        }

        private static void LoadLabels(string[] lines)
        {
            var l = new List<Label>();
            for (int i = 1; i < lines.Length; i++)
            {
                var t = lines[i].Split('\t');
                if (t.Length == 4 && float.TryParse(t[1], out float x) && float.TryParse(t[2], out float z) && float.TryParse(t[3], out float a))
                    l.Add(new Label { name = t[0], x = x, z = z, area = a });
            }
            labels = l;
        }

        // the named regions whose label spot is explored (or all with reveal_all), for the page
        public static string LabelsJson()
        {
            var j = new JsonWriter(4096);
            j.BeginObject().Prop("ready", Png != null).Prop("size", SIZE).Prop("world", TileMath.WORLD_SIZE).Key("labels").BeginArray();
            foreach (var l in labels)
            {
                if (!MuninConfig.REVEAL_ALL && !Fog.IsExplored(l.x, l.z)) continue;
                j.BeginObject().Prop("name", l.name).Prop("x", l.x, 0).Prop("z", l.z, 0).Prop("area", l.area, 0).End();
            }
            // the alt biomes by name: biomes.grid value ALT_BASE + i is alt[i]
            j.End().Prop("altBase", ALT_BASE).Key("alt").BeginArray();
            foreach (var n in altNames) j.Value(n);
            j.End().Key("colours").BeginObject();
            foreach (int b in Order) { var c = Colour(b); j.Prop(Palette.BiomeName(b), $"#{c.r:x2}{c.g:x2}{c.b:x2}"); }
            j.End().End();
            return j.ToString();
        }
    }
}
