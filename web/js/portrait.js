// The 3D portrait on a player card: their character as the server made it (Models/Portraits), drawn
// with three.js into the card and turned with a drag. The server sends the model with the body's
// skin texture only; the game paints chest and leg armour onto the body, so that mix is done here on
// a canvas (skin tinted with their skin colour, then the chest and leg textures over it). Hair and
// beard take their hair colour. One renderer, moved to whichever card is open; three.js is loaded the
// first time a portrait is shown.

import { getJSON } from './net.js';

let three = null;     // { THREE, GLTFLoader, OrbitControls }, once loaded
let view = null;      // the one renderer, scene and camera
const models = new Map();   // portrait id -> Promise<THREE.Object3D> (made once)

async function lib() {
  if (!three) three = Promise.all([import('three'), import('three/addons/loaders/GLTFLoader.js'), import('three/addons/controls/OrbitControls.js')])
    .then(([THREE, g, o]) => ({ THREE, GLTFLoader: g.GLTFLoader, OrbitControls: o.OrbitControls }));
  return three;
}

// data/portrait.json?id=: the details once the server has made it (it starts making it when asked)
async function info(id) {
  for (let i = 0; i < 30; i++) {
    const d = await getJSON(`data/portrait.json?id=${encodeURIComponent(id)}`);
    if (!d.pending) return d;
    await new Promise((r) => setTimeout(r, i < 8 ? 300 : 2000));
  }
  throw new Error('portrait not made');
}

function image(src) {
  return new Promise((ok) => { if (!src) return ok(null); const im = new Image(); im.onload = () => ok(im); im.onerror = () => ok(null); im.src = src; });
}

// the body's texture as the game shows it: skin x skin colour, then the armour painted over it
async function bodyTexture(THREE, d) {
  const [skin, chest, legs] = await Promise.all(['skin', 'chest', 'legs'].map((k) => image(d.body?.[k] ? `models/${d.body[k]}` : '')));
  const base = skin || chest || legs;
  if (!base) return null;
  const w = base.naturalWidth, h = base.naturalHeight, c = document.createElement('canvas');
  c.width = w; c.height = h;
  const g = c.getContext('2d');
  if (skin) {
    g.drawImage(skin, 0, 0, w, h);
    g.globalCompositeOperation = 'multiply'; g.fillStyle = d.skinColor || '#ffffff'; g.fillRect(0, 0, w, h);
    g.globalCompositeOperation = 'destination-in'; g.drawImage(skin, 0, 0, w, h);
    g.globalCompositeOperation = 'source-over';
  } else { g.fillStyle = d.skinColor || '#c8a080'; g.fillRect(0, 0, w, h); }
  if (chest) g.drawImage(chest, 0, 0, w, h);
  if (legs) g.drawImage(legs, 0, 0, w, h);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace; t.flipY = false; t.anisotropy = 8;
  return t;
}

