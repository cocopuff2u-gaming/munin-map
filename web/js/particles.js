// The effects the game plays on what a player carries (a fire staff's flames, Storm Fang's lightning, mist),
// run from the game's own particle settings (the server reads them: Models/ParticleExport.cs). It follows
// the game's particle system module by module: the shape they're born in, start values and their curves,
// colour and size over life, orbit, pull, noise and forces, flipbook pictures, stretched sparks, trails
// behind each particle and ribbons joining them, each drawn with its material's blending.
//
// Mostly the server sends the game's own particles: it runs each system in the engine and records every
// particle 30 times a second, and this plays that back, so the motion is the game's. The settings are still
// used to draw them (pictures, blending, stretch, flipbooks, trails and ribbons), and to run a system here
// when there is no recording. That is simulated in the portrait's frame; a system's local values (its
// shape, local velocities, orbit axes) are turned into it with the system's axes ("basis").

const MAX = 400;          // particles per system
const TRAIL_POINTS = 24;  // points kept behind each particle

// ---- values that vary: numbers, [min, max], curves sampled at 9 points, gradients sampled at 9 points
const sample = (k, t) => {
  const x = Math.min(1, Math.max(0, t)) * (k.length - 1), i = Math.floor(x), f = x - i;
  return k[i] + ((k[Math.min(i + 1, k.length - 1)] ?? k[i]) - k[i]) * f;
};
export function curve(c, t, r) {
  if (c == null) return 0;
  if (typeof c === 'number') return c;
  if (Array.isArray(c)) return c[0] + (c[1] - c[0]) * r;
  const a = sample(c.k, t);
  return c.k2 ? a + (sample(c.k2, t) - a) * r : a;
}
const lerp4 = (a, b, f, o) => { for (let i = 0; i < 4; i++) o[i] = a[i] + (b[i] - a[i]) * f; return o; };
const stops = (g, t, o) => {
  const x = Math.min(1, Math.max(0, t)) * (g.length - 1), i = Math.floor(x);
  return lerp4(g[i], g[Math.min(i + 1, g.length - 1)], x - i, o);
};
export function colour(c, t, r, o = [1, 1, 1, 1]) {
  if (c == null) { o[0] = o[1] = o[2] = o[3] = 1; return o; }
  if (Array.isArray(c)) { for (let i = 0; i < 4; i++) o[i] = c[i]; return o; }
  if (c.a) return lerp4(c.a, c.b, r, o);
  if (c.rnd) return stops(c.g, r, o);
  if (c.g2) { const a = stops(c.g, t, [0, 0, 0, 0]), b = stops(c.g2, t, [0, 0, 0, 0]); return lerp4(a, b, r, o); }
  return stops(c.g, t, o);
}

// ---- a smooth noise field (three sums of sines with this system's own phases): -1..1 on each axis
function noiseField() {
  const ph = Array.from({ length: 9 }, () => Math.random() * 6.283);
  return (x, y, z, s, out) => {
    out[0] = (Math.sin(y * 1.31 + ph[0] + s) * Math.cos(z * 1.73 + ph[1]) + Math.sin(x * 2.17 + z * 0.9 + ph[2] - s * 0.7) * 0.5) / 1.5;
    out[1] = (Math.sin(z * 1.27 + ph[3] - s) * Math.cos(x * 1.61 + ph[4]) + Math.sin(y * 2.31 + x * 0.8 + ph[5] + s * 0.6) * 0.5) / 1.5;
    out[2] = (Math.sin(x * 1.37 + ph[6] + s * 0.8) * Math.cos(y * 1.53 + ph[7]) + Math.sin(z * 2.11 + y * 0.7 + ph[8] - s) * 0.5) / 1.5;
    return out;
  };
}

