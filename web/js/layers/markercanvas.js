// Map markers, their labels and the portal lines, painted on one canvas over the map, redrawn in
// the same event that moves the map (the way valheim.tools draws its pins). With a Leaflet DOM
// element per marker, a zoom or drag moved hundreds of elements each frame, and they could sit a
// frame or half a pixel off the map underneath. Hover and click find the nearest icon.

import { iconSvg, colors } from '../icons.js';
import { ShapesCanvas } from './shapes.js';
import { metersPerPixel } from '../crs.js';
import { layerState } from '../layerstate.js';

const LABEL_FONT = '600 11px';
const iconCache = new Map();   // "name|color" -> canvas with the icon and its drop shadow, or 'loading'
const LABEL_FADE_MS = 180;
const SETTLE_MS = 250;
const LABEL_ZOOM = 5;          // below this only the always-on labels (spawn, bosses, traders, mini bosses)
const GROUP_ZOOM = 6;          // below this, same-kind markers that overlap on screen draw as one with a count
const GROUP_PX = 22;           // how close (screen px) two markers must be to group         // a label knocked out during a zoom may come back once the map has been still this long
const ICON_RES = 72;           // each icon is rendered once this big and drawn scaled to any size:
                               // whole-pixel sizes stepped visibly as icons grew during a zoom

// a small round badge with a number, for a group of markers
const badgeCache = new Map();
function countBadge(s) {
  let c = badgeCache.get(s);
  if (c) return c;
  const R = 3, h = 14, w = Math.max(14, 6 + s.length * 7);
  c = document.createElement('canvas'); c.width = w * R; c.height = h * R;
  const g = c.getContext('2d'); g.scale(R, R);
  g.fillStyle = 'rgba(14,18,24,.92)'; g.strokeStyle = 'rgba(255,255,255,.75)'; g.lineWidth = 1.2;
  g.beginPath(); g.roundRect(0.6, 0.6, w - 1.2, h - 1.2, 7); g.fill(); g.stroke();
  g.fillStyle = '#fff'; g.font = '700 9.5px system-ui, sans-serif'; g.textAlign = 'center'; g.textBaseline = 'middle';
  g.fillText(s, w / 2, h / 2 + 0.5);
  c.w = w; c.h = h;
  badgeCache.set(s, c);
  return c;
}

// name: one of our SVG icons, or img: the URL of one of the game's own map icons (World/MapIcons),
// which get a dark round badge with a ring in the marker's colour behind them
// "dots" style: a plain dot in the marker's colour, but pins and the few always-labelled markers
// (spawn, bosses, traders) keep their icons
const asDot = (it) => layerState.iconDots && !it.pin && !it.always;

function iconImage(name, color, onReady, img, gamePin) {
  if (gamePin && img) return gamePinImage(img, gamePin, onReady);
  const key = img ? `${img}|${color}` : `${name}|${color}`;
  const c = iconCache.get(key);
  if (c && c !== 'loading') return c;
  if (!c) {
    iconCache.set(key, 'loading');
    const im = new Image();
    im.onload = () => {
      const k = ICON_RES / 26, pad = 4 * k;   // shadow and padding in proportion to a 26 px icon
      const cv = document.createElement('canvas');
      cv.width = cv.height = Math.ceil(ICON_RES + pad * 2);
      const g = cv.getContext('2d');
      g.shadowColor = 'rgba(0,0,0,.8)'; g.shadowBlur = 2 * k; g.shadowOffsetY = 1 * k;   // like the old CSS drop-shadow
      let box = ICON_RES;
      if (img) {
        const c = pad + ICON_RES / 2, r = ICON_RES / 2 - 2 * k;
        g.beginPath(); g.arc(c, c, r, 0, Math.PI * 2);
        g.fillStyle = 'rgba(14,18,24,.82)'; g.fill();
        g.shadowColor = 'transparent';
        g.lineWidth = 2 * k; g.strokeStyle = color || '#c8cdd6'; g.stroke();
        g.shadowColor = 'rgba(0,0,0,.8)';
        box = ICON_RES * 0.62;   // the icon inside the ring
      }
      // fit inside the box, keeping the shape (the game's icons aren't all square)
      const s = box / Math.max(im.naturalWidth || box, im.naturalHeight || box);
      const w = (im.naturalWidth || box) * s, h = (im.naturalHeight || box) * s;
      g.imageSmoothingQuality = 'high';
      g.drawImage(im, pad + (ICON_RES - w) / 2, pad + (ICON_RES - h) / 2, w, h);
      cv.padShare = pad / ICON_RES;
      iconCache.set(key, cv);
      onReady();
    };
    im.onerror = () => iconCache.delete(key);
    im.src = img || 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(iconSvg(name, color).replace('<svg ', `<svg width="${ICON_RES}" height="${ICON_RES}" `));
  }
  return null;
}

