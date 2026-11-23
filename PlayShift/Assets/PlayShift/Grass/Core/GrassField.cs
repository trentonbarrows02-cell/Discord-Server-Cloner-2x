using System;

namespace PlayShift.Grass
{
    /// <summary>
    /// The logical lawn. Grass is counted in "units" (GRASS LEFT) that are spread over a fixed grid of cells,
    /// completely independent of how many blades the renderer draws. A level's 1,000,000 units live in
    /// e.g. 1000 x 1000 cells of 5 cm (1 unit each on a 50 m lawn); no per-unit or per-blade objects exist anywhere.
    ///
    /// Coordinates are field-local metres: X in [0, SizeX], Z in [0, SizeZ].
    /// </summary>
    public sealed class GrassField
    {
        private const int PatchRadiusCells = 64;

        private readonly GrassFieldLayout _layout;
        private readonly int[] _units;
        private readonly float[] _heightsCm;
        private readonly float[] _cutTime;
        private readonly int[] _permutation;
        private static int[] s_patchOffsetsX;
        private static int[] s_patchOffsetsZ;

        private GrassLevelDefinition _level;
        private GrassRandom _rng;
        private int _seed;
        private int _baseUnitsPerCell;
        private int _maxUnitsPerCell;
        private int _version;

        // Change tracking for the renderer, per tile of TileSize x TileSize cells. Each tile is queued at most once
        // (FIFO ring), so the upload cost is proportional to how much grass actually changed, however far apart
        // the changes are (several cutting heads, gifts) and never the whole field unless it really all changed.
        private readonly bool[] _tileDirty;
        private readonly int[] _dirtyQueue;
        private int _dirtyHead, _dirtyCount;

        /// <summary>Tiles are TileSize x TileSize cells (32 x 32 = 1.6 m at 5 cm cells).</summary>
        public const int TileShift = 5;
        public const int TileSize = 1 << TileShift;
        public const int TileCellCount = TileSize * TileSize;

        public GrassField(GrassFieldLayout layout)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            layout.Validate();
            _layout = layout;
            int n = layout.CellCount;
            _units = new int[n];
            _heightsCm = new float[n];
            _cutTime = new float[n];
            _permutation = new int[n];
            TilesX = (layout.CellsX + TileSize - 1) >> TileShift;
            TilesZ = (layout.CellsZ + TileSize - 1) >> TileShift;
            _tileDirty = new bool[TilesX * TilesZ];
            _dirtyQueue = new int[TilesX * TilesZ];
            EnsurePatchOffsets();
        }

        public int TilesX { get; }
        public int TilesZ { get; }
        public int TileCount => TilesX * TilesZ;

        /// <summary>Increments on every <see cref="Reset"/> (new level): the renderer uploads everything at once then.</summary>
        public int ResetCount { get; private set; }

        /// <summary>Raised whenever GRASS LEFT changes. Argument is the new remaining count.</summary>
        public event Action<long> RemainingChanged;

        /// <summary>Raised exactly once per level, the moment GRASS LEFT reaches zero.</summary>
        public event Action Cleared;

        public GrassFieldLayout Layout => _layout;
        public GrassLevelDefinition Level => _level;
        public int CellsX => _layout.CellsX;
        public int CellsZ => _layout.CellsZ;
        public int CellCount => _units.Length;
        public float CellSizeX => _layout.CellSizeX;
        public float CellSizeZ => _layout.CellSizeZ;

        public long InitialUnits { get; private set; }
        public long Remaining { get; private set; }
        public long CutByMower { get; private set; }
        public long RemovedByGifts { get; private set; }
        public long AddedByGifts { get; private set; }
        public long TotalCleared => CutByMower + RemovedByGifts;

        /// <summary>True once GRASS LEFT has hit zero. The field is then frozen until the next Reset.</summary>
        public bool IsCleared { get; private set; }

        /// <summary>Extra height applied by gifts this level, in cm.</summary>
        public float HeightBonusCm { get; private set; }

        public float CurrentMinHeightCm => _level == null ? 0f : Math.Min(_level.MinHeightCm + HeightBonusCm, _layout.AbsoluteMaxHeightCm);
        public float CurrentMaxHeightCm => _level == null ? 0f : Math.Min(_level.MaxHeightCm + HeightBonusCm, _layout.AbsoluteMaxHeightCm);