// ---- the shape: a birth position and direction in the system's own frame (Unity's shapes, +Z forward)
function unit(o) { const z = Math.random() * 2 - 1, a = Math.random() * 6.283, s = Math.sqrt(1 - z * z); o[0] = s * Math.cos(a); o[1] = s * Math.sin(a); o[2] = z; return o; }
function eulerZXY(r) {   // Unity's order: Z, then X, then Y (degrees)
  const [x, y, z] = r.map((d) => d * Math.PI / 180), cx = Math.cos(x), sx = Math.sin(x), cy = Math.cos(y), sy = Math.sin(y), cz = Math.cos(z), sz = Math.sin(z);
  return [cy * cz + sy * sx * sz, -cy * sz + sy * sx * cz, sy * cx, cx * sz, cx * cz, -sx, -sy * cz + cy * sx * sz, sy * sz + cy * sx * cz, cy * cx];
}
function born(sh, pos, dir) {
  const radius = sh.radius || 0, thick = sh.thick ?? 1, arc = (sh.arc ?? 360) * Math.PI / 180;
  const rr = () => radius * Math.sqrt(1 - thick + thick * Math.random());
  const t = sh.type || 'Sphere';
  if (t.startsWith('Cone')) {
    const a = Math.random() * arc, r = rr(), tilt = (sh.angle || 0) * Math.PI / 180 * (radius > 0 ? r / radius : Math.sqrt(Math.random()));
    pos[0] = Math.cos(a) * r; pos[1] = Math.sin(a) * r; pos[2] = 0;
    dir[0] = Math.cos(a) * Math.sin(tilt); dir[1] = Math.sin(a) * Math.sin(tilt); dir[2] = Math.cos(tilt);
    if (t.startsWith('ConeVolume')) { const l = (sh.length || 0) * Math.random(); for (let i = 0; i < 3; i++) pos[i] += dir[i] * l; }
  } else if (t.startsWith('Sphere') || t.startsWith('Hemisphere')) {
    unit(dir); if (t.startsWith('Hemisphere')) dir[2] = Math.abs(dir[2]);
    const r = t.endsWith('Shell') ? radius : rr(); for (let i = 0; i < 3; i++) pos[i] = dir[i] * r;
  } else if (t.startsWith('Box') || t === 'Rectangle') {
    const b = sh.box || [1, 1, 1];
    pos[0] = (Math.random() - 0.5) * b[0]; pos[1] = (Math.random() - 0.5) * b[1]; pos[2] = t === 'Rectangle' ? 0 : (Math.random() - 0.5) * b[2];
    dir[0] = 0; dir[1] = 0; dir[2] = 1;
  } else if (t === 'Circle' || t === 'Donut') {
    const a = Math.random() * arc, r = rr();
    dir[0] = Math.cos(a); dir[1] = Math.sin(a); dir[2] = 0; pos[0] = dir[0] * r; pos[1] = dir[1] * r; pos[2] = 0;
    if (t === 'Donut' && sh.donut) { const d = unit([0, 0, 0]); for (let i = 0; i < 3; i++) pos[i] += d[i] * sh.donut * Math.random(); }
  } else if (t.includes('Edge')) {
    pos[0] = (Math.random() * 2 - 1) * radius; pos[1] = 0; pos[2] = 0; dir[0] = 0; dir[1] = 1; dir[2] = 0;
  } else if (sh.points && sh.points.length) {
    const q = sh.points[Math.floor(Math.random() * sh.points.length)];
    pos[0] = q[0]; pos[1] = q[1]; pos[2] = q[2]; dir[0] = q[3]; dir[1] = q[4]; dir[2] = q[5];
    return;   // (mesh points come with the shape's own offset and turn)
  } else { unit(dir); for (let i = 0; i < 3; i++) pos[i] = dir[i] * rr(); }
  if (sh.randomDir) { const u = unit([0, 0, 0]); for (let i = 0; i < 3; i++) dir[i] += (u[i] - dir[i]) * sh.randomDir; }
  if (sh.spherize) { const l = Math.hypot(...pos) || 1; for (let i = 0; i < 3; i++) dir[i] += (pos[i] / l - dir[i]) * sh.spherize; }
  const dl = Math.hypot(...dir) || 1; for (let i = 0; i < 3; i++) dir[i] /= dl;
  // the shape's own scale (not a box's: that is its size), turn and offset
  if (!(t.startsWith('Box') || t === 'Rectangle') && sh.box) for (let i = 0; i < 3; i++) pos[i] *= sh.box[i] / (sh.box[0] || 1) || 1;
  if (sh.rotation && (sh.rotation[0] || sh.rotation[1] || sh.rotation[2])) {
    const m = eulerZXY(sh.rotation), p = [...pos], d = [...dir];
    for (let i = 0; i < 3; i++) { pos[i] = m[i * 3] * p[0] + m[i * 3 + 1] * p[1] + m[i * 3 + 2] * p[2]; dir[i] = m[i * 3] * d[0] + m[i * 3 + 1] * d[1] + m[i * 3 + 2] * d[2]; }
  }
  if (sh.offset) for (let i = 0; i < 3; i++) pos[i] += sh.offset[i];
}

// ---- drawing: camera-facing quads (turned, stretched, a flipbook frame) and strips for trails and ribbons
const BLENDS = (THREE, blend) => {
  switch (blend) {
    // light-adding: adds colour only, never makes the see-through background solid (its dark would show as black squares)
    case 'add': return { blending: THREE.CustomBlending, blendEquation: THREE.AddEquation, blendSrc: THREE.SrcAlphaFactor, blendDst: THREE.OneFactor, blendSrcAlpha: THREE.ZeroFactor, blendDstAlpha: THREE.OneFactor };
    case 'premul': return { blending: THREE.CustomBlending, blendEquation: THREE.AddEquation, blendSrc: THREE.OneFactor, blendDst: THREE.OneMinusSrcAlphaFactor };
    case 'mul': return { blending: THREE.MultiplyBlending, premultipliedAlpha: true };
    default: return { blending: THREE.NormalBlending };
  }
};
// (the game draws in linear light, as these do; the result is turned to screen colours at the end, like the rest)
const FRAG = `uniform sampler2D map; uniform float hasMap; uniform float cover; uniform float ramped; uniform float gradChannel; uniform float gradAlpha;
  varying vec4 vC; varying vec2 vUv; varying vec4 vHi; varying vec4 vLo;
  void main() {
    vec4 t = hasMap > 0.5 ? texture2D(map, vUv) : vec4(1.0, 1.0, 1.0, smoothstep(0.5, 0.0, length(vUv - 0.5)));
    float a = t.a; if (cover > 0.5 && a > 0.99) a = smoothstep(0.03, 0.35, max(t.r, max(t.g, t.b)));   // (a covering picture with no see-through: its black is the background)
    vec3 rgb = t.rgb;
    if (ramped > 0.5) {   // the game's Custom/Gradient Mapped Particle: the picture's light picks the colour along the ramp
      float g = gradChannel < 0.5 ? t.r : gradChannel < 1.5 ? t.g : gradChannel < 2.5 ? t.b : t.a;
      vec4 c = mix(vLo, vHi, g); rgb = c.rgb; a = (gradAlpha > 0.5 ? g : t.a) * c.a;
    }
    gl_FragColor = vec4(rgb * vC.rgb, a * vC.a); if (gl_FragColor.a < 0.004) discard;
    #include <tonemapping_fragment>
    #include <colorspace_fragment>
  }`;
