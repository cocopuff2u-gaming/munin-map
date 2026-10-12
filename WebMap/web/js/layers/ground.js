// The map tiles and the fog, drawn in the same WebGL canvas as the trees and buildings
// (layers/shapes.js), the way MapLibre and OpenLayers' WebGL layers draw tiles.
//
// With the tiles as <img> elements Leaflet shuffled hundreds of DOM nodes and animated them with
// CSS, and a tile level made mid-zoom slid against everything else. Here a tile is a GPU texture,
// the whole map is one canvas, and each square is drawn from the best picture there is: the tile
// itself, else its sharper children still loaded from before a zoom-out, else a stretched ancestor
// (the zoom-2 tiles are always loaded, so there is always one). New tiles fade in over that, so
// nothing is ever blank and nothing pops.

import { ShapesCanvas, screenArea, CLOUD_EXTENT, MIST_RES } from './shapes.js';
import { on } from '../net.js';
import { fetchMist, MIST_BLOCKS } from '../mist.js';
import { OUTSIDE_RIM, worldTile, WORLD_HALF } from '../crs.js';

const MAX_ZOOM = 7, TILE = 256;
const BASE_ZOOM = 2;          // always loaded: the last fallback for any square
const KEEP = 384;             // textures kept (256 KB each), oldest unseen ones go first
const PARALLEL = 6;           // a browser opens about this many connections per server anyway
const FADE_MS = 200;
const span = (z) => TILE * Math.pow(2, MAX_ZOOM - z);
const perSide = (z) => Math.ceil((2 * WORLD_HALF) / span(z));

export class GroundGL extends L.Layer {
  constructor(template) {
    super();
    this.template = template;
    this.order = 0;
    this.tex = new Map();       // "z/x/y" -> {tex, born, seen}
    this.missing = new Set();   // tiles the server doesn't have (yet): not asked for again until it renders them
    this.queue = new Map();     // "z/x/y" -> priority (lower first)
    this.inflight = new Set();
    this.rerendered = new Map(); // "z/x/y" -> ?r= value for tiles re-rendered while the page is open
    this.decoded = new Map();    // "z/x/y" -> ImageBitmap waiting for its upload to the GPU
    on('tiles', (f) => this.onRendered(f.keys));
  }

  onAdd(map) {
    this.sc = ShapesCanvas.for(map);
    this.sc.add(this);
    map.on('move', this.onMove, this);
    // where the mouse rests: the next zoom-in's tiles are loaded around it (prewarm)
    map.on('mousemove', (e) => {
      this.focus = { x: e.latlng.lng, z: e.latlng.lat };
      clearTimeout(this.focusTimer);
      this.focusTimer = setTimeout(() => this.sc && this.sc.idleWork(), 250);
    });
    for (let x = 0; x < perSide(BASE_ZOOM); x++)
      for (let y = 0; y < perSide(BASE_ZOOM); y++) this.want(`${BASE_ZOOM}/${x}/${y}`, -1);
    this.pump();
  }
  onRemove(map) { map.off('move', this.onMove, this); this.sc.remove(this); }

  url(key) {
    const b = this.rerendered.get(key);
    const [z, x, y] = key.split('/');
    return worldTile(this.template.replace('{z}', z).replace('{x}', x).replace('{y}', y) + (b ? `?r=${b}` : ''));
  }

  // tile range covering an area at zoom z
  range(a, z) {
    const s = span(z), n = perSide(z);
    return {
      x0: Math.max(0, Math.floor((a.x + WORLD_HALF) / s)), x1: Math.min(n - 1, Math.floor((a.x1 + WORLD_HALF) / s)),
      y0: Math.max(0, Math.floor((WORLD_HALF - a.z) / s)), y1: Math.min(n - 1, Math.floor((WORLD_HALF - a.z0) / s)),
    };
  }

