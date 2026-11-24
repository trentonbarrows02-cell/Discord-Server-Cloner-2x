using UnityEngine;
using UnityEngine.Rendering;

namespace PlayShift.Grass
{
    public enum GrassQuality
    {
        Low,
        Medium,
        High,
        Ultra,
    }

    /// <summary>
    /// Draws the lawn with GPU procedural instancing. No GameObject, Mesh or stored data per blade exists:
    ///  - blade positions are generated in the shader from (chunk seed, instance index), so there is no blade
    ///    buffer at all: memory and load time do not grow with blade count or quality;
    ///  - one GPU buffer holds per cell (grown height, cut time), laid out in 32 x 32-cell tiles; only tiles that
    ///    changed are uploaded (one contiguous 8 KB copy each), capped per frame so gift bursts never stall a frame;
    ///  - the shader (GrassBlade.hlsl) builds, bends, sways and cut-animates each blade.
    /// Blades are drawn per chunk for frustum/distance culling and LOD: a chunk's procedural sequence is uniformly
    /// random, so drawing its first N blades is a uniform subsample. Density per level and quality preset scale N.
    /// The same cell buffer serves all five levels; a new level re-uploads every tile at once.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GrassFieldRenderer : MonoBehaviour
    {
        private const int CellStride = sizeof(float) * 2;
        private const int MaxCutters = 4;

        [System.Serializable]
        public struct QualityPreset
        {
            [Tooltip("Blade density at full detail (blades per square metre, before the level's density fraction).")]
            [Range(50, 3000)] public int bladesPerSquareMeter;
            public float fullDetailDistance;
            public float midDetailDistance;
            public float maxDrawDistance;
            [Tooltip("Grass chunks closer than this cast real-time shadows (0 = grass casts none).")]
            public float shadowCastDistance;
        }

        [Tooltip("Material using PlayShift/InstancedGrassURP (URP) or PlayShift/InstancedGrass (Built-in). Created automatically when empty.")]
        [SerializeField] private Material grassMaterial;

        [Header("Quality")]
        [SerializeField] private GrassQuality quality = GrassQuality.High;
        // Blades cover the whole field: full detail near, fewer and wider blades with distance (per blade, smooth),
        // sparse wide "card" blades far away; beyond maxDrawDistance they fade into the ground shader
        // (PlayShift/GrassGround), which shows grown/cut grass from the same cell data at almost no cost.
        [SerializeField] private QualityPreset low = new QualityPreset { bladesPerSquareMeter = 300, fullDetailDistance = 8f, midDetailDistance = 18f, maxDrawDistance = 100f, shadowCastDistance = 0f };
        [SerializeField] private QualityPreset medium = new QualityPreset { bladesPerSquareMeter = 480, fullDetailDistance = 10f, midDetailDistance = 22f, maxDrawDistance = 120f, shadowCastDistance = 4f };
        [SerializeField] private QualityPreset high = new QualityPreset { bladesPerSquareMeter = 720, fullDetailDistance = 13f, midDetailDistance = 28f, maxDrawDistance = 145f, shadowCastDistance = 6f };
        [SerializeField] private QualityPreset ultra = new QualityPreset { bladesPerSquareMeter = 1100, fullDetailDistance = 18f, midDetailDistance = 38f, maxDrawDistance = 150f, shadowCastDistance = 10f };
        [Range(0.05f, 1f)] [SerializeField] private float midDetailFraction = 0.45f;
        [Range(0.02f, 1f)] [SerializeField] private float farDetailFraction = 0.2f;

        [Header("Layout")]
        [Tooltip("Chunk edge in metres for culling/LOD (independent of the cutting grid).")]
        [Range(0.5f, 8f)] [SerializeField] private float chunkMeters = 2f;
        [SerializeField] private int bladeSeed = 777;
        [Tooltip("Far away, groups of N x N chunks are drawn with one draw call (fewer draws; the blades are exactly the " +
                 "same as per-chunk drawing, so nothing pops). 1 = off.")]
        [Range(1, 8)] [SerializeField] private int farChunkGroup = 4;
        [Range(3, 10)] [SerializeField] private int bladeSegments = 6;
        [Tooltip("Blade segments at mid and far distance (fewer triangles where the curve can't be seen).")]
        [Range(2, 6)] [SerializeField] private int midBladeSegments = 3;
        [Range(2, 4)] [SerializeField] private int farBladeSegments = 2;

        [Header("Streaming")]
        [Tooltip("Most changed 32x32-cell tiles uploaded per frame (8 KB each). Bursts beyond this spread over the next frames.")]
        [Range(8, 1024)] [SerializeField] private int maxTileUploadsPerFrame = 128;

        [Header("Rendering")]
        [SerializeField] private Camera targetCamera;

        [Header("Cut shape")]
        [Tooltip("Fine GPU mask for the look of cut grass: cut paths follow the real blade circle (smooth, round) instead " +
                 "of the 10 cm counting cells. The grass counter always uses the cells.")]
        [SerializeField] private bool useCutMask = true;
        [Tooltip("Mask texels across the lawn (4096 on a 100 m lawn = 2.4 cm). 16 MB of GPU memory at 4096, 4 MB at 2048.")]
        [SerializeField] private int cutMaskResolution = 4096;

        [Header("Title screen")]
        [Tooltip("The title screen shows an untouched lawn (no cut paths). Only the look: saved progress is not touched.")]
        [SerializeField] private bool untouchedLawnOnTitle = true;
        [Tooltip("Title screen lawn: the ground shader carries the field; blades only near the camera (Title Blade " +
                 "Distance), no grass shadows. Far away from the orbit camera blades are sub-pixel and only add noise.")]
        [SerializeField] private bool titleScreenGrassProfile = true;
        [Tooltip("Title screen: blades only this close to the camera (m), fading into the ground lawn beyond.")]
        [SerializeField] private float titleBladeDistance = 38f;

        private GrassField _field;
        private GrassCutMask _mask;
        /// <summary>The fine cut mask (null when off or unsupported).</summary>
        public GrassCutMask CutMask => _mask;
        private Mesh _bladeMesh, _bladeMeshMid, _bladeMeshFar;
        private GraphicsBuffer _cellBuffer;
        private float[] _tileScratch;
        private int[] _tileList;
        private Chunk[] _chunks;
        private MaterialPropertyBlock _mpb;
        private readonly Plane[] _frustum = new Plane[6];
        private readonly Vector4[] _cutterData = new Vector4[MaxCutters];
        private readonly Vector4[] _cutterShape = new Vector4[MaxCutters];
        private static readonly int CutterShapeId = Shader.PropertyToID("_CutterShape");
        /// <summary>Diagnostics: grass is not pressed under / away from the trimmer (to see what it fixes).</summary>
        public static bool DebugNoToolPress;
        private int _builtCellsX, _builtCellsZ, _builtBladesPerM2 = -1;
        private float _builtSizeX, _builtSizeZ, _builtChunkMeters;
        private int _seenResetCount = -1;
        private int _chunksX, _chunksZ;
        private long _totalBlades;
        private bool _ownsMaterial;

        private struct Chunk
        {
            public float Seed;   // < 2^24 so it survives the float shader property exactly
            public int Count;    // blades at full detail
            public Vector3 LocalMin;
            public Vector3 LocalMax;
        }

        private static readonly int CellsId = Shader.PropertyToID("_Cells");
        private static readonly int CellGridId = Shader.PropertyToID("_CellGrid");
        private static readonly int TilesXId = Shader.PropertyToID("_TilesX");
        private static readonly int ChunkRectId = Shader.PropertyToID("_ChunkRect");
        private static readonly int ChunkSeedId = Shader.PropertyToID("_ChunkSeed");
        private static readonly int HeightBonusId = Shader.PropertyToID("_HeightBonus");
        private static readonly int FieldToWorldId = Shader.PropertyToID("_FieldToWorld");
        private static readonly int LodWidthScaleId = Shader.PropertyToID("_LodWidthScale");
        private static readonly int HeightClampId = Shader.PropertyToID("_HeightClamp");
        private static readonly int GrassTimeId = Shader.PropertyToID("_GrassTime");
        private static readonly int CuttersId = Shader.PropertyToID("_Cutters");
        private static readonly int CutterCountId = Shader.PropertyToID("_CutterCount");
        private static readonly int SpinId = Shader.PropertyToID("_Spin");
        private static readonly int FieldSizeId = Shader.PropertyToID("_FieldSize");
        private static readonly int WorldToFieldId = Shader.PropertyToID("_WorldToField");
        private static readonly int GrassFadeId = Shader.PropertyToID("_GrassFade");
        private static readonly int GrassDebugId = Shader.PropertyToID("_GrassDebug");
        private static readonly int GrassLodId = Shader.PropertyToID("_GrassLod");
        private static readonly int GrassLodFracId = Shader.PropertyToID("_GrassLodFrac");
        private static readonly int ChunkCountId = Shader.PropertyToID("_ChunkCount");
        private static readonly int PsLodDistId = Shader.PropertyToID("_PsLodDist");
        private static readonly int PsGrassAvgId = Shader.PropertyToID("_PsGrassAvg");
        private static readonly int PsGrassDryId = Shader.PropertyToID("_PsGrassDry");
        private static readonly int GrassShowUncutId = Shader.PropertyToID("_GrassShowUncut");
        private static readonly int PsChunkId = Shader.PropertyToID("_PsChunk");
        private static readonly int SuperChunkId = Shader.PropertyToID("_SuperChunk");
        private static readonly int PsChunkGridId = Shader.PropertyToID("_PsChunkGrid");
        private static readonly int PsChunkGrid2Id = Shader.PropertyToID("_PsChunkGrid2");

        // ---- Diagnostics (capture tool / dev only; all off in the game) ----
        /// <summary>0 = normal; 1 = the ground shows the cut state (white = fine mask and cells agree, red = cell only,
        /// cyan = mask only), blades hidden; 2 = blades tinted by detail level (red full, green mid, blue far);
        /// 3 = the ground shows detail zones by distance (red full, green mid, blue far, orange blade fade, grey no
        /// blades), chunk edges (thin lines) and the chunk culling edge (white), blades hidden.</summary>
        public static int DebugView;
        /// <summary>Grass casts no shadows (to see what darkening comes from grass shadows).</summary>
        public static bool DebugNoBladeShadows;
        /// <summary>No blades drawn: only the ground shader.</summary>
        public static bool DebugHideBlades;
        /// <summary>Camera that drives culling and detail levels (null = the target camera / main camera).</summary>
        public Camera LodCamera { get; set; }
        /// <summary>Rebuilds the fine cut mask from the cells, as a loaded save or a new level does.</summary>
        public void DebugRebuildMask() { if (_mask != null && _field != null) _mask.RebuildFromCells(_field); }

        public GrassField Field => _field;
        /// <summary>Blades the whole field has at full detail for the current quality (before the level's density).</summary>
        public long TotalBlades => _totalBlades;
        public int LastDrawnBlades { get; private set; }
        public int LastDrawCalls { get; private set; }
        public int LastTilesUploaded { get; private set; }
        /// <summary>GPU memory owned by the renderer (the tiled cell buffer; blades need none).</summary>
        public long GpuBytes => _cellBuffer != null ? (long)_cellBuffer.count * CellStride : 0;

        public GrassQuality Quality
        {
            get => quality;
            set
            {
                if (quality == value) return;
                quality = value;
                GraphicsProfile.Apply(value); // shadows and LOD distance follow the preset
                RebuildChunksIfNeeded(); // procedural blades: changing quality is free (no buffers to rebuild)
            }
        }

        private QualityPreset Q
        {
            get
            {
                switch (quality)
                {
                    case GrassQuality.Low: return ForTitle(low);
                    case GrassQuality.Medium: return ForTitle(medium);
                    case GrassQuality.Ultra: return ForTitle(ultra);
                    default: return ForTitle(high);
                }
            }
        }

        /// <summary>Binds the logical field. Call once; levels are switched by resetting the same field.</summary>
        public void Bind(GrassField field)
        {
            _field = field;
            if (SystemInfo.graphicsShaderLevel < 45)
            {
                Debug.LogError("[PlayShift Grass] This GPU does not support shader model 4.5 structured buffers; grass cannot be drawn.", this);
                enabled = false;
                return;
            }
            EnsureMaterial();
            EnsureCellBuffer();
            RebuildChunksIfNeeded();
            UploadAllTiles();
            _mask?.Release();
            _mask = null;
            if (useCutMask)
            {
                _mask = new GrassCutMask();
                if (_mask.Create(field, cutMaskResolution)) { _mask.RebuildFromCells(field); field.AcknowledgeMaskRebuild(); }
                else _mask = null;
            }
        }

        public static string DefaultShaderName =>
            GraphicsSettings.currentRenderPipeline != null ? "PlayShift/InstancedGrassURP" : "PlayShift/InstancedGrass";

        private void EnsureMaterial()
        {
            if (grassMaterial != null) return;
            var shader = Shader.Find(DefaultShaderName);
            if (shader == null)
            {
                Debug.LogError($"[PlayShift Grass] No grass material assigned and shader '{DefaultShaderName}' was not found. " +
                               "Assign a grass material (PlayShift > Grass > Create Test Area does this).", this);
                enabled = false;
                return;
            }
            Debug.LogWarning("[PlayShift Grass] No grass material assigned; creating one at runtime. Assign a material asset so the shader ships in builds.", this);
            grassMaterial = new Material(shader) { name = "InstancedGrass (runtime)" };
            _ownsMaterial = true;
        }

        /// <summary>(Re)creates the tiled cell buffer when the field layout changes. Independent of quality.</summary>
        private void EnsureCellBuffer()
        {
            if (_field == null || grassMaterial == null) return;
            var layout = _field.Layout;
            if (_cellBuffer != null && _builtCellsX == layout.CellsX && _builtCellsZ == layout.CellsZ) return;

            ReleaseBuffers();
            if (_bladeMesh == null) _bladeMesh = GrassBladeMesh.Create(bladeSegments);
            if (_bladeMeshMid == null) _bladeMeshMid = GrassBladeMesh.Create(midBladeSegments);
            if (_bladeMeshFar == null) _bladeMeshFar = GrassBladeMesh.Create(farBladeSegments);
            _cellBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _field.TileCount * GrassField.TileCellCount, CellStride);
            _tileScratch = new float[GrassField.TileCellCount * 2];
            _tileList = new int[_field.TileCount];
            _builtCellsX = layout.CellsX;
            _builtCellsZ = layout.CellsZ;

            var grid = new Vector4(layout.CellsX, layout.CellsZ, 1f / layout.CellSizeX, 1f / layout.CellSizeZ);
            grassMaterial.SetBuffer(CellsId, _cellBuffer);
            grassMaterial.SetVector(CellGridId, grid);
            grassMaterial.SetFloat(TilesXId, _field.TilesX);
            // Also global, for the ground shader (PlayShift/GrassGround) that shades distant/cut grass from the same data.
            Shader.SetGlobalBuffer(CellsId, _cellBuffer);
            Shader.SetGlobalVector(CellGridId, grid);
            Shader.SetGlobalFloat(TilesXId, _field.TilesX);
            Shader.SetGlobalVector(FieldSizeId, new Vector4(layout.SizeX, layout.SizeZ, 0f, 0f));
        }

