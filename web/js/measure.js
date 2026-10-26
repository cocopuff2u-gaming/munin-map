// The ruler: measure a route (click points; they snap to markers) and see the distance and how long
// it takes on foot, on a lox or by boat; or pick a start and see everywhere you can reach within so
// many minutes (reachworker.js works it out from the height tiles: walking keeps to land, boats to
// water). Drawn in the WebGL canvas with the map: the route over the fog, the reach under it.
import { ShapesCanvas } from './layers/shapes.js';
import { fromLatLng, toLatLng, worldTile } from './crs.js';
import { escape } from './layers/markers.js';

// travel speeds in metres a second: rough, for a sense of the trip (boats with a fair wind; on foot
// running with stops for stamina)
export const TRAVEL = [
  { id: 'foot', label: 'On foot', speed: 5, kind: 'land' },
  { id: 'lox', label: 'Lox', speed: 7, kind: 'land' },
  { id: 'raft', label: 'Raft', speed: 3.5, kind: 'water' },
  { id: 'karve', label: 'Karve', speed: 8, kind: 'water' },
  { id: 'longship', label: 'Longship', speed: 10, kind: 'water' },
  { id: 'drakkar', label: 'Drakkar', speed: 11, kind: 'water' },
];
const MINUTES = [2, 5, 10, 15, 30];
// Reach in bands, each its own colour from green (close) to red (the limit), five or so whatever
// the minutes: one colour fading out was hard to read
const BAND_COLOURS = [[46, 204, 113], [163, 214, 92], [241, 196, 15], [243, 156, 18], [231, 96, 60], [192, 57, 43]];
export function reachBands(minutes) {
  const step = minutes <= 2 ? 0.5 : minutes <= 5 ? 1 : minutes <= 10 ? 2 : minutes <= 15 ? 3 : 5, n = Math.round(minutes / step);
  return Array.from({ length: n }, (_, i) => ({
    from: i * step, to: (i + 1) * step,
    colour: BAND_COLOURS[n === 1 ? 0 : Math.round((i * (BAND_COLOURS.length - 1)) / (n - 1))],
  }));
}
const fmtMin = (m) => (m % 1 ? `${Math.round(m * 60)} s` : `${m} min`);

export const fmtDist = (m) => (m < 1000 ? `${Math.round(m)} m` : `${(m / 1000).toFixed(m < 10000 ? 2 : 1)} km`);
export const fmtTime = (s) => (!isFinite(s) ? '–' : s < 60 ? `${Math.max(1, Math.round(s))} s` : s < 3600 ? `${Math.round(s / 60)} min` : `${Math.floor(s / 3600)} h ${Math.round((s % 3600) / 60)} min`);

export class MeasureTool {
  constructor(app) {
    this.app = app;
    this.map = app.map;
    this.points = [];            // {x, z, label}
    this.mode = 'measure';
    this.travel = 'foot';
    this.minutes = 10;
    this.reach = null;           // last reach result
    this.pending = new Map();
    this.nextId = 1;
    this.panel = document.getElementById('measure-panel');
    this.route = new RouteGL(this);
    this.tint = new ReachGL(this);
    this.map.on('click', (e) => this.onClick(e));
    document.addEventListener('keydown', (e) => { if (this.active && e.key === 'Escape') this.stop(); });
  }

  toggle() { if (this.active) this.stop(); else this.start(); }
  start() {
    this.active = true;
    this.app.measuring = true;
    document.getElementById('btn-measure')?.classList.add('on');
    this.map.getContainer().classList.add('measuring');
    this.route.addTo(this.map); this.tint.addTo(this.map);
    this.panel.hidden = false;
    this.render();
  }
  stop() {
    this.active = false;
    this.app.measuring = false;
    document.getElementById('btn-measure')?.classList.remove('on');
    this.map.getContainer().classList.remove('measuring');
    this.points = []; this.reach = null;
    this.route.remove(); this.tint.clear(); this.tint.remove();
    this.panel.hidden = true;
  }