async function material(THREE, m, ramped, kind) {
  let map = null;
  if (m?.tex) { try { map = await new THREE.TextureLoader().loadAsync(`models/${m.tex}`); map.colorSpace = THREE.SRGBColorSpace; map.wrapS = map.wrapT = THREE.RepeatWrapping; } catch { map = null; } }
  const vert = kind === 'strip'
    ? `attribute vec4 color; attribute vec4 hi; attribute vec4 lo; varying vec4 vC; varying vec2 vUv; varying vec4 vHi; varying vec4 vLo;
       void main() { vC = color; vUv = uv; vHi = hi; vLo = lo; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`
    : `attribute vec4 color; attribute vec2 corner; attribute vec4 quad; attribute vec3 vel; attribute vec4 hi; attribute vec4 lo; attribute vec4 cell;
       uniform float stretch; uniform float lengthScale; uniform float velScale; uniform float lie;
       varying vec4 vC; varying vec2 vUv; varying vec4 vHi; varying vec4 vLo;
       void main() {
         vC = color; vHi = hi; vLo = lo;
         float size = quad.x, rot = quad.y;
         vec2 c = corner; vUv = cell.xy + (c + 0.5) * cell.zw;
         if (lie > 0.5) {   // lying flat, facing up (a horizontal billboard)
           float s = sin(rot), k = cos(rot); vec3 p = position + vec3(c.x * k - c.y * s, 0.0, c.x * s + c.y * k) * size;
           gl_Position = projectionMatrix * modelViewMatrix * vec4(p, 1.0); return;
         }
         vec4 mv = modelViewMatrix * vec4(position, 1.0);
         if (stretch > 0.5) {   // a spark drawn long along the way it goes (as Unity: the picture's width along it, centred)
           vec3 v = (modelViewMatrix * vec4(vel, 0.0)).xyz; float l = length(v.xy);
           vec2 d = l > 1e-5 ? v.xy / l : vec2(1.0, 0.0), n = vec2(-d.y, d.x);
           float len = size * lengthScale + length(vel) * velScale;
           mv.xy += d * c.x * len + n * c.y * size;
         } else { float s = sin(rot), k = cos(rot); mv.xy += vec2(c.x * k - c.y * s, c.x * s + c.y * k) * size; }
         gl_Position = projectionMatrix * mv;
       }`;
  return new THREE.ShaderMaterial({
    uniforms: { map: { value: map }, hasMap: { value: map ? 1 : 0 }, cover: { value: m?.blend === 'alpha' ? 1 : 0 }, ramped: { value: ramped ? 1 : 0 },
      gradChannel: { value: m?.gradChannel ?? 0 }, gradAlpha: { value: m?.gradAlpha === false ? 0 : 1 },
      stretch: { value: 0 }, lengthScale: { value: 1 }, velScale: { value: 0 }, lie: { value: 0 } },
    vertexShader: vert, fragmentShader: FRAG, transparent: true, depthWrite: false, side: THREE.DoubleSide, ...BLENDS(THREE, m?.blend || 'add'),
  });
}

// ---- the game's own particles, recorded on the server 30 times a second (Models/ParticleExport.cs Bake):
// 24 bytes each: seed, x y z (mm), size (0.1 mm), turn (mrad), r g b a, velocity (cm/s), age, own (its own seed's
// low byte, the same each time round a seamless loop: its ribbon and its random looks)
function unbake(b) {
  const bin = atob(b.data), u8 = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) u8[i] = bin.charCodeAt(i);
  const dv = new DataView(u8.buffer), frames = [];
  let o = 0;
  for (const count of b.counts) {
    const list = [];
    for (let i = 0; i < count; i++, o += 24) {
      list.push({ seed: dv.getUint16(o, true), p: [dv.getInt16(o + 2, true) / 1000, dv.getInt16(o + 4, true) / 1000, dv.getInt16(o + 6, true) / 1000],
        size: dv.getUint16(o + 8, true) / 10000, rot: dv.getInt16(o + 10, true) / 1000, col: [u8[o + 12] / 255, u8[o + 13] / 255, u8[o + 14] / 255, u8[o + 15] / 255],
        v: [dv.getInt16(o + 16, true) / 100, dv.getInt16(o + 18, true) / 100, dv.getInt16(o + 20, true) / 100], age: u8[o + 22] / 255, own: b.seamless ? u8[o + 23] : dv.getUint16(o, true) });
    }
    list.bySeed = new Map(list.map((x) => [x.seed, x]));
    frames.push(list);
  }
  return frames;
}
const seeded = (seed, k) => { const x = Math.sin(seed * 12.9898 + k * 78.233) * 43758.5453; return x - Math.floor(x); };