        /// <summary>Chunk rectangles + blade counts. Tiny CPU-side table; rebuilt when size, chunking or quality change.</summary>
        private void RebuildChunksIfNeeded()
        {
            if (_field == null) return;
            var layout = _field.Layout;
            int bpm = Q.bladesPerSquareMeter;
            if (_chunks != null && _builtBladesPerM2 == bpm && Mathf.Approximately(_builtSizeX, layout.SizeX) &&
                Mathf.Approximately(_builtSizeZ, layout.SizeZ) && Mathf.Approximately(_builtChunkMeters, chunkMeters))
                return;

            float sizeX = layout.SizeX, sizeZ = layout.SizeZ;
            int chunksX = Mathf.CeilToInt(sizeX / chunkMeters);
            int chunksZ = Mathf.CeilToInt(sizeZ / chunkMeters);
            _chunks = new Chunk[chunksX * chunksZ];
            _chunksX = chunksX;
            _chunksZ = chunksZ;
            _totalBlades = 0;
            for (int kz = 0; kz < chunksZ; kz++)
                for (int kx = 0; kx < chunksX; kx++)
                {
                    float x0 = kx * chunkMeters, x1 = Mathf.Min(sizeX, x0 + chunkMeters);
                    float z0 = kz * chunkMeters, z1 = Mathf.Min(sizeZ, z0 + chunkMeters);
                    int i = kz * chunksX + kx;
                    int count = Mathf.Max(1, Mathf.RoundToInt((x1 - x0) * (z1 - z0) * bpm));
                    _chunks[i] = new Chunk
                    {
                        Seed = (uint)(bladeSeed * 7919 + i * 104729) & 0xFFFFFF,
                        Count = count,
                        LocalMin = new Vector3(x0, 0f, z0),
                        LocalMax = new Vector3(x1, 0f, z1),
                    };
                    _totalBlades += count;
                }
            _builtBladesPerM2 = bpm;
            _builtSizeX = sizeX; _builtSizeZ = sizeZ; _builtChunkMeters = chunkMeters;
        }

