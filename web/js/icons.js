// Inline SVG marker icons. Each is a 24x24 glyph; colour comes from the category.

const glyphs = {
  spawn: '<circle cx="12" cy="12" r="9" fill="#111" stroke="COLOR" stroke-width="2.5"/><path d="M12 6l1.8 4.2 4.2.4-3.2 2.8 1 4.2L12 15.4 8.2 17.6l1-4.2L6 10.6l4.2-.4z" fill="COLOR"/>',
  boss: '<path d="M4 6l4 5 4-7 4 7 4-5-2 13H6z" fill="COLOR" stroke="#111" stroke-width="1.2"/>',
  trader: '<path d="M4 9h16l-2 11H6z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M8 9V6a4 4 0 0 1 8 0v3" fill="none" stroke="#111" stroke-width="2"/>',
  dungeon: '<path d="M12 3 4 9v11h16V9z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M10 20v-6h4v6" fill="#111"/>',
  // one per kind of dungeon entrance
  crypt: '<path d="M5 21V10a7 7 0 0 1 14 0v11z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M12 7v7M9.5 9.5h5" stroke="#111" stroke-width="1.6"/><path d="M9 21v-3h6v3" fill="#111"/>',
  sunkencrypt: '<path d="M5 17V9a7 7 0 0 1 14 0v8z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M9 17v-4a3 3 0 0 1 6 0v4" fill="#111"/><path d="M3 19c2-1.5 3 1.5 5 0s3 1.5 5 0 3 1.5 5 0 2 1 3 0" fill="none" stroke="#2c6e8f" stroke-width="1.8"/>',
  trollcave: '<path d="M3 21 8 8l4 3 4-6 5 16z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M9 21c0-4 1.5-6 3-6s3 2 3 6z" fill="#111"/>',
  frostcave: '<path d="M3 21 8 8l4 3 4-6 5 16z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M9 21c0-4 1.5-6 3-6s3 2 3 6z" fill="#111"/><path d="M7 11l1 3 1-3M14 8l1 3 1-3" fill="#fff" stroke="none"/>',
  infestedmine: '<path d="M4 21V12l8-7 8 7v9z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M8 21v-6h8v6" fill="#111"/><circle cx="12" cy="11" r="1.6" fill="#7dd3c8" stroke="none"/>',
  fortress: '<path d="M4 21V8h3v2h2V8h2v2h2V8h2v2h2V8h3v13z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M10 21v-5a2 2 0 0 1 4 0v5z" fill="#111"/>',
  cave: '<path d="M4 20V11a8 8 0 0 1 16 0v9z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M9 20v-5a3 3 0 0 1 6 0v5z" fill="#111"/>',
  camp: '<path d="M12 4l9 16H3z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M12 12l4 8H8z" fill="#111"/>',
  bearcave: '<path d="M3 21 8 8l4 3 4-6 5 16z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M9 21c0-4 1.5-6 3-6s3 2 3 6z" fill="#111"/><circle cx="10.4" cy="14.6" r="1.1" fill="COLOR" stroke="none"/><circle cx="13.6" cy="14.6" r="1.1" fill="COLOR" stroke="none"/>',
  // the Deep North: a shaft going down, and Mörkhalla's dark keep
  hole: '<ellipse cx="12" cy="13" rx="9" ry="6" fill="COLOR" stroke="#111" stroke-width="1.2"/><ellipse cx="12" cy="13.5" rx="5.5" ry="3.4" fill="#111"/><path d="M12 3v6M9.5 6.5 12 9l2.5-2.5" fill="none" stroke="COLOR" stroke-width="1.8"/>',
  morkhalla: '<path d="M4 21V9l3-3 3 3V6l2-3 2 3v3l3-3 3 3v12z" fill="COLOR" stroke="#111" stroke-width="1.2" stroke-linejoin="round"/><path d="M10 21v-5a2 2 0 0 1 4 0v5z" fill="#111"/>',
  // landmarks: the Forge of Potential, an ancestral memorial, a Fimbulvinter orb, a hot spring
  forge: '<path d="M4 12h12l4-3v3l-2 2v2H6v-2z" fill="COLOR" stroke="#111" stroke-width="1.2" stroke-linejoin="round"/><path d="M9 16v3H7v2h10v-2h-2v-3" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M11 9c0-2 2-3 1-6 2 2 3 4 1 6" fill="none" stroke="COLOR" stroke-width="1.5"/>',
  memorial: '<path d="M7 21V8l5-5 5 5v13z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M4 21h16" stroke="#111" stroke-width="2"/><path d="M12 8v7M9.5 10.5h5" stroke="#111" stroke-width="1.6"/>',
  orb: '<circle cx="12" cy="11" r="7" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M12 6v10M7.5 8.5l9 5M16.5 8.5l-9 5" stroke="#fff" stroke-width="1.2" opacity=".8"/><path d="M8 21h8l-1.5-3h-5z" fill="#111"/>',
  hotspring: '<path d="M3 17c0-2.5 4-4 9-4s9 1.5 9 4-4 4-9 4-9-1.5-9-4z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M8 11c-1-2 1-3 0-5M12 11c-1-2 1-3 0-5M16 11c-1-2 1-3 0-5" fill="none" stroke="COLOR" stroke-width="1.6"/>',
  drakenest: '<path d="M3 15c2 5 16 5 18 0z" fill="COLOR" stroke="#111" stroke-width="1.2"/><ellipse cx="9" cy="12" rx="2.6" ry="3.4" fill="#e9f3ff" stroke="#111"/><ellipse cx="15" cy="12" rx="2.6" ry="3.4" fill="#e9f3ff" stroke="#111"/>',
  gucktree: '<path d="M11 21V12M13 21V12" stroke="#5a4632" stroke-width="2"/><path d="M12 13c-4 0-7-2-7-5 2 0 3-2 7-4 4 2 5 4 7 4 0 3-3 5-7 5z" fill="#4b4a32" stroke="#111" stroke-width="1.2"/><circle cx="9" cy="9" r="1.6" fill="COLOR" stroke="none"/><circle cx="14.5" cy="8" r="1.5" fill="COLOR" stroke="none"/><circle cx="12" cy="11" r="1.3" fill="COLOR" stroke="none"/>',
  peak: '<path d="M2 20 9 7l3 5 3-3 7 11z" fill="COLOR" stroke="#111" stroke-width="1.2" stroke-linejoin="round"/><path d="M7 10.6 9 7l2.2 3.8-1.2-.6-1 .9-.9-.8z" fill="#fff" stroke="none"/>',
  sign: '<path d="M4 5h16v9H4z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M12 14v7" stroke="#111" stroke-width="2.4"/><path d="M7 8.5h10M7 11h7" stroke="#111" stroke-width="1.3"/>',
  fulingcamp: '<path d="M12 4l9 16H3z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M12 12l4 8H8z" fill="#111"/><path d="M12 4V1.5M12 2l3 1-3 1" fill="COLOR" stroke="#111" stroke-width="1"/>',
  tarpit: '<path d="M3 15c0-3 4-5 9-5s9 2 9 5-4 5-9 5-9-2-9-5z" fill="COLOR" stroke="#111" stroke-width="1.2"/><circle cx="9" cy="14" r="1.6" fill="#111"/><circle cx="14.5" cy="15.5" r="1.2" fill="#111"/><path d="M12 9c0-2 1-3 0-5" fill="none" stroke="COLOR" stroke-width="1.6"/>',
  village: '<path d="M3 20V10l5-5 5 5v10z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M13 20V12l4-4 4 4v8z" fill="COLOR" stroke="#111" stroke-width="1.2"/>',
  ruin: '<path d="M5 20V9h3v3h2V6h4v6h2V9h3v11z" fill="COLOR" stroke="#111" stroke-width="1.2"/>',
  runestone: '<path d="M8 3h8l2 5-3 13H9L6 8z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M12 7v9M9.5 10h5" stroke="#111" stroke-width="1.6"/>',
  bed: '<path d="M3 18V7M3 13h18v5M21 18v-5" fill="none" stroke="COLOR" stroke-width="2.2" stroke-linecap="round"/><path d="M4 13V10.5a2 2 0 0 1 2-2h3a2 2 0 0 1 2 2V13z" fill="COLOR"/><path d="M11 13v-2.5a2 2 0 0 1 2-2h5a3 3 0 0 1 3 3V13z" fill="COLOR" opacity=".7"/>',
  // a Vegvisir: a tall stone with its glowing rune
  vegvisir: '<path d="M9 2h6l1.5 6L15 22H9L7.5 8z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M12 6v10M9.5 9l2.5 2.5L14.5 9" fill="none" stroke="#111" stroke-width="1.6"/>',
  wreck: '<path d="M3 14h18l-3 5H6z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M12 4v10M12 5c4 1 6 4 6 6-3-1-5-1-6-1" fill="none" stroke="#111" stroke-width="1.8"/>',
  poi: '<circle cx="12" cy="12" r="6" fill="COLOR" stroke="#111" stroke-width="1.5"/>',
  portal: '<ellipse cx="12" cy="12" rx="7" ry="9" fill="#111" stroke="COLOR" stroke-width="2.5"/><ellipse cx="12" cy="12" rx="3" ry="5" fill="COLOR"/>',
  tombstone: '<path d="M6 21V9a6 6 0 0 1 12 0v12z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M12 8v8M9 11h6" stroke="#111" stroke-width="2"/>',
  boat: '<path d="M2 14h20l-4 6H6z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M12 3v11M12 4c5 1 7 5 7 8H12" fill="COLOR" stroke="#111" stroke-width="1.2"/>',
  cart: '<path d="M3 8h10l3 6H4z" fill="COLOR" stroke="#111" stroke-width="1.2"/><circle cx="7" cy="17" r="2.5" fill="#111" stroke="COLOR" stroke-width="1.5"/><circle cx="14" cy="17" r="2.5" fill="#111" stroke="COLOR" stroke-width="1.5"/><path d="M16 12l5-2" stroke="#111" stroke-width="2"/>',
  // a folded map: the cartography table's pins
  maptable: '<path d="M3 6l6-2 6 2 6-2v14l-6 2-6-2-6 2z" fill="COLOR" stroke="#111" stroke-width="1.2" stroke-linejoin="round"/><path d="M9 4v14M15 6v14" stroke="#111" stroke-width="1.1" opacity=".55"/><path d="M5.5 13.5l2.5-3 3 2 3-4 3.5 2.5" fill="none" stroke="#8a3b1f" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/>',
  pin: '<path d="M12 2a6 6 0 0 0-6 6c0 4.5 6 12 6 12s6-7.5 6-12a6 6 0 0 0-6-6z" fill="COLOR" stroke="#111" stroke-width="1.2"/><circle cx="12" cy="8" r="2.2" fill="#111"/>',
  dot: '<circle cx="12" cy="12" r="5" fill="COLOR" stroke="#111" stroke-width="1.5"/>',
  fire: '<path d="M12 3c1 4 5 5 5 10a5 5 0 0 1-10 0c0-2 1-3 2-4 0 2 1 3 2 3 0-3-1-5 1-9z" fill="COLOR" stroke="#111" stroke-width="1.2"/>',
  mine: '<path d="M4 16l6-6 4 4-6 6z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M13 7l4-4 4 4-4 4z" fill="COLOR" stroke="#111" stroke-width="1.2"/>',
  house: '<path d="M4 11l8-7 8 7v9H4z" fill="COLOR" stroke="#111" stroke-width="1.2"/><path d="M10 20v-6h4v6" fill="#111"/>',
  // a ward (guard stone): a shield with its glowing core
  ward: '<path d="M12 2.5l8 3v6c0 5-3.5 8.5-8 10-4.5-1.5-8-5-8-10v-6z" fill="COLOR" stroke="#111" stroke-width="1.2" stroke-linejoin="round"/><path d="M12 7v9M8.5 10.5h7" stroke="#111" stroke-width="1.8"/>',
  invasion: '<circle cx="12" cy="12" r="9.5" fill="#111" stroke="COLOR" stroke-width="2"/><path d="M10.6 5.5h2.8l-.5 8.5h-1.8z" fill="COLOR"/><circle cx="12" cy="17.3" r="1.6" fill="COLOR"/>',
  player: '<path d="M12 2l8 20-8-5-8 5z" fill="COLOR" stroke="#111" stroke-width="1.4" stroke-linejoin="round"/>',
};