// ---- one system
async function system(THREE, e) {
  const frames = e.bake ? unbake(e.bake) : null, baked = !!frames;
  const n = Math.max(4, Math.min(MAX, baked ? Math.max(...frames.map((f) => f.length)) : (e.max || MAX)));
  const B = e.basis || [[1, 0, 0], [0, 1, 0], [0, 0, 1]], O = e.pos, S = e.scale || 1;
  const toFrame = (l, out, isPoint) => { for (let i = 0; i < 3; i++) out[i] = (isPoint ? O[i] : 0) + B[0][i] * l[0] + B[1][i] * l[1] + B[2][i] * l[2]; return out; };
  const render = e.render || { mode: 'Billboard' };
  // (mesh particles, the blood splats on blood-lightning weapons, are drawn as their mesh when the server sent it, else left out)
  const drawParticles = render.mode !== 'None' && render.mode !== 'Mesh';
  const group = new THREE.Group();
  const tint = e.mat?.tint || [1, 1, 1, 1], ttint = e.trail?.tmat?.tint || [1, 1, 1, 1];
  const ramped = !!e.ramp;

  // quads for the particles
  let qgeo = null, qmat = null;
  const quadPos = new Float32Array(n * 4 * 3), quadCol = new Float32Array(n * 4 * 4), quadQ = new Float32Array(n * 4 * 4), quadVel = new Float32Array(n * 4 * 3);
  const quadHi = new Float32Array(n * 4 * 4), quadLo = new Float32Array(n * 4 * 4), quadCell = new Float32Array(n * 4 * 4);
  if (drawParticles) {
    qgeo = new THREE.BufferGeometry();
    const corner = new Float32Array(n * 4 * 2), idx = new Uint32Array(n * 6);
    for (let i = 0; i < n; i++) {
      corner.set([-0.5, -0.5, 0.5, -0.5, 0.5, 0.5, -0.5, 0.5], i * 8);
      idx.set([i * 4, i * 4 + 1, i * 4 + 2, i * 4, i * 4 + 2, i * 4 + 3], i * 6);
    }
    qgeo.setAttribute('position', new THREE.BufferAttribute(quadPos, 3)); qgeo.setAttribute('corner', new THREE.BufferAttribute(corner, 2));
    qgeo.setAttribute('color', new THREE.BufferAttribute(quadCol, 4)); qgeo.setAttribute('quad', new THREE.BufferAttribute(quadQ, 4));
    qgeo.setAttribute('vel', new THREE.BufferAttribute(quadVel, 3)); qgeo.setAttribute('hi', new THREE.BufferAttribute(quadHi, 4));
    qgeo.setAttribute('lo', new THREE.BufferAttribute(quadLo, 4)); qgeo.setAttribute('cell', new THREE.BufferAttribute(quadCell, 4));
    qgeo.setIndex(new THREE.BufferAttribute(idx, 1));
    qmat = await material(THREE, e.mat, ramped, 'quad');
    qmat.uniforms.stretch.value = render.mode === 'Stretch' ? 1 : 0;
    qmat.uniforms.lengthScale.value = render.lengthScale ?? 1; qmat.uniforms.velScale.value = render.velScale ?? 0;
    qmat.uniforms.lie.value = render.mode === 'HorizontalBillboard' ? 1 : 0;
    const mesh = new THREE.Mesh(qgeo, qmat); mesh.frustumCulled = false; group.add(mesh);
  }

  // strips for trails and ribbons
  const T = e.trail;
  const segs = T ? (T.ribbon ? n : n * TRAIL_POINTS) : 0;
  let sgeo = null;
  const sPos = new Float32Array(segs * 6 * 3), sUv = new Float32Array(segs * 6 * 2), sCol = new Float32Array(segs * 6 * 4), sHi = new Float32Array(segs * 6 * 4), sLo = new Float32Array(segs * 6 * 4);
  if (T) {
    sgeo = new THREE.BufferGeometry();
    sgeo.setAttribute('position', new THREE.BufferAttribute(sPos, 3)); sgeo.setAttribute('uv', new THREE.BufferAttribute(sUv, 2));
    sgeo.setAttribute('color', new THREE.BufferAttribute(sCol, 4)); sgeo.setAttribute('hi', new THREE.BufferAttribute(sHi, 4)); sgeo.setAttribute('lo', new THREE.BufferAttribute(sLo, 4));
    const smat = await material(THREE, T.tmat || e.mat, ramped, 'strip');
    const mesh = new THREE.Mesh(sgeo, smat); mesh.frustumCulled = false; group.add(mesh);
  }

  // mesh particles: each the effect's own mesh, sized and turned, facing the viewer (Unity's View alignment)
  const PM = render.mode === 'Mesh' && e.pmesh?.p?.length && e.pmesh?.i?.length ? e.pmesh : null;
  const mv = PM ? PM.p.length / 3 : 0, mi = PM ? PM.i.length : 0;
  let mgeo = null;
  const mPos = new Float32Array(PM ? n * mv * 3 : 0), mCol = new Float32Array(PM ? n * mv * 4 : 0);
  if (PM) {
    mgeo = new THREE.BufferGeometry();
    const uv = new Float32Array(n * mv * 2), idx = new Uint32Array(n * mi);
    for (let k = 0; k < n; k++) {
      for (let v = 0; v < mv; v++) { uv[(k * mv + v) * 2] = PM.uv ? PM.uv[v * 2] : 0.5; uv[(k * mv + v) * 2 + 1] = PM.uv ? PM.uv[v * 2 + 1] : 0.5; }
      for (let t = 0; t < mi; t++) idx[k * mi + t] = k * mv + PM.i[t];
    }
    mgeo.setAttribute('position', new THREE.BufferAttribute(mPos, 3)); mgeo.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
    mgeo.setAttribute('color', new THREE.BufferAttribute(mCol, 4));
    mgeo.setAttribute('hi', new THREE.BufferAttribute(new Float32Array(n * mv * 4), 4)); mgeo.setAttribute('lo', new THREE.BufferAttribute(new Float32Array(n * mv * 4), 4));
    mgeo.setIndex(new THREE.BufferAttribute(idx, 1));
    const mesh = new THREE.Mesh(mgeo, await material(THREE, e.mat, false, 'strip')); mesh.frustumCulled = false; group.add(mesh);
  }

  const P = Array.from({ length: n }, () => ({ live: false, age: 0, life: 1, r: [0, 0, 0, 0, 0], p: [0, 0, 0], v: [0, 0, 0], size: 0, rot: 0, col: [1, 1, 1, 1], born: 0, h: [], trailed: true, frame0: 0, row: 0 }));
  const field = e.noise ? noiseField() : null;
  const centre = [0, 0, 0], oo = e.vel?.oOffset ? toFrame(e.vel.oOffset, [0, 0, 0], true) : O;
  const tmpL = [0, 0, 0], tmpD = [0, 0, 0], tmpF = [0, 0, 0], nz = [0, 0, 0], c4 = [0, 0, 0, 0], c5 = [0, 0, 0, 0];
  let time = 0, owed = 0, birth = 0, burstDone = (e.bursts || []).map(() => 0);
  const dur = Math.max(0.05, e.duration || 5);
  const spawn = () => {
    const q = P.find((x) => !x.live); if (!q) return;
    const tt = (time % dur) / dur;   // (start values that change over the emitter's run)
    for (let i = 0; i < 5; i++) q.r[i] = Math.random();
    q.live = true; q.age = 0; q.life = Math.max(0.02, curve(e.life, tt, q.r[0]));
    if (e.shape) born(e.shape, tmpL, tmpD); else { tmpL[0] = tmpL[1] = tmpL[2] = 0; tmpD[0] = 0; tmpD[1] = 0; tmpD[2] = 1; }
    toFrame(tmpL, q.p, true); toFrame(tmpD, tmpF, false);
    const sp = curve(e.speed, tt, q.r[1]);
    for (let i = 0; i < 3; i++) q.v[i] = tmpF[i] * sp;
    q.size = curve(e.size, tt, q.r[2]); q.rot = curve(e.rot, tt, q.r[3]); colour(e.color, tt, q.r[4], q.col);
    q.born = birth++; q.h.length = 0; q.trailed = !T || Math.random() <= (T.ratio ?? 1);
    if (e.sheet) { q.frame0 = curve(e.sheet.start, 0, Math.random()); q.row = e.sheet.row === -1 ? Math.floor(Math.random() * e.sheet.y) : Math.max(0, e.sheet.row); }
  };
  const turn = (v, axis, ang) => {   // turn v about a unit axis (Rodrigues)
    const c = Math.cos(ang), s = Math.sin(ang), d = axis[0] * v[0] + axis[1] * v[1] + axis[2] * v[2];
    const x = v[0] * c + (axis[1] * v[2] - axis[2] * v[1]) * s + axis[0] * d * (1 - c);
    const y = v[1] * c + (axis[2] * v[0] - axis[0] * v[2]) * s + axis[1] * d * (1 - c);
    const z = v[2] * c + (axis[0] * v[1] - axis[1] * v[0]) * s + axis[2] * d * (1 - c);
    v[0] = x; v[1] = y; v[2] = z;
  };

  const step = (dt) => {
    time += dt;
    const tt = (time % dur) / dur;
    if (e.loop !== false || time < dur) {
      owed += curve(e.rate, tt, Math.random()) * dt;
      while (owed >= 1) { owed -= 1; spawn(); }
      (e.bursts || []).forEach(([bt, count, cycles, every, prob], i) => {
        const due = bt + burstDone[i] * Math.max(0.01, every || dur);
        const local = e.loop !== false ? time % dur : time;
        if ((cycles === 0 || burstDone[i] < cycles) && local >= due && Math.random() <= (prob ?? 1)) { for (let k = 0; k < count; k++) spawn(); burstDone[i]++; }
        if (e.loop !== false && local < bt) burstDone[i] = 0;
      });
    }
    for (const q of P) {
      if (!q.live) continue;
      q.age += dt;
      if (q.age >= q.life) { q.live = false; continue; }
      const t = q.age / q.life;
      // forces change the speed it has; gravity pulls down the portrait
      q.v[1] -= 9.81 * curve(e.gravity, t, q.r[0]) * dt;
      if (e.force) {
        tmpL[0] = curve(e.force.x, t, q.r[1]); tmpL[1] = curve(e.force.y, t, q.r[2]); tmpL[2] = curve(e.force.z, t, q.r[3]);
        if (e.force.world) { tmpF[0] = tmpL[0]; tmpF[1] = tmpL[1]; tmpF[2] = -tmpL[2]; } else toFrame(tmpL, tmpF, false);
        for (let i = 0; i < 3; i++) q.v[i] += tmpF[i] * dt;
      }
      if (e.limit) {
        const lim = curve(e.limit.speed, t, q.r[1]), sp = Math.hypot(...q.v);
        if (sp > lim && sp > 0) { const k = 1 - (1 - lim / sp) * Math.min(1, (e.limit.dampen || 0) * dt * 30); for (let i = 0; i < 3; i++) q.v[i] *= k; }
        const drag = curve(e.limit.drag, t, q.r[2]); if (drag) for (let i = 0; i < 3; i++) q.v[i] *= Math.max(0, 1 - drag * dt);
      }
      // velocity over life adds to that, without changing it; orbit turns it about the centre, radial pushes out
      let mod = 1;
      tmpF[0] = tmpF[1] = tmpF[2] = 0;
      if (e.vel) {
        tmpL[0] = curve(e.vel.x, t, q.r[1]); tmpL[1] = curve(e.vel.y, t, q.r[2]); tmpL[2] = curve(e.vel.z, t, q.r[3]);
        if (e.vel.world) { tmpF[0] = tmpL[0]; tmpF[1] = tmpL[1]; tmpF[2] = -tmpL[2]; } else toFrame(tmpL, tmpF, false);
        mod = e.vel.speedMod != null ? curve(e.vel.speedMod, t, q.r[4]) : 1;
      }
      for (let i = 0; i < 3; i++) q.p[i] += (q.v[i] + tmpF[i]) * mod * dt;
      if (e.vel && (e.vel.ox != null || e.vel.radial != null)) {
        for (let i = 0; i < 3; i++) centre[i] = q.p[i] - oo[i];
        if (e.vel.ox != null) {
          const w = [curve(e.vel.ox, t, q.r[1]), curve(e.vel.oy, t, q.r[2]), curve(e.vel.oz, t, q.r[3])];
          for (let a = 0; a < 3; a++) if (w[a]) turn(centre, B[a], w[a] * dt * mod);
        }
        if (e.vel.radial != null) {
          const rl = Math.hypot(...centre), rv = curve(e.vel.radial, t, q.r[4]) * dt * mod;
          if (rl > 1e-5) { const k = Math.max(0, rl + rv) / rl; for (let i = 0; i < 3; i++) centre[i] *= k; }
        }
        for (let i = 0; i < 3; i++) q.p[i] = oo[i] + centre[i];
      }
      // noise: a wander through a field that drifts with time
      if (field) {
        const f = e.noise.freq || 1, str = curve(e.noise.strength, t, q.r[0]) * (e.noise.pos != null ? curve(e.noise.pos, t, 0.5) : 1);
        field(q.p[0] * f, q.p[1] * f, q.p[2] * f, time * curve(e.noise.scroll, t, 0.5) * 6.283, nz);
        for (let i = 0; i < 3; i++) q.p[i] += nz[i] * str * dt;
      }
      if (e.rotOL) q.rot += curve(e.rotOL, t, q.r[3]) * dt;
      // where it has been (for trails)
      if (T && !T.ribbon) {
        const last = q.h[0];
        if (!last || Math.hypot(q.p[0] - last[0], q.p[1] - last[1], q.p[2] - last[2]) >= (T.minDist || 0.01)) { q.h.unshift([q.p[0], q.p[1], q.p[2], time]); if (q.h.length > TRAIL_POINTS) q.h.pop(); }
        const keep = curve(T.life, t, q.r[2]) * (T.sizeLife ? 1 : q.life);
        while (q.h.length > 1 && time - q.h[q.h.length - 1][3] > keep) q.h.pop();
      }
    }
    draw();
  };

  // per-particle colour now (start x over life x material), and the gradient-map colours
  const look = (q, out) => {
    const t = q.age / q.life;
    for (let i = 0; i < 4; i++) out[i] = q.col[i] * tint[i];
    if (e.colOL && !baked) { colour(e.colOL, t, q.r[4], c4); for (let i = 0; i < 4; i++) out[i] *= c4[i]; }
    return out;
  };
  const camera = [0, 0, 10];
  const meshParts = () => {
    let drawn = 0;
    for (let k = 0; k < n; k++) {
      const q = P[k];
      if (!q.live || q.size <= 0) continue;
      // the viewer's frame at this particle: forward (away from the viewer), right and up; turned by its spin
      let fx = q.p[0] - camera[0], fy = q.p[1] - camera[1], fz = q.p[2] - camera[2];
      const fl = Math.hypot(fx, fy, fz) || 1; fx /= fl; fy /= fl; fz /= fl;
      let rx = fz, rz = -fx; const rl = Math.hypot(rx, rz) || 1; rx /= rl; rz /= rl;
      const ux = rz * fy, uy = rx * fz - rz * fx, uz = -rx * fy;   // (forward x right: up when looking level)
      const cs = Math.cos(q.rot), sn = Math.sin(q.rot), sz = q.size;
      look(q, c5);
      for (let v = 0; v < mv; v++) {
        const a = PM.p[v * 3], b = PM.p[v * 3 + 1], c = -PM.p[v * 3 + 2];
        const x = (a * cs - b * sn) * sz, y = (a * sn + b * cs) * sz, z = c * sz, o = (drawn * mv + v) * 3;
        mPos[o] = q.p[0] + rx * x + ux * y + fx * z; mPos[o + 1] = q.p[1] + uy * y + fy * z; mPos[o + 2] = q.p[2] + rz * x + uz * y + fz * z;
        mCol.set(c5, (drawn * mv + v) * 4);
      }
      drawn++;
    }
    mgeo.setDrawRange(0, drawn * mi);
    mgeo.attributes.position.needsUpdate = true; mgeo.attributes.color.needsUpdate = true;
  };
  const draw = () => {
    if (qgeo) {
      for (let i = 0; i < n; i++) {
        const q = P[i], t = q.live ? q.age / q.life : 0;
        const size = q.live ? Math.max(0, q.size * (e.sizeOL && !baked ? curve(e.sizeOL, t, q.r[2]) : 1)) : 0;
        look(q, c5);
        let cu = 0, cv = 0, cw = 1, ch = 1;
        if (e.sheet && q.live) {
          const tiles = e.sheet.x * e.sheet.y, perRow = e.sheet.row >= -1;
          const count = perRow ? e.sheet.x : tiles;
          let f = Math.floor((curve(e.sheet.frame, (t * (e.sheet.cycles || 1)) % 1, q.r[1]) * count + q.frame0)) % count;
          if (f < 0) f += count;
          const col = perRow ? f : f % e.sheet.x, row = perRow ? q.row : Math.floor(f / e.sheet.x);
          cw = 1 / e.sheet.x; ch = 1 / e.sheet.y; cu = col * cw; cv = 1 - (row + 1) * ch;   // (the sheet's first picture is top left)
        }
        let hi = null, lo = null;
        if (ramped) { hi = colour(e.ramp.hi, t, q.r[0], [0, 0, 0, 0]); lo = colour(e.ramp.lo, t, q.r[0], [0, 0, 0, 0]); }
        for (let k = 0; k < 4; k++) {
          const o = i * 4 + k;
          quadPos[o * 3] = q.p[0]; quadPos[o * 3 + 1] = q.p[1]; quadPos[o * 3 + 2] = q.p[2];
          quadVel[o * 3] = q.v[0]; quadVel[o * 3 + 1] = q.v[1]; quadVel[o * 3 + 2] = q.v[2];
          for (let c = 0; c < 4; c++) quadCol[o * 4 + c] = q.live ? c5[c] : 0;
          quadQ[o * 4] = size; quadQ[o * 4 + 1] = q.rot * (e.flip && q.r[3] < e.flip ? -1 : 1);
          quadCell[o * 4] = cu; quadCell[o * 4 + 1] = cv; quadCell[o * 4 + 2] = cw; quadCell[o * 4 + 3] = ch;
          if (hi) for (let c = 0; c < 4; c++) { quadHi[o * 4 + c] = hi[c]; quadLo[o * 4 + c] = lo[c]; }
        }
      }
      for (const a of ['position', 'color', 'quad', 'vel', 'cell', 'hi', 'lo']) qgeo.attributes[a].needsUpdate = true;
    }
    if (sgeo) strips();
    if (mgeo) meshParts();
  };

  // trails and ribbons: lines of points, drawn as strips facing the camera, the picture stretched along them
  let sN = 0;
  const seg = (a, b, wa, wb, ua, ub, ca, cb, ha, la, hb, lb) => {
    if (sN >= segs) return;
    const dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
    const vx = camera[0] - a[0], vy = camera[1] - a[1], vz = camera[2] - a[2];
    let sx = dy * vz - dz * vy, sy = dz * vx - dx * vz, sz = dx * vy - dy * vx; const sl = Math.hypot(sx, sy, sz) || 1; sx /= sl; sy /= sl; sz /= sl;
    const pts = [[a, -wa, ua, ca, ha, la], [a, wa, ua, ca, ha, la], [b, wb, ub, cb, hb, lb], [a, -wa, ua, ca, ha, la], [b, wb, ub, cb, hb, lb], [b, -wb, ub, cb, hb, lb]];
    for (let k = 0; k < 6; k++) {
      const [p, w, u, c, h, l] = pts[k], o = sN * 6 + k;
      sPos[o * 3] = p[0] + sx * w / 2; sPos[o * 3 + 1] = p[1] + sy * w / 2; sPos[o * 3 + 2] = p[2] + sz * w / 2;
      sUv[o * 2] = u; sUv[o * 2 + 1] = w < 0 ? 0 : 1;
      for (let i = 0; i < 4; i++) { sCol[o * 4 + i] = c[i]; if (h) { sHi[o * 4 + i] = h[i]; sLo[o * 4 + i] = l[i]; } }
    }
    sN++;
  };
  // a trail's colour: the particle's own (when it takes it) x along the trail x over the particle's life x its material
  const trailColour = (q, u) => {
    const c = [1, 1, 1, 1];
    if (T.inherit) { for (let i = 0; i < 4; i++) c[i] = q.col[i]; if (e.colOL && !baked) { colour(e.colOL, q.age / q.life, q.r[4], c4); for (let i = 0; i < 4; i++) c[i] *= c4[i]; } }
    colour(T.colOT, u, q.r[0], c4); for (let i = 0; i < 4; i++) c[i] *= c4[i] * ttint[i];
    if (T.colOL) { colour(T.colOL, q.age / q.life, q.r[1], c4); for (let i = 0; i < 4; i++) c[i] *= c4[i]; }
    return c;
  };
  const strips = () => {
    sN = 0;
    const width = (q, u) => curve(T.width, u, q.r[3]) * (T.sizeWidth ? q.size * (e.sizeOL && !baked ? curve(e.sizeOL, q.age / q.life, q.r[2]) : 1) : 1);
    const ramp = (q) => ramped ? [colour(e.ramp.hi, q.age / q.life, q.r[0], [0, 0, 0, 0]), colour(e.ramp.lo, q.age / q.life, q.r[0], [0, 0, 0, 0])] : [null, null];
    if (T.ribbon) {
      // the particles joined in the order they were born, into this many ribbons
      const live = P.filter((q) => q.live).sort((a, b) => a.born - b.born);
      for (let r = 0; r < (T.ribbons || 1); r++) {
        const line = live.filter((q) => (baked ? q.seed : q.born) % (T.ribbons || 1) === r);
        for (let i = 0; i + 1 < line.length; i++) {
          const a = line[i], b = line[i + 1], ua = i / Math.max(1, line.length - 1), ub = (i + 1) / Math.max(1, line.length - 1);
          const [ha, la] = ramp(a), [hb, lb] = ramp(b);
          seg(a.p, b.p, width(a, ua), width(b, ub), T.stretch ? ua : i, T.stretch ? ub : i + 1, trailColour(a, ua), trailColour(b, ub), ha, la, hb, lb);
        }
      }
    } else {
      for (const q of P) {
        if (!q.live || !q.trailed || q.h.length < 1) continue;
        const line = [q.p, ...q.h.slice(1)], m = line.length;
        const [h, l] = ramp(q);
        for (let i = 0; i + 1 < m; i++) {
          const ua = i / Math.max(1, m - 1), ub = (i + 1) / Math.max(1, m - 1);
          seg(line[i], line[i + 1], width(q, ua), width(q, ub), ua, ub, trailColour(q, ua), trailColour(q, ub), h, l, h, l);
        }
      }
    }
    sgeo.setDrawRange(0, sN * 6);
    for (const a of ['position', 'uv', 'color', 'hi', 'lo']) sgeo.attributes[a].needsUpdate = true;
  };

  // the recorded particles, frame by frame, round and round (a seamless loop: Bake); a trail is where its
  // particle was in the frames before, as long as the trail lasts (back past the loop's start, where each
  // particle's id is seedStep higher)
  const fps = baked ? (e.bake.fps || 30) : 30, L = baked ? frames.length : 0, stepId = baked ? (e.bake.seedStep || 0) : 0;
  const ip = P.map(() => [0, 0, 0]), ic = P.map(() => [1, 1, 1, 1]);
  const playback = (dt) => {
    time += dt;
    // between two recorded frames (30 a second), each particle part of the way from one to the next, so on a
    // faster screen it glides rather than hops (in the next frame, past the loop's start, its id is seedStep lower)
    const at = time * fps, fl = Math.floor(at), mix = at - fl;
    const f = ((fl % L) + L) % L, list = frames[f], next = frames[(f + 1) % L], wrap = f + 1 === L;
    const keep = T ? curve(T.life, 0.5, 0.5) * curve(e.life, 0.5, 0.5) : 0;
    for (let i = 0; i < n; i++) {
      const q = P[i], s = list[i];
      if (!s) { q.live = false; continue; }
      const b = next.bySeed.get(wrap ? (s.seed - stepId) & 0xFFFF : s.seed), k = b && b.age >= s.age ? mix : 0;
      const p = ip[i], c = ic[i];
      for (let a = 0; a < 3; a++) p[a] = k ? s.p[a] + (b.p[a] - s.p[a]) * k : s.p[a];
      for (let a = 0; a < 4; a++) c[a] = k ? s.col[a] + (b.col[a] - s.col[a]) * k : s.col[a];
      let turn = k ? b.rot - s.rot : 0;
      if (turn > Math.PI) turn -= 2 * Math.PI; else if (turn < -Math.PI) turn += 2 * Math.PI;
      q.live = true; q.life = 1; q.p = p; q.col = c; q.v = s.v; q.seed = s.own;
      q.age = k ? s.age + (b.age - s.age) * k : s.age; q.size = k ? s.size + (b.size - s.size) * k : s.size; q.rot = s.rot + turn * k; q.born = -q.age;
      for (let r = 0; r < 5; r++) q.r[r] = seeded(s.own, r);
      q.trailed = !T || q.r[0] <= (T.ratio ?? 1);
      if (e.sheet) { q.frame0 = curve(e.sheet.start, 0, q.r[1]); q.row = e.sheet.row === -1 ? Math.floor(q.r[2] * e.sheet.y) : Math.max(0, e.sheet.row); }
      if (T && !T.ribbon) {
        // its trail: where it is now, then where it was in the frames before (as long as the trail lasts, and
        // back past the loop's start, where its id is seedStep higher)
        q.h.length = 0; q.h.push([p[0], p[1], p[2], time]);
        for (let j = 0; j < TRAIL_POINTS && (j - mix) / fps <= keep; j++) {
          const back = f - j >= 0 ? frames[f - j] : frames[((f - j) % L + L) % L], id = f - j >= 0 ? s.seed : (s.seed + stepId * Math.ceil((j - f) / L)) & 0xFFFF;
          const h = j === 0 ? s : back.bySeed.get(id); if (!h) break;
          if (j > 0 || mix > 0.01) q.h.push([h.p[0], h.p[1], h.p[2], time - (j + mix) / fps]);
        }
      }
    }
    draw();
  };
  if (baked) return { object: group, anim: true, step: (dt, cam) => { if (cam) { camera[0] = cam.x; camera[1] = cam.y; camera[2] = cam.z; } playback(dt); } };

  // already going when first seen
  for (let i = 0; i < 60; i++) step(0.05);
  return { object: group, anim: true, step: (dt, cam) => { if (cam) { camera[0] = cam.x; camera[1] = cam.y; camera[2] = cam.z; } step(dt); } };
}

