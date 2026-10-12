// The biome under the cursor, shown beside the coordinates like the game's map does. From the
// server's grid of biome numbers (data/biomes.grid, explored ground only), fetched the first time
// the mouse moves and again when exploration may have grown (a 304 when it hasn't).
import { worldTile } from './crs.js';

const N = 1024;   // grid cells a side (~21 m each), BiomeMap.GRID

export class BiomeHover {
  constructor() { this.grid = null; this.names = []; this.loading = false; this.loadedAt = 0; }

  // the biome name at world x, z: '' while loading, in the fog, or past the world's edge
  at(x, z) {
    if (!this.loading && Date.now() - this.loadedAt > 120000) this.load();
    if (!this.grid) return '';
    const col = Math.floor((x / this.size + 0.5) * N), row = Math.floor((0.5 - z / this.size) * N);   // row 0 is the north edge
    if (col < 0 || row < 0 || col >= N || row >= N) return '';
    return this.names[this.grid[row * N + col]] || '';
  }

  async load() {
    this.loading = true;
    try {
      const d = await (await fetch('data/biomes.json', { cache: 'no-store' })).json();
      if (!d.ready) return;
      const names = Object.keys(d.colours || {});   // in the server's order: grid value k is names[k - 1]
      // the server's grid of biome numbers (BiomeMap.GridFor): N x N bytes, read as they come
      const r = await fetch(worldTile('data/biomes.grid'), { cache: 'no-cache' });
      if (!r.ok) return;
      const grid = new Uint8Array(await r.arrayBuffer());
      if (grid.length !== N * N) return;
      this.grid = grid; this.names = ['', ...names]; this.size = d.world || 21504;
    } catch (e) { /* no biome picture on this server: the slot stays empty */ }
    finally { this.loading = false; this.loadedAt = Date.now(); }
  }
}
