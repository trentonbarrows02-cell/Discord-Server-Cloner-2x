// Browser preview of the PlayShift grass module, used to record progress videos without Unity.
// Grass logic comes from grass-core.js (a port of the C# core); the blade shader below is a GLSL port of
// Assets/PlayShift/Grass/Shaders/InstancedGrass.shader with the same bending/wind/width maths.
import * as THREE from './three.module.js';
import { GrassProgression, DEFAULT_LEVELS, mulberry32 } from './grass-core.js';

const Q = new URLSearchParams(location.search);
const CELLS = +(Q.get('cells') || 200);
const SIZE = +(Q.get('size') || 40);
const BPC = +(Q.get('bpc') || 8);
const FPS = 30;
const CUT_RADIUS = 0.6;
const MOWER_SPEED = 5; // m/s

// ------------------------------------------------------------------ logic
const prog = new GrassProgression(DEFAULT_LEVELS, { cellsX: CELLS, cellsZ: CELLS, sizeX: SIZE, sizeZ: SIZE });
const field = prog.field;

// ------------------------------------------------------------------ renderer / scene
const canvas = document.getElementById('scene');
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
renderer.setPixelRatio(1);
renderer.setSize(innerWidth, innerHeight, false);
renderer.outputColorSpace = THREE.SRGBColorSpace;

const SKY = new THREE.Color('#a9cfee');
const scene = new THREE.Scene();
scene.background = SKY;
scene.fog = new THREE.Fog(SKY, 45, 120);
const camera = new THREE.PerspectiveCamera(55, innerWidth / innerHeight, 0.05, 400);

const sunDir = new THREE.Vector3(0.45, 0.8, 0.35).normalize();
const sun = new THREE.DirectionalLight(0xfff3e0, 2.2);
sun.position.copy(sunDir).multiplyScalar(50);
scene.add(sun, new THREE.HemisphereLight(0xcfe6ff, 0x4a3a20, 0.9));

// Ground: soil under the lawn, rough meadow around it, a low hedge border.
const ground = new THREE.Mesh(new THREE.PlaneGeometry(400, 400), new THREE.MeshLambertMaterial({ color: 0x5d7a3a }));
ground.rotation.x = -Math.PI / 2; ground.position.set(SIZE / 2, -0.02, SIZE / 2);
const soil = new THREE.Mesh(new THREE.PlaneGeometry(SIZE, SIZE), new THREE.MeshLambertMaterial({ color: 0x3f5a22 }));
soil.rotation.x = -Math.PI / 2; soil.position.set(SIZE / 2, -0.005, SIZE / 2);
scene.add(ground, soil);
const hedgeMat = new THREE.MeshLambertMaterial({ color: 0x2f5a22 });
for (const [x, z, w, d] of [[SIZE / 2, -3.5, SIZE + 9, 1.2], [SIZE / 2, SIZE + 3.5, SIZE + 9, 1.2], [-3.5, SIZE / 2, 1.2, SIZE + 8], [SIZE + 3.5, SIZE / 2, 1.2, SIZE + 8]]) {
  const h = new THREE.Mesh(new THREE.BoxGeometry(w, 1.1, d), hedgeMat);
  h.position.set(x, 0.55, z); scene.add(h);
}
const treeRng = mulberry32(5);
for (let i = 0; i < 26; i++) {
  const a = treeRng() * Math.PI * 2, r = SIZE * 0.85 + treeRng() * 30;
  const t = new THREE.Group();
  const trunk = new THREE.Mesh(new THREE.CylinderGeometry(0.25, 0.35, 3, 8), new THREE.MeshLambertMaterial({ color: 0x5a4027 }));
  trunk.position.y = 1.5;
  const crown = new THREE.Mesh(new THREE.SphereGeometry(2.2 + treeRng() * 1.5, 12, 10), new THREE.MeshLambertMaterial({ color: 0x2e6b2a }));
  crown.position.y = 4.2;
  t.add(trunk, crown); t.position.set(SIZE / 2 + Math.cos(a) * r, 0, SIZE / 2 + Math.sin(a) * r); scene.add(t);
}

// ------------------------------------------------------------------ grass (GPU instanced, one draw call)
function bladeGeometry(segments) {
  const pos = [], uv = [], idx = [];
  for (let s = 0; s < segments; s++) {
    const t = 1 - Math.pow(1 - s / segments, 1.35);
    pos.push(-0.5, t, 0, 0.5, t, 0); uv.push(0, t, 1, t);
  }
  pos.push(0, 1, 0); uv.push(0.5, 1);
  for (let s = 0; s < segments - 1; s++) { const a = s * 2; idx.push(a, a + 2, a + 1, a + 1, a + 2, a + 3); }
  const last = (segments - 1) * 2; idx.push(last, segments * 2, last + 1);
  const g = new THREE.InstancedBufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
  g.setIndex(idx);
  return g;
}

