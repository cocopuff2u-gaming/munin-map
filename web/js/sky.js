// Sky, sun, moon and fog for the 3D view, driven by the game's time of day.
//
// A sky dome (gradient + sun disc + stars) follows the camera; a warm sun and a cool
// hemisphere light track the same position; at night a faint moon takes over. The
// dome is also baked into a small environment map so water and metal catch the sky.
// Day fraction is the game's: 0 midnight, 0.25 sunrise, 0.5 noon, 0.75 sunset.

import * as THREE from 'three';

const skyVert = `
varying vec3 vDir;
void main() {
  vDir = position;
  vec4 wp = modelMatrix * vec4(position, 1.0);
  gl_Position = projectionMatrix * viewMatrix * wp;
  gl_Position.z = gl_Position.w;   // always at the far plane
}`;

const skyFrag = `
varying vec3 vDir;
uniform vec3 uSun; uniform vec3 uZenith; uniform vec3 uHorizon; uniform vec3 uSunTint; uniform float uNight; uniform float uSunDisc;
float hash(vec3 p) { return fract(sin(dot(p, vec3(12.9898, 78.233, 37.719))) * 43758.5453); }
void main() {
  vec3 d = normalize(vDir);
  float h = max(d.y, 0.0);
  vec3 col = mix(uHorizon, uZenith, pow(h, 0.5));
  float sd = max(dot(d, uSun), 0.0);
  col += uSunTint * (pow(sd, 400.0) * uSunDisc + pow(sd, 10.0) * 0.22 + pow(sd, 2.5) * 0.06);
  if (uNight > 0.001) {
    vec3 p = floor(d * 260.0);
    float s = hash(p);
    float star = smoothstep(0.992, 1.0, s) * uNight * smoothstep(0.02, 0.25, d.y);
    col += vec3(star * 0.8);
  }
  gl_FragColor = vec4(col, 1.0);
  #include <tonemapping_fragment>
  #include <colorspace_fragment>
}`;

const c = (hex) => new THREE.Color(hex);
const NIGHT = { zenith: c(0x05081a), horizon: c(0x0e1730) };
const DAY = { zenith: c(0x3d7ad4), horizon: c(0xb9d3ee) };
const DUSK = { zenith: c(0x3a4f8e), horizon: c(0xf3a457) };
const lerp = (a, b, t) => a + (b - a) * t;
const smooth = (a, b, x) => { const t = Math.min(1, Math.max(0, (x - a) / (b - a))); return t * t * (3 - 2 * t); };

export class Lighting {
  constructor(scene, renderer) {
    this.scene = scene;
    this.renderer = renderer;
    this.frac = 0.5;
    this.shadows = false;

    this.hemi = new THREE.HemisphereLight(0xdde9ff, 0x4a5a3a, 1);
    this.sun = new THREE.DirectionalLight(0xfff2dc, 2.4);
    this.moon = new THREE.DirectionalLight(0x8fa8ff, 0);
    this.sun.shadow.mapSize.set(2048, 2048);
    this.sun.shadow.bias = -0.0006;
    this.sun.shadow.normalBias = 0.6;
    const cam = this.sun.shadow.camera;
    cam.near = 50; cam.far = 4500;
    scene.add(this.hemi, this.sun, this.sun.target, this.moon);

    this.uniforms = {
      uSun: { value: new THREE.Vector3(0, 1, 0) }, uZenith: { value: DAY.zenith.clone() }, uHorizon: { value: DAY.horizon.clone() },
      uSunTint: { value: new THREE.Color(1, 0.95, 0.85) }, uNight: { value: 0 }, uSunDisc: { value: 1.6 },
    };
    this.dome = new THREE.Mesh(new THREE.SphereGeometry(1, 32, 16), new THREE.ShaderMaterial({ uniforms: this.uniforms, vertexShader: skyVert, fragmentShader: skyFrag, side: THREE.BackSide, depthWrite: false, depthTest: false, fog: false }));
    this.dome.scale.setScalar(20000);
    this.dome.renderOrder = -1000;
    this.dome.frustumCulled = false;
    scene.add(this.dome);

    scene.fog = new THREE.Fog(DAY.horizon.clone(), 2500, 9500);
    scene.background = DAY.horizon.clone();
    this.pmrem = new THREE.PMREMGenerator(renderer);
    this.envScene = new THREE.Scene();
    this.envDome = new THREE.Mesh(this.dome.geometry, this.dome.material);
    this.envDome.scale.setScalar(100);
    this.envScene.add(this.envDome);
    this.envTarget = null;
    this.setTime(0.5);
  }

