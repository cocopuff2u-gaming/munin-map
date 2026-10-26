// The layer toggles from the sidebar, shared by the 2D map and the 3D view so
// one set of checkboxes drives both. The UI writes here; each view subscribes.

export const layerState = {
  fog: true, fogOpacity: 1,     // fog of war is always on, fully black over unexplored ground
  mist: true,           // the Mistlands mist, cleared around a Wisplight (WebGL map only)
  altBiomes: false,     // the game's biome variants striped over the map (WebGL map only)
  buildings: true, buildingsOpacity: 1,
  ruins: true,          // world-generated structures (2D)
  ruinHidden: new Set(), // kinds of world structure hidden (layers/shapes.js RUIN_SITES indexes)
  players: true, pins: true, labels: true, grid: false, rings: false,   // rings: distance rings around spawn
  veg: true,            // the 2D tree/rock overlay (the 3D view has its own object chips)
  mapStyle: 'terrain',  // the 2D map's look: 'terrain' (the drawn land) or 'biomes' (a solid colour and name per biome)
  vegTrees: true, vegBushes: true, vegBerries: true, vegRocks: true, vegOre: true, vegMushrooms: true, vegPlants: true,   // its groups, each on its own (WebGL map only, vegpack.js VEG_GROUPS)
  vegHidden: new Set(), // single kinds hidden within a group that is on
  time3d: 'live',       // 3D lighting: 'live' follows the server's clock, or noon/morning/evening/night
  shadows: !matchMedia('(max-width: 720px)').matches,   // real shadows in 3D (off on phones by default)
  sets: new Map([["dungeons", false], ["camps", false], ["signs", false], ["peaks", false], ["tablepins", false], ["wards", false], ["runestones", false], ["vegvisirs", false], ["beds", false]]),   // marker set id -> bool (missing = visible); dungeon entrances, camps and resource spots, signs, cartography table pins, wards, runestones, Vegvisirs and beds start off (hundreds of them)
  cats: new Map(),      // location category -> bool (missing = visible)
  iconScale: 1, iconDots: false,   // marker size, and plain dots instead of icons (bosses, traders and pins keep theirs); kept per browser
  listeners: new Set(),
  onChange(fn) { this.listeners.add(fn); return () => this.listeners.delete(fn); },
  set(key, value) {
    this[key] = value;
    if (key === 'iconScale' || key === 'iconDots') { try { localStorage.setItem('munin-icons', JSON.stringify({ scale: this.iconScale, dots: this.iconDots })); } catch (e) { /* this visit only */ } }
    this.emit(key);
  },
  setSet(id, v) { this.sets.set(id, v); this.emit('sets'); },
  setCat(cat, v) { this.cats.set(cat, v); this.emit('cats'); },
  setVisible(id) { return this.sets.get(id) !== false; },
  catVisible(cat) { return this.cats.get(cat) !== false; },
  emit(key) { for (const fn of this.listeners) fn(key, this); },
};

try {
  const saved = JSON.parse(localStorage.getItem('munin-icons') || 'null');
  if (saved) { layerState.iconScale = Math.min(2, Math.max(0.5, +saved.scale || 1)); layerState.iconDots = !!saved.dots; }
} catch (e) { /* defaults */ }