// The effects the game plays on what they carry (a fire staff's flames, sparks, mist) and the lights
// some items give, played here from the game's own settings (the server reads them: Portraits.Effects).
// A simple particle player: born in the effect's shape at its rate, moving at its speed with its gravity,
// growing and fading over their life as the game's curves say; fire adds light, smoke covers.
const MAX_PARTICLES = 200;
const softDot = (() => { let t = null; return (THREE) => {
  if (t) return t;
  const c = document.createElement('canvas'); c.width = c.height = 64;
  const g = c.getContext('2d'), r = g.createRadialGradient(32, 32, 0, 32, 32, 32);
  r.addColorStop(0, 'rgba(255,255,255,1)'); r.addColorStop(0.4, 'rgba(255,255,255,0.5)'); r.addColorStop(1, 'rgba(255,255,255,0)');
  g.fillStyle = r; g.fillRect(0, 0, 64, 64);
  t = new THREE.CanvasTexture(c); return t;
}; })();
const lerp3 = (a, t) => t < 0.5 ? a[0] + (a[1] - a[0]) * t * 2 : a[1] + (a[2] - a[1]) * (t - 0.5) * 2;
async function makeEffects(THREE, list) {
  const out = [];
  for (const e of list) {
    if (e.kind === 'light') {
      // the light it gives: lighting the model, and a soft glow where it is
      const c = new THREE.Color(e.color[0], e.color[1], e.color[2]);
      const g = new THREE.Group();
      // (softer than the game's: in the portrait's soft studio light, a staff's or the void knife's light at full
      // strength washed the whole outfit in its colour)
      const pl = new THREE.PointLight(c, Math.min(4, e.intensity) * 0.3, Math.max(0.5, Math.min(1.5, e.range)), 2);
      pl.position.set(...e.pos); g.add(pl);
      const sp = new THREE.Sprite(new THREE.SpriteMaterial({ map: softDot(THREE), color: c, blending: THREE.AdditiveBlending, depthWrite: false, transparent: true, opacity: 0.55 }));
      sp.position.set(...e.pos); sp.scale.setScalar(0.18); g.add(sp);
      out.push({ object: g, step() {} });
      continue;
    }
    const n = Math.max(4, Math.min(MAX_PARTICLES, e.max || MAX_PARTICLES, Math.ceil(e.rate * Math.max(0.1, e.life) * 1.2)));
    const geo = new THREE.BufferGeometry();
    const pos = new Float32Array(n * 3), col = new Float32Array(n * 4), size = new Float32Array(n), age = new Float32Array(n);
    geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    geo.setAttribute('age', new THREE.BufferAttribute(age, 1));
    geo.setAttribute('color', new THREE.BufferAttribute(col, 4));
    geo.setAttribute('psize', new THREE.BufferAttribute(size, 1));
    let map = softDot(THREE);
    if (e.tex) { try { const t = await new THREE.TextureLoader().loadAsync(`models/${e.tex}`); t.colorSpace = THREE.SRGBColorSpace; map = t; } catch {} }
    // gradient-mapped (fire, frost, lightning): the picture's light picks the colour along a ramp from dark
    // to bright, the ramp changing over the particle's life, and is its see-through too
    const R = e.ramp, v4 = (c) => new THREE.Vector4(...(c || [1, 1, 1, 1]));
    const mat = new THREE.ShaderMaterial({
      uniforms: { map: { value: map }, scale: { value: 400 }, tiles: { value: e.tiles || 1 }, cover: { value: e.additive ? 0 : 1 },
        ramped: { value: R ? 1 : 0 }, hi0: { value: v4(R?.[0]) }, hi1: { value: v4(R?.[1]) }, lo0: { value: v4(R?.[2]) }, lo1: { value: v4(R?.[3]) } },
      vertexShader: `attribute vec4 color; attribute float psize; attribute float age; varying vec4 vC; varying float vT; uniform float scale;
        void main() { vC = color; vT = age; vec4 mv = modelViewMatrix * vec4(position, 1.0); gl_PointSize = psize * scale / -mv.z; gl_Position = projectionMatrix * mv; }`,
      fragmentShader: `uniform sampler2D map; uniform float tiles; uniform float cover; uniform float ramped; uniform vec4 hi0, hi1, lo0, lo1; varying vec4 vC; varying float vT;
        void main() { vec2 uv = vec2(gl_PointCoord.x, 1.0 - gl_PointCoord.y) / tiles + vec2(0.0, 1.0 - 1.0 / tiles); vec4 t = texture2D(map, uv);
          float a = t.a; if (cover > 0.5 && a > 0.99) a = smoothstep(0.03, 0.35, max(t.r, max(t.g, t.b)));   // (a covering picture with no see-through: its black is the background)
          vec3 rgb = t.rgb;
          if (ramped > 0.5) { vec4 c = mix(mix(lo0, lo1, vT), mix(hi0, hi1, vT), t.r); rgb = c.rgb; a = t.r * c.a; }
          gl_FragColor = vec4(min(rgb * vC.rgb, vec3(1.0)), a * vC.a); if (gl_FragColor.a < 0.01) discard; }`,
      transparent: true, depthWrite: false,
      // light-adding: adds colour only, never makes the see-through background solid (its dark would show as black squares)
      ...(e.additive ? { blending: THREE.CustomBlending, blendEquation: THREE.AddEquation, blendSrc: THREE.SrcAlphaFactor, blendDst: THREE.OneFactor,
        blendSrcAlpha: THREE.ZeroFactor, blendDstAlpha: THREE.OneFactor } : { blending: THREE.NormalBlending }),
    });
    const points = new THREE.Points(geo, mat);
    points.frustumCulled = false;
    // trails: where each particle has been, drawn as a line of soft dots behind it that fades (a lightning
    // arc, a swirl); some effects draw only these
    const PER = 3, TS = e.trail ? 16 * PER : 0, tN = n * TS;   // (16 points of its path, 3 dots between each)
    let tgeo = null;
    const tpos = new Float32Array(tN * 3), tcol = new Float32Array(tN * 4), tsize = new Float32Array(tN), tage = new Float32Array(tN);
    if (TS) {
      tgeo = new THREE.BufferGeometry();
      tgeo.setAttribute('position', new THREE.BufferAttribute(tpos, 3)); tgeo.setAttribute('color', new THREE.BufferAttribute(tcol, 4));
      tgeo.setAttribute('psize', new THREE.BufferAttribute(tsize, 1)); tgeo.setAttribute('age', new THREE.BufferAttribute(tage, 1));
      const tp = new THREE.Points(tgeo, mat); tp.frustumCulled = false; points.add(tp);
    }
    const P = Array.from({ length: n }, () => ({ age: 1e9, life: 1, p: new THREE.Vector3(), v: new THREE.Vector3(), w: new THREE.Vector3(), h: [], ht: 0 }));
    const rnd = new THREE.Vector3();
    const origin = new THREE.Vector3(...e.pos), dir = new THREE.Vector3(...e.dir).normalize();
    const side = new THREE.Vector3().crossVectors(dir, Math.abs(dir.y) < 0.9 ? new THREE.Vector3(0, 1, 0) : new THREE.Vector3(1, 0, 0)).normalize();
    const up2 = new THREE.Vector3().crossVectors(dir, side);
    let owed = 0, next = 0;
    const spawn = (q) => {
      q.age = 0; q.life = Math.max(0.05, e.life * (0.8 + Math.random() * 0.4)); q.h.length = 0; q.ht = 0; q.w.set(0, 0, 0);
      const a = Math.random() * Math.PI * 2, r = Math.sqrt(Math.random()) * e.radius;
      if (e.shape.startsWith('Cone') || e.shape === 'Circle' || e.shape === 'Donut') {
        q.p.copy(origin).addScaledVector(side, Math.cos(a) * r).addScaledVector(up2, Math.sin(a) * r);
        const spread = Math.tan((e.angle || 0) * Math.PI / 180) * Math.sqrt(Math.random());
        q.v.copy(dir).addScaledVector(side, Math.cos(a) * spread).addScaledVector(up2, Math.sin(a) * spread).normalize().multiplyScalar(e.speed);
      } else {
        const u = new THREE.Vector3(Math.random() * 2 - 1, Math.random() * 2 - 1, Math.random() * 2 - 1).normalize();
        q.p.copy(origin).addScaledVector(u, e.shape.startsWith('Sphere') || e.shape.startsWith('Hemisphere') ? r : 0);
        q.v.copy(u).multiplyScalar(e.speed);
      }
    };
    const step = (dt) => {
      owed += e.rate * dt;
      while (owed >= 1) { owed -= 1; let k = 0; while (k < n && P[next].age < P[next].life) { next = (next + 1) % n; k++; } if (k < n) spawn(P[next]); else { owed = 0; break; } }
      for (let i = 0; i < n; i++) {
        const q = P[i];
        q.age += dt;
        if (q.age >= q.life) { size[i] = 0; col[i * 4 + 3] = 0; for (let k = i * TS; k < (i + 1) * TS; k++) { tsize[k] = 0; tcol[k * 4 + 3] = 0; } continue; }
        q.v.y -= 9.81 * e.gravity * dt;
        q.p.addScaledVector(q.v, dt);
        if (e.noise) {   // (the game's noise: a wander that changes its mind about freq times a second)
          // (a fast one swings harder: lightning at frequency 10 zigzags across its short trail)
          rnd.set(Math.random() * 2 - 1, Math.random() * 2 - 1, Math.random() * 2 - 1).normalize().multiplyScalar(e.noise.strength * Math.max(1, e.noise.freq * 0.6));
          q.w.lerp(rnd, Math.min(1, dt * Math.max(1, e.noise.freq) * 4));
          q.p.addScaledVector(q.w, dt);
        }
        const t = q.age / q.life;
        pos[i * 3] = q.p.x; pos[i * 3 + 1] = q.p.y; pos[i * 3 + 2] = q.p.z;
        for (let k = 0; k < 4; k++) col[i * 4 + k] = e.color[k] * lerp3([e.fade[0][k], e.fade[1][k], e.fade[2][k]], t);
        size[i] = e.trail?.only ? 0 : e.size * lerp3(e.grow, t); age[i] = t;
        if (TS) {
          // a point of the trail every so often; the line drawn through them, fading to its end
          const keep = TS / PER, every = Math.max(0.005, e.trail.life * q.life / keep);
          q.ht += dt;
          if (q.ht >= every || !q.h.length) { q.ht = 0; q.h.unshift(q.p.clone()); if (q.h.length > keep) q.h.pop(); }
          for (let k = 0; k < TS; k++) {
            const o = i * TS + k, seg = Math.floor(k / PER), a = seg === 0 ? q.p : q.h[seg - 1], b = q.h[seg];
            if (!a || !b) { tsize[o] = 0; tcol[o * 4 + 3] = 0; continue; }
            const f = (k % PER) / PER, fade = 1 - k / TS;
            tpos[o * 3] = a.x + (b.x - a.x) * f; tpos[o * 3 + 1] = a.y + (b.y - a.y) * f; tpos[o * 3 + 2] = a.z + (b.z - a.z) * f;
            for (let c = 0; c < 3; c++) tcol[o * 4 + c] = col[i * 4 + c];
            tcol[o * 4 + 3] = col[i * 4 + 3] * fade; tsize[o] = Math.max(0.012, e.trail.width) * (0.5 + 0.7 * fade); tage[o] = t;
          }
        }
      }
      geo.attributes.position.needsUpdate = true; geo.attributes.color.needsUpdate = true; geo.attributes.psize.needsUpdate = true; geo.attributes.age.needsUpdate = true;
      if (tgeo) for (const k of ['position', 'color', 'psize', 'age']) tgeo.attributes[k].needsUpdate = true;
    };
    for (let i = 0; i < 40; i++) step(0.05);   // (already going when first seen)
    out.push({ object: points, step, mat });
  }
  return out;
}

