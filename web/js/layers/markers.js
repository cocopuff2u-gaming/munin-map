// Marker sets (locations, portals, tombstones, vehicles, custom) and chat
// pins, each as a toggleable Leaflet layer group. Portals sharing a tag are
// joined by a dashed line.

import { toLatLng, fromLatLng } from '../crs.js';
import { markers as store } from '../data.js';
import { iconSvg, colors } from '../icons.js';
import { getJSON, on } from '../net.js';
import { MarkerCanvas } from './markercanvas.js';
import { layerState } from '../layerstate.js';

export const LOCATION_CATS = ['spawn', 'boss', 'trader', 'dungeon', 'camp', 'village', 'ruin', 'runestone', 'poi'];

export function makeIcon(name, color, label, cls = 'mk') {
  return L.divIcon({
    className: '',
    html: `<div class="${cls}">${iconSvg(name, color)}${label ? `<div class="lbl">${escape(label)}</div>` : ''}</div>`,
    iconSize: cls === 'mk-pin' ? [18, 18] : [26, 26],
    iconAnchor: cls === 'mk-pin' ? [9, 18] : [13, 13],
    tooltipAnchor: [0, -12],
  });
}

// this browser's id: made up once, kept; the server keys web pins by it so only this browser can
// remove them. It is the "pin key": pasted into another browser, that one can remove them too
export function setClientId(id) {
  id = String(id || '').replace(/[^A-Za-z0-9_-]/g, '').slice(0, 40);
  if (!id) return false;
  try { localStorage.setItem('munin-client', id); } catch (e) { return false; }
  return true;
}
export function clientId() {
  let id = null;
  try { id = localStorage.getItem('munin-client'); } catch (e) { /* storage blocked */ }
  if (!id) {
    id = Array.from(crypto.getRandomValues(new Uint8Array(12)), (b) => b.toString(36).padStart(2, '0')).join('').slice(0, 20);
    try { localStorage.setItem('munin-client', id); } catch (e) { /* fine, this visit only */ }
  }
  return id;
}

export const PIN_TYPES = ['dot', 'fire', 'mine', 'house', 'cave'];

// which of the game's map icons (by location name or pin type, see World/MapIcons) stands for ours
const GAME_ICON = {
  spawn: 'StartTemple', tombstone: 'pin:Death', base: 'pin:Icon1', boss: 'pin:Boss',   // portals keep their blue icon
  trader: 'Vendor_BlackForest', hildir: 'Hildir_camp', bogwitch: 'BogWitch_Camp',
  hildir1: 'pin:Hildir1', hildir2: 'pin:Hildir2', hildir3: 'pin:Hildir3',   // Hildir's sisters' lairs
  invasion: 'pin:RandomEvent',
  fire: 'pin:Icon0', house: 'pin:Icon1', mine: 'pin:Icon2', dot: 'pin:Icon3', cave: 'pin:Icon4', pin: 'pin:Icon3', bed: 'pin:Bed',
};

// names in the order people expect: Beef 3 before Beef 14, case ignored
const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });
export const byName = (a, b) => collator.compare(a || '', b || '');
export function escape(s) { return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }

export class MarkerLayers {
  constructor(map, opts = {}) {
    this.map = map;
    this.visible = new Map(layerState.sets);     // set id -> bool; the sets that start off are listed in layerstate.js
    this.catVisible = new Map(LOCATION_CATS.map((c) => [c, c !== 'poi']));
    this.sets = [];
    this.pins = new Map();        // pin id -> pin
    this.listeners = new Set();
    // everything is painted on one canvas (markercanvas.js), not a DOM element per marker
    this.canvas = new MarkerCanvas(() => this.drawList(), { gl: !!opts.gl }).addTo(map);
    layerState.onChange((k) => { if (k === 'labels' || k === 'iconScale' || k === 'iconDots') this.canvas.draw(); });
    store.onChange((sets) => this.render(sets));
    on('pin', (f) => this.addPin(f));
    on('rmpin', (f) => this.removePin(f.id));
    this.loadPins();
    // the game's own map icons, when the server could get them (World/MapIcons)
    this.gameIcons = {};
    getJSON('data/icons.json').then((m) => { this.gameIcons = m.icons || {}; this.list = null; this.canvas.draw(); for (const fn of this.listeners) fn(this.sets); }).catch(() => {});   // (lists show the icons too)
  }

