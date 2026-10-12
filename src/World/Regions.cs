using System;
using System.Collections.Generic;
using System.Text;
using MuninMap.Tiles;

namespace MuninMap.World
{
    // Structures and ruins chunks bundled 8x8 to a region (2 km square), so opening the map is a
    // handful of requests instead of hundreds: a browser only opens ~6 connections to a plain-HTTP
    // server, and on a slow upload each small request waits its turn. A region's rev is a hash of
    // its listed chunks' revs, so data/.../r/rx_rz.json?h=rev can be cached for good, like chunks.
    internal static class Regions
    {
        public const int SIZE = 8;   // chunks per side for structures and ruins (vegetation uses smaller ones)
        public static int Count(int size = SIZE) => (TileMath.ChunksPerSide + size - 1) / size;

        // revOf: the chunk's rev when it exists and is listed (explored), else null
        public static int Hash(int rx, int rz, Func<int, int, int?> revOf, int size = SIZE)
        {
            uint h = 2166136261u; bool any = false;
            for (int cz = rz * size; cz < Math.Min(rz * size + size, TileMath.ChunksPerSide); cz++)
                for (int cx = rx * size; cx < Math.Min(rx * size + size, TileMath.ChunksPerSide); cx++)
                {
                    int? rev = revOf(cx, cz);
                    if (rev == null) continue;
                    any = true;
                    foreach (int v in new[] { cx, cz, rev.Value })
                        for (int b = 0; b < 32; b += 8) { h ^= (uint)((v >> b) & 0xff); h *= 16777619u; }
                }
            return any ? (int)(h & 0x7fffffff) : 0;
        }

        // [[rx, rz, rev], ...] for the index: every region with at least one listed chunk
        public static void WriteIndex(Util.JsonWriter j, Func<int, int, int?> revOf, int size = SIZE)
        {
            j.Key("regionSize").Value(size);
            j.Key("regions").BeginArray();
            for (int rz = 0; rz < Count(size); rz++)
                for (int rx = 0; rx < Count(size); rx++)
                {
                    int h = Hash(rx, rz, revOf, size);
                    if (h != 0) j.BeginArray().Value(rx).Value(rz).Value(h).End();
                }
            j.End();
        }

        // {"rx":..,"rz":..,"rev":..,"chunks":[<chunk json>, ...]}; null when the region has nothing listed
        public static string Json(int rx, int rz, Func<int, int, int?> revOf, Func<int, int, string> jsonOf, out int rev)
        {
            rev = Hash(rx, rz, revOf);
            if (rev == 0) return null;
            var sb = new StringBuilder(4096);
            sb.Append("{\"rx\":").Append(rx).Append(",\"rz\":").Append(rz).Append(",\"rev\":").Append(rev).Append(",\"chunks\":[");
            bool first = true;
            for (int cz = rz * SIZE; cz < rz * SIZE + SIZE; cz++)
                for (int cx = rx * SIZE; cx < rx * SIZE + SIZE; cx++)
                {
                    if (revOf(cx, cz) == null) continue;
                    string cj = jsonOf(cx, cz);
                    if (cj == null) continue;
                    if (!first) sb.Append(',');
                    sb.Append(cj); first = false;
                }
            sb.Append("]}");
            return sb.ToString();
        }

        public static bool Parse(string s, out int rx, out int rz, int size = SIZE)
        {
            rx = rz = 0;
            int us = s.IndexOf('_');
            return us > 0 && int.TryParse(s.Substring(0, us), out rx) && int.TryParse(s.Substring(us + 1), out rz)
                && rx >= 0 && rz >= 0 && rx < Count(size) && rz < Count(size);
        }
    }
}
