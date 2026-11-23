// PlayShift Grass first-person test area (browser preview of the Unity implementation).
// Same logic as the Unity module: grass-core.js is a port of the C# core; the grass vertex code mirrors
// InstancedGrass.shader (cut-shortening animation, bending away from the cutting head, wind, colour variation).
import * as THREE from './three.module.js';
import { GLTFLoader } from './GLTFLoader.js';
import { GrassProgression, DEFAULT_LEVELS, mulberry32, hash01 } from './grass-core.js';

const Q = new URLSearchParams(location.search);
const LEVEL = Math.min(5, Math.max(1, +(Q.get('level') || 3))) - 1;
const SIZE = +(Q.get('size') || 24);          // test lawn, metres
const CELLS = +(Q.get('cells') || 480);       // logical cutting grid (5 cm cells: smooth, round cut edges)
const BLADES_M2 = +(Q.get('density') || 1000); // blades per m2 at full detail (level density scales this)
const FPS = +(Q.get('fps') || 30);
const TRIMMER_SCALE = +(Q.get('tscale') || 1.85);
const SHAFT_STRETCH = +(Q.get('shaft') || 1.7);
const HEAD_RADIUS = 0.35 * TRIMMER_SCALE;     // blade tip radius of the model's cutting head (= cut radius)
const CUT_ANIM = 0.18;                        // seconds for a cut blade to shorten

// ------------------------------------------------------------------ logic
const prog = new GrassProgression(DEFAULT_LEVELS, { cellsX: CELLS, cellsZ: CELLS, sizeX: SIZE, sizeZ: SIZE, noiseCells: 44 });
const field = prog.field;
prog.startLevel(LEVEL);
const level = DEFAULT_LEVELS[LEVEL];

// ------------------------------------------------------------------ renderer / scene
const canvas = document.getElementById('scene');
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
renderer.setPixelRatio(1);
renderer.setSize(innerWidth, innerHeight, false);
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.05;
renderer.shadowMap.enabled = true;
renderer.shadowMap.type = THREE.PCFSoftShadowMap;

const scene = new THREE.Scene();
const HORIZON = new THREE.Color('#cfe0ef');
scene.fog = new THREE.Fog(HORIZON, 60, 260);
const camera = new THREE.PerspectiveCamera(68, innerWidth / innerHeight, 0.03, 900);

const SUN_DIR = new THREE.Vector3(-0.55, 0.62, 0.56).normalize();
const sun = new THREE.DirectionalLight(0xfff1dc, 3.1);
sun.castShadow = true;
sun.shadow.mapSize.set(2048, 2048);
Object.assign(sun.shadow.camera, { left: -16, right: 16, top: 16, bottom: -16, near: 1, far: 120 });
sun.shadow.bias = -0.0004;
sun.shadow.normalBias = 0.02;
scene.add(sun, sun.target);
scene.add(new THREE.HemisphereLight(0xbcd8ff, 0x5a6b34, 1.15));

// ------------------------------------------------------------------ sky with procedural clouds
{
  const sky = new THREE.Mesh(new THREE.SphereGeometry(800, 48, 24), new THREE.ShaderMaterial({
    side: THREE.BackSide, depthWrite: false, fog: false,
    uniforms: { sunDir: { value: SUN_DIR } },
    vertexShader: `varying vec3 vDir; void main(){ vDir = normalize(position); gl_Position = projectionMatrix * modelViewMatrix * vec4(position,1.0); gl_Position.z = gl_Position.w; }`,
    fragmentShader: /* glsl */`
      uniform vec3 sunDir; varying vec3 vDir;
      float h(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
      float n(vec2 p){ vec2 i = floor(p), f = fract(p); f = f*f*(3.0-2.0*f);
        return mix(mix(h(i), h(i+vec2(1,0)), f.x), mix(h(i+vec2(0,1)), h(i+vec2(1,1)), f.x), f.y); }
      float fbm(vec2 p){ float a = 0.5, s = 0.0; for (int i = 0; i < 6; i++){ s += a * n(p); p = p * 2.03 + 17.1; a *= 0.5; } return s; }
      void main(){
        vec3 d = normalize(vDir);
        float y = max(d.y, 0.0);
        vec3 zenith = vec3(0.16, 0.38, 0.78), horizon = vec3(0.70, 0.82, 0.93);
        vec3 col = mix(horizon, zenith, pow(y, 0.5));
        float sd = max(dot(d, normalize(sunDir)), 0.0);
        col += vec3(1.0, 0.9, 0.7) * (pow(sd, 600.0) * 6.0 + pow(sd, 12.0) * 0.25);
        // Cumulus layer projected onto a plane.
        vec2 uv = d.xz / (d.y + 0.08) * 1.6;
        float base = fbm(uv * 0.9 + vec2(3.0, 1.0));
        float detail = fbm(uv * 3.1 - 5.0);
        float c = smoothstep(0.50, 0.78, base * 0.75 + detail * 0.35);
        c *= smoothstep(0.02, 0.22, d.y);
        float lightSide = clamp(0.55 + 0.6 * (detail - 0.45) + 0.3 * dot(normalize(vec2(d.x, d.z) + 1e-4), normalize(sunDir.xz)), 0.0, 1.0);
        vec3 cloud = mix(vec3(0.62, 0.67, 0.76), vec3(1.0), lightSide);
        col = mix(col, cloud, c * 0.95);
        if (d.y < 0.0) col = mix(horizon, vec3(0.55, 0.62, 0.55), clamp(-d.y * 4.0, 0.0, 1.0));
        gl_FragColor = vec4(col, 1.0);
        #include <tonemapping_fragment>
        #include <colorspace_fragment>
      }`,
  }));
  sky.renderOrder = -1;
  scene.add(sky);
}

