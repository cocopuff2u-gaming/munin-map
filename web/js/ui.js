// The sidebar: layers, players, markers, stats, events.

import { escape, byName } from './layers/markers.js';
import { iconSvg, colors, materialColors, materialNames } from './icons.js';
import { stats as statsStore, prefabs, objectFilter, OBJECT_CATS, markers as markerStore } from './data.js';
import { layerState } from './layerstate.js';
import { VEG, VEG_GROUPS } from './vegpack.js';
import { RUIN_SITES } from './layers/shapes.js';
import { on } from './net.js';
import { RaidLayer } from './layers/raid.js';

const $ = (s, r = document) => r.querySelector(s);
const el = (html) => { const t = document.createElement('template'); t.innerHTML = html.trim(); return t.content.firstElementChild; };

export function fmtDuration(sec) {
  sec = Math.round(sec || 0);
  const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60);
  if (h >= 48) return `${Math.floor(h / 24)}d ${h % 24}h`;
  if (h > 0) return `${h}h ${m}m`;
  return `${m}m`;
}
export function fmtDist(m) { return m >= 10000 ? `${Math.round(m / 1000)} km` : m >= 1000 ? `${(m / 1000).toFixed(1)} km` : `${Math.round(m)} m`; }
export function fmtAgo(iso) {
  if (!iso) return '';
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return `${Math.floor(s / 86400)} d ago`;
}
function fmtTime(iso) { const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); }
// an event's time: just the time today, the date above it on earlier days; "~" when it was
// worked out from the server logs afterwards (to within a few minutes)
function eventTime(e) {
  const d = new Date(e.ts);
  if (isNaN(d)) return '';
  const t = (e.approx ? '~' : '') + fmtTime(e.ts);
  if (d.toDateString() === new Date().toDateString()) return t;
  return `${d.toLocaleDateString([], { month: 'short', day: 'numeric' })}<br>${t}`;
}

export class Sidebar {
  constructor(app) {
    this.app = app;
    this.root = $('#sidebar');
    this.tabs = this.root.querySelectorAll('.tabs button[data-tab]');
    for (const b of this.tabs) b.addEventListener('click', () => this.show(b.dataset.tab));
    $('#btn-close-sidebar').addEventListener('click', () => app.toggleSidebar(false));
    this.eventFilters = new Set(EVENT_KINDS.map((k) => k[0]));
    this.unread = 0;
    this.active = 'layers';
    this.buildLayers();
    this.buildEvents();
    on('events', (f) => this.addEvents(f.data, f.initial));
    statsStore.onChange((d) => { this.later('stats', () => this.renderStats(d)); if (this.lastPlayers) this.renderPlayers(this.lastPlayers); });   // players: the recently online list
    markerStore.onChange(() => { if (statsStore.data) this.later('stats', () => this.renderStats(statsStore.data)); });   // the totals count bases, portals and boats
  }

  // A panel's rebuild, done only while its tab is open and then in idle time: the markers list is
  // hundreds of rows, and remaking it after every world update (open or not) stalled the map for a
  // good part of a second when it landed mid-zoom. A closed tab is remade when it's opened.
  later(tab, fn) {
    if (!this.pending) this.pending = new Map();
    this.pending.set(tab, fn);
    if (this.active !== tab) return;
    const run = () => { const f = this.pending.get(tab); if (f && this.active === tab) { this.pending.delete(tab); f(); } };
    if (window.requestIdleCallback) requestIdleCallback(run, { timeout: 1000 }); else setTimeout(run, 50);
  }

  show(tab) {
    this.active = tab;
    for (const b of this.tabs) b.classList.toggle('active', b.dataset.tab === tab);
    for (const p of this.root.querySelectorAll('.panel')) p.classList.toggle('active', p.dataset.panel === tab);
    if (tab === 'events') { this.unread = 0; this.badge(); if (this.logOn) this.pollLog(true); }
    if (tab === 'stats') statsStore.refresh();
    const f = this.pending && this.pending.get(tab);
    if (f) { this.pending.delete(tab); f(); }
    this.app.toggleSidebar(true);
  }

  badge() {
    const b = $('#events-badge');
    b.hidden = this.unread === 0;
    b.textContent = this.unread > 99 ? '99+' : this.unread;
  }

