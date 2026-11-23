using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Builds the single template blade every instance shares. Only the UVs matter: uv.x is the side
    /// (0 = left edge, 1 = right edge) and uv.y the normalized position along the blade. The shader turns
    /// those into a curved, tapered, wind-blown blade, so one segmented mesh serves every grass height.
    /// </summary>
    public static class GrassBladeMesh
    {
        public static Mesh Create(int segments)
        {
            segments = Mathf.Clamp(segments, 2, 16);
            int vertCount = segments * 2 + 1;
            var verts = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];
            var tris = new int[(segments - 1) * 6 + 3];

            for (int s = 0; s < segments; s++)
            {
                // Denser rows near the tip, where the curve bends most.
                float t = 1f - Mathf.Pow(1f - s / (float)segments, 1.35f);
                verts[s * 2] = new Vector3(-0.5f, t, 0f);
                verts[s * 2 + 1] = new Vector3(0.5f, t, 0f);
                uvs[s * 2] = new Vector2(0f, t);
                uvs[s * 2 + 1] = new Vector2(1f, t);
            }
            verts[vertCount - 1] = new Vector3(0f, 1f, 0f);
            uvs[vertCount - 1] = new Vector2(0.5f, 1f);

            int k = 0;
            for (int s = 0; s < segments - 1; s++)
            {
                int a = s * 2, b = a + 1, c = a + 2, d = a + 3;
                tris[k++] = a; tris[k++] = c; tris[k++] = b;
                tris[k++] = b; tris[k++] = c; tris[k++] = d;
            }
            int last = (segments - 1) * 2;
            tris[k++] = last; tris[k++] = vertCount - 1; tris[k] = last + 1;

            var mesh = new Mesh { name = "PlayShiftGrassBlade" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            // Real bounds come from the draw call; keep these generous so nothing culls the template.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
            mesh.UploadMeshData(true);
            return mesh;
        }
    }
}