// The glow on some weapons (storm, blood and nature ones, some staffs): the game draws a copy of the weapon a
// little bigger as noise drifting along it in a bright colour, adding light, faded where it turns edge-on.
function weaponGlow(THREE, root, list) {
  const out = [], byName = new Map(list.map((g) => [g.name, g]));
  root.traverse((o) => {
    const g = o.isMesh && byName.get(o.material?.name);
    if (!g) return;
    const map = o.material.map;
    const top = Math.max(1, g.color[0], g.color[1], g.color[2]);   // (the game's colour goes past white: kept in its hue)
    const mat = new THREE.ShaderMaterial({
      uniforms: { map: { value: map }, tint: { value: new THREE.Vector3(g.color[0] / top, g.color[1] / top, g.color[2] / top) }, strength: { value: Math.min(2, top) * (g.color[3] ?? 1) }, time: { value: Math.random() * 10 }, hasMap: { value: map ? 1 : 0 },
        scroll: { value: g.scroll ?? 0.13 }, scale: { value: g.scale || 1 }, push: { value: Math.min(0.02, g.push ?? 0.004) }, pixel: { value: g.pixel || 64 } },
      vertexShader: `varying vec2 vUv; varying vec3 vN; varying vec3 vV; uniform float push;
        void main() { vUv = uv; vec4 mv = modelViewMatrix * vec4(position + normal * push, 1.0); vN = normalize(normalMatrix * normal); vV = normalize(-mv.xyz); gl_Position = projectionMatrix * mv; }`,
      // (the game pixelates the noise on purpose: its blocks are part of the look)
      fragmentShader: `uniform sampler2D map; uniform vec3 tint; uniform float strength; uniform float time; uniform float hasMap; uniform float scroll; uniform float scale; uniform float pixel; varying vec2 vUv; varying vec3 vN; varying vec3 vV;
        void main() {
          vec2 uv = floor(vUv * pixel) / pixel;
          float n1 = hasMap > 0.5 ? texture2D(map, uv * scale + vec2(0.0, time * scroll)).r : 0.6;
          float n2 = hasMap > 0.5 ? texture2D(map, uv * scale * 0.7 - vec2(time * scroll * 0.4, time * scroll * 0.7)).r : 0.6;
          float n = smoothstep(0.25, 0.9, n1 * n2 * 1.8);
          float face = abs(dot(normalize(vN), vV));   // (edge-on: faded, so its sheets don't show as sheets)
          float a = n * smoothstep(0.05, 0.6, face) * strength;
          gl_FragColor = vec4(tint * a, 1.0); if (a < 0.01) discard; }`,
      transparent: true, depthWrite: false, side: THREE.DoubleSide,
      // adds light only: the see-through background stays see-through
      blending: THREE.CustomBlending, blendEquation: THREE.AddEquation, blendSrc: THREE.OneFactor, blendDst: THREE.OneFactor,
      blendSrcAlpha: THREE.ZeroFactor, blendDstAlpha: THREE.OneFactor,
    });
    o.material = mat; o.renderOrder = 2;
    out.push({ object: null, anim: true, step: (dt) => { mat.uniforms.time.value += dt; } });
  });
  return out;
}

