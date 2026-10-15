using System;
using System.IO;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace MuninMap.Util
{
    // Map and overlay tiles are served as lossless WebP, through ImageSharp (pure managed,
    // merged into MuninMap.dll). Same pixels as PNG, 25-35% fewer bytes, which is what a server
    // on a home upload line runs out of first. Tiles are rendered as PNG (quick, so a new
    // one shows at once) and converted in the background. Height tiles stay PNG (the 3D view
    // decodes those).
    //
    // If the library can't load on some runtime the first call says so once, WebP stays off and
    // tiles are PNG exactly as before.
    internal static class WebP
    {
        private static volatile bool broken;
        public static bool Enabled => MuninConfig.TILE_WEBP && !broken;

        public static bool IsWebp(byte[] d) =>
            d != null && d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P';

        // null when WebP is off or failed
        public static byte[] FromPng(byte[] png) => Enabled ? Guard(() => Reencode(png, false)) : null;
        // for the rare browser that doesn't take WebP; works with tile_webp off too, for tiles stored earlier
        public static byte[] ToPng(byte[] webp) => Guard(() => Reencode(webp, true));

        private static byte[] Guard(Func<byte[]> f)
        {
            if (broken) return null;
            try { return f(); }
            catch (Exception e) when (e is TypeLoadException || e is FileNotFoundException || e is FileLoadException
                                      || e is MissingMethodException || e is MissingFieldException || e is BadImageFormatException
                                      || e is TypeInitializationException)
            {
                broken = true;
                ZLog.LogWarning("MuninMap: WebP tiles are off, the ImageSharp library did not load (" + e.GetType().Name + ": " + e.Message + "). Using PNG.");
                return null;
            }
        }

        // Level0 is the quickest setting: a tile in ~40-150 ms and within ~4% of the slowest one's size.
        // Clear: invisible (alpha 0) pixels may lose their colour, which nobody can see.
        private static readonly WebpEncoder encoder = new WebpEncoder
        {
            FileFormat = WebpFileFormatType.Lossless,
            Method = WebpEncodingMethod.Level0,
            TransparentColorMode = WebpTransparentColorMode.Clear
        };

        // kept out of line so a missing library fails inside Guard
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte[] Reencode(byte[] data, bool toPng)
        {
            using (var img = Image.Load<Rgba32>(data))
            using (var ms = new MemoryStream(data.Length * 2))
            {
                if (toPng) img.SaveAsPng(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.BestSpeed });
                else img.SaveAsWebp(ms, encoder);
                return ms.ToArray();
            }
        }
    }
}
