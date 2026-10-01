using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;

namespace Rvo
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class VolumePresenter : MonoBehaviour
    {
        public Material Material;
        public Camera ViewCamera;
        public bool ShowPaths, ShowSlice, ShowVelocityPlanes;
        public int SelectedAgent;
        [Range(0, 255)] public int SliceY = 128;
        private Mesh spheres, boxes, lines;
        private GameObject boxObject, lineObject;
        private Vector3[] vertices, unitSphere;
        private readonly List<Vector3> lineVertices = new List<Vector3>(16384);
        private readonly List<Color> lineColors = new List<Color>(16384);
        private readonly List<int> lineIndices = new List<int>(16384);
        private NavigationVolume map;
        public bool IsFollowing => ViewCamera != null && ViewCamera.TryGetComponent<VolumeCameraControls>(out var controls) && controls.Following;
        private const int Longitude = 8, Latitude = 6, Stride = (Longitude + 1) * (Latitude + 1);
        public void Initialize(NavigationVolume volume, in AgentReadView agents)
        {
            Clear(); map = volume;
            unitSphere = new Vector3[Stride];
            for (int y = 0; y <= Latitude; y++) for (int x = 0; x <= Longitude; x++)
            {
                float theta = Mathf.PI * y / Latitude, phi = 2 * Mathf.PI * x / Longitude;
                unitSphere[y * (Longitude + 1) + x] = new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi));
            }
            vertices = new Vector3[agents.Count * Stride]; var colors = new Color[vertices.Length];
            var triangles = new int[agents.Count * Latitude * Longitude * 6]; int t = 0;
            for (int i = 0; i < agents.Count; i++)
            {
                Color color = Color.HSVToRGB(Mathf.Repeat(agents.Ids[i] * 0.618034f, 1), 0.65f, 1);
                for (int v = 0; v < Stride; v++) colors[i * Stride + v] = color * (0.6f + 0.4f * Mathf.Max(0, Vector3.Dot(unitSphere[v], new Vector3(0.3f, 0.8f, -0.5f).normalized)));
                for (int y = 0; y < Latitude; y++) for (int x = 0; x < Longitude; x++)
                {
                    int a = i * Stride + y * (Longitude + 1) + x, b = a + Longitude + 1;
                    triangles[t++] = a; triangles[t++] = a + 1; triangles[t++] = b;
                    triangles[t++] = a + 1; triangles[t++] = b + 1; triangles[t++] = b;
                }
            }
            spheres = new Mesh { name = "XYZ agent spheres", indexFormat = IndexFormat.UInt32 }; spheres.MarkDynamic();
            spheres.vertices = vertices; spheres.colors = colors; spheres.triangles = triangles;
            GetComponent<MeshFilter>().sharedMesh = spheres; GetComponent<MeshRenderer>().sharedMaterial = Material;
            boxes = NewLines("Static volume", out boxObject); lines = NewLines("Volume debug", out lineObject); lines.MarkDynamic();
            BeginLines(); Box(map.Min, map.Max, new Color(0.25f, 0.4f, 0.55f));
            for (int i = 0; i < map.ObstacleCount; i++) { var b = map.Obstacle(i); Box(b.Min, b.Max, new Color(0.55f, 0.65f, 0.75f)); }
            Upload(boxes); Fit();
        }
        public void Fit()
        {
            if (ViewCamera == null || map == null) return;
            float size = map.Resolution * map.CellSize; Vector3 center = (map.Min + map.Max) / 2;
            ViewCamera.orthographic = false; ViewCamera.farClipPlane = size * 10;
            ViewCamera.transform.position = center + new Vector3(0.9f, 0.7f, -1.3f) * size;
            ViewCamera.transform.LookAt(center);
            if (ViewCamera.TryGetComponent<VolumeCameraControls>(out var controls)) { controls.StopFollowing(); controls.Pivot = center; }
        }
        public void Focus(in AgentReadView agents, int i)
        {
            if (ViewCamera == null) return; SelectedAgent = i; Vector3 p = agents.Positions[i];
            ViewCamera.transform.position = p - ViewCamera.transform.forward * math.max(15, agents.Parameters[i].Radius * 20);
            if (ViewCamera.TryGetComponent<VolumeCameraControls>(out var controls)) controls.BeginFollow(p);
        }
        public void StopFollowing() { if (ViewCamera != null && ViewCamera.TryGetComponent<VolumeCameraControls>(out var controls)) controls.StopFollowing(); }
        public void Present(in AgentReadView agents, VolumeNavigation navigation, VolumeAvoidanceSolver solver, SimulationWorld world)
        {
            var matrix = transform.worldToLocalMatrix;
            for (int i = 0; i < agents.Count; i++)
            {
                Vector3 position = agents.Positions[i]; float radius = agents.Parameters[i].Radius;
                for (int v = 0; v < Stride; v++) vertices[i * Stride + v] = matrix.MultiplyPoint3x4(position + unitSphere[v] * radius);
            }
            spheres.vertices = vertices; spheres.RecalculateBounds(); BeginLines();
            int selected = Mathf.Clamp(SelectedAgent, 0, agents.Count - 1);
            if (ViewCamera != null && ViewCamera.TryGetComponent<VolumeCameraControls>(out var controls) && controls.Following)
                controls.UpdateFollowTarget(agents.Positions[selected]);
            if (ShowPaths) for (int i = 0; i < agents.Count; i++) if (i < 16 || i == selected)
            {
                var path = navigation.Path(i); Color color = i == selected ? Color.yellow : new Color(0.2f, 0.65f, 0.6f);
                Vector3 previous = agents.Positions[i];
                for (int j = path.Cursor; j < path.Count; j++) { Vector3 point = navigation.Waypoint(i, j); Line(previous, point, color); previous = point; }
                Vector3 goal = agents.Goals[i]; float r = agents.Parameters[i].Radius * 1.5f;
                Line(goal - Vector3.right * r, goal + Vector3.right * r, color); Line(goal - Vector3.up * r, goal + Vector3.up * r, color);
                Line(goal - Vector3.forward * r, goal + Vector3.forward * r, color);
            }
            if (ShowSlice)
            {
                int3 c = (int3)math.floor((agents.Positions[selected] - map.Min) / map.CellSize);
                for (int z = math.max(0, c.z - 16); z < math.min(map.Resolution, c.z + 16); z++)
                    for (int x = math.max(0, c.x - 16); x < math.min(map.Resolution, c.x + 16); x++)
                    {
                        int index = map.Index(new int3(x, Mathf.Clamp(SliceY, 0, map.Resolution - 1), z));
                        Vector3 p = map.Center(index); Color color = map.Component(index) < 0 ? Color.red : Color.HSVToRGB(Mathf.Repeat(map.Component(index) * 0.31f, 1), 0.5f, 0.7f);
                        float h = map.CellSize * 0.4f; Line(p - Vector3.right * h, p + Vector3.right * h, color);
                    }
            }
            if (ShowVelocityPlanes && world.Tick > 0)
            {
                var debug = solver.CopyDebug(world, selected);
                Vector3 origin = (Vector3)debug.Position + Vector3.up * agents.Parameters[selected].Radius * 12;
                float speed = agents.Parameters[selected].MaxSpeed;
                // Explicit velocity-space inset, translated above selected agent. Units: 1 world unit = 1 m/s.
                Line(origin, origin + (Vector3)debug.Preferred, Color.cyan); Line(origin, origin + (Vector3)debug.Candidate, Color.magenta);
                Line(origin, origin + (Vector3)debug.Final, Color.green);
                for (int i = 0; i < math.min(16, debug.Planes.Length); i++)
                {
                    var plane = debug.Planes[i]; if (math.abs(plane.Offset) > speed) continue;
                    Vector3 center = origin + (Vector3)(plane.Normal * plane.Offset);
                    Vector3 u = OrcaGeometry3D.Side(plane.Normal), v = math.cross(plane.Normal, (float3)u);
                    u *= speed * 0.35f; v *= speed * 0.35f; Color color = plane.IsStatic ? Color.white : Color.red;
                    Line(center - u - v, center + u - v, color); Line(center + u - v, center + u + v, color);
                    Line(center + u + v, center - u + v, color); Line(center - u + v, center - u - v, color);
                    Line(center, center + (Vector3)plane.Normal * 2, color);
                }
                for (int i = 0; i < debug.Neighbors.Length; i++) Line(debug.Position, debug.Neighbors[i], new Color(0.6f, 0.3f, 0.8f));
            }
            if (lineVertices.Count > 0 || lines.vertexCount > 0) Upload(lines);
        }
        private Mesh NewLines(string label, out GameObject child)
        {
            child = new GameObject(label); child.transform.SetParent(transform, false);
            var mesh = new Mesh { name = label, indexFormat = IndexFormat.UInt32 };
            child.AddComponent<MeshFilter>().sharedMesh = mesh; child.AddComponent<MeshRenderer>().sharedMaterial = Material; return mesh;
        }
        private void BeginLines() { lineVertices.Clear(); lineColors.Clear(); lineIndices.Clear(); }
        private void Line(Vector3 a, Vector3 b, Color color)
        {
            lineIndices.Add(lineVertices.Count); lineIndices.Add(lineVertices.Count + 1);
            lineVertices.Add(transform.InverseTransformPoint(a)); lineVertices.Add(transform.InverseTransformPoint(b)); lineColors.Add(color); lineColors.Add(color);
        }
        private void Box(Vector3 low, Vector3 high, Color color)
        {
            for (int corner = 0; corner < 8; corner++) for (int axis = 0; axis < 3; axis++) if ((corner & (1 << axis)) == 0)
            {
                Vector3 a = new Vector3((corner & 1) == 0 ? low.x : high.x, (corner & 2) == 0 ? low.y : high.y, (corner & 4) == 0 ? low.z : high.z);
                Vector3 b = a; b[axis] = high[axis]; Line(a, b, color);
            }
        }
        private void Upload(Mesh mesh) { mesh.Clear(); mesh.SetVertices(lineVertices); mesh.SetColors(lineColors); mesh.SetIndices(lineIndices, MeshTopology.Lines, 0); mesh.RecalculateBounds(); }
        public void Clear()
        {
            StopFollowing();
            if (spheres != null) Destroy(spheres); if (boxes != null) Destroy(boxes); if (lines != null) Destroy(lines);
            if (boxObject != null) Destroy(boxObject); if (lineObject != null) Destroy(lineObject);
            spheres = boxes = lines = null; map = null;
        }
        private void OnDestroy() { Clear(); }
    }
}
