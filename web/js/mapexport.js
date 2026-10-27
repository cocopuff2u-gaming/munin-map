// Export, the plain kinds (the 3D scene is export.js): a picture of an area of the map, and the
// markers in an area as a CSV or JSON list.
//
// The picture is the terrain tiles (and the tree and rock tiles over them) at the zoom nearest the detail asked for (coarser when it would
// pass MAX_SIDE pixels on a side), the fog of war over it exactly as the map has it, the 64 m game
// zones if asked, and the markers the map shows, with their names. Unexplored ground stays dark:
// nothing leaves the page that the map doesn't show.

import { TILE, WORLD_HALF, MAX_ZOOM, OUTSIDE_COLOR, metersPerPixel, worldTile } from './crs.js';
import { iconSvg } from './icons.js';

const MAX_SIDE = 8192;   // browsers draw canvases this big everywhere; a whole world at 4 m/px is 5250

// the tile zoom for an area span (m) and a wanted detail (m per pixel)
export function imageZoom(span, mpp) {
  let z = Math.round(MAX_ZOOM - Math.log2(mpp));
  while (z > 0 && span / metersPerPixel(z) > MAX_SIDE) z--;
  return z;
}

export function imageSize(a, mpp) {
  const m = metersPerPixel(imageZoom(Math.max(a.x1 - a.x0, a.z1 - a.z0), mpp));
  return { w: Math.round((a.x1 - a.x0) / m), h: Math.round((a.z1 - a.z0) / m), mpp: m };
}

function loadImg(src) {
  return new Promise((resolve) => { const im = new Image(); im.onload = () => resolve(im); im.onerror = () => resolve(null); im.src = src; });
}

export async function mapImage(app, a, opts, report = () => {}) {
  const z = imageZoom(Math.max(a.x1 - a.x0, a.z1 - a.z0), opts.mpp), mpp = metersPerPixel(z), span = TILE * mpp;
  const W = Math.round((a.x1 - a.x0) / mpp), H = Math.round((a.z1 - a.z0) / mpp);
  const cv = document.createElement('canvas'); cv.width = W; cv.height = H;
  const g = cv.getContext('2d');
  g.fillStyle = OUTSIDE_COLOR; g.fillRect(0, 0, W, H);
  const px = (x) => (x - a.x0) / mpp, py = (zz) => (a.z1 - zz) / mpp;

  // the ground, eight tiles at a time
  const n = Math.round(2 * WORLD_HALF / span), jobs = [];
  for (let ty = Math.max(0, Math.floor((WORLD_HALF - a.z1) / span)); ty <= Math.min(n - 1, Math.floor((WORLD_HALF - a.z0) / span)); ty++)
    for (let tx = Math.max(0, Math.floor((a.x0 + WORLD_HALF) / span)); tx <= Math.min(n - 1, Math.floor((a.x1 + WORLD_HALF) / span)); tx++) jobs.push([tx, ty]);
  let done = 0;
  const one = async ([tx, ty]) => {
    for (const layer of opts.veg ? ['map', 'veg'] : ['map']) {   // the ground, then the trees and rocks on it
      try {
        const r = await fetch(worldTile(`tiles/${layer}/${z}/${tx}/${ty}.png`), { headers: { Accept: 'image/webp,image/png' } });
        if (r.ok) g.drawImage(await createImageBitmap(await r.blob()), px(-WORLD_HALF + tx * span), py(WORLD_HALF - ty * span), TILE, TILE);
      } catch (e) { /* a missing tile stays dark */ }
    }
    if (++done % 8 === 0) report(`Map ${Math.round(100 * done / jobs.length)}%`);
  };
  for (let i = 0; i < jobs.length; i += 8) await Promise.all(jobs.slice(i, i + 8).map(one));

  // the fog of war, as the map draws it (its soft veil, pixel (i, j) centred on ((i - half) * px, (j - half) * px))
  const fog = app.layers.fog;
  if (fog && fog.visible !== false && fog.opacity > 0) {
    await fog.loaded;
    const fw = -(fog.size / 2 + 0.5) * fog.px, fe = (fog.size / 2 - 0.5) * fog.px;
    g.save(); g.globalAlpha = fog.opacity; g.imageSmoothingEnabled = true;
    g.drawImage(fog.canvas, px(fw), py(fe), (fe - fw) / mpp, (fe - fw) / mpp);
    g.restore();
  }

  // the game's 64 m zones
  if (opts.grid) {
    const step = 64 / mpp >= 6 ? 64 : 256;
    g.strokeStyle = 'rgba(255,255,255,.22)'; g.lineWidth = 1; g.beginPath();
    for (let x = Math.ceil((a.x0 + 32) / step) * step - 32; x <= a.x1; x += step) { const p = Math.round(px(x)) + 0.5; g.moveTo(p, 0); g.lineTo(p, H); }
    for (let zz = Math.ceil((a.z0 + 32) / step) * step - 32; zz <= a.z1; zz += step) { const p = Math.round(py(zz)) + 0.5; g.moveTo(0, p); g.lineTo(W, p); }
    g.stroke();
  }

  // the markers the map shows (its own list: the same sets, icons and colours)
  if (opts.markers) {
    report('Markers');
    const list = app.layers.markers.drawList();
    if (opts.portals) {   // linked portals, dashed as on the map
      g.save(); g.strokeStyle = 'rgba(92,224,230,.7)'; g.lineWidth = 1.5; g.setLineDash([6, 6]); g.beginPath();
      for (const [x1, z1, x2, z2] of list.lines) { g.moveTo(px(x1), py(z1)); g.lineTo(px(x2), py(z2)); }
      g.stroke(); g.restore();
    }
    const items = list.items.filter((it) => it.x >= a.x0 && it.x <= a.x1 && it.z >= a.z0 && it.z <= a.z1);
    const S = Math.max(16, Math.min(28, Math.round(Math.max(W, H) / 90)));
    const imgs = new Map();
    for (const it of items) {
      const key = it.img || `${it.icon}|${it.color}`;
      if (!imgs.has(key)) imgs.set(key, it.img ? loadImg(it.img) : loadImg('data:image/svg+xml;charset=utf-8,' + encodeURIComponent(iconSvg(it.icon, it.color))));
    }
    const shown = [];
    for (const it of items) {
      const im = await imgs.get(it.img || `${it.icon}|${it.color}`);
      const x = px(it.x), y = py(it.z);
      if (im) g.drawImage(im, x - S / 2, y - S / 2, S, S);
      if (opts.labels && it.label) shown.push([it, x, y]);
    }
    g.font = `600 ${Math.round(S * 0.5)}px system-ui, sans-serif`; g.textAlign = 'center'; g.textBaseline = 'top';
    g.lineJoin = 'round'; g.lineWidth = 3; g.strokeStyle = 'rgba(0,0,0,.85)';
    for (const [it, x, y] of shown) { g.strokeText(it.label, x, y + S / 2 + 1); g.fillStyle = it.labelColor || '#fff'; g.fillText(it.label, x, y + S / 2 + 1); }
  }

  // who is on, where they are now
  if (opts.players) {
    const ps = (app.layers.players.players || []).filter((p) => p.x !== undefined && p.x >= a.x0 && p.x <= a.x1 && p.z >= a.z0 && p.z <= a.z1);
    const R = Math.max(6, Math.min(11, Math.round(Math.max(W, H) / 220)));
    g.font = `700 ${R + 3}px system-ui, sans-serif`; g.textAlign = 'center'; g.textBaseline = 'bottom'; g.lineJoin = 'round';
    for (const p of ps) {
      const x = px(p.x), y = py(p.z);
      g.beginPath(); g.arc(x, y, R, 0, Math.PI * 2); g.fillStyle = app.layers.players.color(p.name); g.fill();
      g.lineWidth = 2; g.strokeStyle = '#000'; g.stroke();
      g.lineWidth = 3; g.strokeStyle = 'rgba(0,0,0,.85)'; g.strokeText(p.name, x, y - R - 2); g.fillStyle = '#fff'; g.fillText(p.name, x, y - R - 2);
    }
  }

  report('Saving');
  const type = opts.format === 'jpeg' ? 'image/jpeg' : opts.format === 'webp' ? 'image/webp' : 'image/png';
  const blob = await new Promise((resolve, reject) => cv.toBlob((b) => (b ? resolve(b) : reject(new Error('The picture was too big for this browser'))), type, 0.9));
  return { blob, w: W, h: H, mpp, ext: blob.type === 'image/jpeg' ? 'jpg' : blob.type === 'image/webp' ? 'webp' : 'png' };   // (a browser without WebP writes PNG)
}

