// Where players go and what weighs on their frame rate, drawn over the map (each off at first):
//   trails      each player's path over the last hours, fading with age (data/activity.json)
//   heat        time players have spent in each 64 m zone, all time
//   deaths      deaths per zone, all time
//   hotspots    zones heavy with build pieces, lights, creatures or dropped items (data/hotspots.json)
// The zone layers are one pixel per zone on a small canvas, laid over the map as an image.
import { toLatLng, fromLatLng } from '../crs.js';
import { getJSON } from '../net.js';
import { escape } from './markers.js';

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

export class ActivityLayers {
  constructor(map) {
    this.map = map;
    this.on = { trails: false, heat: false, deaths: false, hotspots: false };
    this.drawn = { trails: [], heat: null, deaths: null, hotspots: null };
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

  async refresh() {
    const wantActivity = this.on.trails || this.on.heat || this.on.deaths;
    try {
      if (wantActivity) this.activity = await getJSON('data/activity.json');
      if (this.on.hotspots) {
        const h = await getJSON('data/hotspots.json');
        this.hot = h.zones || [];
        this.hotByZone = new Map(this.hot.map((c) => [c[0] + ',' + c[1], c]));
      }
    } catch (e) { console.warn('activity', e); }
    this.draw();
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
    if (this.on.trails && a) {
      // each trail in pieces by age (older fainter), broken where a portal or a gap jumps it
      for (const tr of a.trails) {
        const color = `hsl(${hue(tr.name)} 85% 60%)`, span = 3 * 3600;
        let seg = [], segAge = -1;
        const flush = () => {
          if (seg.length > 1) {
            const l = L.polyline(seg, { renderer: this.renderer, color, weight: 3, opacity: 0.25 + 0.65 * (1 - segAge / 5), interactive: false });
            this.drawn.trails.push(l.addTo(this.map));
          }
        };
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
          seg.push(toLatLng(x, z));
        }
        flush();
        const last = tr.pts[tr.pts.length - 1];
        if (last) this.drawn.trails.push(L.circleMarker(toLatLng(last[0], last[1]), { renderer: this.renderer, radius: 3, color, fillColor: color, fillOpacity: 1, weight: 1, interactive: false })
          .bindTooltip(escape(tr.name), { permanent: false }).addTo(this.map));
      }
    }
  }

  // a click on a hotspot zone (not on a marker): what is there
  onClick(e) {
    if (!this.on.hotspots || !this.hotByZone) return;
    const mc = window.app?.layers?.markers?.canvas;
    if (mc?.hit?.(e.containerPoint)) return;
    const { x, z } = fromLatLng(e.latlng);
    const [zx, zz] = zoneOf(x, z), c = this.hotByZone.get(zx + ',' + zz);
    if (!c) return;
    const base = this.nearestBase(zx * CELL, zz * CELL);
    const rows = [['Build pieces', c[2]], ['Lights & fires', c[3]], ['Creatures', c[4]], ['Items on the ground', c[5]]]
      .map(([k, v]) => `<tr><td>${k}</td><td>${v.toLocaleString()}</td></tr>`).join('');
    L.popup({ offset: [0, -4] }).setLatLng(toLatLng(zx * CELL, zz * CELL)).setContent(
      `<b>Busy area${base ? ' · ' + escape(base) : ''}</b><small>this 64 m square</small><table class="hot-table">${rows}</table>` +
      '<small>Each light and creature costs players\' frame rate far more than a build piece.</small>').openOn(this.map);
  }

  nearestBase(x, z) {
    const set = (window.app?.layers?.markers?.sets || []).find((s) => s.id === 'bases');
    let best = null, bd = 160;
    for (const m of set?.markers || []) { const d = Math.hypot(m.x - x, m.z - z); if (d < bd) { bd = d; best = m.label; } }
    return best;
  }
}
