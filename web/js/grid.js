// The map's grid, shared with the server (Tiles/TileMath.cs): a square of WORLD_SIZE metres centred
// on the origin, in 256 m chunks. Kept apart from crs.js so the vegetation worker (no Leaflet) can
// use it too. 84 chunks reach past the world's edge at 10500 m (80 cut it off at 10240).
export const WORLD_SIZE = 21504;
export const WORLD_HALF = WORLD_SIZE / 2;
export const CHUNKS = WORLD_SIZE / 256;
