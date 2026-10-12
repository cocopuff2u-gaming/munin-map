// Trees and rocks past the tiles' native zoom, drawn as shapes instead of
// scaled-up pixels. The server bakes crowns into the tiles at 1 m per pixel;
// zoomed past that they turn into blocks. From zoom 8 up this layer takes
// over and draws every crown and boulder from the vegetation points the 3D
// view already uses, at the screen's own resolution. Same colours, same
// north-west light, same south-east shadow as the baked tiles, so the switch
// is invisible.

import { TILE, WORLD_HALF, chunkOf, chunksOneZoomOut, metersPerPixel, CHUNKS } from '../crs.js';
import { chunks } from '../data.js';
import { zoomOutPixelBounds } from './tiles.js';

export const VEG_SHAPES_ZOOM = 7.5;   // from here up this layer draws the trees (Leaflet rounds 7.5 to tile zoom 8)

// kind -> [crownRadius, colour, isRock]  (mirrors Palette.cs)
const VEG = {
  1: [4.5, '#568a3a'], 2: [3.0, '#2c5234'], 3: [3.0, '#383e28'], 4: [4.0, '#4a6870'], 5: [2.5, '#46382e'],
  6: [1.3, '#466e32'], 7: [2.5, '#767670', true], 8: [2.5, '#86684a', true], 9: [0.7, '#60462c'], 10: [1.0, '#5a783c'], 11: [3.0, '#3c2822'],
  12: [4.5, '#46702a'], 13: [4.5, '#8aa046'], 14: [3.0, '#486430'], 15: [1.0, '#c43a4a'], 16: [1.0, '#4e64cc'], 17: [1.0, '#e4a840'], 18: [4.5, '#cc963a'],
  19: [0.6, '#d6423a'], 20: [0.6, '#e8c446'], 21: [0.6, '#b06ad6'], 22: [0.6, '#bae0f2'], 23: [0.6, '#968c8c'], 24: [0.6, '#706ed6'],
  25: [0.6, '#f2de5a'], 26: [0.6, '#7cba4e'], 27: [0.6, '#d6be74'], 28: [0.6, '#c4d6a4'], 29: [1.0, '#b0223e'], 30: [1.1, '#8e3a7a'], 31: [1.1, '#703c2c'],
  32: [2.5, '#6c6c68', true], 33: [2.5, '#c4beaa', true], 34: [2.5, '#b0683a', true], 35: [2.5, '#969ca0', true], 36: [2.5, '#c8d2de', true],
  37: [2.5, '#282230', true], 38: [2.5, '#6e543c', true],
};
const MARGIN = 12;   // metres: crowns that stand outside the tile but reach into it

const shade = (hex, k) => {
  const n = parseInt(hex.slice(1), 16);
  const c = (v) => Math.max(0, Math.min(255, Math.round(v * k)));
  return `rgb(${c(n >> 16)},${c((n >> 8) & 255)},${c(n & 255)})`;
};

export class VegLayer extends L.GridLayer {
  constructor(options) {
    // vegetation data is cached for the page's life, so a sweep changes nothing here: no redraw on
    // chunk changes (it used to blink every tree each sweep)
    super(Object.assign({ tileSize: TILE, minZoom: VEG_SHAPES_ZOOM, maxZoom: 10, updateWhenIdle: false, updateWhenZooming: true, keepBuffer: 2, className: 'veg-tile', zIndex: 101 }, options));
  }

  _getTiledPixelBounds(center) { return zoomOutPixelBounds(this, center); }   // see tiles.js
  isReady() { return !this._loading; }

  // fetch the vegetation a zoom-out will draw while the map sits still (see chunksOneZoomOut)
  onAdd(map) { super.onAdd(map); map.on('moveend', this.prefetch, this); }
  onRemove(map) { map.off('moveend', this.prefetch, this); clearTimeout(this.prefetchTimer); super.onRemove(map); }
  prefetch() {
    clearTimeout(this.prefetchTimer);
    this.prefetchTimer = setTimeout(() => {
      const map = this._map;
      if (map && map.getZoom() - 1 >= VEG_SHAPES_ZOOM) for (const [cx, cz] of chunksOneZoomOut(map)) chunks.veg(cx, cz);
    }, 400);
  }

