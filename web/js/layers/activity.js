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

// cells [[zx, zz, value...]] -> an image overlay; color(cell) gives [r, g, b, a] or null. Filled
// squares, one pixel a zone; or (ring) each square outlined with a faint fill, ten pixels a zone,
// so it reads apart from a filled layer under it (lag hotspots over time spent).
function gridOverlay(map, cells, color, className, ring = false) {
  if (!cells.length) return null;
  let x0 = Infinity, x1 = -Infinity, z0 = Infinity, z1 = -Infinity;
  for (const c of cells) { x0 = Math.min(x0, c[0]); x1 = Math.max(x1, c[0]); z0 = Math.min(z0, c[1]); z1 = Math.max(z1, c[1]); }
  const k = ring ? 10 : 1, w = (x1 - x0 + 1) * k, h = (z1 - z0 + 1) * k;
  const cv = document.createElement('canvas');
  cv.width = w; cv.height = h;
  const g = cv.getContext('2d'), img = g.createImageData(w, h);
  for (const c of cells) {
    const rgba = color(c);
    if (!rgba) continue;
    const cx = (c[0] - x0) * k, cy = (z1 - c[1]) * k;   // row 0 = north
    for (let y = 0; y < k; y++) for (let x = 0; x < k; x++) {
      const edge = ring && (x < 2 || y < 2 || x >= k - 2 || y >= k - 2);
      const o = ((cy + y) * w + cx + x) * 4;
      img.data[o] = rgba[0]; img.data[o + 1] = rgba[1]; img.data[o + 2] = rgba[2];
      img.data[o + 3] = !ring || edge ? rgba[3] : Math.round(rgba[3] * 0.22);
    }
  }
  g.putImageData(img, 0, 0);
  const west = x0 * CELL - 32, south = z0 * CELL - 32, east = (x1 + 1) * CELL - 32, north = (z1 + 1) * CELL - 32;
  if (map._activityGL) return new ZoneGL(cv, west, north, east - west, north - south).addTo(map);
  return L.imageOverlay(cv.toDataURL(), L.latLngBounds(toLatLng(west, south), toLatLng(east, north)), { pane: pane(map), interactive: false, className: 'zone-overlay ' + className }).addTo(map);
}

// a zone layer in the WebGL canvas, under the trails (an image overlay over the canvas hid them)
class ZoneGL extends L.Layer {
  constructor(cv, x, z, w, h) { super(); this.cv = cv; this.rect = [x, z, w, h]; this.order = 9.7; }   // over the fog and the grid, under the trails
  onAdd(map) { this.sc = ShapesCanvas.for(map); this.sc.add(this); }
  onRemove() { if (this.tex) this.sc.gl.deleteTexture(this.tex); this.tex = null; this.sc.remove(this); }
  need() {}
  draw(gl, v, sc) {
    if (!this.tex) {
      this.tex = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, this.tex);
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, this.cv);
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
      for (const [k, val] of [[gl.TEXTURE_MIN_FILTER, gl.NEAREST], [gl.TEXTURE_MAG_FILTER, gl.NEAREST], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) gl.texParameteri(gl.TEXTURE_2D, k, val);
    }
    const p = sc.tex;
    sc.setView(p);
    gl.bindVertexArray(sc.quadVao);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.tex);
    gl.uniform1i(p.u.u_tex, 0);
    gl.uniform4f(p.u.u_rect, ...this.rect);
    gl.uniform4f(p.u.u_uv, 0, 0, 1, 1);
    gl.uniform1f(p.u.u_alpha, 1);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    gl.bindVertexArray(null);
  }
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
// seconds ago as "just now", "25 min ago", "3 h ago", "2 days ago"
export function fmtAgo(sec) {
  if (sec < 90) return 'just now';
  if (sec < 3600) return `${Math.round(sec / 60)} min ago`;
  if (sec < 48 * 3600) return `${Math.round(sec / 3600)} h ago`;
  return `${Math.round(sec / 86400)} days ago`;
}
// a unix time as "10:42", or "Tue 10:42" (withDay, or when not today)
function fmtClock(t, withDay) {
  const d = new Date(t * 1000), today = new Date().toDateString() === d.toDateString();
  return d.toLocaleString(undefined, { ...(withDay || !today ? { weekday: 'short' } : {}), hour: '2-digit', minute: '2-digit' });
}

