using System.Collections.Generic;
using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Fine, GPU-only mask of where the grass looks cut (R8 render texture over the lawn, ~2.4 cm per texel on a
    /// 100 m lawn at 4096). The trimmer's strokes are drawn into it as exact swept circles every frame, so cut paths
    /// are round and smooth instead of following the 10 cm logical cells. Cells changed by anything else (gifts adding
    /// or removing grass, a new level, a loaded save) are rebuilt from the cell grid with a smooth reconstruction
    /// (Gaussian over the nearest cells, then a soft threshold), so they never show the grid's 10 cm stair steps. The
    /// grass counter is untouched: it still counts cells. No CPU readback; only small GL quads are drawn, and only
    /// where something changed.
    /// </summary>
    public sealed class GrassCutMask
    {
        private static readonly int SegId = Shader.PropertyToID("_Seg");
        private static readonly int MaskParamsId = Shader.PropertyToID("_MaskParams");
        private static readonly int ValueId = Shader.PropertyToID("_Value");
        private static readonly int GlobalMaskId = Shader.PropertyToID("_CutMask");
        private static readonly int GlobalParamsId = Shader.PropertyToID("_CutMaskParams");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int OldMaskId = Shader.PropertyToID("_OldMask");
        private const int TileCells = 32;
        private const int SeamCells = 3; // reconstruction reaches 2 cells; one more for the soft seam

        private RenderTexture _rt;
        private Material _mat;
        private Texture2D _cellTex;
        private byte[] _cellBytes;
        private Vector2 _size;
        private int _cellsX, _cellsZ;
        private readonly List<int> _scratch = new List<int>();
        private readonly HashSet<int> _tiles = new HashSet<int>();
        // Mip levels let the ground show cut paths smoothly far away (no stair steps or shimmer). Regenerated after
        // changes, at most every MipInterval seconds (blades read level 0, which is always current).
        private const float MipInterval = 0.15f;
        private bool _mipsDirty;
        private float _lastMips = -1f;

        public RenderTexture Texture => _rt;
        public int Width => _rt != null ? _rt.width : 0;
        public int Height => _rt != null ? _rt.height : 0;
        /// <summary>Strokes drawn last frame (for tests/profiling).</summary>
        public int LastStrokes { get; private set; }
        public int LastExternalCells { get; private set; }

        public bool Create(GrassField field, int resolution)
        {
            Release();
            var shader = Shader.Find("Hidden/PlayShift/CutMask");
            if (shader == null || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8))
            {
                Debug.LogWarning("[PlayShift Grass] Cut mask unavailable (shader or R8 render textures missing); cut edges follow the cells.");
                return false;
            }
            var layout = field.Layout;
            _size = new Vector2(layout.SizeX, layout.SizeZ);
            _cellsX = layout.CellsX; _cellsZ = layout.CellsZ;
            int w = Mathf.Clamp(resolution, 256, 8192);
            int h = Mathf.Clamp(Mathf.RoundToInt(w * layout.SizeZ / layout.SizeX), 256, 8192);
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear)
            {
                name = "GrassCutMask", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = true, autoGenerateMips = false,
            };
            _rt.Create();
            _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            // R = cut, G = changed by a gift since the last stamp. Read at exact cell centres (point sampling).
            _cellTex = new Texture2D(_cellsX, _cellsZ, TextureFormat.RG16, false, true)
            {
                name = "GrassCutCells", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
            };
            _cellBytes = new byte[_cellsX * _cellsZ * 2];
            Shader.SetGlobalTexture(GlobalMaskId, _rt);
            Shader.SetGlobalVector(GlobalParamsId, new Vector4(1f / _size.x, 1f / _size.y, 1f, 0f));
            return true;
        }

        /// <summary>Whole mask from the cell grid (new level, loaded save, fresh mask), smoothly reconstructed.</summary>
        public void RebuildFromCells(GrassField field)
        {
            if (_rt == null) return;
            int n = _cellsX * _cellsZ;
            for (int i = 0; i < n; i++) { _cellBytes[2 * i] = field.IsCellCut(i) ? (byte)255 : (byte)0; _cellBytes[2 * i + 1] = 0; }
            _cellTex.SetPixelData(_cellBytes, 0);
            _cellTex.Apply(false, false);
            Graphics.Blit(_cellTex, _rt, _mat, 1);
            _rt.GenerateMips();
            _mipsDirty = false;
        }

        /// <summary>
        /// Per frame: blends cells changed by gifts into the mask (smoothly), then draws this frame's trimmer strokes
        /// (exact swept circles). Strokes go last so a cut made this frame is never undone by a gift in the same frame.
        /// </summary>
        public void Update(GrassField field, IReadOnlyList<GrassCutter.CutStroke> strokes)
        {
            if (_rt == null) return;
            UpdateMask(field, strokes);
            float now = Time.unscaledTime;
            if (_mipsDirty && (now - _lastMips >= MipInterval || _lastMips < 0f))
            {
                _rt.GenerateMips();
                _lastMips = now;
                _mipsDirty = false;
            }
        }

        private void UpdateMask(GrassField field, IReadOnlyList<GrassCutter.CutStroke> strokes)
        {
            if (field.MaskRebuildNeeded) { RebuildFromCells(field); field.AcknowledgeMaskRebuild(); }
            LastExternalCells = 0;
            if (field.ExternalCellChanges.Count > 0)
            {
                _scratch.Clear();
                _scratch.AddRange(field.ExternalCellChanges);
                field.ClearExternalCellChanges();
                LastExternalCells = _scratch.Count;
                StampCells(field);
            }
            LastStrokes = strokes.Count;
            if (strokes.Count == 0) return;
            _mipsDirty = true;

            var prev = RenderTexture.active;
            Graphics.SetRenderTarget(_rt);
            GL.PushMatrix();
            GL.LoadOrtho();
            float aa = _size.x / _rt.width;
            for (int i = 0; i < strokes.Count; i++)
            {
                var s = strokes[i];
                float r = s.Radius + aa;
                _mat.SetVector(SegId, new Vector4(s.A.x, s.A.y, s.B.x, s.B.y));
                _mat.SetVector(MaskParamsId, new Vector4(s.Radius, aa, _size.x, _size.y));
                _mat.SetPass(0);
                float x0 = (Mathf.Min(s.A.x, s.B.x) - r) / _size.x, x1 = (Mathf.Max(s.A.x, s.B.x) + r) / _size.x;
                float z0 = (Mathf.Min(s.A.y, s.B.y) - r) / _size.y, z1 = (Mathf.Max(s.A.y, s.B.y) + r) / _size.y;
                Quad(x0, z0, x1, z1);
            }
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        /// <summary>
        /// Cells changed by gifts: marked in the cell texture (G), then the smooth reconstruction is blended over the old
        /// mask on the 32x32-cell tiles they touch (plus a small seam), only where cells changed. Gift patches get round,
        /// smooth edges; trimmer-cut edges next to them keep their exact circles.
        /// </summary>
        private void StampCells(GrassField field)
        {
            _tiles.Clear();
            int tilesX = (_cellsX + TileCells - 1) / TileCells;
            for (int k = 0; k < _scratch.Count; k++)
            {
                int i = _scratch[k];
                _cellBytes[2 * i] = field.IsCellCut(i) ? (byte)255 : (byte)0;
                _cellBytes[2 * i + 1] = 255;
                _tiles.Add((i / _cellsX / TileCells) * tilesX + (i % _cellsX) / TileCells);
            }
            // The trimmer's cuts never go through these bytes: refresh the cut state around the stamped tiles so the
            // reconstruction sees today's neighbours, not the state of the last rebuild.
            foreach (int t in _tiles)
            {
                int x0 = Mathf.Max(0, (t % tilesX) * TileCells - SeamCells - 2), z0 = Mathf.Max(0, (t / tilesX) * TileCells - SeamCells - 2);
                int x1 = Mathf.Min(_cellsX, (t % tilesX + 1) * TileCells + SeamCells + 2), z1 = Mathf.Min(_cellsZ, (t / tilesX + 1) * TileCells + SeamCells + 2);
                for (int z = z0; z < z1; z++)
                    for (int x = x0; x < x1; x++)
                    {
                        int i = z * _cellsX + x;
                        _cellBytes[2 * i] = field.IsCellCut(i) ? (byte)255 : (byte)0;
                    }
            }
            _cellTex.SetPixelData(_cellBytes, 0);
            _cellTex.Apply(false, false);

            var old = RenderTexture.GetTemporary(_rt.width, _rt.height, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
            Graphics.CopyTexture(_rt, 0, 0, old, 0, 0); // level 0 only (the mask has mips, the copy doesn't)
            _mat.SetTexture(MainTexId, _cellTex);
            _mat.SetTexture(OldMaskId, old);
            var prev = RenderTexture.active;
            Graphics.SetRenderTarget(_rt);
            GL.PushMatrix();
            GL.LoadOrtho();
            _mat.SetPass(3);
            GL.Begin(GL.QUADS);
            float cx = 1f / _cellsX, cz = 1f / _cellsZ;
            foreach (int t in _tiles)
            {
                int x0 = (t % tilesX) * TileCells - SeamCells, z0 = (t / tilesX) * TileCells - SeamCells;
                int x1 = x0 + TileCells + 2 * SeamCells, z1 = z0 + TileCells + 2 * SeamCells;
                QuadNoBegin(Mathf.Max(0, x0) * cx, Mathf.Max(0, z0) * cz, Mathf.Min(_cellsX, x1) * cx, Mathf.Min(_cellsZ, z1) * cz);
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(old);
            _mipsDirty = true;
            // These cells are now in the mask; clear their "changed" mark for the next stamp (uploaded with it).
            for (int k = 0; k < _scratch.Count; k++) _cellBytes[2 * _scratch[k] + 1] = 0;
        }

        private static void Quad(float x0, float z0, float x1, float z1)
        {
            GL.Begin(GL.QUADS);
            QuadNoBegin(x0, z0, x1, z1);
            GL.End();
        }

        private static void QuadNoBegin(float x0, float z0, float x1, float z1)
        {
            GL.TexCoord2(x0, z0); GL.Vertex3(x0, z0, 0f);
            GL.TexCoord2(x0, z1); GL.Vertex3(x0, z1, 0f);
            GL.TexCoord2(x1, z1); GL.Vertex3(x1, z1, 0f);
            GL.TexCoord2(x1, z0); GL.Vertex3(x1, z0, 0f);
        }

        public void Release()
        {
            Shader.SetGlobalVector(GlobalParamsId, Vector4.zero); // shaders fall back to the cells
            if (_rt != null) { _rt.Release(); Object.Destroy(_rt); _rt = null; }
            if (_mat != null) { Object.Destroy(_mat); _mat = null; }
            if (_cellTex != null) { Object.Destroy(_cellTex); _cellTex = null; }
        }
    }
}
