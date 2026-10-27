// Trees, Buildings and World structures drawn on the GPU (WebGL2), all in one canvas.
//
// The canvas-per-tile layers drew every crown and wall piece again for each tile at each zoom,
// on the main thread, right when the zoom animation needed it: that was most of the stutter.
// Here every shape is uploaded once per chunk as a few numbers (instanced quads) and a redraw is
// one cheap draw call per chunk, so the view can be redrawn on every frame of a drag. The canvas
// covers the screen plus half a screen on each side, so a zoom-out by one step is already drawn,
// and Leaflet CSS-scales it during zoom animations like its own vector renderer.
//
// Same looks as the canvas layers they replace (veg.js, structures.js, ruins.js), which stay as
// the fallback for browsers without WebGL2.

import { WORLD_HALF, chunkOf, metersPerPixel, CHUNKS } from '../crs.js';
import { chunks, vegetation } from '../data.js';
import { materialColors, materialNames } from '../icons.js';
import { ruins } from './ruins.js';
import { TREE_STRIDE, vegShowMask } from '../vegpack.js';
import { layerState } from '../layerstate.js';

export function webgl2Available() {
  if (/[?&]gl=0\b/.test(location.search)) return false;   // ?gl=0 forces the old canvas layers (testing)
  try { return !!document.createElement('canvas').getContext('webgl2'); } catch { return false; }
}

const FADE_MS = 250;             // new chunks and layers crossing their zoom limit fade in over this
const PAD = 0.75;                // canvas reaches 3/4 of a screen past each edge: covers a one-notch wheel zoom-out (~1.25 levels)
export const TREES_MIN = 4.5;
const CACHE_BELOW = 7;            // ShapeCache: mid-zoom below this, trees, ruins and buildings are shown from a picture...
const CACHE_SIZE = 1.6;           // ...of 1.6 x the screen's width and height...
const CACHE_FRAMES = 6;           // ...drawn over this many frames
const DATA_MS = 250;              // ShapesCanvas.dataChanged: new data on screen drawn at most this often
const SMALL_FROM = 5.5;           // TreesGL: the small shapes ease in from here to half a level up   // trees are drawn from tile zoom 5 up, like the baked tree tiles were
const BUILDINGS_MIN_ZOOM = 2, DETAIL_ZOOM = 5;
const DOT_CELL = 3;                // metres: zoomed out (below DETAIL_ZOOM, 5.7+ m a pixel) one ruin or building dot per this square

// ---------------------------------------------------------------- GL helpers

function compile(gl, vs, fs) {
  const p = gl.createProgram();
  for (const [type, src] of [[gl.VERTEX_SHADER, vs], [gl.FRAGMENT_SHADER, fs]]) {
    const s = gl.createShader(type);
    gl.shaderSource(s, src); gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error('shader: ' + gl.getShaderInfoLog(s));
    gl.attachShader(p, s);
  }
  gl.linkProgram(p);
  if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error('program: ' + gl.getProgramInfoLog(p));
  const u = {};
  const n = gl.getProgramParameter(p, gl.ACTIVE_UNIFORMS);
  for (let i = 0; i < n; i++) { const name = gl.getActiveUniform(p, i).name; u[name] = gl.getUniformLocation(p, name); }
  return { p, u };
}

const hexRgb = (hex) => { const n = parseInt(hex.slice(1), 16); return [n >> 16, (n >> 8) & 255, n & 255]; };

// shared vertex code: world metres (x, z) to clip space for the current canvas
const VIEW_GLSL = `
uniform vec4 u_view;   // originX, originZ (top-left of the canvas, metres), pixels per metre, unused
uniform vec2 u_css;    // canvas size in CSS pixels
vec2 toPx(vec2 w) { return vec2((w.x - u_view.x) * u_view.z, (u_view.y - w.y) * u_view.z); }
vec4 toClip(vec2 px) { return vec4(px.x / u_css.x * 2.0 - 1.0, 1.0 - px.y / u_css.y * 2.0, 0.0, 1.0); }
`;

// ---------------------------------------------------------------- rectangles (buildings, ruins)

const RECT_VS = `#version 300 es
in vec2 a_corner;                 // -0.5 .. 0.5
in vec2 a_center; in vec2 a_size; in float a_yaw; in float a_h; in vec4 a_color;
uniform float u_dotPx;            // > 0: zoomed out, a fixed square of this half-size, no rotation
uniform float u_minPx;
uniform uint u_sites;             // world structures: kinds of place shown, a bit each (RUIN_SITES; a_color.a is the kind, 255 none)
out vec2 v_local; out vec2 v_half; out vec4 v_color; out float v_h;
${VIEW_GLSL}
void main() {
  uint site = uint(a_color.a * 255.0 + 0.5);
  if (site < 32u && ((u_sites >> site) & 1u) == 0u) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }   // a hidden kind: nothing drawn
  vec2 c = toPx(a_center);
  vec2 sz = u_dotPx > 0.0 ? vec2(u_dotPx * 2.0) : max(a_size * u_view.z, vec2(u_minPx));
  vec2 l = a_corner * (sz + 2.0);                       // one spare pixel all round for the soft edge
  float r = u_dotPx > 0.0 ? 0.0 : radians(a_yaw), cs = cos(r), sn = sin(r);   // canvas convention: +yaw turns clockwise on screen
  vec2 p = c + vec2(l.x * cs - l.y * sn, l.x * sn + l.y * cs);
  v_local = l; v_half = sz * 0.5; v_color = a_color; v_h = a_h;
  gl_Position = toClip(p);
}`;

const RECT_FS = `#version 300 es
precision mediump float;
in vec2 v_local; in vec2 v_half; in vec4 v_color; in float v_h;
uniform float u_alpha;
uniform float u_fade;             // fading in (new chunk, layer appearing)
uniform int u_edge;               // 0 none, 1 Buildings close up (dark outline, lighter roofs), 2 ruins (light edge)
uniform float u_edgeK;            // how far in that look is (it eases in over a zoom range, no jump)
out vec4 o;
void main() {
  vec3 col = v_color.rgb;
  float edge = min(v_half.x - abs(v_local.x), v_half.y - abs(v_local.y));   // pixels inside the border
  float cover = clamp(edge + 0.5, 0.0, 1.0);                                  // antialiased edge, no MSAA needed
  if (cover <= 0.0) discard;
  // outlines ease in as a piece grows on screen, instead of switching on at a size: a hard
  // threshold flipped thousands of pieces at once at some zooms and the colour jumped
  float big = max(v_half.x, v_half.y);
  if (u_edge == 1) {
    float k = u_edgeK * smoothstep(1.5, 3.0, big);
    if (v_h >= 1.5 && v_local.y < 0.0) col = mix(col, vec3(1.0), 0.12 * k);
    if (edge < 1.0) col = mix(col, vec3(0.0), 0.45 * k);
  } else if (u_edge == 2) {
    float k = u_edgeK * smoothstep(1.0, 2.5, big);
    if (edge < 1.15) col = mix(col, vec3(0.91, 0.863, 0.769), 0.75 * k);
  }
  float a = u_alpha * cover * u_fade;
  o = vec4(col * a, a);
}`;

// ---------------------------------------------------------------- tree crowns and boulders

const TREE_VS = `#version 300 es
in vec2 a_corner;
in vec2 a_center; in float a_r; in float a_seed; in vec4 a_color;   // a_color.a: flags (vegpack.js): rock 1, wet 2, kind << 2
uniform uvec2 u_show;                                                // kinds shown, a bit each (kinds 0-31, 32-63: vegpack.js vegShowMask)
out vec2 v_l; out float v_r; out float v_sh; out vec3 v_color; out float v_rock; out float v_seed; out float v_wet;
${VIEW_GLSL}
out float v_cover;
void main() {
  float r0 = a_r * u_view.z;
  // Zoomed out, where trees fade in, the screen holds the most of them (hundreds of thousands)
  // and a bush or plant is a tenth of a pixel: drawing each as a faint dot cost the GPU the most
  // right there, the zoom's hitch. One under a third of a pixel across isn't drawn.
  if (r0 < 0.17) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
  float r = max(r0, 0.8);                              // zoomed out a crown is under a pixel: draw a dot...
  v_cover = min(1.0, (r0 * r0) / (r * r));             // ...as faint as the crown's real area
  float sh = min(r0 * 0.35, 3.0 * u_view.z);         // shadow offset to the south-east
  float half_ = r + sh * 0.5 + 0.5;                  // the edges are smoothed over +-0.5 px
  vec2 l = vec2(sh * 0.5) + a_corner * 2.0 * half_;   // relative to the crown centre
  int fl = int(a_color.a * 255.0 + 0.5);
  uint kind = uint(fl >> 2);
  if ((((kind < 32u ? u_show.x : u_show.y) >> (kind & 31u)) & 1u) == 0u) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }   // a hidden kind: off screen, nothing drawn
  v_rock = float(fl & 1); v_wet = float((fl >> 1) & 1);
  // each tree a shade lighter or darker than its neighbours, so a forest isn't one flat green
  float jit = v_rock > 0.5 ? 0.0 : (fract(sin(a_seed * 12.9898 + a_center.x * 0.37) * 43758.5453) - 0.5) * 0.18;
  v_l = l; v_r = r; v_sh = sh; v_color = a_color.rgb * (1.0 + jit); v_seed = a_seed;
  gl_Position = toClip(toPx(a_center) + l);
}`;

const TREE_FS = `#version 300 es
precision mediump float;
in vec2 v_l; in float v_r; in float v_sh; in vec3 v_color; in float v_rock; in float v_seed; in float v_cover; in float v_wet;
uniform float u_fade;
out vec4 o;
void main() {
  float r = v_r;
  float aS = 0.24 * (1.0 - smoothstep(0.95 * r - 0.5, 0.95 * r + 0.5, length(v_l - vec2(v_sh))));
  bool rock = v_rock > 0.5;
  float aC = (rock ? 0.95 : 0.92) * (1.0 - smoothstep(r - 0.5, r + 0.5, length(v_l)));
  if (aS + aC <= 0.0) discard;
  // lit from the north-west, darker toward the south-east rim (the canvas radial gradient)
  float t = clamp((length(v_l + vec2(0.4 * r)) - 0.1 * r) / (1.25 * r), 0.0, 1.0);
  float hi = rock ? 0.35 : 0.55;
  float k = t < 0.6 ? mix(1.0 + hi * 0.8, 1.0, t / 0.6) : mix(1.0, 0.7 - hi * 0.2, (t - 0.6) / 0.4);
  vec3 col = min(v_color * k, vec3(1.0));
  if (!rock && r > 6.0) {   // a few darker lobes so a big crown reads as foliage, not a coin
    for (int i = 0; i < 3; i++) {
      float a = v_seed + float(i) * 2.1;
      if (length(v_l - vec2(cos(a), sin(a)) * 0.45 * r) < 0.38 * r) col = mix(col, v_color * 0.6, 0.18);
    }
  }
  // in shallow water: faded into the sea, and no shadow
  if (v_wet > 0.5) { col = mix(col, vec3(0.2, 0.35, 0.45), 0.35); aC *= 0.5; aS = 0.0; }
  float f = v_cover * u_fade;
  o = vec4(col * aC * f, (aC + aS * (1.0 - aC)) * f);   // crown over its own shadow, premultiplied
}`;

// ---------------------------------------------------------------- the shared canvas

// a textured rectangle in world metres: map tiles and the fog (layers/ground.js)
const TEX_VS = `#version 300 es
in vec2 a_corner;
uniform vec4 u_rect;   // minX, maxZ, width, height (metres)
uniform vec4 u_uv;     // u0, v0, u1, v1
out vec2 v_uv;
${VIEW_GLSL}
void main() {
  vec2 t = a_corner + 0.5;
  v_uv = mix(u_uv.xy, u_uv.zw, t);
  gl_Position = toClip(toPx(vec2(u_rect.x + t.x * u_rect.z, u_rect.y - t.y * u_rect.w)));
}`;
const TEX_FS = `#version 300 es
precision mediump float;
in vec2 v_uv;
uniform sampler2D u_tex;
uniform float u_alpha;
out vec4 o;
void main() { o = texture(u_tex, v_uv) * u_alpha; }`;

