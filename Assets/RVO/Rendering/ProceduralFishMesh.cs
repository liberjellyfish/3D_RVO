using System.Collections.Generic;
using UnityEngine;

namespace Rvo.Rendering
{
    public static class ProceduralFishMesh
    {
        // 四档都使用同一程序化体型；VAT / impostor 属于后续阶段。
        public static Mesh Create(int lod)
        {
            int[] rings = { 16, 12, 8, 4 }, sides = { 12, 10, 8, 6 };
            if (lod < 0 || lod > 3) throw new System.ArgumentOutOfRangeException(nameof(lod));
            int r = rings[lod], s = sides[lod];
            var vertices = new List<Vector3>(r * s + 8);
            var indices = new List<int>();
            for (int j = 0; j < r; j++)
            {
                float t = j / (float)(r - 1), z = Mathf.Lerp(1, -0.85f, t);
                // sin(PI) 的浮点误差可能略小于零，分数次幂前截断以免产生 NaN 顶点。
                float width = 0.035f + 0.29f * Mathf.Pow(Mathf.Max(0, Mathf.Sin(t * Mathf.PI)), 1.2f) * (1 - t * 0.35f);
                for (int k = 0; k < s; k++)
                {
                    float angle = k * Mathf.PI * 2 / s;
                    vertices.Add(new Vector3(Mathf.Cos(angle) * width, Mathf.Sin(angle) * width * 1.6f, z));
                }
            }
            for (int j = 0; j < r - 1; j++) for (int k = 0; k < s; k++)
            {
                int a = j * s + k, b = j * s + (k + 1) % s, c = a + s, d = b + s;
                indices.Add(a); indices.Add(c); indices.Add(b); indices.Add(b); indices.Add(c); indices.Add(d);
            }
            // 端面用已有顶点封口，不创建额外的逐鱼对象。
            for (int k = 1; k < s - 1; k++)
            {
                indices.Add(0); indices.Add(k); indices.Add(k + 1);
                int end = (r - 1) * s; indices.Add(end); indices.Add(end + k + 1); indices.Add(end + k);
            }
            AddFin(vertices, indices, new Vector3(0, 0.06f, -0.78f), new Vector3(0, -0.06f, -0.78f),
                new Vector3(0, -0.48f, -1.3f), new Vector3(0, 0.48f, -1.3f));
            AddFin(vertices, indices, new Vector3(0, 0.25f, 0.4f), new Vector3(0, 0.64f, 0.08f),
                new Vector3(0, 0.5f, -0.45f), new Vector3(0, 0.22f, -0.6f));
            var mesh = new Mesh { name = $"Procedural fish LOD{lod}" };
            mesh.SetVertices(vertices); mesh.SetTriangles(indices, 0); mesh.RecalculateNormals();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 3.4f);
            return mesh;
        }

        private static void AddFin(List<Vector3> vertices, List<int> indices, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int start = vertices.Count; vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
            indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
            indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
        }
    }
}