  // What to load: the screen and a tile around it at the drawn zoom, nearest the centre first, then
  // the whole padded canvas one zoom out (cheap, a quarter of the tiles), which is what a zoom-out
  // or a long drag shows first.
  need(v) {
    const tz = Math.max(0, Math.min(MAX_ZOOM, Math.round(v.zoom)));
    const s = v.screen ? v : screenArea(v), cx = (s.x + s.x1) / 2, cz = (s.z + s.z0) / 2, sp = span(tz);
    const r = this.range({ x: s.x - sp, x1: s.x1 + sp, z: s.z + sp, z0: s.z0 - sp }, tz);
    // called every frame of a zoom or drag: the same tiles as last time need no new queue
    const key = `${tz}/${r.x0}/${r.x1}/${r.y0}/${r.y1}`;
    if (key === this.needKey) { this.pump(); return; }
    this.needKey = key;
    this.queue.clear();
    for (let y = r.y0; y <= r.y1; y++)
      for (let x = r.x0; x <= r.x1; x++) {
        const mx = -WORLD_HALF + (x + 0.5) * sp, mz = WORLD_HALF - (y + 0.5) * sp;
        this.want(`${tz}/${x}/${y}`, Math.hypot(mx - cx, mz - cz) / sp);
      }
    if (tz > BASE_ZOOM) {
      const r2 = this.range(v, tz - 1);
      for (let y = r2.y0; y <= r2.y1; y++) for (let x = r2.x0; x <= r2.x1; x++) this.want(`${tz - 1}/${x}/${y}`, 1000);
    }
    this.pump();
  }

  // The map is still (ShapesCanvas.idle): load what the next zoom shows first, so it is here
  // already. A wheel zoom closes in on the cursor, so around the mouse (where it last rested on
  // the map, else the middle): the next level over half the screen, the two after over a quarter
  // and an eighth of it; then one level further out over the whole canvas. Decoded tiles are made
  // textures here too, not mid-zoom. true while there is more to do.
  prewarm(gl, v) {
    if (this.decoded.size) { this.upload(gl, false); return true; }
    if (this.queue.size || this.inflight.size) return true;   // the view's own tiles first
    const z = Math.max(0, Math.min(MAX_ZOOM, Math.round(v.zoom)));
    const s = screenArea(v), f = this.focus && this.focus.x > s.x && this.focus.x < s.x1 && this.focus.z < s.z && this.focus.z > s.z0 ? this.focus : null;
    const cx = f ? f.x : (s.x + s.x1) / 2, cz = f ? f.z : (s.z + s.z0) / 2, step = span(Math.min(MAX_ZOOM, z + 1));
    const key = `${z}/${Math.round(cx / step)}/${Math.round(cz / step)}`;
    if (this.prewarmKey === key) return false;
    this.prewarmKey = key;
    const around = (share, tz, pri) => {
      if (tz < BASE_ZOOM || tz > MAX_ZOOM) return;
      const w = (s.x1 - s.x) * share / 2, h = (s.z - s.z0) * share / 2, sp = span(tz);
      const r = this.range({ x: cx - w, x1: cx + w, z: cz + h, z0: cz - h }, tz);
      for (let y = r.y0; y <= r.y1; y++)
        for (let x = r.x0; x <= r.x1; x++) {
          const mx = -WORLD_HALF + (x + 0.5) * sp, mz = WORLD_HALF - (y + 0.5) * sp;
          this.want(`${tz}/${x}/${y}`, pri + Math.hypot(mx - cx, mz - cz) / sp);
        }
    };
    around(0.5, z + 1, 5000);
    around(0.25, z + 2, 6000);
    around(0.125, z + 3, 7000);
    around(1 + 2 * 0.75, z - 1, 8000);   // the padded canvas (shapes.js PAD)
    this.pump();
    return this.queue.size > 0 || this.inflight.size > 0;
  }

  // during a drag the canvas only redraws near its edge; keep loading what comes on screen meanwhile
  onMove() {
    if (this.moveQueued || this._map._animatingZoom) return;
    this.moveQueued = true;
    requestAnimationFrame(() => {
      this.moveQueued = false;
      if (!this._map || !this.sc.view) return;
      const map = this._map, b = map.getBounds();   // the screen itself (need() takes it as is)
      this.need(Object.assign({}, this.sc.view, { x: b.getWest(), x1: b.getEast(), z: b.getNorth(), z0: b.getSouth(), zoom: map.getZoom(), screen: true }));
    });
  }

  want(key, pri) {
    if (this.tex.has(key) || this.missing.has(key) || this.inflight.has(key) || this.decoded.has(key)) return;
    const old = this.queue.get(key);
    if (old === undefined || pri < old) this.queue.set(key, pri);
  }

