// The Mistlands mist mask from the server (data/mist.png: World/BiomeMap, explored ground only),
// shared by the 2D map (layers/ground.js MistGL) and the 3D view.
import { worldTile } from './crs.js';

// which of the world's MIST_BLOCKS x MIST_BLOCKS blocks have any mist (row 0 the north edge)
export const MIST_BLOCKS = 32;

// the mask as an ImageBitmap (rows north first, gray in red) and its blocks; null when it is the
// same as `tag` (the last one's) or there is none. Low priority: it never holds up the map.
export async function fetchMist(tag) {
  const r = await fetch(worldTile('data/mist.png'), { cache: 'no-cache', priority: 'low' });
  if (!r.ok) return null;
  const t = r.headers.get('etag') || r.headers.get('content-length');
  if (t && t === tag) return null;
  const bmp = await createImageBitmap(await r.blob(), { premultiplyAlpha: 'none', colorSpaceConversion: 'none', imageOrientation: 'none' });
  return { bmp, tag: t, blocks: mistBlocks(bmp) };
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
