// The layer toggles from the sidebar, shared by the 2D map and the 3D view so
// one set of checkboxes drives both. The UI writes here; each view subscribes.

export const layerState = {
  fog: true, fogOpacity: 1,     // fog of war is always on, fully black over unexplored ground
  mist: true,           // the Mistlands mist, cleared around a Wisplight (WebGL map only)
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
  sets: new Map([["dungeons", false], ["tablepins", false]]),   // marker set id -> bool (missing = visible); dungeon entrances and cartography table pins start off (hundreds of them)
  cats: new Map(),      // location category -> bool (missing = visible)
  listeners: new Set(),
  onChange(fn) { this.listeners.add(fn); return () => this.listeners.delete(fn); },
  set(key, value) { this[key] = value; this.emit(key); },
  setSet(id, v) { this.sets.set(id, v); this.emit('sets'); },
  setCat(cat, v) { this.cats.set(cat, v); this.emit('cats'); },
  setVisible(id) { return this.sets.get(id) !== false; },
  catVisible(cat) { return this.cats.get(cat) !== false; },
  emit(key) { for (const fn of this.listeners) fn(key, this); },
};