// the markers in an area: every set, or only the ones the map shows; with each one's biome (where
// the map knows it: explored ground) and how far it is from the spawn
export function markerRows(app, a, onlyShown) {
  const ml = app.layers.markers, rows = [], bh = app.biomeHover, sp = app.spawn || { x: 0, z: 0 };
  const more = (r) => { r.biome = (bh && bh.grid && bh.at(r.x, r.z)) || ''; r.fromSpawn = Math.round(Math.hypot(r.x - sp.x, r.z - sp.z)); return r; };
  for (const s of ml.sets) {
    if (s.mapless || (onlyShown && ml.visible.get(s.id) === false)) continue;
    for (const m of s.markers || []) {
      if (m.x < a.x0 || m.x > a.x1 || m.z < a.z0 || m.z > a.z1) continue;
      rows.push(more({ set: s.label || s.id, name: m.label || '', kind: m.icon || m.cat || '', x: Math.round(m.x), z: Math.round(m.z), ...(m.tag ? { tag: m.tag } : {}) }));
    }
  }
  if (!onlyShown || ml.visible.get('pins') !== false)
    for (const p of ml.pins.values()) if (p.x >= a.x0 && p.x <= a.x1 && p.z >= a.z0 && p.z <= a.z1) rows.push(more({ set: 'Pins', name: p.text || '', kind: p.type || 'pin', x: Math.round(p.x), z: Math.round(p.z) }));
  return rows;
}

// CSV for spreadsheets, JSON, or GeoJSON points for map tools (QGIS and the like: x east, z north, in metres)
export function markerFile(rows, format) {
  if (format === 'json') return new Blob([JSON.stringify(rows, null, 1)], { type: 'application/json' });
  if (format === 'geojson') {
    const features = rows.map(({ x, z, ...props }) => ({ type: 'Feature', geometry: { type: 'Point', coordinates: [x, z] }, properties: props }));
    return new Blob([JSON.stringify({ type: 'FeatureCollection', features }, null, 1)], { type: 'application/geo+json' });
  }
  const q = (v) => (/[",\n]/.test(String(v)) ? `"${String(v).replace(/"/g, '""')}"` : String(v));
  const cols = ['set', 'name', 'kind', 'x', 'z', 'biome', 'fromSpawn', 'tag'];
  return new Blob([cols.join(',') + '\n' + rows.map((r) => cols.map((c) => q(r[c] ?? '')).join(',')).join('\n') + '\n'], { type: 'text/csv' });
}
