// How long it takes to get anywhere from a spot, for measure.js: on foot (or a lox) over land, or
// by boat over water, from the height tiles (tiles/height). A grid over the area, each cell land
// or water by its height, and the quickest time to every cell from the start (Dijkstra, 16 ways:
// with the knight's moves the reach comes out round, not an octagon).
// Walking stops at water deeper than wading and at cliffs; a steep climb takes longer; boats keep
// to water deep enough to float. Unknown ground (no height tile: never explored) is not crossed.
// Request { id, x, z, speed (m/s), kind 'land' | 'water', maxSec (0: no limit), radius (metres),
//   hmax, base (URL of tiles/height/), suffix (query), targets: [[x, z]...] }
// Reply { id, x0, z0 (north-west corner), cell, w, h, t (Float32Array seconds, Infinity: no way),
//   targets: [seconds...] }
import { fetchTerrarium } from './png.js';

const TILE = 256, MAX_ZOOM = 7, HALF = 21504 / 2, SEA = 30;
const tiles = new Map();

function tile(base, suffix, z, x, y) {
  const k = `${z}/${x}/${y}`;
  if (!tiles.has(k)) {
    tiles.set(k, fetchTerrarium(`${base}${k}.png${suffix}`).catch(() => null));
    if (tiles.size > 200) tiles.delete(tiles.keys().next().value);
  }
  return tiles.get(k);
}

