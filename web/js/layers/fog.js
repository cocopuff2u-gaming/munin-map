// Fog of war: the server's explored mask drawn as a dark veil over
// everything nobody has walked to yet, softened at the edges.
//
// The mask is fetched once (and again on reconnect and every few minutes as a safety net); in
// between the page reveals ground itself from the live player positions it already gets over the
// websocket, with the server's own explore radius, like the first web maps did. The veil is a
// canvas laid on the map directly, so a reveal is a few pixels painted, not a 2048x2048 image
// re-encoded (that froze the page for ~140 ms per change).

import { on, fetchEarly } from '../net.js';

// L.ImageOverlay with a canvas in place of the <img>, so it can be painted on directly
export const CanvasOverlay = L.ImageOverlay.extend({
  _initImage() {
    const c = (this._image = this._url);
    L.DomUtil.addClass(c, 'leaflet-image-layer');
    if (this._zoomAnimated) L.DomUtil.addClass(c, 'leaflet-zoom-animated');
    if (this.options.className) L.DomUtil.addClass(c, this.options.className);
    c.onselectstart = L.Util.falseFn;
    c.onmousemove = L.Util.falseFn;
    this._updateOpacity();
  },
});

const REFETCH_MS = 5 * 60 * 1000;
const BLUR = 1.2;   // px of the mask: the soft edge of the veil

// opts.gl: the veil is drawn by FogGL in the WebGL canvas (layers/ground.js), which listens through
// onPaint (a changed rectangle of the canvas, or null for all of it) and onStyle
export class FogLayer {
  constructor(map, cfg, opts = {}) {
    this.gl = !!opts.gl;
    this.map = map;
    this.size = cfg.texture_size || 2048;
    this.px = cfg.pixel_size || 12;
    this.radius = cfg.explore_radius || 100;
    const half = this.size / 2;
    // the mask's pixel (i, j) is centred on world ((i - half) * px, (j - half) * px)
    const w = -(half + 0.5) * this.px, e = (half - 0.5) * this.px;
    this.bounds = L.latLngBounds([w, w], [e, e]);
    this.canvas = document.createElement('canvas');   // what is shown: the veil, blurred
    this.canvas.width = this.canvas.height = this.size;
    // (with WebGL the veil is read back after each reveal, FogGL.paint: kept in memory from the start,
    // or the first reveal waits ~300 ms for the GPU to hand the whole canvas over)
    this.canvas.getContext('2d', { willReadFrequently: this.gl }).fillRect(0, 0, this.size, this.size);   // all black until the mask arrives
    this.src = document.createElement('canvas');      // the sharp veil, for isExplored
    this.src.width = this.src.height = this.size;
    this.opacity = 1;   // unexplored ground is black until someone walks there
    this.visible = true;
    this.overlay = this.gl ? null : new CanvasOverlay(this.canvas, this.bounds, { opacity: this.opacity, className: 'fog-layer', zIndex: 300, interactive: false }).addTo(this.map);
    this.timer = null;
    this.exploredPct = 0;
    this.lastAt = new Map();   // player id -> [x, z] where we last revealed around them
    this.refreshed = new Set();  // called after a fetched mask is painted (BaseWorldImage re-fogs itself)
    this.loaded = new Promise((resolve) => { this.markLoaded = resolve; });
  }

  // the mask's two veils made in a worker (fogworker.js), or null where a worker can't draw: the
  // caller then makes them here
  decode(buf) {
    if (this.noWorker || typeof OffscreenCanvas === 'undefined') return Promise.resolve(null);
    try { this.worker = this.worker || new Worker(new URL('../fogworker.js', import.meta.url)); } catch { this.noWorker = true; return Promise.resolve(null); }
    return new Promise((resolve) => {
      this.worker.onmessage = (e) => { if (e.data.error) { console.warn('fog worker', e.data.error); this.noWorker = true; resolve(null); } else resolve(e.data); };
      this.worker.onerror = () => { this.noWorker = true; resolve(null); };
      const png = buf.slice().buffer;   // a copy: the original stays as lastMask
      this.worker.postMessage({ png, size: this.size, blur: BLUR }, [png]);
    });
  }

  async refresh() {
    if (this.refreshing) return;
    this.refreshing = true;
    try { await this.paintMask(); } finally { this.refreshing = false; }
  }