  // ---------------------------------------------------------------- layers
  // Cards by what they are about: the world itself, the people and places on it, guides drawn
  // over it, and the 3D view. Each row: an icon, a name with a line on what it shows, a switch.
  buildLayers() {
    const p = $('#panel-layers');
    const L = this.app.layers, S = layerState;
    // each card folds away like the Map key; this browser remembers which are open
    let opened = {};
    try { opened = JSON.parse(localStorage.getItem('munin-layer-folds') || '{}') || {}; } catch (e) { /* none */ }
    const foldable = (c, title, open = true) => {
      c.open = opened[title] ?? open;
      c.addEventListener('toggle', () => {
        opened[title] = c.open;
        try { localStorage.setItem('munin-layer-folds', JSON.stringify(opened)); } catch (e) { /* this visit only */ }
      });
      return c;
    };
    const card = (title, sub) => {
      const c = foldable(el(`<details class="lcard"><summary><h4>${title}</h4>${sub ? `<span>${sub}</span>` : ''}<i class="chev"></i></summary></details>`), title);
      p.append(c);
      return c;
    };
    const row = (icon, label, desc, checked, onToggle) => {
      const r = el(`<label class="lrow"><span class="lico">${icon}</span><span class="ltext"><b>${label}</b>${desc ? `<small>${desc}</small>` : ''}</span><span class="switch"><input type="checkbox" ${checked ? 'checked' : ''}><i></i></span></label>`);
      r.querySelector('input').addEventListener('change', (e) => onToggle(e.target.checked));
      return r;
    };
    const ico = (name, color) => iconSvg(name, color || colors[name]);

    // ---- the world
    const world = card('The world');
    if (this.app.biomes) {
      // map style: the drawn land, or each biome in a solid colour with its name
      const style = el(`<div class="lrow"><span class="lico">${BIOME_SVG}</span><span class="ltext"><b>Map style</b><small>Terrain, or biomes in solid colours</small></span><select class="sel"><option value="terrain">Terrain</option><option value="biomes">Biomes</option></select></div>`);
      const keyEl = el('<div class="biome-key" hidden></div>');
      const sel = style.querySelector('select'); sel.value = S.mapStyle;
      const apply = () => { const on = sel.value === 'biomes'; this.app.biomes.setOn(on); keyEl.hidden = !on; S.set('mapStyle', sel.value); };
      this.app.biomes.onColours = (cols) => { keyEl.replaceChildren(...Object.entries(cols).map(([n, c]) => el(`<span><i style="background:${c}"></i>${escape(n)}</span>`))); };
      sel.addEventListener('change', apply);
      world.append(style, keyEl);
      if (S.mapStyle === 'biomes') apply();
    }
    if (this.app.altBiomes) {
      // the game's biome variants, striped in a colour each, with a key of the ones this world has
      const keyEl = el('<div class="biome-key" hidden></div>');
      const A = this.app.altBiomes;
      A.onKey = (k) => { keyEl.replaceChildren(...k.map(([n, c]) => el(`<span><i style="background:${c}"></i>${escape(n)}</span>`))); };
      world.append(row(BIOME_SVG, 'Alt biomes', 'Variants like Ruin Black Forest', S.altBiomes, (v) => { A.setOn(v); keyEl.hidden = !v; S.set('altBiomes', v); }), keyEl);
      if (S.altBiomes) { A.setOn(true); keyEl.hidden = false; }
    }
    if (this.app.mist) {
      world.append(row(MIST_SVG, 'Mistlands mist', 'Cleared by Wisplights and lanterns', S.mist, (v) => { this.app.mist.setOn(v); S.set('mist', v); }));
      this.app.mist.setOn(S.mist);
    }
    world.append(row(ico('house', '#c9a26b'), 'Buildings', 'Player builds, coloured by material', S.buildings, (v) => { if (v) L.structures.addTo(this.app.map); else L.structures.remove(); S.set('buildings', v); }));
    // a row with a switch, and under its arrow a checklist ([[id, name, swatch]]) of what can be hidden one by one
    const groupRow = (icon, label, desc, on, onToggle, kinds, hidden, key) => {
      const list = el(`<div class="vkinds${on ? '' : ' off'}" hidden></div>`);
      const r = row(icon, label, desc, on, (v) => { list.classList.toggle('off', !v); onToggle(v); });
      const more = el('<button class="lmore" type="button" title="Choose which" aria-expanded="false"><svg viewBox="0 0 24 24"><path d="M9 6l6 6-6 6"/></svg></button>');
      more.addEventListener('click', (e) => { e.preventDefault(); e.stopPropagation(); list.hidden = !list.hidden; more.setAttribute('aria-expanded', String(!list.hidden)); });
      r.append(more);
      for (const [k, name, swatch] of kinds) {
        const c = el(`<label class="vkind"><input type="checkbox" ${hidden.has(k) ? '' : 'checked'}>${swatch || ''}<span title="${name}">${name}</span></label>`);
        c.querySelector('input').addEventListener('change', (e) => { if (e.target.checked) hidden.delete(k); else hidden.add(k); S.set(key, hidden); });
        list.append(c);
      }
      return [r, list];
    };
    const ruinKinds = RUIN_SITES.map((n, i) => [i, n]).slice(1).concat([[0, 'Other']]);
    world.append(...groupRow(ico('ruin'), 'World structures', 'Ruins, villages, wrecks', S.ruins, (v) => { if (v) L.ruins.addTo(this.app.map); else L.ruins.remove(); S.set('ruins', v); }, ruinKinds, S.ruinHidden, 'ruinHidden'));
    if (this.app.gl) {
      // a switch per group (the WebGL map draws them from data; the plain map's tree tiles come
      // baked in one), and under the arrow the group's kinds to hide one by one
      const vegOn = () => { const any = VEG_GROUPS.some((g) => S[g.key] !== false); if (any !== S.veg) { if (any) L.veg.addTo(this.app.map); else L.veg.remove(); S.set('veg', any); } };
      const icons = { vegTrees: TREE_SVG, vegBushes: SHRUB_SVG, vegBerries: BUSH_SVG, vegRocks: ROCK_SVG, vegOre: ORE_SVG, vegMushrooms: MUSHROOM_SVG, vegPlants: PLANT_SVG };
      for (const g of VEG_GROUPS) {
        const kinds = g.kinds.map(([k, name]) => [k, name, `<i class="${VEG[k][2] ? 'rock' : 'round'}" style="background:${VEG[k][1]}"></i>`]);
        world.append(...groupRow(icons[g.key], g.label, g.desc, S[g.key] !== false, (v) => { S.set(g.key, v); vegOn(); }, kinds, S.vegHidden, 'vegHidden'));
      }
    } else {
      world.append(row(TREE_SVG, 'Trees & rocks', 'Every tree, bush and boulder', S.veg, (v) => { if (v) L.veg.addTo(this.app.map); else L.veg.remove(); S.set('veg', v); }));
    }

    // ---- people and places
    const people = card('People & places');
    people.append(row(ico('player', '#6fb7ff'), 'Players', 'Where everyone online is right now', S.players, (v) => { L.players.setVisible(v); S.set('players', v); }));
    people.append(row(ico('pin'), 'Pins', 'Pins placed on the map', S.pins, (v) => { L.markers.setVisible('pins', v); S.set('pins', v); }));
    people.append(row(LABEL_SVG, 'Names', 'Labels under the markers', S.labels, (v) => { document.body.classList.toggle('no-labels', !v); S.set('labels', v); }));
    people.append(row(ico('dot', '#9aa5b5'), 'Dots', 'Plain dots instead of icons', S.iconDots, (v) => S.set('iconDots', v)));
    const size = el(`<div class="lrow"><span class="lico">${ico('poi', '#9aa5b5')}</span><span class="ltext"><b>Marker size</b></span><input class="lrange" type="range" min="0.5" max="2" step="0.1" value="${S.iconScale}"></div>`);
    size.querySelector('input').addEventListener('input', (e) => S.set('iconScale', +e.target.value));
    people.append(size);
    this.markerSetRows = el('<div class="lsets"></div>');
    people.append(el('<div class="lsub">Markers</div>'), this.markerSetRows);

    // ---- guides drawn over the map
    const guides = card('Map guides');
    guides.append(row(GRID_SVG, 'Grid', '256 m zones with coordinates', S.grid, (v) => { this.app.setGrid(v); S.set('grid', v); }));
    guides.append(row(RINGS_SVG, 'Distance rings', 'Every 500 m out from the spawn', S.rings, (v) => { this.app.setRings(v); S.set('rings', v); }));

    // ---- the 3D view (left out when the server has enable_3d = false)
    if (this.app.config?.enable_3d !== false) {
      const d3 = card('3D view', 'used when you switch to 3D');
      d3.append(el('<div class="lsub">Show</div>'));
      const objs = el('<div class="filters lchips"></div>');
      for (const [cat, label] of OBJECT_CATS) {
        const on = objectFilter.shows(cat);
        const lab = el(`<label class="${on ? '' : 'off'}"><input type="checkbox" ${on ? 'checked' : ''}> ${label}</label>`);
        lab.querySelector('input').addEventListener('change', (e) => { lab.classList.toggle('off', !e.target.checked); objectFilter.set(cat, e.target.checked); });
        objs.append(lab);
      }
      d3.append(objs);
      const light = el(`<div class="lrow"><span class="lico">${SUN_SVG}</span><span class="ltext"><b>Time of day</b><small>The light in 3D</small></span><select class="sel">
        <option value="live">Live</option><option value="morning">Morning</option><option value="noon">Noon</option><option value="evening">Evening</option><option value="night">Night</option></select></div>`);
      const sel = light.querySelector('select'); sel.value = S.time3d;
      sel.addEventListener('change', () => S.set('time3d', sel.value));
      d3.append(light);
      d3.append(row(SHADOW_SVG, 'Shadows', 'Softer on slower devices when off', S.shadows, (v) => S.set('shadows', v)));
    }

    // ---- what the colours mean, folded away: buildings by material, trees and rocks by kind
    const legend = foldable(el('<details class="lcard llegend"><summary><h4>Map key</h4><i class="chev"></i></summary><div class="lsub">Buildings</div><div class="legend lb"></div><div class="lsub">Trees &amp; rocks</div><div class="legend lv"></div></details>'), 'Map key', false);
    materialNames.forEach((n, i) => legend.querySelector('.lb').append(el(`<span><i style="background:${materialColors[i]}"></i>${n}</span>`)));
    for (const [kind, name] of VEG_KEY) if (VEG[kind]) legend.querySelector('.lv').append(el(`<span><i class="${VEG[kind][2] ? 'rock' : 'round'}" style="background:${VEG[kind][1]}"></i>${name}</span>`));
    p.append(legend);

    const setIcon = { spawn: 'spawn', bosses: 'boss', minibosses: 'miniboss', dungeons: 'dungeon', landmarks: 'forge', camps: 'fulingcamp', signs: 'sign', traders: 'trader', portals: 'portal', tablepins: 'maptable', tombstones: 'tombstone', wards: 'ward', runestones: 'runestone', vegvisirs: 'vegvisir', beds: 'bed', bases: 'house', vehicles: 'boat', invasions: 'invasion', locations: 'poi' };
    L.markers.onChange((sets) => {
      this.markerSetRows.replaceChildren();
      for (const s of sets) {
        const n = (s.markers || []).length;
        const r = row(ico(setIcon[s.id] || 'poi'), `${escape(s.label)} <span class="lcount">${n}</span>`, '', L.markers.visible.get(s.id) !== false, (v) => { L.markers.setVisible(s.id, v); S.setSet(s.id, v); });
        r.classList.add('compact');
        this.markerSetRows.append(r);
      }
      this.later('markers', () => this.renderMarkers(sets));
    });
  }

  // ---------------------------------------------------------------- players
  // Who is on now (cards: where, health, follow), then who was on recently (from the stats).
  // the online pill now; the panel (cards remade from scratch) only while its tab is open
  renderPlayers(players) {
    this.lastPlayers = players;
    $('#online-pill').textContent = `${players.length} online`;
    $('#online-pill').classList.toggle('on', players.length > 0);
    this.later('players', () => this.playersPanel(this.lastPlayers));
  }