  pump() {
    while (this.inflight.size < PARALLEL && this.queue.size) {
      let best = null, bp = Infinity;
      for (const [k, p] of this.queue) if (p < bp) { bp = p; best = k; }
      this.queue.delete(best);
      this.load(best);
    }
  }

  async load(key, replace) {
    this.inflight.add(key);
    try {
      // ask for WebP like an <img> does (fetch says */* and would get the PNG)
      const r = await fetch(this.url(key), { headers: { Accept: 'image/webp,image/png' }, priority: replace ? 'low' : 'auto' });
      if (r.status === 404) { this.missing.add(key); return; }
      if (!r.ok) throw new Error(r.status);
      const bmp = await createImageBitmap(await r.blob());   // decoded off the main thread
      if (!this.sc) { bmp.close(); return; }
      // handed to draw() to upload: a few a frame, not a whole new zoom level's worth at once
      this.decoded.set(key, bmp);
      // a tile of the zoom on screen (or the base level): draw it; one fetched ahead for another
      // zoom (prewarm) is only made a texture, while idle
      const z = +key.split('/')[0], v = this.sc.view;
      if (z === BASE_ZOOM || !v || z === Math.max(0, Math.min(MAX_ZOOM, Math.round(v.zoom)))) this.sc.redraw();
      this.sc.idleWork();
    } catch (e) {
      // network trouble: forget it, the next view change asks again
      this.needKey = null;
    } finally {
      this.inflight.delete(key);
      this.pump();
    }
  }

  // Upload decoded tiles: crossing into a new zoom level brings a dozen tiles at once, and turning
  // them all into textures in one frame was a visible stall mid-zoom. Two a frame while the map
  // moves (the stand-in from the level above shows meanwhile), more when it's still.
  upload(gl, moving) {
    let n = moving ? 2 : 8;
    for (const [key, bmp] of this.decoded) {
      if (n-- <= 0) { this.sc.redraw(); break; }
      this.decoded.delete(key);
      const old = this.tex.get(key);
      const tex = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, tex);
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, bmp);
      bmp.close();
      gl.generateMipmap(gl.TEXTURE_2D);   // smooth when shown smaller than 1:1 between zoom levels
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      if (old) gl.deleteTexture(old.tex);
      // a re-render swaps in place; a new tile fades in over whatever stood in for it
      this.tex.set(key, { tex, born: old ? 0 : performance.now(), seen: performance.now(), pinned: key.startsWith(`${BASE_ZOOM}/`) });
    }
    this.evict();
  }

  evict() {
    if (this.tex.size <= KEEP) return;
    const gl = this.sc.gl;
    const old = [...this.tex.entries()].filter(([, t]) => !t.pinned).sort((a, b) => a[1].seen - b[1].seen);
    for (let i = 0; i < old.length && this.tex.size > KEEP; i++) { gl.deleteTexture(old[i][1].tex); this.tex.delete(old[i][0]); }
  }

  // the server re-rendered these: fetch them again (fresh ?r=), keeping the old picture until then
  onRendered(keys) {
    const bust = Date.now();
    for (const k of keys || []) {
      this.rerendered.set(k, bust);
      const wasMissing = this.missing.delete(k);
      if (this.tex.has(k)) this.load(k, true);
      else if (wasMissing && this.sc && this.sc.view) { this.needKey = null; this.need(this.sc.view); }
    }
  }

  quad(gl, p, t, z, x, y, uv, alpha) {
    const s = span(z);
    gl.bindTexture(gl.TEXTURE_2D, t.tex);
    gl.uniform4f(p.u.u_rect, -WORLD_HALF + x * s, WORLD_HALF - y * s, s, s);
    gl.uniform4f(p.u.u_uv, uv[0], uv[1], uv[2], uv[3]);
    gl.uniform1f(p.u.u_alpha, alpha);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
  }

  draw(gl, v, sc) {
    if (this.decoded.size) this.upload(gl, !!(this._map && (this._map._gliding || this._map._animatingZoom || this._map._panAnim?._inProgress)));
    const p = sc.tex, now = performance.now();
    sc.setView(p);
    gl.bindVertexArray(sc.quadVao);
    gl.activeTexture(gl.TEXTURE0);
    gl.uniform1i(p.u.u_tex, 0);
    const tz = Math.max(0, Math.min(MAX_ZOOM, Math.round(v.zoom)));
    const r = this.range(v, tz);
    let fading = false;
    for (let y = r.y0; y <= r.y1; y++)
      for (let x = r.x0; x <= r.x1; x++) {
        const key = `${tz}/${x}/${y}`, t = this.tex.get(key);
        const f = t ? (t.born ? Math.min(1, (now - t.born) / FADE_MS) : 1) : 0;
        if (f < 1) {
          // stand-in: the nearest loaded ancestor, stretched, then any sharper children on top
          for (let l = 1; l <= tz; l++) {
            const a = this.tex.get(`${tz - l}/${x >> l}/${y >> l}`);
            if (!a) continue;
            const k = 1 << l, u0 = (x - ((x >> l) << l)) / k, v0 = (y - ((y >> l) << l)) / k;
            a.seen = now;
            this.quad(gl, p, a, tz, x, y, [u0, v0, u0 + 1 / k, v0 + 1 / k], 1);
            break;
          }
          if (tz < MAX_ZOOM)
            for (let dy = 0; dy < 2; dy++) for (let dx = 0; dx < 2; dx++) {
              const c = this.tex.get(`${tz + 1}/${2 * x + dx}/${2 * y + dy}`);
              if (c) { c.seen = now; this.quad(gl, p, c, tz + 1, 2 * x + dx, 2 * y + dy, [0, 0, 1, 1], 1); }
            }
        }
        if (t) {
          t.seen = now;
          if (f < 1) fading = true;
          this.quad(gl, p, t, tz, x, y, [0, 0, 1, 1], f);
        }
      }
    gl.bindVertexArray(null);
    if (fading) sc.redraw();
  }
}

