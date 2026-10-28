// A card that pops up when a player is clicked, on the 2D map, in the 3D view or in the sidebar
// (online or not): their character in 3D (portrait.js), whether they're on, health, stamina and eitr,
// their connection, what they wear (the game's own item icons and names, and what each does), when
// they play (the last two weeks and recent sessions), their stats, leaderboard badges, and their
// places (bases, portals, beds, tombstones, wards, signs, last death) with a way to each. Stays open
// and keeps updating until dismissed. ?player=<name> opens one from a link.

import { escape } from './layers/markers.js';
import { iconSvg } from './icons.js';
import { stats as statsStore } from './data.js';
import { getJSON } from './net.js';
import { fmtDuration, fmtDist, fmtAgo } from './ui.js';
import { showPortrait, preloadPortrait } from './portrait.js';

const ARMOR_SLOTS = new Set(['helmet', 'chest', 'legs', 'shoulder', 'utility', 'trinket']);
const SLOTS = [['helmet', 'Head'], ['chest', 'Chest'], ['legs', 'Legs'], ['shoulder', 'Cape'], ['utility', 'Belt'], ['trinket', 'Trinket'], ['right', 'Right hand'], ['left', 'Left hand']];

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

// what each piece of gear does (the server's World/ItemIcons), fetched once
let gearData = null, gearAsked = false;
function gearStats() {
  if (!gearAsked) { gearAsked = true; getJSON('data/gear.json').then((m) => { gearData = m; onNames?.(); }).catch(() => {}); }
  return gearData;
}
// a stat's [low, high]: at the level worn when known, else from level 1 to the most it can be upgraded to
function statRange([base, per], g, lv) {
  if (lv > 0) return [base + per * (lv - 1), base + per * (lv - 1)];
  return [base, base + per * Math.max(0, (g.q || 1) - 1)];
}
const num = (v) => Math.round(v * 10) / 10;
const rng = ([a, b]) => a === b ? `${num(a)}` : `${num(a)}–${num(b)}`;
const DMG = { damage: 'Damage', blunt: 'Blunt', slash: 'Slash', pierce: 'Pierce', fire: 'Fire', frost: 'Frost', lightning: 'Lightning', poison: 'Poison', spirit: 'Spirit', chop: 'Chop', pickaxe: 'Pickaxe' };
// the short line under a piece of gear: its armour, or its damage, or its block
function gearLine(g, lv) {
  const out = [];
  if (g.armor) out.push(`Armor ${rng(statRange(g.armor, g, lv))}`);
  const dmg = Object.entries(g.dmg || {}).filter(([k]) => k !== 'chop' && k !== 'pickaxe' || g.type === 'Tool');
  dmg.sort((a, b) => b[1][0] - a[1][0]);
  for (const [k, v] of dmg.slice(0, 2)) out.push(`${DMG[k]} ${rng(statRange(v, g, lv))}`);
  if (g.block && (g.type === 'Shield' || !dmg.length)) out.push(`Block ${rng(statRange(g.block, g, lv))}`);
  return out.join(' · ');
}
const MOD = { Resistant: 'Resistant to', VeryResistant: 'Very resistant to', SlightlyResistant: 'Slightly resistant to', Weak: 'Weak to', VeryWeak: 'Very weak to', SlightlyWeak: 'Slightly weak to', Immune: 'Immune to', Ignore: 'Ignores' };
// all of it, when a piece of gear is clicked
function gearDetail({ nm, label, g, lv }) {
  if (!g) return `<b>${escape(nm)}</b><span>${label}</span>`;
  const rows = [];
  rows.push(lv > 0 ? `Level ${lv}${g.q > 1 ? ` of ${g.q}` : ''}` : g.q > 1 ? `Level not shown by the game: up to ${g.q}, the range is level 1 to ${g.q}` : '');
  if (g.armor) rows.push(`Armor ${rng(statRange(g.armor, g, lv))}`);
  for (const [k, v] of Object.entries(g.dmg || {})) rows.push(`${DMG[k]} ${rng(statRange(v, g, lv))}`);
  if (g.block) rows.push(`Block ${rng(statRange(g.block, g, lv))}`);
  for (const [t, m] of g.mods || []) rows.push(`${MOD[m] || m} ${t.toLowerCase()}`);
  if (g.effect) rows.push(escape(g.effect));
  if (g.set) rows.push(`Set of ${g.set[2]}${g.set[1] ? `: ${escape(g.set[1])}` : ''}`);
  if (g.move) rows.push(`Movement ${g.move > 0 ? '+' : ''}${Math.round(g.move * 100)}%`);
  if (g.eitrRegen) rows.push(`Eitr regen ${g.eitrRegen > 0 ? '+' : ''}${Math.round(g.eitrRegen * 100)}%`);
  if (g.dur) rows.push(`Durability ${rng(statRange(g.dur, g, lv))}`);
  if (g.weight) rows.push(`Weight ${num(g.weight)}`);
  return `<b>${escape(nm)}</b><span>${label}</span><ul>${rows.filter(Boolean).map((r) => `<li>${r}</li>`).join('')}</ul>`;
}