// ------------------------------------------------------------------ procedural textures
function canvasTex(size, draw, repeat = 1) {
  const c = document.createElement('canvas'); c.width = c.height = size;
  draw(c.getContext('2d'), size);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace; t.wrapS = t.wrapT = THREE.RepeatWrapping; t.repeat.set(repeat, repeat);
  t.anisotropy = 8;
  return t;
}
const texRng = mulberry32(31);
function speckle(ctx, s, base, cols, n, rmin, rmax, alpha = 1) {
  ctx.fillStyle = base; ctx.fillRect(0, 0, s, s);
  ctx.globalAlpha = alpha;
  for (let i = 0; i < n; i++) {
    ctx.fillStyle = cols[(texRng() * cols.length) | 0];
    const r = rmin + texRng() * (rmax - rmin), x = texRng() * s, y = texRng() * s;
    ctx.beginPath(); ctx.ellipse(x, y, r, r * (0.3 + texRng() * 0.7), texRng() * 3.14, 0, 6.283); ctx.fill();
  }
  ctx.globalAlpha = 1;
}
// Short mowed-lawn texture under the blades: green, never bare dirt.
const lawnTex = canvasTex(512, (ctx, s) => speckle(ctx, s, '#3f8a1c', ['#4fa224', '#3a7d18', '#5cb52b', '#33721a', '#6cc236', '#468f20'], 16000, 0.6, 2.2), SIZE / 1.5);
// Meadow / soil mix outside the lawn.
const meadowTex = canvasTex(512, (ctx, s) => speckle(ctx, s, '#55682c', ['#617a30', '#4a5d26', '#6e7d38', '#5d5a33', '#73883c', '#3f5222'], 14000, 0.6, 2.5), 220);

// Ground: green lawn under the test area, a gravel path around it, rolling meadow beyond.
const C = SIZE / 2;
function rectDist(x, z) { const dx = Math.max(Math.abs(x - C) - C, 0), dz = Math.max(Math.abs(z - C) - C, 0); return Math.hypot(dx, dz); }
function fbm2(x, z) { let a = 0.5, f = 0.012, s = 0; for (let i = 0; i < 4; i++) { s += a * (hash01(Math.floor(x * f), Math.floor(z * f), 9 + i) * 0 + smoothNoise(x * f, z * f, 9 + i)); a *= 0.5; f *= 2.1; } return s; }
function smoothNoise(x, z, seed) { const x0 = Math.floor(x), z0 = Math.floor(z), fx = x - x0, fz = z - z0, ux = fx * fx * (3 - 2 * fx), uz = fz * fz * (3 - 2 * fz);
  const a = hash01(x0, z0, seed), b = hash01(x0 + 1, z0, seed), c = hash01(x0, z0 + 1, seed), d = hash01(x0 + 1, z0 + 1, seed);
  return (a + (b - a) * ux) + ((c + (d - c) * ux) - (a + (b - a) * ux)) * uz; }
function terrainHeight(x, z) { const d = rectDist(x, z); const t = Math.min(1, Math.max(0, (d - 5) / 30)); return t * t * (3 - 2 * t) * (fbm2(x, z) * 10 - 2.5) - 0.03; }
const gravelTex = canvasTex(256, (ctx, s) => speckle(ctx, s, '#9b8f78', ['#b3a891', '#857a65', '#a89a7d', '#c2b9a3', '#6f6555', '#8c7f68'], 7000, 0.6, 2.2), 1);
{
  const geo = new THREE.PlaneGeometry(700, 700, 220, 220); geo.rotateX(-Math.PI / 2); geo.translate(C, 0, C);
  const pos = geo.attributes.position;
  for (let i = 0; i < pos.count; i++) pos.setY(i, terrainHeight(pos.getX(i), pos.getZ(i)));
  geo.computeVertexNormals();
  const meadow = new THREE.Mesh(geo, new THREE.MeshLambertMaterial({ map: meadowTex }));
  meadow.receiveShadow = true;
  const lawn = new THREE.Mesh(new THREE.PlaneGeometry(SIZE + 1.2, SIZE + 1.2), new THREE.MeshLambertMaterial({ map: lawnTex }));
  lawn.rotation.x = -Math.PI / 2; lawn.position.set(C, 0, C); lawn.receiveShadow = true;
  // Gravel path: a ring around the lawn plus one path leading off towards the trees.
  const outer = new THREE.Shape(); const o = 2.6, w = SIZE + 2 * o;
  outer.moveTo(-o, -o); outer.lineTo(SIZE + o, -o); outer.lineTo(SIZE + o, SIZE + o); outer.lineTo(-o, SIZE + o); outer.lineTo(-o, -o);
  const hole = new THREE.Path(); const ii = 0.9;
  hole.moveTo(-ii, -ii); hole.lineTo(-ii, SIZE + ii); hole.lineTo(SIZE + ii, SIZE + ii); hole.lineTo(SIZE + ii, -ii); hole.lineTo(-ii, -ii);
  outer.holes.push(hole);
  const ringGeo = new THREE.ShapeGeometry(outer); ringGeo.rotateX(Math.PI / 2);
  const uvs = ringGeo.attributes.uv; for (let i = 0; i < uvs.count; i++) uvs.setXY(i, uvs.getX(i) / 1.5, uvs.getY(i) / 1.5);
  gravelTex.repeat.set(1, 1);
  const pathMat = new THREE.MeshLambertMaterial({ map: gravelTex, side: THREE.DoubleSide });
  const ring = new THREE.Mesh(ringGeo, pathMat); ring.position.y = 0.006; ring.receiveShadow = true;
  const spurGeo = new THREE.PlaneGeometry(1.7, 40, 1, 20); spurGeo.rotateX(-Math.PI / 2);
  const spur = new THREE.Mesh(spurGeo, pathMat); spur.position.set(C + 5, 0.02, SIZE + o + 20);
  const sp = spurGeo.attributes.position; for (let i = 0; i < sp.count; i++) sp.setY(i, Math.max(0.02, terrainHeight(spur.position.x + sp.getX(i), spur.position.z + sp.getZ(i)) + 0.03));
  spur.position.y = 0; spur.receiveShadow = true;
  const suv = spurGeo.attributes.uv; for (let i = 0; i < suv.count; i++) suv.setXY(i, suv.getX(i) * 1.2, suv.getY(i) * 26);
  scene.add(meadow, lawn, ring, spur);
}