  // our icon name -> the game's map icon URL, where the game has one for it
  gameIcon(name) {
    const key = GAME_ICON[name], sprite = key && this.gameIcons[key];
    return sprite ? `icons/game/${sprite}.png` : undefined;
  }

  onChange(fn) { this.listeners.add(fn); }
  onPins(fn) { (this.pinListeners ??= new Set()).add(fn); }
  pinList() { return [...this.pins.values()]; }
  emitPins() { for (const fn of this.pinListeners || []) fn(this.pinList()); }

  render(sets) {
    this.storeSets = sets;
    // invasions (layers/raid.js draws them on the map, pulsing like in game) close the list, for finding them
    const inv = window.app && window.app.layers.raid && window.app.layers.raid.invasionMarkers();
    if (inv && inv.length) sets = [...sets, { id: 'invasions', label: 'Invasions', mapless: true, markers: inv }];
    // the spawn point (the server's world_start_pos) heads the list as its own set, like the seed maps do
    const sp = window.app && window.app.spawn;
    if (sp && !sets.some((s) => s.id === 'spawn'))
      sets = [{ id: 'spawn', label: 'Spawn', markers: [{ x: Math.round(sp.x), z: Math.round(sp.z), label: 'Spawn', cat: 'spawn', icon: 'spawn' }] }, ...sets];
    this.sets = sets;
    this.list = null;
    for (const fn of this.listeners) fn(sets);
    this.canvas.draw();
  }

  // what the canvas draws, rebuilt only when sets, pins or toggles change
  drawList() {
    if (this.list) return this.list;
    const items = [], lines = [];
    if (this.visible.get('pins') !== false)
      for (const p of this.pins.values()) {
        const icon = PIN_TYPES.includes(p.type) ? p.type : 'pin';
        items.push({ x: p.x, z: p.z, icon, color: colors[icon], img: this.gameIcon(icon), label: p.text, pin: true, open: (ll) => this.openPin(p, ll) });
      }
    const byTag = new Map();
    for (const set of this.sets) {
      if (this.visible.get(set.id) === false || set.mapless) continue;
      for (const m of set.markers || []) {
        const cat = m.cat || 'custom';
        if (set.id === 'locations' && this.catVisible.get(cat) === false) continue;
        const editable = m.cat === 'base' && window.app?.config?.web_edit_bases !== false;
        const label = m.label;
        // a table pin as the game draws a shared one (markercanvas.js gamePinImage): its grey name too
        const crossSprite = this.gameIcons['pin:Checked'] ? `icons/game/${this.gameIcons['pin:Checked']}.png` : true;
        const gamePin = cat === 'tablepin' ? { checked: m.checked ? crossSprite : false } : undefined;
        items.push({ x: m.x, z: m.z, icon: m.icon || cat, color: cat === 'ward' && !m.on ? colors.wardoff : colors[m.icon] || colors[cat] || '#9aa5b5', img: this.gameIcon(m.icon || cat), label, gamePin,
          area: cat === 'ward' && m.r ? { r: m.r, on: !!m.on } : undefined,   // the ground a ward protects (MarkerGL draws the ring)
          labelColor: gamePin ? '#d6d6d6' : undefined,
          always: cat === 'spawn' || cat === 'boss' || cat === 'trader' || cat === 'miniboss',   // their labels always show (valheim.tools does the same): never in a tug of war
          open: (ll) => L.popup({ offset: [0, -8] }).setLatLng(ll).setContent(editable ? this.basePopup(m, set) : cat === 'portal' ? this.portalPopup(m, set, byTag) : popupHtml(m, set)).openOn(this.map) });
        if (cat === 'portal' && m.tag) { if (!byTag.has(m.tag)) byTag.set(m.tag, []); byTag.get(m.tag).push(m); }
      }
    }
    for (const list of byTag.values()) for (let i = 1; i < list.length; i++) lines.push([list[0].x, list[0].z, list[i].x, list[i].z]);
    this.list = { items, lines, get labels() { return !document.body.classList.contains('no-labels'); } };
    return this.list;
  }