// The light some items give: lighting the model softly, and a glow where it is
const softDot = (() => { let t = null; return (THREE) => {
  if (t) return t;
  const c = document.createElement('canvas'); c.width = c.height = 64;
  const g = c.getContext('2d'), r = g.createRadialGradient(32, 32, 0, 32, 32, 32);
  r.addColorStop(0, 'rgba(255,255,255,1)'); r.addColorStop(0.4, 'rgba(255,255,255,0.5)'); r.addColorStop(1, 'rgba(255,255,255,0)');
  g.fillStyle = r; g.fillRect(0, 0, 64, 64);
  t = new THREE.CanvasTexture(c); return t;
}; })();
function light(THREE, e) {
  const c = new THREE.Color(e.color[0], e.color[1], e.color[2]);
  const g = new THREE.Group();
  // (softer than the game's: in the portrait's soft studio light, a staff's or the void knife's light at full
  // strength washed the whole outfit in its colour)
  const pl = new THREE.PointLight(c, Math.min(4, e.intensity) * 0.3, Math.max(0.5, Math.min(1.5, e.range)), 2);
  pl.position.set(...e.pos); g.add(pl);
  const sp = new THREE.Sprite(new THREE.SpriteMaterial({ map: softDot(THREE), color: c, blending: THREE.AdditiveBlending, depthWrite: false, transparent: true, opacity: 0.55 }));
  sp.position.set(...e.pos); sp.scale.setScalar(0.18); g.add(sp);
  const F = e.flicker;
  if (!F) return { object: g, step() {} };
  // the game's flicker (LightFlicker): its strength and where it sits wander together, on the same sums
  const base = pl.intensity, A = F.axes || [[0, 0, 0], [0, 0, 0], [0, 0, 0]], off = Math.random() * 10;
  let time = 0;
  return { object: g, anim: true, step(dt) {
    time += dt;
    const n = off + time * F.speed, s1 = Math.sin(n), s2 = Math.sin(n * 0.56436), c3 = Math.cos(n * 0.758348);
    const k = 1 + s1 * s2 * c3 * F.amount;
    pl.intensity = base * k; sp.material.opacity = 0.55 * Math.min(1.5, k);
    const o = [s1 * s2, s2 * Math.sin(n * 0.688742), c3 * Math.cos(n * 0.4563696)];
    for (let i = 0; i < 3; i++) { const v = e.pos[i] + A[0][i] * o[0] + A[1][i] * o[1] + A[2][i] * o[2]; pl.position.setComponent(i, v); sp.position.setComponent(i, v); }
  } };
}

export async function makeEffects(THREE, list) {
  const out = [];
  for (const e of list) {
    try {
      if (e.kind === 'light') out.push(light(THREE, e));
      else if (e.kind === 'ps') out.push(await system(THREE, e));
    } catch (err) { /* one effect that can't be played doesn't stop the rest */ }
  }
  return out;
}