  createTile(coords, done) {
    const canvas = document.createElement('canvas');
    const dpr = Math.min(devicePixelRatio || 1, 3);
    canvas.width = TILE * dpr; canvas.height = TILE * dpr;
    canvas.style.width = canvas.style.height = TILE + 'px';
    this.draw(canvas, coords, dpr).then(() => done(null, canvas)).catch((e) => { console.warn(e); done(null, canvas); });
    return canvas;
  }

  async draw(canvas, coords, dpr) {
    // Leaflet still creates tiles for this layer below its minZoom (8). At the opening zoom a
    // single tile spans ~100 chunks, so each page load fetched ~3,200 data/veg/*.bin files
    // and drew crowns nobody can see. Nothing is drawn below 8 anyway.
    if (coords.z < 8) return;
    const mpp = metersPerPixel(coords.z), ppm = 1 / mpp, span = TILE * mpp;
    const minX = -WORLD_HALF + coords.x * span, maxZ = WORLD_HALF - coords.y * span;
    const maxX = minX + span, minZ = maxZ - span;
    const lists = [];
    for (let cz = chunkOf(minZ - MARGIN); cz <= chunkOf(maxZ + MARGIN); cz++)
      for (let cx = chunkOf(minX - MARGIN); cx <= chunkOf(maxX + MARGIN); cx++)
        if (cx >= 0 && cz >= 0 && cx < CHUNKS && cz < CHUNKS) lists.push(chunks.veg(cx, cz));
    const pts = [];
    for (const list of await Promise.all(lists))
      for (const p of list) if (p.x >= minX - MARGIN && p.x <= maxX + MARGIN && p.z >= minZ - MARGIN && p.z <= maxZ + MARGIN && VEG[p.kind]) pts.push(p);
    if (pts.length === 0) return;
    pts.sort((a, b) => b.z - a.z);   // north to south: nearer (southern) crowns overlap farther ones
    const ctx = canvas.getContext('2d');
    ctx.scale(dpr, dpr);
    for (const p of pts) {
      const [radius, color, isRock] = VEG[p.kind];
      const r = radius * p.size * ppm;
      const cx = (p.x - minX) * ppm, cy = (maxZ - p.z) * ppm;
      // shadow to the south-east, away from the sun
      const sh = Math.min(r * 0.35, 3 * ppm);
      ctx.globalAlpha = 0.24;
      ctx.fillStyle = '#000';
      ctx.beginPath(); ctx.arc(cx + sh, cy + sh, r * 0.95, 0, Math.PI * 2); ctx.fill();
      // crown or boulder: lit from the north-west, darker toward the south-east rim
      const hi = isRock ? 0.35 : 0.55;
      const g = ctx.createRadialGradient(cx - r * 0.4, cy - r * 0.4, r * 0.1, cx, cy, r);
      g.addColorStop(0, shade(color, 1 + hi * 0.8));
      g.addColorStop(0.6, shade(color, 1.0));
      g.addColorStop(1, shade(color, 0.7 - hi * 0.2));
      ctx.globalAlpha = isRock ? 0.95 : 0.92;
      ctx.fillStyle = g;
      ctx.beginPath(); ctx.arc(cx, cy, r, 0, Math.PI * 2); ctx.fill();
      if (!isRock && r > 6) {
        // a few darker lobes so a big crown reads as foliage, not a coin
        ctx.globalAlpha = 0.18;
        ctx.fillStyle = shade(color, 0.6);
        const seed = (p.x * 7 + p.z * 3) % 6.28;
        for (let i = 0; i < 3; i++) {
          const a = seed + i * 2.1, d = r * 0.45;
          ctx.beginPath(); ctx.arc(cx + Math.cos(a) * d, cy + Math.sin(a) * d, r * 0.38, 0, Math.PI * 2); ctx.fill();
        }
      }
    }
  }
}
