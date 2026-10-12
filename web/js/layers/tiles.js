// The rendered map tiles, with two things a plain L.TileLayer lacks:
//  * fallback: a tile the server has not rendered yet (or never will, under
//    the fog) shows the nearest zoomed-out ancestor scaled up, so the map is
//    never blank while close-zoom tiles are still rendering;
//  * refresh: when the server says a tile was (re)rendered, just that tile
//    is reloaded in place.

import { MAX_ZOOM, OVER_ZOOM, TILE, WORLD_HALF, worldBounds, OUTSIDE_COLOR, worldTile } from '../crs.js';
import { on } from '../net.js';
import { CanvasOverlay } from './fog.js';

const MAX_FALLBACK = 4;   // how many ancestor levels to try

const PREFETCH_KEEP = 400;

// Leaflet sizes the tile grid during a zoom animation by the LARGER of the two zooms, so zooming out
// only creates tiles for the old (smaller) view and the newly visible edge waits until the
// animation ends, then fades in: the bare border you see. Sizing by the zoom being animated to
// creates the whole new view at the start; those tiles are needed right after anyway.
export function zoomOutPixelBounds(layer, center) {
  const map = layer._map;
  const z = map._animatingZoom ? map._animateToZoom : map.getZoom();
  const scale = map.getZoomScale(z, layer._tileZoom);
  const c = map.project(center, layer._tileZoom).floor();
  const half = map.getSize().divideBy(scale * 2);
  return L.bounds(c.subtract(half), c.add(half));
}

export class FallbackTileLayer extends L.GridLayer {
  constructor(urlTemplate, options) {
    super(Object.assign({ tileSize: TILE, minZoom: 0, maxZoom: OVER_ZOOM, maxNativeZoom: MAX_ZOOM, noWrap: true, bounds: worldBounds, keepBuffer: 2, updateWhenIdle: false, className: 'maptiles' }, options));
    this.template = urlTemplate;
    this.pending = new Set();   // "z/x/y" keys we were told are missing
    on('tiles', (f) => this.onRendered(f.keys));
  }

  // options.prefetchZoomOut = N: once the map has been still for a moment, load the tiles 1..N zoom
  // steps out (low priority). Zooming out then finds the new edge already in the browser instead of
  // leaving it bare for a round trip. The images are kept (up to PREFETCH_KEEP) so the browser
  // holds them decoded.
  onAdd(map) { super.onAdd(map); if (this.options.prefetchZoomOut) map.on('moveend', this.schedulePrefetch, this); }
  onRemove(map) { map.off('moveend', this.schedulePrefetch, this); clearTimeout(this.prefetchTimer); super.onRemove(map); }
  schedulePrefetch() { clearTimeout(this.prefetchTimer); this.prefetchTimer = setTimeout(() => this.prefetchZoomOut(), 600); }
  prefetchZoomOut() {
    const map = this._map;
    if (!map) return;
    if (!this.prefetched) this.prefetched = new Map();
    for (let step = 1; step <= this.options.prefetchZoomOut; step++) this.prefetchLevel(map, Math.round(map.getZoom()) - step);
    for (const k of this.prefetched.keys()) { if (this.prefetched.size <= PREFETCH_KEEP) break; this.prefetched.delete(k); }
  }

  prefetchLevel(map, zoom) {
    const z = Math.min(zoom, MAX_ZOOM);
    if (z < (this.options.minNative || 0) || z < 0) return;
    // the view at `zoom`, in tiles of native zoom z
    const scale = map.getZoomScale(zoom, z), c = map.project(map.getCenter(), z), half = map.getSize().divideBy(2 * scale);
    const n = Math.pow(2, z), r = this._pxBoundsToTileRange(L.bounds(c.subtract(half), c.add(half)));
    for (let y = Math.max(0, r.min.y); y <= Math.min(n - 1, r.max.y); y++)
      for (let x = Math.max(0, r.min.x); x <= Math.min(n - 1, r.max.x); x++) {
        const key = `${z}/${x}/${y}`;
        if (this.prefetched.has(key) || this.pending.has(key)) continue;
        const img = new Image();
        img.fetchPriority = 'low';
        img.decoding = 'async';
        img.src = this.url(z, x, y);
        this.prefetched.set(key, img);
      }
  }

