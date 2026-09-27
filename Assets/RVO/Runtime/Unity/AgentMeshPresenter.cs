using UnityEngine;
using UnityEngine.Rendering;

namespace Rvo
{
    /// <summary>所有圆盘共享一个动态 Mesh；不为每个 Agent 创建 GameObject。</summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class AgentMeshPresenter : MonoBehaviour, ISimulationPresenter
    {
        public Material AgentMaterial;
        public Camera ViewCamera;
        private const int Segments = 16;
        private Mesh mesh;
        private Vector3[] vertices;
        private readonly Vector3[] circle = new Vector3[Segments];

        public void Initialize(in AgentReadView agents)
        {
            Release();
            for (int v = 0; v < Segments; v++)
            {
                float angle = v * 2 * Mathf.PI / Segments;
                circle[v] = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            }
            int stride = Segments + 1;
            vertices = new Vector3[agents.Count * stride];
            var colors = new Color[vertices.Length];
            var triangles = new int[agents.Count * Segments * 3];
            for (int i = 0; i < agents.Count; i++)
            {
                var color = Color.HSVToRGB(Mathf.Repeat(agents.Ids[i] * 0.618034f, 1), 0.65f, 1);
                for (int v = 0; v < stride; v++) colors[i * stride + v] = color;
                for (int v = 0; v < Segments; v++)
                {
                    int start = (i * Segments + v) * 3;
                    triangles[start] = i * stride;
                    triangles[start + 1] = i * stride + 1 + v;
                    triangles[start + 2] = i * stride + 1 + (v + 1) % Segments;
                }
            }
            mesh = new Mesh { name = "RVO agent discs", indexFormat = IndexFormat.UInt32 };
            mesh.MarkDynamic(); mesh.vertices = vertices; mesh.colors = colors; mesh.triangles = triangles;
            GetComponent<MeshFilter>().sharedMesh = mesh;
            GetComponent<MeshRenderer>().sharedMaterial = AgentMaterial;
            Present(agents);
            if (ViewCamera != null)
            {
                var bounds = new Bounds((Vector3)agents.Positions[0], Vector3.zero);
                for (int i = 0; i < agents.Count; i++)
                {
                    bounds.Encapsulate((Vector3)agents.Positions[i]);
                    bounds.Encapsulate((Vector3)agents.Goals[i]);
                }
                ViewCamera.transform.position = bounds.center + Vector3.up * 50f;
                ViewCamera.transform.rotation = Quaternion.Euler(90, 0, 0);
                ViewCamera.orthographicSize = Mathf.Max(5,
                    Mathf.Max(bounds.extents.z, bounds.extents.x / ViewCamera.aspect) * 1.4f + 2);
            }
        }

        public void Present(in AgentReadView agents)
        {
            var worldToLocal = transform.worldToLocalMatrix;
            for (int i = 0; i < agents.Count; i++)
            {
                Vector3 center = agents.Positions[i];
                int start = i * (Segments + 1);
                vertices[start] = worldToLocal.MultiplyPoint3x4(center);
                for (int v = 0; v < Segments; v++)
                {
                    Vector3 point = center + circle[v] * agents.Parameters[i].Radius;
                    vertices[start + v + 1] = worldToLocal.MultiplyPoint3x4(point);
                }
            }
            mesh.vertices = vertices;
            mesh.RecalculateBounds();
        }

        private void OnDestroy() { Release(); }
        private void Release()
        {
            if (mesh != null) Destroy(mesh);
            mesh = null;
        }
    }
}