  onClick(e) {
    if (!this.active) return;
    let { x, z } = fromLatLng(e.latlng), label = '';
    const hit = this.app.layers.markers?.canvas?.hit?.(e.containerPoint);   // snap to a marker under the click
    if (hit) { x = hit.x; z = hit.z; label = hit.label || ''; }
    if (this.mode === 'measure') { this.points.push({ x, z, label }); this.route.changed(); this.render(); }
    else this.reachFrom({ x, z, label });
  }
  // right click while measuring: take the last point back
  undo() { if (this.mode === 'measure' && this.points.length) { this.points.pop(); this.route.changed(); this.render(); return true; } return false; }

  length() { let d = 0; for (let i = 1; i < this.points.length; i++) d += Math.hypot(this.points[i].x - this.points[i - 1].x, this.points[i].z - this.points[i - 1].z); return d; }

  render() {
    const p = this.panel, seg = (m, l) => `<button data-mode="${m}" class="${this.mode === m ? 'on' : ''}">${l}</button>`;
    let body = '';
    if (this.mode === 'measure') {
      const d = this.length();
      if (this.points.length < 2) body = '<div class="mp-hint">Click the map to add points (they snap to markers). Right-click takes the last one back.</div>';
      else {
        body = `<div class="mp-total">${fmtDist(d)}${this.points.length > 2 ? ` <small>${this.points.length - 1} legs</small>` : ''}</div>
          <table class="mp-times">${TRAVEL.map((t) => `<tr><td>${t.label}</td><td>${fmtTime(d / t.speed)}</td></tr>`).join('')}</table>
          <div class="mp-hint">Straight lines: around hills and coasts takes longer. Boats with a fair wind.</div>`;
      }
      body += `<div class="mp-row"><button class="btn small" data-act="clear"${this.points.length ? '' : ' disabled'}>Clear</button></div>`;
    } else {
      body = `<div class="mp-row"><select class="sel" data-act="travel">${TRAVEL.map((t) => `<option value="${t.id}"${t.id === this.travel ? ' selected' : ''}>${t.label}</option>`).join('')}</select>
        <span class="mp-chips">${MINUTES.map((m) => `<button data-min="${m}" class="${m === this.minutes ? 'on' : ''}">${m} min</button>`).join('')}</span></div>`;
      const r = this.reach;
      body += r ? (r.busy || r.none ? `<div class="mp-hint">${r.busy ? 'Working it out…' : 'No way to travel like that from there.'}</div>` : `<div class="mp-hint">${this.travelInfo().label} from ${escape(r.label || `${Math.round(r.x)}, ${Math.round(r.z)}`)}, ${this.travelInfo().kind === 'water' ? 'over water' : 'over land'}:</div>
          <div class="mp-key">${reachBands(this.minutes).map((b) => `<span><i style="background:rgb(${b.colour.join(',')})"></i>${b.from ? fmtMin(b.from).replace(' min', '') + '–' : 'under '}${fmtMin(b.to)}</span>`).join('')}</div>`)
        : '<div class="mp-hint">Click the map where you start.</div>';
    }
    p.innerHTML = `<div class="mp-head"><span class="mp-seg">${seg('measure', 'Measure')}${seg('reach', 'Reach')}</span><button class="icon-btn" data-act="close" title="Close (Esc)"><svg><use href="#i-close"/></svg></button></div>${body}`;
    p.querySelectorAll('[data-mode]').forEach((b) => b.addEventListener('click', () => { this.mode = b.dataset.mode; this.points = []; this.reach = null; this.route.changed(); this.tint.clear(); this.render(); }));
    p.querySelector('[data-act=close]').addEventListener('click', () => this.stop());
    p.querySelector('[data-act=clear]')?.addEventListener('click', () => { this.points = []; this.route.changed(); this.render(); });
    p.querySelector('[data-act=travel]')?.addEventListener('change', (e) => { this.travel = e.target.value; this.again(); });
    p.querySelectorAll('[data-min]').forEach((b) => b.addEventListener('click', () => { this.minutes = +b.dataset.min; this.again(); }));
  }

  travelInfo() { return TRAVEL.find((t) => t.id === this.travel) || TRAVEL[0]; }
  again() { if (this.reach && !this.reach.busy) this.reachFrom(this.reach); else this.render(); }