  playersPanel(players) {
    const p = $('#panel-players');
    const PL = this.app.layers.players;
    p.replaceChildren(el(`<h3>Online now <span class="count">${players.length}</span></h3>`));
    if (players.length === 0) p.append(el('<div class="empty-card">Nobody is online right now.</div>'));
    for (const pl of players) {
      const hp = pl.maxHealth ? Math.round(100 * pl.health / pl.maxHealth) : 100;
      const color = PL.color(pl.name), following = PL.following === pl.id;
      const tags = [pl.dead && '<span class="tag dead">Dead</span>', pl.inBed && '<span class="tag sleep">Sleeping</span>', pl.pvp && '<span class="tag pvp">PvP</span>'].filter(Boolean).join('');
      const where = pl.x !== undefined ? `${escape(pl.biome || '')}${pl.biome ? ' · ' : ''}${pl.weather ? escape(pl.weather) + ' · ' : ''}${this.whereText(pl.x, pl.z)}` : 'Position hidden';
      const r = el(`<div class="pcard${following ? ' on' : ''}">
        <div class="pc-top"><span class="avatar" style="background:${color}">${escape((pl.name || '?').slice(0, 1).toUpperCase())}</span>
          <div class="grow"><div class="pc-name">${escape(pl.name)}${tags}</div><div class="pc-where" title="${pl.x !== undefined ? `${pl.x}, ${pl.z}` : ''}">${where}</div></div>
          <button class="btn small${following ? ' on' : ''}" ${pl.x === undefined ? 'disabled' : ''}>${following ? 'Following' : 'Follow'}</button></div>
        <div class="pc-hp"><div class="hp"><i class="${hp < 30 ? 'low' : ''}" style="width:${hp}%"></i></div><span>${Math.round(pl.health ?? 0)} / ${Math.round(pl.maxHealth ?? 0)}</span></div></div>`);
      r.querySelector('button').addEventListener('click', (e) => { e.stopPropagation(); PL.follow(following ? null : pl.id); });
      r.addEventListener('click', () => { const rect = r.getBoundingClientRect(); this.app.playerCard.show(pl, rect.right, rect.top + rect.height / 2); });
      p.append(r);
    }
    // recently online, from the stats (last seen, and where when the server shares it)
    const online = new Set(players.map((x) => x.name));
    const recent = ((statsStore.data && statsStore.data.players) || []).filter((x) => !online.has(x.name) && x.lastSeen)
      .sort((x, y) => y.lastSeen.localeCompare(x.lastSeen)).slice(0, 12);
    if (recent.length) {
      p.append(el(`<h3>Recently online <span class="count">${recent.length}</span></h3>`));
      const list = el('<div class="mk-list"></div>');
      for (const x of recent) {
        const r = el(`<div class="mrow${x.lastX !== undefined ? ' clickable' : ''}"><span class="avatar small" style="background:${PL.color(x.name)}">${escape((x.name || '?').slice(0, 1).toUpperCase())}</span>
          <div class="grow"><div class="name">${escape(x.name)}</div><div class="meta">${x.lastBiome ? escape(x.lastBiome) + ' · ' : ''}${fmtDuration(x.playtime)} played</div></div><span class="mside" title="${escape(new Date(x.lastSeen).toLocaleString())}">${fmtAgo(x.lastSeen)}</span></div>`);
        if (x.lastX !== undefined) r.addEventListener('click', () => this.app.goTo(x.lastX, x.lastZ, Math.max(this.app.map.getZoom(), 6)));
        list.append(r);
      }
      p.append(list);
    }
  }

  // "at the spawn", "650 m NE of spawn", "2.1 km W of spawn"
  whereText(x, z) {
    const s = this.app.spawn || { x: 0, z: 0 }, dx = x - s.x, dz = z - s.z, d = Math.hypot(dx, dz);
    if (d < 60) return 'at the spawn';
    const dirs = ['E', 'NE', 'N', 'NW', 'W', 'SW', 'S', 'SE'];
    const dir = dirs[(Math.round(Math.atan2(dz, dx) / (Math.PI / 4)) + 8) % 8];
    return `${d < 1000 ? Math.round(d / 10) * 10 + ' m' : (d / 1000).toFixed(1) + ' km'} ${dir} of spawn`;
  }

  // ---------------------------------------------------------------- markers
  // A search box, then a card per set (folded when long); portals as linked pairs.
  renderMarkers(sets) {
    this.mkSets = sets;
    if (!this.mkOpen) this.mkOpen = new Map();
    const p = $('#panel-markers');
    if (!this.mkSearch) {
      this.mkSearch = el('<div class="mk-search"><input type="search" placeholder="Find a marker"></div>');
      this.mkSearch.querySelector('input').addEventListener('input', () => this.renderMarkers(this.mkSets));
      this.mkBody = el('<div></div>');
    }
    if (!p.contains(this.mkSearch)) p.replaceChildren(this.mkSearch, this.mkBody);
    const q = this.mkSearch.querySelector('input').value.trim().toLowerCase();
    const ICON = { spawn: 'spawn', bosses: 'boss', minibosses: 'miniboss', dungeons: 'dungeon', landmarks: 'forge', camps: 'fulingcamp', signs: 'sign', traders: 'trader', portals: 'portal', tablepins: 'maptable', tombstones: 'tombstone', wards: 'ward', runestones: 'runestone', vegvisirs: 'vegvisir', beds: 'bed', bases: 'house', vehicles: 'boat', invasions: 'invasion' };
    const go = (x, z) => this.app.goTo(x, z, Math.max(this.app.map.getZoom(), 6));
    this.mkBody.replaceChildren();
    let any = false;
    for (const s of sets) {
      let ms = (s.markers || []).slice().sort((a, b) => byName(a.label, b.label));
      if (q) ms = ms.filter((m) => (m.label || '').toLowerCase().includes(q) || (m.tag || '').toLowerCase().includes(q));
      if (q && !ms.length) continue;
      any = true;
      const iconName = ICON[s.id] || 'poi';
      const open = q ? true : (this.mkOpen.has(s.id) ? this.mkOpen.get(s.id) : (s.markers || []).length <= 8);
      const card = el(`<details class="mk-card"${open ? ' open' : ''}><summary><span class="ico">${iconSvg(iconName, colors[iconName])}</span><b>${escape(s.label)}</b><span class="lcount">${ms.length}</span><span class="chev"></span></summary><div class="mk-list"></div></details>`);
      card.addEventListener('toggle', () => { if (!q) this.mkOpen.set(s.id, card.open); });
      const list = card.querySelector('.mk-list');
      if (!ms.length) list.append(el('<div class="empty">Nothing found yet.</div>'));
      const row = (m, name, meta, side = '') => {
        const icon = m.icon || m.cat || iconName;
        const r = el(`<div class="mrow clickable" title="${m.x}, ${m.z}">${this.markerIcon(icon, colors[icon] || colors[iconName], m.cat === 'tablepin')}<div class="grow"><div class="name">${name}</div><div class="meta">${meta}</div></div>${side}</div>`);
        r.addEventListener('click', () => { go(m.x, m.z); if (m.invasion) this.app.layers.raid.openInvasion(m); });
        return r;
      };
      if (s.id === 'portals') {
        // one row per tag: its ends, how far apart, and a button for each end
        const byTag = new Map();
        for (const m of ms) { const k = m.tag || m.label || ''; if (!byTag.has(k)) byTag.set(k, []); byTag.get(k).push(m); }
        for (const [tag, ends] of byTag) {
          const far = ends.length === 2 ? Math.hypot(ends[0].x - ends[1].x, ends[0].z - ends[1].z) : 0;
          const meta = ends.length === 2 ? `linked · ${far < 1000 ? Math.round(far) + ' m' : (far / 1000).toFixed(1) + ' km'} apart` : ends.length === 1 ? 'no other end yet' : `${ends.length} ends: only two link`;
          const btns = `<span class="ends">${ends.map((e, i) => `<button class="end" title="${escape(this.whereText(e.x, e.z))}">${i + 1}</button>`).join('')}</span>`;
          const r = row(ends[0], escape(tag || '(untagged)'), meta, btns);
          r.querySelectorAll('.end').forEach((b, i) => b.addEventListener('click', (ev) => { ev.stopPropagation(); go(ends[i].x, ends[i].z); }));
          list.append(r);
        }
      } else {
        for (const m of ms.slice(0, 300)) {
          const where = this.whereText(m.x, m.z);
          let meta = where, side = '';
          if (s.id === 'tombstones' && m.when) side = `<span class="mside">${new Date(m.when / 10000 - 62135596800000).toLocaleDateString([], { month: 'short', day: 'numeric' })}</span>`;
          if (s.id === 'bases' && m.pieces) side = `<span class="mside">${m.pieces.toLocaleString()} pieces</span>`;
          if (s.id === 'spawn') meta = `${m.x}, ${m.z}`;
          if (m.invasion) { const left = RaidLayer.left(m.invasion); side = `<span class="mside">${left === null ? 'until cleared' : RaidLayer.duration(left) + ' left'}</span>`; }
          list.append(row(m, escape(m.label || (m.cat === 'tablepin' ? 'Map pin' : '')), meta, side));
        }
        if (ms.length > 300) list.append(el(`<div class="empty">...and ${ms.length - 300} more: use the search above</div>`));
      }
      this.mkBody.append(card);
    }
    if (!any) this.mkBody.append(el(`<div class="empty-card">${q ? 'No marker matches that.' : 'No markers yet.'}</div>`));
  }

