// Contour lines, like a hiking map: traced in the browser from the height tiles the 3D view uses
// (tiles/height, Terrarium), over explored ground only. Closer zooms get closer lines; every
// fifth line is bolder and carries its height (metres above the sea). Past the closest height
// zoom, that zoom's tile is enlarged and traced again.
import { TILE, MAX_ZOOM, WORLD_HALF, worldTile } from '../crs.js';
import { fetchTerrarium } from '../png.js';

const SEA = 30;                     // the game's water level
const STEP = 2;                     // trace on every 2nd pixel: plenty, and a quarter of the work
const N = TILE / STEP;

// metres between lines at a zoom
const intervalAt = (z) => (z >= 7 ? 5 : z >= 6 ? 10 : z >= 5 ? 20 : z >= 4 ? 25 : 50);

export class ContourLayer extends L.GridLayer {
  constructor(opts) {
    super({ tileSize: TILE, minZoom: 3, pane: ContourLayer.pane(opts.map), updateWhenZooming: false, keepBuffer: 1 });
    this.hmax = opts.heightMaxZoom;
    this.fog = opts.fog;
    this.cache = new Map();         // "z/x/y" -> Promise<Float32Array | null>
  }

  static pane(map) {
    if (!map.getPane('contourPane')) { const p = map.createPane('contourPane'); p.style.zIndex = 420; p.style.pointerEvents = 'none'; }
    return 'contourPane';
  }

  heights(z, x, y) {
    const k = `${z}/${x}/${y}`;
    if (!this.cache.has(k)) {
      this.cache.set(k, fetchTerrarium(worldTile(`tiles/height/${k}.png`)).catch(() => null));
      if (this.cache.size > 96) this.cache.delete(this.cache.keys().next().value);
    }
    return this.cache.get(k);
  }

  createTile(coords, done) {
    const cv = document.createElement('canvas');
    cv.width = TILE; cv.height = TILE;
    this.draw(cv, coords).then(() => done(null, cv), (e) => done(null, cv));
    return cv;
  }

