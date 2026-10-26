// Live players: an arrow that points where they are facing, name, health
// bar, follow mode, and map pings. Positions come about once a second, each update stamped with
// the server's clock (ts). They are played back a little over one update late on that clock, on a
// smooth curve through the last few (Catmull-Rom), so a player moves at an even pace whatever the
// network does to the updates' arrival; the arrow turns the same way. A jump of over 100 m (a
// portal) is not curved through. A followed player keeps the map centred on them every frame.

import { toLatLng } from '../crs.js';
import { iconSvg } from '../icons.js';
import { escape } from './markers.js';
import { on } from '../net.js';

const PALETTE = ['#6fb7ff', '#f2c14e', '#58c27d', '#ff8fa3', '#c9a5ff', '#ffb04d', '#5ce0e6', '#e0e0e0'];

export class PlayersLayer {
  constructor(map) {
    this.map = map;
    this.group = L.layerGroup().addTo(map);
    this.markers = new Map();     // id -> marker
    this.players = [];
    this.following = null;        // player id
    this.listeners = new Set();
    this.colorOf = new Map();
    this.tracks = new Map();      // id -> [{ t (server ms), x, z, yaw }...], the last few updates
    this.gap = 1000;              // ms between updates on the server's clock (smoothed)
    this.offsets = [];            // recent (our clock - server clock) readings: the smallest is the least delayed
    on('players', (f) => this.update(f.data));
    on('ping', (f) => this.ping(f));
    on('shout', (f) => this.shout(f));
  }

  onChange(fn) { this.listeners.add(fn); }

  color(name) {
    if (!this.colorOf.has(name)) this.colorOf.set(name, PALETTE[this.colorOf.size % PALETTE.length]);
    return this.colorOf.get(name);
  }

  update(data) {
    this.players = data.players || [];
    const ts = data.ts || Date.now();
    if (this.lastTs && ts > this.lastTs) this.gap = Math.max(300, Math.min(2500, this.gap * 0.8 + (ts - this.lastTs) * 0.2));
    this.lastTs = ts;
    this.offsets.push(Date.now() - ts);
    if (this.offsets.length > 30) this.offsets.shift();
    this.offset = Math.min(...this.offsets);
    const seen = new Set();
    for (const p of this.players) {
      seen.add(p.id);
      if (p.x === undefined) { this.remove(p.id); continue; }
      const ll = toLatLng(p.x, p.z);
      let mk = this.markers.get(p.id);
      if (!mk) {
        mk = L.marker(ll, { icon: this.icon(p), zIndexOffset: 1000, keyboard: false });
        mk._look = `${p.name}|${!!p.dead}|${this.following === p.id}`;
        mk._exactPos = true;   // drawn at fractions of a pixel: walking speed is a fraction a frame, whole pixels stepped
        mk.bindTooltip('', { direction: 'top', offset: [0, -14] });
        mk.on('click', (e) => { const q = this.players.find((r) => r.id === p.id) || p; const oe = e.originalEvent; if (this.onClick) this.onClick(q, oe ? oe.clientX : 0, oe ? oe.clientY : 0); });
        this.markers.set(p.id, mk);
        this.group.addLayer(mk);
      } else {
        this.refresh(mk, p);
      }
      // this update on the player's track (the last few kept)
      let tr = this.tracks.get(p.id);
      if (!tr) { this.tracks.set(p.id, (tr = [])); mk.setLatLng(ll); }
      if (!tr.length || ts > tr[tr.length - 1].t) tr.push({ t: ts, x: p.x, z: p.z, yaw: p.yaw || 0 });
      if (tr.length > 6) tr.shift();
      mk.setTooltipContent(`<b>${escape(p.name)}</b><br>${p.health}/${p.maxHealth} hp · ${escape(p.biome || '')}${p.weather ? ` · ${escape(p.weather)}` : ''}<br>${p.x}, ${p.z}${p.dead ? ' · dead' : ''}${p.pvp ? ' · PvP' : ''}${p.inBed ? ' · sleeping' : ''}`);
    }
    for (const id of [...this.markers.keys()]) if (!seen.has(id)) this.remove(id);
    this.animate();
    if (this.following && !seen.has(this.following)) this.follow(null);
    for (const fn of this.listeners) fn(this.players);
  }