// ------------------------------------------------------------------ trees and bushes (leaf-card foliage, shadow casting)
const leafTex = canvasTex(256, (ctx, s) => {
  ctx.clearRect(0, 0, s, s);
  const greens = ['#2f5d1f', '#3d7026', '#4c8030', '#2a4f1b', '#5d8f35', '#36632a', '#6a9a3a'];
  for (let i = 0; i < 300; i++) {
    const a = texRng() * 6.283, r = Math.sqrt(texRng()) * s * 0.42;
    const x = s / 2 + Math.cos(a) * r, y = s / 2 + Math.sin(a) * r;
    ctx.fillStyle = greens[(texRng() * greens.length) | 0];
    ctx.beginPath(); ctx.ellipse(x, y, 4 + texRng() * 7, 2 + texRng() * 3, texRng() * 3.14, 0, 6.283); ctx.fill();
  }
});
leafTex.wrapS = leafTex.wrapT = THREE.ClampToEdgeWrapping;
const needleTex = canvasTex(256, (ctx, s) => {
  ctx.clearRect(0, 0, s, s);
  const greens = ['#1d3d1a', '#244920', '#2d5526', '#183516', '#335e2a'];
  for (let i = 0; i < 420; i++) {
    const a = texRng() * 6.283, r = Math.sqrt(texRng()) * s * 0.44, x = s / 2 + Math.cos(a) * r, y = s / 2 + Math.sin(a) * r * 0.7;
    ctx.strokeStyle = greens[(texRng() * greens.length) | 0]; ctx.lineWidth = 1.5 + texRng() * 1.5;
    ctx.beginPath(); ctx.moveTo(x, y); ctx.lineTo(x + (texRng() - 0.5) * 14, y + 4 + texRng() * 8); ctx.stroke();
  }
});
needleTex.wrapS = needleTex.wrapT = THREE.ClampToEdgeWrapping;
const barkTex = canvasTex(128, (ctx, s) => { ctx.fillStyle = '#4b3a2a'; ctx.fillRect(0, 0, s, s);
  for (let i = 0; i < 90; i++) { ctx.fillStyle = texRng() > 0.5 ? '#3a2c20' : '#5d4936'; ctx.fillRect(texRng() * s, 0, 1 + texRng() * 3, s); } }, 1);
{
  const rng = mulberry32(8);
  const leafCards = [], needleCards = [];
  const trunkMat = new THREE.MeshLambertMaterial({ map: barkTex });
  const card = (list, p, s, tint) => list.push({ p, r: [rng() * 6.283, rng() * 6.283, rng() * 6.283], s, tint });
  const trunk = (x, y, z, h, r0, r1) => { const t = new THREE.Mesh(new THREE.CylinderGeometry(r0, r1, h, 9), trunkMat); t.position.set(x, y + h / 2, z); t.castShadow = true; scene.add(t); };
  const addBroadleaf = (x, z, s, shape) => {
    const y = terrainHeight(x, z), trunkH = (2.6 + rng() * 2.4) * s;
    trunk(x, y, z, trunkH, 0.15 * s, 0.32 * s);
    for (let b = 0; b < 4; b++) {
      const br = new THREE.Mesh(new THREE.CylinderGeometry(0.05 * s, 0.11 * s, 2.0 * s, 6), trunkMat);
      const a = rng() * 6.283; br.position.set(x + Math.cos(a) * 0.55 * s, y + trunkH * (0.7 + rng() * 0.25), z + Math.sin(a) * 0.55 * s);
      br.rotation.set(Math.sin(a) * 0.9, 0, -Math.cos(a) * 0.9); br.castShadow = true; scene.add(br);
    }
    // shape: 0 round, 1 tall oval, 2 wide spreading
    const sx = shape === 2 ? 1.5 : shape === 1 ? 0.8 : 1.1, sy = shape === 1 ? 1.7 : shape === 2 ? 0.7 : 1.0;
    const clusters = 7 + ((rng() * 6) | 0);
    for (let c = 0; c < clusters; c++) {
      const a = rng() * 6.283, r = rng() * 1.8 * s * sx;
      const cx = x + Math.cos(a) * r, cz = z + Math.sin(a) * r, cy = y + trunkH + (rng() * 2.8 - 0.3) * s * sy;
      const cr = (1.0 + rng() * 0.9) * s;
      for (let k = 0; k < 24; k++) {
        const u = rng() * 6.283, v = Math.acos(2 * rng() - 1), rr = cr * Math.cbrt(rng());
        card(leafCards, [cx + Math.sin(v) * Math.cos(u) * rr, cy + Math.cos(v) * rr * 0.75, cz + Math.sin(v) * Math.sin(u) * rr], (1.1 + rng() * 0.8) * s, 0.72 + rng() * 0.45);
      }
    }
  };
  const addConifer = (x, z, s) => {
    const y = terrainHeight(x, z), h = (7 + rng() * 5) * s;
    trunk(x, y, z, h * 0.9, 0.08 * s, 0.26 * s);
    const layers = 7;
    for (let l = 0; l < layers; l++) {
      const t = l / (layers - 1), ly = y + h * (0.18 + 0.8 * t), lr = (2.3 * (1 - t) + 0.35) * s;
      const n = Math.round(30 * (1 - t) + 8);
      for (let k = 0; k < n; k++) {
        const a = rng() * 6.283, rr = lr * Math.sqrt(rng());
        card(needleCards, [x + Math.cos(a) * rr, ly + (rng() - 0.6) * 0.8 * s, z + Math.sin(a) * rr], (1.0 + rng() * 0.6) * s, 0.75 + rng() * 0.4);
      }
    }
  };
  const addBush = (x, z, s) => {
    const y = terrainHeight(x, z);
    for (let k = 0; k < 22; k++) {
      const u = rng() * 6.283, rr = s * Math.sqrt(rng());
      card(leafCards, [x + Math.cos(u) * rr, y + 0.35 * s + rng() * 0.6 * s, z + Math.sin(u) * rr], (0.8 + rng() * 0.5) * s, 0.7 + rng() * 0.45);
    }
  };
  // Near ring: mixed broadleaf (round / tall / spreading) and conifers, varied sizes.
  for (let i = 0; i < 40; i++) {
    const a = (i / 40) * 6.283 + rng() * 0.12, r = SIZE * 0.72 + 9 + rng() * 16;
    const x = C + Math.cos(a) * r, z = C + Math.sin(a) * r;
    if (rng() < 0.3) addConifer(x, z, 0.8 + rng() * 0.5); else addBroadleaf(x, z, 0.7 + rng() * 0.75, (rng() * 3) | 0);
  }
  // Bushes and saplings along the path.
  for (let i = 0; i < 34; i++) {
    const a = rng() * 6.283, r = SIZE * 0.72 + 3 + rng() * 6;
    addBush(C + Math.cos(a) * r, C + Math.sin(a) * r, 0.8 + rng() * 0.8);
  }
  // Distant tree line on the hills.
  for (let i = 0; i < 90; i++) {
    const a = rng() * 6.283, r = 70 + rng() * 70;
    const x = C + Math.cos(a) * r, z = C + Math.sin(a) * r;
    if (rng() < 0.4) addConifer(x, z, 1.0 + rng() * 0.6); else addBroadleaf(x, z, 1.0 + rng() * 0.8, (rng() * 3) | 0);
  }
  const build = (list, tex) => {
    const inst = new THREE.InstancedMesh(new THREE.PlaneGeometry(1, 1), new THREE.MeshLambertMaterial({ map: tex, alphaTest: 0.45, side: THREE.DoubleSide }), list.length);
    const m = new THREE.Matrix4(), q = new THREE.Quaternion(), e = new THREE.Euler(), col = new THREE.Color();
    list.forEach((c, i) => {
      q.setFromEuler(e.set(...c.r)); m.compose(new THREE.Vector3(...c.p), q, new THREE.Vector3(c.s, c.s, c.s));
      inst.setMatrixAt(i, m); inst.setColorAt(i, col.setRGB(c.tint, c.tint * 1.02, c.tint * 0.9));
    });
    inst.castShadow = true; inst.receiveShadow = true; scene.add(inst);
  };
  build(leafCards, leafTex); build(needleCards, needleTex);
}