  async draw(cv, { x, y, z }) {
    if (this.hmax < 0) return;
    // the source tile and where this tile sits in it
    const sz = Math.min(z, this.hmax), f = 2 ** (z - sz);
    const sx = Math.floor(x / f), sy = Math.floor(y / f), ox = (x - sx * f) * (TILE / f), oy = (y - sy * f) * (TILE / f);
    const h = await this.heights(sz, sx, sy);
    if (!h) return;
    // heights on an (N+1)^2 grid over this tile, bilinear from the source
    const g = new Float32Array((N + 1) * (N + 1));
    for (let j = 0; j <= N; j++)
      for (let i = 0; i <= N; i++) {
        const u = Math.min(TILE - 1.001, ox + (i * STEP) / f), v = Math.min(TILE - 1.001, oy + (j * STEP) / f);
        const x0 = Math.floor(u), y0 = Math.floor(v), tx = u - x0, ty = v - y0;
        const a = h[y0 * TILE + x0], b = h[y0 * TILE + x0 + 1], c = h[(y0 + 1) * TILE + x0], d = h[(y0 + 1) * TILE + x0 + 1];
        g[j * (N + 1) + i] = (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty - SEA;
      }
    // which grid cells are explored: the fog mask for this tile's square, read once
    const mpp = 2 ** (MAX_ZOOM - z), span = TILE * mpp;
    // tile (0, 0) is the north-west corner of the map's square
    const wx0 = -WORLD_HALF + x * span, wz0 = WORLD_HALF - y * span;
    const seen = this.explored(wx0, wz0, span);
    if (!seen) return;
    const ctx = cv.getContext('2d');
    const step = intervalAt(z);
    let lo = Infinity, hi = -Infinity;
    for (const v of g) { if (v < lo) lo = v; if (v > hi) hi = v; }
    const first = Math.max(step, Math.ceil(lo / step) * step);
    // heights written on the bold lines: zoomed in only, on every other tile (a checkerboard), each
    // where its line passes nearest the tile's middle
    const labels = z >= 6 && (x + y) % 2 === 0;
    for (let level = first; level <= hi; level += step) {
      const major = Math.round(level / step) % 5 === 0;
      ctx.beginPath();
      let lx = -1, ly = -1, ld = Infinity;
      for (let j = 0; j < N; j++)
        for (let i = 0; i < N; i++) {
          if (!seen(i, j)) continue;
          const a = g[j * (N + 1) + i], b = g[j * (N + 1) + i + 1], c = g[(j + 1) * (N + 1) + i + 1], d = g[(j + 1) * (N + 1) + i];
          // marching squares: corners a (top left), b (top right), c (bottom right), d (bottom left)
          const m = (a >= level) | ((b >= level) << 1) | ((c >= level) << 2) | ((d >= level) << 3);
          if (m === 0 || m === 15) continue;
          const px = i * STEP, py = j * STEP;
          const t = (p, q) => (level - p) / (q - p);
          const top = [px + t(a, b) * STEP, py], right = [px + STEP, py + t(b, c) * STEP];
          const bottom = [px + t(d, c) * STEP, py + STEP], left = [px, py + t(a, d) * STEP];
          const seg = SEGS[m];
          for (let s = 0; s < seg.length; s += 2) {
            const p0 = [top, right, bottom, left][seg[s]], p1 = [top, right, bottom, left][seg[s + 1]];
            ctx.moveTo(p0[0], p0[1]); ctx.lineTo(p1[0], p1[1]);
            if (major && labels) { const dd = (p0[0] - TILE / 2) ** 2 + (p0[1] - TILE / 2) ** 2; if (dd < ld) { ld = dd; lx = p0[0]; ly = p0[1]; } }
          }
        }
      ctx.strokeStyle = major ? 'rgba(40, 24, 10, .62)' : 'rgba(40, 24, 10, .32)';
      ctx.lineWidth = major ? 1.4 : 0.8;
      ctx.stroke();
      if (major && lx >= 0 && ld < (TILE * 0.42) ** 2) {
        ctx.font = '600 10px system-ui, sans-serif';
        ctx.lineWidth = 3; ctx.strokeStyle = 'rgba(20, 14, 6, .8)'; ctx.fillStyle = '#f2e6cf';
        ctx.strokeText(level + ' m', lx + 2, ly - 2); ctx.fillText(level + ' m', lx + 2, ly - 2);
      }
    }
  }

  // a test (i, j) -> explored, for the grid over a square of `span` metres from (wx0, wz0) (north-west
  // corner), from one read of the fog mask; null when none of it is explored
  explored(wx0, wz0, span) {
    const fog = this.fog;
    if (!fog || !fog.src) return () => true;
    const px = fog.px, size = fog.size, half = size / 2;
    const i0 = Math.max(0, Math.floor(wx0 / px + half) - 1), i1 = Math.min(size - 1, Math.ceil((wx0 + span) / px + half) + 1);
    const j0 = Math.max(0, Math.floor((wz0 - span) / px + half) - 1), j1 = Math.min(size - 1, Math.ceil(wz0 / px + half) + 1);
    if (i1 < i0 || j1 < j0) return null;
    const w = i1 - i0 + 1, hgt = j1 - j0 + 1;
    const d = fog.src.getContext('2d', { willReadFrequently: true }).getImageData(i0, size - 1 - j1, w, hgt).data;   // rows north first
    let any = false;
    for (let k = 3; k < d.length; k += 4) if (d[k] === 0) { any = true; break; }
    if (!any) return null;
    const cell = span / N;
    return (i, j) => {
      const x = wx0 + (i + 0.5) * cell, z = wz0 - (j + 0.5) * cell;
      const fi = Math.round(x / px + half) - i0, fj = j1 - Math.round(z / px + half);
      if (fi < 0 || fj < 0 || fi >= w || fj >= hgt) return false;
      return d[(fj * w + fi) * 4 + 3] === 0;
    };
  }
}

// marching squares: for each corner mask, the edge pairs to join (0 top, 1 right, 2 bottom, 3 left)
const SEGS = [[], [3, 0], [0, 1], [3, 1], [1, 2], [3, 0, 1, 2], [0, 2], [3, 2], [2, 3], [0, 2], [0, 1, 2, 3], [1, 2], [1, 3], [0, 1], [3, 0], []];