        /// <summary>
        /// Game clock (seconds) stamped onto cells when they are cut, so the renderer can animate each blade
        /// shortening over a fraction of a second. Set it every frame (e.g. to Time.time).
        /// </summary>
        public float Clock { get; set; }

        /// <summary>Time the cell was cut (see <see cref="Clock"/>), or -1 if it is grown.</summary>
        public float GetCutTime(int cell) => _units[cell] == 0 ? _cutTime[cell] : -1f;

        /// <summary>Increments on every visual change; cheap way for observers to detect changes.</summary>
        public int Version => _version;

        public int BaseUnitsPerCell => _baseUnitsPerCell;

        public int GetUnits(int cell) => _units[cell];

        /// <summary>Grown height of a cell in cm, including the gift height bonus (capped at the layout maximum).</summary>
        public float GetHeightCm(int cell) => Math.Min(_heightsCm[cell] + HeightBonusCm, _layout.AbsoluteMaxHeightCm);

        /// <summary>Height before the gift bonus. Stored per cell; the bonus is one shared value (no per-cell rewrite).</summary>
        public float GetBaseHeightCm(int cell) => _heightsCm[cell];
        public bool IsCellCut(int cell) => _units[cell] == 0;

        public int CellIndex(int cx, int cz) => cz * _layout.CellsX + cx;

        /// <summary>Sets the field up for a level: exactly level.StartingUnits units, heights inside the level's range.</summary>
        public void Reset(GrassLevelDefinition level, int seed)
        {
            if (level == null) throw new ArgumentNullException(nameof(level));
            level.Validate();
            _level = level;
            _seed = seed;
            _rng = new GrassRandom(seed);

            int n = _units.Length;
            for (int i = 0; i < n; i++) _permutation[i] = i;
            for (int i = n - 1; i > 0; i--)
            {
                int j = _rng.NextInt(i + 1);
                int t = _permutation[i]; _permutation[i] = _permutation[j]; _permutation[j] = t;
            }

            long baseUnits = level.StartingUnits / n;
            if (baseUnits > int.MaxValue / 8) throw new ArgumentException("Too many units per cell; increase the cell count.");
            int remainder = (int)(level.StartingUnits % n);
            _baseUnitsPerCell = (int)Math.Max(1, baseUnits);
            _maxUnitsPerCell = (int)Math.Max(_baseUnitsPerCell + 1L, (long)_baseUnitsPerCell * _layout.MaxUnitsPerCellMultiplier);

            for (int i = 0; i < n; i++) _units[i] = (int)baseUnits;
            // Spread the remainder over random cells so the total is exact: e.g. 1,000,000 over 333x333 cells.
            for (int k = 0; k < remainder; k++) _units[_permutation[k]] += 1;

            HeightBonusCm = 0f;
            for (int i = 0; i < n; i++) _heightsCm[i] = SampleHeightCm(i % _layout.CellsX, i / _layout.CellsX);
            for (int i = 0; i < n; i++) _cutTime[i] = -1f;

            InitialUnits = level.StartingUnits;
            Remaining = level.StartingUnits;
            CutByMower = 0;
            RemovedByGifts = 0;
            AddedByGifts = 0;
            IsCleared = false;

            ResetCount++;
            MarkAllDirty();
            RemainingChanged?.Invoke(Remaining);
        }

        // ------------------------------------------------------------------ cutting

        /// <summary>Cuts every cell whose centre lies within radius of (x, z). Returns units cut.</summary>
        public long CutCircle(float x, float z, float radius) => CutCapsule(x, z, x, z, radius);