// Contour lines from a height tile (R32F, metres, row 0 north), worked out per pixel: how far this
// pixel is from the nearest line, in screen pixels (the height's change across a pixel, fwidth), so
// lines are a pixel wide and sharp at any zoom. The same heights at every zoom (a thin line each
// u_interval, a bold one each u_major), so a hill looks the same zoomed in or out; thin lines closer
// together than a few pixels fade out (the bold ones stay) instead of turning the slope solid. On
// nearly flat ground a line would smear into a wide band wherever the land sits at its height (a
// plain at 25 m): there lines fade out, as a hiking map leaves flats bare.
const CONTOUR_FS = `#version 300 es
precision highp float;
in vec2 v_uv;
uniform highp sampler2D u_h;
uniform float u_interval;   // metres between thin lines
uniform float u_major;      // metres between bold lines
uniform float u_mpp;        // metres a screen pixel covers
uniform float u_bold;       // 0..1: how much bolder the 100 m lines are (less zoomed out)
uniform float u_alpha;
out vec4 o;
float hAt(ivec2 p) { return texelFetch(u_h, clamp(p, ivec2(0), ivec2(255)), 0).r; }
float line(float h, float step, float w, float half_, float strength) {
  float d = abs(fract(h / step + 0.5) - 0.5) * step / w;          // pixels to the nearest line
  float room = clamp((step / w - 3.0) / 3.0, 0.0, 1.0);            // lines under ~3-6 px apart fade
  return clamp(half_ + 0.5 - d, 0.0, 1.0) * strength * room;
}
void main() {
  vec2 t = v_uv * 256.0 - 0.5;
  ivec2 i = ivec2(floor(t));
  vec2 f = fract(t);
  float h = mix(mix(hAt(i), hAt(i + ivec2(1, 0)), f.x), mix(hAt(i + ivec2(0, 1)), hAt(i + ivec2(1, 1)), f.x), f.y) - 30.0;
  float w = max(fwidth(h), 1e-4);
  float slope = w / u_mpp;                                           // height change per metre across
  float flat_ = smoothstep(0.02, 0.06, slope);                       // under 2% bare, full from 6%
  w = max(w, 0.06 * u_mpp);                                          // and never wider than on a 6% slope
  float a = max(line(h, u_interval, w, 0.45, 0.46), line(h, u_major, w, 0.45 + 0.35 * u_bold, 0.5 + 0.22 * u_bold));
  a *= step(u_interval * 0.5, h) * flat_;                          // nothing at or under the sea, or on the flat
  o = vec4(vec3(0.157, 0.094, 0.039) * a, a) * u_alpha;            // premultiplied
}`;

// The "Height" map style: the land coloured by its height (greens low, tans and browns up the
// hills, grey rock, white tops; the sea darker as it deepens), lit from the north-west by the slope
// for relief. From the same height tiles as the contour lines.
const HEIGHT_FS = `#version 300 es
precision highp float;
in vec2 v_uv;
uniform highp sampler2D u_h;
uniform float u_mpp;
uniform float u_alpha;
out vec4 o;
float hAt(ivec2 p) { return texelFetch(u_h, clamp(p, ivec2(0), ivec2(255)), 0).r; }
vec3 ramp(float h) {
  if (h < 0.0) return mix(vec3(0.20, 0.38, 0.58), vec3(0.05, 0.12, 0.26), clamp(-h / 60.0, 0.0, 1.0));
  vec3 c = mix(vec3(0.36, 0.58, 0.32), vec3(0.62, 0.70, 0.40), smoothstep(0.0, 40.0, h));
  c = mix(c, vec3(0.78, 0.68, 0.46), smoothstep(40.0, 100.0, h));
  c = mix(c, vec3(0.60, 0.47, 0.35), smoothstep(100.0, 180.0, h));
  c = mix(c, vec3(0.70, 0.69, 0.67), smoothstep(180.0, 270.0, h));
  return mix(c, vec3(0.97, 0.97, 0.99), smoothstep(270.0, 370.0, h));
}
void main() {
  vec2 t = v_uv * 256.0 - 0.5;
  ivec2 i = ivec2(floor(t));
  vec2 f = fract(t);
  float h = mix(mix(hAt(i), hAt(i + ivec2(1, 0)), f.x), mix(hAt(i + ivec2(0, 1)), hAt(i + ivec2(1, 1)), f.x), f.y) - 30.0;
  float gx = dFdx(h) / u_mpp, gy = dFdy(h) / u_mpp;               // slope, metres per metre (screen y runs south)
  float shade = 1.0 + 0.45 * clamp((gx + gy) * 0.8, -1.0, 1.0) * step(0.0, h);
  o = vec4(ramp(h) * shade, 1.0) * u_alpha;
}`;

// Soft dark clouds in world space past the world's edge, like the seed maps' surround (generated
// here, nothing to download); darker toward the rim. Inside the circle the fog stays plain black.
const CLOUDS_GLSL = `
float hash2(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
float vnoise(vec2 p) {
  vec2 i = floor(p), f = fract(p), u = f * f * (3.0 - 2.0 * f);
  return mix(mix(hash2(i), hash2(i + vec2(1.0, 0.0)), u.x), mix(hash2(i + vec2(0.0, 1.0)), hash2(i + vec2(1.0, 1.0)), u.x), u.y);
}
float fbm(vec2 p) {
  float s = 0.0, a = 0.5;
  for (int i = 0; i < 5; i++) { s += a * vnoise(p); p = p * 2.03 + vec2(17.1, 9.7); a *= 0.5; }
  return s;
}
vec3 clouds(vec2 w) {
  float big = fbm(w / 2600.0), fine = fbm(w / 650.0 + 3.1);
  float v = smoothstep(0.32, 0.78, big * 0.7 + fine * 0.3);
  vec3 c = mix(vec3(0.045, 0.055, 0.070), vec3(0.330, 0.360, 0.410), v);
  return c * mix(1.0, 0.8, smoothstep(0.55, 1.05, length(w) / 10000.0));   // vignette toward the rim
}`;

// past the world's edge (layers/ground.js WorldEdgeGL)
const EDGE_VS = `#version 300 es
in vec2 a_corner;
uniform vec4 u_rect;
out vec2 v_w;
${VIEW_GLSL}
void main() {
  vec2 t = a_corner + 0.5;
  v_w = vec2(u_rect.x + t.x * u_rect.z, u_rect.y - t.y * u_rect.w);
  gl_Position = toClip(toPx(v_w));
}`;
// The clouds don't move, so they are worked out once into a texture (CLOUD_BAKE_FS, ShapesCanvas
// .cloudTexture) covering CLOUD_EXTENT metres each way, and the edge just samples it: computing
// the noise per pixel each frame was the map's heaviest work once zoomed out past ~4.3 (the padded
// canvas reaching past the rim), most of all on high-DPI screens.
export const CLOUD_EXTENT = 36000, CLOUD_RES = 2048;
const CLOUD_BAKE_VS = `#version 300 es
in vec2 a_corner;
out vec2 v_w;
uniform float u_extent;
void main() { v_w = a_corner * 2.0 * u_extent; gl_Position = vec4(a_corner * 2.0, 0.0, 1.0); }`;
const CLOUD_BAKE_FS = `#version 300 es
precision highp float;
in vec2 v_w;
out vec4 o;
${CLOUDS_GLSL}
void main() { o = vec4(clouds(v_w), 1.0); }`;
const EDGE_MAIN = `
void main() {
  float d = length(v_w);
  // The world's edge: a fine blue line at the exact edge; inside it the sea's blue fades into the
  // map, outside it a soft glow fades out into space. Widths in metres, never under a few pixels.
  float R = u_radius;
  float outside = smoothstep(R - 0.5 * u_mpp, R + 0.5 * u_mpp, d);           // space beyond the edge
  // inside, only a thin band: land in the far north and south runs right up to the edge
  float fin = max(120.0, 3.0 * u_mpp), fout = max(350.0, 9.0 * u_mpp);
  float inner = (1.0 - outside) * smoothstep(R - fin, R, d) * 0.45;         // a hint of blue just inside the line
  float t = max(0.0, d - R) / fout;
  float line = (1.0 - smoothstep(0.5 * u_mpp, 1.6 * u_mpp, abs(d - R))) * 0.85;
  float a = max(max(outside, inner), line);
  if (a <= 0.003) discard;
  vec3 ocean = vec3(18.0, 41.0, 77.0) / 255.0;   // the map tiles' own deep sea (Palette.WaterDeep as drawn)
  vec3 beyond = mix(clouds(v_w), ocean, exp(-t * t) * 0.6);                // space, with the glow near the edge
  vec3 c = mix(ocean, beyond, outside);                                     // inside: the blue fade
  c = mix(c, vec3(0.36, 0.55, 0.72), line / a);   // the edge line: a lighter shade of the same sea
  o = vec4(c * a, a);
}`;
const EDGE_HEAD = `#version 300 es
precision highp float;
in vec2 v_w;
uniform float u_radius, u_mpp;   // world radius, metres per pixel (for a 1 px soft edge)
uniform vec3 u_ring;
out vec4 o;
`;
// sampling the baked clouds, or (if the browser couldn't bake them) working them out per pixel
const EDGE_FS = EDGE_HEAD + `uniform sampler2D u_clouds;
uniform float u_extent;
vec3 clouds(vec2 w) { return texture(u_clouds, w / (2.0 * u_extent) + 0.5).rgb; }` + EDGE_MAIN;
const EDGE_LIVE_FS = EDGE_HEAD + CLOUDS_GLSL + EDGE_MAIN;
// the in-game map's own backdrop past the edge of the world: its "space" texture (the game's files,
// see World/MapIcons on the server), tiled every SPACE_TILE metres, darker toward the rim like the
// game's map
export const SPACE_TILE = 22000;
const EDGE_SPACE_FS = EDGE_HEAD + `uniform sampler2D u_space;
vec3 clouds(vec2 w) {
  vec3 c = texture(u_space, w / ${SPACE_TILE.toFixed(1)}).rgb;
  return c * mix(0.55, 1.0, smoothstep(0.98, 1.25, length(w) / u_radius));
}` + EDGE_MAIN;

// The Mistlands mist (layers/ground.js MistGL): the server's mask of where the game's own map has
// it (World/BiomeMap), light grey and patchy, with a clearing around each Wisplight. The mist itself
// (mask, patches, colour) is worked out once into a texture whenever the mask changes (MIST_BAKE_FS,
// MistGL.bake), so drawing it is one texture read a pixel, like the fog; the noise worked out per
// pixel each frame made a frame ~40% dearer on a slow GPU.
export const MIST_RES = 2048;
const MIST_BAKE_FS = `#version 300 es
precision highp float;
in vec2 v_w;
uniform sampler2D u_mask;
uniform float u_extent;
out vec4 o;
float hash2(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
float vnoise(vec2 p) {
  vec2 i = floor(p), f = fract(p), u = f * f * (3.0 - 2.0 * f);
  return mix(mix(hash2(i), hash2(i + vec2(1.0, 0.0)), u.x), mix(hash2(i + vec2(0.0, 1.0)), hash2(i + vec2(1.0, 1.0)), u.x), u.y);
}
void main() {
  float m = texture(u_mask, vec2(v_w.x / (2.0 * u_extent) + 0.5, 0.5 - v_w.y / (2.0 * u_extent))).r;
  if (m < 0.004) { o = vec4(0.0); return; }
  float n = vnoise(v_w / 220.0) * 0.55 + vnoise(v_w / 70.0 + 7.3) * 0.3 + vnoise(v_w / 23.0 + 3.1) * 0.15;
  float a = smoothstep(0.0, 0.6, m) * (0.8 + 0.18 * n);
  o = vec4(mix(vec3(0.66, 0.67, 0.72), vec3(0.88, 0.89, 0.92), n) * a, a);
}`;
const MIST_FS = `#version 300 es
precision highp float;
in vec2 v_w;
uniform sampler2D u_mist;
uniform float u_half, u_mpp;
uniform vec4 u_wisp[16];   // x, z, radius (metres), unused
uniform int u_nwisp;
out vec4 o;
void main() {
  vec4 c = texture(u_mist, v_w / (2.0 * u_half) + 0.5);
  if (c.a < 0.004) discard;
  float k = 1.0;
  for (int i = 0; i < 16; i++) {
    if (i >= u_nwisp) break;
    float r = u_wisp[i].z;   // the wisp's own range in metres, the same at every zoom
    k *= smoothstep(r * 0.5, r + 2.0 * u_mpp, distance(v_w, u_wisp[i].xy));
  }
  o = c * k;
}`;