  async reachFrom(at) {
    const t = this.travelInfo(), maxSec = this.minutes * 60;
    this.reach = { ...at, busy: true };
    this.render();
    const res = await this.ask({ x: at.x, z: at.z, speed: t.speed, kind: t.kind, maxSec, radius: t.speed * maxSec * 1.05 + 100 });
    if (!this.active || this.mode !== 'reach') return;
    this.reach = { ...at, busy: false, none: !res.started };
    this.tint.set(res, maxSec, t.kind);
    this.render();
  }

  // a question for the reach worker
  ask(q) {
    if (!this.worker) {
      this.worker = new Worker(new URL('./reachworker.js', import.meta.url), { type: 'module' });
      this.worker.onmessage = (e) => { const r = this.pending.get(e.data.id); if (r) { this.pending.delete(e.data.id); r(e.data); } };
    }
    const id = this.nextId++, base = new URL('tiles/height/', location.href).href;
    const suffix = worldTile('x').slice(1);   // the world's query string (?g=...&w=...)
    return new Promise((resolve) => {
      this.pending.set(id, resolve);
      this.worker.postMessage({ id, hmax: this.app.config?.height_max_zoom ?? -1, base, suffix, ...q });
    });
  }

  // how long from (x, z) to each target over land, the whole world (the Stats panel's distances)
  walkTimes(x, z, targets) {
    return this.ask({ x, z, speed: 5, kind: 'land', maxSec: 0, radius: 11000, targets });
  }
}

// ---- the route: lines, points and leg lengths, over the fog and under the markers
class RouteGL extends L.Layer {
  constructor(tool) { super(); this.tool = tool; this.order = 10.6; this.textTex = new Map(); }
  onAdd(map) { this.sc = ShapesCanvas.for(map); this.sc.add(this); }
  onRemove() { this.sc.remove(this); this.sc.redraw(); }
  need() {}
  changed() { this.sc?.redraw(); }
  draw(gl, v, sc) {
    const pts = this.tool.points;
    if (!pts.length) return;
    const l = sc.line;
    sc.setView(l);
    gl.bindVertexArray(sc.quadVao);
    gl.uniform1f(l.u.u_on, 0); gl.uniform1f(l.u.u_off, 0); gl.uniform1f(l.u.u_zoom, v.zoom); gl.uniform1f(l.u.u_ppm, v.ppm);
    for (const [width, color, alpha] of [[5, [0.05, 0.05, 0.05], 0.55], [2.4, [1, 0.85, 0.4], 1]]) {
      gl.uniform1f(l.u.u_width, width); gl.uniform3f(l.u.u_color, ...color); gl.uniform1f(l.u.u_alpha, alpha);
      for (let i = 1; i < pts.length; i++) { gl.uniform2f(l.u.u_a, pts[i - 1].x, pts[i - 1].z); gl.uniform2f(l.u.u_b, pts[i].x, pts[i].z); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4); }
    }
    const r = sc.ring;
    sc.setView(r);
    gl.uniform1f(r.u.u_n, 1);
    for (const [px, color, fill] of [[6, [0.05, 0.05, 0.05], 1], [4, [1, 0.85, 0.4], 1]]) {
      gl.uniform3f(r.u.u_color, ...color); gl.uniform1f(r.u.u_alpha, 1); gl.uniform1f(r.u.u_fill, fill); gl.uniform1f(r.u.u_r, px / v.ppm);
      for (const p of pts) { gl.uniform2f(r.u.u_c, p.x, p.z); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4); }
    }
    // each leg's length at its middle (when there is room), and the total at the end
    const t = sc.tex;
    sc.setView(t);
    gl.activeTexture(gl.TEXTURE0); gl.uniform1i(t.u.u_tex, 0); gl.uniform4f(t.u.u_uv, 0, 0, 1, 1); gl.uniform1f(t.u.u_alpha, 1);
    const label = (s, x, z, dx, dy) => {
      const cv = textImage(s);
      gl.bindTexture(gl.TEXTURE_2D, this.texture(gl, cv));
      gl.uniform4f(t.u.u_rect, x + dx / v.ppm, z - dy / v.ppm, cv.w / v.ppm, cv.h / v.ppm);
      gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    };
    for (let i = 1; i < pts.length; i++) {
      const a = pts[i - 1], b = pts[i], d = Math.hypot(b.x - a.x, b.z - a.z);
      if (d * v.ppm < 70 || pts.length === 2) continue;
      const s = fmtDist(d), cv = textImage(s);
      label(s, (a.x + b.x) / 2, (a.z + b.z) / 2, -cv.w / 2, -cv.h / 2);
    }
    if (pts.length > 1) { const e = pts[pts.length - 1]; label(fmtDist(this.tool.length()), e.x, e.z, 9, -8); }
    gl.bindVertexArray(null);
  }
  texture(gl, cv) {
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
    if (this.textTex.size > 200) { for (const x of this.textTex.values()) gl.deleteTexture(x); this.textTex.clear(); }
    return t;
  }
}