  remove(id) {
    const mk = this.markers.get(id);
    if (mk) { this.group.removeLayer(mk); this.markers.delete(id); }
    this.tracks.delete(id);
  }

  // where a player is shown at a moment of the server's clock: on a Catmull-Rom curve through the
  // updates around it (straight across a jump), held at the newest when the next is late
  at(tr, t) {
    if (!tr.length) return null;
    let i = tr.length - 2;
    while (i >= 0 && tr[i].t > t) i--;
    if (i < 0) return tr[0];
    const a = tr[i], b = tr[i + 1];
    if (!b || t >= b.t) return tr[tr.length - 1];
    const u = (t - a.t) / (b.t - a.t);
    if (Math.hypot(b.x - a.x, b.z - a.z) > 100) return u < 1 ? a : b;   // a portal: no flying across the map
    const p0 = tr[i - 1] && Math.hypot(a.x - tr[i - 1].x, a.z - tr[i - 1].z) <= 100 ? tr[i - 1] : a;
    const p3 = tr[i + 2] && Math.hypot(tr[i + 2].x - b.x, tr[i + 2].z - b.z) <= 100 ? tr[i + 2] : b;
    const cr = (q0, q1, q2, q3) => 0.5 * (2 * q1 + (q2 - q0) * u + (2 * q0 - 5 * q1 + 4 * q2 - q3) * u * u + (3 * q1 - q0 - 3 * q2 + q3) * u * u * u);
    const dy = ((b.yaw - a.yaw + 540) % 360) - 180;
    return { x: cr(p0.x, a.x, b.x, p3.x), z: cr(p0.z, a.z, b.z, p3.z), yaw: a.yaw + dy * u };
  }

  // the moment shown: a little over one update behind the newest, on the server's clock
  shownTime() { return Date.now() - (this.offset || 0) - this.gap * 1.25; }

  // every frame while anyone's on: each marker to where it is now, a followed one under the centre
  animate() {
    if (this.raf) return;
    const step = () => {
      this.raf = 0;
      if (!this.tracks.size) { this.endCentring(); return; }
      const t = this.shownTime();
      for (const [id, tr] of this.tracks) {
        const mk = this.markers.get(id), a = this.at(tr, t);
        if (!mk || !a) continue;
        mk.setLatLng(toLatLng(a.x, a.z));
        const arrow = mk.getElement()?.querySelector('.arrow');
        if (arrow) arrow.style.transform = `rotate(${a.yaw.toFixed(1)}deg)`;
        if (this.following === id) this.keepCentred(a.x, a.z);
      }
      this.raf = requestAnimationFrame(step);
    };
    this.raf = requestAnimationFrame(step);
  }

  // where a player is shown right now (follow starts there, not where the server last said)
  shownAt(id) { const tr = this.tracks.get(id); return tr ? this.at(tr, this.shownTime()) : null; }

  // follow: the map slid along with the player each frame, the way a drag moves it (the map pane
  // shifted and "move" fired), unless someone is dragging or zooming it; "moveend" once it settles
  keepCentred(x, z) {
    const map = this.map;
    if (map._animatingZoom || (map.dragging && map.dragging._draggable && map.dragging._draggable._moving) || (map._smooth && map._smooth.running)) return;
    const off = map.latLngToContainerPoint(toLatLng(x, z)).subtract(map.getSize().divideBy(2));
    if (Math.abs(off.x) < 0.05 && Math.abs(off.y) < 0.05) return;
    if (!this.centring) { this.centring = true; map.fire('movestart'); }
    map._rawPanBy(off);
    map.fire('move');
    clearTimeout(this.centreEnd);
    this.centreEnd = setTimeout(() => this.endCentring(), 400);
  }
  endCentring() {
    if (!this.centring) return;
    this.centring = false;
    clearTimeout(this.centreEnd);
    this.map.fire('moveend');
  }