export const colors = {
  spawn: '#7cff4f', boss: '#ff5c5c',   // spawn: a bright lime nothing else uses
  // dungeon kinds, each its own colour, from its biome and clear of the other markers' colours
  crypt: '#7fa86a', sunkencrypt: '#9aa04e', trollcave: '#a7764c', frostcave: '#9fd1ee', infestedmine: '#7f95b8', fortress: '#c94a32',
  bearcave: '#b08a5e', hole: '#b9d4e6', morkhalla: '#9b86e0', fulingcamp: '#d9b26b', tarpit: '#8f8578',
  forge: '#ff8a4c', memorial: '#c7d4e6', orb: '#7fd3ff', hotspring: '#6fd6c4', drakenest: '#a9c7e8', gucktree: '#9fd35a', sign: '#c9a46a', peak: '#a7a39a', landmark: '#7fd3ff',
  trader: '#f2c14e', hildir: '#f2c14e', bogwitch: '#9be37a', miniboss: '#ff9d4d', hildir1: '#ff9d4d', hildir2: '#ff9d4d', hildir3: '#ff9d4d', dungeon: '#c9a5ff', cave: '#9fd8ff', camp: '#ff9d4d',
  village: '#e0c39a', ruin: '#bfc7d2', runestone: '#8fd3ff', wreck: '#bfc7d2', poi: '#9aa5b5', portal: '#5ce0e6',
  tombstone: '#d6d6d6', boat: '#8fc7ff', cart: '#d1b48c', pin: '#6fb7ff', dot: '#6fb7ff', fire: '#ff9d4d',
  ward: '#ffcf5c', wardoff: '#8c8f96', vegvisir: '#6fe0c8', bed: '#e8b4ff',
  invasion: '#ff3b2f', mine: '#c7c7c7', maptable: '#e8d3a3', house: '#9aa5b5', base: '#9aa5b5', custom: '#6fb7ff',   // homes in gray
};

const ALIAS = { hildir: 'trader', bogwitch: 'trader', hildir1: 'boss', hildir2: 'boss', hildir3: 'boss', miniboss: 'boss' };   // the traders share the bag on lists (the map uses the game's own icons)
export function iconSvg(name, color) {
  const g = glyphs[name] || glyphs[ALIAS[name]] || glyphs.poi;
  return `<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">${g.split('COLOR').join(color || colors[name] || '#9aa5b5')}</svg>`;
}

export const materialColors = [
  '#a07446', '#845c38', '#5c422e', '#9a9892', '#424252', '#767e8c', '#c6a258', '#e68232', '#5ac8d2', '#aad2e6', '#788088', '#aa643c', '#96826e', '#b4aa96', '#704a34',
];
export const materialNames = ['Wood', 'Core wood', 'Dark wood', 'Stone', 'Black marble', 'Iron', 'Thatch', 'Fire', 'Portal', 'Crystal', 'Grausten', 'Flametal', 'Misc', 'Cloth', 'Ashwood'];