// ping and the game's rating of the link, as a few bars
function netHtml(p) {
  const q = p.netQ ?? 1, ms = p.ping;
  const level = ms < 80 && q > 0.8 ? 4 : ms < 150 && q > 0.6 ? 3 : ms < 250 && q > 0.4 ? 2 : 1;
  const word = ['', 'Poor', 'Fair', 'Good', 'Great'][level];
  const queue = p.sendQ > 20000 ? ` · ${Math.round(p.sendQ / 1024)} KB waiting to send` : '';
  return `<div class="pc-net q${level}" title="Their connection to the server: ${ms} ms, link quality ${Math.round(q * 100)}%${queue}"><i></i><i></i><i></i><i></i><span>${ms} ms · ${word}${queue ? ' · lagging' : ''}</span></div>`;
}

// their game and connection, in detail (live only; never their address)
const kb = (b) => b >= 1048576 ? `${(b / 1048576).toFixed(1)} MB/s` : b >= 1024 ? `${Math.round(b / 1024)} KB/s` : `${Math.round(b || 0)} B/s`;
function connHtml(p, server) {
  const rows = [];
  if (p.platform || p.crossplay) rows.push(['Platform', `${escape(p.platform || 'Unknown')}${p.crossplay ? ' (crossplay)' : ''}`]);
  if (p.version) rows.push(['Game version', `${escape(p.version)}${server && server !== p.version ? ` <em>server ${escape(server)}</em>` : ''}`]);
  if (p.netVersion) rows.push(['Network version', p.netVersion]);
  if (p.ping) {
    rows.push(['Ping', `${p.ping} ms`]);
    rows.push(['Link quality', `${Math.round((p.netQ ?? 1) * 100)}%`]);
    rows.push(['Bandwidth', `↓ ${kb(p.outBps)} · ↑ ${kb(p.inBps)}`]);
    if (p.sendQ) rows.push(['Waiting to send', kb(p.sendQ).replace('/s', '')]);
  }
  return rows.map(([k, v]) => `<span>${k}</span><b>${v}</b>`).join('');
}