        /// <summary>
        /// Cuts every cell whose centre lies within radius of the segment a→b. Sweeping from last frame's
        /// position to this frame's means a fast mower never skips strips of grass.
        /// </summary>
        public long CutCapsule(float ax, float az, float bx, float bz, float radius)
        {
            if (IsCleared || _level == null || radius <= 0f) return 0;

            float csx = _layout.CellSizeX, csz = _layout.CellSizeZ;
            int minX = Clamp((int)Math.Floor((Math.Min(ax, bx) - radius) / csx), 0, _layout.CellsX - 1);
            int maxX = Clamp((int)Math.Floor((Math.Max(ax, bx) + radius) / csx), 0, _layout.CellsX - 1);
            int minZ = Clamp((int)Math.Floor((Math.Min(az, bz) - radius) / csz), 0, _layout.CellsZ - 1);
            int maxZ = Clamp((int)Math.Floor((Math.Max(az, bz) + radius) / csz), 0, _layout.CellsZ - 1);

            // Entirely outside the lawn?
            if (Math.Max(ax, bx) + radius < 0f || Math.Min(ax, bx) - radius > _layout.SizeX) return 0;
            if (Math.Max(az, bz) + radius < 0f || Math.Min(az, bz) - radius > _layout.SizeZ) return 0;

            float dx = bx - ax, dz = bz - az;
            float lenSq = dx * dx + dz * dz;
            float r2 = radius * radius;
            long removed = 0;

            for (int cz = minZ; cz <= maxZ; cz++)
            {
                float pz = (cz + 0.5f) * csz;
                int row = cz * _layout.CellsX;
                for (int cx = minX; cx <= maxX; cx++)
                {
                    int i = row + cx;
                    int u = _units[i];
                    if (u == 0) continue;

                    float px = (cx + 0.5f) * csx;
                    float t = 0f;
                    if (lenSq > 1e-12f)
                    {
                        t = ((px - ax) * dx + (pz - az) * dz) / lenSq;
                        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                    }
                    float qx = ax + dx * t - px, qz = az + dz * t - pz;
                    if (qx * qx + qz * qz > r2) continue;

                    _units[i] = 0;
                    _cutTime[i] = Clock;
                    removed += u;
                    MarkCellDirty(cx, cz);
                }
            }

            if (removed == 0) return 0;
            CutByMower += removed;
            _version++;
            ApplyRemoval(removed);
            return removed;
        }

        // ------------------------------------------------------------------ gift operations

        /// <summary>
        /// Regrows grass: first refills cut cells (as one round patch around a random cut cell), then, once the
        /// whole lawn is grown, thickens existing cells up to the per-cell cap. Returns units actually added.
        /// </summary>
        public long AddGrass(long amount)
        {
            if (IsCleared || _level == null || amount <= 0) return 0;
            long left = amount;

            int centre = FindRandomCell(cutCells: true);
            if (centre >= 0)
            {
                left = VisitPatch(centre, left, (cell, budget) =>
                {
                    if (_units[cell] != 0) return 0;
                    int add = (int)Math.Min(budget, _baseUnitsPerCell);
                    _units[cell] = add;
                    _heightsCm[cell] = SampleHeightCm(cell % _layout.CellsX, cell / _layout.CellsX);
                    return add;
                });
                // Patch exhausted but cut cells remain elsewhere: scan the rest of the lawn.
                left = VisitAll(left, (cell, budget) =>
                {
                    if (_units[cell] != 0) return 0;
                    int add = (int)Math.Min(budget, _baseUnitsPerCell);
                    _units[cell] = add;
                    _heightsCm[cell] = SampleHeightCm(cell % _layout.CellsX, cell / _layout.CellsX);
                    return add;
                });
            }

            // Whole lawn grown: make it denser, one base-sized layer per pass so it thickens evenly,
            // never beyond the cap.
            while (left > 0)
            {
                long before = left;
                left = VisitAll(left, (cell, budget) =>
                {
                    int room = _maxUnitsPerCell - _units[cell];
                    if (room <= 0) return 0;
                    int add = (int)Math.Min(budget, Math.Min(room, _baseUnitsPerCell));
                    _units[cell] += add;
                    return add;
                });
                if (left == before) break; // every cell is at the cap
            }

            long added = amount - left;
            if (added == 0) return 0;
            AddedByGifts += added;
            Remaining += added;
            _version++; // changed cells were marked dirty as they were visited
            RemainingChanged?.Invoke(Remaining);
            return added;
        }

        /// <summary>Removes grass as a round patch around a random uncut cell. Returns units actually removed.</summary>
        public long RemoveGrass(long amount)
        {
            if (IsCleared || _level == null || amount <= 0) return 0;
            amount = Math.Min(amount, Remaining);
            long left = amount;

            Func<int, long, int> take = (cell, budget) =>
            {
                int u = _units[cell];
                if (u == 0) return 0;
                int t = (int)Math.Min(budget, u);
                _units[cell] = u - t;
                if (u - t == 0) _cutTime[cell] = Clock;
                return t;
            };

            int centre = FindRandomCell(cutCells: false);
            if (centre >= 0)
            {
                left = VisitPatch(centre, left, take);
                left = VisitAll(left, take);
            }

            long removed = amount - left;
            if (removed == 0) return 0;
            RemovedByGifts += removed;
            _version++; // changed cells were marked dirty as they were visited
            ApplyRemoval(removed);
            return removed;
        }