// seconds as "2 h 10 min", "25 min", "under a minute"
export function fmtSpan(sec) {
  const m = Math.round(sec / 60);
  if (m < 1) return 'under a minute';
  if (m < 60) return `${m} min`;
  const h = Math.floor(m / 60);
  return h >= 48 ? `${Math.round(h / 24)} days` : `${h} h${m % 60 ? ` ${m % 60} min` : ''}`;
}


// a player's colour, the same as their arrow on the map (layers/players.js), as #rrggbb
export function playerColor(name) {
  const pl = window.app?.layers?.players;
  return pl?.color ? pl.color(name) : `hsl(${hue(name)} 85% 60%)`;
}
function colorRgb(name) {
  const c = playerColor(name);
  if (c[0] !== '#') return hslRgb(hue(name));
  const n = parseInt(c.slice(1), 16);
  return [(n >> 16) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
}

// hsl(h 85% 60%) as 0..1 rgb (no players layer: a colour from the name)
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
    const t0 = { name: tr.name, rgb: colorRgb(tr.name), pieces: [], last: null };
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
    this.gl = gl;   // trails and zone layers in the WebGL canvas (TrailsGL, ZoneGL), else Leaflet lines and images
    if (gl) map._activityGL = true;
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
    // trails every 15 s (the server samples each 10 s), the zone layers each minute
    if (Object.values(this.on).some(Boolean)) this.timer = setInterval(() => this.refresh(), this.on.trails ? 15000 : 60000);
    this.hookPlayers();
  }

  // Between fetches the trails grow from the live positions (the players feed, about once a second),
  // so a trail follows its player as they walk. Kept until the server's own samples cover them.
  hookPlayers() {
    const pl = window.app?.layers?.players;
    if (this.hooked || !pl?.onChange) return;
    this.hooked = true;
    this.live = new Map();   // name -> [[x, z, t]...] newer than the server's trail
    pl.onChange((players) => {
      const a = this.activity;
      if (!this.on.trails || !a) return;
      const t = Date.now() / 1000 + (this.skew || 0);
      let moved = false;
      for (const p of players) {
        if (p.x === undefined || p.dead) continue;
        let tr = a.trails.find((r) => r.name === p.name);
        if (!tr) a.trails.push((tr = { name: p.name, pts: [] }));
        const last = tr.pts[tr.pts.length - 1];
        if (last && (Math.hypot(p.x - last[0], p.z - last[1]) < 3 || t - last[2] < 2)) continue;
        const pt = [p.x, p.z, t];
        tr.pts.push(pt);
        if (!this.live.has(p.name)) this.live.set(p.name, []);
        this.live.get(p.name).push(pt);
        moved = true;
      }
      if (!moved) return;
      a.now = Math.max(a.now, t);
      if (!this.liveQueued) { this.liveQueued = true; setTimeout(() => { this.liveQueued = false; this.drawTrails(); }, 1000); }
    });
  }

  onChange(f) { this.listeners.add(f); }
  changed() { for (const f of this.listeners) f(this); }

  setHours(h) { this.hours = h; this.refresh(); }
  setDays(d) { this.days = d; this.refresh(); }
  toggleHidden(name) { if (!this.hidden.delete(name)) this.hidden.add(name); this.draw(); this.changed(); }
  names() { return (this.activity?.trails || []).map((t) => t.name).sort((x, y) => x.localeCompare(y)); }
  colorOf(name) { return playerColor(name); }

  async refresh() {
    const wantActivity = this.on.trails || this.on.heat || this.on.deaths;
    try {
      if (wantActivity) {
        const a = await getJSON(`data/activity.json?hours=${this.on.trails ? this.hours : 3}`);
        this.skew = a.now - Date.now() / 1000;
        // the live points newer than what the server has kept go on the end
        for (const [name, pts] of this.live || []) {
          const tr = a.trails.find((r) => r.name === name), lastT = tr?.pts.length ? tr.pts[tr.pts.length - 1][2] : -Infinity;
          const newer = pts.filter((p) => p[2] > lastT);
          this.live.set(name, newer);
          if (!newer.length) continue;
          if (tr) tr.pts.push(...newer); else a.trails.push({ name, pts: newer });
          a.now = Math.max(a.now, newer[newer.length - 1][2]);
        }
        this.activity = a;
      }
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
      this.drawn.hotspots = gridOverlay(this.map, this.hot, (c) => { const t = Math.sqrt(score(c) / max); return [...ramp(LOAD, t), Math.round(170 + 85 * t)]; }, 'hotspots', true);
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

  // A click (not on a marker) tells about everything shown there, a section each: the trail under
  // it (whose, when they passed), the square's time spent and deaths, its lag, its building.
  async onClick(e) {
    if (window.app?.measuring) return;
    const mc = window.app?.layers?.markers?.canvas;
    if (mc?.hit?.(e.containerPoint)) return;
    const { x, z } = fromLatLng(e.latlng);
    const [zx, zz] = zoneOf(x, z), key = zx + ',' + zz;
    const parts = [];
    const tr = this.on.trails && this.trailAt(e.containerPoint);
    if (tr) parts.push(this.trailHtml(tr));
    const wantHeat = (this.on.heat && this.activity?.heat.some((c) => c[0] === zx && c[1] === zz)) || (this.on.deaths && this.activity?.deaths.some((c) => c[0] === zx && c[1] === zz));
    const hot = this.on.hotspots && this.hotByZone?.get(key), built = this.on.building && this.buildByZone?.get(key);
    if (!tr && !wantHeat && !hot && !built) return;
    if (wantHeat) {
      try { parts.push(this.heatHtml(await getJSON(`data/activity/zone.json?zx=${zx}&zz=${zz}`))); } catch (err) { /* the rest still shows */ }
    }
    if (hot) parts.push(this.hotHtml(hot));
    if (built) parts.push(this.buildHtml(built));
    if (!parts.length) return;
    const zoneShown = wantHeat || hot || built, base = zoneShown && this.nearestBase(zx * CELL, zz * CELL);
    const head = zoneShown ? `<div class="ap-head"><b>${base ? escape(base) : 'This square'}</b><small>64 m square around ${zx * CELL}, ${zz * CELL}</small></div>` : '';
    L.popup({ offset: [0, -4], maxWidth: 320, className: 'act-popup' })
      .setLatLng(tr && !zoneShown ? e.latlng : toLatLng(zx * CELL, zz * CELL))
      .setContent(`<div class="act-pop">${head}${parts.join('')}</div>`).openOn(this.map);
  }

  // the shown trail nearest the click, within 8 px: {tr, i (the point passed nearest), d}
  trailAt(cp) {
    const a = this.activity;
    if (!a) return null;
    const map = this.map, at = (p) => map.latLngToContainerPoint(toLatLng(p[0], p[1]));
    let best = null;
    for (const tr of a.trails) {
      if (this.hidden.has(tr.name)) continue;
      let prev = null;
      for (let i = 0; i < tr.pts.length; i++) {
        const q = at(tr.pts[i]);
        if (prev && Math.hypot(tr.pts[i][0] - tr.pts[i - 1][0], tr.pts[i][1] - tr.pts[i - 1][1]) <= 250) {
          const dx = q.x - prev.x, dy = q.y - prev.y, l2 = dx * dx + dy * dy || 1;
          const f = Math.max(0, Math.min(1, ((cp.x - prev.x) * dx + (cp.y - prev.y) * dy) / l2));
          const d = Math.hypot(cp.x - (prev.x + dx * f), cp.y - (prev.y + dy * f));
          if (d <= 8 && (!best || d < best.d)) best = { tr, i: f < 0.5 ? i - 1 : i, d };
        }
        prev = q;
      }
    }
    return best;
  }

  trailHtml({ tr, i }) {
    const a = this.activity, now = a.now, p = tr.pts[i], last = tr.pts[tr.pts.length - 1];
    let km = 0;
    for (let k = 1; k < tr.pts.length; k++) { const d = Math.hypot(tr.pts[k][0] - tr.pts[k - 1][0], tr.pts[k][1] - tr.pts[k - 1][1]); if (d <= 250) km += d / 1000; }
    const online = (window.app?.layers?.players?.players || []).some((q) => q.name === tr.name && q.x !== undefined);
    return `<div class="ap-sec"><h5><i class="hd" style="background:${this.colorOf(tr.name)}"></i>${escape(tr.name)}'s trail</h5>
      <dl class="ap-kv"><dt>Passed here</dt><dd>${fmtAgo(now - p[2])} <small>${escape(fmtClock(p[2]))}</small></dd>
      <dt>Walked</dt><dd>${km < 1 ? Math.round(km * 1000) + ' m' : km.toFixed(1) + ' km'} <small>in the last ${a.hours || 3} hours</small></dd>
      <dt>Now</dt><dd>${online ? 'online' : 'last seen ' + fmtAgo(now - last[2])}</dd></dl></div>`;
  }

  heatHtml(d) {
    const named = d.players.reduce((n, p) => n + p[1], 0), rest = d.seconds - named, max = Math.max(1, ...d.players.map((p) => p[1]));
    const rows = d.players.slice(0, 8).map(([name, sec, last]) => `<div class="ap-who">
        <i class="hd" style="background:${this.colorOf(name)}"></i><span class="ap-name">${escape(name)}</span>
        <span class="ap-time">${fmtSpan(sec)}</span><small>${fmtAgo(Date.now() / 1000 + (this.skew || 0) - last)}</small>
        <span class="ap-bar"><i style="width:${Math.max(3, Math.round(100 * sec / max))}%;background:${this.colorOf(name)}"></i></span></div>`).join('');
    let html = '';
    if (this.on.heat && d.seconds) {
      html += `<div class="ap-sec"><h5>Time spent here<span>${fmtSpan(d.seconds)}</span></h5>
        ${rows ? `<div class="ap-cols"><span>Who</span><span>Time</span><span>Last here</span></div>${rows}` : ''}
        ${d.players.length > 8 ? `<small class="ap-note">and ${d.players.length - 8} more</small>` : ''}
        ${rest > 600 ? `<small class="ap-note">${rows ? fmtSpan(rest) + ' more' : 'All of it'} from before the map noted who.</small>` : ''}</div>`;
    }
    if (this.on.deaths && d.deaths) html += `<div class="ap-sec"><h5>Deaths here<span>${d.deaths}</span></h5></div>`;
    return html;
  }

  hotHtml(c) {
    const rows = [['Build pieces', c[2]], ['Lights & fires', c[3]], ['Creatures', c[4]], ['Items on the ground', c[5]]]
      .map(([k, v]) => `<dt>${k}</dt><dd>${v.toLocaleString()}</dd>`).join('');
    return `<div class="ap-sec"><h5>Lag hotspot</h5><dl class="ap-kv">${rows}</dl>
      <small class="ap-note">Each light and creature costs players' frame rate far more than a build piece.</small></div>`;
  }

  buildHtml(b) {
    const d = b[2] - b[3];
    return `<div class="ap-sec"><h5>${d > 0 ? 'Built' : 'Taken down'}<span>${d > 0 ? '+' : '−'}${Math.abs(d).toLocaleString()} pieces</span></h5>
      <dl class="ap-kv"><dt>Then <small>(${escape(fmtClock(this.building.since, true))})</small></dt><dd>${b[3].toLocaleString()}</dd><dt>Now</dt><dd>${b[2].toLocaleString()}</dd></dl></div>`;
  }

  nearestBase(x, z) {
    const set = (window.app?.layers?.markers?.sets || []).find((s) => s.id === 'bases');
    let best = null, bd = 160;
    for (const m of set?.markers || []) { const d = Math.hypot(m.x - x, m.z - z); if (d < bd) { bd = d; best = m.label; } }
    return best;
  }
}