  // ---------------------------------------------------------------- stats
  // Top to bottom: what is happening now, the world's progress, the people, the totals, what has
  // been found, and the server's own details folded away at the end.
  renderStats(d) {
    const p = $('#panel-stats');
    if (!d || !d.server) { p.replaceChildren(el('<div class="empty">No stats yet.</div>')); return; }
    const s = d.server, tiles = s.tiles || {}, t = d.totals || {}, ps = d.players || [];
    const count = (id, f) => { const set = markerStore.sets.find((x) => x.id === id); return set ? (f ? set.markers.filter(f).length : set.markers.length) : 0; };
    const tile = (v, label, title = '') => `<div class="stat-tile"${title ? ` title="${escape(title)}"` : ''}><b>${v}</b><span>${label}</span></div>`;
    p.replaceChildren();

    // ---- now
    p.append(el('<h3>Now</h3>'));
    p.append(el(`<div class="stat-tiles">
      ${tile(s.day ?? '–', 'day' + (s.night ? ' · night' : ''))}
      ${tile(s.online ?? 0, (s.online === 1 ? 'player' : 'players') + ' online')}
      ${tile((s.exploredPercent ?? 0).toFixed(1) + '%', 'explored')}
    </div>`));
    p.append(el('<div class="sub">Players online, last 24 h</div>'));
    p.append(sparkline(d.onlineHistory || []));

    // ---- the weather to come in each biome (the game draws it by time alone, so it holds for everyone)
    const fc = d.forecast;
    if (fc && fc.biomes && fc.biomes.length && s.worldTime !== undefined) {
      const cols = 3, when = [];
      for (let k = 0; k < cols; k++) { const m = Math.round((fc.start + k * fc.period - s.worldTime) / 60); when.push(k === 0 ? 'Now' : `in ${m} min`); }
      p.append(el('<h3>Weather</h3>'));
      p.append(el(`<table class="forecast"><thead><tr><th></th>${when.map((w) => `<th>${w}</th>`).join('')}</tr></thead><tbody>${fc.biomes.map((b) =>
        `<tr><td>${escape(b.biome)}</td>${b.weather.slice(0, cols).map((w, k) => `<td class="${k > 0 && w === b.weather[k - 1] ? 'same' : ''}">${escape(w)}</td>`).join('')}</tr>`).join('')}</tbody></table>`));
      p.append(el('<div class="sub">Each change comes on game time: sleeping through the night skips ahead</div>'));
    }

    // ---- bosses: the world's own record of which have fallen
    if (d.bosses && d.bosses.length) {
      const down = d.bosses.filter((b) => b.defeated).length;
      p.append(el(`<h3>Bosses <span class="count">${down} of ${d.bosses.length}</span></h3>`));
      p.append(el(`<div class="boss-track">${d.bosses.map((b) => `<div class="boss${b.defeated ? ' down' : ''}" title="${escape(b.name)}${b.defeated ? ': defeated' : ': not yet'}"><span>${b.defeated ? '&#10003;' : '?'}</span>${escape(b.name)}</div>`).join('')}</div>`));
    }

    // the world's other keys: what its first kills unlocked (raids), and the world modifiers it runs with
    const gk = (d.globalKeys || []).map((k) => String(k));
    const keySet = new Set(gk.map((k) => k.split(' ')[0].toLowerCase()));
    const progress = [], mods = [], effects = [], kills = [], quests = [];
    const knownBoss = new Set(['defeated_eikthyr', 'defeated_gdking', 'defeated_bonemass', 'defeated_dragon', 'defeated_goblinking', 'defeated_queen', 'defeated_fader', 'defeated_frozenking', 'defeated_frozenking_p3']);
    let preset = null;
    for (const k of gk) {
      const [name, ...rest] = k.split(' '), low = name.toLowerCase(), val = rest.join(' ');
      if (knownBoss.has(low) || low === 'activebosses' || MINI_BOSSES[low]) continue;
      if (PROGRESS_KEYS[low]) { progress.push(PROGRESS_KEYS[low]); continue; }
      if (HILDIR_QUESTS[low]) { quests.push(HILDIR_QUESTS[low]); continue; }
      if (low === 'preset') { preset = val; continue; }
      // any other "beaten" key (other bosses, often from mods): defeated_x, x_defeated, x_killed, killedx
      const m = low.match(/^defeated_(.+)$|^(.+)_defeated$|^(.+)_killed$|^killed_?(.+)$/);
      if (m && !MODIFIERS[low]) { const who = m[1] || m[2] || m[3] || m[4]; kills.push(KILL_NAMES[who] || prettyKey(who)); continue; }
      effects.push(modifierText(low, val));
    }
    // Hildir's sisters, each the boss of one of her quest dungeons, and the Writhan
    p.append(el('<div class="sub">Mini bosses</div>'));
    p.append(el(`<div class="boss-track">${Object.entries(MINI_BOSSES).map(([k, [n, where]]) => { const down = keySet.has(k);
      return `<div class="boss${down ? ' down' : ''}" title="${escape(n)}${where ? ', ' + escape(where) : ''}${down ? ': defeated' : ': not yet'}"><span>${down ? '&#10003;' : '?'}</span>${escape(n)}</div>`; }).join('')}</div>`));
    if (quests.length) {
      p.append(el('<div class="sub">Hildir\'s quests</div>'));
      p.append(el(`<div class="chips">${quests.map(([t, tip]) => `<span class="chip on" title="${escape(tip)}">${escape(t)}</span>`).join('')}</div>`));
    }
    if (kills.length) {
      p.append(el('<div class="sub">Other kills</div>'));
      p.append(el(`<div class="chips">${kills.map((t) => `<span class="chip on" title="A world key the game (or a mod) set when this was beaten">${escape(t)}</span>`).join('')}</div>`));
    }
    // the world settings as picked when it was made ("combat_default:resources_more:..."), the exact
    // effects (resource rate 150%...) on hover; without a preset, the effects themselves
    if (preset) {
      const fx = effects.map((e) => e[0]).join(', ');
      for (const part of preset.split(':')) {
        const [what, level] = part.split('_');
        if (!what) continue;
        mods.push([`${PRESET_NAMES[what] || what}: ${LEVELS[level] || level || ''}`, fx ? `In effect: ${fx}` : part]);
      }
    } else mods.push(...effects);
    if (progress.length) {
      p.append(el('<div class="sub">Unlocked by first kills</div>'));
      p.append(el(`<div class="chips">${progress.map(([t, tip]) => `<span class="chip on" title="${escape(tip)}">${escape(t)}</span>`).join('')}</div>`));
    }
    p.append(el('<div class="sub">World modifiers</div>'));
    p.append(el(mods.length ? `<div class="chips">${mods.map(([t, tip]) => `<span class="chip" title="${escape(tip)}">${escape(t)}</span>`).join('')}</div>` : '<div class="note">Normal world: no modifiers.</div>'));

    // ---- players, then who leads what
    p.append(el(`<h3>Players <span class="count">${ps.length}</span></h3>`));
    if (!ps.length) p.append(el('<div class="empty">Nobody has played since the map was installed.</div>'));
    else {
      // sorted by the column clicked (again: the other way round), remembered in this browser
      const [sk, sdir] = this.playerSort();
      const val = (pl) => sk === 'name' ? (pl.name || '').toLowerCase() : sk === 'seen' ? (pl.online ? '~' : pl.lastSeen || '') : (pl[sk] || 0);
      const sorted = ps.slice().sort((a, b) => { const x = val(a), y = val(b); return (x < y ? -1 : x > y ? 1 : 0) * sdir; });
      const rows = sorted.map((pl) => `<tr class="${pl.online ? 'on' : ''}" data-x="${pl.lastX ?? ''}" data-z="${pl.lastZ ?? ''}">
        <td>${escape(pl.name)}</td><td class="num">${fmtDuration(pl.playtime)}</td><td class="num">${pl.deaths}</td>
        <td class="num">${fmtDist(pl.distance)}</td><td class="num">${(pl.built || 0).toLocaleString()}</td><td class="num" title="${escape(pl.lastSeen)}">${pl.online ? 'now' : fmtAgo(pl.lastSeen).replace(' ago', '').replace('just now', 'now')}</td></tr>`).join('');
      const table = el(`<div style="overflow:auto"><table class="stats"><thead><tr><th data-sort="name">Name</th><th class="num" data-sort="playtime" title="Play time since the map first saw them">Played</th><th class="num" data-sort="deaths" title="Deaths">Died</th><th class="num" data-sort="distance" title="Distance walked">Walked</th><th class="num" data-sort="built" title="Everything they ever built that still stands in this world, from before the map was installed too">Built</th><th class="num" data-sort="seen" title="Last seen">Seen</th></tr></thead><tbody>${rows}</tbody></table></div>`);
      for (const th of table.querySelectorAll('th[data-sort]')) {
        const k = th.dataset.sort;
        if (k === sk) th.textContent += sdir < 0 ? ' ▾' : ' ▴';
        th.addEventListener('click', () => {
          const box = [p, p.parentElement], ys = box.map((b) => b && b.scrollTop);   // stay where the table is
          this.setPlayerSort(k, k === sk ? -sdir : k === 'name' ? 1 : -1);
          this.renderStats(statsStore.data);
          box.forEach((b, i) => { if (b) b.scrollTop = ys[i]; });
        });
      }
      for (const tr of table.querySelectorAll('tbody tr'))
        if (tr.dataset.x) { tr.style.cursor = 'pointer'; tr.addEventListener('click', () => this.app.goTo(+tr.dataset.x, +tr.dataset.z, 6)); }
      p.append(table);
    }
    const boards = [
      ['playtime', 'Never logs off', 'most time played', (v) => fmtDuration(v)],
      ['distance', 'Wanderer', 'farthest walked', (v) => fmtDist(v)],
      ['built', 'Master builder', 'most pieces standing (all time)', (v) => v.toLocaleString() + ' pieces'],
      ['revealed', 'Pathfinder', 'most map revealed', (v) => v.toLocaleString() + ' cells'],
      ['bossKills', 'Slayer', 'most boss kills', (v) => v + (v === 1 ? ' boss' : ' bosses')],
      ['raids', 'Raid veteran', 'most raids weathered', (v) => v + (v === 1 ? ' raid' : ' raids')],
      ['finds', 'Discoverer', 'most altars and traders found', (v) => v + (v === 1 ? ' find' : ' finds')],
      ['deaths', "Odin's regular", 'most deaths', (v) => v + (v === 1 ? ' death' : ' deaths')],
    ];
    const cards = boards.map(([k, title, what, fmt]) => {
      const top = ps.filter((x) => (x[k] || 0) > 0).sort((a, b) => b[k] - a[k])[0];
      return top ? `<div class="lead" title="${escape(what)}"><span class="lead-title">${escape(title)}</span><b>${escape(top.name)}</b><span class="lead-val">${fmt(top[k])}</span></div>` : '';
    }).join('');
    if (cards) { p.append(el('<h3>Leaderboard</h3>')); p.append(el(`<div class="leads">${cards}</div>`)); }

    // ---- totals: the adventure so far, and what stands in the world
    p.append(el('<h3>Totals</h3>'));
    p.append(el('<div class="sub">Adventure</div>'));
    p.append(el(`<div class="stat-tiles">
      ${tile(t.bossKills ?? 0, 'boss kills')}${tile(t.raids ?? 0, 'raids')}${tile(t.nightsSlept ?? 0, 'nights slept')}
      ${tile(t.deaths ?? 0, 'deaths')}${tile(t.players ?? 0, 'players ever')}${tile(t.peakOnline ?? 0, 'most online at once', t.peakOnlineUtc ? new Date(t.peakOnlineUtc).toLocaleString() : '')}
    </div>`));
    p.append(el('<div class="sub">The built world</div>'));
    p.append(el(`<div class="stat-tiles">
      ${tile((s.structures ?? 0).toLocaleString(), 'pieces built')}${tile(count('bases'), 'bases')}${tile(count('portals'), 'portals')}
      ${tile(`${count('vehicles', (m) => m.cat !== 'cart')} / ${count('vehicles', (m) => m.cat === 'cart')}`, 'ships / carts')}${tile((s.terraformedZones ?? 0).toLocaleString(), 'terraformed zones')}${tile((s.trees ?? 0).toLocaleString(), 'trees standing')}
    </div>`));
    p.append(el('<div class="note">Boss kills, raids and nights slept count from when the map started tracking them.</div>'));

    // ---- discoveries: boss altars and traders found, newest first
    const disc = d.discoveries || [];
    p.append(el(`<h3>Discoveries <span class="count">${disc.length}</span></h3>`));
    if (!disc.length) p.append(el('<div class="empty">Nothing found yet.</div>'));
    else {
      const list = el('<div></div>');
      for (const f of disc.slice().sort((a, b) => (b.when || '').localeCompare(a.when || ''))) {
        // the icon its marker has on the map (a Hildir lair, Hildir's or the Bog Witch's camp...)
        const m = (this.mkSets || []).flatMap((st) => st.markers || []).find((q) => Math.abs(q.x - f.x) < 3 && Math.abs(q.z - f.z) < 3);
        const icon = (m && (m.icon || m.cat)) || (f.kind === 'boss' ? 'boss' : f.kind === 'miniboss' ? 'miniboss' : 'trader');
        const when = f.when ? `found ${new Date(f.when).toLocaleDateString([], { month: 'short', day: 'numeric' })}${f.who ? ' by ' + escape(f.who) : ''}` : 'found before tracking began';
        const r = el(`<div class="row clickable">${this.markerIcon(icon, colors[icon])}<div class="grow"><div class="name">${escape(f.label)}${f.kind === 'boss' ? "'s altar" : ''}</div><div class="meta">${when}</div></div></div>`);
        r.addEventListener('click', () => this.app.goTo(f.x, f.z, Math.max(this.app.map.getZoom(), 6)));
        list.append(r);
      }
      p.append(list);
    }

    // ---- the server's own details, folded away (remembered open or shut)
    const det = el(`<details class="server-details"${this.serverOpen ? ' open' : ''}><summary>Server details</summary><dl class="kv">
      <dt>Up since</dt><dd>${s.startedUtc ? new Date(s.startedUtc).toLocaleString() : '–'}</dd>
      <dt>Last world sweep</dt><dd>${s.lastSweepUtc ? fmtAgo(s.lastSweepUtc) + ` (${(s.lastSweepSeconds || 0).toFixed(1)} s)` : 'pending'}</dd>
      <dt>World objects</dt><dd>${(s.objects ?? 0).toLocaleString()}</dd>
      <dt>Map tiles rendered</dt><dd>${tiles.onDisk ?? 0}${tiles.queued ? ` (+${tiles.queued} queued)` : ''}</dd>
      <dt>Render time / tile</dt><dd>${tiles.avgMs ? tiles.avgMs.toFixed(0) + ' ms' : '–'}${tiles.mainThreadSampling ? ' · main-thread' : ''}</dd>
      <dt>Max detail</dt><dd>${Math.pow(2, 7 - (tiles.maxRenderZoom ?? 7))} m / px</dd>
      <dt>3D models</dt><dd>${prefabs.stats.exported ?? 0} prefabs${prefabs.stats.unreadable ? ` (${prefabs.stats.unreadable} unreadable)` : ''}${prefabs.stats.queued ? ` +${prefabs.stats.queued} queued` : ''}</dd>
      <dt>Munin Map</dt><dd>${escape(this.app.config?.version || '')}</dd>
    </dl></details>`);
    det.addEventListener('toggle', () => { this.serverOpen = det.open; });
    p.append(det);
  }