// A pin from a cartography table drawn like the game's map draws one shared by someone else
// (Minimap.UpdatePins): its own sprite, no badge; crossed out, the
// game's red cross over it. (The game's 70% grey at 80% works on its paper map but was lost over
// this one's trees and boulders: solid white here, with a dark outline and shadow.)
// gamePin: {checked: URL of the cross sprite, or true to draw one}.
function gamePinImage(img, gamePin, onReady) {
  const cross = gamePin.checked, key = `pin|${img}|${cross || ''}`;
  const c = iconCache.get(key);
  if (c && c !== 'loading') return c;
  if (c) return null;
  iconCache.set(key, 'loading');
  const load = (src) => new Promise((ok, no) => { const im = new Image(); im.onload = () => ok(im); im.onerror = no; im.src = src; });
  Promise.all([load(img), typeof cross === 'string' ? load(cross) : null]).then(([im, x]) => {
    const pad = 4 * ICON_RES / 26, cv = document.createElement('canvas');   // room for the shadow
    cv.width = cv.height = Math.ceil(ICON_RES + pad * 2);
    const g = cv.getContext('2d');
    g.imageSmoothingQuality = 'high';
    const fit = (m) => { const k = ICON_RES / Math.max(m.naturalWidth || ICON_RES, m.naturalHeight || ICON_RES); return [(m.naturalWidth || ICON_RES) * k, (m.naturalHeight || ICON_RES) * k]; };
    const [w, h] = fit(im);
    const sprite = document.createElement('canvas');   // the tinted sprite on its own, then shadowed onto the icon
    sprite.width = sprite.height = cv.width;
    const sg = sprite.getContext('2d');
    sg.imageSmoothingQuality = 'high';
    sg.drawImage(im, pad + (ICON_RES - w) / 2, pad + (ICON_RES - h) / 2, w, h);
    // a dark outline (the sprite's shape in black, nudged all round), so a white pin stands out
    // from the grey boulders and the trees it sits on, then the shadow, then the sprite
    const k = ICON_RES / 26, out = document.createElement('canvas');
    out.width = out.height = cv.width;
    const og = out.getContext('2d');
    for (let a = 0; a < 8; a++) og.drawImage(sprite, Math.cos(a * Math.PI / 4) * 1.6 * k, Math.sin(a * Math.PI / 4) * 1.6 * k);
    og.globalCompositeOperation = 'source-in';
    og.fillStyle = 'rgba(10,12,16,.92)'; og.fillRect(0, 0, out.width, out.height);
    g.shadowColor = 'rgba(0,0,0,.7)'; g.shadowBlur = 2 * k; g.shadowOffsetY = 1 * k;
    g.drawImage(out, 0, 0);
    g.shadowColor = 'transparent';
    g.drawImage(sprite, 0, 0);
    if (x) { const [xw, xh] = fit(x); g.drawImage(x, pad + (ICON_RES - xw) / 2, pad + (ICON_RES - xh) / 2, xw, xh); }
    else if (cross) {   // no sprite from the game: a red cross like it
      const a = pad + ICON_RES * 0.2, b = pad + ICON_RES * 0.8;
      g.strokeStyle = '#d0201a'; g.lineWidth = ICON_RES * 0.12; g.lineCap = 'round';
      g.beginPath(); g.moveTo(a, a); g.lineTo(b, b); g.moveTo(b, a); g.lineTo(a, b); g.stroke();
    }
    cv.padShare = pad / ICON_RES;
    iconCache.set(key, cv);
    onReady();
  }).catch(() => iconCache.delete(key));
  return null;
}