const geo = bladeGeometry(6);
// Chunked like GrassFieldRenderer: blades grouped per chunk and shuffled inside it, so drawing the first
// N instances of a chunk is a uniform subsample (distance LOD), and each chunk is frustum culled.
const CHUNK = +(Q.get('chunk') || 20);
const chunks = [];
{
  const rng = mulberry32(777); const cs = SIZE / CELLS;
  for (let kz = 0; kz < CELLS; kz += CHUNK) for (let kx = 0; kx < CELLS; kx += CHUNK) {
    const x1 = Math.min(CELLS, kx + CHUNK), z1 = Math.min(CELLS, kz + CHUNK);
    const n = (x1 - kx) * (z1 - kz) * BPC, arr = new Float32Array(n * 4); let w = 0;
    for (let cz = kz; cz < z1; cz++) for (let cx = kx; cx < x1; cx++) for (let b = 0; b < BPC; b++) {
      arr[w++] = (cx + rng()) * cs; arr[w++] = (cz + rng()) * cs; arr[w++] = cz * CELLS + cx; arr[w++] = rng();
    }
    for (let i = n - 1; i > 0; i--) { const j = Math.floor(rng() * (i + 1)); for (let c = 0; c < 4; c++) { const t = arr[i * 4 + c]; arr[i * 4 + c] = arr[j * 4 + c]; arr[j * 4 + c] = t; } }
    const g = new THREE.InstancedBufferGeometry();
    g.index = geo.index; g.setAttribute('position', geo.attributes.position); g.setAttribute('uv', geo.attributes.uv);
    g.setAttribute('blade', new THREE.InstancedBufferAttribute(arr, 4));
    g.instanceCount = n;
    const centre = new THREE.Vector3((kx + x1) / 2 * cs, 0.5, (kz + z1) / 2 * cs);
    g.boundingSphere = new THREE.Sphere(centre, Math.hypot((x1 - kx) * cs, (z1 - kz) * cs) / 2 + 2.5);
    chunks.push({ g, n, centre });
  }
}
const LOD = { full: +(Q.get('lodFull') || 14), mid: +(Q.get('lodMid') || 30), midFrac: 0.45, farFrac: 0.18 };
function updateLod() {
  for (const c of chunks) {
    const d = Math.max(0, camera.position.distanceTo(c.centre) - c.g.boundingSphere.radius * 0.7);
    const f = d < LOD.full ? 1 : d < LOD.mid ? LOD.midFrac : LOD.farFrac;
    c.g.instanceCount = Math.max(1, Math.ceil(c.n * f));
    c.mesh.material = f === 1 ? grassMat : f === LOD.midFrac ? grassMatMid : grassMatFar;
  }
}

const cellData = new Float32Array(CELLS * CELLS);
const cellTex = new THREE.DataTexture(cellData, CELLS, CELLS, THREE.RedFormat, THREE.FloatType);
cellTex.minFilter = cellTex.magFilter = THREE.NearestFilter;
cellTex.needsUpdate = true;