// The game's alt biomes (its biome variants, layers/ground.js AltBiomeGL): diagonal stripes over
// the server's biome grid (data/biomes.grid, values from u_base up) in their biome's colour (u_lut:
// a colour per grid value), a steady width on screen at any zoom, soft edges from the four nearest cells.
export const ALT_COLOURS = { Meadows: '#f2f25a', 'Black Forest': '#3ce07a', Swamp: '#ff6a3d', Mountain: '#4cc3ff',
  Plains: '#ffb020', Mistlands: '#c79cff', Ashlands: '#ff5ca8', 'Deep North': '#5af2e0' };
const ALT_FS = `#version 300 es
precision highp float;
in vec2 v_w;
uniform sampler2D u_grid, u_lut;
uniform float u_half, u_mpp, u_base, u_alpha;
out vec4 o;
float idAt(ivec2 c) { c = clamp(c, ivec2(0), ivec2(1023)); return floor(texelFetch(u_grid, c, 0).r * 255.0 + 0.5); }
void main() {
  vec2 g = vec2(v_w.x / (2.0 * u_half) + 0.5, 0.5 - v_w.y / (2.0 * u_half)) * 1024.0 - 0.5;
  ivec2 c0 = ivec2(floor(g)); vec2 f = fract(g);
  float a = idAt(c0), b = idAt(c0 + ivec2(1, 0)), c = idAt(c0 + ivec2(0, 1)), d = idAt(c0 + ivec2(1, 1));
  float m = mix(mix(step(u_base, a), step(u_base, b), f.x), mix(step(u_base, c), step(u_base, d), f.x), f.y);
  float cov = smoothstep(0.3, 0.7, m);
  if (cov <= 0.0) discard;
  float id = f.x < 0.5 ? (f.y < 0.5 ? a : c) : (f.y < 0.5 ? b : d);   // the nearest cell's variant
  if (id < u_base) id = max(max(a, b), max(c, d));
  vec3 col = texelFetch(u_lut, ivec2(int(id), 0), 0).rgb;
  float period = max(14.0 * u_mpp, 18.0);   // ~14 px apart on screen, never closer than 18 m
  float s = abs(fract((v_w.x + v_w.y) / period) - 0.5) * 2.0;
  float stripe = 1.0 - smoothstep(0.45, 0.45 + 3.0 * u_mpp / period, s);
  float alpha = (0.18 + 0.62 * stripe) * cov * u_alpha;
  o = vec4(col * alpha, alpha);
}`;

// The 256 m chunk grid and the distance rings around the spawn, worked out per pixel over the
// whole canvas (layers/ground.js GuideGL): no geometry to rebuild as the zoom changes, so they cost
// the same at any zoom. Lines are a set width in pixels, anti-aliased.
const GUIDE_FS = `#version 300 es
precision highp float;
in vec2 v_w;
uniform float u_mpp, u_radius;
uniform float u_grid, u_rings;   // 0 or 1
uniform vec2 u_spawn;
uniform vec3 u_ringColor;
out vec4 o;
float line(float dPx, float w) { return 1.0 - smoothstep(w * 0.5 - 0.5, w * 0.5 + 0.5, dPx); }
void main() {
  if (length(v_w) > u_radius) discard;
  float a = 0.0; vec3 c = vec3(1.0);
  if (u_grid > 0.5) {
    // the game's zones: 64 m, zone (0, 0) centred on the world's middle (edges at 32 + 64k). Zoomed
    // out, every 2nd, 4th... zone's edge, so the lines stay at least ~16 px apart
    float k = max(0.0, ceil(log2(16.0 * u_mpp / 64.0)));
    float period = 64.0 * exp2(k);
    vec2 d = abs(fract((v_w + 32.0) / period + 0.5) - 0.5) * period / u_mpp;
    a = max(a, line(min(d.x, d.y), 1.0) * 0.22);
  }
  if (u_rings > 0.5) {
    vec2 rel = v_w - u_spawn;
    float r = length(rel);
    // whole rings only: one that would run past the world's edge (the spawn is off centre) is left out
    if (r > 250.0 && floor((r + 250.0) / 500.0) * 500.0 + length(u_spawn) <= u_radius) {
      float k = floor((r + 250.0) / 500.0);            // nearest ring, every 500 m
      float d = abs(r - k * 500.0) / u_mpp;
      bool km = mod(k, 2.0) < 0.5;
      float on = 1.0;
      if (!km) {
        // half-km rings dashed, the dashes fixed to the ground (metres along the ring, not pixels:
        // pixel dashes slid round the ring as the zoom changed). The dash length is a power of two
        // in metres near 10 px, cross-faded between the two nearest, so they never pop either.
        float s = (atan(rel.y, rel.x) + 3.14159265) * k * 500.0;   // metres along the ring
        float lp = log2(10.0 * u_mpp), lo = floor(lp), f = lp - lo;
        float p1 = exp2(lo), p2 = p1 * 2.0;
        float a1 = step(mod(s, p1), p1 * 0.4), a2 = step(mod(s, p2), p2 * 0.4);
        on = mix(a1, a2, f);
      }
      float ra = line(d, km ? 1.4 : 0.9) * (km ? 0.75 : 0.45) * on;
      if (ra > 0.0) { c = mix(c, u_ringColor, ra / max(ra, a + 1e-4)); a = max(a, ra); }
    }
  }
  if (a <= 0.003) discard;
  o = vec4(c * a, a);
}`;

// Many small images in one draw (map markers, layers/markercanvas.js): each instance a rectangle in
// world metres and where its picture sits in a shared atlas texture.
const SPRITE_VS = `#version 300 es
in vec2 a_corner;
in vec4 a_rect;   // minX, maxZ, width, height (metres)
in vec4 a_uv;     // u0, v0, u1, v1 in the atlas
out vec2 v_uv;
${VIEW_GLSL}
void main() {
  vec2 t = a_corner + 0.5;
  v_uv = mix(a_uv.xy, a_uv.zw, t);
  gl_Position = toClip(toPx(vec2(a_rect.x + t.x * a_rect.z, a_rect.y - t.y * a_rect.w)));
}`;
const SPRITE_FS = `#version 300 es
precision mediump float;
in vec2 v_uv;
uniform sampler2D u_tex;
out vec4 o;
void main() { o = texture(u_tex, v_uv); }`;

// a dashed line between two world points, a set width in pixels (portal lines, layers/markercanvas.js)
// A ward's protected ground: a dashed ring like the game's area marker, and a faint fill. The
// dashes are a fixed number around the circle, so they stay put as the map zooms; a ring too small
// to show them is drawn solid.
const RING_VS = `#version 300 es
in vec2 a_corner;
uniform vec2 u_c;                 // centre, metres
uniform float u_r;                // radius, metres
out vec2 v_p; out float v_R;
${VIEW_GLSL}
void main() {
  float R = u_r * u_view.z;
  v_p = a_corner * 2.0 * (R + 3.0); v_R = R;
  gl_Position = toClip(toPx(u_c) + v_p);
}`;
const RING_FS = `#version 300 es
precision highp float;
in vec2 v_p; in float v_R;
uniform vec3 u_color;
uniform float u_alpha, u_fill, u_n;
out vec4 o;
void main() {
  float d = length(v_p);
  float ring = clamp(1.3 - abs(d - v_R), 0.0, 1.0);
  float seg = fract(atan(v_p.y, v_p.x) / 6.2831853 * u_n);
  float dash = smoothstep(0.0, 0.06, seg) * (1.0 - smoothstep(0.55, 0.61, seg));
  dash = mix(1.0, dash, smoothstep(4.0, 8.0, 6.2831853 * v_R / u_n));   // dashes under ~4 px: a solid line
  float a = max(ring * dash, clamp(v_R - d + 0.5, 0.0, 1.0) * u_fill) * u_alpha;
  if (a <= 0.0) discard;
  o = vec4(u_color * a, a);
}`;

const LINE_VS = `#version 300 es
in vec2 a_corner;
uniform vec2 u_a, u_b;
uniform float u_width;
out float v_d; out float v_n;
${VIEW_GLSL}
void main() {
  vec2 pa = toPx(u_a), pb = toPx(u_b), d = pb - pa;
  float len = max(length(d), 0.001);
  vec2 dir = d / len, n = vec2(-dir.y, dir.x);
  float t = a_corner.x + 0.5, side = a_corner.y * 2.0 * (u_width * 0.5 + 1.0);
  v_d = t * len; v_n = side;
  gl_Position = toClip(pa + d * t + n * side);
}`;
// The dashes are laid out in metres along the line, not screen pixels, so they stay on the same
// spots of the map while it zooms (a pixel pattern slid along the line). Each zoom level has its
// own dash length, and the next level's fades in as the dashes grow, so on screen they stay
// about u_on / u_off pixels long.
const LINE_FS = `#version 300 es
precision highp float;   // u_width is shared with the vertex shader: precisions must match
in float v_d; in float v_n;
uniform float u_width, u_on, u_off, u_alpha, u_zoom, u_ppm;
uniform vec3 u_color;
out vec4 o;
float dashAt(float metres, float level) {
  float mpp = exp2(7.0 - level);                      // metres per pixel at that zoom
  float period = (u_on + u_off) * mpp;
  return mod(metres, period) < u_on * mpp ? 1.0 : 0.0;
}
void main() {
  float a = u_alpha * clamp(u_width * 0.5 + 0.5 - abs(v_n), 0.0, 1.0);
  if (u_off > 0.0) {
    float m = v_d / u_ppm, lv = floor(u_zoom), f = fract(u_zoom);
    a *= mix(dashAt(m, lv), dashAt(m, lv + 1.0), smoothstep(0.35, 0.95, f));
  }
  if (a <= 0.0) discard;
  o = vec4(u_color * a, a);
}`;

// the part of a padded view that is the screen itself
export function screenArea(v) {
  const px = (v.x1 - v.x) * PAD / (1 + 2 * PAD), pz = (v.z - v.z0) * PAD / (1 + 2 * PAD);
  return { x: v.x + px, x1: v.x1 - px, z: v.z - pz, z0: v.z0 + pz, zoom: v.zoom };
}

export class ShapesCanvas {
  static for(map) { return map._shapesCanvas || (map._shapesCanvas = new ShapesCanvas(map)); }

