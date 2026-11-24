using System.Collections.Generic;
using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Fine, GPU-only mask of where the grass looks cut (R8 render texture over the lawn, ~2.4 cm per texel on a
    /// 100 m lawn at 4096). The trimmer's strokes are drawn into it as exact swept circles every frame, so cut paths
    /// are round and smooth instead of following the 10 cm logical cells. Cells changed by anything else (gifts adding
    /// or removing grass, a new level, a loaded save) are copied in from the cell grid. The grass counter is untouched:
    /// it still counts cells. No CPU readback; only small GL quads are drawn, and only where something changed.
    /// </summary>
    public sealed class GrassCutMask
    {
        private static readonly int SegId = Shader.PropertyToID("_Seg");
        private static readonly int MaskParamsId = Shader.PropertyToID("_MaskParams");
        private static readonly int ValueId = Shader.PropertyToID("_Value");
        private static readonly int GlobalMaskId = Shader.PropertyToID("_CutMask");
        private static readonly int GlobalParamsId = Shader.PropertyToID("_CutMaskParams");

        private RenderTexture _rt;
        private Material _mat;
        private Texture2D _cellTex;
        private byte[] _cellBytes;
        private Vector2 _size;
        private int _cellsX, _cellsZ;
        private readonly List<int> _scratch = new List<int>();

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
                name = "GrassCutMask", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false,
            };
            _rt.Create();
            _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _cellTex = new Texture2D(_cellsX, _cellsZ, TextureFormat.R8, false, true)
            {
                name = "GrassCutCells", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
            };
            _cellBytes = new byte[_cellsX * _cellsZ];
            Shader.SetGlobalTexture(GlobalMaskId, _rt);
            Shader.SetGlobalVector(GlobalParamsId, new Vector4(1f / _size.x, 1f / _size.y, 1f, 0f));
            return true;
        }

        /// <summary>Whole mask from the cell grid (new level, loaded save, fresh mask).</summary>
        public void RebuildFromCells(GrassField field)
        {
            if (_rt == null) return;
            for (int i = 0; i < _cellBytes.Length; i++) _cellBytes[i] = field.IsCellCut(i) ? (byte)255 : (byte)0;
            _cellTex.SetPixelData(_cellBytes, 0);
            _cellTex.Apply(false, false);
            Graphics.Blit(_cellTex, _rt, _mat, 1);
        }

        /// <summary>
        /// Per frame: copies cells changed by gifts into the mask (whole cells), then draws this frame's trimmer strokes
        /// (exact swept circles). Strokes go last so a cut made this frame is never undone by a gift in the same frame.
        /// </summary>
        public void Update(GrassField field, IReadOnlyList<GrassCutter.CutStroke> strokes)
        {
            if (_rt == null) return;
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

        /// <summary>Changed cells as whole-cell quads, merged into row runs of equal state (a gift patch is a few
        /// hundred quads, not tens of thousands).</summary>
        private void StampCells(GrassField field)
        {
            _scratch.Sort();
            var prev = RenderTexture.active;
            Graphics.SetRenderTarget(_rt);
            GL.PushMatrix();
            GL.LoadOrtho();
            float cx = 1f / _cellsX, cz = 1f / _cellsZ;
            for (int pass = 0; pass < 2; pass++)
            {
                float value = pass == 0 ? 0f : 1f;
                _mat.SetFloat(ValueId, value);
                _mat.SetPass(2);
                GL.Begin(GL.QUADS);
                int k = 0;
                while (k < _scratch.Count)
                {
                    int i = _scratch[k];
                    bool cut = field.IsCellCut(i);
                    if (cut != (pass == 1)) { k++; continue; }
                    int row = i / _cellsX, x0 = i % _cellsX, x1 = x0;
                    int j = k + 1;
                    while (j < _scratch.Count)
                    {
                        int n = _scratch[j];
                        if (n == i) { j++; continue; } // duplicates
                        if (n / _cellsX != row || n % _cellsX != x1 + 1 || field.IsCellCut(n) != cut) break;
                        x1++; i = n; j++;
                    }
                    QuadNoBegin(x0 * cx, row * cz, (x1 + 1) * cx, (row + 1) * cz);
                    k = j;
                }
                GL.End();
            }
            GL.PopMatrix();
            RenderTexture.active = prev;
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