// ------------------------------------------------------------------ grass
const cellData = new Float32Array(CELLS * CELLS * 2);
const cellTex = new THREE.DataTexture(cellData, CELLS, CELLS, THREE.RGFormat, THREE.FloatType);
cellTex.minFilter = cellTex.magFilter = THREE.NearestFilter;
function uploadCells() {
  if (!field.consumeDirty()) return;
  for (let i = 0; i < field.units.length; i++) {
    cellData[2 * i] = field.heights[i] * 0.01;
    cellData[2 * i + 1] = field.units[i] > 0 ? -1 : field.cutTime[i];
  }
  cellTex.needsUpdate = true;
}

const G = { // shared uniforms (same names/meaning as InstancedGrass.shader)
  uCells: { value: cellTex }, uCellsX: { value: CELLS }, uTime: { value: 0 },
  uHeightClamp: { value: new THREE.Vector2(level.min * 0.01, level.max * 0.01) },
  uCutter: { value: new THREE.Vector4(0, 0, 0, HEAD_RADIUS) }, // xyz = head centre, w = radius
  uSpin: { value: 0 },
  uCutAnim: { value: CUT_ANIM },
  uStubble: { value: 0.04 },
  uWind: { value: new THREE.Vector4(0.86, 0.5, 0.17, 1.5) }, // xy dir, strength (rad), speed
  uSunDir: { value: SUN_DIR }, uLodWidth: { value: 1 },
};

function bladeGeometry(segments) {
  const pos = [], idx = [];
  for (let s = 0; s < segments; s++) { const t = 1 - Math.pow(1 - s / segments, 1.4); pos.push(0, t, 0, 1, t, 0); }
  pos.push(0.5, 1, 0);
  for (let s = 0; s < segments - 1; s++) { const a = s * 2; idx.push(a, a + 2, a + 1, a + 1, a + 2, a + 3); }
  const last = (segments - 1) * 2; idx.push(last, segments * 2, last + 1);
  const g = new THREE.InstancedBufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3)); // x = side (0/1), y = t
  g.setIndex(idx);
  return g;
}

