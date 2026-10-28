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

async function model(id) {
  if (!models.has(id)) {
    const p = (async () => {
      const [{ THREE, GLTFLoader }, d] = await Promise.all([lib(), info(id)]);
      const gltf = await new GLTFLoader().loadAsync(`models/${d.glb}`);
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
      return root;
    })();
    p.catch(() => models.delete(id));   // (asked again next time)
    models.set(id, p);
  }
  return models.get(id);
}

// Show portrait `id` in element `box` (sized by CSS). Resolves true when drawn, false if it couldn't be.
export async function showPortrait(box, id) {
  if (!id || !box) return false;
  let root;
  try { root = await model(id); } catch { return false; }
  if (!box.isConnected) return false;
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
    if (v.current) v.scene.remove(v.current);
    v.scene.add(root); v.current = root;
    const b = new THREE.Box3().setFromObject(root), c = b.getCenter(new THREE.Vector3()), s = b.getSize(new THREE.Vector3());
    // facing the camera (the model looks down -z), framed head to toe
    const dist = (s.y * 0.54) / Math.tan(THREE.MathUtils.degToRad(v.camera.fov / 2));
    v.controls.target.copy(c);
    v.camera.position.set(c.x, c.y + s.y * 0.04, c.z - dist);
    v.controls.update();
  }
  const w = box.clientWidth || 140, h = box.clientHeight || 210;
  v.renderer.setPixelRatio(Math.min(3, (devicePixelRatio || 1) * 2));   // (drawn at twice the size: smoother edges)
  v.renderer.setSize(w, h, false);
  v.camera.aspect = w / h; v.camera.updateProjectionMatrix();
  if (v.renderer.domElement.parentNode !== box) box.appendChild(v.renderer.domElement);
  v.draw();
  return true;
}

// Start loading three.js, and a portrait, ahead of a card being opened (models are kept once loaded)
export function preloadPortrait(id) { lib().catch(() => {}); if (id) model(id).catch(() => {}); }