  setVisible(id, v) {
    this.visible.set(id, v);
    if (id === 'invasions' && window.app && window.app.layers.raid) window.app.layers.raid.redraw();
    this.list = null;
    this.canvas.draw();
  }

  setCategory(cat, v) { this.catVisible.set(cat, v); this.render(this.sets); }

  // Every marker with a position, for search.
  all() {
    const out = [];
    for (const set of this.sets) for (const m of set.markers || []) out.push({ kind: set.label, label: m.label, x: m.x, z: m.z, icon: m.icon || m.cat });
    for (const p of this.pins.values()) out.push({ kind: 'Pin', label: p.text || p.name, x: p.x, z: p.z, icon: p.type });
    return out;
  }

  async loadPins() {
    try {
      // the server marks this browser's own pins "mine" (it never sends who owns the others)
      const pins = await getJSON('data/pins.json?client=' + encodeURIComponent(clientId()));
      this.pins.clear();
      for (const p of pins) this.addPin(p);
    } catch (e) { console.warn('pins', e); }
  }

  addPin(p) {
    if (this.pins.get(p.id)?.mine) p.mine = true;   // the live copy doesn't say whose it is
    this.pins.set(p.id, p);
    this.list = null;
    this.canvas.draw();
    this.emitPins();
  }

  removePin(id) {
    if (this.pins.delete(id)) { this.list = null; this.canvas.draw(); this.emitPins(); }
  }

  openPin(p, ll) {
    let key = '';
    try { key = localStorage.getItem('munin-admin-key') || ''; } catch (e) { /* no storage */ }
    const mine = !!p.mine;
    const el = document.createElement('div');
    el.innerHTML = `<b>${escape(p.text || 'Pin')}</b><small>by ${escape(p.name)} · ${p.x}, ${p.z}</small>` +
      (mine || key ? `<div class="pin-actions"><button class="btn small" type="button">Remove pin</button></div><small class="err" hidden></small>` : '');
    el.querySelector('button')?.addEventListener('click', async () => {
      const err = el.querySelector('.err');
      const headers = { 'Content-Type': 'application/json', 'X-Munin-Client': clientId() };
      if (key) headers['X-Munin-Token'] = key;
      try {
        // the server's web stack refuses a POST with an empty body (411), so send {}
        const r = await fetch('api/unpin?id=' + encodeURIComponent(p.id), { method: 'POST', headers, body: '{}' });
        if (!r.ok) { const j = await r.json().catch(() => ({})); throw new Error(j.error || r.status); }
        this.removePin(p.id);
        this.map.closePopup();
      } catch (e) { err.textContent = 'Could not remove: ' + e.message; err.hidden = false; }
    });
    L.popup({ offset: [0, -16] }).setLatLng(ll).setContent(el).openOn(this.map);
  }

  // a portal's popup, with a button to each other end of its tag (byTag: tag -> portals, filled
  // while the list is built, so it is complete by the time anyone clicks)
  portalPopup(m, set, byTag) {
    const el = document.createElement('div');
    el.innerHTML = popupHtml(m, set);
    const others = (m.tag && byTag.get(m.tag) || []).filter((o) => o !== m);
    if (!others.length) return el;
    const row = document.createElement('div');
    row.className = 'pin-actions';
    for (const o of others) {
      const far = Math.hypot(o.x - m.x, o.z - m.z);
      const b = document.createElement('button');
      b.className = 'btn small'; b.type = 'button';
      b.textContent = `Go to the other end (${far < 1000 ? Math.round(far) + ' m' : (far / 1000).toFixed(1) + ' km'})`;
      b.addEventListener('click', () => {
        // there first, then its popup: opened before a glide, the popup was left off-screen
        window.app?.goTo(o.x, o.z, Math.max(this.map.getZoom(), 6), true);
        L.popup({ offset: [0, -8], autoPan: false }).setLatLng(toLatLng(o.x, o.z)).setContent(this.portalPopup(o, set, byTag)).openOn(this.map);
      });
      row.append(b);
    }
    el.append(row);
    return el;
  }