const BLADE_FN = /* glsl */`
  uniform sampler2D uCells; uniform float uCellsX; uniform float uTime; uniform vec2 uHeightClamp;
  uniform vec4 uCutter; uniform float uSpin; uniform float uCutAnim; uniform float uStubble;
  uniform vec4 uWind; uniform float uLodWidth;
  attribute vec4 blade; // x, z, cell, seed
  varying vec3 vBladeColor; varying float vT; varying float vCut;
  float hh(float p) { p = fract(p * 0.1031); p *= p + 33.33; p *= p + p; return fract(p); }
  float vnoise(vec2 p) { vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    float a = hh(i.x * 7.1 + i.y * 113.3), b = hh(i.x * 7.1 + 7.1 + i.y * 113.3), c = hh(i.x * 7.1 + (i.y + 1.0) * 113.3), d = hh(i.x * 7.1 + 7.1 + (i.y + 1.0) * 113.3);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y); }
  void computeBlade(out vec3 P, out vec3 N) {
    float row = floor((blade.z + 0.5) / uCellsX);
    vec2 cell = texelFetch(uCells, ivec2(int(blade.z - row * uCellsX), int(row)), 0).rg;
    float s = blade.w * 1000.0;
    float r1 = hh(s + 1.7), r2 = hh(s + 11.3), r3 = hh(s + 23.9), r4 = hh(s + 37.1), r5 = hh(s + 51.7), r6 = hh(s + 63.2);
    vec3 root = vec3(blade.x, 0.0, blade.y);
    float t = position.y, side = position.x * 2.0 - 1.0;

    // Full grown height: cell noise x per-blade variety (some short, some tall), kept inside the level range.
    float full = clamp(cell.r * (0.82 + 0.36 * r1), uHeightClamp.x, uHeightClamp.y);
    float cutAge = cell.g < 0.0 ? -1.0 : uTime - cell.g;
    float k = cutAge < 0.0 ? 0.0 : clamp(cutAge / uCutAnim, 0.0, 1.0);
    float ease = 1.0 - pow(1.0 - k, 3.0);
    float stub = uStubble * (0.65 + 0.7 * r5);
    float h = mix(full, stub, ease);
    vCut = ease;

    float tall = smoothstep(0.12, 1.5, full) * (1.0 - ease);
    float facing = r2 * 6.2831853;
    vec2 wdir = vec2(cos(facing), sin(facing));
    vec2 bdir = vec2(-wdir.y, wdir.x);
    // Natural lean: mostly upright so tall grass keeps its height, a few blades flop over.
    float arc = 0.08 + 0.22 * r3 * r3 + 0.12 * tall * r6;
    // Wind: rolling gusts, tall grass sways more.
    float ph = dot(root.xz, uWind.xy) * 0.9 - uTime * uWind.w;
    float gust = 0.55 * sin(ph) + 0.3 * sin(ph * 2.7 + r4 * 6.28) + 0.15 * sin(uTime * 7.0 + s);
    float windArc = uWind.z * (0.3 + 0.9 * tall) * (0.6 + 0.4 * gust);
    // Push away from the cutting head (stronger when spinning, stronger for tall grass); roots stay put.
    vec2 away = root.xz - uCutter.xz; float dist = length(away);
    float infl = (1.0 - smoothstep(uCutter.w * 0.7, uCutter.w * 2.3, dist)) * (1.0 - ease);
    float push = infl * (0.35 + 0.55 * uSpin) * (0.6 + 0.9 * tall);
    vec2 b = bdir * arc + normalize(uWind.xy) * windArc + (dist > 1e-3 ? away / dist : bdir) * push;
    // Kick as the blade is struck, then it settles as stubble.
    b += (dist > 1e-3 ? away / dist : bdir) * sin(k * 3.14159) * 0.9 * step(0.0, cutAge) * step(cutAge, uCutAnim);
    float theta = min(length(b), 1.45);
    vec2 dir = length(b) > 1e-4 ? normalize(b) : bdir;
    float a = theta * t;
    float up = theta < 1e-3 ? h * t : h * sin(a) / theta;
    float along = theta < 1e-3 ? 0.0 : h * (1.0 - cos(a)) / theta;
    // Varied widths; taller levels have thicker blades. Tapers to a point.
    float w = 0.0062 * (0.7 + 0.6 * r4) * (1.0 + 0.45 * (sqrt(max(full, 0.05) / 0.2) - 1.0)) * mix(1.0, 1.35, ease) * uLodWidth;
    w *= (1.0 - pow(t, 1.6) * 0.92);
    vec3 wax = vec3(wdir.x, 0.0, wdir.y);
    P = root + vec3(dir.x * along, up, dir.y * along) + wax * side * w;
    vec3 tng = vec3(dir.x * sin(a), cos(a), dir.y * sin(a));
    N = normalize(mix(normalize(cross(wax, tng) + wax * side * 0.5), vec3(0.0, 1.0, 0.0), 0.3));
    vT = t;

    // Colour: yellow-green, olive, dark green, fresh green, with dry golden patches.
    float dryPatch = vnoise(root.xz * 0.35 + 3.1);
    // Mostly yellow-green and golden-green, with fresh and darker greens mixed through.
    vec3 c0 = vec3(0.55, 0.66, 0.18), c1 = vec3(0.66, 0.62, 0.22), c2 = vec3(0.16, 0.32, 0.08), c3 = vec3(0.33, 0.55, 0.14), dry = vec3(0.74, 0.64, 0.30);
    float pick = r6;
    vec3 tip = pick < 0.35 ? c0 : pick < 0.6 ? c1 : pick < 0.78 ? c2 : c3;
    tip = mix(tip, dry, smoothstep(0.66, 0.9, dryPatch) * (0.3 + 0.4 * r5) + 0.08 * step(0.95, r1));
    vec3 rootC = tip * vec3(0.35, 0.42, 0.3);
    vec3 grown = mix(rootC, tip, smoothstep(0.0, 0.85, t)) * (0.85 + 0.3 * r2);
    // Fresh cut lawn: short, vibrant green with paler cut ends.
    vec3 stubC = mix(vec3(0.16, 0.50, 0.05), vec3(0.42, 0.86, 0.17), smoothstep(0.3, 1.0, t)) * (0.85 + 0.3 * r1);
    vBladeColor = mix(grown, stubC, ease);
  }
`;

function grassMaterial(lodWidth) {
  const mat = new THREE.MeshLambertMaterial({ side: THREE.DoubleSide });
  mat.onBeforeCompile = (sh) => {
    Object.assign(sh.uniforms, G, { uLodWidth: { value: lodWidth } });
    sh.vertexShader = BLADE_FN + sh.vertexShader
      .replace('#include <beginnormal_vertex>', 'vec3 bladeP; vec3 bladeN; computeBlade(bladeP, bladeN); vec3 objectNormal = bladeN;')
      .replace('#include <begin_vertex>', 'vec3 transformed = bladeP;');
    sh.fragmentShader = 'uniform vec3 uSunDir; varying vec3 vBladeColor; varying float vT; varying float vCut;\n' + sh.fragmentShader
      .replace('#include <color_fragment>', 'diffuseColor.rgb *= vBladeColor * mix(0.55, 1.0, smoothstep(0.0, 0.7, vT));')
      .replace('#include <emissivemap_fragment>', `
        // Sun shining through blades (back-lit translucency).
        vec3 vdir = normalize(vViewPosition);
        vec3 ldir = normalize((viewMatrix * vec4(uSunDir, 0.0)).xyz);
        float tr = pow(clamp(dot(vdir, ldir), 0.0, 1.0), 4.0) * vT * (1.0 - vCut);
        totalEmissiveRadiance += vBladeColor * vec3(1.0, 0.95, 0.6) * tr * 0.9;`);
  };
  mat.customProgramCacheKey = () => 'grass' + lodWidth;
  return mat;
}
function grassDepthMaterial() {
  const mat = new THREE.MeshDepthMaterial({ depthPacking: THREE.RGBADepthPacking, side: THREE.DoubleSide });
  mat.onBeforeCompile = (sh) => {
    Object.assign(sh.uniforms, G, { uLodWidth: { value: 1 } });
    sh.vertexShader = BLADE_FN + sh.vertexShader.replace('#include <begin_vertex>', 'vec3 bladeP; vec3 bladeN; computeBlade(bladeP, bladeN); vec3 transformed = bladeP;');
  };
  mat.customProgramCacheKey = () => 'grassDepth';
  return mat;
}

