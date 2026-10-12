using System.Collections.Generic;
using UnityEngine;
using MuninMap.Tiles;

namespace MuninMap
{
    // The structures overlay (/structures, a 2048px PNG), kept so anything
    // built against the old endpoints keeps working. Fed by WorldSweep; the
    // real structure data now lives in World.Structures as vector chunks.
    internal static class StructureMap
    {
        private struct Cell { public int n, r, g, b; }
        private static Dictionary<int, Cell> cells = new Dictionary<int, Cell>();   // the sweep's, being filled
        private static volatile Dictionary<int, Cell> published;                     // the last finished sweep's
        private static readonly Dictionary<int, Color32> paletteCache = new Dictionary<int, Color32>();
        private static volatile byte[] png;
        private static volatile bool pngStale = true;
        private static readonly object encodeLock = new object();

        public static volatile bool RefreshRequested;   // legacy /structures/refresh; WorldSweep honours it
        public static int LastCount { get; private set; }
        public static int LastScanned { get; set; }

        private static Color32 MaterialOf(int prefabHash)
        {
            if (paletteCache.TryGetValue(prefabHash, out var cached)) return cached;
            string n = null;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
                if (go != null) n = go.name.ToLowerInvariant();
            } catch { }
            Color32 c;
            if (n == null)                                   c = new Color32(150, 120,  90, 255);
            else if (n.Contains("portal"))                   c = new Color32( 90, 200, 210, 255);
            else if (n.Contains("blackmarble"))              c = new Color32( 70,  70,  85, 255);
            else if (n.Contains("stone") || n.Contains("grausten")) c = new Color32(150, 150, 145, 255);
            else if (n.Contains("iron") || n.Contains("metal")) c = new Color32(120, 130, 145, 255);
            else if (n.Contains("darkwood"))                 c = new Color32( 90,  66,  46, 255);
            else if (n.Contains("roof") || n.Contains("straw") || n.Contains("thatch")) c = new Color32(196, 160,  86, 255);
            else if (n.Contains("fire") || n.Contains("hearth") || n.Contains("forge")) c = new Color32(214, 122,  58, 255);
            else                                             c = new Color32(150, 108,  66, 255);
            paletteCache[prefabHash] = c;
            return c;
        }

        public static void Begin()
        {
            cells = new Dictionary<int, Cell>();
            LastCount = 0;
        }

        public static void Observe(int prefabHash, int idx)
        {
            var mat = MaterialOf(prefabHash);
            cells.TryGetValue(idx, out Cell cell);
            cell.n++; cell.r += mat.r; cell.g += mat.g; cell.b += mat.b;
            cells[idx] = cell;
            LastCount++;
        }

        // Main thread, end of sweep: only hands the cells over. The picture (a 16 MB image) is made
        // when someone asks for it, off the game's thread.
        public static void Finish()
        {
            published = cells;
            pngStale = true;
        }

        private static byte[] Paint(Dictionary<int, Cell> cells)
        {
            int size = MuninConfig.TEXTURE_SIZE;
            var buf = new byte[size * size * 4];
            foreach (var kv in cells)
            {
                var c = kv.Value;
                if (c.n <= 0) continue;
                Put(buf, size, kv.Key, (byte)(c.r / c.n), (byte)(c.g / c.n), (byte)(c.b / c.n), (byte)Mathf.Clamp(120 + c.n * 20, 120, 255));
            }
            foreach (var kv in cells)
            {
                var c = kv.Value;
                byte a = (byte)Mathf.Clamp(55 + c.n * 10, 55, 140);
                int idx = kv.Key;
                foreach (int nb in new[] { idx - 1, idx + 1, idx - size, idx + size })
                {
                    if (nb < 0 || nb >= size * size || cells.ContainsKey(nb)) continue;
                    int o = Offset(size, nb);
                    if (buf[o + 3] >= a) continue;
                    Put(buf, size, nb, (byte)(c.r / c.n), (byte)(c.g / c.n), (byte)(c.b / c.n), a);
                }
            }
            return buf;
        }

        // legacy index: y * size + x with y growing north; PNG rows run north to south
        private static int Offset(int size, int idx)
        {
            int y = idx / size, x = idx % size;
            return ((size - 1 - y) * size + x) * 4;
        }
        private static void Put(byte[] buf, int size, int idx, byte r, byte g, byte b, byte a)
        {
            int o = Offset(size, idx);
            buf[o] = r; buf[o + 1] = g; buf[o + 2] = b; buf[o + 3] = a;
        }

        public static string GetStats() => World.Structures.StatsJson;

        public static byte[] GetPng()
        {
            var cells = published;
            if (cells == null) return new byte[0];
            if (!pngStale && png != null) return png;
            lock (encodeLock)
            {
                if (!pngStale && png != null) return png;
                pngStale = false;
                png = Util.Png.Encode(Paint(cells), MuninConfig.TEXTURE_SIZE, MuninConfig.TEXTURE_SIZE, Util.Png.Format.RGBA, fast: true);
                return png;
            }
        }
    }
}
