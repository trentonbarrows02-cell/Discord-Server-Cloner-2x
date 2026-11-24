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
        // Blades are drawn only near the camera; beyond maxDrawDistance they fade into the ground shader
        // (PlayShift/GrassGround), which shows grown/cut grass from the same cell data at almost no cost.
        [SerializeField] private QualityPreset low = new QualityPreset { bladesPerSquareMeter = 380, fullDetailDistance = 8f, midDetailDistance = 18f, maxDrawDistance = 32f, shadowCastDistance = 0f };
        [SerializeField] private QualityPreset medium = new QualityPreset { bladesPerSquareMeter = 600, fullDetailDistance = 10f, midDetailDistance = 22f, maxDrawDistance = 42f, shadowCastDistance = 6f };
        [SerializeField] private QualityPreset high = new QualityPreset { bladesPerSquareMeter = 950, fullDetailDistance = 13f, midDetailDistance = 28f, maxDrawDistance = 55f, shadowCastDistance = 10f };
        [SerializeField] private QualityPreset ultra = new QualityPreset { bladesPerSquareMeter = 1500, fullDetailDistance = 18f, midDetailDistance = 38f, maxDrawDistance = 75f, shadowCastDistance = 16f };
        [Range(0.05f, 1f)] [SerializeField] private float midDetailFraction = 0.45f;
        [Range(0.02f, 1f)] [SerializeField] private float farDetailFraction = 0.2f;

        [Header("Layout")]
        [Tooltip("Chunk edge in metres for culling/LOD (independent of the cutting grid).")]
        [Range(0.5f, 8f)] [SerializeField] private float chunkMeters = 2f;
        [SerializeField] private int bladeSeed = 777;
        [Range(3, 10)] [SerializeField] private int bladeSegments = 6;

        [Header("Streaming")]
        [Tooltip("Most changed 32x32-cell tiles uploaded per frame (8 KB each). Bursts beyond this spread over the next frames.")]
        [Range(8, 1024)] [SerializeField] private int maxTileUploadsPerFrame = 128;

        [Header("Rendering")]
        [SerializeField] private Camera targetCamera;

        private GrassField _field;
        private Mesh _bladeMesh;
        private GraphicsBuffer _cellBuffer;
        private float[] _tileScratch;
        private int[] _tileList;
        private Chunk[] _chunks;
        private MaterialPropertyBlock _mpb;
        private readonly Plane[] _frustum = new Plane[6];
        private readonly Vector4[] _cutterData = new Vector4[MaxCutters];
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
                    case GrassQuality.Low: return low;
                    case GrassQuality.Medium: return medium;
                    case GrassQuality.Ultra: return ultra;
                    default: return high;
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

            var cam = targetCamera != null ? targetCamera : Camera.main;
            UpdateShaderGlobals();
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

            // Cutting heads bend the grass around them (GPU only; no per-blade physics).
            int n = 0;
            float spin = 0f;
            var heads = GrassCutter.ActiveHeads;
            for (int i = 0; i < heads.Count && n < MaxCutters; i++)
            {
                var h = heads[i];
                _cutterData[n++] = new Vector4(h.Position.x, h.Position.y, h.Position.z, h.Radius);
                spin = Mathf.Max(spin, h.Spin);
            }
            for (int i = n; i < MaxCutters; i++) _cutterData[i] = new Vector4(0f, -1000f, 0f, 0f);
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

            for (int kz = z0; kz <= z1; kz++)
            for (int kx = x0; kx <= x1; kx++)
            {
                ref Chunk c = ref _chunks[kz * _chunksX + kx];

                var localBounds = new Bounds();
                localBounds.SetMinMax(c.LocalMin - new Vector3(tallest, 0f, tallest), c.LocalMax + new Vector3(tallest, tallest, tallest));
                Bounds world = TransformBounds(m, localBounds);

                float fraction = 1f, dist = 0f;
                if (cull)
                {
                    dist = Mathf.Sqrt(world.SqrDistance(camPos));
                    if (dist > q.maxDrawDistance) continue;
                    // Chunks behind the camera can still cast shadows into view, so only frustum-cull distant ones.
                    if (!GeometryUtility.TestPlanesAABB(_frustum, world) && dist > q.shadowCastDistance) continue;
                    if (dist > q.midDetailDistance) fraction = farDetailFraction;
                    else if (dist > q.fullDetailDistance) fraction = midDetailFraction;
                }

                int count = Mathf.Max(1, Mathf.CeilToInt(c.Count * density * fraction));
                drawn += count;
                calls++;
                _mpb.SetVector(ChunkRectId, new Vector4(c.LocalMin.x, c.LocalMin.z, c.LocalMax.x - c.LocalMin.x, c.LocalMax.z - c.LocalMin.z));
                _mpb.SetFloat(ChunkSeedId, c.Seed);
                // Fewer, wider blades far away keep the same ground coverage.
                _mpb.SetFloat(LodWidthScaleId, Mathf.Min(2.5f, 1f / Mathf.Sqrt(fraction)));
                var shadows = dist < q.shadowCastDistance ? ShadowCastingMode.On : ShadowCastingMode.Off;
                Graphics.DrawMeshInstancedProcedural(_bladeMesh, 0, grassMaterial, world, count, _mpb, shadows, true, layer, targetCamera);
            }
            LastDrawnBlades = drawn;
            LastDrawCalls = calls;
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
            if (_bladeMesh != null) Destroy(_bladeMesh);
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
