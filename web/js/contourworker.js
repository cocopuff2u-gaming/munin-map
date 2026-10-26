// Height tiles for layers/contours.js, off the main thread: fetched, decoded (Terrarium PNG ->
// metres), and where to write heights on the lines: for every 25 m level that crosses the tile,
// the crossing nearest the middle of each quarter of it. The lines themselves are drawn by the GPU.
// Request { id, url }; reply { id, h (Float32Array 256 x 256, row 0 north), labels: [px, py, level]... }
// or { id, empty } when there is no height tile (yet).
import { fetchTerrarium } from './png.js';

const TILE = 256, SEA = 30, LABEL_STEP = 25, GRID = 4;

self.onmessage = async (e) => {
  const { id, url } = e.data;
  let h;
  try { h = await fetchTerrarium(url); } catch (err) { self.postMessage({ id, empty: true }); return; }
  const best = new Map();   // "level/quarter" -> [dist², px, py, level]
  for (let y = 0; y < TILE; y += GRID)
    for (let x = 0; x + GRID < TILE; x += GRID) {
      const a = h[y * TILE + x] - SEA, b = h[y * TILE + x + GRID] - SEA;
      const lo = Math.min(a, b), hi = Math.max(a, b);
      for (let level = Math.max(LABEL_STEP, Math.ceil(lo / LABEL_STEP) * LABEL_STEP); level <= hi && level > lo; level += LABEL_STEP) {
        const px = x + ((level - a) / (b - a)) * GRID;
        const q = (px < TILE / 2 ? 0 : 1) + (y < TILE / 2 ? 0 : 2);
        const cx = (q & 1 ? 0.75 : 0.25) * TILE, cy = (q & 2 ? 0.75 : 0.25) * TILE;
        const d = (px - cx) ** 2 + (y - cy) ** 2, k = level + '/' + q, cur = best.get(k);
        if (!cur || d < cur[0]) best.set(k, [d, px, y, level]);
      }
    }
  const labels = [];
  for (const [d, px, py, level] of best.values()) if (d < (TILE * 0.2) ** 2) labels.push(px, py, level);
  self.postMessage({ id, h, labels }, [h.buffer]);
};