const lin = (hex) => new THREE.Color(hex); // THREE.Color converts sRGB hex to linear working space
const grassMat = new THREE.ShaderMaterial({
  side: THREE.DoubleSide,
  uniforms: {
    cellTex: { value: cellTex }, cellsX: { value: CELLS }, heightClamp: { value: new THREE.Vector2(0.15, 0.25) }, time: { value: 0 },
    baseColor: { value: lin('#1a3d0d') }, tipColor: { value: lin('#75ad33') }, dryColor: { value: lin('#9e9e47') }, stubbleColor: { value: lin('#86a83f') },
    dryAmount: { value: 0.25 }, rootAO: { value: 0.35 }, translucency: { value: 0.6 },
    bladeWidth: { value: +(Q.get('width') || 0.024) }, widthGrowth: { value: 0.45 }, heightJitter: { value: 0.12 },
    bendShort: { value: 0.25 }, bendTall: { value: 0.95 }, maxArc: { value: 1.35 }, stubbleHeight: { value: 0.035 },
    windDir: { value: new THREE.Vector2(1, 0.35).normalize() }, windStrength: { value: 0.35 }, windSpeed: { value: 1.6 }, windFreq: { value: 0.18 },
    lightDir: { value: sunDir }, lightColor: { value: new THREE.Vector3(1.0, 0.95, 0.86) }, ambient: { value: new THREE.Vector3(0.16, 0.2, 0.26) },
    fogColor: { value: new THREE.Vector3(SKY.r, SKY.g, SKY.b) }, fogNear: { value: 45 }, fogFar: { value: 120 },
  },
  vertexShader: /* glsl */`
    uniform sampler2D cellTex; uniform float cellsX; uniform vec2 heightClamp; uniform float time;
    uniform vec3 baseColor, tipColor, dryColor, stubbleColor; uniform float dryAmount;
    uniform float bladeWidth, widthGrowth, heightJitter, bendShort, bendTall, maxArc, stubbleHeight;
    uniform vec2 windDir; uniform float windStrength, windSpeed, windFreq;
    attribute vec4 blade;
    varying vec3 vNormal; varying vec3 vColor; varying vec3 vWorld; varying float vT;
    float hash11(float p) { p = fract(p * 0.1031); p *= p + 33.33; p *= p + p; return fract(p); }
    void main() {
      float row = floor((blade.z + 0.5) / cellsX);
      float cellH = texelFetch(cellTex, ivec2(int(blade.z - row * cellsX), int(row)), 0).r;
      float seed = blade.w * 1000.0;
      float r1 = hash11(seed + 1.7), r2 = hash11(seed + 11.3), r3 = hash11(seed + 23.9), r4 = hash11(seed + 37.1);
      bool cut = cellH <= 0.0;
      float h = cut ? stubbleHeight * (0.6 + 0.8 * r1) : clamp(cellH * (1.0 + (r1 * 2.0 - 1.0) * heightJitter), heightClamp.x, heightClamp.y);
      float t = uv.y; float side = uv.x * 2.0 - 1.0;
      vec3 rootWS = vec3(blade.x, 0.0, blade.y);
      float facing = r2 * 6.2831853;
      vec2 widthDir = vec2(cos(facing), sin(facing));
      vec2 bendDir = vec2(-widthDir.y, widthDir.x);
      float tall = clamp((h - 0.15) / 1.35, 0.0, 1.0);
      float arc = mix(bendShort, bendTall, tall * tall * (3.0 - 2.0 * tall)) * (0.55 + 0.9 * r3);
      float phase = dot(rootWS.xz, windDir) * windFreq * 6.2831853 - time * windSpeed;
      float gust = 0.6 * sin(phase) + 0.4 * sin(phase * 2.3 + r4 * 6.2831853);
      float windArc = windStrength * (0.25 + 0.75 * tall) * (0.65 + 0.35 * gust) * (cut ? 0.1 : 1.0);
      vec2 b = bendDir * arc + windDir * windArc;
      float theta = length(b);
      vec2 dir = theta > 1e-4 ? b / theta : bendDir;
      theta = min(theta, maxArc);
      float a = theta * t; float up, along;
      if (theta < 1e-3) { up = h * t; along = 0.5 * h * theta * t * t; }
      else { up = h * sin(a) / theta; along = h * (1.0 - cos(a)) / theta; }
      float width = bladeWidth * (0.7 + 0.6 * r4) * mix(1.0, sqrt(max(h, 0.02) / 0.2), widthGrowth) * (1.0 - 0.75 * t * t);
      vec3 widthAxis = vec3(widthDir.x, 0.0, widthDir.y);
      vec3 posWS = rootWS + vec3(dir.x * along, up, dir.y * along) + widthAxis * (side * 0.5 * width);
      vec3 tangent = vec3(dir.x * sin(a), cos(a), dir.y * sin(a));
      vec3 n = normalize(cross(widthAxis, tangent));
      vNormal = normalize(n + widthAxis * side * 0.4);
      vWorld = posWS; vT = t;
      vec3 grown = mix(baseColor, tipColor, t);
      grown = mix(grown, dryColor * mix(0.6, 1.0, t), dryAmount * r3 * r3);
      grown *= 0.85 + 0.3 * r2;
      vec3 stub = stubbleColor * (0.8 + 0.3 * r1) * mix(0.75, 1.0, t);
      vColor = cut ? stub : grown;
      gl_Position = projectionMatrix * viewMatrix * vec4(posWS, 1.0);
    }`,
  fragmentShader: /* glsl */`
    uniform vec3 lightDir, lightColor, ambient, fogColor; uniform float translucency, rootAO, fogNear, fogFar;
    varying vec3 vNormal; varying vec3 vColor; varying vec3 vWorld; varying float vT;
    void main() {
      vec3 n = normalize(vNormal) * (gl_FrontFacing ? 1.0 : -1.0);
      vec3 l = normalize(lightDir);
      float wrap = clamp((dot(n, l) + 0.5) / 1.5, 0.0, 1.0);
      vec3 v = normalize(cameraPosition - vWorld);
      float back = pow(clamp(dot(v, -l), 0.0, 1.0), 3.0) * translucency * vT;
      float ao = mix(1.0 - rootAO, 1.0, vT);
      vec3 lit = vColor * (ambient + lightColor * (wrap + back)) * ao;
      lit = pow(lit, vec3(1.0 / 2.2));
      float f = smoothstep(fogNear, fogFar, distance(cameraPosition, vWorld));
      gl_FragColor = vec4(mix(lit, fogColor, f), 1.0);
    }`,
});
// Fewer blades far away are drawn wider so ground coverage stays the same (as _LodWidthScale in Unity).
const grassMatMid = grassMat.clone(); grassMatMid.uniforms = { ...grassMat.uniforms, bladeWidth: { value: grassMat.uniforms.bladeWidth.value / Math.sqrt(LOD.midFrac) } };
const grassMatFar = grassMat.clone(); grassMatFar.uniforms = { ...grassMat.uniforms, bladeWidth: { value: Math.min(2.5, 1 / Math.sqrt(LOD.farFrac)) * grassMat.uniforms.bladeWidth.value } };
for (const c of chunks) { c.mesh = new THREE.Mesh(c.g, grassMat); scene.add(c.mesh); }

