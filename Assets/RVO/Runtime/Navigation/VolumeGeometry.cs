using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace Rvo
{
    [Serializable]
    public struct VolumeBox
    {
        public float3 Min, Max;
        public VolumeBox(float3 min, float3 max) { Min = min; Max = max; }
    }
    public struct VolumeBvhNode
    {
        public float3 Min, Max;
        public int Escape, Obstacle;
    }

    public static class VolumeGeometry
    {
        public static bool SegmentBox(float3 a, float3 b, float3 low, float3 high, out float enter)
        {
            enter = 0; float exit = 1; float3 d = b - a;
            for (int axis = 0; axis < 3; axis++)
            {
                if (math.abs(d[axis]) < 1e-8f)
                { if (a[axis] < low[axis] || a[axis] > high[axis]) return false; }
                else
                {
                    float u = (low[axis] - a[axis]) / d[axis], v = (high[axis] - a[axis]) / d[axis];
                    enter = math.max(enter, math.min(u, v)); exit = math.min(exit, math.max(u, v));
                    if (enter > exit) return false;
                }
            }
            return true;
        }
        internal static VolumeBvhNode[] Build(VolumeBox[] boxes)
        {
            if (boxes.Length == 0) return Array.Empty<VolumeBvhNode>();
            var ids = new int[boxes.Length]; for (int i = 0; i < ids.Length; i++) ids[i] = i;
            var nodes = new List<VolumeBvhNode>(2 * boxes.Length - 1);
            BuildRange(boxes, ids, 0, ids.Length, nodes); return nodes.ToArray();
        }
        private static void BuildRange(VolumeBox[] boxes, int[] ids, int first, int count, List<VolumeBvhNode> nodes)
        {
            int index = nodes.Count; float3 low = boxes[ids[first]].Min, high = boxes[ids[first]].Max;
            for (int i = first + 1; i < first + count; i++)
            { low = math.min(low, boxes[ids[i]].Min); high = math.max(high, boxes[ids[i]].Max); }
            nodes.Add(default);
            if (count > 1)
            {
                float3 size = high - low; int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
                Array.Sort(ids, first, count, Comparer<int>.Create((a, b) =>
                { int c = (boxes[a].Min[axis] + boxes[a].Max[axis]).CompareTo(boxes[b].Min[axis] + boxes[b].Max[axis]); return c != 0 ? c : a.CompareTo(b); }));
                int half = count / 2; BuildRange(boxes, ids, first, half, nodes); BuildRange(boxes, ids, first + half, count - half, nodes);
            }
            nodes[index] = new VolumeBvhNode { Min = low, Max = high, Escape = nodes.Count, Obstacle = count == 1 ? ids[first] : -1 };
        }
    }

    // Small native BVH shared by all jobs in a world; the dense labels stay in the immutable map.
    public struct VolumeQuery
    {
        [ReadOnly] public NativeArray<VolumeBvhNode> Nodes;
        public float3 Min, Max;
        public float Clearance;
        public bool SegmentClear(float3 a, float3 b) => SafeFraction(a, b) >= 1;
        public float SafeFraction(float3 a, float3 b)
        {
            float3 low = Min + Clearance, high = Max - Clearance, delta = b - a;
            if (math.any(a <= low) || math.any(a >= high)) return 0;
            float fraction = 1; bool hit = false;
            for (int axis = 0; axis < 3; axis++)
            {
                if (b[axis] <= low[axis]) { fraction = math.min(fraction, (low[axis] - a[axis]) / delta[axis]); hit = true; }
                if (b[axis] >= high[axis]) { fraction = math.min(fraction, (high[axis] - a[axis]) / delta[axis]); hit = true; }
            }
            for (int node = 0; node < Nodes.Length;)
            {
                var box = Nodes[node];
                if (!VolumeGeometry.SegmentBox(a, b, box.Min - Clearance, box.Max + Clearance, out float entry) || entry > fraction)
                { node = box.Escape; continue; }
                if (box.Obstacle >= 0) { fraction = math.min(fraction, entry); hit = true; }
                node++;
            }
            // Keep a world-space gap at a contact, independent of velocity-plane tolerance.
            return hit ? math.max(0, fraction - 0.0001f / math.max(math.length(delta), 0.0001f)) : 1;
        }
    }
}
