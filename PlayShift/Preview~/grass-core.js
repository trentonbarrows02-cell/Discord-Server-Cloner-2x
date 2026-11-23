// JS port of the engine-free C# core (Assets/PlayShift/Grass/Core) so the browser preview behaves like
// the Unity module: same unit bookkeeping, height noise, swept cutting, gift operations and progression.

export const DEFAULT_LEVELS = [
  { name: 'Level 1', min: 15, max: 25, units: 1_000_000 },
  { name: 'Level 2', min: 30, max: 45, units: 1_000_000 },
  { name: 'Level 3', min: 50, max: 70, units: 1_000_000 },
  { name: 'Level 4', min: 80, max: 110, units: 1_000_000 },
  { name: 'Level 5', min: 120, max: 150, units: 1_000_000 },
];

export function hash01(x, z, seed) {
  let h = (Math.imul(x | 0, 0x8DA6B343) ^ Math.imul(z | 0, 0xD8163841) ^ Math.imul(seed | 0, 0xCB1AB31F)) >>> 0;
  h ^= h >>> 16; h = Math.imul(h, 0x7FEB352D) >>> 0;
  h ^= h >>> 15; h = Math.imul(h, 0x846CA68B) >>> 0;
  h ^= h >>> 16;
  return (h >>> 8) / 16777216;
}

function valueNoise(x, z, seed) {
  const x0 = Math.floor(x), z0 = Math.floor(z);
  const fx = x - x0, fz = z - z0;
  const ux = fx * fx * (3 - 2 * fx), uz = fz * fz * (3 - 2 * fz);
  const a = hash01(x0, z0, seed), b = hash01(x0 + 1, z0, seed);
  const c = hash01(x0, z0 + 1, seed), d = hash01(x0 + 1, z0 + 1, seed);
  const ab = a + (b - a) * ux, cd = c + (d - c) * ux;
  return ab + (cd - ab) * uz;
}