  // ---------------------------------------------------------------- events
  buildEvents() {
    const p = $('#panel-events');
    // Events, or the server's own console lines (admins: Live/ServerLog on the server)
    const mode = el('<div class="seg"><button class="on" data-m="events">Events</button><button data-m="log">Server log</button></div>');
    for (const b of mode.querySelectorAll('button')) b.addEventListener('click', () => {
      for (const x of mode.querySelectorAll('button')) x.classList.toggle('on', x === b);
      this.eventsBox.hidden = b.dataset.m !== 'events';
      this.logBox.hidden = b.dataset.m !== 'log';
      this.logOn = b.dataset.m === 'log';
      if (this.logOn) this.pollLog(true);
    });
    p.append(mode);
    this.eventsBox = el('<div></div>');
    this.logBox = this.buildLog();
    this.logBox.hidden = true;
    p.append(this.eventsBox, this.logBox);
    // which kinds of event the list shows: a chip each, with All / None
    const head = el('<div class="filters-head"><span>Show these events</span><button class="link" data-all="1">All</button><button class="link" data-all="0">None</button></div>');
    const filters = el('<div class="ev-filters"></div>');
    const chips = [], groups = new Map();
    for (const [t, label, tip, group] of EVENT_KINDS) {
      if (!groups.has(group)) {
        const g = el(`<div class="ev-group"><div class="ev-group-name">${escape(group)}</div><div class="filters"></div></div>`);
        filters.append(g);
        groups.set(group, g.querySelector('.filters'));
      }
      const lab = el(`<label title="${escape(tip)}"><input type="checkbox" checked>${escape(label)}</label>`);
      const box = lab.querySelector('input');
      const set = (on) => {
        box.checked = on;
        lab.classList.toggle('off', !on);
        if (on) this.eventFilters.add(t); else this.eventFilters.delete(t);
      };
      box.addEventListener('change', () => { set(box.checked); this.applyEventFilter(); });
      chips.push(set);
      groups.get(group).append(lab);
    }
    for (const b of head.querySelectorAll('button')) b.addEventListener('click', () => { for (const set of chips) set(b.dataset.all === '1'); this.applyEventFilter(); });
    this.eventsBox.append(head, filters);
    this.eventList = el('<div id="event-list"></div>');
    this.eventsNone = el('<div class="empty" hidden>No events of the kinds picked above yet. "Show older" looks further back.</div>');
    this.eventsBox.append(this.eventList, this.eventsNone);
    // older history from the server's log, a page at a time
    this.olderBtn = el('<button class="btn older">Show older</button>');
    this.olderBtn.addEventListener('click', () => this.loadOlder());
    this.eventsBox.append(this.olderBtn);
  }

