using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;

namespace WebMap.Tiles
{
    // Player terraforming, decoded from the world.
    //
    // Every zone a player has dug, raised, levelled or paved in owns a
    // "_TerrainCompiler" object whose ZDO carries the modifications as a
    // compressed blob (ZDOVars.s_TCData): for each of the 65x65 heightmap
    // vertices a flag and two height deltas (level + smooth), then for each
    // vertex a flag and an RGBA paint colour (r = dirt, g = cultivated,
    // b = paved). The renderer adds the deltas to the generator's base height,
    // which is exactly what the game's Heightmap does, so roads, moats and
    // flattened bases render as they are in the world.
    //
    // Reading a ZDO is main-thread work; the world sweep calls Observe from
    // there and only decodes a blob when its data revision has changed. The
    // decoded patches are immutable arrays swapped into a concurrent map, so
    // renderer threads read them without locking.
    internal static class TerrainPatches
    {
        public const int W = 65;                  // vertices per zone edge
        public const int N = W * W;

        public sealed class Patch
        {
            public int zx, zz;
            public float[] delta;                 // N entries, level + smooth, 0 where unmodified
            public byte[] paint;                  // N*3 entries r,g,b in 0..255, or null when nothing is painted
            public bool anyHeight, anyPaint;
            public uint rev;
            public int hash;                      // cheap content hash, used for tile dirtiness
        }

        private static readonly ConcurrentDictionary<long, Patch> patches = new ConcurrentDictionary<long, Patch>();
        // zone key -> data revision last decoded, so an unchanged zone costs one dictionary lookup per sweep
        private static readonly ConcurrentDictionary<long, uint> seenRev = new ConcurrentDictionary<long, uint>();

        public static int Count => patches.Count;

        public static Patch Get(int zx, int zz)
        {
            patches.TryGetValue(TileMath.ZoneKey(zx, zz), out var p);
            return p;
        }

        // Main thread. Returns true when the zone's terrain changed since last time.
        public static bool Observe(ZDO zdo, UnityEngine.Vector3 pos)
        {
            int zx = TileMath.ZoneCoord(pos.x), zz = TileMath.ZoneCoord(pos.z);
            long key = TileMath.ZoneKey(zx, zz);
            uint rev = 0;
            try { rev = unchecked((uint)zdo.DataRevision); } catch { }
            if (seenRev.TryGetValue(key, out uint prev) && prev == rev && patches.ContainsKey(key)) return false;

            byte[] blob = null;
            try { blob = zdo.GetByteArray(ZDOVars.s_TCData); } catch { }
            if (blob == null || blob.Length == 0)
            {
                seenRev[key] = rev;
                return patches.TryRemove(key, out _);
            }

            Patch p;
            try { p = Decode(blob, zx, zz, rev); }
            catch (Exception e)
            {
                if (WebMapConfig.DEBUG) ZLog.LogWarning($"MuninMap: terrain blob for zone {zx},{zz} not understood: {e.Message}");
                seenRev[key] = rev;
                return false;
            }
            seenRev[key] = rev;
            // (a zone whose terrain data changes nothing has no patch: it is decoded again every
            // sweep, and used to count as changed every time, re-rendering its tiles for nothing)
            bool had = patches.TryGetValue(key, out var old), has = p.anyHeight || p.anyPaint;
            if (has) patches[key] = p; else patches.TryRemove(key, out _);
            return had != has || (has && old.hash != p.hash);
        }