  url(z, x, y, bust) {
    // a tile re-rendered while this page was open keeps its fresh ?r= (the browser may hold the old one for 10 min)
    if (!bust && this.rerendered) bust = this.rerendered.get(`${z}/${x}/${y}`);
    return worldTile(this.template.replace('{z}', z).replace('{x}', x).replace('{y}', y) + (bust ? `?r=${bust}` : ''));
  }

  // Also load options.edgeBufferTiles rings of tiles beyond the viewport, so a short drag lands on
  // tiles that are already there instead of black squares.
  _getTiledPixelBounds(center) {
    const b = zoomOutPixelBounds(this, center);
    const pad = (this.options.edgeBufferTiles || 0) * TILE;
    return pad ? L.bounds(b.min.subtract([pad, pad]), b.max.add([pad, pad])) : b;
  }

  createTile(coords, done) {
    const wrap = document.createElement('div');
    wrap.className = 'tile-wrap';
    const z = Math.min(coords.z, MAX_ZOOM);
    // a layer that only exists from some zoom up (the vegetation overlay) stays empty below it
    if (z < (this.options.minNative || 0)) { setTimeout(() => done(null, wrap), 0); return wrap; }
    // above native zoom Leaflet asks for native tiles scaled, via the coordinate math below
    const scaleUp = Math.pow(2, coords.z - z);
    const nx = Math.floor(coords.x / scaleUp), ny = Math.floor(coords.y / scaleUp);
    wrap.dataset.key = `${z}/${nx}/${ny}`;
    this.load(wrap, z, nx, ny, coords, scaleUp, 0, done);
    return wrap;
  }

  load(wrap, z, x, y, coords, scaleUp, level, done, bust) {
    const az = z - level;
    if (az < (this.options.minNative || 0)) { if (done) done(null, wrap); return; }
    const ax = Math.floor(x / Math.pow(2, level)), ay = Math.floor(y / Math.pow(2, level));
    const img = new Image();
    img.decoding = 'async';
    img.onload = () => {
      // the ancestor covers 2^level tiles per side at zoom z; and coords.z may be above native
      const f = Math.pow(2, level) * scaleUp;
      const size = TILE * f;
      img.style.width = size + 'px';
      img.style.height = size + 'px';
      const offX = ((coords.x % f) + f) % f, offY = ((coords.y % f) + f) % f;
      img.style.left = -(offX * TILE) + 'px';
      img.style.top = -(offY * TILE) + 'px';
      wrap.replaceChildren(img);
      wrap.dataset.level = level;
      if (done) { done(null, wrap); done = null; }
    };
    img.onerror = () => {
      if (level === 0) this.pending.add(`${z}/${x}/${y}`);
      if (level < MAX_FALLBACK) this.load(wrap, z, x, y, coords, scaleUp, level + 1, done);
      else if (done) { done(null, wrap); done = null; }
    };
    img.src = this.url(az, ax, ay, bust);
  }

  // Server pushed "these tiles changed": refresh matching tiles that are on screen,
  // and any tile currently showing a fallback whose real tile is one of them.
  onRendered(keys) {
    if (!keys || !this._tiles) return;
    const set = new Set(keys);
    const bust = Date.now();
    if (!this.rerendered) this.rerendered = new Map();
    for (const k of keys) this.rerendered.set(k, bust);
    for (const id in this._tiles) {
      const t = this._tiles[id];
      const wrap = t.el;
      if (!wrap || !wrap.dataset) continue;
      const key = wrap.dataset.key;
      const level = +(wrap.dataset.level || 0);
      let hit = set.has(key);
      if (!hit && level > 0) {
        // showing an ancestor: does any rendered key sit on the path down to us?
        const [z, x, y] = key.split('/').map(Number);
        for (let l = 0; l <= level && !hit; l++) hit = set.has(`${z - l}/${x >> l}/${y >> l}`);
      }
      if (hit) {
        const [z, x, y] = key.split('/').map(Number);
        const coords = t.coords;
        const scaleUp = Math.pow(2, coords.z - z);
        this.pending.delete(key);
        this.load(wrap, z, x, y, coords, scaleUp, 0, null, bust);
      }
    }
  }
}