function uploadCells() {
  if (!field.consumeDirty()) return;
  for (let i = 0; i < cellData.length; i++) cellData[i] = field.units[i] > 0 ? field.heights[i] * 0.01 : 0;
  cellTex.needsUpdate = true;
  grassMat.uniforms.heightClamp.value.set(field.curMin * 0.01, field.curMax * 0.01);
}

// ------------------------------------------------------------------ placeholder trimmer (replace with the real model)
const mower = new THREE.Group();
{
  const red = new THREE.MeshLambertMaterial({ color: 0xd0342c }), dark = new THREE.MeshLambertMaterial({ color: 0x222222 });
  const grey = new THREE.MeshLambertMaterial({ color: 0x9a9a9a });
  const deck = new THREE.Mesh(new THREE.BoxGeometry(1.2, 0.28, 1.25), red); deck.position.y = 0.3;
  const hood = new THREE.Mesh(new THREE.BoxGeometry(0.9, 0.35, 0.55), red); hood.position.set(0, 0.6, 0.25);
  const seat = new THREE.Mesh(new THREE.BoxGeometry(0.6, 0.12, 0.45), dark); seat.position.set(0, 0.62, -0.35);
  const back = new THREE.Mesh(new THREE.BoxGeometry(0.6, 0.45, 0.1), dark); back.position.set(0, 0.85, -0.55);
  const wheel = new THREE.CylinderGeometry(0.22, 0.22, 0.16, 16); wheel.rotateZ(Math.PI / 2);
  for (const [x, z, s] of [[-0.62, 0.45, 0.8], [0.62, 0.45, 0.8], [-0.62, -0.45, 1.2], [0.62, -0.45, 1.2]]) {
    const w = new THREE.Mesh(wheel, dark); w.scale.set(1, s, s); w.position.set(x, 0.22 * s, z); mower.add(w);
  }
  const wheelPost = new THREE.Mesh(new THREE.CylinderGeometry(0.04, 0.04, 0.4), grey); wheelPost.position.set(0, 0.85, 0.15); wheelPost.rotation.x = -0.5;
  const steer = new THREE.Mesh(new THREE.TorusGeometry(0.16, 0.03, 8, 20), dark); steer.position.set(0, 1.02, 0.05); steer.rotation.x = -1.1;
  mower.add(deck, hood, seat, back, wheelPost, steer);
}
scene.add(mower);
const mowerState = { x: 0.5, z: 0.5, heading: 0, path: [], pathIndex: 0 };

