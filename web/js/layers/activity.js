// Where players go and what weighs on their frame rate, drawn over the map (each off at first):
//   trails      each player's path over the last 3 or 24 hours, fading with age, players can be
//               left out (data/activity.json?hours=)
//   heat        time players have spent in each 64 m zone, all time; click one for who and how long
//   deaths      deaths per zone, all time
//   hotspots    zones heavy with build pieces, lights, creatures or dropped items (data/hotspots.json)
//   building    zones where build pieces went up or came down over the last day or week (data/building.json?days=)
// The zone layers are one pixel per zone on a small canvas, laid over the map as an image.
import { toLatLng, fromLatLng } from '../crs.js';
import { getJSON } from '../net.js';
import { escape } from './markers.js';
import { ShapesCanvas } from './shapes.js';

const CELL = 64;
const zoneOf = (x, z) => [Math.floor((x + 32) / CELL), Math.floor((z + 32) / CELL)];

function pane(map) {
  if (!map.getPane('activityPane')) {
    const p = map.createPane('activityPane');
    p.style.zIndex = 430;   // over the fog and buildings, under the world edge and markers
    p.style.pointerEvents = 'none';
  }
  return 'activityPane';
}

// cells [[zx, zz, value...]] -> an image overlay; color(cell) gives [r, g, b, a] or null
function gridOverlay(map, cells, color, className) {
  if (!cells.length) return null;
  let x0 = Infinity, x1 = -Infinity, z0 = Infinity, z1 = -Infinity;
  for (const c of cells) { x0 = Math.min(x0, c[0]); x1 = Math.max(x1, c[0]); z0 = Math.min(z0, c[1]); z1 = Math.max(z1, c[1]); }
  const w = x1 - x0 + 1, h = z1 - z0 + 1;
  const cv = document.createElement('canvas');
  cv.width = w; cv.height = h;
  const g = cv.getContext('2d'), img = g.createImageData(w, h);
  for (const c of cells) {
    const rgba = color(c);
    if (!rgba) continue;
    const o = ((z1 - c[1]) * w + (c[0] - x0)) * 4;   // row 0 = north
    img.data[o] = rgba[0]; img.data[o + 1] = rgba[1]; img.data[o + 2] = rgba[2]; img.data[o + 3] = rgba[3];
  }
  g.putImageData(img, 0, 0);
  const sw = toLatLng(x0 * CELL - 32, z0 * CELL - 32), ne = toLatLng((x1 + 1) * CELL - 32, (z1 + 1) * CELL - 32);
  return L.imageOverlay(cv.toDataURL(), L.latLngBounds(sw, ne), { pane: pane(map), interactive: false, className: 'zone-overlay ' + className }).addTo(map);
}

// t in 0..1 along a ramp of [r, g, b] stops
function ramp(stops, t) {
  t = Math.max(0, Math.min(1, t)) * (stops.length - 1);
  const i = Math.min(stops.length - 2, Math.floor(t)), f = t - i, a = stops[i], b = stops[i + 1];
  return [0, 1, 2].map((k) => Math.round(a[k] + (b[k] - a[k]) * f));
}
const HEAT = [[40, 60, 200], [40, 190, 220], [120, 220, 80], [250, 210, 50], [240, 70, 40]];
const LOAD = [[250, 220, 80], [250, 140, 40], [235, 50, 40], [190, 30, 120]];

const hue = (name) => { let h = 0; for (const c of name) h = (h * 31 + c.charCodeAt(0)) >>> 0; return h % 360; };
// seconds as "2 h 10 min", "25 min", "under a minute"
export function fmtSpan(sec) {
  const m = Math.round(sec / 60);
  if (m < 1) return 'under a minute';
  if (m < 60) return `${m} min`;
  const h = Math.floor(m / 60);
  return h >= 48 ? `${Math.round(h / 24)} days` : `${h} h${m % 60 ? ` ${m % 60} min` : ''}`;
}

// a time, short: "Tue 21:40", or "12 Oct" when older than a week
export function fmtWhen(iso) {
  const d = new Date(iso);
  if (isNaN(d)) return '';
  if (Date.now() - d < 6.5 * 864e5) return d.toLocaleString(undefined, { weekday: 'short', hour: '2-digit', minute: '2-digit' });
  return d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
}

// hsl(h 85% 60%) as 0..1 rgb
function hslRgb(h) {
  const s = 0.85, l = 0.6, k = (n) => (n + h / 30) % 12, a = s * Math.min(l, 1 - l);
  return [0, 8, 4].map((n) => l - a * Math.max(-1, Math.min(k(n) - 3, 9 - k(n), 1)));
}

