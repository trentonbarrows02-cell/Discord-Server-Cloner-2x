using UnityEngine;
using UnityEngine.Rendering;

namespace PlayShift.Grass
{
    /// <summary>
    /// Draws the lawn with GPU procedural instancing. No GameObject or Mesh per blade/unit exists:
    ///  - one static GPU buffer holds every blade (field position, owning cell, random seed);
    ///  - one GPU buffer holds a height per logical cell (0 = cut), updated only for the dirty slice;
    ///  - the shader builds, bends and sways each blade from those two buffers.
    /// The same buffers are reused for all five levels; a new level only re-uploads cell heights.
    /// Blades are grouped into chunks for frustum culling and distance LOD (a uniform prefix subset
    /// of each chunk is drawn farther away, with wider blades so coverage stays dense).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GrassFieldRenderer : MonoBehaviour
    {
        private const int BladeStride = sizeof(float) * 4;

        [Tooltip("Material using the PlayShift/InstancedGrass shader. Assign it so the shader is included in builds.")]
        [SerializeField] private Material grassMaterial;

        [Header("Density")]
        [Tooltip("Blades drawn per logical cell at full detail. 250x250 cells x 8 = 500k blades.")]
        [Range(1, 32)] [SerializeField] private int bladesPerCell = 8;
        [Tooltip("Cells per chunk edge for culling/LOD.")]
        [Range(4, 64)] [SerializeField] private int chunkCells = 25;
        [SerializeField] private int bladeSeed = 777;
        [Range(3, 10)] [SerializeField] private int bladeSegments = 6;

        [Header("Distance LOD")]
        [SerializeField] private float fullDetailDistance = 20f;
        [SerializeField] private float midDetailDistance = 45f;
        [Range(0.05f, 1f)] [SerializeField] private float midDetailFraction = 0.5f;
        [Range(0.02f, 1f)] [SerializeField] private float farDetailFraction = 0.2f;
        [SerializeField] private float maxDrawDistance = 160f;

        [Header("Rendering")]
        [SerializeField] private Camera targetCamera;
        [SerializeField] private ShadowCastingMode shadowCasting = ShadowCastingMode.Off;
        [Tooltip("Scene directional light used for grass shading (defaults to RenderSettings.sun or the first directional light).")]
        [SerializeField] private Light sunLight;
        [SerializeField] private Color ambientColor = new Color(0.42f, 0.48f, 0.55f);

        private GrassField _field;
        private Mesh _bladeMesh;
        private GraphicsBuffer _bladeBuffer;
        private GraphicsBuffer _cellBuffer;
        private float[] _cellHeights;
        private Chunk[] _chunks;
        private MaterialPropertyBlock _mpb;
        private readonly Plane[] _frustum = new Plane[6];
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
        private static readonly int CellHeightsId = Shader.PropertyToID("_CellHeights");
        private static readonly int FieldToWorldId = Shader.PropertyToID("_FieldToWorld");
        private static readonly int InstanceOffsetId = Shader.PropertyToID("_InstanceOffset");
        private static readonly int LodWidthScaleId = Shader.PropertyToID("_LodWidthScale");
        private static readonly int HeightClampId = Shader.PropertyToID("_HeightClamp");
        private static readonly int LightDirId = Shader.PropertyToID("_GrassLightDir");
        private static readonly int LightColorId = Shader.PropertyToID("_GrassLightColor");
        private static readonly int AmbientId = Shader.PropertyToID("_GrassAmbient");

        public GrassField Field => _field;
        public int TotalBlades => _bladeBuffer != null ? _bladeBuffer.count : 0;

        /// <summary>Binds the logical field. Call once; levels are switched by resetting the same field.</summary>
        public void Bind(GrassField field)
        {
            _field = field;
            if (!SystemInfo.supportsComputeShaders && SystemInfo.graphicsShaderLevel < 45)
            {
                Debug.LogError("[PlayShift Grass] This GPU does not support structured buffers (shader model 4.5); grass cannot be drawn.", this);
                enabled = false;
                return;
            }
            EnsureMaterial();
            RebuildIfLayoutChanged();
            UploadAllCells();
        }

        private void EnsureMaterial()
        {
            if (grassMaterial != null) return;
            var shader = Shader.Find("PlayShift/InstancedGrass");
            if (shader == null)
            {
                Debug.LogError("[PlayShift Grass] No grass material assigned and shader 'PlayShift/InstancedGrass' was not found. " +
                               "Assign a material using that shader (PlayShift > Grass > Create Level Setup does this).", this);
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
            if (_bladeBuffer != null && _builtCellsX == layout.CellsX && _builtCellsZ == layout.CellsZ &&
                _builtBladesPerCell == bladesPerCell && Mathf.Approximately(_builtSizeX, layout.SizeX) &&
                Mathf.Approximately(_builtSizeZ, layout.SizeZ))
                return;

            ReleaseBuffers();
            if (_bladeMesh == null) _bladeMesh = GrassBladeMesh.Create(bladeSegments);

            int cellsX = layout.CellsX, cellsZ = layout.CellsZ;
            int chunksX = Mathf.CeilToInt(cellsX / (float)chunkCells);
            int chunksZ = Mathf.CeilToInt(cellsZ / (float)chunkCells);
            float csx = layout.CellSizeX, csz = layout.CellSizeZ;

            var blades = new Vector4[cellsX * cellsZ * bladesPerCell];
            _chunks = new Chunk[chunksX * chunksZ];
            var rng = new GrassRandom(bladeSeed);
            int write = 0;

            // Blades are written chunk by chunk, then shuffled inside each chunk so that any prefix of a
            // chunk is a uniform sample of it: drawing the first N instances is a valid LOD.
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
                            for (int b = 0; b < bladesPerCell; b++)
                            {
                                float px = (cx + rng.NextFloat()) * csx;
                                float pz = (cz + rng.NextFloat()) * csz;
                                blades[write++] = new Vector4(px, pz, cell, rng.NextFloat());
                            }
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
            _cellBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cellsX * cellsZ, sizeof(float));
            _cellHeights = new float[cellsX * cellsZ];

            grassMaterial.SetBuffer(BladesId, _bladeBuffer);
            grassMaterial.SetBuffer(CellHeightsId, _cellBuffer);

            _builtCellsX = cellsX; _builtCellsZ = cellsZ; _builtBladesPerCell = bladesPerCell;
            _builtSizeX = layout.SizeX; _builtSizeZ = layout.SizeZ;
        }

        private void UploadAllCells()
        {
            if (_field == null || _cellBuffer == null) return;
            _field.TryConsumeDirtyRange(out _, out _);
            _field.CopyRenderHeightsMeters(_cellHeights, 0, _cellHeights.Length);
            _cellBuffer.SetData(_cellHeights);
        }

        private void LateUpdate()
        {
            if (_field == null || _field.Level == null || _bladeBuffer == null || grassMaterial == null) return;

            // Only the slice of cells touched since last frame goes to the GPU (a mower pass is a few KB).
            if (_field.TryConsumeDirtyRange(out int start, out int count))
            {
                _field.CopyRenderHeightsMeters(_cellHeights, start, count);
                _cellBuffer.SetData(_cellHeights, start, start, count);
            }

            var cam = targetCamera != null ? targetCamera : Camera.main;
            UpdateShaderGlobals();
            DrawChunks(cam);
        }

        private void UpdateShaderGlobals()
        {
            grassMaterial.SetMatrix(FieldToWorldId, transform.localToWorldMatrix);
            float minM = _field.CurrentMinHeightCm * 0.01f, maxM = _field.CurrentMaxHeightCm * 0.01f;
            grassMaterial.SetVector(HeightClampId, new Vector4(minM, maxM, 0f, 0f));

            var sun = sunLight != null ? sunLight : FindSun();
            if (sun != null)
            {
                Vector3 toLight = -sun.transform.forward;
                grassMaterial.SetVector(LightDirId, new Vector4(toLight.x, toLight.y, toLight.z, 0f));
                Color c = sun.color.linear * sun.intensity;
                grassMaterial.SetVector(LightColorId, new Vector4(c.r, c.g, c.b, 1f));
            }
            else
            {
                grassMaterial.SetVector(LightDirId, new Vector4(0.3f, 0.85f, 0.4f, 0f));
                grassMaterial.SetVector(LightColorId, Vector4.one);
            }
            Color a = ambientColor.linear;
            grassMaterial.SetVector(AmbientId, new Vector4(a.r, a.g, a.b, 1f));
        }

        private Light FindSun()
        {
            if (RenderSettings.sun != null) { sunLight = RenderSettings.sun; return sunLight; }
#if UNITY_2023_1_OR_NEWER
            var lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
#else
            var lights = FindObjectsOfType<Light>();
#endif
            foreach (var l in lights)
                if (l.type == LightType.Directional && l.isActiveAndEnabled) { sunLight = l; return l; }
            return null;
        }

        private void DrawChunks(Camera cam)
        {
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            Matrix4x4 m = transform.localToWorldMatrix;
            float tallest = _field.CurrentMaxHeightCm * 0.01f * 1.2f + 0.05f;
            Vector3 camPos = Vector3.zero;
            bool cull = cam != null;
            if (cull)
            {
                GeometryUtility.CalculateFrustumPlanes(cam, _frustum);
                camPos = cam.transform.position;
            }
            int layer = gameObject.layer;

            for (int i = 0; i < _chunks.Length; i++)
            {
                ref Chunk c = ref _chunks[i];
                if (c.Count == 0) continue;

                var localBounds = new Bounds();
                localBounds.SetMinMax(c.LocalMin - new Vector3(tallest, 0f, tallest), c.LocalMax + new Vector3(tallest, tallest, tallest));
                Bounds world = TransformBounds(m, localBounds);

                float fraction = 1f;
                if (cull)
                {
                    if (!GeometryUtility.TestPlanesAABB(_frustum, world)) continue;
                    float dist = Mathf.Sqrt(world.SqrDistance(camPos));
                    if (dist > maxDrawDistance) continue;
                    if (dist > midDetailDistance) fraction = farDetailFraction;
                    else if (dist > fullDetailDistance) fraction = midDetailFraction;
                }

                int count = Mathf.Max(1, Mathf.CeilToInt(c.Count * fraction));
                _mpb.SetFloat(InstanceOffsetId, c.Offset);
                // Fewer, wider blades far away keep the same ground coverage.
                _mpb.SetFloat(LodWidthScaleId, Mathf.Min(2.5f, 1f / Mathf.Sqrt(fraction)));
                Graphics.DrawMeshInstancedProcedural(_bladeMesh, 0, grassMaterial, world, count, _mpb, shadowCasting, false, layer, targetCamera);
            }
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
            // Buffers are released on disable (e.g. domain reload); rebuild them when re-enabled.
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
