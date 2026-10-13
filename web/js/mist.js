// The Mistlands mist mask from the server (data/mist.png: World/BiomeMap, explored ground only),
// shared by the 2D map (layers/ground.js MistGL) and the 3D view, with the clearings the Dvergr
// lanterns and Mistlands torches keep cut out of it (data/mistclear.json: World/Demisters).
import { worldTile } from './crs.js';
import { WORLD_HALF } from './grid.js';

// which of the world's MIST_BLOCKS x MIST_BLOCKS blocks have any mist (row 0 the north edge)
export const MIST_BLOCKS = 32;

// the mask as an ImageBitmap (rows north first, gray in red) and its blocks; null when it is the
// same as `tag` (the last one's) or there is none. Low priority: it never holds up the map.
export async function fetchMist(tag) {
  const opts = { cache: 'no-cache', priority: 'low' };
  const [r, rc] = await Promise.all([fetch(worldTile('data/mist.png'), opts), fetch(worldTile('data/mistclear.json'), opts).catch(() => null)]);
  if (!r.ok) return null;
  const tagOf = (x) => (x && x.ok && (x.headers.get('etag') || x.headers.get('content-length'))) || '';
  const t = tagOf(r) + '|' + tagOf(rc);
  if (t === tag) return null;
  let bmp = await createImageBitmap(await r.blob(), { premultiplyAlpha: 'none', colorSpaceConversion: 'none', imageOrientation: 'none' });
  const blocks = mistBlocks(bmp);
  let clear = [];
  try { if (rc && rc.ok) clear = await rc.json(); } catch (e) { /* the mist without them */ }
  if (clear.length) bmp = await cutClearings(bmp, clear);
  return { bmp, tag: t, blocks };
}

// The clearings cut out of the mask, on a picture twice its size (~10 m a pixel, as fine as the 2D
// map bakes the mist): the mist gone within 3/4 of the range, thinning out to the edge of it.
// Done once each time the mask or the clearings change, so drawing the mist costs nothing more.
const CLEAR_SCALE = 2;
async function cutClearings(bmp, list) {
  const w = bmp.width * CLEAR_SCALE, h = bmp.height * CLEAR_SCALE;
  const c = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(w, h) : Object.assign(document.createElement('canvas'), { width: w, height: h });
  const g = c.getContext('2d');
  g.drawImage(bmp, 0, 0, w, h);
  const px = w / (2 * WORLD_HALF);   // pixels a metre
  for (const [x, z, r] of list) {
    const cx = (x + WORLD_HALF) * px, cy = (WORLD_HALF - z) * px, rr = Math.max(r * px, 1);
    const grad = g.createRadialGradient(cx, cy, rr * 0.75, cx, cy, rr);
    grad.addColorStop(0, 'rgba(0,0,0,1)');
    grad.addColorStop(1, 'rgba(0,0,0,0)');
    g.fillStyle = grad;
    g.fillRect(cx - rr, cy - rr, rr * 2, rr * 2);
  }
  bmp.close?.();
  return createImageBitmap(c, { premultiplyAlpha: 'none', colorSpaceConversion: 'none', imageOrientation: 'none' });
}

// read off the picture once each time it changes
function mistBlocks(bmp) {
  const w = bmp.width, h = bmp.height, out = new Uint8Array(MIST_BLOCKS * MIST_BLOCKS);
  try {
    const c = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(w, h) : Object.assign(document.createElement('canvas'), { width: w, height: h });
    const g = c.getContext('2d', { willReadFrequently: true });
    g.drawImage(bmp, 0, 0);
    const d = g.getImageData(0, 0, w, h).data;
    for (let y = 0; y < h; y++) {
      const row = Math.floor(y * MIST_BLOCKS / h) * MIST_BLOCKS;
      for (let x = 0; x < w; x++) if (d[(y * w + x) * 4]) out[row + Math.floor(x * MIST_BLOCKS / w)] = 1;
    }
  } catch (e) { out.fill(1); }   // can't read it: draw it everywhere
  return out;
}

// any mist in the world square from (minX, maxZ) spanning `span` metres?
export function mistIn(blocks, minX, maxZ, span, half) {
  if (!blocks) return false;
  const B = MIST_BLOCKS, s = (2 * half) / B;
  const c0 = Math.max(0, Math.floor((minX + half) / s)), c1 = Math.min(B - 1, Math.floor((minX + span + half - 0.01) / s));
  const r0 = Math.max(0, Math.floor((half - maxZ) / s)), r1 = Math.min(B - 1, Math.floor((half - maxZ + span - 0.01) / s));
  for (let r = r0; r <= r1; r++) for (let c = c0; c <= c1; c++) if (blocks[r * B + c]) return true;
  return false;
}