        public static Patch Decode(byte[] blob, int zx, int zz, uint rev)
        {
            byte[] raw = Gunzip(blob);
            var p = new Patch { zx = zx, zz = zz, rev = rev, delta = new float[N] };
            int hash = 17;
            using (var br = new BinaryReader(new MemoryStream(raw)))
            {
                br.ReadInt32();                     // version
                br.ReadInt32();                     // operations
                br.ReadSingle(); br.ReadSingle(); br.ReadSingle();   // last op point
                br.ReadSingle();                    // last op radius
                int n = br.ReadInt32();
                if (n != N) throw new FormatException("height grid is " + n + " vertices, expected " + N);
                for (int i = 0; i < n; i++)
                {
                    if (br.ReadBoolean())
                    {
                        float level = br.ReadSingle();
                        float smooth = br.ReadSingle();
                        float d = level + smooth;
                        p.delta[i] = d;
                        if (d != 0f) { p.anyHeight = true; hash = hash * 31 + i; hash = hash * 31 + (int)(d * 100f); }
                    }
                }
                int m = br.ReadInt32();
                if (m != N) throw new FormatException("paint grid is " + m + " vertices, expected " + N);
                for (int i = 0; i < m; i++)
                {
                    if (br.ReadBoolean())
                    {
                        float r = br.ReadSingle(), g = br.ReadSingle(), b = br.ReadSingle();
                        br.ReadSingle();            // a
                        if (r > 0.01f || g > 0.01f || b > 0.01f)
                        {
                            if (p.paint == null) p.paint = new byte[N * 3];
                            p.paint[i * 3] = (byte)(Clamp01(r) * 255f);
                            p.paint[i * 3 + 1] = (byte)(Clamp01(g) * 255f);
                            p.paint[i * 3 + 2] = (byte)(Clamp01(b) * 255f);
                            p.anyPaint = true;
                            hash = hash * 31 + i; hash = hash * 31 + p.paint[i * 3] + p.paint[i * 3 + 1] * 7 + p.paint[i * 3 + 2] * 13;
                        }
                    }
                }
            }
            p.hash = hash;
            return p;
        }

        private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        private static byte[] Gunzip(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            using (var outp = new MemoryStream())
            {
                gz.CopyTo(outp);
                return outp.ToArray();
            }
        }

        // Renderer threads. Bilinear height delta at a world position; 0 where nothing was modified.
        public static float DeltaAt(float wx, float wz)
        {
            int zx = TileMath.ZoneCoord(wx), zz = TileMath.ZoneCoord(wz);
            if (!patches.TryGetValue(TileMath.ZoneKey(zx, zz), out var p) || !p.anyHeight) return 0f;
            float fx = wx - (zx * TileMath.ZONE_SIZE - 32);   // 0..64
            float fz = wz - (zz * TileMath.ZONE_SIZE - 32);
            return Bilinear(p.delta, fx, fz);
        }

        // Paint mask at a world position: r = dirt, g = cultivated, b = paved (0..1 each). Returns false when unpainted.
        public static bool PaintAt(float wx, float wz, out float dirt, out float cultivated, out float paved)
        {
            dirt = cultivated = paved = 0f;
            int zx = TileMath.ZoneCoord(wx), zz = TileMath.ZoneCoord(wz);
            if (!patches.TryGetValue(TileMath.ZoneKey(zx, zz), out var p) || !p.anyPaint) return false;
            float fx = wx - (zx * TileMath.ZONE_SIZE - 32);
            float fz = wz - (zz * TileMath.ZONE_SIZE - 32);
            int i0 = (int)fx, j0 = (int)fz;
            if (i0 < 0) i0 = 0; if (i0 > W - 2) i0 = W - 2;
            if (j0 < 0) j0 = 0; if (j0 > W - 2) j0 = W - 2;
            float tx = fx - i0, tz = fz - j0;
            if (tx < 0) tx = 0; if (tx > 1) tx = 1; if (tz < 0) tz = 0; if (tz > 1) tz = 1;
            int a = (j0 * W + i0) * 3, b = a + 3, c = a + W * 3, d = c + 3;
            byte[] pm = p.paint;
            dirt      = Lerp(Lerp(pm[a], pm[b], tx), Lerp(pm[c], pm[d], tx), tz) / 255f;
            cultivated= Lerp(Lerp(pm[a + 1], pm[b + 1], tx), Lerp(pm[c + 1], pm[d + 1], tx), tz) / 255f;
            paved     = Lerp(Lerp(pm[a + 2], pm[b + 2], tx), Lerp(pm[c + 2], pm[d + 2], tx), tz) / 255f;
            return dirt > 0.02f || cultivated > 0.02f || paved > 0.02f;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static float Bilinear(float[] g, float fx, float fz)
        {
            int i0 = (int)fx, j0 = (int)fz;
            if (i0 < 0) i0 = 0; if (i0 > W - 2) i0 = W - 2;
            if (j0 < 0) j0 = 0; if (j0 > W - 2) j0 = W - 2;
            float tx = fx - i0, tz = fz - j0;
            if (tx < 0) tx = 0; if (tx > 1) tx = 1; if (tz < 0) tz = 0; if (tz > 1) tz = 1;
            int a = j0 * W + i0;
            float top = g[a] + (g[a + 1] - g[a]) * tx;
            float bot = g[a + W] + (g[a + W + 1] - g[a + W]) * tx;
            return top + (bot - top) * tz;
        }
    }
}