// The fog veil (FogLayer's canvas, layers/fog.js) as a texture over everything in the canvas.
// A reveal re-uploads only the square it touched.
export class FogGL extends L.Layer {
  constructor(fog) {
    super();
    this.fog = fog;
    this.order = 9;
    fog.onPaint = (rect) => this.paint(rect);
  }
  onAdd(map) { this.sc = ShapesCanvas.for(map); this.sc.add(this); this.paint(null); this.fog.onStyle = () => this.sc.redraw(); }
  onRemove() { this.sc.remove(this); }
  need() {}

  paint(rect) {
    if (!this.sc) return;
    const gl = this.sc.gl, c = this.fog.canvas;
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
    if (!this.tex || !rect) {
      if (!this.tex) {
        this.tex = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, this.tex);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      }
      gl.bindTexture(gl.TEXTURE_2D, this.tex);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, c);
    } else {
      const x = Math.max(0, Math.floor(rect.x)), y = Math.max(0, Math.floor(rect.y));
      const w = Math.min(c.width - x, Math.ceil(rect.w) + 1), h = Math.min(c.height - y, Math.ceil(rect.h) + 1);
      if (w <= 0 || h <= 0) return;
      gl.bindTexture(gl.TEXTURE_2D, this.tex);
      gl.texSubImage2D(gl.TEXTURE_2D, 0, x, y, gl.RGBA, gl.UNSIGNED_BYTE, c.getContext('2d').getImageData(x, y, w, h));
    }
    this.sc.redraw();
  }

  draw(gl, v, sc) {
    const f = this.fog;
    if (!this.tex || !f.visible || f.opacity <= 0) return;
    const p = sc.tex, b = f.bounds;   // plain black veil (the clouds are only past the world's edge)
    sc.setView(p);
    gl.bindVertexArray(sc.quadVao);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.tex);
    gl.uniform1i(p.u.u_tex, 0);
    gl.uniform4f(p.u.u_rect, b.getWest(), b.getNorth(), b.getEast() - b.getWest(), b.getNorth() - b.getSouth());
    gl.uniform4f(p.u.u_uv, 0, 0, 1, 1);
    gl.uniform1f(p.u.u_alpha, f.opacity);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    gl.bindVertexArray(null);
  }
}