  // ---- the server log: every line the game wrote, in order, with the time it was written. Needs
  // the server's admin key (announce.token beside the plugin), kept in this browser once entered.
  buildLog() {
    const box = el(`<div class="serverlog">
      <div class="log-key" hidden><p>The server log is for admins. Enter the admin key (the text in <code>announce.token</code> beside the plugin on the server):</p>
        <div class="row-in"><input type="text" placeholder="Admin key" autocomplete="off" spellcheck="false"><button class="btn">Open</button></div><div class="err" hidden>That key was not accepted.</div></div>
      <div class="log-tools" hidden><input type="search" placeholder="Search the log"><div class="log-opts"><label><input type="checkbox" checked> Hide routine lines</label><button class="btn" title="Forget the admin key in this browser">Lock</button></div></div>
      <div class="log-lines"></div></div>`);
    this.logLines = box.querySelector('.log-lines');
    this.logKeyBox = box.querySelector('.log-key');
    this.logTools = box.querySelector('.log-tools');
    const keyIn = this.logKeyBox.querySelector('input');
    const go = () => { this.setLogKey(keyIn.value.replace(/\s+/g, '')); keyIn.value = ''; this.pollLog(true); };   // spaces never belong in a key
    this.logKeyBox.querySelector('button').addEventListener('click', go);
    keyIn.addEventListener('keydown', (e) => { if (e.key === 'Enter') go(); });
    const [search, quiet, lock] = [this.logTools.querySelector('input[type=search]'), this.logTools.querySelector('input[type=checkbox]'), this.logTools.querySelector('button')];
    this.logFilter = { q: '', quiet: true };
    search.addEventListener('input', () => { this.logFilter.q = search.value.toLowerCase(); this.filterLog(); });
    quiet.addEventListener('change', () => { this.logFilter.quiet = quiet.checked; this.filterLog(); });
    lock.addEventListener('click', () => { this.setLogKey(''); this.logLines.replaceChildren(); this.logNext = -1; this.pollLog(true); });
    this.logNext = -1;
    return box;
  }

  // a marker's icon in a list, the same picture as on the map: the game's sprite in its badge
  // (layers/markercanvas.js iconImage), or bare for a table pin; our own glyph where the game has none
  markerIcon(name, color, bare = false) {
    const img = this.app.layers && this.app.layers.markers && this.app.layers.markers.gameIcon(name);
    if (!img) return `<span class="ico">${iconSvg(name, color)}</span>`;
    return `<span class="ico game${bare ? ' bare' : ''}" style="--ring:${color || '#c8cdd6'}"><img src="${img}" alt=""></span>`;
  }

  // the stats panel's player table order: [column, 1 up or -1 down]; play time, most first, by default
  playerSort() {
    if (!this.pSort) { try { this.pSort = JSON.parse(localStorage.getItem('munin-player-sort')); } catch (e) { /* the default */ } }
    if (!Array.isArray(this.pSort) || this.pSort.length !== 2) this.pSort = ['playtime', -1];
    return this.pSort;
  }
  setPlayerSort(k, dir) { this.pSort = [k, dir]; try { localStorage.setItem('munin-player-sort', JSON.stringify(this.pSort)); } catch (e) { /* this visit only */ } }

  setLogKey(k) { try { if (k) localStorage.setItem('munin-admin-key', k); else localStorage.removeItem('munin-admin-key'); } catch (e) { /* this visit only */ } this.logKey = k; }
  getLogKey() { if (this.logKey === undefined) { try { this.logKey = localStorage.getItem('munin-admin-key') || ''; } catch (e) { this.logKey = ''; } } return this.logKey; }

  // fetch new lines every few seconds while the view is open
  async pollLog(now) {
    clearTimeout(this.logTimer);
    if (!this.logOn || this.active !== 'events') return;
    const key = this.getLogKey();
    this.logKeyBox.hidden = !!key; this.logTools.hidden = !key;
    if (!key) return;
    try {
      const r = await fetch(`api/serverlog?after=${this.logNext}&limit=${this.logNext < 0 ? 1500 : 1000}`, { cache: 'no-store', headers: { 'X-Munin-Token': key } });
      if (r.status === 403) { this.setLogKey(''); this.logKeyBox.hidden = false; this.logTools.hidden = true; this.logKeyBox.querySelector('.err').hidden = false; return; }
      this.logKeyBox.querySelector('.err').hidden = true;
      const d = await r.json();
      const stick = this.logLines.scrollHeight - this.logLines.scrollTop - this.logLines.clientHeight < 40;   // following the end
      for (const l of d.lines || []) this.logLines.append(this.logRow(l));
      while (this.logLines.children.length > 3000) this.logLines.firstElementChild.remove();
      if ((d.lines || []).length) this.logNext = d.next;
      if (stick || now) this.logLines.scrollTop = this.logLines.scrollHeight;
    } catch (e) { /* offline: try again */ }
    this.logTimer = setTimeout(() => this.pollLog(), 3000);
  }

  logRow(l) {
    const d = new Date(l.ts);
    const t = isNaN(d) ? '' : (d.toDateString() === new Date().toDateString() ? '' : d.toLocaleDateString([], { month: 'short', day: 'numeric' }) + ' ')
      + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    const text = l.text.replace(/^\d\d\/\d\d\/\d{4} \d\d:\d\d:\d\d: /, '');   // the game's own date in front: the time column has it
    const row = el(`<div class="logline ${l.level}"><time>${t}</time><span>${escape(text)}</span></div>`);
    row.dataset.text = l.text.toLowerCase();
    row.dataset.routine = ROUTINE.test(l.text) ? '1' : '';
    row.hidden = !this.logVisible(row);
    return row;
  }
  logVisible(row) { return !(this.logFilter.quiet && row.dataset.routine) && (!this.logFilter.q || row.dataset.text.includes(this.logFilter.q)); }
  filterLog() { for (const r of this.logLines.children) r.hidden = !this.logVisible(r); }

