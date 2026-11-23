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

        // Per-tile bookkeeping for gifts: how many cells of each tile are cut, how many cells the tile has (edge tiles
        // are partial) and a shuffled tile order. Gifts find cut / grown cells through these in O(tiles + tile size)
        // instead of scanning every cell, so a gift costs the same on a 1M or 2M lawn (a few KB, not 4 B per target).
        private readonly int[] _tileCut;
        private readonly int[] _tileCells;
        private readonly int[] _tileOrder;

        /// <summary>Patches tried before a gift falls back to whole tiles (keeps the cost bounded with scattered cuts).</summary>
        private const int MaxPatchRounds = 8;

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
            TilesX = (layout.CellsX + TileSize - 1) >> TileShift;
            TilesZ = (layout.CellsZ + TileSize - 1) >> TileShift;
            int tiles = TilesX * TilesZ;
            _tileDirty = new bool[tiles];
            _dirtyQueue = new int[tiles];
            _tileCut = new int[tiles];
            _tileCells = new int[tiles];
            _tileOrder = new int[tiles];
            for (int t = 0; t < tiles; t++)
            {
                int w = Math.Min(TileSize, layout.CellsX - ((t % TilesX) << TileShift));
                int h = Math.Min(TileSize, layout.CellsZ - ((t / TilesX) << TileShift));
                _tileCells[t] = w * h;
            }
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
            int tiles = _tileOrder.Length;
            for (int t = 0; t < tiles; t++) _tileOrder[t] = t;
            for (int t = tiles - 1; t > 0; t--)
            {
                int j = _rng.NextInt(t + 1);
                int s = _tileOrder[t]; _tileOrder[t] = _tileOrder[j]; _tileOrder[j] = s;
            }

            long baseUnits = level.StartingUnits / n;
            if (baseUnits > int.MaxValue / 8) throw new ArgumentException("Too many units per cell; increase the cell count.");
            int remainder = (int)(level.StartingUnits % n);
            _baseUnitsPerCell = (int)Math.Max(1, baseUnits);
            _maxUnitsPerCell = (int)Math.Max(_baseUnitsPerCell + 1L, (long)_baseUnitsPerCell * _layout.MaxUnitsPerCellMultiplier);

            for (int i = 0; i < n; i++) _units[i] = (int)baseUnits;
            // Spread the remainder over scattered distinct cells so the total is exact: e.g. 1,000,000 over 333x333
            // cells. k -> (k * step + offset) mod n visits distinct cells when step and n share no factor.
            long step = 1_000_003L;
            while (Gcd(step, n) != 1) step += 2;
            long start = _rng.NextInt(n);
            for (long k = 0; k < remainder; k++) _units[(int)((k * step + start) % n)] += 1;
            RecountTiles();

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
                    _tileCut[(cz >> TileShift) * TilesX + (cx >> TileShift)]++;
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

            // Regrow cut cells as round patches around random cut cells (then whole tiles if cuts are scattered).
            left = Spend(left, cutCells: true, (cell, budget) =>
            {
                if (_units[cell] != 0) return 0;
                int add = (int)Math.Min(budget, _baseUnitsPerCell);
                _units[cell] = add;
                _heightsCm[cell] = SampleHeightCm(cell % _layout.CellsX, cell / _layout.CellsX);
                _tileCut[TileOf(cell)]--;
                return add;
            });

            // Whole lawn grown: make it denser, one base-sized layer per pass, never beyond the cap.
            while (left > 0)
            {
                long before = left;
                left = VisitTiles(left, cutCells: false, (cell, budget) =>
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
                if (u - t == 0) { _cutTime[cell] = Clock; _tileCut[TileOf(cell)]++; }
                return t;
            };

            left = Spend(left, cutCells: false, take);

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

        // ------------------------------------------------------------------ save / load

        private const uint SaveMagic = 0x53534750; // "PGSS"
        private const int SaveVersion = 1;

        /// <summary>
        /// Writes which grass is left: per-cell units run-length encoded (cut areas are contiguous, so a mowed
        /// million-target lawn is typically tens of KB), plus the counters and gift height bonus. Heights are not
        /// stored: <see cref="Reset"/> regenerates them exactly from the level and seed.
        /// </summary>
        public void WriteState(System.IO.Stream stream)
        {
            if (_level == null) throw new InvalidOperationException("No level in progress.");
            var w = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write(SaveMagic);
            w.Write(SaveVersion);
            w.Write(_layout.CellsX); w.Write(_layout.CellsZ);
            w.Write(_level.DisplayName ?? string.Empty);
            w.Write(_level.StartingUnits);
            w.Write(_seed);
            w.Write(InitialUnits); w.Write(Remaining);
            w.Write(CutByMower); w.Write(RemovedByGifts); w.Write(AddedByGifts);
            w.Write(HeightBonusCm);
            int n = _units.Length, i = 0;
            while (i < n)
            {
                int v = _units[i], run = 1;
                while (i + run < n && _units[i + run] == v) run++;
                Write7Bit(w, (uint)v);
                Write7Bit(w, (uint)run);
                i += run;
            }
            w.Flush();
        }

        /// <summary>
        /// Restores a <see cref="WriteState"/> snapshot onto this field. Call after <see cref="Reset"/> with the same
        /// level and seed (so heights match). Returns false, leaving the field untouched, if the snapshot is for another
        /// layout, level or seed, or is damaged. GRASS LEFT is recomputed from the cells, so it is always exact.
        /// </summary>
        public bool TryReadState(System.IO.Stream stream)
        {
            if (_level == null) return false;
            try
            {
                var r = new System.IO.BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                if (r.ReadUInt32() != SaveMagic || r.ReadInt32() != SaveVersion) return false;
                if (r.ReadInt32() != _layout.CellsX || r.ReadInt32() != _layout.CellsZ) return false;
                if (r.ReadString() != (_level.DisplayName ?? string.Empty) || r.ReadInt64() != _level.StartingUnits) return false;
                if (r.ReadInt32() != _seed) return false;
                long initial = r.ReadInt64(); r.ReadInt64(); // stored Remaining: recomputed below instead
                long cut = r.ReadInt64(), removed = r.ReadInt64(), added = r.ReadInt64();
                float bonus = r.ReadSingle();

                int n = _units.Length;
                var units = new int[n];
                int i = 0;
                long total = 0;
                while (i < n)
                {
                    int v = (int)Read7Bit(r), run = (int)Read7Bit(r);
                    if (run <= 0 || i + run > n || v < 0) return false;
                    for (int k = 0; k < run; k++) units[i + k] = v;
                    total += (long)v * run;
                    i += run;
                }

                Array.Copy(units, _units, n);
                for (int c = 0; c < n; c++) _cutTime[c] = _units[c] == 0 ? -1000f : -1f; // cut long ago: no animation
                RecountTiles();
                InitialUnits = initial;
                Remaining = total;
                CutByMower = cut; RemovedByGifts = removed; AddedByGifts = added;
                HeightBonusCm = bonus;
                IsCleared = Remaining == 0;
                ResetCount++; // renderer re-uploads everything
                MarkAllDirty();
                RemainingChanged?.Invoke(Remaining);
                return true;
            }
            catch (System.IO.EndOfStreamException) { return false; }
            catch (System.IO.IOException) { return false; }
        }

        private static void Write7Bit(System.IO.BinaryWriter w, uint v)
        {
            while (v >= 0x80) { w.Write((byte)(v | 0x80)); v >>= 7; }
            w.Write((byte)v);
        }

        private static uint Read7Bit(System.IO.BinaryReader r)
        {
            uint v = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte b = r.ReadByte();
                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return v;
            }
            throw new System.IO.IOException("Bad varint");
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

        private int TileOf(int cell)
        {
            int cx = cell % _layout.CellsX, cz = cell / _layout.CellsX;
            return (cz >> TileShift) * TilesX + (cx >> TileShift);
        }

        /// <summary>Cells of the tile that are cut (cutCells) or still grown.</summary>
        private int TileMatches(int tile, bool cutCells) => cutCells ? _tileCut[tile] : _tileCells[tile] - _tileCut[tile];

        private void RecountTiles()
        {
            Array.Clear(_tileCut, 0, _tileCut.Length);
            int cellsX = _layout.CellsX, n = _units.Length;
            for (int i = 0; i < n; i++)
                if (_units[i] == 0) _tileCut[((i / cellsX) >> TileShift) * TilesX + ((i % cellsX) >> TileShift)]++;
        }

        private static long Gcd(long a, long b) { while (b != 0) { long t = a % b; a = b; b = t; } return a; }

        /// <summary>
        /// Spends a gift's budget as round patches around random matching cells, up to <see cref="MaxPatchRounds"/>
        /// patches, then whole tiles. Cost is bounded by patches + tiles, never by the number of cells on the lawn.
        /// </summary>
        private long Spend(long budget, bool cutCells, Func<int, long, int> visit)
        {
            for (int round = 0; round < MaxPatchRounds && budget > 0; round++)
            {
                int centre = FindRandomCell(cutCells);
                if (centre < 0) return budget;
                budget = VisitPatch(centre, budget, visit);
            }
            return VisitTiles(budget, cutCells, visit);
        }

        /// <summary>A random cut (or grown) cell, found via the per-tile counts: O(tiles + tile size). -1 if none.</summary>
        private int FindRandomCell(bool cutCells)
        {
            int nt = _tileOrder.Length;
            int offset = _rng.NextInt(nt);
            for (int k = 0; k < nt; k++)
            {
                int idx = offset + k; if (idx >= nt) idx -= nt;
                int tile = _tileOrder[idx];
                if (TileMatches(tile, cutCells) == 0) continue;
                int cx0 = (tile % TilesX) << TileShift, cz0 = (tile / TilesX) << TileShift;
                int lo = _rng.NextInt(TileCellCount);
                for (int j = 0; j < TileCellCount; j++)
                {
                    int local = (lo + j) & (TileCellCount - 1);
                    int cx = cx0 + (local & (TileSize - 1)), cz = cz0 + (local >> TileShift);
                    if (cx >= _layout.CellsX || cz >= _layout.CellsZ) continue;
                    int cell = cz * _layout.CellsX + cx;
                    if ((_units[cell] == 0) == cutCells) return cell;
                }
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

        /// <summary>
        /// Visits cells tile by tile (random tile order, skipping tiles with no matching cells) until the budget is
        /// spent. Returns budget left.
        /// </summary>
        private long VisitTiles(long budget, bool cutCells, Func<int, long, int> visit)
        {
            if (budget <= 0) return budget;
            int nt = _tileOrder.Length;
            int offset = _rng.NextInt(nt);
            int cellsX = _layout.CellsX, cellsZ = _layout.CellsZ;
            for (int k = 0; k < nt && budget > 0; k++)
            {
                int idx = offset + k; if (idx >= nt) idx -= nt;
                int tile = _tileOrder[idx];
                if (TileMatches(tile, cutCells) == 0) continue;
                int cx0 = (tile % TilesX) << TileShift, cz0 = (tile / TilesX) << TileShift;
                int cx1 = Math.Min(cx0 + TileSize, cellsX), cz1 = Math.Min(cz0 + TileSize, cellsZ);
                for (int cz = cz0; cz < cz1 && budget > 0; cz++)
                    for (int cx = cx0; cx < cx1 && budget > 0; cx++)
                    {
                        int got = visit(cz * cellsX + cx, budget);
                        if (got != 0) { budget -= got; MarkCellDirty(cx, cz); }
                    }
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
