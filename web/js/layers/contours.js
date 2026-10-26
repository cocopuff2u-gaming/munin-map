// Contour lines, like a hiking map, drawn by the GPU from the height tiles the 3D view uses
// (tiles/height): each tile goes up once as a texture of heights, and a shader (shapes.js
// CONTOUR_FS) works out per pixel how close it is to a line. So the lines are part of the very
// frames the map draws (no wobble in a glide, nothing left at the old size), a pixel wide and sharp
// at every zoom, and cost the page nothing between zooms. They sit just under the fog, which hides
// what nobody has explored. Closer zooms get closer lines; every fifth is bolder and, zoomed in,
// carries its height (metres above the sea). Fetching and decoding run in contourworker.js.
import { TILE, MAX_ZOOM, WORLD_HALF, WORLD_SIZE, worldTile } from '../crs.js';
import { ShapesCanvas } from './shapes.js';

const MIN_DRAW = 3;          // not drawn further out than this zoom
const LABELS_FROM = 5.5;     // heights written from this zoom in
const KEEP = 200;            // height textures kept on the GPU (256 KB each)
const UPLOADS = 3;           // new textures per frame at most: the rest the frames after
const LABEL_COLOR = '#f2e6cf';

const span = (z) => TILE * 2 ** (MAX_ZOOM - z);   // metres a tile covers
const intervalAt = (zoom) => (zoom >= 6.5 ? 5 : zoom >= 5.5 ? 10 : zoom >= 4.5 ? 20 : zoom >= 3.5 ? 25 : 50);

export class ContourGL extends L.Layer {
  constructor(opts) {
    super();
    this.order = 8.9;                // over buildings and biomes, under the fog (9)
    this.hmax = opts.heightMaxZoom;
    this.tex = new Map();            // key -> { t: texture, labels } (most recently drawn last)
    this.ready = new Map();          // key -> { h, labels } waiting for its upload
    this.empty = new Set();          // keys with no height tile (yet), this visit
    this.busy = new Set();
    this.pending = new Map();        // worker request id -> resolve
    this.nextId = 1;
    this.textTex = new Map();        // label canvas -> texture
  }

  onAdd(map) {
    this.sc = ShapesCanvas.for(map); this.sc.add(this); this.sc.redraw();
    try { indexedDB.deleteDatabase('munin-contours'); } catch (e) { /* an earlier build kept traced tiles there */ }
  }
  onRemove() {
    const gl = this.sc.gl;
    for (const e of this.tex.values()) gl.deleteTexture(e.t);
    for (const t of this.textTex.values()) gl.deleteTexture(t);
    this.tex.clear(); this.textTex.clear(); this.ready.clear();
    this.sc.remove(this);
    this.sc.redraw();
  }

  heightZoom(v) { return Math.max(0, Math.min(this.hmax, Math.round(v.zoom))); }

  range(v, z) {
    const s = span(z), n = Math.round(WORLD_SIZE / s);
    return {
      s, x0: Math.max(0, Math.floor((v.x + WORLD_HALF) / s)), x1: Math.min(n - 1, Math.floor((v.x1 + WORLD_HALF) / s)),
      y0: Math.max(0, Math.floor((WORLD_HALF - v.z) / s)), y1: Math.min(n - 1, Math.floor((WORLD_HALF - v.z0) / s)),
    };
  }

  // the height tiles over the view: ask for the ones not here yet, nearest the middle first
  need(v) {
    if (this.hmax < 0 || v.zoom < MIN_DRAW - 0.5) return;
    const z = this.heightZoom(v), r = this.range(v, z);
    const cx = (r.x0 + r.x1) / 2, cy = (r.y0 + r.y1) / 2, want = [];
    for (let y = r.y0; y <= r.y1; y++) for (let x = r.x0; x <= r.x1; x++) {
      const k = `${z}/${x}/${y}`;
      if (!this.tex.has(k) && !this.ready.has(k) && !this.empty.has(k) && !this.busy.has(k)) want.push([Math.hypot(x - cx, y - cy), k]);
    }
    want.sort((a, b) => a[0] - b[0]);
    for (const [, k] of want.slice(0, 8)) this.load(k);
  }

  async load(k) {
    this.busy.add(k);
    const id = this.nextId++;
    const res = await new Promise((resolve) => {
      this.pending.set(id, resolve);
      this.worker().postMessage({ id, url: new URL(worldTile(`tiles/height/${k}.png`), location.href).href });
    });
    this.busy.delete(k);
    if (res.h && this._map) this.ready.set(k, res); else this.empty.add(k);
    this.sc.redraw();
  }

  worker() {
    if (!this.w) {
      this.w = new Worker(new URL('../contourworker.js', import.meta.url), { type: 'module' });
      this.w.onmessage = (e) => { const r = this.pending.get(e.data.id); if (r) { this.pending.delete(e.data.id); r(e.data); } };
    }
    return this.w;
  }