self.onmessage = async (e) => {
  const q = e.data;
  const radius = Math.min(HALF, q.radius);
  const cell = Math.max(16, Math.ceil((2 * radius) / 640));
  const x0 = Math.max(-HALF, q.x - radius), x1 = Math.min(HALF, q.x + radius);
  const zN = Math.min(HALF, q.z + radius), zS = Math.max(-HALF, q.z - radius);
  const w = Math.ceil((x1 - x0) / cell), h = Math.ceil((zN - zS) / cell);
  // heights at each cell's middle, from the height zoom about half a cell a pixel
  const hz = Math.max(0, Math.min(q.hmax, MAX_ZOOM - Math.floor(Math.log2(cell / 2))));
  const span = TILE * 2 ** (MAX_ZOOM - hz), mpp = span / TILE;
  const tx0 = Math.floor((x0 + HALF) / span), tx1 = Math.floor((x1 - 1e-3 + HALF) / span);
  const ty0 = Math.floor((HALF - zN) / span), ty1 = Math.floor((HALF - zS - 1e-3) / span);
  const got = new Map();
  await Promise.all([...Array((tx1 - tx0 + 1) * (ty1 - ty0 + 1)).keys()].map(async (i) => {
    const tx = tx0 + (i % (tx1 - tx0 + 1)), ty = ty0 + Math.floor(i / (tx1 - tx0 + 1));
    got.set(tx + '/' + ty, await tile(q.base, q.suffix, hz, tx, ty));
  }));
  const H = new Float32Array(w * h).fill(NaN);
  for (let j = 0; j < h; j++)
    for (let i = 0; i < w; i++) {
      const wx = x0 + (i + 0.5) * cell, wz = zN - (j + 0.5) * cell;
      const gx = (wx + HALF) / span, gy = (HALF - wz) / span, tx = Math.floor(gx), ty = Math.floor(gy);
      const t = got.get(tx + '/' + ty);
      if (!t) continue;
      const px = Math.min(TILE - 1, Math.floor((gx - tx) * TILE)), py = Math.min(TILE - 1, Math.floor((gy - ty) * TILE));
      H[j * w + i] = t[py * TILE + px] - SEA;
    }
  const land = q.kind !== 'water';
  const ok = (k) => { const v = H[k]; return v === v && (land ? v > -0.8 : v < -1.5); };   // wade to 0.8 m; a boat needs 1.5 m
  // the start: its cell, or the nearest one this way of travel can use (a boat starts at the shore)
  let si = Math.floor((q.x - x0) / cell), sj = Math.floor((zN - q.z) / cell), start = -1;
  for (let r = 0; r <= Math.ceil((land ? 300 : 2000) / cell) && start < 0; r++)   // a boat: from the nearest shore
    for (let dj = -r; dj <= r && start < 0; dj++)
      for (let di = -r; di <= r; di++) {
        if (Math.max(Math.abs(di), Math.abs(dj)) !== r) continue;
        const i = si + di, j = sj + dj;
        if (i >= 0 && j >= 0 && i < w && j < h && ok(j * w + i)) { start = j * w + i; break; }
      }
  const T = new Float32Array(w * h).fill(Infinity);
  if (start >= 0) {
    // Dijkstra with a binary heap of (time, cell)
    let heapT = new Float32Array(w * h), heapK = new Int32Array(w * h), n = 0;
    const push = (t, k) => {
      if (n === heapT.length) {   // (a cell can be in the heap more than once: grow as needed)
        const a = new Float32Array(n * 2), b = new Int32Array(n * 2);
        a.set(heapT); b.set(heapK); heapT = a; heapK = b;
      }
      let i = n++;
      while (i > 0) { const p = (i - 1) >> 1; if (heapT[p] <= t) break; heapT[i] = heapT[p]; heapK[i] = heapK[p]; i = p; }
      heapT[i] = t; heapK[i] = k;
    };
    const pop = () => {
      const t = heapT[0], k = heapK[0], lt = heapT[--n], lk = heapK[n];
      let i = 0;
      for (;;) {
        let c = 2 * i + 1;
        if (c >= n) break;
        if (c + 1 < n && heapT[c + 1] < heapT[c]) c++;
        if (heapT[c] >= lt) break;
        heapT[i] = heapT[c]; heapK[i] = heapK[c]; i = c;
      }
      heapT[i] = lt; heapK[i] = lk;
      return [t, k];
    };
    const K = Math.sqrt(5), D = [[1, 0, 1], [-1, 0, 1], [0, 1, 1], [0, -1, 1], [1, 1, Math.SQRT2], [1, -1, Math.SQRT2], [-1, 1, Math.SQRT2], [-1, -1, Math.SQRT2],
      [2, 1, K], [2, -1, K], [-2, 1, K], [-2, -1, K], [1, 2, K], [1, -2, K], [-1, 2, K], [-1, -2, K]];
    const limit = q.maxSec > 0 ? q.maxSec : Infinity;
    T[start] = 0; push(0, start);
    while (n > 0) {
      const [t, k] = pop();
      if (t > T[k] || t > limit) continue;
      const i = k % w, j = (k - i) / w;
      for (const [di, dj, f] of D) {
        const a = i + di, b = j + dj;
        if (a < 0 || b < 0 || a >= w || b >= h) continue;
        const m = b * w + a;
        if (!ok(m)) continue;
        if (f > 2 && !ok(j * w + i + Math.sign(di)) && !ok((j + Math.sign(dj)) * w + i)) continue;   // no leaping a gap
        const d = f * cell;
        let cost = d / q.speed;
        if (land) {
          const climb = Math.abs(H[m] - H[k]) / d;
          if (climb > 1.2) continue;                          // a cliff
          if (climb > 0.45) cost *= 1 + (climb - 0.45) * 3;   // steep: slower
        }
        const nt = Math.fround(t + cost);   // compared as stored: a time rounded up in the Float32Array
        if (nt < T[m]) { T[m] = nt; push(nt, m); }   // otherwise always looked improvable, and requeued forever
      }
    }
  }
  const targets = (q.targets || []).map(([x, z]) => {
    const i = Math.floor((x - x0) / cell), j = Math.floor((zN - z) / cell);
    let best = Infinity;   // the target's cell, or the best next to it (an altar on a rock, a trader by the shore)
    for (let dj = -2; dj <= 2; dj++) for (let di = -2; di <= 2; di++) {
      const a = i + di, b = j + dj;
      if (a >= 0 && b >= 0 && a < w && b < h) best = Math.min(best, T[b * w + a]);
    }
    return best;
  });
  self.postMessage({ id: q.id, x0, z0: zN, cell, w, h, t: T, targets, started: start >= 0 }, [T.buffer]);
};