  constructor(map) {
    this.map = map;
    this.sets = [];
    const pane = map.getPane('shapesPane') || map.createPane('shapesPane');
    pane.style.zIndex = 245;            // over the tiles, under the fog (overlayPane)
    pane.style.pointerEvents = 'none';
    this.canvas = L.DomUtil.create('canvas', 'leaflet-zoom-animated', pane);
    this.canvas.style.position = 'absolute';
    // edges are smoothed in the shaders, so no multisampling: a quarter of the GPU work per frame
    const gl = this.gl = this.canvas.getContext('webgl2', { antialias: false, premultipliedAlpha: true, alpha: true });
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    this.quad = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, this.quad);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-0.5, -0.5, 0.5, -0.5, -0.5, 0.5, 0.5, 0.5]), gl.STATIC_DRAW);
    this.rect = compile(gl, RECT_VS, RECT_FS);
    this.tree = compile(gl, TREE_VS, TREE_FS);
    this.tex = compile(gl, TEX_VS, TEX_FS);
    this.edge = compile(gl, EDGE_VS, EDGE_FS);
    this.sprite = compile(gl, SPRITE_VS, SPRITE_FS);
    this.edgeLive = compile(gl, EDGE_VS, EDGE_LIVE_FS);
    try { this.edgeSpace = compile(gl, EDGE_VS, EDGE_SPACE_FS); } catch (e) { this.edgeSpace = null; }
    try { this.cloudBake = compile(gl, CLOUD_BAKE_VS, CLOUD_BAKE_FS); } catch (e) { this.cloudBake = null; }
    this.guide = compile(gl, EDGE_VS, GUIDE_FS);
    try { this.contour = compile(gl, TEX_VS, CONTOUR_FS); } catch (e) { this.contour = null; }
    try { this.heightFill = compile(gl, TEX_VS, HEIGHT_FS); } catch (e) { this.heightFill = null; }
    try { this.alt = compile(gl, EDGE_VS, ALT_FS); } catch (e) { this.alt = null; }
    try { this.mist = compile(gl, EDGE_VS, MIST_FS); this.mistBake = compile(gl, CLOUD_BAKE_VS, MIST_BAKE_FS); } catch (e) { this.mist = this.mistBake = null; }
    this.line = compile(gl, LINE_VS, LINE_FS);
    this.ring = compile(gl, RING_VS, RING_FS);
    this.quadVao = gl.createVertexArray();
    gl.bindVertexArray(this.quadVao);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.quad);
    const lc = gl.getAttribLocation(this.tex.p, 'a_corner');
    gl.enableVertexAttribArray(lc);
    gl.vertexAttribPointer(lc, 2, gl.FLOAT, false, 0, 0);
    gl.bindVertexArray(null);
    this.frame = 0;
    this.cache = new ShapeCache(this);
    map.on('zoomanim', this.onZoomAnim, this);
    map.on('zoom', this.onZoom, this);
    map.on('move', this.onMove, this);
    map.on('moveend zoomend viewreset resize', this.reset, this);
  }

  add(set) { if (!this.sets.includes(set)) { this.sets.push(set); this.sets.sort((a, b) => a.order - b.order); } this.reset(); }
  remove(set) { this.sets = this.sets.filter((s) => s !== set); this.staticDirty = true; this.redraw(); }

  // the layout Leaflet's own L.Renderer uses: a canvas centred on the view, padded
  reset() {
    const map = this.map;
    if (map._animatingZoom) return;
    this.zoomDirty = false;
    const size = map.getSize(), pad = size.multiplyBy(PAD).round();
    const min = map.containerPointToLayerPoint(pad.multiplyBy(-1)).round();
    const w = size.x + pad.x * 2, h = size.y + pad.y * 2;
    this.center = map.getCenter(); this.zoom = map.getZoom();
    this.min = min;
    L.DomUtil.setPosition(this.canvas, min);
    // screen resolution (at least 1x so it stays sharp), at most 2x, and at most ~16 million pixels
    const dpr = Math.max(1, Math.min(window.devicePixelRatio || 1, 2, Math.sqrt(16e6 / (w * h))));
    if (this.cssW !== w || this.cssH !== h || this.dpr !== dpr) {
      this.cssW = w; this.cssH = h; this.dpr = dpr;
      this.canvas.style.width = w + 'px'; this.canvas.style.height = h + 'px';
      this.canvas.width = Math.round(w * dpr); this.canvas.height = Math.round(h * dpr);
    }
    const tl = map.layerPointToLatLng(min);
    const ppm = 1 / metersPerPixel(this.zoom);
    this.view = { x: tl.lng, z: tl.lat, ppm, zoom: this.zoom, x1: tl.lng + w / ppm, z0: tl.lat - h / ppm };
    // a drag's re-centre (same zoom and size) only moves the still picture: drawStill shifts it
    const st = this.still;
    this.staticDirty = this.staticDirty === true || !st || !st.view || st.view.zoom !== this.zoom || st.dpr !== dpr || st.w !== this.canvas.width || st.h !== this.canvas.height ? true : 'move';
    for (const s of this.sets) s.need(this.view, this);
    this.draw();
    if (!map._gliding) this.idleWork();
  }

  // A drag moves the canvas with the map pane, so nothing needs drawing until the screen gets
  // within a quarter of the padding of the canvas edge; then re-centre (once per frame at most).
  onMove() {
    if (this.map._animatingZoom || this.moveQueued || !this.min) return;
    const tl = this.map.containerPointToLayerPoint([0, 0]).subtract(this.min);
    const size = this.map.getSize(), slack = size.multiplyBy(PAD * 0.25);
    if (tl.x > slack.x && tl.y > slack.y && tl.x + size.x < this.cssW - slack.x && tl.y + size.y < this.cssH - slack.y) return;
    this.moveQueued = true;
    requestAnimationFrame(() => { this.moveQueued = false; this.reset(); });
  }

  onZoomAnim(e) {
    // If the canvas was just moved (a reset at the end of a drag) in this same frame, the browser
    // would start the zoom's CSS transition from where it was before that move, and the shapes
    // would glide in from the wrong place. Reading the style makes it take the move first.
    void getComputedStyle(this.canvas).transform;
    this.updateTransform(e.center, e.zoom);
  }
  // A zoom without Leaflet's animation (the smooth zoom, a pinch): keep the old picture lined up
  // right away, and redraw it sharp at the new zoom next frame (the smooth zoom already redraws in
  // its own frame, which clears this).
  onZoom() {
    if (this.map._animatingZoom) return;
    this.updateTransform(this.map.getCenter(), this.map.getZoom());
    this.zoomDirty = true;
    if (!this.zoomQueued) {
      this.zoomQueued = true;
      requestAnimationFrame(() => { this.zoomQueued = false; if (this.zoomDirty) this.reset(); });
    }
  }
  updateTransform(center, zoom) {
    if (!this.center) return;
    const map = this.map;
    const scale = map.getZoomScale(zoom, this.zoom);
    const half = map.getSize().multiplyBy(0.5 + PAD);
    const off = half.multiplyBy(-scale).add(map.project(this.center, zoom)).subtract(map._getNewPixelOrigin(center, zoom));
    L.DomUtil.setTransform(this.canvas, off, scale);
  }

  redraw() { if (!this.drawQueued) { this.drawQueued = true; requestAnimationFrame(() => { this.drawQueued = false; this.draw(); }); } }

  // During a smooth zoom every frame is redrawn anyway, so only the part of the canvas on screen
  // is painted (a scissor) and the sets skip what is off screen (this.onScreen): the padding is
  // 5/6 of the canvas and only matters for a drag, which doesn't redraw. The whole canvas is drawn
  // again as soon as the zoom ends (smoothzoom.js).
  draw() {
    const gl = this.gl, v = this.view;
    if (!v) return;
    gl.viewport(0, 0, this.canvas.width, this.canvas.height);
    this.onScreen = !!this.map._gliding;
    if (this.onScreen) {
      const size = this.map.getSize(), d = this.dpr;
      const px = (this.cssW - size.x) / 2, py = (this.cssH - size.y) / 2;
      gl.enable(gl.SCISSOR_TEST);
      gl.scissor(Math.floor(px * d), Math.floor(this.canvas.height - (py + size.y) * d), Math.ceil(size.x * d) + 1, Math.ceil(size.y * d) + 1);
    }
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    // Trees, ruins and buildings (next to each other in the order) come from a picture: mid-zoom
    // the ShapeCache one, otherwise a picture of them as they were last drawn (drawStill), so a
    // redraw for something else (a marker hovered, a player moving, a label fading, every frame
    // of a drag) doesn't draw every one of them again
    const statics = this.sets.filter((s) => s instanceof ChunkShapes);
    const cached = this.onScreen && v.zoom < CACHE_BELOW;
    if (!cached) this.cache.reset();   // a new zoom starts a new picture
    let fromCache = null;
    for (const s of this.sets) {
      if (statics.includes(s)) {
        if (fromCache === null) fromCache = cached ? this.cache.draw(gl, v, statics) : this.onScreen ? false : this.drawStill(gl, v, statics);
        if (fromCache) continue;
      }
      s.draw(gl, v, this);
    }
    if (this.onScreen) gl.disable(gl.SCISSOR_TEST);
  }

  // New data for what is on screen (chunks arriving, blocks built): drawn in batches, at most every
  // DATA_MS. While a view loads, hundreds of chunks arrive; redrawing the whole map for each one
  // queued seconds of GPU work. (A view change still redraws at once: reset.)
  dataChanged() {
    if (this.dataTimer) return;
    this.dataTimer = setTimeout(() => { this.dataTimer = null; this.staticDirty = true; this.redraw(); }, DATA_MS);
  }

  // Work done while the map is still (an idle callback, a share at a time), so the first zoom
  // after a page load or a long move finds it done instead of doing it mid-zoom: the shapes
  // around the view fetched and built for the GPU whatever the zoom (trees too, from far out), the
  // mid-zoom pictures allocated and every program run once (a GPU compiles one at its first use).
  idleWork() {
    if (this.idleQueued) return;
    this.idleQueued = true;
    const run = () => { this.idleQueued = false; this.idle(); };
    if (window.requestIdleCallback) requestIdleCallback(run, { timeout: 3000 }); else setTimeout(run, 200);
  }
  idle() {
    const v = this.view, map = this.map;
    if (!v || map._gliding || map._animatingZoom) return;   // the next reset asks again
    const gl = this.gl;
    let more = false;
    const s = screenArea(v), mx = (s.x + s.x1) / 2, mz = (s.z + s.z0) / 2;
    for (const set of this.sets) {
      if (set instanceof ChunkShapes) {
        // every explored chunk in the end, nearest the view first (~9 MB once: browsers keep it)
        if (set.prefetch(mx, mz, 32)) more = true;
        if (set.dirty.size) { const quiet = !set.showing(); set.rebuild(gl, 8, quiet); more = more || set.dirty.size > 0; }
      } else if (set.prewarm && set.prewarm(gl, v)) more = true;   // map tiles a level in and out, marker pictures
    }
    this.warm(gl);
    if (more) this.idleWork();
  }
  warm(gl) {
    const sets = this.sets.filter((s) => s instanceof ChunkShapes && !(this.warmed || (this.warmed = new Set())).has(s) && s.blocks.size);
    if (!sets.length) return;
    const [pw, ph] = this.cache.size();
    const a = this.cache.slot(gl, 0, pw, ph), b = this.cache.slot(gl, 1, pw, ph);
    gl.bindFramebuffer(gl.FRAMEBUFFER, a.fb);
    gl.viewport(0, 0, 1, 1);   // one pixel, cleared after: only the programs' first run matters
    for (const set of sets) {
      const g = set.blocks.values().next().value, p = set.program(this);
      this.setView(p);
      set.uniforms(gl, p, this.view.zoom);
      gl.uniform1f(p.u.u_fade, 1);
      gl.bindVertexArray(g.vao);
      gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, 1);
      if (g.vaoBig) { gl.bindVertexArray(g.vaoBig); gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, 1); }
      this.warmed.add(set);
    }
    gl.bindVertexArray(null);
    drawPicture(gl, this, b.tex, this.view);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, this.canvas.width, this.canvas.height);
  }

  // the still map's trees, ruins and buildings, drawn into a picture the canvas's size only when
  // one of them changed (ChunkShapes.changed) or the view did (reset), shown pixel for pixel
  drawStill(gl, v, sets) {
    const W = this.canvas.width, H = this.canvas.height;
    const r = this.still || (this.still = { w: 0, h: 0 });
    if (r.w !== W || r.h !== H) { renderTarget(gl, r, W, H); this.staticDirty = true; }
    if (this.staticDirty === 'move' && this.shiftStill(gl, r, v, sets)) {
      this.staticDirty = false;
    } else if (this.staticDirty) {
      this.staticDirty = false;   // (before drawing: a layer still fading in asks again)
      r.view = v; r.min = this.min; r.dpr = this.dpr;
      gl.bindFramebuffer(gl.FRAMEBUFFER, r.fb);
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
      this.record = r.drawn = new Map(); r.partial = new Map();
      for (const s of sets) s.draw(gl, v, this);
      this.record = null;
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    }
    drawPicture(gl, this, this.still.tex, v);
    return true;
  }

  // A drag re-centred the canvas: copy the still picture over by the move (whole device pixels, so
  // an exact copy) into the other picture, then draw the shapes only in the strips it uncovered
  // (this.cull), plus, over the copied part, the blocks the old picture didn't have (this.skip: a
  // layer drawn only around the screen, the trees zoomed out, reaches new blocks as it moves).
  // A re-centre redrew the whole canvas, ~6 screens of trees and ruins in one frame: the catch a
  // drag had every half screen or so.
  shiftStill(gl, r, v, sets) {
    const d = this.dpr, W = r.w, H = r.h;
    if (!r.view || d % 1 || !r.min) return false;   // (a fractional screen scale can't copy exactly: draw it all)
    // where the old picture lands, device pixels (y down), from where the two views are in the
    // world: a glide (smoothzoom.js glideTo, a follow) moves the map without moving the map pane,
    // so the canvas's layer position (min) stays put while the world under it doesn't
    const fx = (r.view.x - v.x) * v.ppm * d, fy = (v.z - r.view.z) * v.ppm * d;
    const sx = Math.round(fx), sy = Math.round(fy);
    if (Math.abs(fx - sx) > 0.01 || Math.abs(fy - sy) > 0.01) return false;   // not whole pixels: draw it all
    if (Math.abs(sx) >= W * 0.6 || Math.abs(sy) >= H * 0.6) return false;
    const n = r.other || (r.other = { w: 0, h: 0 });
    if (n.w !== W || n.h !== H) renderTarget(gl, n, W, H);
    gl.bindFramebuffer(gl.FRAMEBUFFER, n.fb);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    const gy = -sy;   // framebuffers count rows from the bottom
    const x0 = Math.max(0, -sx), x1 = Math.min(W, W - sx), y0 = Math.max(0, -gy), y1 = Math.min(H, H - gy);
    gl.bindFramebuffer(gl.READ_FRAMEBUFFER, r.fb);
    gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, n.fb);
    gl.blitFramebuffer(x0, y0, x1, y1, x0 + sx, y0 + gy, x1 + sx, y1 + gy, gl.COLOR_BUFFER_BIT, gl.NEAREST);
    gl.bindFramebuffer(gl.READ_FRAMEBUFFER, null);
    gl.bindFramebuffer(gl.FRAMEBUFFER, n.fb);
    // uncovered: a column on one side (full height) and a row on the top or bottom (the rest of the width)
    const strips = [];
    if (sx > 0) strips.push([0, 0, sx, H]); else if (sx < 0) strips.push([W + sx, 0, -sx, H]);
    const cx0 = sx > 0 ? sx : 0, cw = W - Math.abs(sx);
    if (sy > 0) strips.push([cx0, 0, cw, sy]); else if (sy < 0) strips.push([cx0, H + sy, cw, -sy]);
    gl.enable(gl.SCISSOR_TEST);
    this.partial = r.partial || new Map(); this.shiftFailed = false;
    for (const [px, py, pw, ph] of strips) {   // (px, py: top-left, device pixels, y down)
      gl.scissor(px, H - py - ph, pw, ph);
      this.cull = { x: v.x + px / d / v.ppm, x1: v.x + (px + pw) / d / v.ppm, z: v.z - py / d / v.ppm, z0: v.z - (py + ph) / d / v.ppm };
      for (const s of sets) s.draw(gl, v, this);
    }
    this.cull = null;
    gl.scissor(x0 + sx, y0 + gy, x1 - x0, y1 - y0);   // the copied part
    this.skip = r.drawn || new Map();
    this.record = n.drawn = new Map();
    for (const s of sets) s.draw(gl, v, this);
    this.skip = this.record = this.partial = null;
    gl.disable(gl.SCISSOR_TEST);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    if (this.shiftFailed) return false;   // (drawStill draws it all)
    // blocks the old picture had that are out of the area now stay in the picture, cut off at the
    // strips: if one comes back, shifting can't finish it (shiftFailed), so it's drawn all again
    n.partial = new Map();
    for (const s of sets) {
      const now = n.drawn.get(s) || new Set(), was = new Set([...((r.drawn && r.drawn.get(s)) || []), ...((r.partial && r.partial.get(s)) || [])]);
      n.partial.set(s, new Set([...was].filter((g) => !now.has(g))));
    }
    // the new picture is now the still one
    n.other = r; r.other = null;
    n.view = v; n.min = this.min; n.dpr = d;
    this.still = n;
    return true;
  }

  // instanced attributes: [name, size, type, normalized] laid out in this order in one buffer
  // draw `count` sprites from `data` (8 floats each: rect, uv) with the atlas texture `tex`
  drawSprites(tex, data, count) {
    if (!count) return;
    const gl = this.gl, p = this.sprite;
    if (!this.spriteBuf) {
      this.spriteBuf = gl.createBuffer();
      this.spriteVao = this.makeVao(p, this.spriteBuf, [['a_rect', 4, gl.FLOAT, false], ['a_uv', 4, gl.FLOAT, false]], 32);
    }
    this.setView(p);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.spriteBuf);
    gl.bufferData(gl.ARRAY_BUFFER, data.subarray(0, count * 8), gl.DYNAMIC_DRAW);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.uniform1i(p.u.u_tex, 0);
    gl.bindVertexArray(this.spriteVao);
    gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, count);
    gl.bindVertexArray(null);
  }

  makeVao(prog, buffer, layout, stride, base = 0) {   // base: where in the buffer the first instance starts (bytes)
    const gl = this.gl;
    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.quad);
    const lc = gl.getAttribLocation(prog.p, 'a_corner');
    gl.enableVertexAttribArray(lc);
    gl.vertexAttribPointer(lc, 2, gl.FLOAT, false, 0, 0);
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    let off = base;
    for (const [name, size, type, norm] of layout) {
      const loc = gl.getAttribLocation(prog.p, name);
      const bytes = type === gl.FLOAT ? 4 : 1;
      if (loc >= 0) {
        gl.enableVertexAttribArray(loc);
        gl.vertexAttribPointer(loc, size, type, norm, stride, off);
        gl.vertexAttribDivisor(loc, 1);
      }
      off += size * bytes;
    }
    gl.bindVertexArray(null);
    return vao;
  }

  // the clouds past the world's edge, made once (see CLOUD_BAKE_FS); null if this browser can't
  // render into the texture (WorldEdgeGL then works them out per pixel, as before)
  cloudTexture() {
    if (this.clouds !== undefined) return this.clouds;
    if (!this.cloudBake) return (this.clouds = null);
    const gl = this.gl, t = gl.createTexture();
    for (let u = 0; u < 4; u++) { gl.activeTexture(gl.TEXTURE0 + u); gl.bindTexture(gl.TEXTURE_2D, null); }
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.texStorage2D(gl.TEXTURE_2D, 1, gl.RGBA8, CLOUD_RES, CLOUD_RES);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    const fb = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
    gl.bindTexture(gl.TEXTURE_2D, null);   // not bound while drawn into
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, t, 0);
    if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) {
      gl.bindFramebuffer(gl.FRAMEBUFFER, null); gl.deleteFramebuffer(fb); gl.deleteTexture(t);
      return (this.clouds = null);
    }
    gl.viewport(0, 0, CLOUD_RES, CLOUD_RES);
    gl.disable(gl.BLEND);
    gl.useProgram(this.cloudBake.p);
    gl.uniform1f(this.cloudBake.u.u_extent, CLOUD_EXTENT);
    gl.bindVertexArray(this.quadVao);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    gl.bindVertexArray(null);
    gl.enable(gl.BLEND);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.deleteFramebuffer(fb);
    gl.viewport(0, 0, this.canvas.width, this.canvas.height);
    if (gl.getError() !== gl.NO_ERROR) { gl.deleteTexture(t); return (this.clouds = null); }
    return (this.clouds = t);
  }

  setView(prog) {
    const gl = this.gl, v = this.view;
    gl.useProgram(prog.p);
    gl.uniform4f(prog.u.u_view, v.x, v.z, v.ppm, 0);
    gl.uniform2f(prog.u.u_css, this.cssW, this.cssH);
  }
}