const bladeGeo = bladeGeometry(+(Q.get('segments') || 6));
const CHUNK_M = 2; // chunk edge in metres
const LOD = { full: +(Q.get('lodFull') || 7), mid: +(Q.get('lodMid') || 15), midFrac: 0.45, farFrac: 0.2, shadowDist: +(Q.get('shadowDist') || 7) };
const matFull = grassMaterial(1), matMid = grassMaterial(1 / Math.sqrt(LOD.midFrac)), matFar = grassMaterial(1 / Math.sqrt(LOD.farFrac));
const depthMat = grassDepthMaterial();
const chunks = [];
{
  const rng = mulberry32(777), cs = SIZE / CELLS;
  for (let z0 = 0; z0 < SIZE; z0 += CHUNK_M) for (let x0 = 0; x0 < SIZE; x0 += CHUNK_M) {
    const x1 = Math.min(SIZE, x0 + CHUNK_M), z1 = Math.min(SIZE, z0 + CHUNK_M);
    const n = Math.round((x1 - x0) * (z1 - z0) * BLADES_M2), arr = new Float32Array(n * 4);
    for (let i = 0; i < n; i++) {
      const px = x0 + rng() * (x1 - x0), pz = z0 + rng() * (z1 - z0);
      const cx = Math.min(CELLS - 1, Math.floor(px / cs)), cz = Math.min(CELLS - 1, Math.floor(pz / cs));
      arr[i * 4] = px; arr[i * 4 + 1] = pz; arr[i * 4 + 2] = cz * CELLS + cx; arr[i * 4 + 3] = rng();
    }
    for (let i = n - 1; i > 0; i--) { const j = Math.floor(rng() * (i + 1)); for (let c = 0; c < 4; c++) { const t = arr[i * 4 + c]; arr[i * 4 + c] = arr[j * 4 + c]; arr[j * 4 + c] = t; } }
    const g = new THREE.InstancedBufferGeometry();
    g.index = bladeGeo.index; g.setAttribute('position', bladeGeo.attributes.position);
    g.setAttribute('blade', new THREE.InstancedBufferAttribute(arr, 4));
    const centre = new THREE.Vector3((x0 + x1) / 2, 0.6, (z0 + z1) / 2);
    g.boundingSphere = new THREE.Sphere(centre, Math.hypot(x1 - x0, z1 - z0) / 2 + 1.8);
    const mesh = new THREE.Mesh(g, matFull);
    mesh.customDepthMaterial = depthMat; mesh.receiveShadow = true;
    scene.add(mesh);
    chunks.push({ g, n, centre, mesh });
  }
}
function updateLod() {
  const dens = level.density;
  for (const c of chunks) {
    const d = Math.max(0, camera.position.distanceTo(c.centre) - 1.4);
    const f = d < LOD.full ? 1 : d < LOD.mid ? LOD.midFrac : LOD.farFrac;
    c.g.instanceCount = Math.max(1, Math.ceil(c.n * dens * f));
    c.mesh.material = f === 1 ? matFull : f === LOD.midFrac ? matMid : matFar;
    c.mesh.castShadow = d < LOD.shadowDist;
  }
}

// ------------------------------------------------------------------ clippings (pooled instanced slivers)
const CLIP_N = 700;
const clipGeo = new THREE.PlaneGeometry(0.011, 0.045 + 0.035 * Math.min(1, level.max / 100));
const clipMesh = new THREE.InstancedMesh(clipGeo, new THREE.MeshLambertMaterial({ side: THREE.DoubleSide }), CLIP_N);
clipMesh.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
clipMesh.frustumCulled = false;
scene.add(clipMesh);
const clips = Array.from({ length: CLIP_N }, () => ({ life: 0, p: new THREE.Vector3(), v: new THREE.Vector3(), r: new THREE.Euler(), w: new THREE.Vector3(), s: 1 }));
{ const col = new THREE.Color(); const cr = mulberry32(4); const pal = ['#5d9a2c', '#7ab53a', '#a7c24a', '#c9b45a', '#3f7a22'];
  for (let i = 0; i < CLIP_N; i++) clipMesh.setColorAt(i, col.set(pal[(cr() * pal.length) | 0])); }
let clipNext = 0;
const clipRng = mulberry32(99);
function emitClippings(n, head, headVel, spinDir) {
  for (let k = 0; k < n; k++) {
    const c = clips[clipNext]; clipNext = (clipNext + 1) % CLIP_N;
    const phi = clipRng() * 6.283, rr = HEAD_RADIUS * (0.6 + 0.4 * clipRng());
    c.p.set(head.x + Math.cos(phi) * rr, 0.06 + clipRng() * Math.min(0.5, level.max * 0.004), head.z + Math.sin(phi) * rr);
    const sp = 2.2 + clipRng() * 3.2;
    c.v.set(-Math.sin(phi) * spinDir * sp + headVel.x + Math.cos(phi) * 0.8, 1.2 + clipRng() * 2.6, Math.cos(phi) * spinDir * sp + headVel.z + Math.sin(phi) * 0.8);
    c.r.set(clipRng() * 6.28, clipRng() * 6.28, clipRng() * 6.28);
    c.w.set((clipRng() - 0.5) * 30, (clipRng() - 0.5) * 30, (clipRng() - 0.5) * 30);
    c.life = 0.55 + clipRng() * 0.6; c.s = 0.7 + clipRng() * 0.7;
  }
}
const _m = new THREE.Matrix4(), _q = new THREE.Quaternion(), _sv = new THREE.Vector3();
function updateClippings(dt) {
  for (let i = 0; i < CLIP_N; i++) {
    const c = clips[i];
    if (c.life > 0) {
      c.life -= dt;
      c.v.y -= 9.8 * dt; c.v.multiplyScalar(1 - 2.2 * dt);
      c.p.addScaledVector(c.v, dt);
      if (c.p.y < 0.02) { c.p.y = 0.02; c.v.set(0, 0, 0); c.w.multiplyScalar(0.8); }
      c.r.x += c.w.x * dt; c.r.y += c.w.y * dt; c.r.z += c.w.z * dt;
    }
    const s = c.life > 0 ? c.s * Math.min(1, c.life / 0.2) : 0;
    _m.compose(c.p, _q.setFromEuler(c.r), _sv.set(s, s, s));
    clipMesh.setMatrixAt(i, _m);
  }
  clipMesh.instanceMatrix.needsUpdate = true;
}

