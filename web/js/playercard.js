// A card that pops up when a player is clicked, on the 2D map, in the 3D view or in the sidebar
// (online or not): who they are and whether they're on, health, stamina and eitr, what they wear
// (the game's own item icons and names), when they play (the last two weeks and recent sessions),
// their lifetime stats and their places (beds, tombstones, wards) with a way to each. Stays open
// and keeps updating until dismissed. ?player=<name> opens one from a link.

import { escape } from './layers/markers.js';
import { iconSvg } from './icons.js';
import { stats as statsStore } from './data.js';
import { getJSON } from './net.js';
import { fmtDuration, fmtDist, fmtAgo } from './ui.js';

const SLOTS = [['helmet', 'Head'], ['chest', 'Chest'], ['legs', 'Legs'], ['shoulder', 'Cape'], ['utility', 'Belt'], ['right', 'Right hand'], ['left', 'Left hand']];

// "ArmorBronzeChest" -> "Armor Bronze Chest"; drop the variant suffixes the game adds
export function itemName(prefab) {
  if (!prefab) return '';
  return prefab.replace(/_.*$/, '').replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2');
}

// the game's names for items (server's World/ItemIcons), fetched once
let itemNames = null, itemNamesAsked = false, onNames = null;
function gameName(prefab) {
  if (!itemNamesAsked) { itemNamesAsked = true; getJSON('data/items.json').then((m) => { itemNames = m; onNames?.(); }).catch(() => {}); }
  return (itemNames && itemNames[prefab]) || itemName(prefab);
}