        /// <summary>Makes all grass taller by cm (capped by the layout's absolute max). Returns cm actually applied.</summary>
        public float IncreaseHeight(float cm)
        {
            if (IsCleared || _level == null || cm <= 0f) return 0f;
            float room = _layout.AbsoluteMaxHeightCm - (_level.MaxHeightCm + HeightBonusCm);
            float applied = Math.Min(cm, Math.Max(0f, room));
            if (applied <= 0f) return 0f;

            // O(1): the bonus is applied when heights are read (GetHeightCm) and by the shader (_HeightBonus), so a
            // stream of "taller" gifts never rewrites or re-uploads the million cells.
            HeightBonusCm += applied;
            _version++;
            return applied;
        }

        // ------------------------------------------------------------------ renderer support

        /// <summary>Number of tiles changed and not yet consumed by the renderer.</summary>
        public int DirtyTileCount => _dirtyCount;

        /// <summary>
        /// Moves up to <paramref name="max"/> changed tiles (oldest first) into <paramref name="tiles"/> and returns
        /// how many. A renderer can cap this per frame so a burst of changes spreads over a few frames instead of
        /// stalling one.
        /// </summary>
        public int ConsumeDirtyTiles(int[] tiles, int max)
        {
            int n = _dirtyQueue.Length;
            int k = Math.Min(Math.Min(max, tiles.Length), _dirtyCount);
            for (int i = 0; i < k; i++)
            {
                int t = _dirtyQueue[_dirtyHead];
                _dirtyHead = _dirtyHead + 1 == n ? 0 : _dirtyHead + 1;
                _tileDirty[t] = false;
                tiles[i] = t;
            }
            _dirtyCount -= k;
            return k;
        }

        /// <summary>
        /// Writes one tile in tile-major order (2 floats per cell, x fastest): [2j] = base grown height in metres
        /// (without <see cref="HeightBonusCm"/>, which the renderer passes to the shader once; kept after cutting so
        /// the cut can animate from it), [2j+1] = cut time (see <see cref="Clock"/>) or -1 while grown. Cells past
        /// the field edge are written as (0, -1). The GPU buffer is laid out tile after tile, so a tile is one
        /// contiguous upload of <see cref="TileCellCount"/> cells.
        /// </summary>
        public void CopyRenderTile(int tile, float[] destination)
        {
            int tx = tile % TilesX, tz = tile / TilesX;
            int cx0 = tx << TileShift, cz0 = tz << TileShift;
            int cellsX = _layout.CellsX, cellsZ = _layout.CellsZ;
            int w = 0;
            for (int lz = 0; lz < TileSize; lz++)
            {
                int cz = cz0 + lz;
                for (int lx = 0; lx < TileSize; lx++, w += 2)
                {
                    int cx = cx0 + lx;
                    if (cx >= cellsX || cz >= cellsZ) { destination[w] = 0f; destination[w + 1] = -1f; continue; }
                    int i = cz * cellsX + cx;
                    destination[w] = _heightsCm[i] * 0.01f;
                    destination[w + 1] = _units[i] > 0 ? -1f : _cutTime[i];
                }
            }
        }

        /// <summary>
        /// Row-major copy (tests/tools): [2i] = grown height in metres including the gift bonus, [2i+1] = cut time
        /// or -1 while the cell is grown.
        /// </summary>
        public void CopyRenderCells(float[] destination, int start, int count)
        {
            for (int i = start, end = start + count; i < end; i++)
            {
                destination[2 * i] = GetHeightCm(i) * 0.01f;
                destination[2 * i + 1] = _units[i] > 0 ? -1f : _cutTime[i];
            }
        }

        /// <summary>Writes render heights in metres: blade height (with bonus) for grown cells, 0 for cut cells.</summary>
        public void CopyRenderHeightsMeters(float[] destination, int start, int count)
        {
            for (int i = start, end = start + count; i < end; i++)
                destination[i] = _units[i] > 0 ? GetHeightCm(i) * 0.01f : 0f;
        }

