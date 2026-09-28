using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Rvo
{
    // 深度优先扁平 BVH；Escape 指向整个子树之后，查询无需递归或临时栈，后续可扩为 float3。
    public struct ObstacleNode
    {
        public float2 Min, Max;
        public int Escape, Leaf;
    }

    public static class ObstacleBvh
    {
        internal static ObstacleNode[] Build(int width, int height, float cellSize, float2 origin, bool[] occupied)
        {
            var used = new bool[occupied.Length]; var rectangles = new List<ObstacleNode>();
            // 将相邻占据格合为不重叠矩形，减少静态约束、树叶和绘制顶点。
            for (int z = 0; z < height; z++) for (int x = 0; x < width; x++)
            {
                int index = z * width + x; if (!occupied[index] || used[index]) continue;
                int w = 1, h = 1;
                while (x + w < width && occupied[index + w] && !used[index + w]) w++;
                while (z + h < height)
                {
                    bool full = true;
                    for (int dx = 0; dx < w; dx++) if (!occupied[(z + h) * width + x + dx] || used[(z + h) * width + x + dx]) { full = false; break; }
                    if (!full) break; h++;
                }
                for (int dz = 0; dz < h; dz++) for (int dx = 0; dx < w; dx++) used[(z + dz) * width + x + dx] = true;
                rectangles.Add(new ObstacleNode { Min = origin + new float2(x, z) * cellSize,
                    Max = origin + new float2(x + w, z + h) * cellSize, Leaf = 1 });
            }
            if (rectangles.Count == 0) return Array.Empty<ObstacleNode>();
            var leaves = rectangles.ToArray(); var nodes = new List<ObstacleNode>(leaves.Length * 2 - 1);
            BuildRange(leaves, 0, leaves.Length, nodes); return nodes.ToArray();
        }
        private static void BuildRange(ObstacleNode[] leaves, int start, int count, List<ObstacleNode> nodes)
        {
            int index = nodes.Count; float2 low = leaves[start].Min, high = leaves[start].Max;
            for (int i = start + 1; i < start + count; i++) { low = math.min(low, leaves[i].Min); high = math.max(high, leaves[i].Max); }
            nodes.Add(default);
            if (count > 1)
            {
                int axis = high.x - low.x >= high.y - low.y ? 0 : 1;
                Array.Sort(leaves, start, count, axis == 0 ? XComparer.Instance : ZComparer.Instance);
                int middle = count / 2; BuildRange(leaves, start, middle, nodes); BuildRange(leaves, start + middle, count - middle, nodes);
            }
            nodes[index] = new ObstacleNode { Min = low, Max = high, Escape = nodes.Count, Leaf = count == 1 ? 1 : 0 };
        }
        private sealed class XComparer : IComparer<ObstacleNode>
        { public static readonly XComparer Instance = new XComparer(); public int Compare(ObstacleNode a, ObstacleNode b) => CompareAxis(a, b, 0); }
        private sealed class ZComparer : IComparer<ObstacleNode>
        { public static readonly ZComparer Instance = new ZComparer(); public int Compare(ObstacleNode a, ObstacleNode b) => CompareAxis(a, b, 1); }
        private static int CompareAxis(ObstacleNode a, ObstacleNode b, int axis)
        {
            int order = (a.Min[axis] + a.Max[axis]).CompareTo(b.Min[axis] + b.Max[axis]);
            if (order != 0) return order;
            order = a.Min.x.CompareTo(b.Min.x); if (order != 0) return order;
            order = a.Min.y.CompareTo(b.Min.y); if (order != 0) return order;
            order = a.Max.x.CompareTo(b.Max.x); return order != 0 ? order : a.Max.y.CompareTo(b.Max.y);
        }

        public static bool SegmentBox(float2 a, float2 b, float2 low, float2 high, out float enter)
        {
            enter = 0; float exit = 1; float2 delta = b - a;
            for (int axis = 0; axis < 2; axis++)
            {
                if (math.abs(delta[axis]) < 1e-8f)
                { if (a[axis] < low[axis] || a[axis] > high[axis]) return false; }
                else
                {
                    float t1 = (low[axis] - a[axis]) / delta[axis], t2 = (high[axis] - a[axis]) / delta[axis];
                    enter = math.max(enter, math.min(t1, t2)); exit = math.min(exit, math.max(t1, t2));
                    if (enter > exit) return false;
                }
            }
            return true;
        }
    }
}