// Everything past the world's edge (radius 10 km) in a dark ocean colour with a soft rim, over the
// fog, so the round world reads as a world and not as fog that goes on forever.
const rgb = (hex) => { const n = parseInt(hex.slice(1), 16); return [(n >> 16) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255]; };
export class WorldEdgeGL extends L.Layer {
  constructor(radius) { super(); this.radius = radius; this.order = 10; }
  onAdd(map) {
    this.sc = ShapesCanvas.for(map); this.sc.add(this);
    // the game's own map backdrop, when the server could take it from the game files; the clouds
    // drawn here until it arrives, or for good when it can't
    if (this.space === undefined) {
      this.space = null;
      const img = new Image();
      img.onload = () => {
        const gl = this.sc.gl, t = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, t);
        gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, img);
        gl.generateMipmap(gl.TEXTURE_2D);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
        this.space = t;
        this.sc.redraw();
      };
      // 1 MB: asked for once the page is idle and at low priority, so it never holds up the map tiles
      img.fetchPriority = 'low';
      const go = () => { img.src = 'icons/game/mapbg_spacetex.png'; };
      if (window.requestIdleCallback) requestIdleCallback(go, { timeout: 6000 }); else setTimeout(go, 3000);
    }
  }
  onRemove() { this.sc.remove(this); }
  need() {}
  draw(gl, v, sc) {
    const space = this.space && sc.edgeSpace ? this.space : null;
    const clouds = space ? null : sc.cloudTexture();
    const p = space ? sc.edgeSpace : clouds ? sc.edge : sc.edgeLive;
    sc.setView(p);
    gl.activeTexture(gl.TEXTURE0);
    if (space) { gl.bindTexture(gl.TEXTURE_2D, space); gl.uniform1i(p.u.u_space, 0); }
    else if (clouds) { gl.bindTexture(gl.TEXTURE_2D, clouds); gl.uniform1i(p.u.u_clouds, 0); gl.uniform1f(p.u.u_extent, CLOUD_EXTENT); }
    gl.bindVertexArray(sc.quadVao);
    gl.uniform4f(p.u.u_rect, v.x, v.z, v.x1 - v.x, v.z - v.z0);   // the whole canvas
    gl.uniform1f(p.u.u_radius, this.radius);
    gl.uniform1f(p.u.u_mpp, 1 / v.ppm);
    gl.uniform3f(p.u.u_ring, ...rgb(OUTSIDE_RIM));
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    gl.bindVertexArray(null);
  }
}

// The chunk grid (with each chunk's corner coordinates) and the rings every 500 m around the spawn
// (labelled each kilometre), drawn in the WebGL canvas over the fog. They used to be a Leaflet grid
// layer under the canvas (hidden) and twelve SVG circles redrawn every frame of a zoom (slow).
const textCache = new Map();
function textImage(s, color) {
  const k = `${s}|${color}`;
  let c = textCache.get(k);
  if (c) return c;
  const R = 3;   // drawn 3x and scaled down: sharp at any zoom step
  c = document.createElement('canvas');
  const g = c.getContext('2d'), font = `600 ${10 * R}px ui-monospace, monospace`;
  g.font = font;
  const w = Math.ceil(g.measureText(s).width) + 4 * R;
  c.width = w; c.height = 14 * R;
  g.font = font; g.textBaseline = 'middle';
  g.lineWidth = 3 * R; g.strokeStyle = 'rgba(0,0,0,.65)'; g.lineJoin = 'round';
  g.strokeText(s, 2 * R, 7 * R);
  g.fillStyle = color; g.fillText(s, 2 * R, 7 * R);
  c.w = w / R; c.h = 14;
  if (textCache.size > 600) textCache.clear();
  textCache.set(k, c);
  return c;
}