  // popup for an auto-detected base: rename it, hide it, or put the name back
  basePopup(m, set) {
    const el = document.createElement('div');
    el.className = 'base-popup';
    el.innerHTML = popupHtml(m, set) + `<form class="pin-form base-edit"><div class="row">
      <input name="label" maxlength="24" placeholder="Name this base" value="${escape(m.renamed ? m.label : '')}" autocomplete="off">
      <button class="btn small" type="submit">Rename</button></div>
      <div class="row"><button class="btn small" type="button" data-act="hide">Hide this base</button>
      ${m.renamed ? '<button class="btn small" type="button" data-act="reset">Auto name</button>' : ''}</div>
      <small class="err" hidden></small></form>`;
    const form = el.querySelector('form'), err = el.querySelector('.err');
    const send = async (body) => {
      try {
        const r = await fetch('api/base', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Munin-Client': clientId() }, body: JSON.stringify(Object.assign({ x: m.x, z: m.z }, body)) });
        if (!r.ok) { const j = await r.json().catch(() => ({})); throw new Error(j.error || r.status); }
        this.map.closePopup();
      } catch (e) { err.textContent = 'Could not change base: ' + e.message; err.hidden = false; }
    };
    form.addEventListener('submit', (ev) => { ev.preventDefault(); const v = String(new FormData(form).get('label') || '').trim(); if (v) send({ label: v }); });
    el.querySelector('[data-act=hide]').addEventListener('click', () => send({ hidden: true }));
    el.querySelector('[data-act=reset]')?.addEventListener('click', () => send({}));
    return el;
  }

  // right click / long press on the map: a small form, then POST /api/pin
  openPinEditor(latlng) {
    const { x, z } = fromLatLng(latlng);
    let name = '';
    try { name = localStorage.getItem('munin-pin-name') || ''; } catch (e) { /* no storage */ }
    const el = document.createElement('form');
    el.className = 'pin-form';
    el.innerHTML = `<b>New pin</b><small>${Math.round(x)}, ${Math.round(z)}</small>
      <div class="row"><select name="type">${PIN_TYPES.map((t) => `<option value="${t}">${t}</option>`).join('')}</select>
      <input name="text" maxlength="20" placeholder="Label (letters, numbers)" autocomplete="off"></div>
      <div class="row"><input name="name" maxlength="16" placeholder="Your name" value="${escape(name)}" autocomplete="off"><button class="btn small" type="submit">Add pin</button>${this.onPing ? '<button class="btn small" type="button" data-act="ping" title="Ping this spot in game">Ping</button>' : ''}</div>
      <small class="err" hidden></small>
      <details class="pin-key"><summary>Pin key</summary><small>Your pins can be removed from this browser only. To remove them from another one, copy this key there.</small>
      <div class="row"><input name="key" value="${escape(clientId())}" autocomplete="off" spellcheck="false"><button class="btn small" type="button" data-act="key">Use key</button></div></details>`;
    const popup = L.popup({ closeButton: true, autoPan: true, className: 'pin-form-popup' }).setLatLng(latlng).setContent(el).openOn(this.map);
    setTimeout(() => el.querySelector('[name=text]').focus(), 50);
    el.querySelector('[data-act=ping]')?.addEventListener('click', () => {
      const who = String(new FormData(el).get('name') || '').trim();
      if (!who) { el.querySelector('[name=name]').focus(); return; }
      try { localStorage.setItem('munin-pin-name', who); } catch (e) { /* no storage */ }
      this.onPing(latlng, who);
      this.map.closePopup(popup);
    });
    el.querySelector('[data-act=key]').addEventListener('click', () => {
      const err = el.querySelector('.err'), key = el.querySelector('[name=key]');
      if (key.value.trim() === clientId()) return;
      if (!setClientId(key.value.trim())) { err.textContent = 'That key can\'t be used here'; err.hidden = false; return; }
      key.value = clientId();
      this.loadPins();
      this.map.closePopup(popup);
    });
    el.addEventListener('submit', async (ev) => {
      ev.preventDefault();
      const fd = new FormData(el);
      const who = String(fd.get('name') || '').trim();
      try { localStorage.setItem('munin-pin-name', who); } catch (e) { /* no storage */ }
      const err = el.querySelector('.err');
      try {
        const r = await fetch('api/pin', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Munin-Client': clientId() },
          body: JSON.stringify({ x: Math.round(x * 10) / 10, z: Math.round(z * 10) / 10, type: fd.get('type'), text: fd.get('text'), name: who, client: clientId() }) });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) throw new Error(j.error || r.status);
        // the live pin may have come in already, or still be on its way
        const placed = this.pins.get(j.id);
        if (placed) placed.mine = true; else if (j.id) this.pins.set(j.id, { id: j.id, mine: true, type: fd.get('type'), name: who, x: Math.round(x * 10) / 10, z: Math.round(z * 10) / 10, text: fd.get('text') });
        this.list = null; this.canvas.draw(); this.emitPins();
        this.map.closePopup(popup);
      } catch (e) { err.textContent = 'Could not add pin: ' + e.message; err.hidden = false; }
    });
  }
}