  // a marker already on the map: turn its arrow and move its health bar in place (a new icon is a
  // new element, remade for each player every second); only a change of look remakes it
  refresh(mk, p) {
    const look = `${p.name}|${!!p.dead}|${this.following === p.id}`, el = mk.getElement();
    if (mk._look !== look || !el) { mk._look = look; mk.setIcon(this.icon(p)); return; }
    const hp = p.maxHealth > 0 ? Math.max(0, Math.min(1, p.health / p.maxHealth)) : 1;
    const arrow = el.querySelector('.arrow'), bar = el.querySelector('.hp i');
    if (arrow && !this.tracks.has(p.id)) arrow.style.transform = `rotate(${Math.round(p.yaw || 0)}deg)`;   // (a tracked one turns smoothly in animate)
    if (bar) { bar.style.width = `${Math.round(hp * 100)}%`; bar.classList.toggle('low', hp < 0.3); }
  }

  icon(p) {
    const col = this.color(p.name);
    const hp = p.maxHealth > 0 ? Math.max(0, Math.min(1, p.health / p.maxHealth)) : 1;
    return L.divIcon({
      className: '',
      html: `<div class="mk mk-player${p.dead ? ' dead' : ''}${this.following === p.id ? ' following' : ''}">
        <div class="arrow" style="transform:rotate(${Math.round(p.yaw || 0)}deg)">${iconSvg('player', col)}</div>
        <div class="lbl">${escape(p.name)}</div>
        <div class="hp"><i class="${hp < 0.3 ? 'low' : ''}" style="width:${Math.round(hp * 100)}%"></i></div></div>`,
      iconSize: [30, 30], iconAnchor: [15, 15],
    });
  }

  follow(id) {
    this.following = id;
    if (this.onFollow) this.onFollow(id);
    for (const p of this.players) { const mk = this.markers.get(p.id); if (mk) this.refresh(mk, p); }
    const p = this.players.find((q) => q.id === id);
    if (p && p.x !== undefined) {
      // straight onto them where they're shown, no fly: a glide landed where they had been and
      // then had to catch up
      const a = this.shownAt(id) || p, z = Math.max(this.map.getZoom(), 6);
      this.map.setView(toLatLng(a.x, a.z), z, { animate: false });
    }
    for (const fn of this.listeners) fn(this.players);
  }

  ping(f) {
    const ll = toLatLng(f.x, f.z);
    const mk = L.marker(ll, { icon: L.divIcon({ className: '', html: '<div class="ping"></div>', iconSize: [40, 40], iconAnchor: [20, 20] }), interactive: false, zIndexOffset: 2000 });
    mk.addTo(this.map);
    const lbl = L.tooltip({ permanent: true, direction: 'top', offset: [0, -20], className: 'ping-label' }).setContent(`${escape(f.name)} pinged`).setLatLng(ll).addTo(this.map);
    setTimeout(() => { mk.remove(); lbl.remove(); }, 8000);
  }

  // a shout, where it was shouted, for a few seconds: the game's shout pin with "name: text" under it,
  // as its map shows one
  shout(f) {
    const icons = window.app && window.app.layers.markers && window.app.layers.markers.gameIcons;
    const s = icons && icons['pin:Shout'];
    const html = `<div class="shout-pin">${s ? `<img src="icons/game/${s}.png" alt="">` : iconSvg('chat', '#fff')}</div><div class="shout-text">${escape(f.name)}: ${escape(f.text)}</div>`;
    const mk = L.marker(toLatLng(f.x, f.z), {
      icon: L.divIcon({ className: 'shout-icon', html, iconSize: [28, 28], iconAnchor: [14, 14] }),
      interactive: false, keyboard: false, zIndexOffset: 1800,
    }).addTo(this.map);
    setTimeout(() => mk.remove(), 10000);
  }

  setVisible(v) { if (v) this.group.addTo(this.map); else this.group.remove(); }
}