// ------------------------------------------------------------------ first-person rig + floating trimmer (no hands, no body)
const player = { x: SIZE / 2, z: -1.75, yaw: 0, pitch: -0.5, vx: 0, vz: 0, eye: 1.65, walk: 0 };
const trimmer = { on: false, rpm: 0, load: 0, sweep: 0, sweepPhase: 0, yawLag: 0 };
const rig = new THREE.Group();          // follows player position + (lagged) yaw
const bodyPivot = new THREE.Group();    // pivot at the head's ground contact point
const headGroup = new THREE.Group();    // horizontal spinning head
rig.add(bodyPivot); scene.add(rig); scene.add(headGroup);
let cutterHead = null, trimmerReady = false;
const blurDisc = new THREE.Mesh(new THREE.CircleGeometry(HEAD_RADIUS * 1.02, 48), new THREE.MeshBasicMaterial({
  map: canvasTex(256, (ctx, s) => {
    // Soft motion-blur ring: brightest where the blade tips travel, smooth around the circle.
    const img = ctx.createImageData(s, s);
    for (let y = 0; y < s; y++) for (let x = 0; x < s; x++) {
      const dx = (x + 0.5) / s * 2 - 1, dy = (y + 0.5) / s * 2 - 1, r = Math.hypot(dx, dy), th = Math.atan2(dy, dx);
      const ring = Math.exp(-Math.pow((r - 0.8) / 0.13, 2)) * (r < 1 ? 1 : 0) * (r > 0.45 ? 1 : 0);
      const a = ring * (0.55 + 0.45 * Math.cos(3 * th)) * 0.9;
      const i = (y * s + x) * 4; img.data[i] = 215; img.data[i + 1] = 220; img.data[i + 2] = 228; img.data[i + 3] = a * 255;
    }
    ctx.putImageData(img, 0, 0);
  }), transparent: true, depthWrite: false, opacity: 0 }));
blurDisc.rotation.x = -Math.PI / 2; blurDisc.position.y = 0.06;
headGroup.add(blurDisc);
new GLTFLoader().load('./Trimmer.glb', (g) => {
  // Long-shaft trimmer: stretch the shaft (model Z) so it reaches from the head up to the bottom-right corner.
  const model = g.scene; model.scale.set(TRIMMER_SCALE, TRIMMER_SCALE, TRIMMER_SCALE * SHAFT_STRETCH);
  model.traverse((o) => { if (o.isMesh) { o.castShadow = true; o.receiveShadow = true; } if (o.name === 'CutterHead') cutterHead = o; });
  bodyPivot.add(model);
  if (cutterHead) {
    // Detach the head so it stays level with the ground while the shaft is angled up to the player.
    cutterHead.parent.remove(cutterHead);
    cutterHead.position.set(0, 0, 0); cutterHead.quaternion.identity();
    const holder = new THREE.Group(); holder.rotation.y = Math.PI / 2; holder.scale.setScalar(TRIMMER_SCALE);
    holder.add(cutterHead); headGroup.add(holder);
  }
  trimmerReady = true;
}, undefined, (e) => { console.error('Trimmer.glb failed', e); trimmerReady = true; });

// Viewmodel placement (player space: x right, y up, z forward).
const VM = { headX: +(Q.get('vmX') || 0.05), headZ: +(Q.get('vmZ') || 1.4), headY: 0.02, pitch: +(Q.get('vmPitch') || 17) * Math.PI / 180, yaw: +(Q.get('vmYaw') || -34) * Math.PI / 180 };
const headWorld = new THREE.Vector3(), prevHead = new THREE.Vector3(), headVel = new THREE.Vector3();
let headInit = false;

function updatePlayerAndTrimmer(dt, input) {
  // Movement: slower in thick grass ("resistance").
  const resist = 1 - 0.35 * trimmer.load;
  const speed = input.speed * resist;
  player.yaw += input.turn * dt; player.pitch = input.pitch ?? player.pitch;
  const fx = Math.sin(player.yaw), fz = Math.cos(player.yaw);
  player.x += fx * speed * dt; player.z += fz * speed * dt;
  player.walk += speed * dt;

  // Blade spin-up/down, frame-rate independent.
  const targetRpm = trimmer.on ? 1 : 0;
  trimmer.rpm += (targetRpm - trimmer.rpm) * (1 - Math.exp(-(trimmer.on ? 2.6 : 1.6) * dt));
  G.uSpin.value = trimmer.rpm;

  // Operator sweep: the head arcs left/right in front while cutting.
  trimmer.sweepPhase += dt * input.sweepHz * Math.PI * 2;
  const sweepTarget = input.sweep * Math.sin(trimmer.sweepPhase);
  trimmer.sweep += (sweepTarget - trimmer.sweep) * (1 - Math.exp(-8 * dt));
  // Rig yaw lags slightly behind the camera when turning.
  let dy = player.yaw - trimmer.yawLag; dy = Math.atan2(Math.sin(dy), Math.cos(dy));
  trimmer.yawLag += dy * (1 - Math.exp(-7 * dt));

  // Camera with gentle walk bob.
  const bob = Math.sin(player.walk * 5.2) * 0.022 * Math.min(1, speed);
  camera.position.set(player.x, player.eye + bob, player.z);
  camera.rotation.set(0, 0, 0); camera.rotateY(player.yaw + Math.PI); camera.rotateX(player.pitch);

  // Rig: position at player, yaw lagged, + sweep rotation about the player.
  rig.position.set(player.x, 0, player.z);
  rig.rotation.set(0, trimmer.yawLag + trimmer.sweep, 0);
  // Vibration: idle hum when spinning, stronger in thick grass. Small, no camera shake.
  const vib = (0.0025 * trimmer.rpm + 0.006 * trimmer.load) ;
  const jx = (Math.sin(clock * 91.0) + Math.sin(clock * 57.3)) * vib, jy = Math.sin(clock * 73.1) * vib;
  const sway = Math.sin(player.walk * 2.6) * 0.03 * Math.min(1, speed) + Math.sin(clock * 1.3) * 0.006;
  // Pull back a touch when the blades bite into tall grass.
  const pull = -0.06 * trimmer.load;
  // Rig space is right-handed with +Z forward, so the player's right is -X.
  bodyPivot.position.set(-(VM.headX + sway + jx), VM.headY + jy, VM.headZ + pull);
  bodyPivot.rotation.set(0, 0, 0); bodyPivot.rotateY(-VM.yaw); bodyPivot.rotateX(VM.pitch);

  bodyPivot.updateWorldMatrix(true, false);
  headWorld.setFromMatrixPosition(bodyPivot.matrixWorld);
  headWorld.y = Math.max(0.02, headWorld.y);
  headGroup.position.copy(headWorld);
  headGroup.rotation.set(jy * 2, rig.rotation.y, jx * 2);
  if (!headInit) { prevHead.copy(headWorld); headInit = true; }
  headVel.copy(headWorld).sub(prevHead).divideScalar(Math.max(dt, 1e-4));

  // Spin: real head rotates fast; at high rpm the blur disc takes over so it reads as a spinning blade.
  if (cutterHead) cutterHead.rotation.y += dt * trimmer.rpm * 38;
  blurDisc.material.opacity = Math.max(0, (trimmer.rpm - 0.3) / 0.7) * 0.3;
  blurDisc.rotation.z += dt * trimmer.rpm * 60;
  G.uCutter.value.set(headWorld.x, headWorld.y, headWorld.z, HEAD_RADIUS);

  // Cutting: only spinning blades cut; swept capsule from last frame's head position = exact counting.
  let cut = 0;
  if (trimmer.rpm > 0.55) cut = field.cutCapsule(prevHead.x, prevHead.z, headWorld.x, headWorld.z, HEAD_RADIUS);
  const tallness = Math.min(1, level.max / 150);
  const loadTarget = cut > 0 ? Math.min(1, (cut / (dt * 40000)) * (0.4 + 0.8 * tallness)) : 0;
  trimmer.load += (loadTarget - trimmer.load) * (1 - Math.exp(-6 * dt));
  if (cut > 0) emitClippings(Math.min(60, Math.ceil(cut / 30 * (0.5 + tallness))), headWorld, headVel, 1);
  prevHead.copy(headWorld);

  sun.position.set(player.x + SUN_DIR.x * 60, SUN_DIR.y * 60, player.z + SUN_DIR.z * 60);
  sun.target.position.set(player.x + Math.sin(player.yaw) * 6, 0, player.z + Math.cos(player.yaw) * 6);
}

