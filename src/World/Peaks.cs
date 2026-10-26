using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace MuninMap.World
{
    // The world's mountain tops, from the world generator (terraforming aside): the land sampled every
    // 32 m, the highest point of each hill looked for again every 4 m, and of two tops closer than
    // 500 m only the higher kept. Once per world on a worker thread (a few seconds), then from
    // peaks.txt beside the world's map data. Markers shows them on explored ground only.
    internal static class Peaks
    {
        public struct Peak { public float x, z, height; }   // height: metres above the sea

        private const int Step = 32, Keep = 60;
        private const float MinAboveSea = 60f, Apart = 500f;
        private static volatile List<Peak> peaks = new List<Peak>();

        public static List<Peak> All => peaks;

        public static void Start(string worldDir)
        {
            var wg = WorldGenerator.instance;
            if (wg == null) return;
            int seed = wg.GetSeed();
            float sea = 30f;
            try { if (ZoneSystem.instance != null) sea = ZoneSystem.instance.m_waterLevel; } catch { }
            string path = Path.Combine(worldDir, "peaks.txt");
            var cached = Load(path, seed);
            if (cached != null) { peaks = cached; return; }
            Task.Run(() =>
            {
                try
                {
                    var t0 = DateTime.UtcNow;
                    var found = Find(wg, sea);
                    peaks = found;
                    Save(path, seed, found);
                    ZLog.Log($"MuninMap: found {found.Count} peaks in {(DateTime.UtcNow - t0).TotalSeconds:0.0}s (highest {(found.Count > 0 ? found[0].height : 0):0} m)");
                }
                catch (Exception e) { ZLog.LogWarning("MuninMap: peaks: " + e.Message); }
            });
        }

        private static float HeightAt(WorldGenerator wg, float x, float z)
        {
            var b = wg.GetBiome(x, z);
            return wg.GetBiomeHeight(b, x, z, out Color _);
        }

        private static List<Peak> Find(WorldGenerator wg, float sea)
        {
            const float R = 10000f;
            int n = (int)(2 * R / Step) + 1;
            var h = new float[n * n];
            for (int iz = 0; iz < n; iz++)
                for (int ix = 0; ix < n; ix++)
                {
                    float x = -R + ix * Step, z = -R + iz * Step;
                    h[iz * n + ix] = x * x + z * z > R * R ? -1000f : HeightAt(wg, x, z);
                }
            // a top: higher than everything within 3 samples (~100 m) and well above the sea
            var tops = new List<Peak>();
            for (int iz = 3; iz < n - 3; iz++)
                for (int ix = 3; ix < n - 3; ix++)
                {
                    float v = h[iz * n + ix];
                    if (v < sea + MinAboveSea) continue;
                    bool top = true;
                    for (int dz = -3; dz <= 3 && top; dz++)
                        for (int dx = -3; dx <= 3; dx++)
                            if ((dx != 0 || dz != 0) && h[(iz + dz) * n + ix + dx] > v) { top = false; break; }
                    if (!top) continue;
                    // the summit itself, near the sample
                    float bx = -R + ix * Step, bz = -R + iz * Step, best = v;
                    for (int dz = -8; dz <= 8; dz++)
                        for (int dx = -8; dx <= 8; dx++)
                        {
                            float x = -R + ix * Step + dx * 4, z = -R + iz * Step + dz * 4, hh = HeightAt(wg, x, z);
                            if (hh > best) { best = hh; bx = x; bz = z; }
                        }
                    tops.Add(new Peak { x = bx, z = bz, height = best - sea });
                }
            tops.Sort((a, b) => b.height.CompareTo(a.height));
            var kept = new List<Peak>();
            foreach (var p in tops)
            {
                bool near = false;
                foreach (var k in kept) if ((k.x - p.x) * (k.x - p.x) + (k.z - p.z) * (k.z - p.z) < Apart * Apart) { near = true; break; }
                if (near) continue;
                kept.Add(p);
                if (kept.Count >= Keep) break;
            }
            return kept;
        }

        private static List<Peak> Load(string path, int seed)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var lines = File.ReadAllLines(path);
                if (lines.Length == 0 || lines[0] != "v1 " + seed) return null;
                var list = new List<Peak>();
                for (int i = 1; i < lines.Length; i++)
                {
                    var p = lines[i].Split(' ');
                    if (p.Length != 3) continue;
                    list.Add(new Peak
                    {
                        x = float.Parse(p[0], CultureInfo.InvariantCulture),
                        z = float.Parse(p[1], CultureInfo.InvariantCulture),
                        height = float.Parse(p[2], CultureInfo.InvariantCulture),
                    });
                }
                return list;
            }
            catch { return null; }
        }

        private static void Save(string path, int seed, List<Peak> list)
        {
            try
            {
                var lines = new List<string> { "v1 " + seed };
                foreach (var p in list) lines.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.0} {1:0.0} {2:0.0}", p.x, p.z, p.height));
                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: could not write peaks.txt: " + e.Message); }
        }
    }
}