// Clippings spray behind the deck.
const CLIP_N = 600;
const clipGeo = new THREE.BufferGeometry();
const clipPos = new Float32Array(CLIP_N * 3), clipVel = new Float32Array(CLIP_N * 3), clipLife = new Float32Array(CLIP_N);
clipGeo.setAttribute('position', new THREE.BufferAttribute(clipPos, 3));
const clips = new THREE.Points(clipGeo, new THREE.PointsMaterial({ color: 0x7fb53c, size: 0.06 }));
clips.frustumCulled = false; scene.add(clips);
let clipNext = 0;
const fxRng = mulberry32(99);
function emitClippings(n) {
  const fx = Math.sin(mowerState.heading), fz = Math.cos(mowerState.heading);
  for (let k = 0; k < n; k++) {
    const i = clipNext; clipNext = (clipNext + 1) % CLIP_N;
    const side = fxRng() > 0.5 ? 1 : -1;
    clipPos[i * 3] = mowerState.x + fz * 0.65 * side; clipPos[i * 3 + 1] = 0.2; clipPos[i * 3 + 2] = mowerState.z - fx * 0.65 * side;
    clipVel[i * 3] = fz * side * (1 + fxRng() * 2) - fx * fxRng(); clipVel[i * 3 + 1] = 1 + fxRng() * 2; clipVel[i * 3 + 2] = -fx * side * (1 + fxRng() * 2) - fz * fxRng();
    clipLife[i] = 0.6 + fxRng() * 0.5;
  }
}
function updateClippings(dt) {
  for (let i = 0; i < CLIP_N; i++) {
    if (clipLife[i] <= 0) { clipPos[i * 3 + 1] = -10; continue; }
    clipLife[i] -= dt; clipVel[i * 3 + 1] -= 9 * dt;
    clipPos[i * 3] += clipVel[i * 3] * dt; clipPos[i * 3 + 1] = Math.max(0.02, clipPos[i * 3 + 1] + clipVel[i * 3 + 1] * dt); clipPos[i * 3 + 2] += clipVel[i * 3 + 2] * dt;
  }
  clipGeo.attributes.position.needsUpdate = true;
}

// ------------------------------------------------------------------ autopilot (lanes, then mop up leftovers)
function buildLanes() {
  const pts = []; let forward = true;
  for (let z = 0.5; z < SIZE + CUT_RADIUS; z += 1.1) {
    if (forward) pts.push([-0.8, z], [SIZE + 0.8, z]); else pts.push([SIZE + 0.8, z], [-0.8, z]);
    forward = !forward;
  }
  return pts;
}
function nearestUncut() {
  let best = -1, bd = Infinity; const cs = SIZE / CELLS;
  for (let i = 0; i < field.units.length; i++) {
    if (!field.units[i]) continue;
    const x = (i % CELLS + 0.5) * cs, z = (((i / CELLS) | 0) + 0.5) * cs;
    const d = (x - mowerState.x) ** 2 + (z - mowerState.z) ** 2;
    if (d < bd) { bd = d; best = i; }
  }
  if (best < 0) return null;
  return [(best % CELLS + 0.5) * cs, (((best / CELLS) | 0) + 0.5) * cs];
}
function pathLengthLeft() {
  let len = 0, px = mowerState.x, pz = mowerState.z;
  for (let k = mowerState.pathIndex; k < mowerState.path.length; k++) { const [x, z] = mowerState.path[k]; len += Math.hypot(x - px, z - pz); px = x; pz = z; }
  return len;
}
function simulate(dt) {
  // Sub-step so no step moves more than 0.25 m: cutting stays exact at any time-lapse speed.
  let left = dt;
  while (left > 1e-6 && prog.playing) {
    const sub = Math.min(left, 0.25 / MOWER_SPEED);
    left -= sub;
    let target = mowerState.path[mowerState.pathIndex];
    if (!target) { target = nearestUncut(); if (!target) break; mowerState.path.push(target); }
    const dx = target[0] - mowerState.x, dz = target[1] - mowerState.z, dist = Math.hypot(dx, dz);
    const step = MOWER_SPEED * sub;
    const px = mowerState.x, pz = mowerState.z;
    if (dist <= step) { mowerState.x = target[0]; mowerState.z = target[1]; mowerState.pathIndex++; }
    else { mowerState.x += dx / dist * step; mowerState.z += dz / dist * step; }
    if (dist > 1e-4) {
      const want = Math.atan2(dx, dz);
      let d = want - mowerState.heading; d = Math.atan2(Math.sin(d), Math.cos(d));
      mowerState.heading += d * Math.min(1, sub * 8);
    }
    prog.tick(sub);
    const cut = field.cutCapsule(px, pz, mowerState.x, mowerState.z, CUT_RADIUS);
    if (cut > 0) emitClippings(Math.min(3, 1 + (cut >> 6)));
  }
}

// ------------------------------------------------------------------ HUD
const $ = (id) => document.getElementById(id);
const hud = $('hud');
function layoutHud() { hud.style.transform = `scale(${innerWidth / 1920}, ${innerHeight / 1080})`; }
layoutHud();
const fmtTime = (s) => { const tt = Math.floor(s * 10), t = tt % 10, sec = Math.floor(tt / 10), m = Math.floor(sec / 60) % 60, h = Math.floor(sec / 3600); return h ? `${h}:${String(m).padStart(2, '0')}:${String(sec % 60).padStart(2, '0')}.${t}` : `${String(m).padStart(2, '0')}:${String(sec % 60).padStart(2, '0')}.${t}`; };
const fmtN = (n) => n.toLocaleString('en-US');
const range = (a, b) => `${+a.toFixed(1)}-${+b.toFixed(1)} cm`;
let toasts = [];
let punch = 0, punchColor = '#fff';

