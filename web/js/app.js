// Entry point: builds the map, wires the layers to the server, and owns the
// bits of UI that are not the sidebar (search, permalink, 2D/3D switch).

import { ValheimCRS, worldBounds, toLatLng, fromLatLng, MAX_ZOOM, OVER_ZOOM, TILE, WORLD_HALF, WORLD_RADIUS, OUTSIDE_COLOR, metersPerPixel } from './crs.js';
import { connect, on, state, getJSON } from './net.js';
import { FallbackTileLayer, BaseWorldImage } from './layers/tiles.js';
import { VegLayer, VEG_SHAPES_ZOOM } from './layers/veg.js';
import { PlayerCard } from './playercard.js';
import { FogLayer } from './layers/fog.js';
import { StructuresLayer } from './layers/structures.js';
import { RuinsLayer } from './layers/ruins.js';
import { webgl2Available, TreesGL, RuinsGL, BuildingsGL } from './layers/shapes.js';
import { GroundGL, FogGL, WorldEdgeGL, GuideGL, BiomeGL, AltBiomeGL, MistGL } from './layers/ground.js';
import { SmoothZoom, SmoothZoomControl } from './smoothzoom.js';
import { MarkerLayers, escape, byName } from './layers/markers.js';
import { PlayersLayer } from './layers/players.js';
import { RaidLayer } from './layers/raid.js';
import { ActivityLayers } from './layers/activity.js';
import { ContourGL } from './layers/contours.js';
import { MeasureTool } from './measure.js';
import { chunks, objects, prefabs, markers, stats } from './data.js';
import { Sidebar } from './ui.js';
import { BiomeHover } from './biomehover.js';

const $ = (s) => document.querySelector(s);

class App {
  constructor() {
    this.config = null;
    this.mode = '2d';
    this.view3d = null;
    this.root = $('#app');
    this.map = L.map('map', {
      crs: ValheimCRS, minZoom: 0, maxZoom: OVER_ZOOM, zoomSnap: 0, zoomDelta: 0.5, wheelPxPerZoomLevel: 90,   // zoomSnap 0: any zoom, for the smooth zoom
      maxBounds: worldBounds.pad(0.25), maxBoundsViscosity: 0.6, zoomControl: false, attributionControl: false,
      preferCanvas: true, worldCopyJump: false, inertia: true,
    });
    this.layers = {};
    // continuous zooming: wheel, buttons and double-click glide (smoothzoom.js)
    this.gl = webgl2Available();
    this.smoothZoom = new SmoothZoom(this.map, 160, this.gl);
    new SmoothZoomControl({ position: 'topleft', smooth: this.smoothZoom }).addTo(this.map);
    // Leaflet's vector renderers (portal lines, the grid) only stretch their last drawing during a
    // zoom and redraw when it ends; with a glide that left fat lines for the whole glide. Redraw
    // them at the real zoom once a frame instead (a few dozen lines: cheap).
    // Done right in the zoom event, so they are never a frame behind.
    this.map.on('zoom', () => {
      if (this.map._animatingZoom) return;
      this.map.eachLayer((l) => { if (l instanceof L.Renderer && l._map) l._reset(); });
    });
    // zoom out no further than the whole world circle in view, and there centre it in the part of
    // the map the top bar and sidebar don't cover (again after a resize or the sidebar toggling)
    this.fitWorld();
    this.map.on('resize', () => this.fitWorld());
    this.map.on('zoomend', () => this.centreWorld());
    // Blurry whole world under the map, for the edges of a fast zoom-out. Under the WebGL map it is
    // fogged in the page first (the fog there only covers the canvas), see start().
    // updateWhenZooming false: during a zoom the tiles on screen just scale, in step with the shapes on
    // the GPU canvas; a level Leaflet creates mid-animation started its transition a frame late, and
    // the ground slid under the trees by up to ~30 px. The new zoom's tiles come right after (mostly
    // from the browser cache), with the blurry world image under any edge for that moment.
    // the map tiles: in the WebGL canvas with everything else when the browser can (layers/ground.js)
    this.layers.tiles = (this.gl ? new GroundGL('tiles/map/{z}/{x}/{y}.png')
      : new FallbackTileLayer('tiles/map/{z}/{x}/{y}.png', { zIndex: 100, edgeBufferTiles: 1, prefetchZoomOut: 1, updateWhenZooming: false })).addTo(this.map);
    // Tree crowns and rocks over the ground (the 3D view uses the clean ground tiles). Trees, buildings and world structures on the GPU when the browser can (see shapes.js): there
    // the trees at every zoom come from the vegetation data and no tree tiles are loaded at all.
    if (this.gl) this.layers.veg = L.layerGroup([new TreesGL()]).addTo(this.map);
    else {
      this.vegTiles = new FallbackTileLayer('tiles/veg/{z}/{x}/{y}.png', { zIndex: 101, minNative: 5, className: 'maptiles vegtiles', prefetchZoomOut: 2 });
      const vegShapes = new VegLayer();
      this.layers.veg = L.layerGroup([this.vegTiles, vegShapes]).addTo(this.map);
      this.vegHandOff(vegShapes);
    }
    this.gridLayer = null;
    this.hoverTip = L.tooltip({ direction: 'top', offset: [0, -8], opacity: 0.95 });
    this.map.on('zoomend', () => this.onZoom());
    this.map.on('moveend', () => this.updateHash());
    this.map.on('mousemove', (e) => this.onMouseMove(e));
    this.map.on('click', () => this.hideSearch());
    this.pendingMove = null;
  }