// their stats: the long-run numbers, then where they spend their time and where they die
const BIOME_COL = { Meadows: '#8fbf4a', 'Black Forest': '#3f7a45', Swamp: '#8a6a45', Mountain: '#c8d2dc', Plains: '#d9c25c', Mistlands: '#7d77a8', Ashlands: '#c0503a', 'Deep North': '#9fd3ec', Ocean: '#3f78b5' };
function statsHtml(st) {
  const cell = (v, label, title = '') => `<div${title ? ` title="${escape(title)}"` : ''}><b>${v}</b><span>${label}</span></div>`;
  const km2 = (cells) => { const a = (cells || 0) * 144 / 1e6; return a >= 10 ? a.toFixed(0) + ' km²' : a.toFixed(1) + ' km²'; };   // explored cells are 12 m
  const hours = (st.playtime || 0) / 3600, cells = [];
  cells.push(cell(fmtDuration(st.playtime), 'played', 'Since the map first saw them'));
  cells.push(cell(st.sessions, 'visits'));
  if (st.sessions > 1) cells.push(cell(fmtDuration(st.playtime / st.sessions), 'a visit, usually'));
  cells.push(cell(st.deaths, 'deaths'));
  if (hours >= 1) cells.push(cell(st.deaths ? (st.deaths / hours).toFixed(st.deaths / hours < 1 ? 2 : 1) : '0', 'deaths an hour'));
  if (st.deathAt) cells.push(cell(fmtDuration(Date.now() / 1000 - st.deathAt), 'since they died', 'Time since their last death'));
  if (st.closeCalls) cells.push(cell(st.closeCalls, 'close calls', 'Times under 10% health that they lived through'));
  if (st.lowHealth !== undefined) cells.push(cell(Math.max(1, Math.round(st.lowHealth)), 'least health', 'The lowest their health has been and lived'));
  cells.push(cell(fmtDist(st.distance), 'walked'));
  if (st.sailed) cells.push(cell(fmtDist(st.sailed), 'sailed', `${fmtDuration(st.sailing)} at the helm or aboard`));
  cells.push(cell(st.portalTrips ?? 0, 'portal trips'));
  cells.push(cell(km2(st.revealed), 'map revealed', 'New ground this player uncovered on the map'));
  if (st.farthest) cells.push(cell(fmtDist(st.farthest), 'farthest out', 'The farthest from the middle of the world they have been'));
  if (st.highest !== undefined && st.highest > 0) cells.push(cell(`${Math.round(st.highest)} m`, 'highest point', 'The highest they have stood, above the sea'));
  cells.push(cell((st.built || 0).toLocaleString(), 'pieces standing', 'Everything they ever built that still stands in this world, from before the map was installed too'));
  const bd = Object.keys(st.builtDays || {}).sort();
  if (bd.length >= 2) { const d = st.builtDays[bd[bd.length - 1]] - st.builtDays[bd[0]]; cells.push(cell(`${d >= 0 ? '+' : ''}${d.toLocaleString()}`, 'built this week', `Change in pieces standing since ${bd[0]}`)); }
  cells.push(cell(st.bossKills || 0, 'boss kills'));
  cells.push(cell(st.raids || 0, 'raids weathered'));
  if (st.atBase && st.playtime) cells.push(cell(`${Math.round(100 * st.atBase / st.playtime)}%`, 'time at base', 'At a base (fires, beds, workbenches around them) rather than out exploring'));
  if (st.dungeon) cells.push(cell(fmtDuration(st.dungeon), 'in dungeons', 'Crypts, caves and other places below'));
  if (st.afk) cells.push(cell(fmtDuration(st.afk), 'AFK', 'Not moving or turning for five minutes or more'));
  if (st.ping >= 0 && st.ping !== undefined) cells.push(cell(`${Math.round(st.ping)} ms`, 'usual ping', 'Their ping to the server, averaged'));
  let html = `<div class="pc-stats">${cells.join('')}</div>`;
  // time in each biome, as one bar
  const bt = Object.entries(st.biomeTime || {}).filter(([, s]) => s > 0).sort((a, b) => b[1] - a[1]);
  const tot = bt.reduce((a, [, s]) => a + s, 0);
  if (tot > 60) {
    html += `<div class="pc-sub">Time in each biome</div><div class="pc-biomebar">${bt.map(([b, s]) => `<i style="flex:${s};background:${BIOME_COL[b] || '#888'}" title="${escape(b)}: ${fmtDuration(s)} (${Math.round(100 * s / tot)}%)"></i>`).join('')}</div>
      <div class="pc-biomekey">${bt.slice(0, 5).map(([b, s]) => `<span><i style="background:${BIOME_COL[b] || '#888'}"></i>${escape(b)} ${Math.round(100 * s / tot)}%</span>`).join('')}</div>`;
  }
  const bdth = Object.entries(st.biomeDeaths || {}).sort((a, b) => b[1] - a[1]);
  if (bdth.length) html += `<div class="pc-first">Deaths: ${bdth.map(([b, n]) => `${escape(b)} ${n}`).join(' · ')}</div>`;
  return html;
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
    if (this.name !== player.name) this.openItem = null;
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

  hide() { this.el.hidden = true; this.name = null; this.sig = null; this.openItem = null; }

  // live update from the players feed; the portraits of who's on are fetched ahead (a few at most)
  update() {
    if (!this.preloaded) {
      this.preloaded = new Set();
      setTimeout(() => preloadPortrait(), 3000);
    }
    for (const q of (this.app.layers.players.players || []).slice(0, 12)) if (q.portrait && !this.preloaded.has(q.portrait)) { this.preloaded.add(q.portrait); setTimeout(() => preloadPortrait(q.portrait), 4000); }
    if (this.name !== null) this.refresh();
  }

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
      const state = [p.dead ? 'dead' : '', p.inBed ? 'sleeping' : '', p.atBase ? 'at base' : '', p.pvp ? 'PvP' : ''].filter(Boolean);
      status = `<span class="pc-on">Online</span>${since}${state.length ? ' · ' + state.join(' · ') : ''}`;
    } else status = st?.lastSeen ? `Last seen ${fmtAgo(st.lastSeen)}${st.lastBiome ? ' · ' + escape(st.lastBiome) : ''}` : 'Offline';
    const where = p ? (p.x !== undefined ? `${escape(p.biome || '')}${p.weather ? ` · ${escape(p.weather)}` : ''} · ${escape(this.app.sidebar?.whereText?.(p.x, p.z) || `${p.x}, ${p.z}`)}` : 'Position hidden') : '';
    // their connection: ping, and how the game rates the link (live only)
    const net = p && p.ping ? netHtml(p) : '';
    const conn = p ? connHtml(p, statsStore.data?.server?.gameVersion) : '';
    // what they wear: the game's icon and name, what it does (at the level they have it, where the game
    // says; else what it can be), the slot (offline: what they wore when last seen)
    const worn = p ? p.gear : st && st.gear && Object.keys(st.gear).length ? st.gear : null;
    const levels = (p ? p.levels : st?.levels) || {};
    const G = gearStats();
    let armorLo = 0, armorHi = 0;
    const items = worn ? SLOTS.filter(([k]) => worn[k]).map(([k, label]) => {
      const pf = worn[k], nm = gameName(pf), g = G?.[pf], lv = levels[k] || 0;
      if (g?.armor && ARMOR_SLOTS.has(k)) { const [a, b] = statRange(g.armor, g, lv); armorLo += a; armorHi += b; }
      return { k, label, pf, nm, g, lv };
    }) : [];
    const gear = items.map(({ k, label, pf, nm, g, lv }) => {
      const stars = lv > 0 ? `<i class="pc-lv" title="Level ${lv}${g?.q ? ` of ${g.q}` : ''}">${'★'.repeat(Math.min(lv, 10))}</i>` : '';
      const line = g ? gearLine(g, lv) : '';
      return `<div class="pc-item${this.openItem === k ? ' open' : ''}" data-k="${k}" title="${escape(nm)}"><img src="icons/item/${encodeURIComponent(pf.replace(/[^A-Za-z0-9_-]/g, ''))}.png" alt="" loading="lazy" onerror="this.style.visibility='hidden'"><div><b>${escape(nm)}${stars}</b><span>${line || label}</span></div></div>`;
    }).join('');
    const open = items.find((it) => it.k === this.openItem);
    const detail = open ? `<div class="pc-detail">${gearDetail(open)}</div>` : '';
    const armor = armorHi > 0 ? `Armor ${armorLo === armorHi ? Math.round(armorLo) : `${Math.round(armorLo)}–${Math.round(armorHi)}`}` : '';
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
      // when in the day they're usually on (their recent sessions, this browser's clock)
      const hours = new Array(24).fill(0);
      for (const [a, b] of st.recent || []) for (let t = a; t < b; t += 600) hours[new Date(t * 1000).getHours()] += 10;
      const busiest = Math.max(...hours);
      if (busiest >= 30) {
        const on = hours.map((m) => m >= busiest * 0.35);
        let best = [0, 0];   // the longest run of busy hours (wrapping past midnight)
        for (let h = 0; h < 24; h++) { let n = 0; while (n < 24 && on[(h + n) % 24]) n++; if (n > best[1]) best = [h, n]; }
        const hh = (h) => new Date(2000, 0, 1, h % 24).toLocaleTimeString([], { hour: 'numeric' });
        activity += `<div class="pc-hours" title="When their recent sessions were">${hours.map((m) => `<i style="opacity:${m ? 0.25 + 0.75 * m / busiest : 0.08}"></i>`).join('')}</div>
          <div class="pc-first">Usually on ${best[1] >= 24 ? 'around the clock' : `${hh(best[0])} – ${hh(best[0] + best[1])}`}</div>`;
      }
    }
    // badges: top of a leaderboard
    let badges = '';
    const all = (statsStore.data?.players || []);
    if (st && all.length > 1) {
      const BOARDS = [['playtime', 'Never logs off'], ['distance', 'Wanderer'], ['built', 'Master builder'], ['revealed', 'Pathfinder'], ['bossKills', 'Boss slayer'], ['raids', 'Raid veteran'], ['finds', 'Explorer'], ['portalTrips', 'Portal hopper'], ['sailed', 'Sea dog'], ['closeCalls', 'Lives on the edge']];
      badges = BOARDS.filter(([k]) => (st[k] || 0) > 0 && all.every((o) => o === st || (o[k] || 0) < st[k])).map(([, t]) => `<span class="pc-badge">${t}</span>`).join('');
    }
    const life = st ? statsHtml(st) : '';
    // their places: the bases they built most of, their portals, claimed beds, tombstones, wards,
    // signs they wrote (from the markers), and where they last died: all of them, in a list that scrolls
    const places = [];
    for (const set of this.app.layers.markers?.sets || []) {
      const kind = { bases: 'house', portals: 'portal', beds: 'bed', tombstones: 'tombstone', wards: 'ward', signs: 'sign' }[set.id];
      if (!kind) continue;
      for (const m of set.markers || []) if ((kind === 'house' || kind === 'portal' ? m.by : kind === 'sign' ? m.author : m.owner) === name) places.push({ m, kind });
    }
    if (st?.deathAt && (st.deathX || st.deathZ)) places.push({ m: { x: st.deathX, z: st.deathZ }, kind: 'death' });
    // the same kind close together (a base's wards) is one row; bases, last death, portals, beds, tombstones, wards, signs
    const label = { house: 'Base', portal: 'Portal', bed: 'Bed', tombstone: 'Tombstone', ward: 'Ward', sign: 'Sign', death: 'Last died' }, order = { house: 0, death: 1, portal: 2, bed: 3, tombstone: 4, ward: 5, sign: 6 }, groups = [];
    for (const pl of places) {
      const g = pl.kind !== 'house' && pl.kind !== 'portal' && pl.kind !== 'sign' && groups.find((q) => q.kind === pl.kind && Math.hypot(q.m.x - pl.m.x, q.m.z - pl.m.z) < 150);
      if (g) g.n++; else groups.push({ ...pl, n: 1 });
    }
    groups.sort((a, b) => order[a.kind] - order[b.kind] || (b.m.pieces || 0) - (a.m.pieces || 0));
    const what = ({ m, kind, n }) => kind === 'house' ? `${escape(m.label || 'Base')} <em>${(m.pieces || 0).toLocaleString()} pieces</em>`
      : kind === 'portal' ? `Portal <em>${escape(m.tag || 'untagged')}</em>`
      : kind === 'sign' ? `Sign <em>${escape(m.label || '')}</em>`
      : kind === 'death' ? `Last died <em>${fmtAgo(new Date(st.deathAt * 1000).toISOString())}${st.deathBiome ? ' · ' + escape(st.deathBiome) : ''}</em>`
      : `${label[kind]}${n > 1 ? ` ×${n}` : ''}${kind === 'ward' && n === 1 && m.on === false ? ' (off)' : ''}`;
    const placeRows = groups.map((g, i) => `<div class="pc-place" data-i="${i}" title="Show it on the map">${iconSvg(g.kind === 'death' ? 'tombstone' : g.kind)}<span>${what(g)}</span><small>${escape(this.app.sidebar?.whereText?.(g.m.x, g.m.z) || `${Math.round(g.m.x)}, ${Math.round(g.m.z)}`)}</small></div>`).join('');
    const following = p && PL.following === p.id;
    const there = p && p.x !== undefined ? [p.x, p.z] : st && st.lastX !== undefined ? [st.lastX, st.lastZ] : null;
    const portrait = p?.portrait || st?.portrait || '';
    // the same card as a second ago but for the bars, the place and the ping: just move those
    // (rebuilding it all every update remade its pictures and could hitch the map)
    const barsHtml = (p ? bar('Health', p.health, p.maxHealth, 'hp-health') : '') + (p && p.stamina !== undefined ? bar('Stamina', p.stamina, Math.max(p.stamina, 100), 'hp-stamina') : '') + (p && p.eitr !== undefined && p.eitr > 0 ? bar('Eitr', p.eitr, Math.max(p.eitr, 100), 'hp-eitr') : '');
    const sig = [name, status, !!where, !!net, !!conn, gear, detail, armor, activity, life, placeRows, badges, following, !!there, portrait].join('|');
    if (sig === this.sig && !this.el.hidden) {
      const b = this.el.querySelector('.pc-bars'), w = this.el.querySelector('.pc-where'), n = this.el.querySelector('.pc-net');
      if (b) b.innerHTML = barsHtml;
      if (w) w.innerHTML = where;
      if (n) n.outerHTML = net;
      const cn = this.el.querySelector('.pc-conn');
      if (cn) cn.innerHTML = conn;
      return;
    }
    this.sig = sig;
    const scroll = this.el.querySelector('.pc-places')?.scrollTop || 0;
    this.el.innerHTML = `
      <button class="icon-btn small pc-close" data-act="close" title="Close"><svg><use href="#i-close"/></svg></button>
      <div class="pc-hero" style="--pc:${col}">
        <div class="pc-portrait"><span class="pc-initial">${escape((name || '?').slice(0, 1).toUpperCase())}</span></div>
        <div class="pc-who">
          <div class="name">${escape(name)}</div>
          <div class="meta">${status}</div>${where ? `<div class="meta pc-where">${where}</div>` : ''}
          <div class="pc-bars">${barsHtml}</div>
          ${net}
          ${badges ? `<div class="pc-badges">${badges}</div>` : ''}
        </div>
      </div>
      ${gear ? `<div class="pc-section">${p ? 'Wearing' : 'Last seen wearing'}${armor ? ` <small>${armor}</small>` : ''}</div><div class="pc-items${p ? '' : ' was'}">${gear}</div>${detail}` : ''}
      ${activity}
      ${life ? `<div class="pc-section">Stats</div>${life}` : ''}
      ${conn ? `<div class="pc-section">Connection</div><div class="pc-conn">${conn}</div>` : ''}
      ${placeRows ? `<div class="pc-section">Their places <small>${groups.length}</small></div><div class="pc-places">${placeRows}</div>` : ''}
      <div class="pc-actions">
        ${p ? `<button class="btn small ${following ? 'on' : ''}" data-act="follow" ${p.x === undefined ? 'disabled' : ''}>${following ? 'Unfollow' : 'Follow'}</button>` : ''}
        <button class="btn small" data-act="goto" ${there ? '' : 'disabled'} title="${p ? '' : 'Where they were last seen'}">${p ? 'Go to' : 'Last seen'}</button>
        <button class="btn small" data-act="link" title="Copy a link that opens this card">Link</button>
      </div>`;
    const pl = this.el.querySelector('.pc-places');
    if (pl) pl.scrollTop = scroll;
    const box = this.el.querySelector('.pc-portrait');
    if (portrait) showPortrait(box, portrait).then((ok) => { if (ok) box.classList.add('drawn'); });
    this.el.querySelector('[data-act=close]').addEventListener('click', () => this.hide());
    this.el.querySelector('[data-act=follow]')?.addEventListener('click', () => { PL.follow(following ? null : p.id); this.refresh(); });
    this.el.querySelector('[data-act=goto]').addEventListener('click', () => there && this.app.goTo(there[0], there[1], Math.max(this.app.mode === '2d' ? this.app.map.getZoom() : 7, 7)));
    this.el.querySelector('[data-act=link]').addEventListener('click', async () => {
      const url = `${location.origin}${location.pathname}?player=${encodeURIComponent(name)}`;
      try { await navigator.clipboard.writeText(url); this.app.toast('Link copied'); } catch { this.app.toast(url); }
    });
    this.el.querySelectorAll('.pc-item').forEach((r) => r.addEventListener('click', () => { this.openItem = this.openItem === r.dataset.k ? null : r.dataset.k; this.refresh(); }));
    this.el.querySelectorAll('.pc-place').forEach((r) => r.addEventListener('click', () => { const { m } = groups[+r.dataset.i]; this.app.goTo(m.x, m.z, Math.max(this.app.mode === '2d' ? this.app.map.getZoom() : 7, 7)); }));
  }
}