// Labels are rendered once into small images and drawn like the icons: canvas text is snapped to
// whole pixels by the browser, so while the map glided the labels stepped beside their icons.
const labelCache = new Map();   // text -> canvas
const labelWidths = new Map();  // text -> width in CSS px (measured without making the picture)
let measure = null, labelBudget = Infinity;
// A label's width, for working out where labels fit: a measureText, no canvas made
function labelWidth(text, font) {
  const c = labelCache.get(text);
  if (c) return c.w;
  let w = labelWidths.get(text);
  if (w === undefined) {
    if (!measure) measure = document.createElement('canvas').getContext('2d');
    measure.font = `${LABEL_FONT} ${font}`;
    w = Math.ceil(measure.measureText(text).width) + 6;
    if (labelWidths.size > 4000) labelWidths.clear();
    labelWidths.set(text, w);
  }
  return w;
}
// The label's picture, made the first time it's drawn. Only a few new ones a frame (labelBudget,
// set by the draw): crossing zoom 5 used to make every label's picture and texture in one frame,
// a visible stall; now they come in over the next few frames with their fade.
function labelImage(text, font, color = '#fff') {
  const ck = color === '#fff' ? text : `${color}|${text}`;
  let c = labelCache.get(ck);
  if (c) return c;
  if (labelBudget <= 0) return null;
  labelBudget--;
  const k = 3;   // rendered at 3x, drawn at 1x: sharp on any screen
  const w = labelWidth(text, font), h = 16;
  c = document.createElement('canvas');
  c.width = w * k; c.height = h * k;
  const g = c.getContext('2d');
  g.scale(k, k);
  g.font = `${LABEL_FONT} ${font}`; g.textAlign = 'center'; g.textBaseline = 'top'; g.lineJoin = 'round';
  g.lineWidth = 3; g.strokeStyle = 'rgba(0,0,0,.85)'; g.strokeText(text, w / 2, 1);
  g.fillStyle = color; g.fillText(text, w / 2, 1);
  c.w = w; c.h = h;
  if (labelCache.size > 2000) labelCache.clear();
  labelCache.set(ck, c);
  return c;
}

// source() -> {items: [{x, z, icon, color, label, pin, open(latlng)}], lines: [[x1, z1, x2, z2]], labels: bool}
export class MarkerCanvas extends L.Layer {
  constructor(source, opts = {}) {
    super();
    this.source = source;
    this.hover = null;
    this.useGL = !!opts.gl;
  }

  onAdd(map) {
    if (this.useGL) this.gl = new MarkerGL(this).addTo(map);
    this.canvas = L.DomUtil.create('canvas', 'leaflet-zoom-animated marker-canvas', map.getPane('markerPane'));
    this.canvas.style.pointerEvents = 'none';
    this.font = getComputedStyle(document.body).fontFamily || 'sans-serif';
    map.on('viewreset resize moveend', this.draw, this);
    map.on('move', this.onMove, this);
    map.on('zoomanim', this.onZoomAnim, this);
    map.on('mousemove', this.onMouseMove, this);
    map.on('mouseout', this.onMouseOut, this);
    map.on('click', this.onClick, this);
    this.draw();
  }

  onRemove(map) {
    map.off('viewreset resize moveend', this.draw, this);
    map.off('move', this.onMove, this);
    map.off('zoomanim', this.onZoomAnim, this);
    map.off('mousemove', this.onMouseMove, this);
    map.off('mouseout', this.onMouseOut, this);
    map.off('click', this.onClick, this);
    this.canvas.remove();
  }

  // A drag: the WebGL canvas moves with the map and has every marker on it out to its padding, so
  // nothing to draw (it re-centres itself when needed); only the 2D fallback follows the screen.
  // (Redrawing on every move redrew the whole map canvas each frame of a drag.)
  onMove() { if (!this.gl) this.draw(); }

  // Leaflet's own animated zooms (keyboard): scale the last picture along, like its vector renderer
  onZoomAnim(e) {
    if (!this.topLeft) return;
    const map = this._map;
    L.DomUtil.setTransform(this.canvas, map._latLngToNewLayerPoint(this.topLeft, e.zoom, e.center), map.getZoomScale(e.zoom, this.drawZoom));
  }

  // the markers to draw, with where they are on screen (container pixels)
  placed() {
    const map = this._map, size = map.getSize(), out = [];
    for (const it of this.src.items) {
      const p = map.latLngToContainerPoint([it.z, it.x]);
      if (p.x < -40 || p.y < -40 || p.x > size.x + 40 || p.y > size.y + 40) continue;
      out.push({ it, p });
    }
    return out;
  }

  draw() {
    const map = this._map;
    if (!map) return;
    this.src = this.source();
    if (this.gl) { this.gl.changed(); return; }   // drawn in the WebGL canvas (MarkerGL)
    const size = map.getSize(), dpr = Math.min(window.devicePixelRatio || 1, 3), cv = this.canvas;
    if (cv.width !== Math.round(size.x * dpr) || cv.height !== Math.round(size.y * dpr)) {
      cv.width = Math.round(size.x * dpr); cv.height = Math.round(size.y * dpr);
      cv.style.width = size.x + 'px'; cv.style.height = size.y + 'px';
    }
    // Keep the canvas on the container's corner while the map pane moves under it. The canvas
    // itself sits on whole pixels (a layer at a fractional offset gets snapped by the browser, a
    // little differently each frame, and the icons wobbled during a glide); the fraction is
    // drawn into the picture instead, so every icon lands exactly on its spot.
    const at = map.containerPointToLayerPoint([0, 0]), whole = at.round();
    L.DomUtil.setPosition(cv, whole);
    this.topLeft = map.containerPointToLatLng([0, 0]); this.drawZoom = map.getZoom();
    const g = cv.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, (at.x - whole.x) * dpr, (at.y - whole.y) * dpr);
    g.clearRect(-2, -2, size.x + 4, size.y + 4);
    const redraw = () => this.draw();