  async paintMask() {
    try {
      // no-cache, not no-store: an unchanged mask comes back as a 304 with no body
      const res = await fetchEarly('data/fog.png', { cache: 'no-cache' });
      if (!res.ok) throw new Error(`fog ${res.status}`);
      const buf = new Uint8Array(await res.arrayBuffer());
      const prev = this.lastMask;
      if (prev && prev.length === buf.length && prev.every((b, i) => b === buf[i])) return;
      this.lastMask = buf;
      const sctx = this.src.getContext('2d', { willReadFrequently: true });
      const ctx = this.canvas.getContext('2d');
      let v = await this.decode(buf);
      if (v) {
        sctx.putImageData(new ImageData(new Uint8ClampedArray(v.sharp), this.size, this.size), 0, 0);
        ctx.putImageData(new ImageData(new Uint8ClampedArray(v.soft), this.size, this.size), 0, 0);
      } else {
        const img = await createImageBitmap(new Blob([buf], { type: 'image/png' }));
        sctx.clearRect(0, 0, this.size, this.size);
        sctx.drawImage(img, 0, 0, this.size, this.size);
        const id = sctx.getImageData(0, 0, this.size, this.size);
        const d = id.data;
        let explored = 0;
        for (let i = 0; i < d.length; i += 4) {
          const e = d[i] > 127;
          if (e) explored++;
          d[i] = 0; d[i + 1] = 0; d[i + 2] = 0; d[i + 3] = e ? 0 : 255;
        }
        sctx.putImageData(id, 0, 0);
        ctx.clearRect(0, 0, this.size, this.size);
        ctx.filter = `blur(${BLUR}px)`;
        ctx.drawImage(this.src, 0, 0);
        ctx.filter = 'none';
        v = { explored };
      }
      this.exploredPct = 100 * v.explored / (Math.PI * Math.pow(10000 / this.px, 2));
      this.lastAt.clear();   // the mask may predate reveals made meanwhile: the next players frame makes them again
      if (this.onPaint) this.onPaint(null);
      this.markLoaded();
      for (const fn of this.refreshed) fn();
    } catch (e) {
      console.warn('fog', e);
    }
  }

  // Reveal the ground around a world position, the way the server does (Fog.Reveal), on both
  // the sharp mask and the shown veil (soft-edged, like the blur).
  reveal(x, z) {
    const half = this.size / 2, r = this.radius / this.px;
    const i = x / this.px + half, row = this.size - 1 - (z / this.px + half);   // canvas rows run north to south
    const s = this.src.getContext('2d', { willReadFrequently: true });
    s.globalCompositeOperation = 'destination-out';
    s.beginPath(); s.arc(i, row, r, 0, Math.PI * 2); s.fill();
    s.globalCompositeOperation = 'source-over';
    const c = this.canvas.getContext('2d');
    const g = c.createRadialGradient(i, row, Math.max(0, r - BLUR), i, row, r + BLUR);
    g.addColorStop(0, 'rgba(0,0,0,1)'); g.addColorStop(1, 'rgba(0,0,0,0)');
    c.globalCompositeOperation = 'destination-out';
    c.fillStyle = g;
    c.beginPath(); c.arc(i, row, r + BLUR, 0, Math.PI * 2); c.fill();
    c.globalCompositeOperation = 'source-over';
    const e = r + BLUR + 1;
    if (this.onPaint) this.onPaint({ x: i - e, y: row - e, w: 2 * e, h: 2 * e });
  }

  // live players: reveal around each one that moved a few metres since we last did
  onPlayers(data) {
    const step = Math.max(this.px, this.radius / 8);
    for (const p of (data && data.players) || []) {
      if (p.x === undefined) continue;   // hidden or no position: the periodic refetch covers them
      const last = this.lastAt.get(p.id);
      if (last && Math.hypot(p.x - last[0], p.z - last[1]) < step) continue;
      this.lastAt.set(p.id, [p.x, p.z]);
      this.reveal(p.x, p.z);
    }
  }

  start(intervalMs = REFETCH_MS) {
    this.refresh();
    this.timer = setInterval(() => this.refresh(), intervalMs);
    on('players', (f) => this.onPlayers(f.data));
    on('reconnected', () => this.refresh());   // back after a gap: catch up with the server's mask
  }

  setVisible(v) {
    this.visible = v;
    if (this.overlay) { if (v) this.overlay.addTo(this.map); else this.overlay.remove(); }
    if (this.onStyle) this.onStyle();
  }

  setOpacity(o) {
    this.opacity = o;
    if (this.overlay) this.overlay.setOpacity(o);
    if (this.onStyle) this.onStyle();
  }

  // is a world position explored? (from the mask, kept up to date by reveal)
  isExplored(x, z) {
    const half = this.size / 2;
    const i = Math.round(x / this.px + half), j = Math.round(z / this.px + half);
    if (i < 0 || j < 0 || i >= this.size || j >= this.size) return false;
    const ctx = this.src.getContext('2d', { willReadFrequently: true });
    // src rows run north (top) to south, mask row j is south-based
    const p = ctx.getImageData(i, this.size - 1 - j, 1, 1).data;
    return p[3] === 0;
  }
}