  // sun direction for a day fraction, as the game turns its sun (EnvMan.SetEnv: Euler(-90 + sunAngle, 0, 0) x
  // Euler(0, -90, 0) x Euler(-90 + 360 x fraction, 0, 0), sunAngle 60): rises in the east at a quarter, highest
  // in the south at noon (60 degrees up), sets in the west. (Scene z is south.)
  sunDir(frac, angle = 60) {
    const a = (frac - 0.25) * Math.PI * 2, b = (angle - 90) * Math.PI / 180;
    return new THREE.Vector3(Math.cos(a), Math.cos(b) * Math.sin(a), -Math.sin(b) * Math.sin(a)).normalize();
  }

  // the weather here (data/environments.json: one of the game's EnvSetups), or null for the map's own look
  setEnv(env) { if (env === this.env) return; this.env = env; this.setTime(this.frac); }

  setTime(frac) {
    this.frac = ((frac % 1) + 1) % 1;
    const dir = this.sunDir(this.frac, this.env?.sunAngle ?? 60), e = dir.y;
    const day = smooth(-0.06, 0.22, e), dusk = 1 - smooth(0, 0.3, Math.abs(e)), night = 1 - smooth(-0.25, 0.02, e);
    const zenith = NIGHT.zenith.clone().lerp(DAY.zenith, day).lerp(DUSK.zenith, dusk * 0.7);
    const horizon = NIGHT.horizon.clone().lerp(DAY.horizon, day).lerp(DUSK.horizon, dusk * 0.85);
    this.uniforms.uSun.value.copy(dir);
    this.uniforms.uZenith.value.copy(zenith);
    this.uniforms.uHorizon.value.copy(horizon);
    this.uniforms.uNight.value = night;
    this.uniforms.uSunDisc.value = e > -0.05 ? 1.6 : 0;
    this.uniforms.uSunTint.value.set(1, lerp(0.55, 0.95, smooth(0, 0.3, e)), lerp(0.25, 0.82, smooth(0, 0.35, e))).multiplyScalar(smooth(-0.1, 0.02, e));

    this.sun.color.set(1, lerp(0.6, 0.95, smooth(0, 0.3, e)), lerp(0.3, 0.86, smooth(0, 0.35, e)));
    this.sun.intensity = 2.6 * smooth(-0.02, 0.2, e);
    this.sunDirection = dir;
    this.hemi.color.copy(zenith).lerp(new THREE.Color(0xffffff), 0.35);
    this.hemi.groundColor.set(0x4a5a3a).lerp(new THREE.Color(0x0a0c12), night);
    this.hemi.intensity = lerp(0.32, 1.0, day);
    this.moon.intensity = 0.55 * night;
    this.moonDirection = new THREE.Vector3(-dir.x, Math.max(0.35, -dir.y), -dir.z).normalize();
    this.sun.castShadow = this.shadows && this.sun.intensity > 0.05;
    this.moon.castShadow = this.shadows && this.sun.intensity <= 0.05;

    this.scene.fog.color.copy(horizon);
    this.scene.background.copy(horizon);
    this.scene.fog.near = 2500; this.scene.fog.far = 9500;
    if (this.env) this.gameLight(this.env, dir);
    this.envDirty = true;
  }