        // ------------------------------------------------------------------ internals

        private void ApplyRemoval(long removed)
        {
            Remaining -= removed;
            if (Remaining < 0) Remaining = 0; // defensive; cannot happen with consistent bookkeeping
            RemainingChanged?.Invoke(Remaining);
            if (Remaining == 0 && !IsCleared)
            {
                IsCleared = true;
                Cleared?.Invoke();
            }
        }

        /// <summary>Base height (level range, without the gift bonus; see <see cref="GetHeightCm"/>).</summary>
        private float SampleHeightCm(int cx, int cz)
        {
            float min = _level.MinHeightCm, max = _level.MaxHeightCm;
            float s = Math.Max(1f, _layout.HeightNoiseCells);
            float patch = GrassRandom.ValueNoise(cx / s, cz / s, _seed);
            float detail = GrassRandom.ValueNoise(cx / (s * 0.3f) + 17.3f, cz / (s * 0.3f) - 4.1f, _seed + 1);
            float white = GrassRandom.Hash01(cx, cz, _seed + 2);
            float t = patch * 0.55f + detail * 0.25f + white * 0.20f;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            return min + (max - min) * t;
        }

        private int FindRandomCell(bool cutCells)
        {
            int n = _permutation.Length;
            int offset = _rng.NextInt(n);
            for (int k = 0; k < n; k++)
            {
                int cell = _permutation[(offset + k) % n];
                if ((_units[cell] == 0) == cutCells) return cell;
            }
            return -1;
        }

        /// <summary>Visits cells in growing circles around centre until the budget is spent. Returns budget left.</summary>
        private long VisitPatch(int centre, long budget, Func<int, long, int> visit)
        {
            int ccx = centre % _layout.CellsX, ccz = centre / _layout.CellsX;
            for (int k = 0; k < s_patchOffsetsX.Length && budget > 0; k++)
            {
                int cx = ccx + s_patchOffsetsX[k], cz = ccz + s_patchOffsetsZ[k];
                if ((uint)cx >= (uint)_layout.CellsX || (uint)cz >= (uint)_layout.CellsZ) continue;
                int got = visit(CellIndex(cx, cz), budget);
                if (got != 0) { budget -= got; MarkCellDirty(cx, cz); }
            }
            return budget;
        }

        private long VisitAll(long budget, Func<int, long, int> visit)
        {
            if (budget <= 0) return budget;
            int n = _permutation.Length;
            int offset = _rng.NextInt(n);
            for (int k = 0; k < n && budget > 0; k++)
            {
                int cell = _permutation[(offset + k) % n];
                int got = visit(cell, budget);
                if (got != 0) { budget -= got; MarkCellDirty(cell % _layout.CellsX, cell / _layout.CellsX); }
            }
            return budget;
        }

        private void MarkCellDirty(int cx, int cz)
        {
            int t = (cz >> TileShift) * TilesX + (cx >> TileShift);
            if (_tileDirty[t]) return;
            _tileDirty[t] = true;
            int n = _dirtyQueue.Length;
            int slot = _dirtyHead + _dirtyCount;
            _dirtyQueue[slot >= n ? slot - n : slot] = t;
            _dirtyCount++;
        }

        private void MarkAllDirty()
        {
            int n = _dirtyQueue.Length;
            for (int t = 0; t < n; t++) { _tileDirty[t] = true; _dirtyQueue[t] = t; }
            _dirtyHead = 0;
            _dirtyCount = n;
            _version++;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        private static void EnsurePatchOffsets()
        {
            if (s_patchOffsetsX != null) return;
            int r = PatchRadiusCells;
            var list = new System.Collections.Generic.List<(int x, int z, int d)>();
            for (int z = -r; z <= r; z++)
                for (int x = -r; x <= r; x++)
                {
                    int d = x * x + z * z;
                    if (d <= r * r) list.Add((x, z, d));
                }
            list.Sort((a, b) => a.d.CompareTo(b.d));
            var xs = new int[list.Count];
            var zs = new int[list.Count];
            for (int i = 0; i < list.Count; i++) { xs[i] = list[i].x; zs[i] = list[i].z; }
            s_patchOffsetsZ = zs;
            s_patchOffsetsX = xs;
        }
    }
}