  applyEventFilter() {
    let shown = 0;
    for (const e of this.eventList.children) { e.hidden = !this.eventFilters.has(e.dataset.type); if (!e.hidden) shown++; }
    if (this.eventsNone) this.eventsNone.hidden = shown > 0;
  }

  eventRow(e) {
    const said = e.type === 'chat' || e.type === 'shout' || e.type === 'whisper';
    // chat reads "Name: message", the name in a colour of its own so a conversation is easy to follow
    const who = said ? `<b style="color:${nameColor(e.name)}">${escape(e.name)}:</b>` : `<b>${escape(e.name)}</b>`;
    const row = el(`<div class="event${said ? ' said' : ''}${e.x !== undefined ? ' clickable' : ''}" data-type="${escape(e.type)}"><time${e.approx ? ' title="Worked out from the server log: to within a few minutes"' : ''}>${eventTime(e)}</time><div class="t">${who} <span class="msg">${escape(e.text)}</span></div></div>`);
    if (e.x !== undefined) row.addEventListener('click', () => this.app.goTo(e.x, e.z, 6));
    row.hidden = !this.eventFilters.has(e.type);
    row.dataset.ts = e.ts;
    return row;
  }

  async loadOlder() {
    const rows = this.eventList.children, last = rows[rows.length - 1];
    const before = last ? last.dataset.ts : new Date().toISOString();
    this.olderBtn.disabled = true; this.olderBtn.textContent = 'Loading...';
    try {
      const r = await fetch(`data/events/older.json?before=${encodeURIComponent(before)}&limit=100`, { cache: 'no-store' });
      const list = (await r.json()).filter((e) => !hiddenEvent(e));
      this.olderLoaded = true;
      for (let i = list.length - 1; i >= 0; i--) this.eventList.append(this.eventRow(list[i]));   // newest of them first, under what is shown
      this.applyEventFilter();
      this.olderBtn.textContent = list.length ? 'Show older' : 'No older events';
      this.olderBtn.disabled = !list.length;
    } catch (err) { this.olderBtn.textContent = 'Show older'; this.olderBtn.disabled = false; }
  }

  addEvents(list, initial) {
    if (!list) return;
    list = list.filter((e) => !hiddenEvent(e));
    for (const e of list) {
      this.eventList.prepend(this.eventRow(e));
      if (!initial && this.active !== 'events') this.unread++;
    }
    if (!this.olderLoaded) while (this.eventList.children.length > 300) this.eventList.lastElementChild.remove();   // trimmed unless older history was asked for
    this.applyEventFilter();
    this.badge();
    if (!initial) for (const e of list) if (e.type === 'death' || e.type === 'join' || e.type === 'leave' || e.type === 'server' || e.type === 'boss' || e.type === 'raid' || e.type === 'invasion' || e.type === 'found' || e.type === 'biome') this.app.toast(`${e.name} ${e.text}`);
  }
}

// the trees and rocks in the map key, by kind (vegpack.js VEG), trees first, then bushes, then the ground
const VEG_KEY = [[1, 'Beech'], [12, 'Oak'], [13, 'Birch'], [18, 'Autumn birch'], [2, 'Fir'], [14, 'Pine'], [3, 'Swamp tree'], [4, 'Mistlands tree'],
  [11, 'Ash tree'], [5, 'Dead tree'], [6, 'Bush'], [15, 'Raspberry'], [16, 'Blueberry'], [17, 'Cloudberry'], [29, 'Lingonberry'], [30, 'Ashvine'], [31, 'Ash fern'],
  [19, 'Mushroom'], [20, 'Yellow mushroom'], [21, 'Magecap'], [22, 'Jotun puffs'], [23, 'Smoke puff'], [24, 'Thistle'], [25, 'Dandelion'],
  [26, 'Fiddlehead'], [27, 'Wild barley'], [28, 'Wild flax'], [9, 'Stump'], [7, 'Boulder'], [32, 'Cliff'], [33, 'Giant bones'],
  [34, 'Copper'], [35, 'Tin'], [36, 'Silver'], [37, 'Obsidian'], [38, 'Muddy scrap pile'], [8, 'Other ore']];

// small line icons for the layer rows that have no map glyph
const SVG = (d) => `<svg viewBox="0 0 24 24" style="fill:none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">${d}</svg>`;
const TREE_SVG = SVG('<path d="M12 3 6 12h3l-4 6h14l-4-6h3z" fill="#4f8a3a" stroke="#2c5234"/><path d="M12 18v3" stroke="#7a5a3a"/>');
const BUSH_SVG = SVG('<circle cx="12" cy="13" r="7" fill="#466e32" stroke="#2f4d22"/><circle cx="9.5" cy="11" r="1.6" fill="#4e64cc" stroke="none"/><circle cx="14" cy="14.5" r="1.6" fill="#c43a4a" stroke="none"/><circle cx="13.5" cy="10" r="1.4" fill="#e4a840" stroke="none"/>');
const BIOME_SVG = SVG('<rect x="3" y="3" width="9" height="9" fill="#86ba48" stroke="none"/><rect x="12" y="3" width="9" height="9" fill="#dec458" stroke="none"/><rect x="3" y="12" width="9" height="9" fill="#2e5c38" stroke="none"/><rect x="12" y="12" width="9" height="9" fill="#d6dce4" stroke="none"/><rect x="3" y="3" width="18" height="18" rx="2"/>');
const MIST_SVG = SVG('<path d="M3 9h12M6 13h15M3 17h11" stroke="#b4b8c4"/><circle cx="18" cy="8" r="2.4" fill="#9fd8ff" stroke="#5aa8e0"/>');
const MUSHROOM_SVG = SVG('<path d="M4 12a8 7 0 0 1 16 0z" fill="#d6423a" stroke="#8e2a24"/><circle cx="9" cy="9" r="1.2" fill="#fff" stroke="none"/><circle cx="14" cy="8" r="1" fill="#fff" stroke="none"/><path d="M10 12v6a2 2 0 0 0 4 0v-6" fill="#efe6d2" stroke="#bfb39a"/>');
const SHRUB_SVG = SVG('<circle cx="9" cy="14" r="5" fill="#466e32" stroke="#2f4d22"/><circle cx="15" cy="12" r="6" fill="#4f7a38" stroke="#2f4d22"/>');
const ORE_SVG = SVG('<path d="M4 18l3-8 5-3 5 3 3 8z" fill="#5a5a56" stroke="#3e3e3a"/><circle cx="9" cy="13" r="1.6" fill="#d07a40" stroke="none"/><circle cx="14" cy="11" r="1.4" fill="#c8d2de" stroke="none"/><circle cx="14.5" cy="15.5" r="1.3" fill="#d07a40" stroke="none"/>');
const PLANT_SVG = SVG('<path d="M12 21v-9M12 14c-3 0-5-2-5-5 3 0 5 2 5 5zM12 12c0-3 2-5 5-5 0 3-2 5-5 5z" stroke="#5a9a3c" fill="#7cba4e"/><circle cx="12" cy="5" r="2" fill="#706ed6" stroke="none"/>');
const ROCK_SVG = SVG('<path d="M4 18l3-8 5-3 5 3 3 8z" fill="#767670" stroke="#4e4e4a"/><path d="M10 12l2 2 3-1" stroke="#86684a" stroke-width="1.6"/>');
const LABEL_SVG = SVG('<rect x="3" y="7" width="18" height="10" rx="3"/><path d="M7 12h10"/>');
const GRID_SVG = SVG('<rect x="4" y="4" width="16" height="16" rx="1.5"/><path d="M4 12h16M12 4v16" stroke-width="1.4"/>');
const RINGS_SVG = SVG('<circle cx="12" cy="12" r="2" fill="#7cff4f" stroke="none"/><circle cx="12" cy="12" r="5.5" stroke="#7cff4f"/><circle cx="12" cy="12" r="9" stroke="#7cff4f" stroke-dasharray="2 2.5"/>');
const SUN_SVG = SVG('<circle cx="12" cy="12" r="4" fill="#ffd866" stroke="#ffd866"/><path d="M12 2v2.5M12 19.5V22M2 12h2.5M19.5 12H22M4.9 4.9l1.8 1.8M17.3 17.3l1.8 1.8M4.9 19.1l1.8-1.8M17.3 6.7l1.8-1.8" stroke="#ffd866"/>');
const SHADOW_SVG = SVG('<circle cx="10" cy="10" r="5"/><path d="M8 19c3 1.5 9 1.5 12-2" opacity=".6"/>');