// Blurry whole-world base under the map, like the old single-image map: the zoom-BASE_ZOOM
// tiles are stitched into ONE picture once, shortly after load, and laid under the tile layers.
// Panning anywhere then shows the land straight away while the sharp tiles load on top, and one
// picture is cheap to scale while zooming (a tiled underlay redraws dozens of stretched squares).
const BASE_ZOOM = 2;   // 9 tiles, ~450 KB: z3 was 25 tiles, ~1.5 MB, for a picture you only see for a moment
// opts.fog (a FogLayer): the picture is fogged here, in the page, before it is shown, and gray past
// opts.radius; under the WebGL map it is what shows at the edges of a fast zoom-out, and with no
// fog of its own it showed unexplored ground there. Re-fogged whenever a fetched mask changes.
export class BaseWorldImage {
  constructor(map, template, opts = {}) {
    this.map = map;
    this.template = template;
    this.opts = opts;
    map.createPane('basePane').style.zIndex = 150;   // under tilePane (200)
    setTimeout(() => this.build(), 1500);
  }

  async build() {
    const span = TILE * Math.pow(2, MAX_ZOOM - BASE_ZOOM);   // metres per base tile
    const n = Math.ceil((2 * WORLD_HALF) / span);
    const canvas = this.raw = document.createElement('canvas');
    canvas.width = canvas.height = Math.round((2 * WORLD_HALF) / span * TILE);   // exactly the world: the last tiles hang over the edge at zoom 2
    const ctx = canvas.getContext('2d');
    const jobs = [];
    for (let y = 0; y < n; y++)
      for (let x = 0; x < n; x++) {
        const url = worldTile(this.template.replace('{z}', BASE_ZOOM).replace('{x}', x).replace('{y}', y));
        jobs.push(fetch(url, { priority: 'low', headers: { Accept: 'image/webp,image/png' } }).then((r) => (r.ok ? r.blob() : null)).then((b) => (b ? createImageBitmap(b) : null))
          .then((img) => { if (img) ctx.drawImage(img, x * TILE, y * TILE); }).catch(() => {}));
      }
    await Promise.all(jobs);
    const fog = this.opts.fog;
    if (!fog) {
      const blob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.85));
      if (!blob) return;
      this.overlay = L.imageOverlay(URL.createObjectURL(blob), worldBounds, { pane: 'basePane', interactive: false, className: 'base-world' }).addTo(this.map);
      return;
    }
    await fog.loaded;   // never show it unfogged
    this.shown = document.createElement('canvas');
    this.shown.width = this.shown.height = canvas.width;
    this.compose();
    this.overlay = new CanvasOverlay(this.shown, worldBounds, { pane: 'basePane', interactive: false, className: 'base-world' }).addTo(this.map);
    fog.refreshed.add(() => this.compose());
  }

  compose() {
    const fog = this.opts.fog, S = this.shown.width, k = S / (2 * WORLD_HALF), c = this.shown.getContext('2d');
    c.globalCompositeOperation = 'source-over';
    c.drawImage(this.raw, 0, 0);
    const b = fog.bounds;   // the fog canvas: black where unexplored, north up
    c.drawImage(fog.canvas, (b.getWest() + WORLD_HALF) * k, (WORLD_HALF - b.getNorth()) * k, (b.getEast() - b.getWest()) * k, (b.getNorth() - b.getSouth()) * k);
    if (this.opts.radius) {   // gray past the world's edge
      c.fillStyle = OUTSIDE_COLOR;
      c.beginPath(); c.rect(0, 0, S, S); c.arc(S / 2, S / 2, this.opts.radius * k, 0, Math.PI * 2, true); c.fill();
    }
  }
}