// Each trail split into pieces by age (older fainter), broken where a portal or a gap jumps it:
// [{name, rgb, pieces: [{alpha, pts: [[x, z]...]}], last: [x, z]}]
function trailPieces(a, hidden) {
  const span = (a.hours || 3) * 3600, out = [];
  for (const tr of a.trails) {
    if (hidden.has(tr.name)) continue;
    const t0 = { name: tr.name, rgb: hslRgb(hue(tr.name)), pieces: [], last: null };
    let seg = [], segAge = -1;
    const flush = () => { if (seg.length > 1) t0.pieces.push({ alpha: 0.25 + 0.65 * (1 - segAge / 5), pts: seg }); };
    for (let i = 0; i < tr.pts.length; i++) {
      const [x, z, t] = tr.pts[i], age = Math.min(5, Math.floor((a.now - t) / (span / 6)));
      const prev = tr.pts[i - 1];
      const jump = prev && (Math.hypot(x - prev[0], z - prev[1]) > 250 || t - prev[2] > 180);
      if (jump || age !== segAge) {
        const keep = !jump && seg.length ? seg[seg.length - 1] : null;
        flush();
        seg = keep ? [keep] : [];
        segAge = age;
      }
      seg.push([x, z]);
    }
    flush();
    const last = tr.pts[tr.pts.length - 1];
    if (last) t0.last = [last[0], last[1]];
    out.push(t0);
  }
  return out;
}

// The trails in the WebGL canvas with the map, so they are redrawn at the exact zoom every frame
// of a glide. As Leaflet lines they were drawn a frame late with each point rounded to a pixel
// and the line simplified anew at every zoom, so they swam about while zooming.
class TrailsGL extends L.Layer {
  constructor() { super(); this.order = 9.8; this.n = 0; this.dots = []; }   // over the fog and the grid, under the world's edge
  onAdd(map) { this.sc = ShapesCanvas.for(map); this.sc.add(this); }
  onRemove() { this.sc.remove(this); }
  need() {}
  set(trails) {
    let n = 0;
    for (const t of trails) for (const p of t.pieces) n += p.pts.length - 1;
    const d = this.data = new Float32Array(n * 9);
    let i = 0, group = 0;
    for (const t of trails) {
      for (const p of t.pieces) {
        for (let k = 1; k < p.pts.length; k++, i += 9) d.set([p.pts[k - 1][0], p.pts[k - 1][1], p.pts[k][0], p.pts[k][1], ...t.rgb, p.alpha, group], i);
        group++;
      }
    }
    this.n = n;
    this.dots = trails.filter((t) => t.last).map((t) => ({ at: t.last, rgb: t.rgb }));
    this.sc?.redraw();
  }
  draw(gl, v, sc) {
    sc.drawLines(this.data, this.n, 3);
    if (!this.dots.length) return;
    const r = sc.ring;   // where each player is now (or was last): a dot
    sc.setView(r);
    gl.bindVertexArray(sc.quadVao);
    gl.uniform1f(r.u.u_n, 1); gl.uniform1f(r.u.u_alpha, 1); gl.uniform1f(r.u.u_fill, 1);
    for (const [px, dark] of [[4.5, true], [3, false]]) {
      gl.uniform1f(r.u.u_r, px / v.ppm);
      for (const d of this.dots) {
        gl.uniform3f(r.u.u_color, ...(dark ? [0.05, 0.05, 0.05] : d.rgb));
        gl.uniform2f(r.u.u_c, d.at[0], d.at[1]);
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
      }
    }
    gl.bindVertexArray(null);
  }
}

export class ActivityLayers {
  constructor(map, gl) {
    this.map = map;
    this.gl = gl;   // trails in the WebGL canvas (TrailsGL), else Leaflet lines
    this.on = { trails: false, heat: false, deaths: false, hotspots: false, building: false };
    this.drawn = { trails: [], heat: null, deaths: null, hotspots: null, building: null };
    this.hours = 3;                 // trails: the last 3 or 24 hours
    this.hidden = new Set();        // trails: players left out
    this.days = 1;                  // building: over the last day or week
    this.listeners = new Set();     // the Layers panel's controls, told when the data or a choice changes
    this.timer = null;
    this.renderer = L.canvas({ pane: pane(map), padding: 0.2 });
    map.on('click', (e) => this.onClick(e));
  }

