// The 3D portrait on a player card: their character as the server made it (Models/Portraits), drawn
// with three.js into the card and turned with a drag. The server sends the model with the body's
// skin texture only; the game paints chest and leg armour onto the body, so that mix is done here on
// a canvas (skin tinted with their skin colour, then the chest and leg textures over it). Hair and
// beard take their hair colour. One renderer, moved to whichever card is open; three.js is loaded the
// first time a portrait is shown.

import { getJSON } from './net.js';

import { makeEffects } from './particles.js';
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

// The glow on some weapons (storm, blood and nature ones, some staffs): the game's Unlit/WeaponGlow shader, its
// maths as the game has it. A copy of the weapon pushed out along its normals; a noise picture, its texture
// coordinates snapped to a pixel grid and swirled by a scrolling 3D curl noise, taken away from a fade along
// the weapon's length; that times the glow colour, blended over what is behind. (The game draws in linear
// light: the result is turned to screen colours, like everything else here.)
let curl = null;
function curlNoise(THREE, file) {   // the game's curl noise, 64 x 64 x 64, from the 8 x 8 slices the server saved
  if (!curl) curl = new Promise((ok) => {
    const im = new Image();
    im.onload = () => {
      const c = document.createElement('canvas'); c.width = im.width; c.height = im.height;
      const g = c.getContext('2d'); g.drawImage(im, 0, 0);
      const px = g.getImageData(0, 0, c.width, c.height).data, N = 64, data = new Uint8Array(N * N * N * 4);
      for (let z = 0; z < N; z++) for (let y = 0; y < N; y++) for (let x = 0; x < N; x++) {
        const sx = (z % 8) * N + x, sy = Math.floor(z / 8) * N + y, i = (sy * c.width + sx) * 4, o = ((z * N + y) * N + x) * 4;
        data[o] = px[i]; data[o + 1] = px[i + 1]; data[o + 2] = 0; data[o + 3] = 255;
      }
      const t = new THREE.Data3DTexture(data, N, N, N);
      t.format = THREE.RGBAFormat; t.minFilter = t.magFilter = THREE.LinearFilter;
      t.wrapS = t.wrapT = t.wrapR = THREE.RepeatWrapping; t.needsUpdate = true;
      ok(t);
    };
    im.onerror = () => ok(null);
    im.src = `models/${file}`;
  });
  return curl;
}
async function weaponGlow(THREE, root, list) {
  const out = [], byName = new Map(list.map((g) => [g.name, g]));
  const meshes = [];
  root.traverse((o) => { if (o.isMesh && byName.get(o.material?.name)) meshes.push(o); });
  if (!meshes.length) return out;
  const noise = await curlNoise(THREE, list[0].noise || 'hd/curlnoise.png');
  for (const o of meshes) {
    const g = byName.get(o.material.name), map = o.material.map;
    if (map) { map.wrapS = map.wrapT = THREE.RepeatWrapping; map.needsUpdate = true; }
    const e = g.edge || [0, 1, 0, 1];
    const mat = new THREE.ShaderMaterial({
      uniforms: { map: { value: map }, curl: { value: noise }, hasCurl: { value: noise ? 1 : 0 }, color: { value: new THREE.Vector4(...g.color) },
        time: { value: Math.random() * 10 }, scroll: { value: g.scroll ?? 0.25 }, scrollX: { value: g.scrollX ?? 0.05 }, texScale: { value: g.scale || 1 },
        pixel: { value: g.pixel || 64 }, curlSize: { value: g.curlSize || 1 }, curlStrength: { value: g.curlStrength ?? 0.05 },
        warp: { value: g.warp ? 1 : 0 }, edge: { value: new THREE.Vector4(...e) }, push: { value: g.push ?? 0.05 } },
      vertexShader: `uniform float push; varying vec2 vUv;
        void main() { vUv = uv; gl_Position = projectionMatrix * modelViewMatrix * vec4(position + normal * push, 1.0); }`,
      fragmentShader: `precision highp sampler3D;
        uniform sampler2D map; uniform sampler3D curl; uniform float hasCurl; uniform vec4 color; uniform float time, scroll, scrollX, texScale, pixel, curlSize, curlStrength, warp; uniform vec4 edge;
        varying vec2 vUv;
        void main() {
          vec2 uv = vec2(vUv.x, 1.0 - vUv.y);   // (the game's texture coordinates: glTF's are upside down)
          // the fade along it: from its v (folded to a back and forth with domain warp), soft at both ends
          float v = uv.y;
          if (warp > 0.5) { vec2 k = (2.0 * v >= -2.0 * v) ? vec2(2.0, 0.5) : vec2(-2.0, -0.5); v = 1.0 - abs(k.x * fract(k.y * v) - 1.0); }
          vec2 f = clamp((vec2(v, 1.0 - v) - edge.zx) / vec2(edge.w - edge.z, edge.y - edge.x), 0.0, 1.0);
          f = f * f * (3.0 - 2.0 * f);
          float fade = min(f.x, f.y);
          // the noise: snapped to the pixel grid, swirled by the curl noise, scrolling
          vec2 p = floor(uv * texScale * pixel) / pixel;
          vec3 q = (vec3(p.x, p.y, p.y) - 0.33 * time * scroll) / curlSize;
          vec2 c = hasCurl > 0.5 ? texture(curl, q).rg * 4.0 - 1.0 : vec2(0.5);   // (stored as (v + 1) / 4)
          p += (c * 2.0 - 1.0) * curlSize * curlStrength;
          vec2 s = vec2(p.x + time * scrollX, p.y - time * scroll);
          float n = texture2D(map, vec2(s.x, 1.0 - s.y)).r;
          float a = clamp(fade - n, 0.0, 1.0);
          gl_FragColor = a * color;
          #include <tonemapping_fragment>
          #include <colorspace_fragment>
        }`,
      transparent: true, depthWrite: true, side: THREE.FrontSide, blending: THREE.NormalBlending,
    });
    o.material = mat; o.renderOrder = 2;
    out.push({ object: null, anim: true, step: (dt) => { mat.uniforms.time.value += dt; } });
  }
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
      root.userData.effects.push(...await weaponGlow(THREE, root, d.weaponGlow || []));
      root.userData.partial = d.textures === false;   // made while some of its textures were still being read: made again soon
      return root;
    })();
    p.catch(() => models.delete(id));   // (asked again next time)
    models.set(id, p);
  }
  return models.get(id);
}

