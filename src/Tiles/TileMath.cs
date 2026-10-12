using System;

namespace MuninMap.Tiles
{
    // The tile pyramid.
    //
    // The world is a square of WORLD_SIZE metres centred on the origin, which
    // contains all of Valheim's world: 10 km of land and sea and then the game's
    // 500 m edge band, so land in the far north and south reaches 10.5 km. Zoom MAX_ZOOM is one
    // pixel per metre -- the game's own heightmap resolution, so there is no
    // more detail to be had -- and every zoom below halves that. Tiles are
    // TILE_SIZE pixels square at every zoom; tile (0,0) is the north-west
    // corner (north is +z in Valheim, which is up on the map).
    //
    // The browser uses exactly the same numbers (see web/js/crs.js), so a tile
    // key here and a tile key there always mean the same square of ground.
    internal static class TileMath
    {
        public const int TILE_SIZE = 256;
        public const int MAX_ZOOM = 7;                 // 1 m / px
        public const int WORLD_SIZE = 21504;           // metres; 84 tiles at max zoom (was 20480: cut the world's edge at 10240)
        public const int WORLD_HALF = WORLD_SIZE / 2;
        public const int ZONE_SIZE = 64;               // Valheim zone edge in metres
        public const int CHUNK_SIZE = 256;             // vector data chunk edge in metres (= 1 tile at max zoom)

        public static float MetersPerPixel(int zoom) => (float)Math.Pow(2, MAX_ZOOM - zoom);

        public static float TileSpanMeters(int zoom) => TILE_SIZE * MetersPerPixel(zoom);

        public static int TilesPerSide(int zoom) => (int)Math.Ceiling(WORLD_SIZE / TileSpanMeters(zoom));

        public static bool Valid(int zoom, int x, int y)
        {
            if (zoom < 0 || zoom > MAX_ZOOM) return false;
            int n = TilesPerSide(zoom);
            return x >= 0 && y >= 0 && x < n && y < n;
        }

        // World-space bounds of a tile. minZ is the SOUTH edge (smaller z), maxZ the north.
        public static void TileBounds(int zoom, int x, int y, out float minX, out float minZ, out float maxX, out float maxZ)
        {
            float span = TileSpanMeters(zoom);
            minX = -WORLD_HALF + x * span;
            maxX = minX + span;
            maxZ = WORLD_HALF - y * span;
            minZ = maxZ - span;
        }

        public static void WorldToTile(int zoom, float wx, float wz, out int tx, out int ty)
        {
            float span = TileSpanMeters(zoom);
            tx = (int)Math.Floor((wx + WORLD_HALF) / span);
            ty = (int)Math.Floor((WORLD_HALF - wz) / span);
        }

        public static long Key(int zoom, int x, int y) => ((long)zoom << 40) | ((long)x << 20) | (uint)y;
        public static void Unkey(long key, out int zoom, out int x, out int y)
        {
            zoom = (int)(key >> 40); x = (int)((key >> 20) & 0xFFFFF); y = (int)(key & 0xFFFFF);
        }

        // Zones: Valheim zone (zx, zz) covers x in [zx*64-32, zx*64+32).
        public static int ZoneCoord(float w) => (int)Math.Floor((w + ZONE_SIZE / 2f) / ZONE_SIZE);
        public static long ZoneKey(int zx, int zz) => ((long)(zx + 32768) << 20) | (uint)(zz + 32768);
        public static void UnzoneKey(long key, out int zx, out int zz) { zx = (int)(key >> 20) - 32768; zz = (int)(key & 0xFFFFF) - 32768; }
        public static float ZoneCenter(int z) => z * ZONE_SIZE;

        // Vector-data chunks: 256 m squares aligned with max-zoom tiles.
        public static int ChunkCoord(float w) => (int)Math.Floor((w + WORLD_HALF) / CHUNK_SIZE);
        public static int ChunksPerSide => WORLD_SIZE / CHUNK_SIZE;   // 84
        public static float ChunkMin(int c) => -WORLD_HALF + c * CHUNK_SIZE;
    }
}
