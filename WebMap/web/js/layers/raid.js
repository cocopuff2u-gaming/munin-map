// Raids and invasions, drawn the way the game's map draws them (Minimap.UpdateEventPin and
// UpdatePersistentEventPins): the see-through red area sprite, and the red "!" at double pin size,
// pulsing, with the event's name under it. The game's own sprites when the server could get them
// (World/MapIcons), look-alikes otherwise. Both are drawn over the fog, as in game.
//
// Raid: the attack on a base going on now; its area is 90% of the raid's range across (the game:
// range * 2 * 0.9). The server sends it in the hello frame (a page opened mid-raid) and as "raid"
// frames when one starts or ends (data null).
// Invasions (PersistentEventSystem): sites that stay for an hour or two, area its radius * 2 across.
// The hello frame has them, and "invasions" frames the whole list whenever it changes.
import { on } from '../net.js';
import { toLatLng } from '../crs.js';
import { escape } from './markers.js';

export class RaidLayer {
  constructor(map, markers) {
    this.map = map;
    this.markers = markers;   // has the game's icon names (gameIcons)
    this.raidParts = [];
    this.invasionParts = [];
    on('hello', (f) => { this.set(f.raid); this.setInvasions(f.invasions); });
    on('raid', (f) => this.set(f.data));
    on('invasions', (f) => this.setInvasions(f.data));
    // the game's raid sprites fetched and decoded ahead, so the first raid or invasion shows at once
    if (markers && markers.onChange) markers.onChange(() => this.prewarm());
  }

  prewarm() {
    if (this.warm) return;
    const srcs = ['pin:EventArea', 'pin:RandomEvent', 'pin:Shout'].map((k) => this.sprite(k)).filter(Boolean);
    if (!srcs.length) return;
    this.warm = srcs.map((src) => { const i = new Image(); i.src = src; i.decode && i.decode().catch(() => {}); return i; });
  }

  sprite(key) {
    const s = this.markers && this.markers.gameIcons && this.markers.gameIcons[key];
    return s ? `icons/game/${s}.png` : null;
  }

  // the game's icon list may still be loading: draw again with its sprites once it is here
  later() {
    if (this.sprite('pin:RandomEvent') || this.retried) return;
    this.retried = true;
    setTimeout(() => { this.set(this.lastRaid); this.redraw(); }, 2000);
  }

  set(r) {
    for (const p of this.raidParts) p.remove();
    this.lastRaid = r;
    this.raidParts = r && r.x !== undefined ? this.draw(r.x, r.z, r.r * 0.9, r.name || 'Raid', () => this.raidHtml(r)) : [];
  }

  setInvasions(list) {
    const at = performance.now();
    this.lastInvasions = (list || []).map((v) => Object.assign({ at }, v));   // at: when "left" was counted
    this.redraw();
    if (this.markers && this.markers.storeSets) this.markers.render(this.markers.storeSets);   // the Markers list has them too
  }

  redraw() {
    for (const p of this.invasionParts) p.remove();
    const shown = !this.markers || this.markers.visible.get('invasions') !== false;
    this.invasionParts = shown ? (this.lastInvasions || []).flatMap((v) => this.draw(v.x, v.z, v.r, v.name || 'Invasion', () => this.invasionHtml(v))) : [];
  }

  // for the Markers list (markers.js): one row per invasion
  invasionMarkers() {
    return (this.lastInvasions || []).map((v) => ({ x: Math.round(v.x), z: Math.round(v.z), label: v.name || 'Invasion', cat: 'invasion', icon: 'invasion', invasion: v }));
  }

  // seconds to go, counted down from when the server said
  static left(v) { return v.left === undefined ? null : Math.max(0, v.left - (performance.now() - v.at) / 1000); }

  static duration(sec) {
    const m = Math.round(sec / 60);
    return m < 1 ? 'under a minute' : m < 60 ? `${m} min` : `${Math.floor(m / 60)} h ${m % 60 ? (m % 60) + ' min' : ''}`.trim();
  }

  // the players near one (live, from the players feed)
  near(x, z, r) {
    const ps = (window.app && window.app.layers.players && window.app.layers.players.players) || [];
    return ps.filter((p) => p.x !== undefined && Math.hypot(p.x - x, p.z - z) <= r + 150).map((p) => p.name);
  }

  invasionHtml(v) {
    const left = RaidLayer.left(v), near = this.near(v.x, v.z, v.r);
    return `<div class="raid-pop"><b>${escape(v.name || 'Invasion')}</b>
      <table class="dungeon-info">
        <tr><td>Ends</td><td>${left === null ? 'when it is cleared' : left < 1 ? 'any moment' : 'in ' + RaidLayer.duration(left)}</td></tr>
        <tr><td>Area</td><td>${Math.round(v.r * 2)} m across</td></tr>
        <tr><td>Near it</td><td>${near.length ? near.map(escape).join(', ') : 'nobody'}</td></tr>
      </table><div class="meta">${Math.round(v.x)}, ${Math.round(v.z)}</div></div>`;
  }

  raidHtml(r) {
    const near = this.near(r.x, r.z, r.r);
    return `<div class="raid-pop"><b>Raid</b><div>${escape(r.name || '')}</div>
      <table class="dungeon-info"><tr><td>Near it</td><td>${near.length ? near.map(escape).join(', ') : 'nobody'}</td></tr></table>
      <div class="meta">${Math.round(r.x)}, ${Math.round(r.z)}</div></div>`;
  }

  // the popup of an invasion in the Markers list
  openInvasion(m) {
    const v = m.invasion;
    if (v) L.popup({ offset: [0, -24] }).setLatLng(toLatLng(v.x, v.z)).setContent(this.invasionHtml(v)).openOn(this.map);
  }

  draw(x, z, half, name, popup) {
    this.later();
    const area = this.sprite('pin:EventArea');
    const bounds = L.latLngBounds(toLatLng(x - half, z - half), toLatLng(x + half, z + half));
    const parts = [area
      ? L.imageOverlay(area, bounds, { interactive: false, className: 'raid-area' }).addTo(this.map)
      : L.polygon(Array.from({ length: 72 }, (_, i) => toLatLng(x + Math.cos(i * Math.PI / 36) * half, z + Math.sin(i * Math.PI / 36) * half)),
        { stroke: false, fillColor: '#ff0f00', fillOpacity: 0.44, interactive: false }).addTo(this.map)];
    const bang = this.sprite('pin:RandomEvent');
    const html = `<div class="raid-pin">${bang ? `<img src="${bang}" alt="">` : '<b>!</b>'}</div><div class="raid-name">${escape(name)}</div>`;
    parts.push(L.marker(toLatLng(x, z), {
      icon: L.divIcon({ className: 'raid-icon', html, iconSize: [64, 64], iconAnchor: [32, 32] }),
      interactive: !!popup, keyboard: false, zIndexOffset: 1500,
    }).bindPopup(popup || '', { offset: [0, -24] }).addTo(this.map));
    if (!popup) parts[1].unbindPopup();
    return parts;
  }
}