  // the fallback's gray past the world's edge: a huge square with the world circle cut out
  addWorldEdgeMask() {
    const R = WORLD_RADIUS, far = R * 4, ring = [];
    for (let i = 0; i < 180; i++) { const a = (i / 180) * Math.PI * 2; ring.push(toLatLng(Math.cos(a) * R, Math.sin(a) * R)); }
    this.map.createPane('edgePane').style.zIndex = 450;   // over the fog overlay, under markers
    L.polygon([[[-far, -far], [-far, far], [far, far], [far, -far]], ring],
      { pane: 'edgePane', stroke: false, fillColor: OUTSIDE_COLOR, fillOpacity: 1, interactive: false, renderer: L.svg({ padding: 1, pane: 'edgePane' }) }).addTo(this.map);
  }

  // the canvas fallback's tree hand-off: baked tiles up to 7.5, shapes past it
  vegHandOff(vegShapes) {
    const showVegTiles = (on) => { const el = this.vegTiles.getContainer(); if (el) el.style.opacity = on ? '' : 0; };
    const shapesReady = () => this.map.getZoom() >= VEG_SHAPES_ZOOM && vegShapes._map && vegShapes.isReady();
    this.map.on('zoomanim', (e) => { if (e.zoom < VEG_SHAPES_ZOOM) showVegTiles(true); });
    this.map.on('zoomend', () => showVegTiles(!shapesReady()));
    vegShapes.on('load', () => showVegTiles(!shapesReady()));
    this.vegTiles.on('add', () => showVegTiles(!shapesReady()));
  }

  // the map area not under the top bar or the sidebar, in container pixels
  visibleArea() {
    const size = this.map.getSize(), cs = getComputedStyle(this.root);
    const top = parseFloat(cs.getPropertyValue('--topbar-h')) || 52;
    const sideOpen = !this.root.classList.contains('sidebar-hidden') && !matchMedia('(max-width: 720px)').matches;
    const side = sideOpen ? parseFloat(cs.getPropertyValue('--sidebar-w')) || 340 : 0;
    return { left: side, top, right: size.x, bottom: size.y };
  }

  fitWorld() {
    const a = this.visibleArea(), size = this.map.getSize();
    const pad = L.point(size.x - (a.right - a.left) + 32, size.y - (a.bottom - a.top) + 32);
    const z = Math.floor(this.map.getBoundsZoom(L.latLngBounds([-WORLD_RADIUS, -WORLD_RADIUS], [WORLD_RADIUS, WORLD_RADIUS]), false, pad) * 4) / 4;
    this.map.setMinZoom(z);
    // the pan limit has to allow the view that centre gives at that zoom, or Leaflet centres the
    // limit instead (straight under the sidebar)
    const c = this.map.project(this.worldCentreView(z), z), h = size.divideBy(2);
    this.map.setMaxBounds(worldBounds.pad(0.25).extend(L.latLngBounds(this.map.unproject(c.subtract(h), z), this.map.unproject(c.add(h), z))));
  }

  // the map centre that puts the world's centre in the middle of the visible area at zoom z
  worldCentreView(z) {
    const a = this.visibleArea(), size = this.map.getSize();
    const off = L.point((a.left + a.right) / 2 - size.x / 2, (a.top + a.bottom) / 2 - size.y / 2);
    return this.map.unproject(this.map.project(L.latLng(0, 0), z).subtract(off), z);
  }

  centreWorld() {
    const map = this.map, z = map.getZoom();
    if (z > map.getMinZoom() + 0.01) return;
    const target = this.worldCentreView(z);
    if (map.latLngToContainerPoint(target).distanceTo(map.latLngToContainerPoint(map.getCenter())) > 2) map.panTo(target, { duration: 0.35 });
  }