  set(kind, v) {
    this.on[kind] = v;
    this.refresh();
    clearInterval(this.timer);
    if (Object.values(this.on).some(Boolean)) this.timer = setInterval(() => this.refresh(), 60000);
  }

  onChange(f) { this.listeners.add(f); }
  changed() { for (const f of this.listeners) f(this); }

  setHours(h) { this.hours = h; this.refresh(); }
  setDays(d) { this.days = d; this.refresh(); }
  toggleHidden(name) { if (!this.hidden.delete(name)) this.hidden.add(name); this.draw(); this.changed(); }
  names() { return (this.activity?.trails || []).map((t) => t.name).sort((x, y) => x.localeCompare(y)); }
  colorOf(name) { return `hsl(${hue(name)} 85% 60%)`; }

  async refresh() {
    const wantActivity = this.on.trails || this.on.heat || this.on.deaths;
    try {
      if (wantActivity) this.activity = await getJSON(`data/activity.json?hours=${this.on.trails ? this.hours : 3}`);
      if (this.on.building) this.building = await getJSON(`data/building.json?days=${this.days}`);
      if (this.on.hotspots) {
        const h = await getJSON('data/hotspots.json');
        this.hot = h.zones || [];
        this.hotPending = !!h.pending;
        this.hotByZone = new Map(this.hot.map((c) => [c[0] + ',' + c[1], c]));
      }
    } catch (e) { console.warn('activity', e); }
    this.draw();
    this.changed();
  }

  clear(kind) {
    const d = this.drawn[kind];
    if (Array.isArray(d)) { d.forEach((l) => l.remove()); this.drawn[kind] = []; } else if (d) { d.remove(); this.drawn[kind] = null; }
  }

  draw() {
    for (const k of Object.keys(this.on)) this.clear(k);
    const a = this.activity;
    if (this.on.heat && a) {
      // log scale: a minute here and there still shows, the busiest zones at the top
      const max = Math.log1p(Math.max(1, ...a.heat.map((c) => c[2])));
      this.drawn.heat = gridOverlay(this.map, a.heat, (c) => [...ramp(HEAT, Math.log1p(c[2]) / max), 150], 'heat');
    }
    if (this.on.deaths && a) {
      const max = Math.max(1, ...a.deaths.map((c) => c[2]));
      this.drawn.deaths = gridOverlay(this.map, a.deaths, (c) => [220, 30, 50, Math.round(110 + 145 * Math.sqrt(c[2] / max))], 'deaths');
    }
    if (this.on.hotspots && this.hot) {
      const score = (c) => c[2] + c[3] * 15 + c[4] * 12 + c[5] * 2;   // as World/Hotspots.Score
      const max = Math.max(1, ...this.hot.map(score));
      this.drawn.hotspots = gridOverlay(this.map, this.hot, (c) => { const t = Math.sqrt(score(c) / max); return [...ramp(LOAD, t), Math.round(90 + 130 * t)]; }, 'hotspots');
    }
    if (this.on.building && this.building) {
      const z = this.building.zones || [];
      const max = Math.max(1, ...z.map((c) => Math.abs(c[2] - c[3])));
      this.drawn.building = gridOverlay(this.map, z, (c) => {
        const d = c[2] - c[3], t = Math.sqrt(Math.abs(d) / max);
        return d > 0 ? [70, 220, 110, Math.round(120 + 135 * t)] : [235, 80, 70, Math.round(110 + 120 * t)];
      }, 'building');
      this.buildByZone = new Map(z.map((c) => [c[0] + ',' + c[1], c]));
    }
    this.drawTrails();
  }

  drawTrails() {
    this.clear('trails');
    const a = this.activity, show = this.on.trails && a;
    const trails = show ? trailPieces(a, this.hidden) : [];
    if (this.gl) {
      if (show) {
        if (!this.trailsGL) this.trailsGL = new TrailsGL();
        if (!this.trailsGL._map) this.trailsGL.addTo(this.map);
        this.trailsGL.set(trails);
      } else if (this.trailsGL?._map) this.trailsGL.remove();
    } else {
      for (const tr of trails) {
        const color = this.colorOf(tr.name);
        for (const p of tr.pieces) this.drawn.trails.push(L.polyline(p.pts.map(([x, z]) => toLatLng(x, z)), { renderer: this.renderer, color, weight: 3, opacity: p.alpha, interactive: false }).addTo(this.map));
        if (tr.last) this.drawn.trails.push(L.circleMarker(toLatLng(...tr.last), { renderer: this.renderer, radius: 3, color, fillColor: color, fillOpacity: 1, weight: 1, interactive: false }).addTo(this.map));
      }
    }
  }

