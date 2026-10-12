// Player-built structures as a canvas tile layer drawn from the chunk data:
// every piece is a rotated rectangle in the colour of its material, so a
// base reads as walls, floors and roofs rather than a blob -- and at 1 m/px
// you can count the longhouse's roof beams.

import { zoomOutPixelBounds } from './tiles.js';
import { MAX_ZOOM, TILE, WORLD_HALF, chunkOf, chunksOneZoomOut, metersPerPixel, CHUNKS } from '../crs.js';
import { chunks } from '../data.js';
import { materialColors, materialNames } from '../icons.js';

export class StructuresLayer extends L.GridLayer {
  constructor(options) {
    super(Object.assign({ tileSize: TILE, minZoom: 2, maxZoom: 10, updateWhenIdle: false, updateWhenZooming: true, keepBuffer: 2, className: 'structures-tile', zIndex: 250 }, options));
    this.opacity = 0.95;
    // Repaint the tiles in place when the data changes (every world sweep), instead of redraw(),
    // which removes every tile first and makes the whole layer blink.
    chunks.onChange(() => this.repaint());
  }

  repaint() {
    if (!this._map) return;
    for (const t of Object.values(this._tiles)) this.draw(t.el, t.coords, t.el.width / TILE).catch(() => {});
  }

  _getTiledPixelBounds(center) { return zoomOutPixelBounds(this, center); }   // see tiles.js

  onAdd(map) { super.onAdd(map); map.on('moveend', this.prefetch, this); }
  onRemove(map) { map.off('moveend', this.prefetch, this); clearTimeout(this.prefetchTimer); super.onRemove(map); }
  prefetch() {
    clearTimeout(this.prefetchTimer);
    this.prefetchTimer = setTimeout(() => {
      if (this._map) for (const [cx, cz] of chunksOneZoomOut(this._map)) if (chunks.has(cx, cz)) chunks.get(cx, cz).catch(() => {});
    }, 400);
  }

  createTile(coords, done) {
    const canvas = document.createElement('canvas');
    const dpr = Math.min(devicePixelRatio || 1, 3);   // draw at the screen's own resolution
    canvas.width = TILE * dpr; canvas.height = TILE * dpr;
    canvas.style.width = canvas.style.height = TILE + 'px';
    this.draw(canvas, coords, dpr).then(() => done(null, canvas)).catch((e) => { console.warn(e); done(null, canvas); });
    return canvas;
  }

  async draw(canvas, coords, dpr = 1) {
    const z = coords.z;
    const mpp = metersPerPixel(z);           // metres per pixel at this zoom
    const span = TILE * mpp;
    const minX = -WORLD_HALF + coords.x * span, maxZ = WORLD_HALF - coords.y * span;
    const maxX = minX + span, minZ = maxZ - span;
    const c0x = chunkOf(minX - 8), c1x = chunkOf(maxX + 8), c0z = chunkOf(minZ - 8), c1z = chunkOf(maxZ + 8);
    const lists = [];
    for (let cz = c0z; cz <= c1z; cz++)
      for (let cx = c0x; cx <= c1x; cx++)
        if (cx >= 0 && cz >= 0 && cx < CHUNKS && cz < CHUNKS && chunks.has(cx, cz)) lists.push(chunks.get(cx, cz));
    if (lists.length === 0) { canvas.getContext('2d').clearRect(0, 0, canvas.width, canvas.height); return; }   // a repaint may find it emptied
    const datas = await Promise.all(lists);
    const ctx = canvas.getContext('2d');
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.scale(dpr, dpr);
    const ppm = 1 / mpp;                     // pixels per metre
    const detailed = z >= 5;
    ctx.globalAlpha = this.opacity;
    for (const data of datas) {
      for (const p of data.pieces) {
        const [x, zz, , yaw, sx, sz, h, mat] = p;
        if (x < minX - 8 || x > maxX + 8 || zz < minZ - 8 || zz > maxZ + 8) continue;
        const px = (x - minX) * ppm, py = (maxZ - zz) * ppm;
        const col = materialColors[mat] || '#a07446';
        if (!detailed) {
          // a dot with a little weight: bases still read as settlements from afar
          ctx.fillStyle = col;
          const r = z >= 4 ? 1.2 : 0.9;
          ctx.fillRect(px - r, py - r, r * 2, r * 2);
          continue;
        }
        let w = Math.max(sx * ppm, 1.2), d = Math.max(sz * ppm, 1.2);
        ctx.save();
        ctx.translate(px, py);
        ctx.rotate(yaw * Math.PI / 180);    // Unity yaw turns +x toward -z (south); with canvas y pointing south that is a positive canvas rotation
        ctx.fillStyle = col;
        ctx.fillRect(-w / 2, -d / 2, w, d);
        if (z >= 7 && (w > 4 || d > 4)) {
          ctx.strokeStyle = 'rgba(0,0,0,.45)';
          ctx.lineWidth = 1;
          ctx.strokeRect(-w / 2 + .5, -d / 2 + .5, w - 1, d - 1);
          // taller pieces get a lighter top so roofs stand out from floors
          if (h >= 1.5) { ctx.fillStyle = 'rgba(255,255,255,.12)'; ctx.fillRect(-w / 2, -d / 2, w, d * 0.5); }
        }
        ctx.restore();
      }
    }
  }

  setOpacity(o) { this.opacity = o; this.repaint(); }

  // Pieces near a world position (for hover), nearest first.
  async pick(x, z, radius) {
    const cx = chunkOf(x), cz = chunkOf(z);
    const out = [];
    for (let dz = -1; dz <= 1; dz++)
      for (let dx = -1; dx <= 1; dx++) {
        const ax = cx + dx, az = cz + dz;
        if (ax < 0 || az < 0 || ax >= 80 || az >= 80 || !chunks.has(ax, az)) continue;
        const data = await chunks.get(ax, az);
        for (const p of data.pieces) {
          const d = Math.hypot(p[0] - x, p[1] - z);
          const reach = Math.max(p[4], p[5]) / 2 + radius;
          if (d <= reach) out.push({ d, prefab: data.prefabs[p[8]], material: materialNames[p[7]] || 'Misc', y: p[2], h: p[6] });
        }
      }
    out.sort((a, b) => a.d - b.d);
    return out;
  }
}
