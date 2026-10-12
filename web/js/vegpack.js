import { WORLD_HALF } from './grid.js';
// Tree and rock records packed for the GPU (layers/shapes.js TreesGL), shared by the page and the
// vegetation worker (vegworker.js).

// kind -> [crownRadius, colour, isRock]  (mirrors Palette.cs and layers/veg.js)
export const VEG = {
  1: [4.5, '#568a3a'], 2: [3.0, '#2c5234'], 3: [3.0, '#383e28'], 4: [4.0, '#4a6870'], 5: [2.5, '#46382e'],
  6: [1.3, '#466e32'], 7: [2.5, '#767670', true], 8: [2.5, '#86684a', true], 9: [0.7, '#60462c'], 10: [1.0, '#5a783c'], 11: [3.0, '#3c2822'],
  12: [4.5, '#46702a'], 13: [4.5, '#8aa046'], 14: [3.0, '#486430'], 15: [1.0, '#c43a4a'], 16: [1.0, '#4e64cc'], 17: [1.0, '#e4a840'], 18: [4.5, '#cc963a'],
  19: [0.6, '#d6423a'], 20: [0.6, '#e8c446'], 21: [0.6, '#b06ad6'], 22: [0.6, '#bae0f2'], 23: [0.6, '#968c8c'], 24: [0.6, '#706ed6'],
  25: [0.6, '#f2de5a'], 26: [0.6, '#7cba4e'], 27: [0.6, '#d6be74'], 28: [0.6, '#c4d6a4'], 29: [1.0, '#b0223e'], 30: [1.1, '#8e3a7a'], 31: [1.1, '#703c2c'],
  // kinds of rock and ore (Palette.Veg Cliff..MuddyScrap)
  32: [2.5, '#6c6c68', true], 33: [2.5, '#c4beaa', true], 34: [2.5, '#b0683a', true], 35: [2.5, '#969ca0', true], 36: [2.5, '#c8d2de', true],
  37: [2.5, '#282230', true], 38: [2.5, '#6e543c', true],
};
export const TREE_STRIDE = 20;   // x, z, radius, seed (float32) + rgb, flags (uint8: rock 1, in shallow water 2, kind << 2)

// The Layers panel's groups, each with its own switch, and the kinds in each that can be hidden
// one by one (layerState: the group's key, and vegHidden for single kinds). Kinds not listed
// (10, a generic berry bush) go with their group all the same.
export const VEG_GROUPS = [
  { key: 'vegTrees', label: 'Trees', desc: 'Forests, lone trees, stumps', kinds: [[1, 'Beech'], [12, 'Oak'], [13, 'Birch'], [18, 'Autumn birch'], [2, 'Fir'], [14, 'Pine'],
    [3, 'Swamp tree'], [4, 'Yggdrasil'], [11, 'Ash tree'], [5, 'Dead tree'], [9, 'Stump']] },
  { key: 'vegBushes', label: 'Bushes', desc: 'Shrubs, ferns and vines', kinds: [[6, 'Shrub'], [31, 'Ash fern'], [30, 'Ash vine']] },
  { key: 'vegBerries', label: 'Berries', desc: 'Every berry bush', kinds: [[15, 'Raspberry'], [16, 'Blueberry'], [17, 'Cloudberry'], [29, 'Lingonberry']], more: [10] },
  { key: 'vegRocks', label: 'Rocks', desc: 'Boulders, cliffs, giant bones', kinds: [[7, 'Boulder'], [32, 'Cliff'], [33, 'Giant bones']] },
  { key: 'vegOre', label: 'Ore', desc: 'Copper, tin, silver and more', kinds: [[34, 'Copper'], [35, 'Tin'], [36, 'Silver'], [37, 'Obsidian'], [38, 'Muddy scrap'], [8, 'Other ore']] },
  { key: 'vegMushrooms', label: 'Mushrooms', desc: 'Every kind you can pick', kinds: [[19, 'Red'], [20, 'Yellow'], [21, 'Magecap'], [22, 'Jotun puffs']] },
  { key: 'vegPlants', label: 'Plants', desc: 'Herbs and wild crops', kinds: [[24, 'Thistle'], [25, 'Dandelion'], [26, 'Fiddlehead'], [23, 'Smoke puff'], [27, 'Wild barley'], [28, 'Wild flax']] },
];