function refreshHud() {
  $('left').textContent = fmtN(field.remaining);
  const scale = Math.max(field.initial, field.initial + field.addedByGifts);
  $('fill').style.width = `${(100 * field.remaining / scale).toFixed(2)}%`;
  $('tmr').textContent = 'TIME  ' + fmtTime(prog.elapsed);
  $('hgt').textContent = 'Grass ' + range(field.curMin, field.curMax);
  $('counter').style.transform = `scale(${1 + 0.08 * punch})`;
  $('left').style.color = punch > 0 ? mixColor('#ffffff', punchColor, punch) : '#fff';
  const feed = $('feed');
  feed.replaceChildren(...toasts.map((t) => {
    const d = document.createElement('div');
    d.className = 'toast'; d.textContent = t.text; d.style.color = t.color; d.style.opacity = Math.min(1, t.life / 0.6);
    return d;
  }));
}
function mixColor(a, b, t) {
  const pa = [1, 3, 5].map((i) => parseInt(a.substr(i, 2), 16)), pb = [1, 3, 5].map((i) => parseInt(b.substr(i, 2), 16));
  return `rgb(${pa.map((v, i) => Math.round(v + (pb[i] - v) * t)).join(',')})`;
}
function gift(user, giftName, kind, amount, count = 1) {
  let applied = 0, text = '', color = '#8cff73';
  const what = count > 1 ? `${giftName} x${count}` : giftName;
  if (kind === 'add') { applied = prog.playing ? field.addGrass(amount * count) : 0; text = `@${user} sent ${what}: +${fmtN(applied)} grass`; }
  if (kind === 'remove') { applied = prog.playing ? field.removeGrass(amount * count) : 0; text = `@${user} sent ${what}: -${fmtN(applied)} grass`; color = '#ff8c73'; }
  if (kind === 'taller') { applied = prog.playing ? field.increaseHeight(amount * count) : 0; text = `@${user} sent ${what}: grass +${applied} cm taller`; color = '#8cccff'; }
  if (!applied) return;
  toasts.push({ text, color, life: 4 }); if (toasts.length > 6) toasts.shift();
  punch = 1; punchColor = color.length === 7 ? color : '#ffffff';
}

// Confetti on a 1920x1080 canvas inside the HUD.
const confetti = $('confetti').getContext('2d');
const PAL = ['#ffd633', '#59e659', '#4db3ff', '#ff668c', '#ffffff', '#bf73ff'];
let pieces = [], rain = 0, rainAcc = 0;
const cRng = mulberry32(2024);
function burst(n, fromTop) {
  for (let i = 0; i < n; i++) {
    const p = fromTop
      ? { x: cRng() * 1920, y: -20, vx: (cRng() - 0.5) * 120, vy: 120 + cRng() * 140 }
      : (() => { const a = (35 + cRng() * 110) * Math.PI / 180, s = 700 + cRng() * 800; return { x: 960 + (cRng() - 0.5) * 576, y: 620, vx: Math.cos(a) * s, vy: -Math.sin(a) * s }; })();
    Object.assign(p, { w: 10 + cRng() * 10, h: 18 + cRng() * 16, rot: cRng() * 6.28, spin: (cRng() - 0.5) * 18, life: 3.5 + cRng() * 2, c: PAL[(cRng() * PAL.length) | 0], i: pieces.length });
    pieces.push(p);
  }
}
function updateConfetti(dt, now) {
  if (rain > 0) { rainAcc += rain * dt; while (rainAcc >= 1) { rainAcc--; burst(1, true); } }
  confetti.clearRect(0, 0, 1920, 1080);
  pieces = pieces.filter((p) => (p.life -= dt) > 0 && p.y < 1140);
  for (const p of pieces) {
    p.vy += 900 * dt; p.vx *= 1 - 1.6 * dt; p.vy *= 1 - 1.6 * dt; p.vx += Math.sin((now + p.i) * 4) * 40 * dt;
    p.x += p.vx * dt; p.y += p.vy * dt; p.rot += p.spin * dt;
    confetti.save(); confetti.translate(p.x, p.y); confetti.rotate(p.rot); confetti.scale(1, Math.abs(Math.cos(p.rot * 0.7)) + 0.15);
    confetti.fillStyle = p.c; confetti.fillRect(-p.w / 2, -p.h / 2, p.w, p.h); confetti.restore();
  }
}