    // wards' protected ground (MarkerGL draws the same)
    const ppm = 1 / metersPerPixel(map.getZoom());
    for (const it of this.src.items) {
      if (!it.area || it.area.r * ppm < 6) continue;
      const c = map.latLngToContainerPoint([it.z, it.x]), R = it.area.r * ppm;
      if (c.x < -R || c.y < -R || c.x > size.x + R || c.y > size.y + R) continue;
      g.save();
      g.strokeStyle = g.fillStyle = it.area.on ? colors.ward : colors.wardoff;
      g.beginPath(); g.arc(c.x, c.y, R, 0, Math.PI * 2);
      g.globalAlpha = it.area.on ? 0.07 : 0.04; g.fill();
      g.globalAlpha = it.area.on ? 0.85 : 0.6; g.lineWidth = 1.5; g.setLineDash([2 * Math.PI * R / 40 * 0.55, 2 * Math.PI * R / 40 * 0.45]); g.stroke();
      g.restore();
    }

    // portal lines under everything
    if (this.src.lines.length) {
      g.save();
      g.strokeStyle = colors.portal; g.globalAlpha = 0.6; g.lineWidth = 1.5; g.setLineDash([4, 6]);
      g.beginPath();
      for (const [x1, z1, x2, z2] of this.src.lines) {
        const a = map.latLngToContainerPoint([z1, x1]), b = map.latLngToContainerPoint([z2, x2]);
        g.moveTo(a.x, a.y); g.lineTo(b.x, b.y);
      }
      g.stroke();
      g.restore();
    }

    // icons grow in smoothly with zoom (like valheim.tools' badges), at any in-between size
    const placed = this.placed(), grow = Math.max(16, Math.min(26, 16 + (map.getZoom() - 2) * 4)) * layerState.iconScale;
    g.imageSmoothingQuality = 'high';
    for (let i = placed.length - 1; i >= 0; i--) {   // last-first: the list's first end up on top (as in MarkerGL)
      const { it, p } = placed[i];
      const dot = asDot(it), px = it.pin ? 18 * layerState.iconScale : dot ? grow * 0.55 : grow;
      const im = dot ? iconImage('dot', it.color, redraw) : iconImage(it.icon, it.color, redraw, it.img, it.gamePin);
      if (!im) continue;
      // markers sit centred on their spot, pins stand on it
      const pad = px * im.padShare, x = p.x - px / 2 - pad, y = (it.pin ? p.y - px : p.y - px / 2) - pad;
      g.drawImage(im, x, y, px + pad * 2, px + pad * 2);
    }

    // labels: under the icon, white with a dark edge; one that would overlap a label already
    // shown is left out (hovering its icon shows it), and none below zoom 4 but the hovered one
    const showAll = this.src.labels && map.getZoom() >= 4, taken = [];
    const label = (it, p, force) => {
      if (!it.label) return;
      const im = labelImage(it.label, this.font, it.labelColor), top = it.pin ? p.y + 1 : p.y + grow / 2;
      const r = { x0: p.x - im.w / 2, x1: p.x + im.w / 2, y0: top, y1: top + 14 };
      if (!force && taken.some((q) => r.x0 < q.x1 && r.x1 > q.x0 && r.y0 < q.y1 && r.y1 > q.y0)) return;
      taken.push(r);
      g.drawImage(im, p.x - im.w / 2, top, im.w, im.h);
      return true;
    };
    const hovered = this.hover && placed.find((q) => q.it === this.hover);
    if (hovered) label(hovered.it, hovered.p, true);
    // Which labels show is decided when the map is still and kept for a whole glide: decided
    // every frame, labels popped on and off as their neighbours shifted, which read as shaking.
    if (map._gliding && this.shown) {
      for (const { it, p } of placed) if (it !== this.hover && this.shown.has(it)) label(it, p, true);
    } else {
      this.shown = new Set();
      if (showAll) for (const { it, p } of placed) if (it !== this.hover && label(it, p, false)) this.shown.add(it);
    }
  }

  // the marker under a container point, if any
  hit(cp) {
    let best = null, bd = 15;
    for (const { it, p } of this.placed()) {
      const cy = it.pin ? p.y - 9 : p.y;
      const d = Math.hypot(p.x - cp.x, cy - cp.y);
      if (d < bd) { bd = d; best = it; }
    }
    return best;
  }

  onMouseMove(e) {
    if (!this.src) return;
    if (this._map.dragging && this._map.dragging.moving()) return;   // passing over icons mid-drag isn't hovering
    const h = this.hit(e.containerPoint);
    if (h !== this.hover) {
      this.hover = h;
      this._map.getContainer().classList.toggle('marker-hover', !!h);
      this.draw();
    }
  }

  onMouseOut() { if (this.hover) { this.hover = null; this._map.getContainer().classList.remove('marker-hover'); this.draw(); } }

  onClick(e) {
    if (!this.src) return;
    const it = this.hit(e.containerPoint);
    if (it && it.open) it.open(L.latLng(it.z, it.x));
  }
}