// ---------------------------------------------------------------- a set of per-chunk shapes

// Mid-zoom below CACHE_BELOW the screen holds a hundred thousand trees, ruin pieces and
// buildings or more, and drawing every one of them every frame was what made the zoom catch. So
// they are drawn into a picture of the screen (and some around it when zooming out) and each frame
// shows that picture scaled to the zoom: one rectangle. The next picture is drawn a share a frame
// (CACHE_FRAMES) and swapped in when done, so no frame pays for every shape at once. When the
// zoom stops, the shapes are drawn sharp again.
class ShapeCache {
  constructor(sc) { this.sc = sc; this.slots = []; this.front = this.back = null; }
  reset() { this.front = this.back = null; }

  // draw the sets (ChunkShapes, next to each other in the draw order) from the picture; false
  // when no picture covers the screen this frame (the caller draws them as usual)
  draw(gl, v, sets) {
    for (const s of sets) { s.fadeNow = s.layerFade(v); if (s.dirty.size) s.rebuild(gl, 2); }
    const s = screenArea(v);
    this.build(gl, s, v.zoom, sets);
    const c = this.front;
    if (!c || c.x > s.x || c.x1 < s.x1 || c.z < s.z || c.z0 > s.z0 || Math.abs(c.zoom - v.zoom) > 0.8) return false;
    drawPicture(gl, this.sc, c.slot.tex, c);
    return true;
  }

