using System;
using System.IO;
using System.Threading;
using MuninMap.Models.Unity;

namespace MuninMap.Models
{
    // The game's curl noise, a 128 x 128 x 128 texture of half floats that the weapon glow shader swirls its
    // noise with (Unlit/WeaponGlow: _CurlTex). Read once from the game files on a background thread and kept
    // in the model library as a 512 x 512 picture: 64 slices of 64 x 64 (8 across, 8 down), red and green its
    // first and third channels (the two the shader uses), stored as (v + 1) / 4 so the game's -1..3 fits.
    internal static class NoiseExtractor
    {
        public const string FileName = "curlnoise.png";
        private const int Size = 64, Across = 8;
        private static int started;

        public static string PathIn(string hdDir) => Path.Combine(hdDir, FileName);

        public static void Ensure(string dataDir, string hdDir)
        {
            if (File.Exists(PathIn(hdDir)) || Interlocked.Exchange(ref started, 1) == 1) return;
            new Thread(() =>
            {
                try { Run(dataDir, hdDir); }
                catch (Exception e) { ZLog.LogWarning("MuninMap: curl noise not read: " + e.Message); }
            }) { IsBackground = true, Name = "MuninMap curl noise", Priority = ThreadPriority.BelowNormal }.Start();
        }

        private static void Run(string dataDir, string hdDir)
        {
            foreach (string path in TextureExtractor.AssetFiles(dataDir))
            {
                if (!TextureExtractor.IsBundle(path)) continue;
                using (var bundle = new BundleFile(path))
                    foreach (var node in bundle.Nodes)
                    {
                        if (node.Name.EndsWith(".resS", StringComparison.Ordinal) || node.Name.EndsWith(".resource", StringComparison.Ordinal) || node.Size < 48 || node.Size > 512L * 1024 * 1024) continue;
                        byte[] data;
                        try { data = bundle.ReadNode(node); } catch { continue; }
                        if (!SerializedFile.Looks(data)) continue;
                        SerializedFile sf;
                        try { sf = new SerializedFile(data); } catch { continue; }
                        foreach (var o in sf.Objects)
                        {
                            if (sf.ClassOf(o) != 117) continue;   // Texture3D
                            var d = sf.ReadObject(o);
                            if (d == null || !(d.TryGetValue("m_Name", out object nm) && (nm as string) == "CurlNoise")) continue;
                            int w = SerializedFile.AsInt(d, "m_Width"), h = SerializedFile.AsInt(d, "m_Height"), depth = SerializedFile.AsInt(d, "m_Depth");
                            if (!(d.TryGetValue("m_StreamData", out object so) && so is System.Collections.Generic.Dictionary<string, object> sd)) return;
                            long off = Convert.ToInt64(sd["offset"]); int size = Convert.ToInt32(sd["size"]);
                            string res = (sd["path"] as string ?? "");
                            res = res.Substring(res.LastIndexOf('/') + 1);
                            var resNode = bundle.Find(res);
                            if (resNode == null || size != w * h * depth * 8) return;   // (RGBA half floats expected)
                            byte[] raw = bundle.ReadRange(resNode.Offset + off, size);
                            Write(raw, w, h, depth, PathIn(hdDir));
                            ZLog.Log("MuninMap: curl noise read from the game files");
                            return;
                        }
                    }
            }
        }

        // averaged down to 64 cubed, the two channels the shader samples
        private static void Write(byte[] raw, int w, int h, int depth, string file)
        {
            int fx = w / Size, fy = h / Size, fz = depth / Size, side = Size * Across;
            var rgba = new byte[side * side * 4];
            for (int z = 0; z < Size; z++)
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                    {
                        float r = 0f, g = 0f; int n = 0;
                        for (int dz = 0; dz < fz; dz++)
                            for (int dy = 0; dy < fy; dy++)
                                for (int dx = 0; dx < fx; dx++)
                                {
                                    int i = (((z * fz + dz) * h + (y * fy + dy)) * w + (x * fx + dx)) * 8;
                                    r += Half(raw, i); g += Half(raw, i + 4); n++;
                                }
                        int px = (z % Across) * Size + x, py = (z / Across) * Size + y, o = (py * side + px) * 4;
                        rgba[o] = Byte(r / n); rgba[o + 1] = Byte(g / n); rgba[o + 2] = 0; rgba[o + 3] = 255;
                    }
            byte[] png = Util.Png.Encode(rgba, side, side, Util.Png.Format.RGBA, fast: true);
            string tmp = file + ".tmp";
            File.WriteAllBytes(tmp, png);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }
        private static byte Byte(float v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round((v + 1f) / 4f * 255f)));
        private static float Half(byte[] b, int i)
        {
            int hbits = b[i] | (b[i + 1] << 8);
            int sign = (hbits >> 15) & 1, exp = (hbits >> 10) & 31, man = hbits & 1023;
            float v = exp == 0 ? man / 1024f * (float)Math.Pow(2, -14) : exp == 31 ? 65504f : (1f + man / 1024f) * (float)Math.Pow(2, exp - 15);
            return sign == 1 ? -v : v;
        }
    }
}
