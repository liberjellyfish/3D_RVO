using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>小型不可变粗图共享的 ALT 下界；细图不能借用粗图距离。</summary>
    internal sealed class VolumeLandmarks
    {
        internal const int LandmarkCount = 4;
        internal readonly float[] Distances;
        private readonly int count;

        internal VolumeLandmarks(NavigationVolume map)
        {
            count = map.Count;
            using (var labels = new NativeArray<int>(map.labels, Allocator.TempJob))
            using (var nodes = new NativeArray<VolumeBvhNode>(map.Nodes, Allocator.TempJob))
            using (var distances = new NativeArray<float>(count * LandmarkCount, Allocator.TempJob))
            using (var edges = new NativeArray<uint>(count, Allocator.TempJob))
            using (var heap = new NativeArray<int>(count, Allocator.TempJob))
            using (var slots = new NativeArray<int>(count, Allocator.TempJob))
            using (var nearest = new NativeArray<float>(count, Allocator.TempJob))
            {
                // 一次性粗图预处理也走 Burst，避免把排队等待转成主线程启动卡顿。
                new BuildJob { labels = labels, Distances = distances, edges = edges, heap = heap, slots = slots,
                    nearest = nearest, count = count, resolution = map.Resolution, cellSize = map.CellSize,
                    largest = map.LargestComponent, query = new VolumeQuery { Nodes = nodes, Min = map.Min,
                        Max = map.Max, Clearance = map.ClearanceRadius } }.Run();
                Distances = distances.ToArray();
            }
        }
        internal float LowerBound(int node, int goal)
        {
            float lower = 0;
            for (int k = 0; k < LandmarkCount; k++) lower = math.max(lower, Bound(Distances[k * count + node], Distances[k * count + goal]));
            return lower;
        }
        internal static float Bound(float a, float b)
        {
            // 与二维 ALT 相同，长路径相减保留浮点余量；拥堵只增加边代价。
            return math.isfinite(a) && math.isfinite(b) ? math.max(0, math.abs(a - b) - 0.001f * math.max(a, b)) : 0;
        }
        [BurstCompile]
        private struct BuildJob : IJob
        {
            [ReadOnly] public NativeArray<int> labels;
            [ReadOnly] public VolumeQuery query;
            public NativeArray<float> Distances, nearest;
            public NativeArray<uint> edges;
            public NativeArray<int> heap, slots;
            public int count, resolution, largest;
            public float cellSize;
            private int3 Cell(int cell) => new int3(cell % resolution, cell / resolution % resolution, cell / (resolution * resolution));
            private float3 Center(int3 cell) => query.Min + ((float3)cell + 0.5f) * cellSize;
            public void Execute()
            {
                int source = -1;
                for (int cell = 0; cell < count; cell++)
                {
                    nearest[cell] = float.PositiveInfinity;
                    if (labels[cell] <= 0) continue;
                    if (source < 0 && labels[cell] == largest) source = cell;
                    int3 c = Cell(cell);
                    for (int bit = 0; bit < 27; bit++)
                    {
                        if (bit == 13) continue;
                        int3 next = c + Direction(bit);
                        if (math.any(next < 0) || math.any(next >= resolution)) continue;
                        int index = next.x + resolution * (next.y + resolution * next.z);
                        if (labels[index] > 0 && query.SegmentClear(Center(c), Center(next))) edges[cell] |= 1u << bit;
                    }
                }
                for (int landmark = 0; landmark < LandmarkCount; landmark++)
                {
                    int offset = landmark * count, size = 1;
                    for (int i = 0; i < count; i++) { Distances[offset + i] = float.PositiveInfinity; slots[i] = -1; }
                    heap[0] = source; slots[source] = 0; Distances[offset + source] = 0;
                    while (size > 0)
                    {
                        int current = heap[0]; slots[current] = -2; size--;
                        if (size > 0)
                        {
                            heap[0] = heap[size]; slots[heap[0]] = 0; int at = 0;
                            while (at * 2 + 1 < size)
                            {
                                int child = at * 2 + 1;
                                if (child + 1 < size && Distances[offset + heap[child + 1]] < Distances[offset + heap[child]]) child++;
                                if (Distances[offset + heap[at]] <= Distances[offset + heap[child]]) break;
                                Swap(heap, slots, at, child); at = child;
                            }
                        }
                        for (int bit = 0; bit < 27; bit++) if ((edges[current] & (1u << bit)) != 0)
                        {
                            int3 d = Direction(bit); int next = current + d.x + resolution * (d.y + resolution * d.z);
                            if (slots[next] == -2) continue;
                            float candidate = Distances[offset + current] + math.length((float3)d) * cellSize;
                            if (candidate >= Distances[offset + next]) continue;
                            Distances[offset + next] = candidate; int at = slots[next];
                            if (at < 0) { at = size++; heap[at] = next; slots[next] = at; }
                            while (at > 0)
                            {
                                int parent = (at - 1) / 2;
                                if (Distances[offset + heap[parent]] <= candidate) break;
                                Swap(heap, slots, at, parent); at = parent;
                            }
                        }
                    }
                    // 最远点采样只覆盖最大连通域，其他连通域自动退回几何下界。
                    float farthest = -1;
                    for (int i = 0; i < count; i++) if (labels[i] == largest)
                    {
                        nearest[i] = math.min(nearest[i], Distances[offset + i]);
                        if (nearest[i] > farthest) { farthest = nearest[i]; source = i; }
                    }
                }
            }
        }
        private static int3 Direction(int bit) => new int3(bit % 3 - 1, bit / 3 % 3 - 1, bit / 9 - 1);
        private static void Swap(NativeArray<int> heap, NativeArray<int> slots, int a, int b)
        { int cell = heap[a]; heap[a] = heap[b]; heap[b] = cell; slots[heap[a]] = a; slots[heap[b]] = b; }
    }
}