// The same markers drawn in the WebGL canvas with the map (layers/shapes.js), so they are in the
// very picture the map is: on a 2D canvas of their own the browser could show their new
// positions a frame after the map's, and during a zoom the icons trailed and then settled.
class MarkerGL extends L.Layer {
  constructor(owner) { super(); this.owner = owner; this.order = 11; this.tex = new WeakMap(); }
  onAdd(map) {
    this.sc = ShapesCanvas.for(map); this.sc.add(this);
    // any zoom (wheel glide, pinch, keyboard): labels hold until it ends, then get re-decided
    map.on('zoomstart', () => { this.zooming = true; }, this);
    map.on('zoomend', () => { this.zooming = false; this.zoomEndAt = performance.now(); this.changed(); }, this);
  }
  onRemove() { this.sc.remove(this); }
  need() {}
  changed() { if (this.sc) this.sc.redraw(); }

  // The map is still (ShapesCanvas.idle): make every marker's icon and name picture and put them
  // on the GPU now, a few labels a call, so a zoom or a drag never waits for one. true while more.
  prewarm(gl, v) {
    const src = this.owner.source(), font = this.owner.font;
    if (!src || !font) return false;
    let more = false;
    // labels only within a screen of the view (each is a small texture; thousands would add up)
    const w = (v.x1 - v.x) / 2.5, h = (v.z - v.z0) / 2.5, mx = (v.x + v.x1) / 2, mz = (v.z + v.z0) / 2;
    labelBudget = 16;
    for (const it of src.items) {
      const im = iconImage(it.icon, it.color, () => this.sc.idleWork(), it.img, it.gamePin);
      if (im) this.atlasSlot(gl, im); else more = true;
      if (!it.label || Math.abs(it.x - mx) > w * 1.5 || Math.abs(it.z - mz) > h * 1.5) continue;
      const lb = labelImage(it.label, font, it.labelColor);
      if (lb) this.texture(gl, lb); else { more = true; break; }   // out of this call's budget
    }
    labelBudget = Infinity;
    return more;
  }