async function model(id, again = 0) {
  if (!models.has(id)) {
    const p = (async () => {
      const [{ THREE, GLTFLoader }, d] = await Promise.all([lib(), info(id)]);
      // (asked again: the server has made it again since, so not the copy the browser kept)
      const gltf = await new GLTFLoader().loadAsync(`models/${d.glb}${again ? `?r=${Date.now()}` : ''}`);
      const root = gltf.scene, hair = new THREE.Color(d.hairColor || '#ffffff');
      const body = await bodyTexture(THREE, d);
      root.traverse((o) => {
        if (!o.isMesh) return;
        const m = o.material;
        if (m.name === 'munin_body') { if (body) { m.map = body; m.color.set('#ffffff'); } else m.color.set(d.skinColor || '#c8a080'); }
        else if (m.name === 'munin_hair') m.color.copy(hair);
        m.metalness = 0; m.roughness = Math.max(m.roughness, 0.7);
        if (m.map) m.map.anisotropy = 8;
        m.needsUpdate = true;
      });
      root.userData.effects = await makeEffects(THREE, d.effects || []);
      for (const e of root.userData.effects) if (e.object) root.add(e.object);
      root.userData.effects.push(...weaponGlow(THREE, root, d.weaponGlow || []));
      root.userData.partial = d.textures === false;   // made while some of its textures were still being read: made again soon
      return root;
    })();
    p.catch(() => models.delete(id));   // (asked again next time)
    models.set(id, p);
  }
  return models.get(id);
}