let overlayShownAt = -1, overlayEl = null;
function showOverlay(el, burstN, rainRate) {
  hideOverlays(); overlayEl = el; el.style.display = 'block'; overlayShownAt = clock; burst(burstN, false); rain = rainRate;
}
function hideOverlays() {
  for (const id of ['cleared', 'victory']) $(id).style.display = 'none';
  overlayEl = null; pieces = []; rain = 0;
  $('c-next').style.filter = ''; $('c-next').style.transform = '';
}
function animateOverlay() {
  if (!overlayEl) return;
  const t = clock - overlayShownAt, p = Math.min(1, t / 0.45), c1 = 1.70158, c3 = c1 + 1;
  const ease = 1 + c3 * Math.pow(p - 1, 3) + c1 * Math.pow(p - 1, 2);
  const card = overlayEl.querySelector('.card');
  card.style.transform = `translate(-50%, -50%) scale(${0.6 + 0.4 * ease})`;
  card.querySelector('.title').style.transform = `scale(${1 + 0.04 * Math.sin(t * 5)})`;
}

prog.onStarted = (i) => {
  hideOverlays();
  $('lvl').textContent = `LEVEL ${i + 1} / ${prog.levels.length}`;
  mowerState.x = 0.5; mowerState.z = -0.6; mowerState.heading = Math.PI / 2;
  mowerState.path = buildLanes(); mowerState.pathIndex = 0;
  toasts = [];
  uploadCells();
};
prog.onCleared = (r) => {
  if (r.final) return;
  $('c-sub').textContent = `${r.name.toUpperCase()} COMPLETE`;
  $('c-time').textContent = fmtTime(r.seconds);
  $('c-cut').textContent = fmtN(r.total);
  $('c-detail').textContent = r.giftRemoved || r.giftAdded ? `Mower ${fmtN(r.mower)}   |   Gifts removed ${fmtN(r.giftRemoved)}   |   Gifts added ${fmtN(r.giftAdded)}` : 'Mowed 100% by you';
  const next = prog.levels[prog.index + 1];
  $('c-hint').textContent = `Next: ${next.name}  -  ${range(next.min, next.max)} grass`;
  showOverlay($('cleared'), 140, 18);
};
prog.onAllCompleted = (results) => {
  $('v-title').textContent = `ALL ${results.length} LEVELS COMPLETED!`;
  let total = 0, cut = 0;
  $('v-table').innerHTML = results.map((r) => { total += r.seconds; cut += r.total; return `<tr><td>${r.levelNumber}</td><td>${r.name}</td><td class="time">${fmtTime(r.seconds)}</td><td>${fmtN(r.total)} cut</td></tr>`; }).join('');
  $('v-totals').innerHTML = `TOTAL TIME <span style="color:var(--gold)">${fmtTime(total)}</span> &nbsp;&nbsp;&nbsp; TOTAL GRASS CUT <span style="color:var(--grass)">${fmtN(cut)}</span>`;
  showOverlay($('victory'), 260, 45);
};

// ------------------------------------------------------------------ camera
const camPos = new THREE.Vector3(), camLook = new THREE.Vector3();
let camMode = 'chase', camInit = false;
function cameraTarget() {
  const grassH = field.level ? field.curMax * 0.01 : 0.2;
  const fx = Math.sin(mowerState.heading), fz = Math.cos(mowerState.heading);
  if (camMode === 'overview') {
    return [new THREE.Vector3(SIZE / 2 - 4, 34, -16), new THREE.Vector3(SIZE / 2, 0, SIZE * 0.5)];
  }
  if (camMode === 'low') {
    const p = new THREE.Vector3(mowerState.x - fx * 6 + fz * 4, 0.6 + grassH * 1.1, mowerState.z - fz * 6 - fx * 4);
    return [p, new THREE.Vector3(mowerState.x + fx * 2, grassH * 0.4, mowerState.z + fz * 2)];
  }
  const back = 4.5 + grassH * 2, up = 2.4 + grassH * 1.6;
  return [new THREE.Vector3(mowerState.x - fx * back, up, mowerState.z - fz * back), new THREE.Vector3(mowerState.x + fx * 3, 0.3, mowerState.z + fz * 3)];
}
function updateCamera(dt, snap) {
  const [p, l] = cameraTarget();
  const k = snap || !camInit ? 1 : 1 - Math.exp(-3 * dt);
  camPos.lerp(p, k); camLook.lerp(l, k); camInit = true;
  camera.position.copy(camPos); camera.lookAt(camLook);
}

// ------------------------------------------------------------------ director (scripted video)
let clock = 0;
const segments = [];
const seg = (dur, opts = {}) => segments.push({ dur, ...opts });
function captionSet(text) { const c = $('caption'); c.style.display = text ? 'block' : 'none'; c.textContent = text || ''; }
function badge(text) { const b = $('badge'); b.style.display = text ? 'block' : 'none'; b.textContent = text || ''; }