export class GuideGL extends L.Layer {
  constructor(radius, color) { super(); this.radius = radius; this.color = color; this.order = 9.5; this.grid = false; this.rings = null; this.tex = new Map(); }
  onAdd(map) { this.sc = ShapesCanvas.for(map); this.sc.add(this); }
  onRemove() { this.sc.remove(this); }
  need() {}
  set(grid, spawn) {   // spawn: {x, z} for the rings, or null
    this.grid = grid; this.rings = spawn;
    if (this.sc) this.sc.redraw();
  }
  texture(gl, cv) {
    let t = this.tex.get(cv);
    if (t) return t;
    if (this.tex.size > 600) { for (const x of this.tex.values()) gl.deleteTexture(x); this.tex.clear(); }
    t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, cv);
    gl.generateMipmap(gl.TEXTURE_2D);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    this.tex.set(cv, t);
    return t;
  }
  draw(gl, v, sc) {
    if (!this.grid && !this.rings) return;
    const p = sc.guide;
    sc.setView(p);
    gl.bindVertexArray(sc.quadVao);
    gl.uniform4f(p.u.u_rect, v.x, v.z, v.x1 - v.x, v.z - v.z0);
    gl.uniform1f(p.u.u_mpp, 1 / v.ppm);
    gl.uniform1f(p.u.u_radius, this.radius);
    gl.uniform1f(p.u.u_grid, this.grid ? 1 : 0);
    gl.uniform1f(p.u.u_rings, this.rings ? 1 : 0);
    gl.uniform2f(p.u.u_spawn, this.rings ? this.rings.x : 0, this.rings ? this.rings.z : 0);
    const n = parseInt(this.color.slice(1), 16);
    gl.uniform3f(p.u.u_ringColor, (n >> 16) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);

    // labels
    const t = sc.tex;
    sc.setView(t);
    gl.activeTexture(gl.TEXTURE0);
    gl.uniform1i(t.u.u_tex, 0);
    gl.uniform4f(t.u.u_uv, 0, 0, 1, 1);
    const label = (cv, x, z, dx, dy, alpha) => {   // top-left of the text dx, dy px from world (x, z)
      gl.uniform1f(t.u.u_alpha, alpha);
      gl.bindTexture(gl.TEXTURE_2D, this.texture(gl, cv));
      gl.uniform4f(t.u.u_rect, x + dx / v.ppm, z - dy / v.ppm, cv.w / v.ppm, cv.h / v.ppm);
      gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    };
    if (this.grid) {
      // each chunk's north-west corner, as the old grid tiles had it; every 2nd, 4th... chunk when
      // they would crowd (at least ~150 px apart)
      const px = 256 * v.ppm;
      if (px >= 20) {
        let step = 256;
        while (step * v.ppm < 150) step *= 2;
        const a = Math.min(1, (px - 20) / 30) * 0.8;
        for (let x = Math.floor(v.x / step) * step; x <= v.x1; x += step)
          for (let z = Math.ceil(v.z / step) * step; z >= v.z0; z -= step) {
            if (Math.hypot(x, z) > this.radius) continue;
            label(textImage(`${x}, ${z}`, 'rgba(255,255,255,.75)'), x, z, 3, 2, a);
          }
      }
    }
    if (this.rings) {
      // a label at the north of each kilometre ring, while that point is inside the world
      // every kilometre, and the outermost ring whatever it is (9.5 km when the spawn sits off centre)
      const last = Math.floor((this.radius - Math.hypot(this.rings.x, this.rings.z)) / 500) * 500;
      for (let r = 1000; r <= last; r += 500) {
        if (r % 1000 !== 0 && r !== last) continue;
        const cv = textImage(`${r % 1000 === 0 ? r / 1000 : (r / 1000).toFixed(1)} km`, this.color);
        label(cv, this.rings.x, this.rings.z + r, -cv.w / 2, -cv.h / 2, 1);
      }
    }
    gl.uniform1f(t.u.u_alpha, 1);
    gl.bindVertexArray(null);
  }
}

// The "Biomes" map style: each biome as a solid colour with its name (the server's World/BiomeMap,
// only explored ground in the picture), drawn over the ground and under trees, buildings and fog.
// Labels are bigger and fade out when zoomed far in (the colour already says it by then).
function bigText(s, color) {
  const R = 3, c = document.createElement('canvas'), g = c.getContext('2d'), font = `700 ${13 * R}px system-ui, sans-serif`;
  g.font = font;
  const w = Math.ceil(g.measureText(s).width) + 8 * R;
  c.width = w; c.height = 20 * R;
  g.font = font; g.textBaseline = 'middle'; g.lineJoin = 'round';
  g.lineWidth = 4 * R; g.strokeStyle = 'rgba(0,0,0,.7)'; g.strokeText(s, 4 * R, 10 * R);
  g.fillStyle = color; g.fillText(s, 4 * R, 10 * R);
  c.w = w / R; c.h = 20;
  return c;
}