  // ?fps=1: a small readout of frames per second and the slowest frame over the last 2 s,
  // to check smoothness on a real machine (the test browser here has no GPU)
  showFps() {
    const el = document.createElement('div');
    el.style.cssText = 'position:fixed;right:8px;bottom:8px;z-index:9999;background:#000a;color:#fff;font:12px monospace;padding:4px 6px;border-radius:4px;pointer-events:none';
    document.body.append(el);
    let last = performance.now(), frames = 0, worst = 0, t0 = last;
    const tick = (now) => {
      frames++; worst = Math.max(worst, now - last); last = now;
      if (now - t0 >= 2000) { el.textContent = `${Math.round(frames * 1000 / (now - t0))} fps · slowest ${Math.round(worst)} ms`; frames = 0; worst = 0; t0 = now; }
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  }

  async start() {
    if (/[?&]fps=1\b/.test(location.search)) this.showFps();
    this.embedMode(new URLSearchParams(location.search));
    // ?player=<name>: open that player's card once the stats are in
    const wanted = new URLSearchParams(location.search).get('player');
    if (wanted) {
      const off = stats.onChange((d) => {
        const st = (d?.players || []).find((x) => x.name.toLowerCase() === wanted.toLowerCase()) || { name: wanted };
        off();
        const open = () => { if (this.playerCard) this.playerCard.showByName(st.name); else setTimeout(open, 300); };   // (the card is made later in start)
        open();
      });
      stats.refresh();
    }
    this.config = await getJSON('config').catch(() => ({}));
    this.applyConfig(this.config);
    this.layers.fog = new FogLayer(this.map, this.config, { gl: this.gl });
    // gray past the world's edge, over the fog (which stays black inside the circle)
    if (this.gl) { new FogGL(this.layers.fog).addTo(this.map); new WorldEdgeGL(WORLD_RADIUS).addTo(this.map); this.biomes = new BiomeGL().addTo(this.map); this.altBiomes = new AltBiomeGL().addTo(this.map); this.mist = new MistGL().addTo(this.map); }
    else this.addWorldEdgeMask();
    this.baseImage = new BaseWorldImage(this.map, 'tiles/map/{z}/{x}/{y}.png', this.gl ? { fog: this.layers.fog, radius: WORLD_RADIUS } : {});
    this.layers.ruins = (this.gl ? new RuinsGL() : new RuinsLayer()).addTo(this.map);   // world-generated structures, under player builds
    this.layers.structures = (this.gl ? new BuildingsGL() : new StructuresLayer()).addTo(this.map);
    this.layers.markers = new MarkerLayers(this.map, { gl: this.gl });
    this.layers.activity = new ActivityLayers(this.map);
    // right click (long press on a phone) places a pin, unless the server turned web pins off
    this.map.on('contextmenu', (e) => { if (this.measure?.active) { this.measure.undo(); return; } if (this.config?.web_pins !== false) this.layers.markers.openPinEditor(e.latlng); });
    // the ruler: distances, travel times and reach (drawn in the WebGL map)
    if (this.gl) {
      this.measure = new MeasureTool(this);
      const mb = $('#btn-measure');
      mb.hidden = false;
      mb.addEventListener('click', () => this.measure.toggle());
    }
    // middle click: ping the spot, shown in game like a player's ping (Server/WebPing.cs); the pin
    // form has a Ping button too, for touch screens
    const box = this.map.getContainer();
    box.addEventListener('mousedown', (e) => { if (e.button === 1) e.preventDefault(); });   // no auto-scroll
    box.addEventListener('auxclick', (e) => { if (e.button === 1) { e.preventDefault(); this.webPing(this.map.mouseEventToLatLng(e)); } });
    this.layers.markers.onPing = (latlng, name) => this.webPing(latlng, name);
    this.layers.players = new PlayersLayer(this.map);
    this.layers.raid = new RaidLayer(this.map, this.layers.markers);
    this.layers.players.onFollow = (id) => { if (this.view3d) this.view3d.follow(id); };
    this.sidebar = new Sidebar(this);
    // the biome grid for the cursor's readout, fetched while idle rather than at the first mouse move
    const warmBiomes = () => (this.biomeHover ??= new BiomeHover()).at(0, 0);
    if (window.requestIdleCallback) requestIdleCallback(warmBiomes, { timeout: 5000 }); else setTimeout(warmBiomes, 3000);
    this.layers.players.onChange((ps) => { this.sidebar.renderPlayers(ps); if (this.view3d) this.view3d.setPlayers(ps); });
    this.bindUi();
    if (!this.applyHash()) this.goToSpawn(false);
    this.onZoom();
    this.layers.fog.start();   // reveals live from player positions, refetches the mask every few minutes
    chunks.refreshIndex();
    if (this.config.enable_3d !== false) { objects.refreshIndex(); prefabs.refresh(); }   // 3D-only data
    markers.refresh();
    stats.refresh();
    connect();
    on('hello', (f) => { if (f.config) { this.config = f.config; this.applyConfig(f.config); } });
    on('connection', (ok) => { $('#conn').hidden = ok; });
    on('players', (f) => this.showWind(f.data && f.data.windDeg));
    on('tiles', (f) => { $('#render-status').textContent = f.status ? `tiles ${f.status}` : ''; });
    setInterval(() => { if (this.sidebar.active === 'stats') stats.refresh(); }, 30000);
    if (matchMedia('(max-width: 720px)').matches) this.toggleSidebar(false);
  }

  applyConfig(c) {
    if (!c) return;
    $('#title').textContent = c.title || 'Valheim';
    document.title = `${c.title || 'Valheim'} · Munin Map`;
    if (c.world_name) $('#subtitle').textContent = c.world_name;
    // with 3D off, hide what only works with it (inline style: CSS display would override `hidden`)
    $('#btn-mode').style.display = c.enable_3d === false ? 'none' : '';
    $('#export-kind-scene').style.display = c.enable_3d === false ? 'none' : '';
    if (c.world_start_pos && typeof c.world_start_pos === 'string') {
      const [x, y, z] = c.world_start_pos.split(',').map(Number);
      this.spawn = { x, z };
    }
  }

  bindUi() {
    $('#btn-menu').addEventListener('click', () => this.toggleSidebar());
    $('#btn-home').addEventListener('click', () => this.goToSpawn(true));
    for (const b of $('#btn-mode').querySelectorAll('button')) b.addEventListener('click', () => this.setMode(b.dataset.mode));
    this.playerCard = new PlayerCard(this);
    this.layers.players.onClick = (p, x, y) => this.playerCard.show(p, x, y);
    this.layers.players.onChange((list) => this.playerCard.update(list));
    $('#btn-export').addEventListener('click', () => this.openExport());
    $('#export-close').addEventListener('click', () => this.closeExport());
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !$('#export-dialog').hidden) this.closeExport(); });
    // the outlined area follows the map while the dialog is open
    this.map.on('move', () => { if (!$('#export-dialog').hidden && !this.exportMoving) { this.exportMoving = true; requestAnimationFrame(() => { this.exportMoving = false; this.exportChanged(); }); } });
    $('#export-form').addEventListener('submit', (e) => { e.preventDefault(); this.runExport(); });
    $('#export-form').addEventListener('change', () => this.exportChanged());
    // light or dark menus (the map stays as it is), kept by the browser; index.html sets it before drawing
    const themeBtn = $('#btn-theme'), showTheme = () => {
      const light = document.documentElement.dataset.theme === 'light';
      themeBtn.querySelector('use').setAttribute('href', light ? '#i-moon' : '#i-sun');
      themeBtn.title = light ? 'Dark menus' : 'Light menus';
    };
    themeBtn.addEventListener('click', () => {
      const t = document.documentElement.dataset.theme === 'light' ? 'dark' : 'light';
      document.documentElement.dataset.theme = t;
      try { localStorage.setItem('munin-theme', t); } catch (e) { /* this visit only */ }
      showTheme();
    window.muninThemeShown = showTheme;   // (the browser's setting changed while no choice is kept)
    });
    showTheme();
    $('#btn-fullscreen').addEventListener('click', () => { if (document.fullscreenElement) document.exitFullscreen(); else document.documentElement.requestFullscreen(); });
    $('#btn-link').addEventListener('click', async () => {
      this.updateHash();
      try { await navigator.clipboard.writeText(location.href); this.toast('Link copied'); } catch { this.toast(location.href); }
    });
    const search = $('#search');
    if (matchMedia('(max-width: 560px)').matches) search.placeholder = 'Search…';
    search.addEventListener('input', () => this.onSearch(search.value));
    search.addEventListener('focus', () => this.onSearch(search.value));
    search.addEventListener('keydown', (e) => {
      const res = $('#search-results');
      const items = [...res.querySelectorAll('button.hit')];
      let i = items.findIndex((b) => b.classList.contains('active'));
      if (e.key === 'ArrowDown') { i = Math.min(items.length - 1, i + 1); e.preventDefault(); }
      else if (e.key === 'ArrowUp') { i = Math.max(0, i - 1); e.preventDefault(); }
      else if (e.key === 'Enter') {
        // Enter goes to the highlighted match and keeps the list; Enter again, the next one (Shift: back)
        if (!items.length) return;
        if (i >= 0 && i === this.searchAt) i = (i + (e.shiftKey ? items.length - 1 : 1)) % items.length;
        i = Math.max(0, i);
        this.searchAt = i;
        items[i].go();
        items[i].scrollIntoView({ block: 'nearest' });
      }
      else if (e.key === 'Escape') { this.hideSearch(); search.blur(); return; }
      else return;
      items.forEach((b, k) => b.classList.toggle('active', k === i));
    });
    document.addEventListener('keydown', (e) => {
      if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT' || e.metaKey || e.ctrlKey || e.altKey) return;
      const k = e.key.length === 1 ? e.key.toLowerCase() : e.key;
      if (k === '/') { e.preventDefault(); search.focus(); return; }
      if (k === 'Escape') { this.layers.players.follow(null); this.playerCard?.hide(); return; }
      if (k === 'Home') { e.preventDefault(); this.goToSpawn(true); return; }
      if (k === 'm') { this.setMode(this.mode === '3d' ? '2d' : '3d'); return; }
      if (k === 'l') { this.toggleSidebar(); return; }
      if (k === 'p') { if (this.layers.players.following) this.layers.players.follow(null); else this.cyclePlayer(); return; }
      if (this.mode === '3d') return;   // the 3D view polls its own keys
      // 2D: WASD pans (arrows and +/- are Leaflet's own), Shift is fast
      const step = e.shiftKey ? 300 : 100;
      const pan = { w: [0, -step], s: [0, step], a: [-step, 0], d: [step, 0] }[k];
      if (pan) { e.preventDefault(); this.layers.players.follow(null); this.map.panBy(pan, { animate: true, duration: 0.15 }); }
    });
    window.addEventListener('hashchange', () => this.applyHash());
  }

  // ?embed: the map alone (no bars, buttons or sidebar), for a stream overlay or a page of its own;
  // ?follow=<name>: keep following that player (picked up again whenever they come back online)
  embedMode(qs) {
    if (qs.has('embed')) {
      this.root.classList.add('embed', 'sidebar-hidden');
      setTimeout(() => this.map.invalidateSize(), 0);
    }
    const name = (qs.get('follow') || '').trim().toLowerCase();
    if (!name) return;
    setInterval(() => {
      const PL = this.layers.players;
      if (!PL || PL.following) return;
      const p = PL.players.find((x) => x.name.toLowerCase() === name && x.x !== undefined);
      if (p) PL.follow(p.id);
    }, 3000);
  }

  // P: follow the next player on the list (2D and 3D)
  cyclePlayer() {
    const PL = this.layers.players, list = PL.players.filter((p) => p.x !== undefined);
    if (list.length === 0) return;
    const i = list.findIndex((p) => p.id === this.lastCycled);
    const p = list[(i + 1) % list.length];
    this.lastCycled = p.id;
    PL.follow(p.id);
  }

  // the wind arrow by the coordinates: where the wind blows to, like the arrow on the game's map
  showWind(deg) {
    const w = $('#wind');
    if (deg === undefined || deg === null) { w.textContent = ''; return; }
    const names = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'];
    const from = names[Math.round(((deg + 180) % 360) / 45) % 8];   // a wind is named for where it comes from
    if (!w.firstChild) w.innerHTML = '<svg viewBox="0 0 24 24"><path d="M12 2l6 14h-5v6h-2v-6H6z" fill="currentColor"/></svg><span></span>';
    w.querySelector('svg').style.transform = `rotate(${Math.round(deg)}deg)`;
    w.querySelector('span').textContent = `${from} wind`;
  }

  toggleSidebar(show) {
    const hidden = this.root.classList.contains('sidebar-hidden');
    const next = show === undefined ? hidden : show;
    this.root.classList.toggle('sidebar-hidden', !next);
    setTimeout(() => { this.map.invalidateSize(); this.fitWorld(); this.centreWorld(); }, 220);
  }

  onZoom() {
    const z = this.map.getZoom();
    const c = this.map.getContainer();
    c.classList.toggle('zoom-lt-4', z < 4);
    c.classList.toggle('zoom-lt-3', z < 3);
    this.updateHash();
  }

  // ---------------------------------------------------------------- navigation
  // jump: straight there, no glide (a far jump glided with cached layers riding along until it
  // ended: trees, raids, a popup opened at the far end; issues #2, #14)
  goTo(x, z, zoom, jump) {
    if (this.mode === '3d' && this.view3d) { this.view3d.lookAt(x, z); return; }
    this.layers.players.follow(null);
    const to = toLatLng(x, z), zz = zoom ?? this.map.getZoom();
    const size = this.map.getSize(), far = this.map.project(to, zz).distanceTo(this.map.project(this.map.getCenter(), zz));
    if (jump || far > 3 * Math.max(size.x, size.y)) this.map.setView(to, zz, { animate: false });
    else this.smoothZoom.glideTo(to, zz);
  }

  goToSpawn(animate) {
    const s = this.spawn || { x: 0, z: 0 };
    if (this.mode === '3d' && this.view3d) { this.view3d.lookAt(s.x, s.z); return; }
    if (animate) this.smoothZoom.glideTo(toLatLng(s.x, s.z), 4);
    else this.map.setView(toLatLng(s.x, s.z), 4, { animate: false });
  }

  // #x,z,zoom for 2D; #x,z,zoom,3d,dist,heading,tilt for 3D (zoom is the 2D zoom the same
  // camera distance would give, so the link still lands about right in 2D)
  updateHash() {
    if (this.hashLock) return;
    let h;
    if (this.mode === '3d' && this.view3d) {
      const p = this.view3d.pose();
      const zoom = Math.min(7, Math.max(0, 3 + Math.log2(3200 / p.dist)));
      h = `#${p.x.toFixed(0)},${p.z.toFixed(0)},${zoom.toFixed(2)},3d,${p.dist.toFixed(0)},${p.yaw.toFixed(0)},${p.pitch.toFixed(0)}`;
    } else {
      const c = fromLatLng(this.map.getCenter());
      h = `#${c.x.toFixed(0)},${c.z.toFixed(0)},${this.map.getZoom().toFixed(2)}`;
    }
    if (location.hash !== h) { try { history.replaceState(null, '', h); } catch { /* browser rate limit */ } }
  }

  applyHash() {
    const m = /^#(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?),(\d+(?:\.\d+)?)(?:,(3d)(?:,(\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?))?)?/.exec(location.hash);
    if (!m) return false;
    this.hashLock = true;
    this.map.setView(toLatLng(+m[1], +m[2]), +m[3], { animate: false });
    const pose = m[4] ? { x: +m[1], z: +m[2], zoom: +m[3], ...(m[5] !== undefined ? { dist: +m[5], yaw: +m[6], pitch: +m[7] } : {}) } : null;
    if (pose && this.mode === '3d' && this.view3d) this.view3d.setPose(pose.dist !== undefined ? pose : { x: pose.x, z: pose.z, dist: 3200 / Math.pow(2, pose.zoom - 3), yaw: 30, pitch: 47 });
    this.hashLock = false;
    if (pose && this.mode !== '3d') { this.pendingPose = pose; this.setMode('3d'); }
    else if (!pose && this.mode === '3d') this.setMode('2d');
    return true;
  }

  // ---------------------------------------------------------------- hover
  onMouseMove(e) {
    const p = fromLatLng(e.latlng);
    $('#coords').textContent = `${p.x.toFixed(0)}, ${p.z.toFixed(0)}`;
    const biome = (this.biomeHover ??= new BiomeHover()).at(p.x, p.z), bel = $('#biome');
    if (bel.textContent !== biome) bel.textContent = biome;
    if (this.map.getZoom() < 6 || !this.map.hasLayer(this.layers.structures)) { this.hideHover(); return; }
    clearTimeout(this.hoverTimer);
    this.hoverTimer = setTimeout(async () => {
      const hits = await this.layers.structures.pick(p.x, p.z, metersPerPixel(this.map.getZoom()) * 3);
      if (!hits.length) { this.hideHover(); return; }
      const h = hits[0];
      const more = hits.length > 1 ? `<br><small>+${hits.length - 1} more piece${hits.length > 2 ? 's' : ''} here</small>` : '';
      this.hoverTip.setLatLng(e.latlng).setContent(`<b>${escape(h.prefab)}</b><br><small>${escape(h.material)} · y ${h.y.toFixed(0)}</small>${more}`);
      if (!this.hoverTip._map) this.hoverTip.addTo(this.map);
    }, 60);
  }
  hideHover() { clearTimeout(this.hoverTimer); if (this.hoverTip._map) this.hoverTip.remove(); }

  // ---------------------------------------------------------------- search
  onSearch(q) {
    const res = $('#search-results');
    q = (q || '').trim();
    if (!q) { res.hidden = true; return; }
    const out = [];
    const coord = /^(-?\d+(?:\.\d+)?)[ ,]+(-?\d+(?:\.\d+)?)$/.exec(q);
    if (coord) out.push({ kind: 'Coordinates', label: `${coord[1]}, ${coord[2]}`, x: +coord[1], z: +coord[2] });
    const ql = q.toLowerCase();
    for (const p of this.layers.players.players) if (p.name.toLowerCase().includes(ql) && p.x !== undefined) out.push({ kind: 'Player', label: p.name, x: p.x, z: p.z, player: p.id });
    // markers: names that start with the query first, then the rest, each in name order
    const found = [];
    for (const m of this.layers.markers.all()) if ((m.label || '').toLowerCase().includes(ql) || (m.kind || '').toLowerCase() === ql) found.push(m);
    const starts = (m) => ((m.label || '').toLowerCase().startsWith(ql) ? 0 : 1);
    out.push(...found.sort((a, b) => starts(a) - starts(b) || byName(a.label, b.label) || byName(a.kind, b.kind)));
    res.replaceChildren();
    this.searchAt = -1;
    // 50 at a time, the rest behind "Show more" (it used to stop at 30, with no way to the others)
    const page = (from) => {
      for (const r of out.slice(from, from + 50)) {
        const b = document.createElement('button');
        b.className = 'hit';
        b.innerHTML = `<span>${escape(r.label)}</span><span class="kind">${escape(r.kind)} · ${Math.round(r.x)}, ${Math.round(r.z)}</span>`;
        b.go = () => { this.goTo(r.x, r.z, Math.max(this.map.getZoom(), 6)); if (r.player) this.layers.players.follow(r.player); };
        b.addEventListener('click', () => { b.go(); this.hideSearch(); });
        res.append(b);
      }
      const left = out.length - from - 50;
      if (left > 0) {
        const more = document.createElement('button');
        more.className = 'more';
        more.textContent = `Show more (${left} left)`;
        more.addEventListener('click', (e) => { e.stopPropagation(); more.remove(); page(from + 50); });
        res.append(more);
      }
    };
    page(0);
    if (!out.length) res.append(Object.assign(document.createElement('div'), { className: 'empty', textContent: 'No matches' }));
    res.hidden = false;
  }
  hideSearch() { $('#search-results').hidden = true; }

  // ---------------------------------------------------------------- grid
  // dashed rings every 500 m around the spawn, labelled each kilometre (how far is that boss?)
  // with WebGL both are drawn by GuideGL (layers/ground.js)
  guide() {
    if (!this.guideGL) this.guideGL = new GuideGL(WORLD_RADIUS, '#7cff4f').addTo(this.map);   // the spawn's colour
    this.guideGL.set(!!this.gridOn, this.ringsOn && this.spawn ? this.spawn : null);
  }

  // the Height map style: the land coloured by height, from the height tiles (made on first use)
  setHeightStyle(v) {
    if (v && !this.heightFill) this.heightFill = new ContourGL({ mode: 'fill', heightMaxZoom: this.config?.height_max_zoom ?? -1 });
    if (!this.heightFill) return;
    if (v) this.heightFill.addTo(this.map); else this.heightFill.remove();
  }

  // contour lines from the height tiles (made on first use)
  setContours(v) {
    if (v && !this.contours) this.contours = new ContourGL({ heightMaxZoom: this.config?.height_max_zoom ?? -1 });
    if (!this.contours) return;
    if (v) this.contours.addTo(this.map); else this.contours.remove();
  }

  setRings(v) {
    this.ringsOn = v;
    if (this.gl) { this.guide(); return; }
    if (this.rings) { this.rings.remove(); this.rings = null; }
    if (!v || !this.spawn) return;
    const g = L.layerGroup(), c = toLatLng(this.spawn.x, this.spawn.z), renderer = L.svg({ padding: 1 });
    for (let r = 500; r <= 6000; r += 500) {
      const km = r % 1000 === 0;
      g.addLayer(L.circle(c, { radius: r, renderer, interactive: false, fill: false, weight: km ? 1.4 : 0.9, opacity: km ? 0.75 : 0.45, color: '#7cff4f', dashArray: km ? null : '4 6' }));   // the spawn's colour
      if (km) g.addLayer(L.marker(toLatLng(this.spawn.x, this.spawn.z + r), { interactive: false, keyboard: false,
        icon: L.divIcon({ className: '', html: `<div class="ring-lbl">${r / 1000} km</div>`, iconSize: [40, 14], iconAnchor: [20, 7] }) }));
    }
    this.rings = g.addTo(this.map);
  }

  setGrid(v) {
    this.gridOn = v;
    if (this.gl) { this.guide(); return; }
    if (v && !this.gridLayer) {
      this.gridLayer = new (L.GridLayer.extend({
        createTile(coords) {
          const c = document.createElement('canvas'); c.width = TILE; c.height = TILE;
          const ctx = c.getContext('2d');
          const span = TILE * metersPerPixel(coords.z);
          if (span <= 256) {
            // chunk lines fall on tile borders at zoom >= 7; draw the border
            ctx.strokeStyle = 'rgba(255,255,255,.25)'; ctx.strokeRect(0.5, 0.5, TILE - 1, TILE - 1);
          } else {
            const n = span / 256, step = TILE / n;
            ctx.strokeStyle = 'rgba(255,255,255,.18)';
            for (let i = 0; i <= n; i++) { ctx.beginPath(); ctx.moveTo(i * step + .5, 0); ctx.lineTo(i * step + .5, TILE); ctx.moveTo(0, i * step + .5); ctx.lineTo(TILE, i * step + .5); ctx.stroke(); }
          }
          const minX = -WORLD_HALF + coords.x * span, maxZ = WORLD_HALF - coords.y * span;
          ctx.fillStyle = 'rgba(255,255,255,.5)'; ctx.font = '10px monospace';
          ctx.fillText(`${minX.toFixed(0)}, ${maxZ.toFixed(0)}`, 4, 12);
          return c;
        },
      }))({ tileSize: TILE, minZoom: 3, maxZoom: OVER_ZOOM, zIndex: 400, opacity: 1 });
    }
    if (this.gridLayer) { if (v) this.gridLayer.addTo(this.map); else this.gridLayer.remove(); }
  }

  // ---------------------------------------------------------------- 3D
  async ensureView3D() {
    if (this.view3d) return this.view3d;
    const { View3D } = await import('./view3d.js');
    this.view3d = new View3D($('#gl'), this.config);
    this.view3d.onUnfollow = () => this.layers.players.follow(null);
    this.view3d.onHome = () => this.goToSpawn(true);
    this.view3d.onView = () => this.updateHash();
    this.view3d.setPlayers(this.layers.players.players);
    this.view3d.setPins(this.layers.markers.pinList());
    this.layers.markers.onPins((pins) => this.view3d.setPins(pins));
    this.view3d.onPlayerClick = (id, x, y) => { const p = this.layers.players.players.find((q) => q.id === id); if (p) this.playerCard.show(p, x, y); };
    return this.view3d;
  }

  async setMode(mode) {
    if (mode === this.mode) return;
    if (mode === '3d' && this.config?.enable_3d === false) return;   // 3D switched off on the server (button, card, key M, links)
    // the switch shows which view is on, not which one a click leads to
    const sw = $('#btn-mode'), mark = (m) => { for (const b of sw.querySelectorAll('button')) { b.classList.toggle('on', b.dataset.mode === m); b.setAttribute('aria-pressed', String(b.dataset.mode === m)); } };
    if (mode === '3d') {
      sw.classList.add('busy');
      try {
        await this.ensureView3D();
        const c = fromLatLng(this.map.getCenter());
        $('#view3d').hidden = false;
        $('#map').style.visibility = 'hidden';
        this.view3d.show(this.pendingPose || { x: c.x, z: c.z, zoom: this.map.getZoom() });
        this.pendingPose = null;
        this.mode = '3d';
        if (this.layers.players.following) this.view3d.follow(this.layers.players.following);
        this.root.dataset.mode = '3d';
        mark('3d');
      } catch (e) {
        console.error(e);
        this.toast('3D view could not start: ' + e.message);
      }
      sw.classList.remove('busy');
    } else {
      const c = this.view3d ? this.view3d.center() : null;
      this.view3d?.hide();
      $('#view3d').hidden = true;
      $('#map').style.visibility = '';
      this.mode = '2d';
      this.root.dataset.mode = '2d';
      mark('2d');
      if (c) this.map.setView(toLatLng(c.x, c.z), this.map.getZoom(), { animate: false });
      this.map.invalidateSize();
    }
    this.updateHash();
  }

  // ---------------------------------------------------------------- export
  // One dialog for every kind: a picture of the map, the markers as a list, or a 3D scene. The area
  // is outlined on the 2D map while it's open, the line by the button says what will come out, and
  // the choices are kept for next time.
  openExport() {
    const d = $('#export-dialog'), form = $('#export-form');
    try {
      const saved = JSON.parse(localStorage.getItem('munin-export') || '{}');
      for (const [k, v] of Object.entries(saved)) {
        const els = form.querySelectorAll(`[name="${k}"]`);
        if (els.length && els[0].type === 'radio') els.forEach((x) => { x.checked = x.value === v; });
        else if (els.length === 1 && els[0].type === 'checkbox') els[0].checked = !!v;
        else if (els.length === 1) els[0].value = v;
      }
    } catch (e) { /* defaults */ }
    if (this.config?.enable_3d === false && form.kind.value === 'scene') form.querySelector('[name=kind][value=image]').checked = true;
    d.hidden = false;
    this.exportChanged();
  }

  closeExport() {
    $('#export-dialog').hidden = true;
    if (this.exportBox) { this.exportBox.remove(); this.exportBox = null; }
  }

  // the area asked for, in world metres
  exportArea(f) {
    const area = f.get('area');
    if (area === 'world') return { x0: -WORLD_RADIUS, z0: -WORLD_RADIUS, x1: WORLD_RADIUS, z1: WORLD_RADIUS };
    if (area === 'view') {
      const b = this.map.getBounds(), sw = fromLatLng(b.getSouthWest()), ne = fromLatLng(b.getNorthEast());
      return { x0: Math.max(sw.x, -WORLD_RADIUS), z0: Math.max(sw.z, -WORLD_RADIUS), x1: Math.min(ne.x, WORLD_RADIUS), z1: Math.min(ne.z, WORLD_RADIUS) };
    }
    const c = this.mode === '3d' && this.view3d ? this.view3d.center() : fromLatLng(this.map.getCenter()), half = +area / 2;
    return { x0: c.x - half, z0: c.z - half, x1: c.x + half, z1: c.z + half };
  }

  async exportChanged() {
    const form = $('#export-form'), f = new FormData(form), kind = f.get('kind');
    for (const s of form.querySelectorAll('[data-kind]')) s.hidden = s.dataset.kind !== kind;
    const area = form.area;
    area.querySelector('option[value=view]').disabled = this.mode !== '2d';
    area.querySelector('option[value=world]').disabled = kind === 'scene';
    if (area.selectedOptions[0]?.disabled) area.value = '512';
    form.imgLabels.disabled = form.imgPortals.disabled = !form.imgMarkers.checked;
    const groundOnly = form.mode.value === 'heightmap' || form.mode.value === 'stl';   // (the ground alone: nothing else to include)
    for (const x of form.querySelectorAll('[data-kind=scene] .checks input')) x.disabled = groundOnly;
    const saved = {};
    for (const [k, v] of f.entries()) if (k !== 'cat') saved[k] = v;
    for (const x of form.querySelectorAll('input[type=checkbox]:not([name=cat])')) saved[x.name] = x.checked;
    try { localStorage.setItem('munin-export', JSON.stringify(saved)); } catch (e) { /* this visit only */ }
    const a = this.exportArea(new FormData(form));
    // the area on the map
    if (this.mode === '2d') {
      const bounds = [toLatLng(a.x0, a.z0), toLatLng(a.x1, a.z1)];
      if (this.exportBox) this.exportBox.setBounds(bounds);
      else this.exportBox = L.rectangle(bounds, { color: '#f2c14e', weight: 2, dashArray: '6 6', fill: true, fillOpacity: 0.06, interactive: false }).addTo(this.map);
    }
    // what will come out
    const status = $('#export-status'), km = (m) => (m >= 1000 ? (m / 1000).toFixed(1) + ' km' : Math.round(m) + ' m');
    const size = `${km(a.x1 - a.x0)} × ${km(a.z1 - a.z0)}`;
    const { imageSize, markerRows } = await import('./mapexport.js');
    const f2 = new FormData(form);
    if (kind === 'image') {
      const im = imageSize(a, +f2.get('mpp'));
      status.textContent = `${size} · ${im.w} × ${im.h} px${im.mpp > +f2.get('mpp') ? ` (${im.mpp} m per pixel: the most a picture holds)` : ''}`;
    } else if (kind === 'markers') {
      const n = markerRows(this, a, form.mkShown.checked).length;
      status.textContent = `${size} · ${n.toLocaleString()} marker${n === 1 ? '' : 's'}`;
    } else status.textContent = size;
  }

  async runExport() {
    const form = $('#export-form'), status = $('#export-status'), go = $('#export-go');
    const f = new FormData(form), kind = f.get('kind'), a = this.exportArea(f);
    const stamp = new Date().toISOString().slice(0, 10), world = (this.config?.world_name || 'world').replace(/[^\w-]+/g, '_');
    const save = (blob, name) => {
      const link = document.createElement('a');
      link.href = URL.createObjectURL(blob); link.download = name;
      document.body.appendChild(link); link.click(); link.remove();
      setTimeout(() => URL.revokeObjectURL(link.href), 60000);
      this.toast('Export ready: ' + name);
    };
    go.disabled = true;
    try {
      if (kind === 'image') {
        const { mapImage } = await import('./mapexport.js');
        const out = await mapImage(this, a, { mpp: +f.get('mpp'), veg: form.imgVeg.checked, markers: form.imgMarkers.checked, labels: form.imgLabels.checked, portals: form.imgPortals.checked, players: form.imgPlayers.checked, grid: form.imgGrid.checked, format: f.get('imgFormat') }, (msg) => { status.textContent = msg; });
        const name = `${world}-map-${stamp}.${out.ext}`;
        save(out.blob, name);
        status.textContent = `${name} · ${out.w} × ${out.h} px · ${(out.blob.size / 1048576).toFixed(1)} MB`;
      } else if (kind === 'markers') {
        const { markerRows, markerFile } = await import('./mapexport.js');
        const bh = (this.biomeHover ??= new BiomeHover());
        if (!bh.grid) await bh.load();   // (for each marker's biome)
        const rows = markerRows(this, a, form.mkShown.checked), fmt = f.get('mkFormat');
        const name = `${world}-markers-${stamp}.${fmt}`;
        save(markerFile(rows, fmt), name);
        status.textContent = `${name} · ${rows.length.toLocaleString()} markers`;
      } else {
        const opts = { ...a, step: +f.get('step'), cats: new Set(f.getAll('cat')), water: f.get('water') === 'on', markers: f.get('markers') === 'on', trees: true, mode: f.get('mode') };
        try { await this.ensureView3D(); } catch (e) { status.textContent = '3D could not start: ' + e.message; return; }
        const { Exporter } = await import('./export.js');   // loaded on use: it pulls in three.js
        const ex = new Exporter(this);
        ex.report = (msg) => { status.textContent = msg; };
        const out = await ex.run(opts);
        save(out.blob, out.name);
        status.textContent = `${out.name} · ${(out.blob.size / 1048576).toFixed(1)} MB · ${out.meta.objects} objects`;
      }
    } catch (e) {
      status.textContent = e.message || String(e);
      console.warn('export', e);
    } finally { go.disabled = false; }
  }

  // POST /api/ping as the name pins are made under (asked the first time); once every 10 s
  async webPing(latlng, name) {
    if (this.config?.web_pings === false) return;
    const { x, z } = fromLatLng(latlng);
    if (!name) { try { name = localStorage.getItem('munin-pin-name') || ''; } catch (e) { /* no storage */ } }
    if (!name) {
      name = (window.prompt('Your name, shown in game with the ping') || '').trim();
      if (!name) return;
      try { localStorage.setItem('munin-pin-name', name); } catch (e) { /* this visit only */ }
    }
    try {
      const r = await fetch('api/ping', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ x: Math.round(x * 10) / 10, z: Math.round(z * 10) / 10, name }) });
      if (r.status === 429) { const j = await r.json().catch(() => ({})); this.toast(`Wait ${j.wait || 10} s to ping again`); }
      else if (!r.ok) this.toast('Could not ping');
    } catch (e) { this.toast('Could not ping'); }
  }

  toast(msg) {
    const t = $('#toast');
    t.textContent = msg; t.hidden = false;
    clearTimeout(this.toastTimer);
    this.toastTimer = setTimeout(() => { t.hidden = true; }, 3500);
  }
}

window.app = new App();
window.app.start();