// the mini bosses: the key the game sets when each is beaten
const MINI_BOSSES = {
  bosshildir1: ['Brenna', 'Smouldering Tomb, Black Forest'],
  bosshildir2: ['Geirrhafa', 'Howling Cavern, Mountains'],
  bosshildir3: ['Zil & Thungr', 'Sealed Tower, Plains'],
  defeated_writhan: ['Writhan', ''],
};
const HILDIR_QUESTS = {
  hildir1: ['Brenna\'s chest returned', 'Hildir\'s first quest done'],
  hildir2: ['Geirrhafa\'s chest returned', 'Hildir\'s second quest done'],
  hildir3: ['Zil & Thungr\'s chest returned', 'Hildir\'s third quest done'],
};
// kill keys whose game name is not just the key prettied
const KILL_NAMES = { elakingmole: 'Eyeless One' };
const prettyKey = (s) => s.replace(/_/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, (c) => c.toUpperCase());

// world keys the game sets as a world progresses (each widens what can attack you)
const PROGRESS_KEYS = {
  killedtroll: ['Troll raids', 'The first troll was killed: trolls can now raid bases'],
  killed_surtling: ['Surtling raids', 'The first surtling was killed: surtlings can now raid bases'],
  killedbat: ['Bat raids', 'The first bat was killed: bats can now raid bases'],
  stonecircle: ['Stone circle', 'The stone circle event has happened'],
  ashlandsocean: ['Ashlands sea', 'Someone has sailed the Ashlands sea'],
};
// a world modifier key ("resourcerate 200", "nomap") in words
const MODIFIERS = {
  playerdamage: 'Player damage', enemydamage: 'Enemy damage', worldlevel: 'World level', eventrate: 'Raid rate', resourcerate: 'Resources',
  staminarate: 'Stamina use', adrenalinerate: 'Adrenaline', eitrrate: 'Eitr use', durabilityrate: 'Durability loss', foodrate: 'Food duration',
  movestaminarate: 'Movement stamina', staminaregenrate: 'Stamina regen', skillgainrate: 'Skill gain', skillreductionrate: 'Skill loss on death',
  enemyspeedsize: 'Enemy speed & size', enemylevelupyrate: 'Enemy level-ups', enemyleveluprate: 'Enemy level-ups', carryweightrate: 'Carry weight',
  playerevents: 'Raids follow each player', fire: 'Fire spreads', deathkeepequip: 'Keep equipment on death', deathdeleteitems: 'Items lost on death',
  deathdeleteunequipped: 'Unequipped items lost on death', deathskillsreset: 'All skills lost on death', deathkeepinventory: 'Keep inventory on death',
  nobuildcost: 'No build cost', nocraftcost: 'No craft cost', allpiecesunlocked: 'All pieces unlocked', noworkbench: 'No workbench needed',
  allrecipesunlocked: 'All recipes unlocked', worldlevellockedtools: 'Tools locked by world level', passivemobs: 'Passive enemies', nomap: 'No map',
  noportals: 'No portals', nobossportals: 'No portals near bosses', dungeonbuild: 'Building in dungeons', teleportall: 'Portals carry everything',
  nopseudodrops: 'No extra drops', nobuildingfall: 'Buildings never collapse', noheavysnow: 'No heavy snow', allheavysnow: 'Heavy snow everywhere', preset: 'Preset',
};
// the world-settings preset ("combat_hard:raids_more:...") in words
const PRESET_NAMES = { combat: 'Combat', deathpenalty: 'Death penalty', resources: 'Resources', raids: 'Raids', portals: 'Portals' };
const LEVELS = { default: 'Normal', veryeasy: 'Very easy', easy: 'Easy', hard: 'Hard', veryhard: 'Very hard', casual: 'Casual', hardcore: 'Hardcore',
  more: 'More', muchmore: 'Much more', most: 'Most', less: 'Less', muchless: 'Much less', none: 'None' };
function modifierText(name, val) {
  const label = MODIFIERS[name] || name.replace(/_/g, ' ');
  if (!val) return [label, name];
  const n = Number(val);
  // a scale on the game's normal amount (skill loss 15: 15% of what a death normally costs), not a share of the whole
  const shown = Number.isFinite(n) && name.endsWith('rate') || ['playerdamage', 'enemydamage', 'enemyspeedsize'].includes(name) ? `${n}% of normal` : val;
  return [`${label}: ${shown}`, `${name} ${val}`];
}

// the kinds of event, their names on the filter chips and what each covers
const EVENT_KINDS = [
  ['join', 'Joins', 'A player joined the server', 'Players'],
  ['leave', 'Leaves', 'A player left the server', 'Players'],
  ['death', 'Deaths', 'A player died (click to see where)', 'Players'],
  ['biome', 'Biomes', 'A player entered a biome for the first time', 'Players'],
  ['boss', 'Boss kills', 'A boss was defeated, and by whom', 'World'],
  ['raid', 'Raids', 'A raid started or ended (click to see where)', 'World'],
  ['invasion', 'Invasions', 'An invasion appeared somewhere in the world or went away (click to see where)', 'World'],
  ['found', 'Found', 'A boss altar or trader was found for the first time', 'World'],
  ['sleep', 'Slept', 'Everyone slept through the night', 'World'],
  ['chat', 'Chat', 'Chat messages', 'Chat'],
  ['shout', 'Shouts', 'Shouted messages, heard across the map', 'Chat'],
  ['ping', 'Pings', 'Someone pinged the map', 'Other'],
  ['pin', 'Pins', 'Someone placed a pin', 'Other'],
  ['server', 'Server', 'Messages from the server itself (startup, announcements)', 'Other'],
];

// the server's chatter that says nothing new: connection counts, object clean-up, save steps,
// socket housekeeping (shown again with "Hide routine lines" off)
const ROUTINE = /^\s*Connections \d|ZDOS:|^Destroying abandoned|^World save \([1-4]\/5\)|^ZPlayFabSocket|^Disposing socket|^Considering autobackup|^SaveSystem\.|^No autobackup|^PrepareSave|^GetSaveClone|^Available space|^Update PlayFab|^Sending message to save|^Checking for any blocked|^ZRpc timeout|^Muted PlayFab|^Placed location|^Found location of type|^Dungeon loaded|^DungeonDB|^Loaded \d+ locations/;

// left out of the feed: the in-game announcement of a leave, already listed as the leave itself
// (older logs have both), and the game's "I have arrived!" shout on every spawn (the join says it)
function hiddenEvent(e) {
  return (e.type === 'server' && /^player _.+_ (left|joined)$/.test(e.text || '')) || (e.type === 'shout' && e.text === 'I have arrived!');
}

// a steady, readable colour per player name
const NAME_COLORS = ['#7cc7ff', '#ffb86b', '#9be37a', '#f590d0', '#ffd866', '#8fd8d0', '#c9a7ff', '#ff8f8f'];
function nameColor(name) {
  let h = 0;
  for (const ch of String(name)) h = (h * 31 + ch.charCodeAt(0)) | 0;
  return NAME_COLORS[Math.abs(h) % NAME_COLORS.length];
}

function sparkline(hist) {
  const w = 300, h = 44, pad = 2;
  if (!hist.length) return el('<div class="empty">No history yet.</div>');
  const max = Math.max(1, ...hist.map((p) => p[1]));
  const t0 = hist[0][0], t1 = hist[hist.length - 1][0] || t0 + 1;
  const pts = hist.map(([t, n]) => [pad + (w - 2 * pad) * (t - t0) / Math.max(1, t1 - t0), h - pad - (h - 2 * pad) * n / max]);
  const line = pts.map((p, i) => (i ? 'L' : 'M') + p[0].toFixed(1) + ' ' + p[1].toFixed(1)).join(' ');
  const area = line + ` L${pts[pts.length - 1][0].toFixed(1)} ${h - pad} L${pts[0][0].toFixed(1)} ${h - pad} Z`;
  return el(`<svg class="spark" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none"><line class="axis" x1="0" y1="${h - pad}" x2="${w}" y2="${h - pad}"/><path class="area" d="${area}"/><path d="${line}"/><title>peak ${max}</title></svg>`);
}
