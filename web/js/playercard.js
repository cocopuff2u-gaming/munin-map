// A card that pops up when a player is clicked, on the 2D map, in the 3D view or in the
// sidebar: health, stamina and eitr bars, state, biome and position, what they carry,
// and their lifetime stats. Stays open and keeps updating until dismissed.

import { escape } from './layers/markers.js';
import { iconSvg } from './icons.js';
import { stats as statsStore } from './data.js';
import { fmtDuration, fmtDist, fmtAgo } from './ui.js';

const SLOTS = [['right', 'Right hand'], ['left', 'Left hand'], ['helmet', 'Head'], ['chest', 'Chest'], ['legs', 'Legs'], ['shoulder', 'Cape'], ['utility', 'Belt']];

// "ArmorBronzeChest" -> "Armor Bronze Chest"; drop the variant suffixes the game adds
export function itemName(prefab) {
  if (!prefab) return '';
  return prefab.replace(/_.*$/, '').replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2');
}

export class PlayerCard {
  constructor(app) {
    this.app = app;
    this.playerId = null;
    this.el = document.createElement('div');
    this.el.className = 'player-card';
    this.el.hidden = true;
    document.getElementById('app').appendChild(this.el);
    this.el.addEventListener('click', (e) => e.stopPropagation());
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') this.hide(); });
    document.addEventListener('pointerdown', (e) => { if (!this.el.hidden && !this.el.contains(e.target) && !e.target.closest('.mk-player') && !e.target.closest('#panel-players')) this.hide(); });
  }

  // show for a player at a screen point (client coordinates); the card sits beside it
  show(player, x, y) {
    this.playerId = player.id;
    this.render(player);
    this.el.hidden = false;
    const w = this.el.offsetWidth, h = this.el.offsetHeight;
    let left = x + 16, top = y - h / 2;
    if (left + w > innerWidth - 8) left = x - w - 16;
    if (left < 8) left = 8;
    top = Math.max(8, Math.min(innerHeight - h - 8, top));
    this.el.style.left = left + 'px'; this.el.style.top = top + 'px';
  }

  hide() { this.el.hidden = true; this.playerId = null; }

  // live update from the players feed
  update(players) {
    if (this.playerId === null) return;
    const p = (players || []).find((q) => q.id === this.playerId);
    if (!p) { this.hide(); return; }
    this.render(p);
  }

  render(p) {
    const PL = this.app.layers.players;
    const col = PL.color(p.name);
    const bar = (label, v, max, cls) => max > 0 ? `<div class="pc-bar"><span>${label}</span><div class="hp ${cls}"><i class="${v / max < 0.3 ? 'low' : ''}" style="width:${Math.round(100 * Math.max(0, Math.min(1, v / max)))}%"></i></div><b>${Math.round(v)}${max ? ` / ${Math.round(max)}` : ''}</b></div>` : '';
    const state = [p.dead ? 'dead' : '', p.inBed ? 'sleeping' : '', p.pvp ? 'PvP' : '', p.hidden ? 'position hidden' : ''].filter(Boolean);
    const gear = SLOTS.filter(([k]) => p.gear && p.gear[k]).map(([k, label]) => `<div class="pc-gear"><span>${label}</span><b>${escape(itemName(p.gear[k]))}</b></div>`).join('');
    const st = (statsStore.data?.players || []).find((s) => s.name === p.name);
    const cell = (v, label, title = '') => `<div${title ? ` title="${escape(title)}"` : ''}><b>${v}</b><span>${label}</span></div>`;
    const km2 = (cells) => { const a = (cells || 0) * 144 / 1e6; return a >= 10 ? a.toFixed(0) + ' km²' : a.toFixed(1) + ' km²'; };   // explored cells are 12 m
    const life = st ? `<div class="pc-stats">
        ${cell(fmtDuration(st.playtime), 'played')}
        ${cell(st.sessions, 'visits')}
        ${cell(st.deaths, 'deaths')}
        ${cell(fmtDist(st.distance), 'walked')}
        ${cell(st.portalTrips ?? 0, 'portal trips')}
        ${cell(km2(st.revealed), 'map revealed', 'New ground this player uncovered on the map')}
        ${cell((st.built || 0).toLocaleString(), 'pieces built', 'Building pieces of theirs standing in the world')}
        ${cell(st.bossKills || 0, 'boss kills')}
        ${cell(st.raids || 0, 'raids weathered')}
        ${cell(st.finds || 0, 'finds', 'Boss altars, traders and mini-boss lairs they were first to find')}
      </div>
      ${(st.biomes || []).length ? `<div class="pc-biomes">${st.biomes.filter((b) => b !== 'Ocean' && b !== 'Unknown').map((b) => `<span class="chip">${escape(b)}</span>`).join('')}</div>` : ''}
      ${st.firstSeen ? `<div class="pc-first">First seen ${escape(new Date(st.firstSeen).toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' }))}</div>` : ''}` : '';
    const can3d = this.app.config?.enable_3d !== false;   // the server can switch 3D off: no way in from here either
    const following = PL.following === p.id;
    this.el.innerHTML = `
      <div class="pc-head"><span class="ico" style="color:${col}">${iconSvg('player', col)}</span><div class="grow"><div class="name">${escape(p.name)}</div>
        <div class="meta">${p.x !== undefined ? `${escape(p.biome || '')}${p.weather ? ` · ${escape(p.weather)}${p.wind !== undefined ? `, wind ${Math.round(p.wind * 100)}%` : ''}` : ''} · ${p.x}, ${p.z}` : 'position hidden'}${state.length ? ' · ' + state.join(' · ') : ''}</div></div>
        <button class="icon-btn small" data-act="close" title="Close"><svg><use href="#i-close"/></svg></button></div>
      ${bar('Health', p.health, p.maxHealth, 'hp-health')}
      ${p.stamina !== undefined ? bar('Stamina', p.stamina, Math.max(p.stamina, 100), 'hp-stamina') : ''}
      ${p.eitr !== undefined && p.eitr > 0 ? bar('Eitr', p.eitr, Math.max(p.eitr, 100), 'hp-eitr') : ''}
      ${gear ? `<div class="pc-section">Equipped</div>${gear}` : ''}
      ${life ? `<div class="pc-section">All time</div>${life}` : ''}
      <div class="pc-actions">
        <button class="btn small ${following ? 'on' : ''}" data-act="follow" ${p.x === undefined ? 'disabled' : ''}>${following ? 'Unfollow' : 'Follow'}</button>
        <button class="btn small" data-act="goto" ${p.x === undefined ? 'disabled' : ''}>Go to</button>
        ${can3d || this.app.mode === '3d' ? `<button class="btn small" data-act="mode">${this.app.mode === '3d' ? 'View in 2D' : 'View in 3D'}</button>` : ''}
      </div>`;
    this.el.querySelector('[data-act=close]').addEventListener('click', () => this.hide());
    this.el.querySelector('[data-act=follow]').addEventListener('click', () => { PL.follow(following ? null : p.id); this.render(p); });
    this.el.querySelector('[data-act=goto]').addEventListener('click', () => this.app.goTo(p.x, p.z, Math.max(this.app.mode === '2d' ? this.app.map.getZoom() : 7, 7)));
    this.el.querySelector('[data-act=mode]')?.addEventListener('click', async () => { await this.app.setMode(this.app.mode === '3d' ? '2d' : '3d'); if (p.x !== undefined) this.app.goTo(p.x, p.z, 7); this.render(p); });
  }
}