  // draw this frame's share of the next picture
  build(gl, s, zoom, sets) {
    const sc = this.sc;
    let b = this.back;
    if (!b) {
      // Zooming in, the screen only shrinks inside the picture: it needs no margin. Zooming out
      // it grows, so a margin; the first picture of a zoom is drawn in one go (this frame shows
      // it instead of the shapes) with less of one. The size in pixels stays the same whatever
      // the area (a new size reallocates the texture): a smaller area is just sharper.
      const sm = sc.map._smooth, zoomingIn = sm && sm.running && sm.goal > zoom;
      const first = !this.front, k = zoomingIn ? 1.05 : first ? 1.2 : CACHE_SIZE;
      const size = sc.map.getSize(), cssW = size.x * k, cssH = size.y * k;
      const ppm = 1 / metersPerPixel(zoom), w = cssW / ppm, h = cssH / ppm, mx = (s.x + s.x1) / 2, mz = (s.z + s.z0) / 2;
      const [pw, ph] = this.size();
      const area = { x: mx - w / 2, x1: mx + w / 2, z: mz + h / 2, z0: mz - h / 2 };
      const list = [];   // [set, block], in draw order
      let total = 0;
      for (const set of sets) {
        if (!(set.fadeNow > 0)) continue;
        for (const g of set.blocksIn(area, set.cacheMargin())) { list.push([set, g]); total += g.count; }
      }
      const slot = this.slot(gl, this.front && this.front.slot === this.slots[0] ? 1 : 0, pw, ph);
      b = this.back = Object.assign(area, { slot, pw, ph, cssW, cssH, ppm, zoom, list, i: 0,
        per: first ? Infinity : total / CACHE_FRAMES });   // per: shapes a frame
    }
    gl.bindFramebuffer(gl.FRAMEBUFFER, b.slot.fb);
    gl.viewport(0, 0, b.pw, b.ph);
    const scissor = sc.onScreen;   // ShapesCanvas.draw clips to the screen mid-zoom
    if (scissor) gl.disable(gl.SCISSOR_TEST);
    if (b.i === 0) { gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT); }
    let cur = null, p = null;
    for (let n = 0; b.i < b.list.length && n < b.per; b.i++) {
      const [set, g] = b.list[b.i];
      if (!g.count) continue;   // emptied since (ChunkShapes.rebuild)
      if (set !== cur) {
        cur = set; p = set.program(sc);
        gl.useProgram(p.p);
        gl.uniform4f(p.u.u_view, b.x, b.z, b.ppm, 0);
        gl.uniform2f(p.u.u_css, b.cssW, b.cssH);
        set.uniforms(gl, p, b.zoom);
      }
      set.drawBlock(gl, g, p, set.fadeNow, set.smallFade(b.zoom));
      n += g.count;
    }
    gl.bindVertexArray(null);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, sc.canvas.width, sc.canvas.height);
    if (scissor) gl.enable(gl.SCISSOR_TEST);
    if (b.i >= b.list.length) { this.front = b; this.back = null; }   // done: show it, and start the next
  }

  // the pictures' size in pixels: the screen x CACHE_SIZE at its resolution (at most ~8 million)
  size() {
    const sc = this.sc, size = sc.map.getSize();
    const scale = Math.min(sc.dpr, Math.sqrt(8e6 / (size.x * size.y * CACHE_SIZE * CACHE_SIZE)));
    return [Math.max(1, Math.round(size.x * CACHE_SIZE * scale)), Math.max(1, Math.round(size.y * CACHE_SIZE * scale))];
  }

  // the texture and framebuffer for picture i (0 or 1), sized pw x ph
  slot(gl, i, pw, ph) {
    const c = this.slots[i] || (this.slots[i] = { w: 0, h: 0 });
    if (c.w !== pw || c.h !== ph) renderTarget(gl, c, pw, ph);
    return c;
  }
}

// (re)make t.tex, a w x h texture, and t.fb, a framebuffer drawing into it
function renderTarget(gl, t, w, h) {
  if (!t.tex) {
    t.tex = gl.createTexture(); t.fb = gl.createFramebuffer();
    gl.bindTexture(gl.TEXTURE_2D, t.tex);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  }
  gl.bindTexture(gl.TEXTURE_2D, t.tex);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
  gl.bindFramebuffer(gl.FRAMEBUFFER, t.fb);
  gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, t.tex, 0);
  gl.bindFramebuffer(gl.FRAMEBUFFER, null);   // (an RGBA8 texture always makes a complete framebuffer: not checked, checking waits for the GPU)
  t.w = w; t.h = h;
}

// a picture drawn into a render target, shown over world area a ({x, x1, z, z0})
function drawPicture(gl, sc, tex, a) {
  const p = sc.tex;
  sc.setView(p);
  gl.activeTexture(gl.TEXTURE0);
  gl.bindTexture(gl.TEXTURE_2D, tex);
  gl.uniform1i(p.u.u_tex, 0);
  gl.uniform1f(p.u.u_alpha, 1);
  gl.uniform4f(p.u.u_rect, a.x, a.z, a.x1 - a.x, a.z - a.z0);
  gl.uniform4f(p.u.u_uv, 0, 1, 1, 0);   // a framebuffer's first row is the picture's south edge
  gl.bindVertexArray(sc.quadVao);
  gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
  gl.bindVertexArray(null);
}

// Shapes per 256 m chunk, drawn in blocks of BLOCK x BLOCK chunks (1 km): one GPU buffer and one
// draw call per block. A draw call per chunk was hundreds of them a layer around zoom 4-5, where the
// screen covers the most chunks with trees showing, and Firefox pays a lot per call: the zoom caught
// there. A chunk's packed data is kept here; a block is rebuilt (its chunks copied end to end) when
// one of them changes, a few blocks a frame, so a screenful arriving at once doesn't stall a frame.
// Subclasses say which chunks they need, how to fetch one, and how to pack it.
const BLOCK = 4;
const NEED_MS = 150;
class ChunkShapes extends L.Layer {
  constructor() {
    super();
    this.gpu = new Map();       // "cx_cz" -> {rev, count, bytes} or {pending: true}
    this.blocks = new Map();    // "bx_bz" -> {buf, vao, count, born}
    this.dirty = new Set();     // blocks to rebuild
    this.loading = 0;
  }
  onAdd(map) { this.sc = ShapesCanvas.for(map); this.sc.add(this); }
  onRemove() { if (this.sc) this.sc.remove(this); }

  needArea(v) { return v; }

  // chunks overlapping the canvas, with a margin for shapes that reach over a chunk edge
  chunksIn(v, margin) {
    const out = [];
    const x0 = Math.max(0, chunkOf(v.x - margin)), x1 = Math.min(CHUNKS - 1, chunkOf(v.x1 + margin));
    const z0 = Math.max(0, chunkOf(v.z0 - margin)), z1 = Math.min(CHUNKS - 1, chunkOf(v.z + margin));
    for (let cz = z1; cz >= z0; cz--) for (let cx = x0; cx <= x1; cx++) out.push([cx, cz]);   // north first
    return out;
  }

  // fetch what the canvas covers (screen + padding: the next zoom-out is already loaded)
  // Called every frame of a zoom. Zoomed out, the canvas covers the whole world (~7000 chunks) and
  // walking them all each frame cost 10-20 ms a layer, a stall on every zoom-out frame. So: only
  // when the chunk range changed or the data did, and mid-glide at most every NEED_MS.
  need(v) {
    if (!this.fetchesAt(v.zoom)) return;
    const a = this.needArea(v), m = 16;
    const key = `${chunkOf(a.x - m)},${chunkOf(a.x1 + m)},${chunkOf(a.z0 - m)},${chunkOf(a.z + m)},${this.dataRev || 0}`;
    const now = performance.now(), map = this._map;
    if (key === this.needKey) { if (this.loading === 0) this.fire('load'); return; }
    if (map && map._gliding && now - (this.needAt || 0) < NEED_MS) return;
    this.needKey = key; this.needAt = now;
    // only the chunks that have anything (a few hundred), not every square of the range
    const x0 = chunkOf(a.x - m), x1 = chunkOf(a.x1 + m), z0 = chunkOf(a.z0 - m), z1 = chunkOf(a.z + m);
    // a zoom-out can bring hundreds of new chunks into range at once: mid-glide start only a
    // batch a frame (nearest the screen's middle first) and come back for the rest
    const cxm = chunkOf((a.x + a.x1) / 2), czm = chunkOf((a.z + a.z0) / 2);
    let todo = [];
    for (const [cx, cz] of this.listAll()) {
      if (cx < x0 || cx > x1 || cz < z0 || cz > z1) continue;
      const g = this.gpu.get(`${cx}_${cz}`);
      if (g && (g.pending || g.rev === this.rev(cx, cz))) continue;
      todo.push([cx, cz]);
    }
    const cap = map && map._gliding ? 48 : 400;
    if (todo.length > cap) {
      todo.sort((p, q) => Math.hypot(p[0] - cxm, p[1] - czm) - Math.hypot(q[0] - cxm, q[1] - czm));
      todo = todo.slice(0, cap);
      this.needKey = null;   // more to start: the next frame looks again
    }
    for (const [cx, cz] of todo) this.startFetch(cx, cz);
    if (this.loading === 0) this.fire('load');   // nothing to wait for: tell the tree hand-off (app.js)
  }

  startFetch(cx, cz) {
    const k = `${cx}_${cz}`, g = this.gpu.get(k), rev = this.rev(cx, cz);
    this.gpu.set(k, Object.assign(g || {}, { pending: true }));
    this.loading++;
    Promise.resolve(this.fetch(cx, cz)).then((data) => this.upload(k, data, rev)).catch(() => { this.gpu.delete(k); this.needKey = null; })
      .finally(() => {
        this.loading--;
        // drawn now: redraw; fetched ahead (prefetch, off screen or a layer not shown at this zoom):
        // only get it ready. A redraw for each prefetched chunk redrew the whole map hundreds of times.
        if (this.showing() && this.inView(cx, cx, cz, cz)) this.sc.dataChanged(); else if (this.sc) this.sc.idleWork();
        if (this.loading === 0) this.fire('load');
      });
  }

  // chunks cx0..cx1 x cz0..cz1 overlap the canvas
  inView(cx0, cx1, cz0, cz1) {
    const v = this.sc && this.sc.view;
    return !!v && cx1 >= chunkOf(v.x) && cx0 <= chunkOf(v.x1) && cz1 >= chunkOf(v.z0) && cz0 <= chunkOf(v.z);
  }
  blockInView(bk) { const [bx, bz] = bk.split('_').map(Number); return this.inView(bx * BLOCK, bx * BLOCK + BLOCK - 1, bz * BLOCK, bz * BLOCK + BLOCK - 1); }

  // drawn at this zoom (or still fading out)
  showing() { return (this.shown || 0) > 0 || !!(this.sc && this.sc.view && this.visibleAt(this.sc.view.zoom) > 0); }

  // The map is still: fetch chunks whatever the zoom, nearest (mx, mz) first, at most max more a
  // call, until every chunk with data is here (trees from a zoom-out too, so a zoom-in or a long
  // drag later finds them ready). true while some remain.
  prefetch(mx, mz, max) {
    const rev = this.dataRev || 0;
    if (this.prefetched === rev) return false;
    const cxm = chunkOf(mx), czm = chunkOf(mz), key = `${rev},${cxm},${czm}`;
    if (this.prefetchKey !== key) {   // the view moved (or the data changed): nearest first from here
      this.prefetchList = this.listAll().slice().sort((p, q) => ((p[0] - cxm) ** 2 + (p[1] - czm) ** 2) - ((q[0] - cxm) ** 2 + (q[1] - czm) ** 2));
      this.prefetchKey = key; this.prefetchAt = 0;
    }
    const list = this.prefetchList;
    for (let n = 0; this.prefetchAt < list.length; this.prefetchAt++) {
      const [cx, cz] = list[this.prefetchAt], g = this.gpu.get(`${cx}_${cz}`);
      if (g && (g.pending || g.rev === this.rev(cx, cz))) continue;
      if (n++ >= max) return true;
      this.startFetch(cx, cz);
    }
    this.prefetched = rev;   // all started
    return false;
  }

  // every chunk with data, as [cx, cz], made again when the data's index changes (refresh)
  listAll() {
    if (!this.all || this.allRev !== (this.dataRev || 0)) { this.all = this.allChunks(); this.allRev = this.dataRev || 0; }
    return this.all;
  }
  static keysToChunks(keys) { const out = []; for (const k of keys) { const i = k.indexOf('_'); out.push([+k.slice(0, i), +k.slice(i + 1)]); } return out; }

  blockOf(k) { const [cx, cz] = k.split('_').map(Number); return `${Math.floor(cx / BLOCK)}_${Math.floor(cz / BLOCK)}`; }

  // a chunk's data arrived: keep it packed, its block gets rebuilt when drawn
  // a chunk's data arrived: kept as it came, packed when its block is rebuilt (a few blocks a
  // frame) rather than here, where a region's worth of chunks arrives in one go
  upload(k, data, rev) {
    if (!this.sc) { this.gpu.delete(k); return; }
    this.gpu.set(k, { rev, count: -1, data });
    this.dirty.add(this.blockOf(k));
  }

  packed(g) {
    if (g.count !== -1) return g;
    const packed = this.pack(g.data);
    g.data = null;
    if (!packed || packed.count === 0) { g.count = 0; return g; }
    const b = packed.bytes;
    g.bytes = b instanceof ArrayBuffer ? new Uint8Array(b) : new Uint8Array(b.buffer, b.byteOffset, b.byteLength);
    g.count = packed.count;
    g.small = packed.small || 0;
    return g;
  }

