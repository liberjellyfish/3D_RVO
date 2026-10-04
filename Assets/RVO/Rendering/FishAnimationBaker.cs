using UnityEngine;

namespace Rvo.Rendering
{
    public enum FishAnimationMode { Procedural, VertexTexture, BoneTexture }
    public enum FishFarMode { Mesh, CrossQuads, CrossTriangles, Billboard }

    /// <summary>同一波形、网格与相位的隔离对照。纹理由 renderer 持有，不是每条鱼的资产。</summary>
    public static class FishAnimationBaker
    {
        public const int Frames = 64, Bones = 24;

        public static Vector2 Wave(float z, float phase)
        {
            float s = Mathf.Clamp01((1 - z) / 2.3f), angle = 7 * s - phase;
            return new Vector2(s * s * Mathf.Sin(angle), -(2 * s * Mathf.Sin(angle) + 7 * s * s * Mathf.Cos(angle)) / 2.3f);
        }

        public static Texture2D Bake(Mesh mesh, FishAnimationMode mode)
        {
            if (mode == FishAnimationMode.Procedural) return null;
            bool vat = mode == FishAnimationMode.VertexTexture;
            int width = vat ? mesh.vertexCount : Bones * 3;
            var pixels = new Color[width * Frames];
            var vertices = mesh.vertices;
            for (int frame = 0; frame < Frames; frame++)
            {
                float phase = frame * 2 * Mathf.PI / Frames;
                for (int item = 0; item < (vat ? vertices.Length : Bones); item++)
                {
                    float z = vat ? vertices[item].z : Mathf.Lerp(1, -1.3f, item / (float)(Bones - 1));
                    var wave = Wave(z, phase);
                    if (vat)
                    {
                        // 本鱼形仅沿 X 变形；保存位移与导数，精确重建 inverse-transpose normal。
                        pixels[frame * width + item] = new Color(wave.x, wave.y, 0, 0);
                    }
                    else
                    {
                        // 局部仿射骨架近似波形；不是刚体骨骼/DCC 动画性能的替代证据。
                        int offset = frame * width + item * 3;
                        pixels[offset] = new Color(1, 0, wave.y, wave.x - wave.y * z);
                        pixels[offset + 1] = new Color(0, 1, 0, 0);
                        pixels[offset + 2] = new Color(0, 0, 1, 0);
                    }
                }
            }
            var texture = new Texture2D(width, Frames, vat ? TextureFormat.RGHalf : TextureFormat.RGBAHalf, false, true)
            { name = "Fish " + mode, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels(pixels); texture.Apply(false, true);
            return texture;
        }

        public static Mesh CreateCard(FishFarMode mode)
        {
            bool triangle = mode == FishFarMode.CrossTriangles;
            int planes = mode == FishFarMode.Billboard ? 1 : 3, perPlane = triangle ? 3 : 4;
            var vertices = new Vector3[planes * perPlane]; var normals = new Vector3[vertices.Length];
            var indices = new int[planes * (triangle ? 3 : 6)];
            for (int plane = 0; plane < planes; plane++)
            {
                float angle = plane * Mathf.PI / 3;
                var side = new Vector3(Mathf.Sin(angle), Mathf.Cos(angle), 0);
                int start = plane * perPlane, index = plane * (triangle ? 3 : 6);
                vertices[start] = side * -0.72f + Vector3.forward * -1.3f;
                vertices[start + 1] = side * 0.72f + Vector3.forward * -1.3f;
                vertices[start + 2] = side * (triangle ? 0 : 0.72f) + Vector3.forward * 1.15f;
                if (!triangle) vertices[start + 3] = side * -0.72f + Vector3.forward * 1.15f;
                for (int i = 0; i < perPlane; i++) normals[start + i] = Vector3.Cross(side, Vector3.forward);
                indices[index] = start; indices[index + 1] = start + 1; indices[index + 2] = start + 2;
                if (!triangle) { indices[index + 3] = start; indices[index + 4] = start + 2; indices[index + 5] = start + 3; }
            }
            var mesh = new Mesh { name = "Experimental fish " + mode, vertices = vertices, normals = normals, triangles = indices };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 3.4f); return mesh;
        }
    }
}
