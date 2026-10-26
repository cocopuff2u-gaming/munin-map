// Live players: an arrow that points where they are facing, name, health
// bar, follow mode, and map pings. Positions come about once a second; in between each player
// glides from where it was shown to the new spot over the time the updates take to come (one
// update behind, so never ahead of the truth), turning smoothly, and a followed player keeps the
// map centred on them every frame instead of a pan each second. A jump (a portal) is not glided.

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
    this.motion = new Map();      // id -> { fx, fz, fyaw, tx, tz, tyaw, t0, dur }
    this.gap = 1000;              // ms between updates, as they come (smoothed)
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
    const now = performance.now();
    if (this.lastUpdate) this.gap = Math.max(300, Math.min(2500, this.gap * 0.7 + (now - this.lastUpdate) * 0.3));
    this.lastUpdate = now;
    const seen = new Set();
    for (const p of this.players) {
      seen.add(p.id);
      if (p.x === undefined) { this.remove(p.id); continue; }
      const ll = toLatLng(p.x, p.z);
      let mk = this.markers.get(p.id);
      if (!mk) {
        mk = L.marker(ll, { icon: this.icon(p), zIndexOffset: 1000, keyboard: false });
        mk._look = `${p.name}|${!!p.dead}|${this.following === p.id}`;
        mk.bindTooltip('', { direction: 'top', offset: [0, -14] });
        mk.on('click', (e) => { const q = this.players.find((r) => r.id === p.id) || p; const oe = e.originalEvent; if (this.onClick) this.onClick(q, oe ? oe.clientX : 0, oe ? oe.clientY : 0); });
        this.markers.set(p.id, mk);
        this.group.addLayer(mk);
      } else {
        this.refresh(mk, p);
      }
      // glide from where it is shown now to the new spot (a jump of over 100 m: straight there)
      const m = this.motion.get(p.id), at = m ? this.at(m, now) : null, yaw = p.yaw || 0;
      if (!at || Math.hypot(p.x - at.x, p.z - at.z) > 100) this.motion.set(p.id, { fx: p.x, fz: p.z, fyaw: yaw, tx: p.x, tz: p.z, tyaw: yaw, t0: now, dur: 1 });
      else this.motion.set(p.id, { fx: at.x, fz: at.z, fyaw: at.yaw, tx: p.x, tz: p.z, tyaw: yaw, t0: now, dur: this.gap });
      if (!m) mk.setLatLng(ll);
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
    this.motion.delete(id);
  }

  // where a glide is at a moment (yaw the short way round)
  at(m, now) {
    const t = Math.min(1, (now - m.t0) / m.dur), e = t;   // (linear: steady walking looks steady)
    let dy = ((m.tyaw - m.fyaw + 540) % 360) - 180;
    return { x: m.fx + (m.tx - m.fx) * e, z: m.fz + (m.tz - m.fz) * e, yaw: m.fyaw + dy * e, done: t >= 1 };
  }

  // a frame of the glides: each marker to its spot now, the followed one under the map's centre
  animate() {
    if (this.raf) return;
    const step = () => {
      this.raf = 0;
      const now = performance.now();
      let moving = false;
      for (const [id, m] of this.motion) {
        const mk = this.markers.get(id);
        if (!mk) continue;
        const a = this.at(m, now);
        if (!a.done) moving = true;
        mk.setLatLng(toLatLng(a.x, a.z));
        const arrow = mk.getElement()?.querySelector('.arrow');
        if (arrow) arrow.style.transform = `rotate(${Math.round(a.yaw)}deg)`;
        if (this.following === id) this.keepCentred(a.x, a.z);
      }
      if (moving) this.raf = requestAnimationFrame(step);
      else this.endCentring();
    };
    this.raf = requestAnimationFrame(step);
  }

  // follow: the map moved with the player each frame (as the smooth zoom moves it, smoothzoom.js),
  // unless someone is dragging it or zooming; Leaflet's "moveend" once it settles
  keepCentred(x, z) {
    const map = this.map;
    if (map._animatingZoom || (map.dragging && map.dragging._draggable && map.dragging._draggable._moving) || (map._smooth && map._smooth.running)) return;
    const ll = toLatLng(x, z);
    if (!map._smooth || !map._smooth.exact) { if (!this.panTimer) this.panTimer = setTimeout(() => { this.panTimer = null; map.panTo(ll, { animate: true, duration: 0.5, noMoveStart: true }); }, 500); return; }
    if (!this.centring) { this.centring = true; map._moveStart(false, false); }
    map._move(ll, map.getZoom());
    clearTimeout(this.centreEnd);
    this.centreEnd = setTimeout(() => this.endCentring(), 400);
  }
  endCentring() {
    if (!this.centring) return;
    this.centring = false;
    clearTimeout(this.centreEnd);
    this.map._moveEnd(false);
  }

  // a marker already on the map: turn its arrow and move its health bar in place (a new icon is a
  // new element, remade for each player every second); only a change of look remakes it
  refresh(mk, p) {
    const look = `${p.name}|${!!p.dead}|${this.following === p.id}`, el = mk.getElement();
    if (mk._look !== look || !el) { mk._look = look; mk.setIcon(this.icon(p)); return; }
    const hp = p.maxHealth > 0 ? Math.max(0, Math.min(1, p.health / p.maxHealth)) : 1;
    const arrow = el.querySelector('.arrow'), bar = el.querySelector('.hp i');
    if (arrow) arrow.style.transform = `rotate(${Math.round(p.yaw || 0)}deg)`;
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
      const ll = toLatLng(p.x, p.z), z = Math.max(this.map.getZoom(), 6);
      if (this.map._smooth) this.map._smooth.glideTo(ll, z); else this.map.setView(ll, z);
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