export function mulberry32(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6D2B79F5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const PATCH_R = 64;
const PATCH = (() => {
  const l = [];
  for (let z = -PATCH_R; z <= PATCH_R; z++)
    for (let x = -PATCH_R; x <= PATCH_R; x++) {
      const d = x * x + z * z;
      if (d <= PATCH_R * PATCH_R) l.push([x, z, d]);
    }
  l.sort((p, q) => p[2] - q[2]);
  return l;
})();

export class GrassField {
  constructor(layout) {
    this.layout = { cellsX: 250, cellsZ: 250, sizeX: 50, sizeZ: 50, absMaxCm: 220, maxMul: 4, noiseCells: 14, ...layout };
    const n = this.layout.cellsX * this.layout.cellsZ;
    this.units = new Int32Array(n);
    this.heights = new Float32Array(n);
    this.perm = new Int32Array(n);
    this.level = null;
    this.onCleared = null;
    this.dirtyMin = Infinity; this.dirtyMax = -1;
  }
  get cellCount() { return this.units.length; }
  get csx() { return this.layout.sizeX / this.layout.cellsX; }
  get csz() { return this.layout.sizeZ / this.layout.cellsZ; }
  get curMin() { return Math.min(this.level.min + this.bonus, this.layout.absMaxCm); }
  get curMax() { return Math.min(this.level.max + this.bonus, this.layout.absMaxCm); }

  reset(level, seed) {
    this.level = level; this.seed = seed; this.rng = mulberry32(seed);
    const n = this.units.length;
    for (let i = 0; i < n; i++) this.perm[i] = i;
    for (let i = n - 1; i > 0; i--) { const j = Math.floor(this.rng() * (i + 1)); const t = this.perm[i]; this.perm[i] = this.perm[j]; this.perm[j] = t; }
    const base = Math.floor(level.units / n), rem = level.units % n;
    this.base = Math.max(1, base); this.maxPerCell = Math.max(this.base + 1, this.base * this.layout.maxMul);
    this.units.fill(base);
    for (let k = 0; k < rem; k++) this.units[this.perm[k]] += 1;
    this.bonus = 0;
    for (let i = 0; i < n; i++) this.heights[i] = this.sample(i % this.layout.cellsX, (i / this.layout.cellsX) | 0);
    this.initial = level.units; this.remaining = level.units;
    this.cutByMower = 0; this.removedByGifts = 0; this.addedByGifts = 0; this.cleared = false;
    this.markDirty(0, n - 1);
  }

  sample(cx, cz) {
    const s = Math.max(1, this.layout.noiseCells);
    const patch = valueNoise(cx / s, cz / s, this.seed);
    const detail = valueNoise(cx / (s * 0.3) + 17.3, cz / (s * 0.3) - 4.1, this.seed + 1);
    const white = hash01(cx, cz, this.seed + 2);
    const t = Math.min(1, Math.max(0, patch * 0.55 + detail * 0.25 + white * 0.2));
    return this.curMin + (this.curMax - this.curMin) * t;
  }

  cutCapsule(ax, az, bx, bz, r) {
    if (this.cleared || !this.level) return 0;
    const { cellsX, cellsZ } = this.layout, csx = this.csx, csz = this.csz;
    const cl = (v, hi) => Math.max(0, Math.min(hi, v));
    const minX = cl(Math.floor((Math.min(ax, bx) - r) / csx), cellsX - 1), maxX = cl(Math.floor((Math.max(ax, bx) + r) / csx), cellsX - 1);
    const minZ = cl(Math.floor((Math.min(az, bz) - r) / csz), cellsZ - 1), maxZ = cl(Math.floor((Math.max(az, bz) + r) / csz), cellsZ - 1);
    if (Math.max(ax, bx) + r < 0 || Math.min(ax, bx) - r > this.layout.sizeX) return 0;
    if (Math.max(az, bz) + r < 0 || Math.min(az, bz) - r > this.layout.sizeZ) return 0;
    const dx = bx - ax, dz = bz - az, len = dx * dx + dz * dz, r2 = r * r;
    let removed = 0, first = Infinity, last = -1;
    for (let cz = minZ; cz <= maxZ; cz++) {
      const pz = (cz + 0.5) * csz;
      for (let cx = minX; cx <= maxX; cx++) {
        const i = cz * cellsX + cx, u = this.units[i];
        if (!u) continue;
        const px = (cx + 0.5) * csx;
        let t = 0;
        if (len > 1e-12) t = Math.min(1, Math.max(0, ((px - ax) * dx + (pz - az) * dz) / len));
        const qx = ax + dx * t - px, qz = az + dz * t - pz;
        if (qx * qx + qz * qz > r2) continue;
        this.units[i] = 0; removed += u;
        if (i < first) first = i; if (i > last) last = i;
      }
    }
    if (!removed) return 0;
    this.cutByMower += removed;
    this.markDirty(first, last);
    this.applyRemoval(removed);
    return removed;
  }

  findRandom(cut) {
    const n = this.perm.length, off = Math.floor(this.rng() * n);
    for (let k = 0; k < n; k++) { const c = this.perm[(off + k) % n]; if ((this.units[c] === 0) === cut) return c; }
    return -1;
  }
  visitPatch(centre, budget, f) {
    const { cellsX, cellsZ } = this.layout, ccx = centre % cellsX, ccz = (centre / cellsX) | 0;
    for (let k = 0; k < PATCH.length && budget > 0; k++) {
      const cx = ccx + PATCH[k][0], cz = ccz + PATCH[k][1];
      if (cx < 0 || cz < 0 || cx >= cellsX || cz >= cellsZ) continue;
      budget -= f(cz * cellsX + cx, budget);
    }
    return budget;
  }
  visitAll(budget, f) {
    const n = this.perm.length, off = Math.floor(this.rng() * n);
    for (let k = 0; k < n && budget > 0; k++) budget -= f(this.perm[(off + k) % n], budget);
    return budget;
  }

  addGrass(amount) {
    if (this.cleared || amount <= 0) return 0;
    let left = amount;
    const regrow = (c, b) => {
      if (this.units[c]) return 0;
      const a = Math.min(b, this.base); this.units[c] = a;
      this.heights[c] = this.sample(c % this.layout.cellsX, (c / this.layout.cellsX) | 0); return a;
    };
    const centre = this.findRandom(true);
    if (centre >= 0) { left = this.visitPatch(centre, left, regrow); left = this.visitAll(left, regrow); }
    while (left > 0) {
      const before = left;
      left = this.visitAll(left, (c, b) => {
        const room = this.maxPerCell - this.units[c]; if (room <= 0) return 0;
        const a = Math.min(b, room, this.base); this.units[c] += a; return a;
      });
      if (left === before) break;
    }
    const added = amount - left;
    this.addedByGifts += added; this.remaining += added;
    this.markDirty(0, this.units.length - 1);
    return added;
  }

  removeGrass(amount) {
    if (this.cleared || amount <= 0) return 0;
    amount = Math.min(amount, this.remaining);
    let left = amount;
    const take = (c, b) => { const u = this.units[c]; if (!u) return 0; const t = Math.min(b, u); this.units[c] = u - t; return t; };
    const centre = this.findRandom(false);
    if (centre >= 0) { left = this.visitPatch(centre, left, take); left = this.visitAll(left, take); }
    const removed = amount - left;
    if (!removed) return 0;
    this.removedByGifts += removed;
    this.markDirty(0, this.units.length - 1);
    this.applyRemoval(removed);
    return removed;
  }

  increaseHeight(cm) {
    if (this.cleared || cm <= 0) return 0;
    const applied = Math.min(cm, Math.max(0, this.layout.absMaxCm - (this.level.max + this.bonus)));
    if (applied <= 0) return 0;
    this.bonus += applied;
    for (let i = 0; i < this.heights.length; i++) this.heights[i] = Math.min(this.heights[i] + applied, this.layout.absMaxCm);
    this.markDirty(0, this.units.length - 1);
    return applied;
  }

  applyRemoval(n) {
    this.remaining = Math.max(0, this.remaining - n);
    if (this.remaining === 0 && !this.cleared) { this.cleared = true; this.onCleared && this.onCleared(); }
  }
  markDirty(a, b) { this.dirtyMin = Math.min(this.dirtyMin, a); this.dirtyMax = Math.max(this.dirtyMax, b); }
  consumeDirty() {
    if (this.dirtyMax < this.dirtyMin) return null;
    const r = [this.dirtyMin, this.dirtyMax]; this.dirtyMin = Infinity; this.dirtyMax = -1; return r;
  }
}

export class GrassProgression {
  constructor(levels, layout, seed = 1234) {
    this.levels = levels; this.seed = seed; this.field = new GrassField(layout);
    this.state = 'NotStarted'; this.index = -1; this.elapsed = 0; this.results = [];
    this.onStarted = null; this.onCleared = null; this.onAllCompleted = null;
    this.field.onCleared = () => this.handleCleared();
  }
  get level() { return this.levels[this.index]; }
  get isFinal() { return this.index === this.levels.length - 1; }
  get playing() { return this.state === 'Playing'; }
  startLevel(i) {
    if (i === 0) this.results = [];
    this.index = i; this.elapsed = 0; this.state = 'Playing';
    this.field.reset(this.levels[i], this.seed + i * 7919);
    this.onStarted && this.onStarted(i);
  }
  nextLevel() { if (this.state !== 'LevelCleared') return false; this.startLevel(this.index + 1); return true; }
  tick(dt) { if (this.playing) this.elapsed += dt; }
  handleCleared() {
    const f = this.field;
    const r = { levelNumber: this.index + 1, name: this.level.name, seconds: this.elapsed, total: f.cutByMower + f.removedByGifts,
      mower: f.cutByMower, giftRemoved: f.removedByGifts, giftAdded: f.addedByGifts, final: this.isFinal };
    this.results.push(r);
    this.state = r.final ? 'AllLevelsCompleted' : 'LevelCleared';
    this.onCleared && this.onCleared(r);
    if (r.final && this.onAllCompleted) this.onAllCompleted(this.results);
  }
}