// Show portrait `id` in element `box` (sized by CSS). Resolves true when drawn, false if it couldn't be.
// keepView: the same player in other clothes, seen from where the viewer had turned them. A portrait
// not made yet, or made before all of its textures were in (something new), is asked for again while shown.
export async function showPortrait(box, id, keepView = false, again = 0) {
  if (!id || !box) return false;
  const retry = (ms) => setTimeout(() => {
    if (!box.isConnected || box.closest('[hidden]')) return;   // (the card was closed, or shows something else now)
    models.delete(id);
    showPortrait(box, id, true, again + 1).then((ok) => { if (ok) box.classList.add('drawn'); });
  }, ms);
  let root;
  try { root = await model(id, again); } catch { if (again < 20) retry(15000); return false; }
  if (!box.isConnected) return false;
  root.userData.id = id;
  if (root.userData.partial && again < 40) retry(again < 6 ? 5000 : 15000);
  const { THREE, OrbitControls } = await lib();
  if (!view) {
    let renderer;
    try { renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true }); } catch { return false; }
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.toneMapping = THREE.ACESFilmicToneMapping; renderer.toneMappingExposure = 1.05;
    // a studio light: warm key from the front left, soft fill, a cool rim from behind to lift the outline
    const scene = new THREE.Scene();
    scene.add(new THREE.HemisphereLight(0xfff4e6, 0x2a2018, 1.1));
    const key = new THREE.DirectionalLight(0xffe2c0, 3.0); key.position.set(-1.6, 2.4, -2.6); scene.add(key);
    const fill = new THREE.DirectionalLight(0xc8d8ff, 0.8); fill.position.set(2.2, 0.8, -1.8); scene.add(fill);
    const rim = new THREE.DirectionalLight(0xbfd8ff, 2.2); rim.position.set(0.8, 2.6, 3); scene.add(rim);
    const camera = new THREE.PerspectiveCamera(18, 2 / 3, 0.05, 80);
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enablePan = false; controls.enableZoom = false; controls.enableDamping = false;
    controls.minPolarAngle = Math.PI * 0.3; controls.maxPolarAngle = Math.PI * 0.62;
    view = { THREE, renderer, scene, camera, controls, current: null, draw: () => renderer.render(scene, camera) };
    controls.addEventListener('change', view.draw);
    renderer.domElement.className = 'pc-3d';
    renderer.domElement.title = 'Drag to turn';
  }
  const v = view;
  if (v.current !== root) {
    // where the viewer had turned the last one (around its own middle)
    const turned = keepView && v.current ? v.camera.position.clone().sub(v.controls.target) : null;
    if (v.current) v.scene.remove(v.current);
    v.scene.add(root); v.current = root;
    // every character the same size: framed on their body (not what they hold or wear, nor the effects),
    // from the same distance, enough for a tall Viking head to toe
    const b = new THREE.Box3();
    root.traverse((o) => { if (o.isMesh && o.material?.name === 'munin_body') b.expandByObject(o); });
    if (b.isEmpty()) root.traverse((o) => { if (o.isMesh) b.expandByObject(o); });
    const c = b.getCenter(new THREE.Vector3());
    c.y = b.min.y + 0.95;   // (the middle of a 1.9 m figure standing on its feet)
    const half = Math.tan(THREE.MathUtils.degToRad(v.camera.fov / 2));
    const dist = (2.05 * 0.5) / half;
    const s = { y: 2.05 };
    v.controls.target.copy(c);
    if (turned) v.camera.position.copy(c).add(turned);
    else v.camera.position.set(c.x, c.y + s.y * 0.04, c.z - dist);
    v.controls.update();
  }
  const w = box.clientWidth || 140, h = box.clientHeight || 210;
  v.renderer.setPixelRatio(Math.min(3, (devicePixelRatio || 1) * 2));   // (drawn at twice the size: smoother edges)
  v.renderer.setSize(w, h, false);
  v.camera.aspect = w / h; v.camera.updateProjectionMatrix();
  if (v.renderer.domElement.parentNode !== box) box.appendChild(v.renderer.domElement);
  // effects: played while the portrait is on screen
  const fx = root.userData.effects || [];
  for (const f of fx) if (f.mat) f.mat.uniforms.scale.value = h * v.renderer.getPixelRatio() / (2 * Math.tan(THREE.MathUtils.degToRad(v.camera.fov / 2)));
  if (v.raf) cancelAnimationFrame(v.raf), v.raf = 0;
  if (fx.some((f) => f.mat || f.anim)) {
    let last = performance.now();
    const tick = (now) => {
      if (!v.renderer.domElement.isConnected || v.current !== root) { v.raf = 0; return; }
      const dt = Math.min(0.1, (now - last) / 1000); last = now;
      for (const f of fx) f.step(dt);
      v.draw();
      v.raf = requestAnimationFrame(tick);
    };
    v.raf = requestAnimationFrame(tick);
  }
  v.draw();
  return true;
}

// Start loading three.js, and a portrait, ahead of a card being opened (models are kept once loaded)
export function preloadPortrait(id) { lib().catch(() => {}); if (id) model(id).catch(() => {}); }