// ------------------------------------------------------------------ HUD
const $ = (id) => document.getElementById(id);
const hud = $('hud');
hud.style.transform = `scale(${innerWidth / 1920}, ${innerHeight / 1080})`;
$('lvl').textContent = `LEVEL ${LEVEL + 1}`;
$('hgt').textContent = `${level.min}-${level.max} cm grass`;
let shownLeft = -1;
function refreshHud() {
  // Count down smoothly towards the exact value (never lags more than a few frames behind).
  if (shownLeft < 0 || field.remaining > shownLeft) shownLeft = field.remaining;
  else shownLeft = Math.max(field.remaining, Math.floor(shownLeft - Math.max(1, (shownLeft - field.remaining) * 0.35)));
  $('left').textContent = shownLeft.toLocaleString('en-US');
  $('fill').style.width = `${(100 * shownLeft / field.initial).toFixed(2)}%`;
}
const caption = (t) => { $('caption').textContent = t || ''; };

// ------------------------------------------------------------------ scripted demo / interactive input
let clock = 0;
const DT = 1 / FPS;
const plan = Q.get('plan') || 'demo';
const keys = {};
let mouseDX = 0, mouseDY = 0;
function scriptedInput(t) {
  // 0-1.6 s: trimmer off, looking at the grass. 1.6-2.8 s: spin up. 2.8-9.5 s: walk forward cutting with
  // sweeps. 9.5-13 s: stop and turn around to look at the cut strip and its border.
  const inp = { speed: 0, turn: 0, sweep: 0, sweepHz: 0.55, pitch: -0.5 };
  trimmer.on = t > 1.6;
  if (t < 1.6) caption('Trimmer off');
  else if (t < 2.8) caption('Blades spinning up');
  else if (t < 9.5) caption('Cutting');
  else caption('Fresh cut lawn behind');
  if (t >= 2.8 && t < 9.5) { inp.speed = 0.95; inp.sweep = 0.42; }
  if (t >= 9.5 && t < 12.0) { inp.turn = (Math.PI * Math.PI / 5) * Math.sin(Math.min(1, (t - 9.5) / 2.5) * Math.PI); inp.pitch = -0.5 - 0.1 * Math.sin(Math.min(1, (t - 9.5) / 2.5) * Math.PI); }
  if (t >= 12.0) inp.pitch = -0.5;
  return inp;
}
function interactiveInput() {
  const inp = { speed: 0, turn: 0, sweep: keys.ShiftLeft ? 0 : 0.35, sweepHz: 0.6 };
  if (keys.KeyW) inp.speed += 1.6; if (keys.KeyS) inp.speed -= 1.0;
  player.yaw -= mouseDX * 0.0025; player.pitch = Math.max(-1.2, Math.min(0.6, player.pitch - mouseDY * 0.0025));
  mouseDX = mouseDY = 0;
  if (keys.KeyA) player.yaw += 1.8 * DT; if (keys.KeyD) player.yaw -= 1.8 * DT;
  inp.pitch = player.pitch;
  return inp;
}
const DEMO_LEN = +(Q.get('len') || 13.5);
window.frame = (render = true) => {
  clock += DT;
  field.clock = clock; G.uTime.value = clock;
  const inp = plan === 'interactive' ? interactiveInput() : scriptedInput(clock);
  updatePlayerAndTrimmer(DT, inp);
  updateClippings(DT);
  uploadCells();
  updateLod();
  refreshHud();
  $('state').textContent = trimmer.rpm > 0.05 ? `TRIMMER ${Math.round(trimmer.rpm * 100)}%` : 'TRIMMER OFF';
  if (Q.get('debugCam') === 'side') { camera.position.set(player.x - 4.5, 1.2, player.z + 1.0); camera.lookAt(player.x, 0.6, player.z + 1.0); }
  if (Q.get('debugCam') === 'top') { camera.position.set(player.x, 6, player.z + 1.0); camera.lookAt(player.x, 0, player.z + 1.01); }
  if (render) renderer.render(scene, camera);
  window.stats = { remaining: field.remaining, cut: field.cutByMower, load: trimmer.load, rpm: trimmer.rpm };
  return plan !== 'interactive' && clock >= DEMO_LEN;
};

if (plan === 'interactive') {
  addEventListener('keydown', (e) => { keys[e.code] = true; if (e.code === 'Space') trimmer.on = !trimmer.on; });
  addEventListener('keyup', (e) => { keys[e.code] = false; });
  canvas.addEventListener('click', () => canvas.requestPointerLock());
  addEventListener('mousemove', (e) => { if (document.pointerLockElement) { mouseDX += e.movementX; mouseDY += e.movementY; } });
  trimmer.on = true;
}
const waitReady = () => {
  if (!trimmerReady) return setTimeout(waitReady, 50);
  uploadCells();
  window.previewReady = true;
  if (!Q.has('record')) { const loop = () => { window.frame(); requestAnimationFrame(loop); }; requestAnimationFrame(loop); }
};
waitReady();
