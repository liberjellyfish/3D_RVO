using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rvo
{
    /// <summary>一个障碍 Mesh，一个线段 Mesh；地图/路径读取后立即复制，不保留快照。</summary>
    public sealed class GridMapPresenter : MonoBehaviour
    {
        public Material Material;
        public bool ShowPaths;
        public bool ShowGoals = true;
        [Min(1)] public int MaxDisplayedPaths = 32;
        [Min(0)] public int SelectedAgent;
        private Mesh obstacleMesh, lineMesh;
        private GameObject obstacleObject, lineObject;
        private int version = -1;
        private uint lastGoalHash;
        private bool lastShowPaths, lastShowGoals;
        private float lastHeight;
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Color> colors = new List<Color>();
        private readonly List<int> indices = new List<int>();

        public void Present(GridNavigation navigation, in AgentReadView agents, float height)
        {
            if (obstacleMesh == null)
            {
                obstacleMesh = Create("Navigation obstacles", out obstacleObject);
                lineMesh = Create("Navigation paths and goals", out lineObject);
            }
            var map = navigation.Map;
            bool mapChanged = version != map.Version;
            if (mapChanged)
            {
                ResetBuffers();
                for (int nodeIndex = 0; nodeIndex < map.NodeCount; nodeIndex++)
                {
                    var node = map.Node(nodeIndex); if (node.Leaf == 0) continue;
                    float2 low = node.Min, high = node.Max; int start = vertices.Count;
                    Add(low, height - 0.02f, new Color(0.35f, 0.43f, 0.52f));
                    Add(new float2(low.x, high.y), height - 0.02f, new Color(0.35f, 0.43f, 0.52f));
                    Add(high, height - 0.02f, new Color(0.35f, 0.43f, 0.52f));
                    Add(new float2(high.x, low.y), height - 0.02f, new Color(0.35f, 0.43f, 0.52f));
                    indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
                    indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
                }
                Upload(obstacleMesh, MeshTopology.Triangles); version = map.Version;
            }
            uint goalHash = 2166136261;
            for (int i = 0; i < agents.Count; i++)
                unchecked { goalHash = (goalHash ^ math.hash(new float4(agents.Goals[i],agents.Parameters[i].Radius)) ^ (uint)agents.Ids[i]) * 16777619; }
            // 无路径显示时目标/网格都是静态几何，不再每个渲染帧重建并上传 Mesh。
            if (!mapChanged && !ShowPaths && !lastShowPaths && lineMesh.vertexCount > 0 && goalHash == lastGoalHash &&
                lastShowGoals == ShowGoals && lastHeight == height) return;
            lastGoalHash = goalHash; lastShowPaths = ShowPaths; lastShowGoals = ShowGoals; lastHeight = height;
            ResetBuffers();
            Color gridColor = new Color(0.1f, 0.15f, 0.2f);
            // 大地图只画主网格，避免 512 条细线淹没 Agent；障碍 Mesh 已合并成矩形。
            int gridStep = math.max(1, math.max(map.Width, map.Height) / 32);
            for (int x = 0; x <= map.Width; x += gridStep) Line(map.Min + new float2(x * map.CellSize, 0),
                new float2(map.Min.x + x * map.CellSize, map.Max.y), height - 0.04f, gridColor);
            for (int z = 0; z <= map.Height; z += gridStep) Line(map.Min + new float2(0, z * map.CellSize),
                new float2(map.Max.x, map.Min.y + z * map.CellSize), height - 0.04f, gridColor);
            for (int i = 0; i < agents.Count; i++)
            {
                Color color = Color.HSVToRGB(Mathf.Repeat(agents.Ids[i] * 0.618034f, 1), 0.6f, 0.65f);
                var path = navigation.Path(i); float2 previous = agents.Positions[i].xz;
                if (ShowPaths && (i < MaxDisplayedPaths || i == SelectedAgent) && path.MapVersion == map.Version && path.Status == GridPathStatus.Ready)
                    for (int point = path.Cursor; point < path.Count; point++)
                    { float2 next = navigation.Waypoint(i, point); Line(previous, next, height - 0.01f, color); previous = next; }
                float2 goal = agents.Goals[i].xz;
                if (!ShowGoals) continue;
                float radius = agents.Parameters[i].Radius, cross = radius * 0.95f;
                float layer = height + math.max(0.04f,radius*0.04f);
                // 白色描边 + 深色饱和内芯；十字高于圆盘，不再与 agent 共面闪烁或被遮盖。
                Color goalColor = Color.HSVToRGB(Mathf.Repeat(agents.Ids[i]*0.618034f,1),0.9f,0.38f);
                Line(goal-new float2(cross,0),goal+new float2(cross,0),layer,Color.white,radius*0.26f);
                Line(goal-new float2(0,cross),goal+new float2(0,cross),layer,Color.white,radius*0.26f);
                Line(goal-new float2(cross,0),goal+new float2(cross,0),layer+0.01f,goalColor,radius*0.15f);
                Line(goal-new float2(0,cross),goal+new float2(0,cross),layer+0.01f,goalColor,radius*0.15f);
            }
            Upload(lineMesh, MeshTopology.Triangles);
        }
        private Mesh Create(string label, out GameObject child)
        {
            child = new GameObject(label); child.transform.SetParent(transform, false);
            var mesh = new Mesh { name = label, indexFormat = IndexFormat.UInt32 }; mesh.MarkDynamic();
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            child.AddComponent<MeshRenderer>().sharedMaterial = Material; return mesh;
        }
        private void ResetBuffers() { vertices.Clear(); colors.Clear(); indices.Clear(); }
        private void Add(float2 point, float height, Color color)
        { vertices.Add(transform.InverseTransformPoint(new Vector3(point.x, height, point.y))); colors.Add(color); }
        private void Line(float2 a, float2 b, float height, Color color, float width = 0.05f)
        {
            float2 d = math.normalizesafe(b-a); float2 side = new float2(-d.y,d.x)*width*0.5f;
            int start = vertices.Count;
            Add(a-side,height,color); Add(a+side,height,color); Add(b+side,height,color); Add(b-side,height,color);
            indices.Add(start); indices.Add(start+1); indices.Add(start+2);
            indices.Add(start); indices.Add(start+2); indices.Add(start+3);
        }
        private void Upload(Mesh mesh, MeshTopology topology)
        { mesh.Clear(); mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetIndices(indices, topology, 0); mesh.RecalculateBounds(); }
        public void Clear()
        {
            if (obstacleMesh != null) Destroy(obstacleMesh); if (lineMesh != null) Destroy(lineMesh);
            if (obstacleObject != null) Destroy(obstacleObject); if (lineObject != null) Destroy(lineObject);
            obstacleMesh = lineMesh = null; obstacleObject = lineObject = null; version = -1;
        }
        private void OnDestroy() { Clear(); }
    }
}