export class BiomeGL extends L.Layer {
  constructor() { super(); this.order = 0.5; this.on = false; this.labels = []; this.colours = {}; this.texts = new Map(); }
  onAdd(map) { this.sc = ShapesCanvas.for(map); this.sc.add(this); }
  onRemove() { this.sc.remove(this); }
  need() {}
  setOn(on) {
    this.on = on;
    if (on) this.refresh();
    clearInterval(this.timer);
    if (on) this.timer = setInterval(() => this.refresh(), 120000);   // exploration grows: the picture shows more
    if (this.sc) this.sc.redraw();
  }
  async refresh() {
    try {
      const d = await (await fetch('data/biomes.json', { cache: 'no-store' })).json();
      this.labels = d.labels || []; this.colours = d.colours || {}; this.size = d.world || 21504;
      if (this.onColours) this.onColours(this.colours);
      if (!d.ready) { this.sc && this.sc.redraw(); return; }
      const r = await fetch(worldTile('data/biomes.png'), { cache: 'no-cache' });
      if (!r.ok) return;
      const bmp = await createImageBitmap(await r.blob());
      const gl = this.sc.gl;
      if (!this.tex) this.tex = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, this.tex);
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, bmp);
      gl.generateMipmap(gl.TEXTURE_2D);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      this.sc.redraw();
    } catch (e) { /* try again next time */ }
  }
  text(s) {
    let t = this.texts.get(s);
    if (!t) { t = bigText(s, '#ffffff'); this.texts.set(s, t); }
    return t;
  }
  draw(gl, v, sc) {
    if (!this.on || !this.tex) return;
    const p = sc.tex, H = (this.size || 21504) / 2;
    sc.setView(p);
    gl.bindVertexArray(sc.quadVao);
    gl.activeTexture(gl.TEXTURE0);
    gl.uniform1i(p.u.u_tex, 0);
    gl.bindTexture(gl.TEXTURE_2D, this.tex);
    gl.uniform4f(p.u.u_rect, -H, H, 2 * H, 2 * H);
    gl.uniform4f(p.u.u_uv, 0, 0, 1, 1);
    gl.uniform1f(p.u.u_alpha, 0.92);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    // names: the biggest regions first, none on top of another, fading out past zoom 6.5
    const fade = Math.max(0, Math.min(1, (7 - v.zoom) / 0.5));
    if (fade > 0) {
      const taken = [];
      for (const l of [...this.labels].sort((a, b) => b.area - a.area)) {
        const cv = this.text(l.name);
        const x = (l.x - v.x) * v.ppm - cv.w / 2, y = (v.z - l.z) * v.ppm - cv.h / 2;
        if (taken.some((t) => x < t[2] && x + cv.w > t[0] && y < t[3] && y + cv.h > t[1])) continue;
        taken.push([x - 6, y - 4, x + cv.w + 6, y + cv.h + 4]);
        if (!this.texCache) this.texCache = new Map();
        let t = this.texCache.get(cv);
        if (!t) {
          t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t);
          gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
          gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, cv);
          gl.generateMipmap(gl.TEXTURE_2D);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
          this.texCache.set(cv, t);
        }
        gl.bindTexture(gl.TEXTURE_2D, t);
        gl.uniform4f(p.u.u_rect, v.x + x / v.ppm, v.z - y / v.ppm, cv.w / v.ppm, cv.h / v.ppm);
        gl.uniform1f(p.u.u_alpha, fade);
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
      }
      gl.uniform1f(p.u.u_alpha, 1);
    }
    gl.bindVertexArray(null);
  }
}