  // rebuild up to n changed blocks: their chunks' data end to end in the block's one buffer. With
  // chunks in two parts (trees: the small ones first, see vegpack.js) the block is too: every
  // chunk's first part, then every chunk's second, and vaoBig starts at the second.
  rebuild(gl, n, quiet = false) {   // quiet: the layer isn't drawn now (built ahead): no redraw
    for (const bk of this.dirty) {
      if (n-- <= 0) {   // more next time: soon if any of it is on screen, else while idle
        if (!quiet && [...this.dirty].some((k) => this.blockInView(k))) this.sc.dataChanged(); else this.sc.idleWork();
        return;
      }
      this.dirty.delete(bk);
      if (!quiet && this.blockInView(bk)) this.sc.dataChanged();   // (one off screen changes nothing drawn)
      const [bx, bz] = bk.split('_').map(Number);
      let total = 0, count = 0, small = 0;
      const parts = [], bigs = [];
      for (let cz = bz * BLOCK; cz < bz * BLOCK + BLOCK; cz++)
        for (let cx = bx * BLOCK; cx < bx * BLOCK + BLOCK; cx++) {
          const g = this.gpu.get(`${cx}_${cz}`);
          if (g && !g.pending && g.count) this.packed(g);
          if (g && g.count > 0) {
            const cut = (g.small || 0) * this.stride;
            parts.push(cut ? g.bytes.subarray(0, cut) : g.bytes);
            if (cut) bigs.push(g.bytes.subarray(cut));
            total += g.bytes.byteLength; count += g.count; small += g.small || 0;
          }
        }
      let blk = this.blocks.get(bk);
      if (blk && blk.vaoBig) { gl.deleteVertexArray(blk.vaoBig); blk.vaoBig = null; }
      // the still picture has this block as it was: a drag mustn't shift it on (shiftStill), the
      // next draw draws it all (an off-screen block changing doesn't redraw at once, as before)
      const st = this.sc.still, had = blk && st && st.drawn && st.drawn.get(this);
      if (had && had.has(blk)) this.sc.staticDirty = true;
      if (!count) {
        if (blk) { this.dropDots(gl, blk); gl.deleteBuffer(blk.buf); gl.deleteVertexArray(blk.vao); blk.count = 0; this.blocks.delete(bk); }   // (count 0: a tree picture being drawn skips it)
        continue;
      }
      const all = new Uint8Array(total);
      let o = 0;
      for (const part of parts.concat(bigs)) { all.set(part, o); o += part.byteLength; }
      if (!blk) {
        const buf = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, buf);
        gl.bufferData(gl.ARRAY_BUFFER, all, gl.STATIC_DRAW);
        // (no fade-in for a new block: fading one in redrew the whole map every frame while data loaded)
        blk = { buf, count, born: 0, vao: this.sc.makeVao(this.program(this.sc), buf, this.layout(gl), this.stride) };
        this.blocks.set(bk, blk);
      } else {
        gl.bindBuffer(gl.ARRAY_BUFFER, blk.buf);
        gl.bufferData(gl.ARRAY_BUFFER, all, gl.STATIC_DRAW);
        blk.count = count;
      }
      blk.small = small;
      if (small) blk.vaoBig = this.sc.makeVao(this.program(this.sc), blk.buf, this.layout(gl), this.stride, small * this.stride);
      if (this.dotCell) this.buildDots(gl, blk, all, count);
    }
  }

  // Zoomed out, ruins and buildings are all the same small dot whatever their size, and the
  // pieces of one ruin or house pile hundreds of dots on the same few pixels. So each block also
  // keeps one piece per dotCell metres (per kind of place, so the Layers filter still works), and
  // drawBlock draws those while this.dots is on: the same picture from a fraction of the shapes.
  // The biggest piece of each square stands for it, and pieces under a metre across (torches,
  // candles, small decoration) make no dot at all: a dot is the same size whatever it stands for,
  // and a lone mist torch came out as a bright orange spot as big as a wall.
  buildDots(gl, blk, all, count) {
    const f = new Float32Array(all.buffer, all.byteOffset, all.byteLength >> 2), st = this.stride, cell = this.dotCell;
    const best = new Map();   // square -> [offset, size]
    let tiny = 0;
    for (let i = 0; i < count; i++) {
      const o = i * st, q = o >> 2, size = Math.max(f[q + 2], f[q + 3]);
      if (size < 1) { tiny++; continue; }
      const k = ((Math.floor(f[q] / cell) + 8192) * 16384 + Math.floor(f[q + 1] / cell) + 8192) * 256 + all[o + st - 1];
      const b = best.get(k);
      if (!b || size > b[1]) best.set(k, [o, size]);
    }
    const keep = [...best.values()].map((b) => b[0]).sort((a, b) => a - b);
    this.dropDots(gl, blk);
    if (!tiny && keep.length > count * 0.8) return;   // hardly any fewer: draw them all
    const out = new Uint8Array(keep.length * st);
    keep.forEach((o, j) => out.set(all.subarray(o, o + st), j * st));
    blk.dotBuf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, blk.dotBuf);
    gl.bufferData(gl.ARRAY_BUFFER, out, gl.STATIC_DRAW);
    blk.dotVao = this.sc.makeVao(this.program(this.sc), blk.dotBuf, this.layout(gl), st);
    blk.dotCount = keep.length;
  }
  dropDots(gl, blk) {
    if (!blk.dotBuf) return;
    gl.deleteBuffer(blk.dotBuf); gl.deleteVertexArray(blk.dotVao);
    blk.dotBuf = blk.dotVao = null; blk.dotCount = 0;
  }

  // Chunk data changed on the server: the next reset fetches what changed, and the old shapes stay
  // drawn until the new ones arrive (no blink). Only chunks gone from the index are dropped.
  refresh() {
    for (const [k, g] of this.gpu) {
      const [cx, cz] = k.split('_').map(Number);
      if (!g.pending && !this.has(cx, cz)) { this.gpu.delete(k); this.dirty.add(this.blockOf(k)); }
    }
    this.dataRev = (this.dataRev || 0) + 1;   // the next need() looks at every chunk again
    if (this.sc) this.sc.reset();
  }

  // How visible the whole layer is: eases toward 1 when it should show at this zoom and toward 0
  // when not, over FADE_MS, so crossing a layer's zoom limit fades instead of switching.
  layerFade(v) {
    const now = performance.now(), target = this.visibleAt(v.zoom);
    const dt = this.fadeAt ? Math.min(now - this.fadeAt, 50) : FADE_MS;   // after a quiet spell, start the fade now rather than jump
    this.fadeAt = now;
    const cur = this.shown === undefined ? target : this.shown;
    const step = dt / FADE_MS;
    this.shown = cur < target ? Math.min(target, cur + step) : Math.max(target, cur - step);
    if (this.shown !== target) this.changed();
    return this.shown;
  }

  // something this layer shows changed: draw it again (not just reuse the still picture, ShapesCanvas.draw)
  changed() { if (this.sc) { this.sc.staticDirty = true; this.sc.redraw(); } }

  // for the mid-zoom picture (ShapeCache): how much of the small shapes to draw, the margin
  // for blocksIn, and the layer's own uniforms (after the program and view are set)
  smallFade() { return 1; }
  cacheMargin() { return 16; }
  uniforms() {}

  // the blocks with anything in them over area v (+ margin metres), north first
  blocksIn(v, margin) {
    const out = [];
    const bx0 = Math.floor(Math.max(0, chunkOf(v.x - margin)) / BLOCK), bx1 = Math.floor(Math.min(CHUNKS - 1, chunkOf(v.x1 + margin)) / BLOCK);
    const bz0 = Math.floor(Math.max(0, chunkOf(v.z0 - margin)) / BLOCK), bz1 = Math.floor(Math.min(CHUNKS - 1, chunkOf(v.z + margin)) / BLOCK);
    for (let bz = bz1; bz >= bz0; bz--)
      for (let bx = bx0; bx <= bx1; bx++) {
        const g = this.blocks.get(`${bx}_${bz}`);
        if (g && g.count) out.push(g);
      }
    return out;
  }

  // smallFade: how much of the block's first part (its small shapes) to draw, 0 to 1
  drawBlock(gl, g, prog, fade, smallFade) {
    if (this.dots && g.dotVao) {
      gl.uniform1f(prog.u.u_fade, fade);
      gl.bindVertexArray(g.dotVao);
      gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, g.dotCount);
      return;
    }
    if (!g.small || smallFade >= 1) {
      gl.uniform1f(prog.u.u_fade, fade);
      gl.bindVertexArray(g.vao);
      gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, g.count);
      return;
    }
    if (smallFade > 0) {
      gl.uniform1f(prog.u.u_fade, fade * smallFade);
      gl.bindVertexArray(g.vao);
      gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, g.small);
    }
    gl.uniform1f(prog.u.u_fade, fade);
    gl.bindVertexArray(g.vaoBig);
    gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, g.count - g.small);
  }

  drawChunks(gl, v, margin, prog, fade = 1, smallFade = 1) {
    if (this.dirty.size) {
      const m = this._map, moving = m && (m._gliding || m._animatingZoom);
      this.rebuild(gl, moving ? 2 : 16);   // (buffers and vertex arrays only: the layer's program stays in use)
    }
    if (this.sc && this.sc.onScreen && v === this.sc.view) v = screenArea(v);   // mid-zoom: only what is on screen
    // the still picture: which blocks it has (record), and a drag's re-centre (shiftStill) drawing
    // only an uncovered strip (cull) or only the blocks the picture lacks (skip)
    const sc = this.sc, c = sc && sc.cull, skip = sc && sc.skip && (sc.skip.get(this) || new Set());
    let list = this.blocksIn(v, margin);
    if (sc && sc.record) sc.record.set(this, new Set(list));
    const part = sc && sc.partial && sc.partial.get(this);
    if (part && part.size && list.some((g) => part.has(g))) sc.shiftFailed = true;
    if (c) {   // (whole blocks are drawn, past the area's edge: so the blocks that reach the strip)
      const inStrip = new Set(this.blocksIn(c, 0));
      list = list.filter((g) => inStrip.has(g));
    }
    if (skip) list = list.filter((g) => !skip.has(g));
    const now = performance.now();
    let fading = false;
    for (const g of list) {
      const f = Math.min(1, (now - g.born) / FADE_MS);
      if (f < 1) fading = true;
      this.drawBlock(gl, g, prog, f * fade, smallFade);
    }
    gl.bindVertexArray(null);
    if (fading) this.changed();
  }
}


// Regions of building or ruin pieces fetched and packed in a worker (rectworker.js), so a big
// region never parses on the page mid-zoom. chunk() resolves to the chunk packed for the GPU
// ({bytes, count}), or null when it can't (no regions listed, no workers): the caller then takes
// the plain way.
class PackedRegions {
  constructor(loader, colors, fallback) {
    this.loader = loader; this.colors = colors; this.fallback = fallback;
    this.loads = new Map(); this.calls = new Map(); this.nextId = 1;
    try {
      this.worker = new Worker(new URL('../rectworker.js', import.meta.url));
      this.worker.onmessage = (e) => { const c = this.calls.get(e.data.id); if (c) { this.calls.delete(e.data.id); c(e.data); } };
    } catch (e) { this.worker = null; }
  }
  chunk(cx, cz, rev) {
    const L_ = this.loader, rk = `${Math.floor(cx / L_.size)}_${Math.floor(cz / L_.size)}`, rrev = L_.revs.get(rk);
    if (!this.worker || !rrev) return null;
    const key = `${rk}:${rrev}`;
    if (!this.loads.has(key)) {
      const url = new URL(`data/${L_.kind}/r/${rk}.json?h=${rrev}`, location.href).href;
      const p = new Promise((resolve, reject) => {
        const id = this.nextId++;
        this.calls.set(id, (d) => (d.error ? reject(new Error(d.error)) : resolve(new Map(d.chunks.map((c) => [`${c.cx}_${c.cz}`, c])))));
        this.worker.postMessage({ id, url, colors: this.colors, fallback: this.fallback });
      });
      p.catch(() => this.loads.delete(key));
      if (this.loads.size > 64) this.loads.delete(this.loads.keys().next().value);   // packed bytes are kept on the GPU side; this is only for chunks still to come
      this.loads.set(key, p);
    }
    return this.loads.get(key).then((m) => { const c = m.get(`${cx}_${cz}`); return c && c.rev === rev ? c : null; }, () => null);
  }
}