const day = (d) => d.toISOString().slice(0, 10);
const clock = (t) => new Date(t * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
function dayLabel(t) {
  const d = new Date(t * 1000), today = new Date(), y = new Date(Date.now() - 864e5);
  if (d.toDateString() === today.toDateString()) return 'Today';
  if (d.toDateString() === y.toDateString()) return 'Yesterday';
  return d.toLocaleDateString([], { weekday: 'short', month: 'short', day: 'numeric' });
}

export class PlayerCard {
  constructor(app) {
    this.app = app;
    this.name = null;          // whose card is open
    this.el = document.createElement('div');
    this.el.className = 'player-card';
    this.el.hidden = true;
    document.getElementById('app').appendChild(this.el);
    this.el.addEventListener('click', (e) => e.stopPropagation());
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') this.hide(); });
    document.addEventListener('pointerdown', (e) => { if (!this.el.hidden && !this.el.contains(e.target) && !e.target.closest('.mk-player') && !e.target.closest('#panel-players') && !e.target.closest('.pl-table')) this.hide(); });
    statsStore.onChange(() => { if (this.name && !this.el.hidden) this.refresh(); });
    onNames = () => { if (this.name && !this.el.hidden) this.refresh(); };   // the game's item names came in
  }

  live(name) { return (this.app.layers.players.players || []).find((q) => q.name === name); }

  // show for a player (online, from the players feed, or just a name) beside a screen point
  show(player, x, y) {
    this.name = player.name;
    this.refresh();
    this.el.hidden = false;
    const w = this.el.offsetWidth, h = this.el.offsetHeight;
    let left = x + 16, top = y - h / 2;
    if (left + w > innerWidth - 8) left = x - w - 16;
    if (left < 8) left = 8;
    top = Math.max(8, Math.min(innerHeight - h - 8, top));
    this.el.style.left = left + 'px'; this.el.style.top = top + 'px';
  }
  showByName(name) { this.show({ name }, innerWidth / 2 - 160, innerHeight / 2); }

  hide() { this.el.hidden = true; this.name = null; }

  // live update from the players feed
  update() { if (this.name !== null) this.refresh(); }

  refresh() {
    const p = this.live(this.name);
    const st = (statsStore.data?.players || []).find((s) => s.name === this.name);
    const owns = (this.app.layers.markers?.sets || []).some((set) => (set.markers || []).some((m) => m.owner === this.name));
    if (!p && !st && !owns) { this.hide(); return; }   // (someone with beds or wards but no stats yet still gets a card)
    this.render(p, st);
  }

  render(p, st) {
    const PL = this.app.layers.players, name = this.name, col = PL.color(name);
    const bar = (label, v, max, cls) => max > 0 ? `<div class="pc-bar"><span>${label}</span><div class="hp ${cls}"><i class="${v / max < 0.3 ? 'low' : ''}" style="width:${Math.round(100 * Math.max(0, Math.min(1, v / max)))}%"></i></div><b>${Math.round(v)}${max ? ` / ${Math.round(max)}` : ''}</b></div>` : '';
    // who, and whether they're on
    let status;
    if (p) {
      const since = st?.sessionStart ? ` · ${fmtDuration(Date.now() / 1000 - st.sessionStart)} this session` : '';
      const state = [p.dead ? 'dead' : '', p.inBed ? 'sleeping' : '', p.pvp ? 'PvP' : ''].filter(Boolean);
      status = `<span class="pc-on">Online</span>${since}${state.length ? ' · ' + state.join(' · ') : ''}`;
    } else status = st?.lastSeen ? `Last seen ${fmtAgo(st.lastSeen)}${st.lastBiome ? ' · ' + escape(st.lastBiome) : ''}` : 'Offline';
    const where = p ? (p.x !== undefined ? `${escape(p.biome || '')}${p.weather ? ` · ${escape(p.weather)}` : ''} · ${escape(this.app.sidebar?.whereText?.(p.x, p.z) || `${p.x}, ${p.z}`)}` : 'Position hidden') : '';
    // what they wear: the game's icon and name, the slot under it
    // (offline: what they wore when last seen, kept by the server)
    const worn = p ? p.gear : st && st.gear && Object.keys(st.gear).length ? st.gear : null;
    const gear = worn ? SLOTS.filter(([k]) => worn[k]).map(([k, label]) => {
      const pf = worn[k], nm = gameName(pf);
      return `<div class="pc-item" title="${escape(nm)}"><img src="icons/item/${encodeURIComponent(pf.replace(/[^A-Za-z0-9_-]/g, ''))}.png" alt="" loading="lazy" onerror="this.style.visibility='hidden'"><div><b>${escape(nm)}</b><span>${label}</span></div></div>`;
    }).join('') : '';
    // when they play: the last 14 days (minutes a day) and the last sessions
    let activity = '';
    if (st) {
      const days = st.days || {}, bars = [];
      let most = 1;
      for (let i = 13; i >= 0; i--) { const d = day(new Date(Date.now() - i * 864e5)), s = days[d] || 0; bars.push([d, s]); most = Math.max(most, s); }
      const total = bars.reduce((a, [, s]) => a + s, 0);
      const chart = `<div class="pc-days" title="Time played each day, the last two weeks">${bars.map(([d, s]) => `<i style="height:${s ? Math.max(8, Math.round(100 * s / most)) : 0}%" title="${d}: ${s ? fmtDuration(s) : 'not on'}"></i>`).join('')}</div>`;
      const sessions = (st.recent || []).slice(-4).reverse();
      const list = sessions.map(([a, b]) => `<div class="pc-sess"><span>${dayLabel(a)}</span><span>${clock(a)}–${clock(b)}</span><b>${fmtDuration(b - a)}</b></div>`).join('');
      if (total || sessions.length) activity = `<div class="pc-section">Last two weeks <small>${total ? fmtDuration(total) + ' played' : ''}</small></div>${chart}${list}`;
    }
    // lifetime
    const cell = (v, label, title = '') => `<div${title ? ` title="${escape(title)}"` : ''}><b>${v}</b><span>${label}</span></div>`;
    const km2 = (cells) => { const a = (cells || 0) * 144 / 1e6; return a >= 10 ? a.toFixed(0) + ' km²' : a.toFixed(1) + ' km²'; };   // explored cells are 12 m
    const life = st ? `<div class="pc-stats">
        ${cell(fmtDuration(st.playtime), 'played', 'Since the map first saw them')}
        ${cell(st.sessions, 'visits')}
        ${cell(st.deaths, 'deaths')}
        ${cell(fmtDist(st.distance), 'walked')}
        ${cell(st.portalTrips ?? 0, 'portal trips')}
        ${cell(km2(st.revealed), 'map revealed', 'New ground this player uncovered on the map')}
        ${cell((st.built || 0).toLocaleString(), 'pieces standing', 'Everything they ever built that still stands in this world, from before the map was installed too')}
        ${cell(st.bossKills || 0, 'boss kills')}
        ${cell(st.raids || 0, 'raids weathered')}
      </div>
      ${(st.biomes || []).length ? `<div class="pc-biomes">${st.biomes.filter((b) => b !== 'Ocean' && b !== 'Unknown').map((b) => `<span class="chip">${escape(b)}</span>`).join('')}</div>` : ''}
      ${st.firstSeen ? `<div class="pc-first">First seen ${escape(new Date(st.firstSeen).toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' }))}${st.finds ? ` · first to find ${st.finds} place${st.finds > 1 ? 's' : ''}` : ''}</div>` : ''}` : '';
    // their places: claimed beds, tombstones, wards (from the markers)
    const places = [];
    for (const set of this.app.layers.markers?.sets || []) {
      if (set.id !== 'beds' && set.id !== 'tombstones' && set.id !== 'wards') continue;
      for (const m of set.markers || []) if (m.owner === name) places.push({ m, kind: set.id === 'beds' ? 'bed' : set.id === 'wards' ? 'ward' : 'tombstone' });
    }
    // the same kind close together (a base's wards) is one row; beds first, then tombstones, wards
    const label = { bed: 'Bed', tombstone: 'Tombstone', ward: 'Ward' }, order = { bed: 0, tombstone: 1, ward: 2 }, groups = [];
    for (const pl of places) {
      const g = groups.find((q) => q.kind === pl.kind && Math.hypot(q.m.x - pl.m.x, q.m.z - pl.m.z) < 150);
      if (g) g.n++; else groups.push({ ...pl, n: 1 });
    }
    groups.sort((a, b) => order[a.kind] - order[b.kind]);
    places.length = 0; places.push(...groups);
    const placeRows = groups.slice(0, 8).map(({ m, kind, n }, i) => `<div class="pc-place" data-i="${i}">${iconSvg(kind)}<span>${label[kind]}${n > 1 ? ` ×${n}` : ''}${kind === 'ward' && n === 1 && m.on === false ? ' (off)' : ''}</span><small>${escape(this.app.sidebar?.whereText?.(m.x, m.z) || `${Math.round(m.x)}, ${Math.round(m.z)}`)}</small></div>`).join('');
    const can3d = this.app.config?.enable_3d !== false;
    const following = p && PL.following === p.id;
    const there = p && p.x !== undefined ? [p.x, p.z] : st && st.lastX !== undefined ? [st.lastX, st.lastZ] : null;
    this.el.innerHTML = `
      <div class="pc-head"><span class="avatar" style="background:${col}">${escape((name || '?').slice(0, 1).toUpperCase())}</span><div class="grow"><div class="name">${escape(name)}</div>
        <div class="meta">${status}</div>${where ? `<div class="meta">${where}</div>` : ''}</div>
        <button class="icon-btn small" data-act="close" title="Close"><svg><use href="#i-close"/></svg></button></div>
      ${p ? bar('Health', p.health, p.maxHealth, 'hp-health') : ''}
      ${p && p.stamina !== undefined ? bar('Stamina', p.stamina, Math.max(p.stamina, 100), 'hp-stamina') : ''}
      ${p && p.eitr !== undefined && p.eitr > 0 ? bar('Eitr', p.eitr, Math.max(p.eitr, 100), 'hp-eitr') : ''}
      ${gear ? `<div class="pc-section">${p ? 'Wearing' : 'Last seen wearing'}</div><div class="pc-items${p ? '' : ' was'}">${gear}</div>` : ''}
      ${activity}
      ${life ? `<div class="pc-section">All time</div>${life}` : ''}
      ${placeRows ? `<div class="pc-section">Their places</div>${placeRows}` : ''}
      <div class="pc-actions">
        ${p ? `<button class="btn small ${following ? 'on' : ''}" data-act="follow" ${p.x === undefined ? 'disabled' : ''}>${following ? 'Unfollow' : 'Follow'}</button>` : ''}
        <button class="btn small" data-act="goto" ${there ? '' : 'disabled'} title="${p ? '' : 'Where they were last seen'}">${p ? 'Go to' : 'Last seen'}</button>
        ${can3d || this.app.mode === '3d' ? `<button class="btn small" data-act="mode">${this.app.mode === '3d' ? '2D' : '3D'}</button>` : ''}
        <button class="btn small" data-act="link" title="Copy a link that opens this card">Link</button>
      </div>`;
    this.el.querySelector('[data-act=close]').addEventListener('click', () => this.hide());
    this.el.querySelector('[data-act=follow]')?.addEventListener('click', () => { PL.follow(following ? null : p.id); this.refresh(); });
    this.el.querySelector('[data-act=goto]').addEventListener('click', () => there && this.app.goTo(there[0], there[1], Math.max(this.app.mode === '2d' ? this.app.map.getZoom() : 7, 7)));
    this.el.querySelector('[data-act=mode]')?.addEventListener('click', async () => { await this.app.setMode(this.app.mode === '3d' ? '2d' : '3d'); if (there) this.app.goTo(there[0], there[1], 7); this.refresh(); });
    this.el.querySelector('[data-act=link]').addEventListener('click', async () => {
      const url = `${location.origin}${location.pathname}?player=${encodeURIComponent(name)}`;
      try { await navigator.clipboard.writeText(url); this.app.toast('Link copied'); } catch { this.app.toast(url); }
    });
    this.el.querySelectorAll('.pc-place').forEach((r) => r.addEventListener('click', () => { const { m } = places[+r.dataset.i]; this.app.goTo(m.x, m.z, Math.max(this.app.map.getZoom(), 7)); }));
  }
}