// the kinds shown now, as two 32-bit masks (kinds 0-31, 32-63) for the tree shader's u_show
export function vegShowMask(state) {
  const m = [0, 0];
  for (const g of VEG_GROUPS) {
    if (state[g.key] === false) continue;
    for (const k of g.kinds.map((e) => e[0]).concat(g.more || []))
      if (!state.vegHidden.has(k)) m[k >> 5] |= 1 << (k & 31);
  }
  return [m[0] >>> 0, m[1] >>> 0];
}

const RGB = {};
for (const k in VEG) { const n = parseInt(VEG[k][1].slice(1), 16); RGB[k] = [n >> 16, (n >> 8) & 255, n & 255]; }

// pts: {x, z, kind, size, wet}; north to south, so southern crowns overlap northern ones
const seedOf = (p) => (p.x * 7 + p.z * 3) % 6.28;   // a per-tree number for the crown's lobes

// Crowns under SMALL_R metres (bushes, plants, small stones) come first, then the rest: zoomed out,
// where they are a fraction of a pixel, the map draws only the second part (TreesGL), and up close
// trees cover the bushes under them. small = how many of the first part.
export const SMALL_R = 2;
const radiusOf = (p) => VEG[p.kind][0] * p.size;

export function packTrees(pts) {
  const north = (a, b) => b.z - a.z;
  const all = pts.filter((p) => VEG[p.kind]);
  const small = all.filter((p) => radiusOf(p) < SMALL_R).sort(north);
  const list = small.concat(all.filter((p) => radiusOf(p) >= SMALL_R).sort(north));
  const n = list.length;
  const bytes = new ArrayBuffer(n * TREE_STRIDE), f = new Float32Array(bytes), u = new Uint8Array(bytes);
  for (let i = 0; i < n; i++) {
    const p = list[i], o = i * 5, [r, g, b] = RGB[p.kind];
    f[o] = p.x; f[o + 1] = p.z; f[o + 2] = VEG[p.kind][0] * p.size; f[o + 3] = seedOf(p);
    u[o * 4 + 16] = r; u[o * 4 + 17] = g; u[o * 4 + 18] = b; u[o * 4 + 19] = (VEG[p.kind][2] ? 1 : 0) | (p.wet ? 2 : 0) | (p.kind << 2);
  }
  return { bytes, count: n, small: small.length };
}

// a VGR3 region (see Vegetation.RegionBin): -> [{cx, cz, bytes, count}]
export function unpackRegion(buf) {
  const dv = new DataView(buf);
  if (buf.byteLength < 8 || String.fromCharCode(dv.getUint8(0), dv.getUint8(1), dv.getUint8(2), dv.getUint8(3)) !== 'VGR3') return [];
  const n = dv.getUint32(4, true), out = [];
  let o = 8;
  for (let i = 0; i < n; i++) {
    const cx = dv.getUint8(o), cz = dv.getUint8(o + 1), count = dv.getUint32(o + 2, true);
    o += 6;
    const minX = -WORLD_HALF + cx * 256, minZ = -WORLD_HALF + cz * 256, pts = new Array(count);
    for (let j = 0; j < count; j++, o += 6)
      { const k = dv.getUint8(o + 4); pts[j] = { x: minX + dv.getInt16(o, true) / 4, z: minZ + dv.getInt16(o + 2, true) / 4, kind: k & 0x7f, wet: k >= 0x80, size: dv.getUint8(o + 5) / 32 }; }
    out.push(Object.assign({ cx, cz }, packTrees(pts)));
  }
  return out;
}