// ---------------------------------------------------------------- the three layers

const RECT_LAYOUT = (gl) => [['a_center', 2, gl.FLOAT, false], ['a_size', 2, gl.FLOAT, false], ['a_yaw', 1, gl.FLOAT, false], ['a_h', 1, gl.FLOAT, false], ['a_color', 4, gl.UNSIGNED_BYTE, true]];
const RECT_STRIDE = 28;

function packRects(pieces, colorOf) {
  const n = pieces.length;
  const bytes = new ArrayBuffer(n * RECT_STRIDE), f = new Float32Array(bytes), u = new Uint8Array(bytes);
  for (let i = 0; i < n; i++) {
    const [x, z, , yaw, sx, sz, h, mat, , site] = pieces[i];
    const o = i * 7;
    f[o] = x; f[o + 1] = z; f[o + 2] = sx; f[o + 3] = sz; f[o + 4] = yaw; f[o + 5] = h;
    const [r, g, b] = colorOf(mat);
    u[o * 4 + 24] = r; u[o * 4 + 25] = g; u[o * 4 + 26] = b; u[o * 4 + 27] = site ?? 255;   // alpha: a world structure's kind of place (RUIN_SITES), 255 none
  }
  return { bytes, count: n };
}

const matRgb = new Map();
const materialRgb = (mat) => {
  if (!matRgb.has(mat)) matRgb.set(mat, hexRgb(materialColors[mat] || '#a07446'));
  return matRgb.get(mat);
};

export class BuildingsGL extends ChunkShapes {
  constructor() {
    super();
    this.order = 3; this.opacity = 0.95; this.stride = RECT_STRIDE; this.dotCell = DOT_CELL;
    chunks.onChange(() => this.refresh());
  }
  fetchesAt(zoom) { return zoom >= BUILDINGS_MIN_ZOOM - 1; }   // a step early, so zooming in finds it ready
  has(cx, cz) { return chunks.has(cx, cz); }
  allChunks() { return ChunkShapes.keysToChunks(chunks.index.keys()); }
  rev(cx, cz) { const e = chunks.index.get(`${cx}_${cz}`); return e ? e.rev : 0; }
  fetch(cx, cz) {
    if (!this.packedRegions) this.packedRegions = new PackedRegions(chunks.regions, materialColors.map(hexRgb), hexRgb('#a07446'));
    const p = this.packedRegions.chunk(cx, cz, this.rev(cx, cz));
    return p ? p.then((c) => c || chunks.get(cx, cz)) : chunks.get(cx, cz);
  }
  pack(data) { return data.bytes ? data : packRects(data.pieces, materialRgb); }
  program(sc) { return sc.rect; }
  layout(gl) { return RECT_LAYOUT(gl); }
  visibleAt(zoom) { return zoom >= BUILDINGS_MIN_ZOOM - 0.5 ? 1 : 0; }
  draw(gl, v, sc) {
    const fade = this.layerFade(v);
    if (fade <= 0) return;
    sc.setView(sc.rect);
    this.uniforms(gl, sc.rect, v.zoom);
    this.drawChunks(gl, v, 16, sc.rect, fade);
  }
  uniforms(gl, p, zoom) {
    const tz = Math.round(zoom);   // switch looks at the same zooms the tiles did
    this.dots = tz < DETAIL_ZOOM;
    gl.uniform1f(p.u.u_dotPx, tz >= DETAIL_ZOOM ? 0 : tz >= 4 ? 1.2 : 0.9);
    gl.uniform1f(p.u.u_minPx, 1.2);
    gl.uniform1ui(p.u.u_sites, 0xffffffff);
    gl.uniform1f(p.u.u_alpha, this.opacity);
    gl.uniform1i(p.u.u_edge, 1);
    gl.uniform1f(p.u.u_edgeK, Math.min(1, Math.max(0, (zoom - 6.25) / 0.75)));   // outlines and roofs ease in from 6.25 to 7
  }
  setOpacity(o) { this.opacity = o; this.changed(); }

  // Pieces near a world position (for hover), nearest first.
  async pick(x, z, radius) {
    const cx = chunkOf(x), cz = chunkOf(z);
    const out = [];
    for (let dz = -1; dz <= 1; dz++)
      for (let dx = -1; dx <= 1; dx++) {
        const ax = cx + dx, az = cz + dz;
        if (ax < 0 || az < 0 || ax >= 80 || az >= 80 || !chunks.has(ax, az)) continue;
        const data = await chunks.get(ax, az);
        for (const p of data.pieces) {
          const d = Math.hypot(p[0] - x, p[1] - z);
          const reach = Math.max(p[4], p[5]) / 2 + radius;
          if (d <= reach) out.push({ d, prefab: data.prefabs[p[8]], material: materialNames[p[7]] || 'Misc', y: p[2], h: p[6] });
        }
      }
    out.sort((a, b) => a.d - b.d);
    return out;
  }
}

// the kinds of place a world structure piece stands in (World/Ruins.cs Sites, same order), for the
// Layers panel's switches per kind
export const RUIN_SITES = ['Other', 'Houses', 'Stone ruins', 'Fuling villages', 'Dvergr sites', 'Ashlands ruins', 'Shipwrecks', 'Camps', 'Boss altars', 'Deep North', 'Dungeons'];

const RUIN_WOOD = hexRgb('#4a3f33'), RUIN_STONE = hexRgb('#3d4654');
const STONE_MATS = new Set([3, 4, 10]);   // Stone, Black marble, Grausten (icons.js materialNames)

export class RuinsGL extends ChunkShapes {
  constructor() {
    super();
    this.order = 2; this.stride = RECT_STRIDE; this.dotCell = DOT_CELL;
    ruins.onChange(() => this.refresh());
    if (ruins.indexRev < 0) ruins.refreshIndex();
    layerState.onChange((k) => { if (k === 'ruinHidden') this.changed(); });
  }
  fetchesAt(zoom) { return zoom >= BUILDINGS_MIN_ZOOM - 1; }
  has(cx, cz) { return ruins.has(cx, cz); }
  allChunks() { return ChunkShapes.keysToChunks(ruins.index.keys()); }
  rev(cx, cz) { const e = ruins.index.get(`${cx}_${cz}`); return e ? e.rev : 0; }
  fetch(cx, cz) {
    if (!this.packedRegions) this.packedRegions = new PackedRegions(ruins.regions, Array.from({ length: 32 }, (_, m) => (STONE_MATS.has(m) ? RUIN_STONE : RUIN_WOOD)), RUIN_WOOD);
    const p = this.packedRegions.chunk(cx, cz, this.rev(cx, cz));
    return p ? p.then((c) => c || ruins.get(cx, cz)) : ruins.get(cx, cz);
  }
  pack(d) { return d.bytes ? d : packRects(d, (mat) => (STONE_MATS.has(mat) ? RUIN_STONE : RUIN_WOOD)); }
  program(sc) { return sc.rect; }
  layout(gl) { return RECT_LAYOUT(gl); }
  visibleAt(zoom) { return zoom >= BUILDINGS_MIN_ZOOM - 0.5 ? 1 : 0; }
  draw(gl, v, sc) {
    const fade = this.layerFade(v);
    if (fade <= 0) return;
    sc.setView(sc.rect);
    this.uniforms(gl, sc.rect, v.zoom);
    this.drawChunks(gl, v, 16, sc.rect, fade);
  }
  uniforms(gl, p, zoom) {
    const tz = Math.round(zoom);
    this.dots = tz < DETAIL_ZOOM;
    gl.uniform1f(p.u.u_dotPx, tz >= DETAIL_ZOOM ? 0 : tz >= 4 ? 0.9 : 0.7);
    gl.uniform1f(p.u.u_minPx, 1.2);
    let mask = 0;
    for (let i = 0; i < RUIN_SITES.length; i++) if (!layerState.ruinHidden.has(i)) mask |= 1 << i;
    gl.uniform1ui(p.u.u_sites, mask >>> 0);
    gl.uniform1f(p.u.u_alpha, 0.9);
    gl.uniform1i(p.u.u_edge, 2);
    gl.uniform1f(p.u.u_edgeK, tz >= DETAIL_ZOOM ? 1 : 0);
  }
}

// Every tree and rock from zoom 5 up (the same range the baked tree tiles covered), so there is no
// hand-off between two kinds of trees and no tree tiles to download: the whole explored world's
// vegetation is ~2 MB, fetched by region and kept for good.
export class TreesGL extends ChunkShapes {
  constructor() {
    super(); this.order = 1; this.stride = TREE_STRIDE;
    vegetation.onChange(() => this.refresh());
    layerState.onChange((k) => { if (/^veg[A-Z]/.test(k)) this.changed(); });   // a group or kind switched on or off
  }
  // from 4 only what is on screen (a zoom-in will show it), padding too from TREES_MIN
  fetchesAt(zoom) { return zoom >= TREES_MIN - 0.5; }
  needArea(v) { return v.zoom >= TREES_MIN ? v : screenArea(v); }
  has(cx, cz) { return vegetation.has(cx, cz); }
  allChunks() {   // vegetation comes by region (size x size chunks)
    const out = [], n = vegetation.size;
    for (const [rx, rz] of ChunkShapes.keysToChunks(vegetation.revs.keys()))
      for (let j = 0; j < n; j++) for (let i = 0; i < n; i++) out.push([rx * n + i, rz * n + j]);
    return out;
  }
  rev(cx, cz) { return vegetation.rev(cx, cz); }
  fetch(cx, cz) { return vegetation.get(cx, cz); }
  pack(packed) { return packed; }   // done by the worker (vegpack.js)
  program(sc) { return sc.tree; }
  layout(gl) { return [['a_center', 2, gl.FLOAT, false], ['a_r', 1, gl.FLOAT, false], ['a_seed', 1, gl.FLOAT, false], ['a_color', 4, gl.UNSIGNED_BYTE, true]]; }
  // eases in between 4.5 and 5.25 (and over FADE_MS when a zoom crosses that)
  visibleAt(zoom) { return Math.min(1, Math.max(0, (zoom - TREES_MIN) / 0.75)); }
  uniforms(gl, p) { const [lo, hi] = vegShowMask(layerState); gl.uniform2ui(p.u.u_show, lo, hi); }
  // bushes, plants and small stones (over half the shapes) only from SMALL_FROM, where they
  // start to be more than a speck: below it they cost the GPU the most for next to nothing
  smallFade(zoom) { return Math.min(1, Math.max(0, (zoom - SMALL_FROM) / 0.5)); }
  cacheMargin() { return 12; }
  draw(gl, v, sc) {
    const fade = this.layerFade(v);
    if (fade <= 0) return;
    sc.setView(sc.tree);
    this.uniforms(gl, sc.tree, v.zoom);
    // Every tree and rock at every zoom (drawing only some when zoomed out made them pop in as
    // you zoomed). Below 6.5 only around the screen, though: at those zooms the whole explored
    // world is in the padding, and a zoom-out from there fades the trees anyway.
    let area = v;
    if (sc.onScreen) area = screenArea(v);   // mid-zoom: the screen only (the scissor clips the rest anyway)
    else if (v.zoom < 6.5) {
      const s = screenArea(v), mx = (s.x1 - s.x) * 0.3, mz = (s.z - s.z0) * 0.3;
      area = { x: s.x - mx, x1: s.x1 + mx, z: s.z + mz, z0: s.z0 - mz };
    }
    this.drawChunks(gl, area, 12, sc.tree, fade, this.smallFade(v.zoom));
  }
}