        /// <summary>Uploads up to <paramref name="budget"/> changed tiles (one contiguous SetData each).</summary>
        private void UploadTiles(int budget)
        {
            if (_field == null || _cellBuffer == null) return;
            int n = _field.ConsumeDirtyTiles(_tileList, budget);
            for (int k = 0; k < n; k++) UploadTile(_tileList[k]);
            LastTilesUploaded = n;
        }

        /// <summary>Uploads every tile (new level or fresh buffer) and drops whatever was queued.</summary>
        private void UploadAllTiles()
        {
            if (_field == null || _cellBuffer == null) return;
            while (_field.ConsumeDirtyTiles(_tileList, int.MaxValue) > 0) { }
            for (int t = 0; t < _field.TileCount; t++) UploadTile(t);
            LastTilesUploaded = _field.TileCount;
            _seenResetCount = _field.ResetCount;
        }

        private void UploadTile(int tile)
        {
            _field.CopyRenderTile(tile, _tileScratch);
            // Indices/count are in floats (2 per cell); the tile is one contiguous block of the tile-major buffer.
            _cellBuffer.SetData(_tileScratch, 0, tile * GrassField.TileCellCount * 2, _tileScratch.Length);
        }

        /// <summary>Main-thread milliseconds spent in this renderer last frame (uploads + culling + draw submission).</summary>
        public float LastCpuMs { get; private set; }

