// Raids and invasions going on now, over the fog, in the game map's red (both alike, as in game):
// the area a see-through red disc with a fine edge, a pulsing red "!" in the middle, and under it the name with how long it has
// been going (or, for an invasion, how long it has left). A click anywhere on the area tells more:
// when it began, who is near, how big it is.
//
// Raid: the attack on a base going on now; its area is 90% of the raid's range across (the game:
// range * 2 * 0.9). The server sends it in the hello frame (a page opened mid-raid) and as "raid"
// frames when one starts or ends (data null).
// Invasions (PersistentEventSystem): sites that stay for an hour or two, area its radius * 2 across.
// The hello frame has them, and "invasions" frames the whole list whenever it changes.
// Both are a row in the Markers list ("Raids & invasions"), whose switch hides them on the map.
import { on } from '../net.js';
import { toLatLng } from '../crs.js';
import { escape } from './markers.js';

const RED = '#ff0f00';   // the game's event area and "!" (Minimap m_eventAreaPrefab, the RandomEvent pin)

// the area as a picture: the game's see-through red, a little deeper towards a fine edge
const areaCache = {};
function areaImage(color) {
  if (areaCache[color]) return areaCache[color];
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200"><defs><radialGradient id="g"><stop offset="0" stop-color="${color}" stop-opacity=".3"/><stop offset=".85" stop-color="${color}" stop-opacity=".38"/><stop offset="1" stop-color="${color}" stop-opacity=".48"/></radialGradient></defs>` +
    `<circle cx="100" cy="100" r="99" fill="url(#g)"/><circle cx="100" cy="100" r="98.6" fill="none" stroke="${color}" stroke-width="1.2" opacity=".8"/></svg>`;
  return (areaCache[color] = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg));
}

const ago = (sec) => (sec < 60 ? 'just now' : RaidLayer.duration(sec) + ' ago');

export class RaidLayer {
  constructor(map, markers) {
    this.map = map;
    this.markers = markers;   // has the game's icon names (gameIcons)
    this.raidParts = [];
    this.invasionParts = [];
    on('hello', (f) => { this.set(f.raid); this.setInvasions(f.invasions); });
    on('raid', (f) => this.set(f.data));
    on('invasions', (f) => this.setInvasions(f.data));
    setInterval(() => { if (this.lastRaid || this.lastInvasions?.length) this.redraw(); }, 30000);   // "12 min", "ends in 40 min" kept current
  }

  shown() { return !this.markers || this.markers.visible.get('invasions') !== false; }

  set(r) {
    for (const p of this.raidParts) p.remove();
    if (r !== this.lastRaid) { this.lastRaid = r && Object.assign({ seen: Date.now() / 1000 }, r); if (this.markers?.storeSets) this.markers.render(this.markers.storeSets); }
    r = this.lastRaid;
    this.raidParts = r && r.x !== undefined && this.shown() ? this.draw(r.x, r.z, r.r * 0.9, 'Raid', RED, RaidLayer.raidStatus(r), () => this.raidHtml(r)) : [];
  }

  setInvasions(list) {
    const at = performance.now();
    this.lastInvasions = (list || []).map((v) => Object.assign({ at }, v));   // at: when "left" was counted
    this.redraw();
    if (this.markers && this.markers.storeSets) this.markers.render(this.markers.storeSets);   // the Markers list has them too
  }

  redraw() {
    for (const p of this.invasionParts) p.remove();
    this.invasionParts = this.shown() ? (this.lastInvasions || []).flatMap((v) => this.draw(v.x, v.z, v.r, v.name || 'Invasion', RED, RaidLayer.invasionStatus(v), () => this.invasionHtml(v))) : [];
    if (this.lastRaid !== undefined && !this.redrawing) { this.redrawing = true; this.set(this.lastRaid); this.redrawing = false; }   // the switch hides both
  }