// The game's own finish, from its in-game post-processing profile ("ingame"): bloom (intensity 0.3, threshold 0.7 with a
// 0.7 soft knee, radius 5), contrast 1.2, a slightly cool white balance (temperature -8) and ACES tone mapping.
// The scene is drawn in linear light to a buffer; the bright parts are cut out, blurred down a chain of half
// sizes and back up (Unity's post-processing stack v1 bloom), added back, graded and turned to screen colours.
// Exposure +1 stop (x2), as the profile has it.
function finish(THREE, renderer, scene, camera) {
  const quadCam = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1), quadScene = new THREE.Scene();
  const quad = new THREE.Mesh(new THREE.PlaneGeometry(2, 2)); quad.frustumCulled = false; quadScene.add(quad);
  const vs = 'varying vec2 vUv; void main() { vUv = uv; gl_Position = vec4(position.xy, 0.0, 1.0); }';
  const pass = (fs, uniforms) => new THREE.ShaderMaterial({ uniforms, vertexShader: vs, fragmentShader: fs, depthTest: false, depthWrite: false, blending: THREE.NoBlending, toneMapped: false });
  const threshold = Math.pow(0.7, 2.2), knee = threshold * 0.7 + 1e-5;   // (the profile's threshold is in screen colours)
  const prefilter = pass(`uniform sampler2D src; uniform vec2 texel; uniform vec3 curve; uniform float threshold; varying vec2 vUv;
    void main() {
      vec4 d = texel.xyxy * vec4(-1.0, -1.0, 1.0, 1.0);   // (a 4-tap box down to half size)
      vec3 c = (texture2D(src, vUv + d.xy).rgb + texture2D(src, vUv + d.zy).rgb + texture2D(src, vUv + d.xw).rgb + texture2D(src, vUv + d.zw).rgb) * 0.25;
      float br = max(c.r, max(c.g, c.b)), rq = clamp(br - curve.x, 0.0, curve.y); rq = curve.z * rq * rq;
      gl_FragColor = vec4(c * max(rq, br - threshold) / max(br, 1e-5), 1.0);
    }`, { src: { value: null }, texel: { value: new THREE.Vector2() }, curve: { value: new THREE.Vector3(threshold - knee, knee * 2, 0.25 / knee) }, threshold: { value: threshold } });
  const down = pass(`uniform sampler2D src; uniform vec2 texel; varying vec2 vUv;
    void main() { vec4 d = texel.xyxy * vec4(-1.0, -1.0, 1.0, 1.0);
      gl_FragColor = vec4((texture2D(src, vUv + d.xy).rgb + texture2D(src, vUv + d.zy).rgb + texture2D(src, vUv + d.xw).rgb + texture2D(src, vUv + d.zw).rgb) * 0.25, 1.0); }`,
    { src: { value: null }, texel: { value: new THREE.Vector2() } });
  const up = pass(`uniform sampler2D src; uniform sampler2D base; uniform vec2 texel; uniform float scale; varying vec2 vUv;
    void main() {   // (a 9-tap tent up to the next size, added to that size's own)
      vec4 d = texel.xyxy * vec4(1.0, 1.0, -1.0, 0.0) * scale;
      vec3 s = texture2D(src, vUv - d.xy).rgb + texture2D(src, vUv - d.wy).rgb * 2.0 + texture2D(src, vUv - d.zy).rgb
        + texture2D(src, vUv + d.zw).rgb * 2.0 + texture2D(src, vUv).rgb * 4.0 + texture2D(src, vUv + d.xw).rgb * 2.0
        + texture2D(src, vUv + d.zy).rgb + texture2D(src, vUv + d.wy).rgb * 2.0 + texture2D(src, vUv + d.xy).rgb;
      gl_FragColor = vec4(texture2D(base, vUv).rgb + s / 16.0, 1.0); }`,
    { src: { value: null }, base: { value: null }, texel: { value: new THREE.Vector2() }, scale: { value: 1 } });
  const out = pass(`uniform sampler2D scene; uniform sampler2D bloom; uniform vec2 texel; uniform float scale; uniform float intensity; uniform float exposure; varying vec2 vUv;
    vec3 aces(vec3 c) {   // (three.js's ACES fit, the game's tone mapper)
      const mat3 i = mat3(0.59719, 0.07600, 0.02840, 0.35458, 0.90834, 0.13383, 0.04823, 0.01566, 0.83777);
      const mat3 o = mat3(1.60475, -0.10208, -0.00327, -0.53108, 1.10813, -0.07276, -0.07367, -0.00605, 1.07602);
      c = i * c; vec3 a = c * (c + 0.0245786) - 0.000090537, b = c * (0.983729 * c + 0.4329510) + 0.238081; return clamp(o * (a / b), 0.0, 1.0);
    }
    vec3 logc(vec3 x) { return 0.244161 * log2(5.555556 * x + 0.047996) / 3.321928 + 0.386036; }   // (Unity's LogC, for grading contrast)
    vec3 unlogc(vec3 x) { return (pow(vec3(10.0), (x - 0.386036) / 0.244161) - 0.047996) / 5.555556; }
    vec3 srgb(vec3 c) { return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(0.0031308, c)); }
    void main() {
      vec4 sc = texture2D(scene, vUv);
      vec4 d = texel.xyxy * vec4(1.0, 1.0, -1.0, 0.0) * scale;
      vec3 b = (texture2D(bloom, vUv - d.xy).rgb + texture2D(bloom, vUv - d.wy).rgb * 2.0 + texture2D(bloom, vUv - d.zy).rgb
        + texture2D(bloom, vUv + d.zw).rgb * 2.0 + texture2D(bloom, vUv).rgb * 4.0 + texture2D(bloom, vUv + d.xw).rgb * 2.0
        + texture2D(bloom, vUv + d.zy).rgb + texture2D(bloom, vUv + d.wy).rgb * 2.0 + texture2D(bloom, vUv + d.xy).rgb) / 16.0 * intensity;
      vec3 c = (sc.rgb + b) * exposure;
      c *= vec3(0.985, 1.0, 1.025);                                           // white balance: temperature -8
      c = max(unlogc((logc(max(c, vec3(0.0))) - 0.4135884) * 1.2 + 0.4135884), vec3(0.0));   // contrast 1.2
      c = srgb(aces(c));
      gl_FragColor = vec4(c, clamp(max(sc.a, max(b.r, max(b.g, b.b))), 0.0, 1.0));   // (the halo shows over the see-through background too)
    }`, { scene: { value: null }, bloom: { value: null }, texel: { value: new THREE.Vector2() }, scale: { value: 1 }, intensity: { value: 0.3 }, exposure: { value: 2.0 } });
  let w = 0, h = 0, sceneRT = null, levels = [], ups = [];
  const target = (W, H, samples = 0) => new THREE.WebGLRenderTarget(W, H, { type: THREE.HalfFloatType, depthBuffer: samples > 0, samples });
  const blit = (mat, to) => { quad.material = mat; renderer.setRenderTarget(to); renderer.render(quadScene, quadCam); };
  return () => {
    const size = renderer.getDrawingBufferSize(new THREE.Vector2());
    if (size.x !== w || size.y !== h) {
      w = size.x; h = size.y;
      sceneRT?.dispose(); levels.forEach((l) => l.dispose()); ups.forEach((l) => l.dispose()); levels = [];
      sceneRT = target(w, h, 4);
      const logh = Math.log2(Math.min(w, h)) + 5 - 8, n = Math.max(1, Math.min(16, Math.floor(logh)));
      for (let i = 0, lw = w, lh = h; i < n; i++) { lw = Math.max(1, lw >> 1); lh = Math.max(1, lh >> 1); levels.push(target(lw, lh)); }
      ups = levels.slice(0, -1).map((l) => target(l.width, l.height));
      up.uniforms.scale.value = out.uniforms.scale.value = 0.5 + logh - Math.floor(logh);
    }
    const tm = renderer.toneMapping; renderer.toneMapping = THREE.NoToneMapping;
    renderer.setRenderTarget(sceneRT); renderer.setClearColor(0x000000, 0); renderer.clear(); renderer.render(scene, camera);
    // the bright parts, down the chain of half sizes and back up
    prefilter.uniforms.src.value = sceneRT.texture; prefilter.uniforms.texel.value.set(1 / w, 1 / h); blit(prefilter, levels[0]);
    for (let i = 1; i < levels.length; i++) { down.uniforms.src.value = levels[i - 1].texture; down.uniforms.texel.value.set(1 / levels[i - 1].width, 1 / levels[i - 1].height); blit(down, levels[i]); }
    let last = levels[levels.length - 1];
    for (let i = levels.length - 2; i >= 0; i--) {
      up.uniforms.src.value = last.texture; up.uniforms.base.value = levels[i].texture; up.uniforms.texel.value.set(1 / last.width, 1 / last.height);
      blit(up, ups[i]); last = ups[i];
    }
    out.uniforms.scene.value = sceneRT.texture; out.uniforms.bloom.value = last.texture; out.uniforms.texel.value.set(1 / last.width, 1 / last.height);
    blit(out, null);
    renderer.toneMapping = tm;
  };
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
    view = { THREE, renderer, scene, camera, controls, current: null };
    view.draw = finish(THREE, renderer, scene, camera);
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
    // further back when what's held reaches out past the card's sides (a sword held out, a spear across), up
    // to a third further, so the weapon stays in the picture
    // (held items are the rigid parts; skinned clothing's bounds are its rest pose, wider than it hangs)
    const all = b.clone();
    root.traverse((o) => { if (o.isMesh && !o.isSkinnedMesh && !o.material?.name?.startsWith('munin_weaponglow')) all.expandByObject(o); });
    const aspect = (box.clientWidth || 140) / (box.clientHeight || 210);
    const reach = all.isEmpty() ? 0 : Math.max(c.x - all.min.x, all.max.x - c.x) + 0.08;
    const dist = Math.min(1.35, Math.max(1, reach / (1.025 * aspect))) * (2.05 * 0.5) / half;
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
  if (v.raf) cancelAnimationFrame(v.raf), v.raf = 0;
  if (fx.some((f) => f.anim)) {
    const cam = new THREE.Vector3();
    let last = performance.now();
    const tick = (now) => {
      if (!v.renderer.domElement.isConnected || v.current !== root) { v.raf = 0; return; }
      const dt = Math.max(0, Math.min(0.1, (now - last) / 1000));   // (the first frame's time can come a moment before the clock was read)
      last = now;
      root.worldToLocal(cam.copy(v.camera.position));   // (trails and ribbons face the camera)
      for (const f of fx) f.step(dt, cam);
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