  // a tile's height texture, uploaded now if it just came in (a few a frame)
  entry(gl, k) {
    let e = this.tex.get(k);
    if (e) { this.tex.delete(k); this.tex.set(k, e); return e; }   // (most recently drawn last)
    const r = this.ready.get(k);
    if (!r) return null;
    if (this.uploads >= UPLOADS) { this.more = true; return null; }
    this.uploads++;
    this.ready.delete(k);
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.R32F, TILE, TILE, 0, gl.RED, gl.FLOAT, r.h);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    e = { t, labels: r.labels };
    this.tex.set(k, e);
    while (this.tex.size > KEEP) { const [old, oe] = this.tex.entries().next().value; gl.deleteTexture(oe.t); this.tex.delete(old); }
    return e;
  }

  draw(gl, v, sc) {
    const p = sc.contour;
    if (!p || this.hmax < 0 || v.zoom < MIN_DRAW - 0.5) return;
    this.uploads = 0; this.more = false;
    const z = this.heightZoom(v), r = this.range(v, z), interval = intervalAt(v.zoom);
    sc.setView(p);
    gl.bindVertexArray(sc.quadVao);
    gl.activeTexture(gl.TEXTURE0);
    gl.uniform1i(p.u.u_h, 0);
    gl.uniform1f(p.u.u_interval, interval);
    gl.uniform1f(p.u.u_alpha, Math.min(1, (v.zoom - MIN_DRAW + 0.5) * 2));   // eases in at the far end
    const drawn = [];
    for (let y = r.y0; y <= r.y1; y++) for (let x = r.x0; x <= r.x1; x++) {
      const k = `${z}/${x}/${y}`;
      if (this.empty.has(k)) continue;
      let e = this.entry(gl, k), u0 = 0, v0 = 0, u1 = 1, v1 = 1;
      // not in yet: the part of a zoomed-out tile over this one stands in
      for (let up = 1; !e && up <= 3 && z - up >= 0; up++) {
        const f = 2 ** up, pk = `${z - up}/${Math.floor(x / f)}/${Math.floor(y / f)}`;
        e = this.tex.get(pk);
        if (e) { u0 = (x % f) / f; v0 = (y % f) / f; u1 = u0 + 1 / f; v1 = v0 + 1 / f; }
      }
      if (!e) continue;
      gl.bindTexture(gl.TEXTURE_2D, e.t);
      gl.uniform4f(p.u.u_uv, u0, v0, u1, v1);
      gl.uniform4f(p.u.u_rect, -WORLD_HALF + x * r.s, WORLD_HALF - y * r.s, r.s, r.s);
      gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
      if (u0 === 0 && u1 === 1) drawn.push([x, y, e]);
    }
    if (v.zoom >= LABELS_FROM) this.labels(gl, v, sc, r, drawn, interval * 5);
    gl.bindVertexArray(null);
    if (this.more) sc.redraw();   // the textures left over, next frame
  }

  // heights on the bold lines, kept apart from each other
  labels(gl, v, sc, r, drawn, every) {
    const t = sc.tex, taken = [];
    sc.setView(t);
    gl.bindVertexArray(sc.quadVao);
    gl.activeTexture(gl.TEXTURE0);
    gl.uniform1i(t.u.u_tex, 0);
    gl.uniform4f(t.u.u_uv, 0, 0, 1, 1);
    gl.uniform1f(t.u.u_alpha, Math.min(1, (v.zoom - LABELS_FROM) * 2 + 0.3));
    for (const [x, y, e] of drawn) {
      const l = e.labels;
      for (let i = 0; i < l.length; i += 3) {
        if (l[i + 2] % every !== 0) continue;
        const wx = -WORLD_HALF + x * r.s + (l[i] / TILE) * r.s, wz = WORLD_HALF - y * r.s - (l[i + 1] / TILE) * r.s;
        const sx = (wx - v.x) * v.ppm, sy = (v.z - wz) * v.ppm;
        if (taken.some(([ax, ay]) => Math.abs(ax - sx) < 90 && Math.abs(ay - sy) < 40)) continue;
        taken.push([sx, sy]);
        const cv = labelImage(`${l[i + 2]} m`);
        gl.bindTexture(gl.TEXTURE_2D, this.textTexture(gl, cv));
        gl.uniform4f(t.u.u_rect, wx + 2 / v.ppm, wz + cv.h / 2 / v.ppm, cv.w / v.ppm, cv.h / v.ppm);
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
      }
    }
    gl.uniform1f(t.u.u_alpha, 1);
  }

  textTexture(gl, cv) {
    let t = this.textTex.get(cv);
    if (t) return t;
    t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, cv);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
    gl.generateMipmap(gl.TEXTURE_2D);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    this.textTex.set(cv, t);
    return t;
  }
}

// a height label (the grid's monospace font has a slashed zero, which read as an 8 here)
const labelCache = new Map();
function labelImage(s) {
  let c = labelCache.get(s);
  if (c) return c;
  const R = 2, font = `600 ${10.5 * R}px system-ui, sans-serif`;
  c = document.createElement('canvas');
  const g = c.getContext('2d');
  g.font = font;
  const w = Math.ceil(g.measureText(s).width) + 6 * R;
  c.width = w; c.height = 15 * R;
  g.font = font; g.textBaseline = 'middle'; g.lineJoin = 'round';
  g.lineWidth = 3 * R; g.strokeStyle = 'rgba(20, 14, 6, .8)'; g.strokeText(s, 3 * R, 7.5 * R);
  g.fillStyle = LABEL_COLOR; g.fillText(s, 3 * R, 7.5 * R);
  c.w = w / R; c.h = 15;
  labelCache.set(s, c);
  return c;
}