  texture(gl, cv) {
    let t = this.tex.get(cv);
    if (t) return t;
    t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, cv);
    gl.generateMipmap(gl.TEXTURE_2D);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    this.tex.set(cv, t);
    return t;
  }

  // Where an icon's picture sits in the shared atlas (1024 px, 128 px slots: 64 icons), uploading it
  // the first time it's needed. [u0, v0, u1, v1], or null when the atlas is full.
  atlasSlot(gl, cv) {
    if (!this.slots) this.slots = new Map();
    let uv = this.slots.get(cv);
    if (uv) return uv;
    const A = 1024, S = 128;
    if (!this.atlas) {
      this.atlas = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, this.atlas);
      gl.texStorage2D(gl.TEXTURE_2D, 1 + Math.log2(A), gl.RGBA8, A, A);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      this.nextSlot = 0;
    }
    const i = this.nextSlot;
    if (i >= (A / S) * (A / S)) return null;
    this.nextSlot++;
    // the picture scaled into its slot with a 4 px clear border, so mipmaps don't bleed neighbours in
    const c = document.createElement('canvas'); c.width = c.height = S;
    c.getContext('2d').drawImage(cv, 4, 4, S - 8, S - 8);
    const sx = (i % (A / S)) * S, sy = Math.floor(i / (A / S)) * S;
    gl.bindTexture(gl.TEXTURE_2D, this.atlas);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
    gl.texSubImage2D(gl.TEXTURE_2D, 0, sx, sy, gl.RGBA, gl.UNSIGNED_BYTE, c);
    gl.generateMipmap(gl.TEXTURE_2D);
    uv = [(sx + 4) / A, (sy + 4) / A, (sx + S - 4) / A, (sy + S - 4) / A];
    this.slots.set(cv, uv);
    return uv;
  }

  // an image whose top-left sits at canvas pixel (px, py), w x h pixels
  image(gl, p, v, cv, px, py, w, h) {
    gl.bindTexture(gl.TEXTURE_2D, this.texture(gl, cv));
    gl.uniform4f(p.u.u_rect, v.x + px / v.ppm, v.z - py / v.ppm, w / v.ppm, h / v.ppm);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
  }

  draw(gl, v, sc) {
    const o = this.owner, map = o._map, src = o.src || (o.src = o.source());
    if (!map || !src) return;
    const toPx = (x, z) => [(x - v.x) * v.ppm, (v.z - z) * v.ppm];
    const W = (v.x1 - v.x) * v.ppm, H = (v.z - v.z0) * v.ppm, redraw = () => sc.redraw();

    // portal lines, dashed, under the icons
    if (src.lines.length) {
      const p = sc.line;
      sc.setView(p);
      gl.bindVertexArray(sc.quadVao);
      const c = parseInt(colors.portal.slice(1), 16);
      gl.uniform3f(p.u.u_color, (c >> 16) / 255, ((c >> 8) & 255) / 255, (c & 255) / 255);
      gl.uniform1f(p.u.u_width, 1.5); gl.uniform1f(p.u.u_on, 4); gl.uniform1f(p.u.u_off, 6); gl.uniform1f(p.u.u_alpha, 0.6);
      gl.uniform1f(p.u.u_zoom, v.zoom); gl.uniform1f(p.u.u_ppm, v.ppm);
      for (const [x1, z1, x2, z2] of src.lines) { gl.uniform2f(p.u.u_a, x1, z1); gl.uniform2f(p.u.u_b, x2, z2); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4); }
    }

    // wards: the ground each protects, under everything (only once the ring is big enough to read)
    const rings = src.items.filter((it) => it.area && it.area.r * v.ppm >= 6);
    if (rings.length) {
      const p = sc.ring;
      sc.setView(p);
      gl.bindVertexArray(sc.quadVao);
      gl.uniform1f(p.u.u_n, 40);
      for (const it of rings) {
        const R = it.area.r * v.ppm, [x, y] = toPx(it.x, it.z);
        if (x < -R - 4 || y < -R - 4 || x > W + R + 4 || y > H + R + 4) continue;
        const c = parseInt((it.area.on ? colors.ward : colors.wardoff).slice(1), 16);
        gl.uniform3f(p.u.u_color, (c >> 16) / 255, ((c >> 8) & 255) / 255, (c & 255) / 255);
        gl.uniform1f(p.u.u_alpha, it.area.on ? 0.85 : 0.6);
        gl.uniform1f(p.u.u_fill, it.area.on ? 0.07 : 0.04);
        gl.uniform2f(p.u.u_c, it.x, it.z); gl.uniform1f(p.u.u_r, it.area.r);
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
      }
    }

    const p = sc.tex;
    sc.setView(p);
    gl.bindVertexArray(sc.quadVao);
    gl.activeTexture(gl.TEXTURE0);
    gl.uniform1i(p.u.u_tex, 0);
    gl.uniform4f(p.u.u_uv, 0, 0, 1, 1);
    gl.uniform1f(p.u.u_alpha, 1);
    const grow = Math.max(16, Math.min(26, 16 + (v.zoom - 2) * 4)) * layerState.iconScale;
    let placed = [];
    for (const it of src.items) {
      const [x, y] = toPx(it.x, it.z);
      if (x < -40 || y < -40 || x > W + 40 || y > H + 40) continue;
      placed.push({ it, x, y, n: 1 });
    }
    // Zoomed out, markers of the same kind that overlap on screen draw as one with a count (portals
    // by the hundred in a busy world): far fewer icons and labels. In list order, so the first of a
    // group (and its spot) stays put as the zoom changes; spawn, bosses, traders and pins never group.
    if (v.zoom < GROUP_ZOOM) {
      const heads = [], grid = new Map(), R2 = GROUP_PX * GROUP_PX;
      for (const q of placed) {
        if (q.it.always || q.it.pin) { heads.push(q); continue; }
        const gx = Math.floor(q.x / GROUP_PX), gy = Math.floor(q.y / GROUP_PX);
        let into = null;
        for (let dx = -1; dx <= 1 && !into; dx++) for (let dy = -1; dy <= 1 && !into; dy++) {
          const list = grid.get((gx + dx) * 65536 + gy + dy);
          if (list) into = list.find((h) => h.it.icon === q.it.icon && (h.x - q.x) ** 2 + (h.y - q.y) ** 2 < R2);
        }
        if (into) { into.n++; continue; }
        heads.push(q);
        const k = gx * 65536 + gy; let list = grid.get(k); if (!list) grid.set(k, (list = [])); list.push(q);
      }
      placed = heads;
    }
    // drawn last-first, so the first in the list (pins, spawn, bosses...) end up on top, the same
    // order that wins the labels; a base built on an altar no longer hides the altar
    // every icon in one draw: their pictures share an atlas texture (a draw call each made a
    // thousand markers cost a few ms a frame)
    if (!this.spriteData || this.spriteData.length < placed.length * 8) this.spriteData = new Float32Array(Math.max(1024, placed.length * 2) * 8);
    let n = 0;
    for (let i = placed.length - 1; i >= 0; i--) {
      const { it, x, y } = placed[i];
      const dot = asDot(it), px = it.pin ? 18 * layerState.iconScale : dot ? grow * 0.55 : grow;
      const im = dot ? iconImage('dot', it.color, redraw) : iconImage(it.icon, it.color, redraw, it.img, it.gamePin);
      if (!im) continue;
      const uv = this.atlasSlot(gl, im);
      if (!uv) continue;
      const pad = px * im.padShare, size = px + pad * 2, x0 = x - px / 2 - pad, y0 = (it.pin ? y - px : y - px / 2) - pad;
      const d = this.spriteData, o = n * 8;
      d[o] = v.x + x0 / v.ppm; d[o + 1] = v.z - y0 / v.ppm; d[o + 2] = size / v.ppm; d[o + 3] = size / v.ppm;
      d[o + 4] = uv[0]; d[o + 5] = uv[1]; d[o + 6] = uv[2]; d[o + 7] = uv[3];
      n++;
    }
    sc.drawSprites(this.atlas, this.spriteData, n);
    sc.setView(p);
    gl.bindVertexArray(sc.quadVao);
    // a group's count, on its icon's top-right
    for (const q of placed) {
      if (q.n < 2) continue;
      const b = countBadge(q.n > 99 ? '99+' : String(q.n));
      this.image(gl, p, v, b, q.x + grow * 0.2, q.y - grow * 0.75, b.w, b.h);
    }

    // Labels fade in and out (MapLibre does this with its symbols): which ones may show is worked
    // out every frame, labels already showing keep their place first so a neighbour shifting a
    // little doesn't knock them out, and each eases toward shown or hidden over LABEL_FADE_MS.
    // Hovered first; none below zoom 4 (they fade out there too).
    const now = performance.now(), dt = this.lastLabel ? Math.min(100, now - this.lastLabel) : 1000;
    this.lastLabel = now;
    if (!this.alpha) this.alpha = new Map();
    // Labels are tracked by name and spot, not by object: the marker list is rebuilt after every
    // sweep, and new objects looked like new labels that faded in again (random flashes).
    const key = (it) => it._key || (it._key = `${it.label}|${Math.round(it.x)}|${Math.round(it.z)}`);   // made once per marker, not every frame
    const want = new Set(), taken = [], font = o.font;
    // pad: room a label needs around it. A label showing stays until it really overlaps (-2 px);
    // a hidden one comes in only with clear space (+6 px). One on the edge can't bounce.
    // placed labels in a screen grid of 64 px cells, so each one is checked only against its
    // neighbours (it was every placed label: a thousand markers made that a million checks a frame)
    const CELL = 64, grid = new Map();
    const cellsOf = (r, f) => { for (let gx = Math.floor(r.x0 / CELL); gx <= Math.floor(r.x1 / CELL); gx++) for (let gy = Math.floor(r.y0 / CELL); gy <= Math.floor(r.y1 / CELL); gy++) if (f(gx * 4096 + gy) === false) return false; return true; };
    const fits = (q, pad = 0) => {
      if (!q.it.label) return false;
      const lw = labelWidth(q.it.label, font), top = q.it.pin ? q.y + 1 : q.y + grow / 2;
      const r = { x0: q.x - lw / 2, x1: q.x + lw / 2, y0: top, y1: top + 14 };
      const probe = { x0: r.x0 - pad - 8, x1: r.x1 + pad + 8, y0: r.y0 - pad - 8, y1: r.y1 + pad + 8 };
      const clear = cellsOf(probe, (k) => { const list = grid.get(k); return !list || !list.some((t) => r.x0 - pad < t.x1 && r.x1 + pad > t.x0 && r.y0 - pad < t.y1 && r.y1 + pad > t.y0); });
      if (!clear) return false;
      taken.push(r);
      cellsOf(r, (k) => { let list = grid.get(k); if (!list) grid.set(k, (list = [])); list.push(r); });
      return true;
    };
    const KEEP = -2, ENTER = 6;
    // While the map is still, labels are chosen in priority order (the ones already showing
    // first). During a zoom, labels may only drop out, never come in: one that comes to overlap a
    // label above it fades out at once (two labels on top of each other looked undecided), and
    // new ones are chosen when the zoom settles. Nothing flips back and forth.
    const hovered = o.hover && placed.find((q) => q.it === o.hover);
    // labels come in only once the map has been still a moment: a slow wheel is many tiny zooms,
    // and settling after each one made labels on the edge flash
    const settled = !(map._gliding || this.zooming) && now - (this.zoomEndAt || 0) > SETTLE_MS;
    // spawn and boss names always show and claim their space first
    for (const q of placed) if (q.it.always && q.it.label && src.labels) { fits(q); want.add(key(q.it)); }
    if (!this.dropped) this.dropped = new Set();
    if (!settled && this.want) {
      // labels showing stay while they fit; new ones (say, a marker coming into view) come in at
      // once if they have clear space, but one knocked out during this zoom stays out until it
      // settles, so nothing goes out and in and out again
      for (const q of placed) if (!q.it.always && q.n === 1 && v.zoom >= LABEL_ZOOM && this.want.has(key(q.it)) && fits(q, KEEP)) want.add(key(q.it));
      if (src.labels && v.zoom >= LABEL_ZOOM)
        for (const q of placed) if (!q.it.always && q.n === 1 && !want.has(key(q.it)) && !this.dropped.has(key(q.it)) && !this.want.has(key(q.it)) && fits(q, ENTER)) want.add(key(q.it));
      for (const q of placed) if (this.want.has(key(q.it)) && !want.has(key(q.it))) this.dropped.add(key(q.it));
      this.want = want;
      if (!(map._gliding || this.zooming) && !this.settleTimer)
        this.settleTimer = setTimeout(() => { this.settleTimer = null; this.changed(); }, SETTLE_MS);
    } else {
      if (src.labels && v.zoom >= LABEL_ZOOM) {
        const shown = (q) => (this.alpha.get(key(q.it)) || 0) > 0.5;
        for (const q of placed) if (!q.it.always && q.n === 1 && shown(q) && fits(q, KEEP)) want.add(key(q.it));
        for (const q of placed) if (!q.it.always && q.n === 1 && !shown(q) && fits(q, ENTER)) want.add(key(q.it));
      }
      this.want = want;
      this.dropped.clear();
    }
    let animating = false;
    const step = dt / LABEL_FADE_MS;
    labelBudget = map._gliding || this.zooming ? 6 : 24;   // new label pictures this frame: few while the map moves
    for (const q of placed) {
      const k = key(q.it), cur = this.alpha.get(k) || 0, target = want.has(k) ? 1 : 0;
      // already there: stays (it used to step down and back up every other frame, the flicker)
      const a = target > cur ? Math.min(1, cur + step) : target < cur ? Math.max(0, cur - step) : cur;
      if (a !== target) animating = true;
      if (a > 0) this.alpha.set(k, a); else this.alpha.delete(k);
      if (a <= 0 || !q.it.label) continue;
      const im = labelImage(q.it.label, font, q.it.labelColor), top = q.it.pin ? q.y + 1 : q.y + grow / 2;
      if (!im) { this.alpha.set(k, Math.min(a, step)); animating = true; continue; }   // its picture comes next frame: start its fade then
      gl.uniform1f(p.u.u_alpha, a);
      this.image(gl, p, v, im, q.x - im.w / 2, top, im.w, im.h);
    }
    // the hovered marker's label, on top, without moving anyone else's
    labelBudget = Infinity;   // the hovered label and anything else draw at once
    if (hovered && hovered.it.label && !want.has(key(hovered.it))) {
      const im = labelImage(hovered.it.label, font, hovered.it.labelColor), top = hovered.it.pin ? hovered.y + 1 : hovered.y + grow / 2;
      gl.uniform1f(p.u.u_alpha, 1);
      this.image(gl, p, v, im, hovered.x - im.w / 2, top, im.w, im.h);
    }
    gl.uniform1f(p.u.u_alpha, 1);
    if (animating) sc.redraw();
    gl.bindVertexArray(null);
  }
}