// what the server knows of a dungeon's inside (World/Dungeons.cs)
function dungeonHtml(m) {
  const d = m.inside;
  if (!d) return '<small>Nobody has been near it yet</small>';
  const rows = [];
  const row = (label, value, done) => rows.push(`<tr${done ? ' class="done"' : ''}><td>${label}</td><td>${value}</td></tr>`);
  if (d.rooms) row('Rooms', d.rooms);
  if (d.chests) row('Chests emptied', `${d.emptied} of ${d.chests}`, d.emptied === d.chests);
  if (d.gates) row(d.gates > 1 ? 'Locked gates opened' : 'Locked gate', d.gates > 1 ? `${d.opened} of ${d.gates}` : d.opened ? 'Open' : 'Shut', d.opened === d.gates);
  if (d.monsters) row('Monsters inside', d.monsters);   // (none spawn till someone comes near: 0 says nothing)
  if (d.graves) row('Graves inside', d.graves);
  // what is still there to pick up or mine, by the game's names (taken ones are gone)
  if (d.left && d.left.length) {
    rows.push('<tr class="sub"><td colspan="2">Still inside</td></tr>');
    for (const [name, n] of d.left) row(escape(name), n);
  }
  const state = d.visited ? (d.chests && d.emptied === d.chests ? 'Cleared out' : 'Someone has been inside') : 'No sign anyone has been inside';
  return `<div class="dungeon-state${d.visited ? ' visited' : ''}">${state}</div><table class="dungeon-info">${rows.join('')}</table>`;
}

function popupHtml(m, set) {
  let extra = '';
  if (m.cat === 'dungeon') extra = dungeonHtml(m);
  else if (m.cat === 'portal') extra = `<small>Portal tag: ${escape(m.tag || '(none)')}</small>`;
  else if (m.cat === 'base') extra = `<small>${m.pieces} pieces</small>`;
  else if (m.cat === 'tablepin') extra = m.checked ? '<small>Crossed out on the map</small>' : '';
  else if (m.cat === 'sign') extra = m.author ? `<small>Written by ${escape(m.author)}</small>` : '';
  else if (m.cat === 'bed') extra = '<small>Claimed bed (a player keeps old beds too: their spawn is the one they slept in last)</small>';
  else if (m.cat === 'runestone') extra = m.text ? `<div class="runetext">${escape(m.text)}</div>` : '';
  else if (m.cat === 'ward') extra = `<small>${m.on ? 'On' : 'Off'} · protects ${Math.round(m.r)} m around</small>` + (m.permitted && m.permitted.length ? `<br><small>Can build here: ${m.permitted.map(escape).join(', ')}</small>` : '');
  else if (m.cat === 'tombstone') extra = `<small>${m.when ? new Date(m.when / 10000 - 62135596800000).toLocaleString() : ''}</small>`;
  else if (m.prefab) extra = `<small>${escape(m.prefab)}${m.placed === false ? ' · not yet generated' : ''}</small>`;
  else if (m.description) extra = `<small>${escape(m.description)}</small>`;
  return `<b>${escape(m.label || (m.cat === 'tablepin' ? 'Map pin' : ''))}</b><small>${escape(set.label)} · ${m.x}, ${m.z}</small>${extra ? '<br>' + extra : ''}`;
}