  // The game's own light for this weather and time (EnvMan.SetEnv): its night, morning, day and evening
  // weights (EnvMan.Update), the sun's colour and strength, the fog's colour (toward the sun's own fog
  // colour by day), the ambient between its night and day colours. Fog density sets how far the fog
  // starts, scaled up for a view from far above (the game's own would white out the map).
  gameLight(env, dir) {
    const f = this.frac, c01 = (x) => Math.min(1, Math.max(0, x));
    let n = Math.pow(Math.max(1 - c01(f / 0.25), c01((f - 0.75) / 0.25)), 0.5), d = Math.pow(c01(1 - Math.abs(f - 0.5) / 0.25), 0.5);
    let m = Math.min(c01(1 - (f - 0.26) / -0.02), c01(1 - (f - 0.26) / 0.08)), ev = Math.min(c01(1 - (f - 0.74) / -0.08), c01(1 - (f - 0.74) / 0.02));
    const k = 1 / (n + d + m + ev); n *= k; d *= k; m *= k; ev *= k;
    if (env.dark) { n = 1; d = m = ev = 0; }
    const col = (q, day) => {
      const w = [n, day ? m : 0, day ? d : 0, day ? ev : 0], o = [0, 0, 0];
      for (let i = 0; i < 4; i++) for (let a = 0; a < 3; a++) o[a] += (q && q[i] ? q[i][a] : 0) * w[i];
      return new THREE.Color().setRGB(o[0], o[1], o[2], THREE.SRGBColorSpace);
    };
    const sunCol = col(env.sun, d > 0), fog = col(env.fog, true), fogSun = col(env.fogSun, d > 0);
    fog.lerp(fogSun, c01(Math.max(n, d) * 3));
    const amb = new THREE.Color().setRGB(lerp(env.ambNight[0], env.ambDay[0], d), lerp(env.ambNight[1], env.ambDay[1], d), lerp(env.ambNight[2], env.ambDay[2], d), THREE.SRGBColorSpace);
    const K = 2.6 / 1.2;   // (the map's sun at the game's usual 1.2)
    this.sun.color.copy(sunCol); this.sun.intensity = K * env.lightDay * d * (dir.y > -0.02 ? 1 : 0);
    this.moon.color.copy(sunCol); this.moon.intensity = K * env.lightNight * n;
    this.hemi.color.copy(amb); this.hemi.groundColor.copy(amb).multiplyScalar(0.45); this.hemi.intensity = 1.0;
    const zenith = this.uniforms.uZenith.value.clone().lerp(fog, 0.35);
    this.uniforms.uHorizon.value.copy(fog); this.uniforms.uZenith.value.copy(zenith);
    this.uniforms.uSunTint.value.copy(sunCol).multiplyScalar(c01(dir.y * 8 + 0.2));
    this.scene.fog.color.copy(fog); this.scene.background.copy(fog);
    const fd = env.fogDensity || [0.01, 0.01, 0.01, 0.01];
    const dens = Math.max(1e-4, fd[0] * n + fd[1] * m + fd[2] * d + fd[3] * ev);
    const far = Math.min(14000, Math.max(1500, 9500 * Math.sqrt(0.01 / dens)));
    this.scene.fog.far = far; this.scene.fog.near = far * 0.26;
    this.sun.castShadow = this.shadows && this.sun.intensity > 0.05;
    this.moon.castShadow = this.shadows && this.sun.intensity <= 0.05;
  }

  // Bake the sky into an environment map (reflections on water, sheen on metal). Cheap; done
  // only when the time of day changed.
  updateEnvironment() {
    if (!this.envDirty) return;
    this.envDirty = false;
    const old = this.envTarget;
    this.envTarget = this.pmrem.fromScene(this.envScene, 0.04, 10, 200);
    this.scene.environment = this.envTarget.texture;
    this.scene.environmentIntensity = lerp(0.35, 1.0, this.hemi.intensity);
    if (old) old.dispose();
  }

  setShadows(on) {
    this.shadows = on;
    this.renderer.shadowMap.enabled = on;
    this.renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    this.setTime(this.frac);
    this.scene.traverse((o) => { if (o.material) o.material.needsUpdate = true; });
  }

  // Per frame: keep the dome on the camera and the shadow box around the point of interest, sized
  // to the view distance so close-ups get sharp shadows and overviews still get some.
  update(camera, target) {
    this.dome.position.copy(camera.position);
    const dist = camera.position.distanceTo(target);
    const half = Math.min(1400, Math.max(120, dist * 0.9));
    for (const [light, dir] of [[this.sun, this.sunDirection], [this.moon, this.moonDirection]]) {
      light.target.position.copy(target);
      light.position.copy(target).addScaledVector(dir, 2200);
      light.target.updateMatrixWorld();
      const sc = light.shadow.camera;
      if (sc.left !== -half) { sc.left = sc.bottom = -half; sc.right = sc.top = half; sc.updateProjectionMatrix(); }
    }
    this.updateEnvironment();
  }
}