function levelSegments(i, { intro = 3, gifts = [], timelapse = 5, clearedDur = 3, cam = 'chase' }) {
  seg(intro, { start: () => { if (prog.index !== i) prog.startLevel(i); camMode = cam; badge(''); captionSet(`${DEFAULT_LEVELS[i].name}: ${range(DEFAULT_LEVELS[i].min, DEFAULT_LEVELS[i].max)} grass`); }, speed: 1, snapCam: true, fadeIn: true, events: gifts });
  seg(timelapse, {
    start: (s) => { camMode = 'overview'; captionSet(''); s.speed = Math.max(4, (pathLengthLeft() / MOWER_SPEED) / (s.dur * 0.92)); badge(`TIME-LAPSE x${Math.round(s.speed)}`); },
    untilCleared: true,
  });
  if (i < DEFAULT_LEVELS.length - 1)
    seg(clearedDur, { start: () => badge(''), press: clearedDur - 0.9, end: () => prog.nextLevel(), fadeOut: true });
  else
    seg(clearedDur, { start: () => badge('') });
}
const levelPlan = (Q.get('plan') || 'full');
if (levelPlan === 'shot') {
  const L = +(Q.get('level') || 1) - 1;
  levelSegments(L, { intro: +(Q.get('dur') || 2), cam: Q.get('cam') || 'chase', timelapse: 1, clearedDur: 1 });
}
if (levelPlan === 'full') {
  levelSegments(0, { intro: 6, gifts: [[1.6, () => gift('mowfan22', 'TikTok', 'remove', 5000)], [3.6, () => gift('lawnqueen', 'Rose', 'add', 2500, 4)]], timelapse: 6, clearedDur: 3.6 });
  levelSegments(1, { intro: 3, timelapse: 3.5, clearedDur: 2.6, cam: 'low' });
  levelSegments(2, { intro: 3.5, gifts: [[1.4, () => gift('gardenguy', 'Galaxy', 'taller', 20)]], timelapse: 3.5, clearedDur: 2.6, cam: 'low' });
  levelSegments(3, { intro: 3, timelapse: 3.5, clearedDur: 2.6, cam: 'low' });
  levelSegments(4, { intro: 4.5, gifts: [[2.0, () => gift('bigtipper', 'Ice Cream Cone', 'remove', 15000)]], timelapse: 4.5, clearedDur: 6 });
}

let segIndex = -1, segT = 0, current = null;
function nextSegment() {
  segIndex++; current = segments[segIndex]; segT = 0;
  if (!current) return;
  current.fired = new Set();
  current.start && current.start(current);
  if (current.snapCam) updateCamera(0, true);
}

const DT = 1 / FPS;
window.frame = () => {
  if (segIndex < 0) nextSegment();
  if (!current) return true;
  clock += DT; segT += DT;
  const s = current;
  if (s.events) s.events.forEach(([t, f], k) => { if (segT >= t && !s.fired.has(k)) { s.fired.add(k); f(); } });
  const speed = s.speed || 0;
  if (speed > 0 || s.untilCleared) simulate(DT * (s.speed || 1));
  updateClippings(DT);
  uploadCells();
  grassMat.uniforms.time.value = clock;
  mower.position.set(mowerState.x, 0, mowerState.z); mower.rotation.y = mowerState.heading;
  updateCamera(DT, false);
  updateLod();
  toasts.forEach((t) => (t.life -= DT)); toasts = toasts.filter((t) => t.life > 0);
  punch = Math.max(0, punch - DT * 2.5);
  refreshHud();
  animateOverlay();
  updateConfetti(DT, clock);
  if (s.press !== undefined && segT >= s.press) { const b = $('c-next'); const k = Math.min(1, (segT - s.press) / 0.15); b.style.transform = `scale(${1 - 0.06 * Math.sin(k * Math.PI)})`; b.style.filter = 'brightness(1.25)'; }
  let fade = 0;
  if (s.fadeIn) fade = Math.max(fade, 1 - segT / 0.35);
  if (s.fadeOut) fade = Math.max(fade, 1 - (s.dur - segT) / 0.35);
  $('fade').style.opacity = Math.max(0, Math.min(1, fade));
  renderer.render(scene, camera);
  const done = s.untilCleared ? (!prog.playing && segT >= 0.3) || segT > s.dur * 4 : segT >= s.dur;
  if (done) { s.end && s.end(); nextSegment(); }
  return !current;
};

// Interactive mode (no ?record): run in real time.
if (!Q.has('record')) {
  const loop = () => { if (!window.frame()) requestAnimationFrame(loop); };
  requestAnimationFrame(loop);
}
window.previewReady = true;