// ---- the reach: everywhere within the minutes, tinted, with a line each few minutes; under the fog
class ReachGL extends L.Layer {
  constructor(tool) { super(); this.tool = tool; this.order = 8.95; }
  onAdd(map) { this.sc = ShapesCanvas.for(map); this.sc.add(this); }
  onRemove() { this.sc.remove(this); this.sc.redraw(); }
  need() {}
  clear() { this.res = null; this.dirty = true; this.sc?.redraw(); }
  set(res, maxSec, kind) { this.res = res; this.maxSec = maxSec; this.kind = kind; this.dirty = true; this.sc?.redraw(); }
  draw(gl, v, sc) {
    if (this.dirty) {
      this.dirty = false;
      if (this.t) { gl.deleteTexture(this.t); this.t = null; }
      if (this.res) this.t = this.texture(gl);
    }
    if (!this.t) return;
    const r = this.res, p = sc.tex;
    sc.setView(p);
    gl.bindVertexArray(sc.quadVao);
    gl.activeTexture(gl.TEXTURE0); gl.uniform1i(p.u.u_tex, 0); gl.bindTexture(gl.TEXTURE_2D, this.t);
    gl.uniform4f(p.u.u_uv, 0, 0, 1, 1); gl.uniform1f(p.u.u_alpha, 1);
    gl.uniform4f(p.u.u_rect, r.x0, r.z0, r.w * r.cell, r.h * r.cell);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    gl.bindVertexArray(null);
  }
  texture(gl) {
    const water = this.kind === 'water';
    const { w, h, t } = this.res, max = this.maxSec, bands = reachBands(max / 60), band = (bands[0].to - bands[0].from) * 60;
    const img = new Uint8Array(w * h * 4);
    const bandOf = (s) => (s <= max ? Math.floor(s / band) : -1);
    for (let j = 0; j < h; j++)
      for (let i = 0; i < w; i++) {
        const k = j * w + i, s = t[k];
        if (!(s <= max)) continue;
        const b = bandOf(s);
        const edge = (i + 1 < w && bandOf(t[k + 1]) !== b) || (j + 1 < h && bandOf(t[k + w]) !== b) || (i > 0 && bandOf(t[k - 1]) !== b) || (j > 0 && bandOf(t[k - w]) !== b);
        const col = bands[Math.min(bands.length - 1, b)].colour, a = edge ? 0.9 : water ? 0.55 : 0.42;   // (stronger over the dark sea)
        const o = k * 4;   // premultiplied
        img[o] = col[0] * a; img[o + 1] = col[1] * a; img[o + 2] = col[2] * a; img[o + 3] = a * 255;
      }
    const tex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, img);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    return tex;
  }
}

const cache = new Map();
function textImage(s) {
  let c = cache.get(s);
  if (c) return c;
  const R = 2, font = `700 ${11.5 * R}px system-ui, sans-serif`;
  c = document.createElement('canvas');
  const g = c.getContext('2d');
  g.font = font;
  const w = Math.ceil(g.measureText(s).width) + 8 * R;
  c.width = w; c.height = 16 * R;
  g.font = font; g.textBaseline = 'middle'; g.lineJoin = 'round';
  g.lineWidth = 3.5 * R; g.strokeStyle = 'rgba(0,0,0,.8)'; g.strokeText(s, 4 * R, 8 * R);
  g.fillStyle = '#ffd866'; g.fillText(s, 4 * R, 8 * R);
  c.w = w / R; c.h = 16;
  cache.set(s, c);
  if (cache.size > 300) cache.clear();
  return c;
}