  // a click on a zone (not on a marker): what changed there (building), what weighs there
  // (hotspots), or who spent how long there (time spent)
  onClick(e) {
    if (window.app?.measuring) return;
    const mc = window.app?.layers?.markers?.canvas;
    if (mc?.hit?.(e.containerPoint)) return;
    const { x, z } = fromLatLng(e.latlng);
    const [zx, zz] = zoneOf(x, z);
    const b = this.on.building && this.buildByZone?.get(zx + ',' + zz);
    if (b && !(this.on.hotspots && this.hotByZone?.get(zx + ',' + zz))) {
      const d = b[2] - b[3], base = this.nearestBase(zx * CELL, zz * CELL);
      L.popup({ offset: [0, -4] }).setLatLng(toLatLng(zx * CELL, zz * CELL)).setContent(
        `<b>${d > 0 ? 'Built' : 'Taken down'}${base ? ' · ' + escape(base) : ''}</b><small>this 64 m square, since ${escape(fmtWhen(new Date(this.building.since * 1000).toISOString()))}</small>` +
        `<table class="hot-table"><tr><td>Build pieces then</td><td>${b[3].toLocaleString()}</td></tr><tr><td>Now</td><td>${b[2].toLocaleString()}</td></tr></table>`).openOn(this.map);
      return;
    }
    if (this.on.heat && !(this.on.hotspots && this.hotByZone?.get(zx + ',' + zz))) { this.heatPopup(zx, zz); return; }
    if (!this.on.hotspots || !this.hotByZone) return;
    const c = this.hotByZone.get(zx + ',' + zz);
    if (!c) return;
    const base = this.nearestBase(zx * CELL, zz * CELL);
    const rows = [['Build pieces', c[2]], ['Lights & fires', c[3]], ['Creatures', c[4]], ['Items on the ground', c[5]]]
      .map(([k, v]) => `<tr><td>${k}</td><td>${v.toLocaleString()}</td></tr>`).join('');
    L.popup({ offset: [0, -4] }).setLatLng(toLatLng(zx * CELL, zz * CELL)).setContent(
      `<b>Busy area${base ? ' · ' + escape(base) : ''}</b><small>this 64 m square</small><table class="hot-table">${rows}</table>` +
      '<small>Each light and creature costs players\' frame rate far more than a build piece.</small>').openOn(this.map);
  }

  async heatPopup(zx, zz) {
    if (!this.activity?.heat.some((c) => c[0] === zx && c[1] === zz)) return;
    let d;
    try { d = await getJSON(`data/activity/zone.json?zx=${zx}&zz=${zz}`); } catch (e) { return; }
    const base = this.nearestBase(zx * CELL, zz * CELL), named = d.players.reduce((n, p) => n + p[1], 0);
    const rows = d.players.slice(0, 10).map(([name, sec, last]) =>
      `<tr><td><i class="hd" style="background:${this.colorOf(name)}"></i>${escape(name)}</td><td>${fmtSpan(sec)}</td><td>${escape(fmtWhen(new Date(last * 1000).toISOString()))}</td></tr>`).join('');
    const rest = d.seconds - named;
    L.popup({ offset: [0, -4] }).setLatLng(toLatLng(zx * CELL, zz * CELL)).setContent(
      `<b>${fmtSpan(d.seconds)} spent here${base ? ' · ' + escape(base) : ''}</b><small>this 64 m square, all players together${d.deaths ? `, ${d.deaths} death${d.deaths === 1 ? '' : 's'}` : ''}</small>` +
      (rows ? `<table class="hot-table heat-table"><tr><th>Who</th><th>Time</th><th>Last here</th></tr>${rows}</table>` : '') +
      (d.players.length > 10 ? `<small>and ${d.players.length - 10} more</small>` : '') +
      (rest > 600 ? `<small>${rows ? 'Another ' + fmtSpan(rest) : 'All of it'} from before who was here was recorded.</small>` : '')).openOn(this.map);
  }

  nearestBase(x, z) {
    const set = (window.app?.layers?.markers?.sets || []).find((s) => s.id === 'bases');
    let best = null, bd = 160;
    for (const m of set?.markers || []) { const d = Math.hypot(m.x - x, m.z - z); if (d < bd) { bd = d; best = m.label; } }
    return best;
  }
}