// The Mistlands mist, the way the game's map has it: the server's mask (data/mist.png, explored
// ground only, from the game's own map mask), drawn over trees and buildings and under the fog.
// A player wearing a Wisplight clears it around them, as far as the wisp does in game (the server
// sends each one's range as "wisp"). Hidden players clear nothing here, like they show nothing.
export class MistGL extends L.Layer {
  constructor() {
    super();
    this.order = 8.5; this.on = false; this.wisps = [];
    on('players', (f) => this.setWisps((f.data && f.data.players) || []));
  }
  onAdd(map) { this.sc = ShapesCanvas.for(map); this.sc.add(this); if (this.on) this.later(); }
  later() {
    if (this.tex || this.waiting) return;
    this.waiting = true;
    const go = () => { this.waiting = false; this.refresh(); };
    if (window.requestIdleCallback) requestIdleCallback(go, { timeout: 4000 }); else setTimeout(go, 1500);
  }
  onRemove() { this.sc.remove(this); }
  need() {}
  setOn(v) {
    this.on = v;
    clearInterval(this.timer);
    // asked for once the page is idle and at low priority (a few tens of KB), so it never holds up
    // the map tiles; again every two minutes as exploration grows (a 304 when it hasn't)
    if (v) { this.later(); this.timer = setInterval(() => this.refresh(), 120000); }
    if (this.sc) this.sc.redraw();
  }
  setWisps(players) {
    const w = players.filter((p) => p.wisp > 0 && p.x !== undefined && !p.dead).slice(0, 16).map((p) => [p.x, p.z, p.wisp]);
    const key = w.join(';');
    if (key === this.wispKey) return;
    this.wispKey = key; this.wisps = w;
    if (this.on && this.sc) this.sc.redraw();
  }
  async refresh() {
    if (!this.sc || !this.sc.mist || !this.sc.mistBake) return;
    try {
      const got = await fetchMist(this.tag);
      if (!got) return;   // the same mask: the baked mist stands
      this.tag = got.tag; this.blocks = got.blocks;
      const bmp = got.bmp;
      this.bake(bmp);
      this.sc.redraw();
    } catch (e) { /* try again next time */ }
  }
  // the mist worked out once into a MIST_RES texture over the world's square (row 0 the south edge)
  bake(bmp) {
    const sc = this.sc, gl = sc.gl, p = sc.mistBake;
    for (let u = 0; u < 4; u++) { gl.activeTexture(gl.TEXTURE0 + u); gl.bindTexture(gl.TEXTURE_2D, null); }
    gl.activeTexture(gl.TEXTURE0);
    const mask = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, mask);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, bmp);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    if (!this.tex) {
      this.tex = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, this.tex);
      gl.texStorage2D(gl.TEXTURE_2D, 1, gl.RGBA8, MIST_RES, MIST_RES);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    }
    const fb = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, this.tex, 0);
    gl.viewport(0, 0, MIST_RES, MIST_RES);
    gl.disable(gl.BLEND);
    gl.useProgram(p.p);
    gl.bindTexture(gl.TEXTURE_2D, mask);
    gl.uniform1i(p.u.u_mask, 0);
    gl.uniform1f(p.u.u_extent, WORLD_HALF);
    gl.bindVertexArray(sc.quadVao);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    gl.bindVertexArray(null);
    gl.enable(gl.BLEND);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.deleteFramebuffer(fb);
    gl.deleteTexture(mask);
    gl.viewport(0, 0, sc.canvas.width, sc.canvas.height);
  }
  draw(gl, v, sc) {
    const p = sc.mist;
    if (!this.on || !this.tex || !p) return;
    const H = WORLD_HALF;
    sc.setView(p);
    gl.bindVertexArray(sc.quadVao);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.tex);
    gl.uniform1i(p.u.u_mist, 0);
    gl.uniform1f(p.u.u_half, H);
    gl.uniform1f(p.u.u_mpp, 1 / v.ppm);
    const a = this.wispBuf || (this.wispBuf = new Float32Array(64));
    a.fill(0);
    this.wisps.forEach((w, i) => { a[i * 4] = w[0]; a[i * 4 + 1] = w[1]; a[i * 4 + 2] = w[2]; });
    gl.uniform4fv(p.u['u_wisp[0]'], a);
    gl.uniform1i(p.u.u_nwisp, this.wisps.length);
    // only the blocks of the world with mist in them, and of those only what is on screen: the
    // shader runs per pixel drawn, and most views have no Mistlands in them at all
    const B = MIST_BLOCKS, span = (2 * H) / B, bl = this.blocks;
    const c0 = Math.max(0, Math.floor((v.x + H) / span)), c1 = Math.min(B - 1, Math.floor((v.x1 + H) / span));
    const r0 = Math.max(0, Math.floor((H - v.z) / span)), r1 = Math.min(B - 1, Math.floor((H - v.z0) / span));
    for (let r = r0; r <= r1; r++) {
      for (let c = c0; c <= c1; c++) {
        if (!bl[r * B + c]) continue;
        let e = c;
        while (e < c1 && bl[r * B + e + 1]) e++;   // a run of them in the row: one rectangle
        const x0 = Math.max(v.x, -H + c * span), x1 = Math.min(v.x1, -H + (e + 1) * span);
        const zt = Math.min(v.z, H - r * span), zb = Math.max(v.z0, H - (r + 1) * span);
        gl.uniform4f(p.u.u_rect, x0, zt, x1 - x0, zt - zb);
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
        c = e;
      }
    }
    gl.bindVertexArray(null);
  }
}