        private void LateUpdate()
        {
            if (_field == null || _field.Level == null || _cellBuffer == null || grassMaterial == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Tick();
            LastCpuMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        private void Tick()
        {
            // A new level (or a fresh buffer) changes every cell: upload it all at once, level transitions can afford
            // it. Otherwise only the tiles touched since last frame, capped so gift bursts spread over a few frames.
            if (_field.ResetCount != _seenResetCount) UploadAllTiles();
            else UploadTiles(maxTileUploadsPerFrame);

            _mask?.Update(_field, GrassCutter.FrameStrokes);
            var cam = LodCamera != null ? LodCamera : targetCamera != null ? targetCamera : Camera.main;
            UpdateShaderGlobals();
            Shader.SetGlobalFloat(GrassDebugId, DebugView);
            {
                var q0 = Q;
                Shader.SetGlobalVector(PsLodDistId, new Vector4(q0.fullDetailDistance, q0.midDetailDistance, q0.maxDrawDistance, q0.maxDrawDistance * 0.72f));
                Shader.SetGlobalVector(PsChunkId, new Vector4(chunkMeters, _field.CurrentMaxHeightCm * 0.013f + 0.1f, 0f, 0f));
            }
            // Title screen: the menu lawn (MenuLawn, shell grass) stands in for the blades; no gameplay blades drawn.
            bool title = MainMenu.IsOpen && titleScreenGrassProfile;
            if (DebugHideBlades || DebugView == 1 || DebugView == 3 || title) { LastDrawnBlades = 0; LastDrawCalls = 0; return; }
            DrawChunks(cam);
        }

        private void UpdateShaderGlobals()
        {
            grassMaterial.SetMatrix(FieldToWorldId, transform.localToWorldMatrix);
            float minM = _field.CurrentMinHeightCm * 0.01f, maxM = _field.CurrentMaxHeightCm * 0.01f;
            grassMaterial.SetVector(HeightClampId, new Vector4(minM, maxM, 0f, 0f));
            grassMaterial.SetFloat(HeightBonusId, _field.HeightBonusCm * 0.01f);
            grassMaterial.SetFloat(GrassTimeId, _field.Clock);
            // Globals for the ground shader + the blade distance fade (blades sink into the ground-shaded grass).
            var q = Q;
            Shader.SetGlobalFloat(GrassTimeId, _field.Clock);
            Shader.SetGlobalMatrix(WorldToFieldId, transform.worldToLocalMatrix);
            Shader.SetGlobalVector(GrassFadeId, new Vector4(q.maxDrawDistance * 0.72f, q.maxDrawDistance, 0f, 0f));
            // The ground's uncut grass uses the blades' average colour, so near blades, the ground under them and the
            // field beyond the draw distance are one colour. Weights = the blade shader's palette mix; 0.9 = roots.
            Color avg = grassMaterial.GetColor("_ColYellowGreen") * 0.35f + grassMaterial.GetColor("_ColGoldenGreen") * 0.25f
                      + grassMaterial.GetColor("_ColDarkGreen") * 0.18f + grassMaterial.GetColor("_ColFreshGreen") * 0.22f;
            Shader.SetGlobalColor(PsGrassAvgId, avg * 0.9f);
            Color dry = grassMaterial.GetColor("_ColDry");
            Shader.SetGlobalVector(PsGrassDryId, new Vector4(dry.r * 0.9f, dry.g * 0.9f, dry.b * 0.9f, grassMaterial.GetFloat("_DryAmount")));
            Shader.SetGlobalFloat(GrassShowUncutId, MainMenu.IsOpen && untouchedLawnOnTitle ? 1f : 0f);

            // Cutting heads bend the grass around them (GPU only; no per-blade physics).
            int n = 0;
            float spin = 0f;
            var heads = GrassCutter.ActiveHeads;
            for (int i = 0; i < heads.Count && n < MaxCutters; i++)
            {
                var h = heads[i];
                _cutterShape[n] = DebugNoToolPress ? new Vector4(-1000f, 0f, 0f, 0f) : new Vector4(h.BottomY, h.Shaft.x, h.Shaft.y, h.Shaft.z);
                _cutterData[n++] = new Vector4(h.Position.x, h.Position.y, h.Position.z, h.Radius);
                spin = Mathf.Max(spin, h.Spin);
            }
            for (int i = n; i < MaxCutters; i++) { _cutterData[i] = new Vector4(0f, -1000f, 0f, 0f); _cutterShape[i] = new Vector4(-1000f, 0f, 0f, 0f); }
            grassMaterial.SetVectorArray(CutterShapeId, _cutterShape);
            grassMaterial.SetVectorArray(CuttersId, _cutterData);
            grassMaterial.SetFloat(CutterCountId, n);
            grassMaterial.SetFloat(SpinId, spin);
        }

        private void DrawChunks(Camera cam)
        {
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            var q = Q;
            Matrix4x4 m = transform.localToWorldMatrix;
            float tallest = _field.CurrentMaxHeightCm * 0.01f * 1.3f + 0.1f;
            float density = Mathf.Clamp(_field.Level.BladeDensity, 0.05f, 1f);
            Vector3 camPos = Vector3.zero;
            bool cull = cam != null;
            if (cull)
            {
                GeometryUtility.CalculateFrustumPlanes(cam, _frustum);
                camPos = cam.transform.position;
            }
            int layer = gameObject.layer;
            int drawn = 0, calls = 0;
            // Per-blade detail falloff (GrassBlade.hlsl): each chunk draws enough blades for its nearest point; blades
            // farther in the chunk collapse in the shader, so density changes smoothly, never chunk by chunk.
            grassMaterial.SetVector(GrassLodId, cull ? new Vector4(q.fullDetailDistance, q.midDetailDistance, q.maxDrawDistance, 0f) : Vector4.zero);
            grassMaterial.SetVector(GrassLodFracId, new Vector4(midDetailFraction, farDetailFraction, 0f, 0f));

            // Only the chunks within draw distance of the camera are visited (O(visible), not O(field)): on a big
            // map most of the field is drawn by the ground shader, not by blades.
            int x0 = 0, z0 = 0, x1 = _chunksX - 1, z1 = _chunksZ - 1;
            if (cull)
            {
                Vector3 local = transform.InverseTransformPoint(camPos);
                float reach = q.maxDrawDistance + tallest;
                x0 = Mathf.Max(0, Mathf.FloorToInt((local.x - reach) / chunkMeters));
                x1 = Mathf.Min(_chunksX - 1, Mathf.FloorToInt((local.x + reach) / chunkMeters));
                z0 = Mathf.Max(0, Mathf.FloorToInt((local.z - reach) / chunkMeters));
                z1 = Mathf.Min(_chunksZ - 1, Mathf.FloorToInt((local.z + reach) / chunkMeters));
            }

            var layout = _field.Layout;
            grassMaterial.SetVector(PsChunkGridId, new Vector4(chunkMeters, _chunksX, _chunksZ, bladeSeed));
            grassMaterial.SetVector(PsChunkGrid2Id, new Vector4(layout.SizeX, layout.SizeZ, _builtBladesPerM2, density));
            int S = Mathf.Max(1, farChunkGroup);
            for (int gz = z0 / S; gz <= z1 / S; gz++)
            for (int gx = x0 / S; gx <= x1 / S; gx++)
            {
                int ax = gx * S, az = gz * S;
                int bx = Mathf.Min(_chunksX - 1, ax + S - 1), bz = Mathf.Min(_chunksZ - 1, az + S - 1);
                if (S > 1 && cull)
                {
                    // A whole group far away (beyond mid detail and grass shadows): one draw for all its chunks.
                    var lb = new Bounds();
                    lb.SetMinMax(_chunks[az * _chunksX + ax].LocalMin - new Vector3(tallest, 0f, tallest),
                                 _chunks[bz * _chunksX + bx].LocalMax + new Vector3(tallest, tallest, tallest));
                    Bounds gw = TransformBounds(m, lb);
                    float gd = Mathf.Sqrt(gw.SqrDistance(camPos));
                    if (gd > q.maxDrawDistance) continue;
                    if (gd > q.midDetailDistance && gd > q.shadowCastDistance)
                    {
                        if (!GeometryUtility.TestPlanesAABB(_frustum, gw)) continue;
                        float subFull = chunkMeters * chunkMeters * _builtBladesPerM2 * density;
                        int per = Mathf.Max(1, Mathf.CeilToInt(subFull * LodFraction(q, gd)));
                        drawn += per * S * S;
                        calls++;
                        _mpb.SetVector(SuperChunkId, new Vector4(S, ax, az, 0f));
                        _mpb.SetFloat(ChunkCountId, 1f);
                        _mpb.SetFloat(LodWidthScaleId, 1f);
                        Graphics.DrawMeshInstancedProcedural(_bladeMeshFar, 0, grassMaterial, gw, per * S * S, _mpb, ShadowCastingMode.Off, true, layer, targetCamera);
                        continue;
                    }
                }
            for (int kz = az; kz <= bz; kz++)
            for (int kx = ax; kx <= bx; kx++)
            {
                ref Chunk c = ref _chunks[kz * _chunksX + kx];

                var localBounds = new Bounds();
                localBounds.SetMinMax(c.LocalMin - new Vector3(tallest, 0f, tallest), c.LocalMax + new Vector3(tallest, tallest, tallest));
                Bounds world = TransformBounds(m, localBounds);

                float fraction = 1f, dist = 0f;
                bool visible = true;
                Mesh mesh = _bladeMesh;
                if (cull)
                {
                    dist = Mathf.Sqrt(world.SqrDistance(camPos));
                    if (dist > q.maxDrawDistance) continue;
                    // Chunks behind the camera can still cast shadows into view: those are drawn shadow-only.
                    visible = GeometryUtility.TestPlanesAABB(_frustum, world);
                    if (!visible && dist > q.shadowCastDistance) continue;
                    fraction = LodFraction(q, dist);
                    if (dist > q.midDetailDistance) mesh = _bladeMeshFar;
                    else if (dist > q.fullDetailDistance) mesh = _bladeMeshMid;
                }

                float full = c.Count * density;
                int count = Mathf.Max(1, Mathf.CeilToInt(full * fraction));
                drawn += count;
                calls++;
                _mpb.SetVector(ChunkRectId, new Vector4(c.LocalMin.x, c.LocalMin.z, c.LocalMax.x - c.LocalMin.x, c.LocalMax.z - c.LocalMin.z));
                _mpb.SetFloat(ChunkSeedId, c.Seed);
                _mpb.SetFloat(ChunkCountId, full);
                _mpb.SetFloat(LodWidthScaleId, 1f);
                _mpb.SetVector(SuperChunkId, Vector4.zero);
                if (!visible && DebugNoBladeShadows) continue;
                var shadows = DebugNoBladeShadows ? ShadowCastingMode.Off : !visible ? ShadowCastingMode.ShadowsOnly
                    : dist < q.shadowCastDistance ? ShadowCastingMode.On : ShadowCastingMode.Off;
                Graphics.DrawMeshInstancedProcedural(mesh, 0, grassMaterial, world, count, _mpb, shadows, true, layer, targetCamera);
            }
            }
            LastDrawnBlades = drawn;
            LastDrawCalls = calls;
        }

        private QualityPreset ForTitle(QualityPreset q)
        {
            if (!titleScreenGrassProfile || !MainMenu.IsOpen) return q;
            // From the high orbiting title camera most of the lawn is far away: blades there are thinner than a pixel
            // and only sparkle (the grainy look). The title screen draws blades only near the camera; the ground shader
            // (the blades' average colour, anti-aliased grain) carries the rest: smooth, and a fraction of the cost.
            q.shadowCastDistance = 0f;
            q.fullDetailDistance = Mathf.Min(q.fullDetailDistance, 18f);
            q.midDetailDistance = Mathf.Min(q.midDetailDistance, 26f);
            q.maxDrawDistance = Mathf.Min(q.maxDrawDistance, titleBladeDistance);
            return q;
        }

        /// <summary>Fraction of blades kept at a distance (same curve as the shader's per-blade falloff).</summary>
        private float LodFraction(QualityPreset q, float d)
        {
            if (d <= q.fullDetailDistance) return 1f;
            float half = 0.5f * (q.fullDetailDistance + q.midDetailDistance);
            if (d <= half) return Mathf.Lerp(1f, midDetailFraction, (d - q.fullDetailDistance) / Mathf.Max(half - q.fullDetailDistance, 1e-3f));
            if (d <= q.midDetailDistance) return Mathf.Lerp(midDetailFraction, farDetailFraction, (d - half) / Mathf.Max(q.midDetailDistance - half, 1e-3f));
            return Mathf.Max(0.004f, farDetailFraction * q.midDetailDistance * q.midDetailDistance / (d * d));
        }

        private static Bounds TransformBounds(Matrix4x4 m, Bounds b)
        {
            Vector3 c = m.MultiplyPoint3x4(b.center);
            Vector3 e = b.extents;
            Vector3 ax = m.MultiplyVector(new Vector3(e.x, 0, 0));
            Vector3 ay = m.MultiplyVector(new Vector3(0, e.y, 0));
            Vector3 az = m.MultiplyVector(new Vector3(0, 0, e.z));
            Vector3 ext = new Vector3(
                Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x),
                Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y),
                Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
            return new Bounds(c, ext * 2f);
        }

        private void ReleaseBuffers()
        {
            _cellBuffer?.Release(); _cellBuffer = null;
        }

        private void OnEnable()
        {
            GraphicsProfile.Apply(quality);
            if (_field != null && _cellBuffer == null && grassMaterial != null)
            {
                EnsureCellBuffer();
                RebuildChunksIfNeeded();
                UploadAllTiles(); // the fresh buffer is empty
            }
        }

        private void OnDisable() => ReleaseBuffers();

        private void OnDestroy()
        {
            ReleaseBuffers();
            _mask?.Release();
            _mask = null;
            if (_bladeMesh != null) Destroy(_bladeMesh);
            if (_bladeMeshMid != null) Destroy(_bladeMeshMid);
            if (_bladeMeshFar != null) Destroy(_bladeMeshFar);
            if (_ownsMaterial && grassMaterial != null) Destroy(grassMaterial);
        }

        private void OnDrawGizmosSelected()
        {
            Vector2 size = _field != null ? new Vector2(_field.Layout.SizeX, _field.Layout.SizeZ) : new Vector2(50f, 50f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.3f, 1f, 0.3f, 0.8f);
            Gizmos.DrawWireCube(new Vector3(size.x * 0.5f, 0f, size.y * 0.5f), new Vector3(size.x, 0.01f, size.y));
        }
    }
}