  // for the Markers list (markers.js): the raid and each invasion, a row each
  invasionMarkers() {
    const r = this.lastRaid, rows = r && r.x !== undefined ? [{ x: Math.round(r.x), z: Math.round(r.z), label: 'Raid: ' + (r.name || ''), cat: 'invasion', icon: 'invasion', invasion: r, raid: true }] : [];
    return rows.concat((this.lastInvasions || []).map((v) => ({ x: Math.round(v.x), z: Math.round(v.z), label: v.name || 'Invasion', cat: 'invasion', icon: 'invasion', invasion: v })));
  }

  // seconds it has been going (the raid from when it began, an invasion from its age when sent)
  static raidAge(r) { return Math.max(0, Date.now() / 1000 - (r.since || r.seen)); }
  static invasionAge(v) { return v.age === undefined ? null : v.age + (performance.now() - v.at) / 1000; }
  static raidStatus(r) { const a = RaidLayer.raidAge(r); return a < 60 ? 'just began' : RaidLayer.duration(a); }
  static invasionStatus(v) { const left = RaidLayer.left(v); return left === null ? 'until cleared' : left < 60 ? 'ending' : 'ends in ' + RaidLayer.duration(left); }

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
    const left = RaidLayer.left(v), age = RaidLayer.invasionAge(v), near = this.near(v.x, v.z, v.r);
    return `<div class="raid-pop invasion"><b>${escape(v.name || 'Invasion')}</b><small>Invasion: a camp of enemies that stays until cleared or its time runs out</small>
      <table class="dungeon-info">
        ${age !== null ? `<tr><td>Began</td><td>${ago(age)}</td></tr>` : ''}
        <tr><td>Ends</td><td>${left === null ? 'when it is cleared' : left < 1 ? 'any moment' : 'in ' + RaidLayer.duration(left)}</td></tr>
        <tr><td>Area</td><td>${Math.round(v.r * 2)} m across</td></tr>
        <tr><td>Near it now</td><td>${near.length ? near.map(escape).join(', ') : 'nobody'}</td></tr>
      </table><div class="meta">${Math.round(v.x)}, ${Math.round(v.z)}</div></div>`;
  }

  raidHtml(r) {
    const near = this.near(r.x, r.z, r.r);
    return `<div class="raid-pop"><b>Raid</b><div class="raid-msg">${escape(r.name || '')}</div>
      <table class="dungeon-info">
        <tr><td>Began</td><td>${ago(RaidLayer.raidAge(r))}</td></tr>
        ${r.near ? `<tr><td>Came for</td><td>${escape(r.near)}</td></tr>` : ''}
        <tr><td>Near it now</td><td>${near.length ? near.map(escape).join(', ') : 'nobody'}</td></tr>
        <tr><td>Area</td><td>${Math.round(r.r * 1.8)} m across</td></tr>
      </table><small>A raid lasts a few minutes, or until its attackers are beaten.</small><div class="meta">${Math.round(r.x)}, ${Math.round(r.z)}</div></div>`;
  }

  // the popup of an invasion in the Markers list
  openInvasion(m) {
    const v = m.invasion;
    if (v) L.popup({ offset: [0, -14] }).setLatLng(toLatLng(v.x, v.z)).setContent(m.raid ? this.raidHtml(v) : this.invasionHtml(v)).openOn(this.map);
  }

  draw(x, z, half, name, color, status, popup) {
    const at = toLatLng(x, z);
    const open = () => L.popup({ offset: [0, -14] }).setLatLng(at).setContent(popup()).openOn(this.map);
    const bounds = L.latLngBounds(toLatLng(x - half, z - half), toLatLng(x + half, z + half));
    const area = L.imageOverlay(areaImage(color), bounds, { interactive: true, className: 'raid-area', bubblingMouseEvents: false }).on('click', open).addTo(this.map);
    const html = `<div class="raid-pin" style="--c:${color}"><b>!</b></div><div class="raid-name" style="--c:${color}">${escape(name)}<small>${escape(status)}</small></div>`;
    const pin = L.marker(at, {
      icon: L.divIcon({ className: 'raid-icon', html, iconSize: [30, 30], iconAnchor: [15, 15] }),
      keyboard: false, zIndexOffset: 1500,
    }).on('click', open).addTo(this.map);
    return [area, pin];
  }
}
