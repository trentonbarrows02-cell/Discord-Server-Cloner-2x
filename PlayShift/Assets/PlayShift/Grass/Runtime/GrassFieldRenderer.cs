using System.Collections.Generic;
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
    /// Draws the lawn with GPU procedural instancing. No GameObject or Mesh per blade/unit exists:
    ///  - one static GPU buffer holds every blade (field position, owning cell, random seed);
    ///  - one GPU buffer holds per cell (grown height, cut time); only the dirty slice is uploaded;
    ///  - the shader (GrassBlade.hlsl) builds, bends, sways and cut-animates each blade.
    /// Blades are grouped into chunks for frustum/distance culling and LOD: a chunk's blades are shuffled,
    /// so drawing the first N is a uniform subsample. Density per level and quality preset scale N.
    /// The same buffers serve all five levels; a new level only re-uploads the cells.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GrassFieldRenderer : MonoBehaviour
    {
        private const int BladeStride = sizeof(float) * 4;
        private const int CellStride = sizeof(float) * 2;
        private const int MaxCutters = 4;

        [System.Serializable]
        public struct QualityPreset
        {
            [Range(1, 32)] public int bladesPerCell;
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
        [SerializeField] private QualityPreset low = new QualityPreset { bladesPerCell = 4, fullDetailDistance = 9f, midDetailDistance = 22f, maxDrawDistance = 90f, shadowCastDistance = 0f };
        [SerializeField] private QualityPreset medium = new QualityPreset { bladesPerCell = 6, fullDetailDistance = 12f, midDetailDistance = 30f, maxDrawDistance = 120f, shadowCastDistance = 7f };
        [SerializeField] private QualityPreset high = new QualityPreset { bladesPerCell = 10, fullDetailDistance = 16f, midDetailDistance = 40f, maxDrawDistance = 160f, shadowCastDistance = 12f };
        [SerializeField] private QualityPreset ultra = new QualityPreset { bladesPerCell = 16, fullDetailDistance = 24f, midDetailDistance = 55f, maxDrawDistance = 200f, shadowCastDistance = 20f };
        [Range(0.05f, 1f)] [SerializeField] private float midDetailFraction = 0.45f;
        [Range(0.02f, 1f)] [SerializeField] private float farDetailFraction = 0.2f;

        [Header("Layout")]
        [Tooltip("Cells per chunk edge for culling/LOD.")]
        [Range(4, 64)] [SerializeField] private int chunkCells = 20;
        [SerializeField] private int bladeSeed = 777;
        [Range(3, 10)] [SerializeField] private int bladeSegments = 6;

        [Header("Rendering")]
        [SerializeField] private Camera targetCamera;

        private GrassField _field;
        private Mesh _bladeMesh;
        private GraphicsBuffer _bladeBuffer;
        private GraphicsBuffer _cellBuffer;
        private float[] _cellScratch;
        private Vector2[] _cellData;
        private Chunk[] _chunks;
        private MaterialPropertyBlock _mpb;
        private readonly Plane[] _frustum = new Plane[6];
        private readonly Vector4[] _cutterData = new Vector4[MaxCutters];
        private int _builtCellsX, _builtCellsZ, _builtBladesPerCell;
        private float _builtSizeX, _builtSizeZ;
        private bool _ownsMaterial;

        private struct Chunk
        {
            public int Offset;
            public int Count;
            public Vector3 LocalMin;
            public Vector3 LocalMax;
        }

        private static readonly int BladesId = Shader.PropertyToID("_Blades");
        private static readonly int CellsId = Shader.PropertyToID("_Cells");
        private static readonly int FieldToWorldId = Shader.PropertyToID("_FieldToWorld");
        private static readonly int InstanceOffsetId = Shader.PropertyToID("_InstanceOffset");
        private static readonly int LodWidthScaleId = Shader.PropertyToID("_LodWidthScale");
        private static readonly int HeightClampId = Shader.PropertyToID("_HeightClamp");
        private static readonly int GrassTimeId = Shader.PropertyToID("_GrassTime");
        private static readonly int CuttersId = Shader.PropertyToID("_Cutters");
        private static readonly int CutterCountId = Shader.PropertyToID("_CutterCount");
        private static readonly int SpinId = Shader.PropertyToID("_Spin");

        public GrassField Field => _field;
        public int TotalBlades => _bladeBuffer != null ? _bladeBuffer.count : 0;
        public int LastDrawnBlades { get; private set; }

        public GrassQuality Quality
        {
            get => quality;
            set
            {
                if (quality == value) return;
                quality = value;
                if (_field != null) { RebuildIfLayoutChanged(); UploadAllCells(); }
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
            RebuildIfLayoutChanged();
            UploadAllCells();
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

        private void RebuildIfLayoutChanged()
        {
            if (_field == null || grassMaterial == null) return;
            var layout = _field.Layout;
            int bpc = Q.bladesPerCell;
            if (_bladeBuffer != null && _builtCellsX == layout.CellsX && _builtCellsZ == layout.CellsZ &&
                _builtBladesPerCell == bpc && Mathf.Approximately(_builtSizeX, layout.SizeX) &&
                Mathf.Approximately(_builtSizeZ, layout.SizeZ))
                return;

            ReleaseBuffers();
            if (_bladeMesh == null) _bladeMesh = GrassBladeMesh.Create(bladeSegments);

            int cellsX = layout.CellsX, cellsZ = layout.CellsZ;
            int chunksX = Mathf.CeilToInt(cellsX / (float)chunkCells);
            int chunksZ = Mathf.CeilToInt(cellsZ / (float)chunkCells);
            float csx = layout.CellSizeX, csz = layout.CellSizeZ;

            var blades = new Vector4[cellsX * cellsZ * bpc];
            _chunks = new Chunk[chunksX * chunksZ];
            var rng = new GrassRandom(bladeSeed);
            int write = 0;
            for (int kz = 0; kz < chunksZ; kz++)
                for (int kx = 0; kx < chunksX; kx++)
                {
                    int start = write;
                    int x0 = kx * chunkCells, x1 = Mathf.Min(cellsX, x0 + chunkCells);
                    int z0 = kz * chunkCells, z1 = Mathf.Min(cellsZ, z0 + chunkCells);
                    for (int cz = z0; cz < z1; cz++)
                        for (int cx = x0; cx < x1; cx++)
                        {
                            int cell = cz * cellsX + cx;
                            for (int b = 0; b < bpc; b++)
                                blades[write++] = new Vector4((cx + rng.NextFloat()) * csx, (cz + rng.NextFloat()) * csz, cell, rng.NextFloat());
                        }
                    for (int i = write - 1; i > start; i--)
                    {
                        int j = start + rng.NextInt(i - start + 1);
                        var t = blades[i]; blades[i] = blades[j]; blades[j] = t;
                    }
                    _chunks[kz * chunksX + kx] = new Chunk
                    {
                        Offset = start,
                        Count = write - start,
                        LocalMin = new Vector3(x0 * csx, 0f, z0 * csz),
                        LocalMax = new Vector3(x1 * csx, 0f, z1 * csz),
                    };
                }

            _bladeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, blades.Length, BladeStride);
            _bladeBuffer.SetData(blades);
            int n = cellsX * cellsZ;
            _cellBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, CellStride);
            _cellScratch = new float[n * 2];
            _cellData = new Vector2[n];

            grassMaterial.SetBuffer(BladesId, _bladeBuffer);
            grassMaterial.SetBuffer(CellsId, _cellBuffer);

            _builtCellsX = cellsX; _builtCellsZ = cellsZ; _builtBladesPerCell = bpc;
            _builtSizeX = layout.SizeX; _builtSizeZ = layout.SizeZ;
        }

        private void UploadAllCells()
        {
            if (_field == null || _cellBuffer == null) return;
            _field.TryConsumeDirtyRange(out _, out _);
            UploadRange(0, _cellData.Length);
        }

        private void UploadRange(int start, int count)
        {
            _field.CopyRenderCells(_cellScratch, start, count);
            for (int i = start, end = start + count; i < end; i++)
                _cellData[i] = new Vector2(_cellScratch[2 * i], _cellScratch[2 * i + 1]);
            _cellBuffer.SetData(_cellData, start, start, count);
        }

        private void LateUpdate()
        {
            if (_field == null || _field.Level == null || _bladeBuffer == null || grassMaterial == null) return;

            // Only the slice of cells touched since last frame goes to the GPU.
            if (_field.TryConsumeDirtyRange(out int start, out int count))
                UploadRange(start, count);

            var cam = targetCamera != null ? targetCamera : Camera.main;
            UpdateShaderGlobals();
            DrawChunks(cam);
        }

        private void UpdateShaderGlobals()
        {
            grassMaterial.SetMatrix(FieldToWorldId, transform.localToWorldMatrix);
            float minM = _field.CurrentMinHeightCm * 0.01f, maxM = _field.CurrentMaxHeightCm * 0.01f;
            grassMaterial.SetVector(HeightClampId, new Vector4(minM, maxM, 0f, 0f));
            grassMaterial.SetFloat(GrassTimeId, _field.Clock);

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
            int drawn = 0;

            for (int i = 0; i < _chunks.Length; i++)
            {
                ref Chunk c = ref _chunks[i];
                if (c.Count == 0) continue;

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
                _mpb.SetFloat(InstanceOffsetId, c.Offset);
                // Fewer, wider blades far away keep the same ground coverage.
                _mpb.SetFloat(LodWidthScaleId, Mathf.Min(2.5f, 1f / Mathf.Sqrt(fraction)));
                var shadows = dist < q.shadowCastDistance ? ShadowCastingMode.On : ShadowCastingMode.Off;
                Graphics.DrawMeshInstancedProcedural(_bladeMesh, 0, grassMaterial, world, count, _mpb, shadows, true, layer, targetCamera);
            }
            LastDrawnBlades = drawn;
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
            _bladeBuffer?.Release(); _bladeBuffer = null;
            _cellBuffer?.Release(); _cellBuffer = null;
        }

        private void OnEnable()
        {
            if (_field != null && _bladeBuffer == null)
            {
                RebuildIfLayoutChanged();
                UploadAllCells();
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
