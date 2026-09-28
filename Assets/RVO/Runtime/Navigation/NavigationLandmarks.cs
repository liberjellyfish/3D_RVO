using Unity.Mathematics;

namespace Rvo
{
    /// <summary>ALT 下界：共享 4 个地标的精确图距离。与运动维度无关，只依赖非负边图。</summary>
    public sealed class NavigationLandmarks
    {
        private readonly float[][] distances;
        public long PayloadBytes { get; }
        public NavigationLandmarks(NavigationGrid map)
        {
            int count = map.SpawnCellCount == 0 ? 0 : 4;
            distances = new float[count][]; PayloadBytes = (long)count * map.Count * sizeof(float);
            if (count == 0) return;
            var heap = new int[map.Count]; var slots = new int[map.Count]; var nearest = new float[map.Count];
            for (int i = 0; i < nearest.Length; i++) nearest[i] = float.PositiveInfinity;
            int source = map.SpawnCell(0);
            for (int landmark = 0; landmark < count; landmark++)
            {
                var distance = new float[map.Count]; distances[landmark] = distance;
                for (int i = 0; i < distance.Length; i++) { distance[i] = float.PositiveInfinity; slots[i] = -1; }
                int size = 1; heap[0] = source; slots[source] = 0; distance[source] = 0;
                while (size > 0)
                {
                    int current = heap[0]; slots[current] = -2; size--;
                    if (size > 0)
                    {
                        heap[0] = heap[size]; slots[heap[0]] = 0; int slot = 0;
                        while (2*slot+1 < size)
                        {
                            int child = 2*slot+1;
                            if (child+1 < size && distance[heap[child+1]] < distance[heap[child]]) child++;
                            if (distance[heap[slot]] <= distance[heap[child]]) break;
                            Swap(heap,slots,slot,child); slot = child;
                        }
                    }
                    int mask = map.EdgeMask(current);
                    for (int edge = 0; edge < 8; edge++) if ((mask & (1 << edge)) != 0)
                    {
                        int2 d = NavigationGrid.Direction(edge); int next = current + d.y*map.Width+d.x;
                        if (slots[next] == -2) continue;
                        float candidate = distance[current] + map.CellSize * (d.x != 0 && d.y != 0 ? 1.41421356237f : 1);
                        if (candidate >= distance[next]) continue;
                        distance[next] = candidate; int slot = slots[next];
                        if (slot < 0) { slot = size++; heap[slot] = next; slots[next] = slot; }
                        while (slot > 0)
                        { int parent = (slot-1)/2; if (distance[heap[parent]] <= candidate) break; Swap(heap,slots,parent,slot); slot = parent; }
                    }
                }
                // Farthest-point sampling，覆盖最大连通域；其他连通域仍使用 octile。
                float farthest = -1;
                for (int i = 0; i < map.SpawnCellCount; i++)
                {
                    int cell = map.SpawnCell(i); nearest[cell] = math.min(nearest[cell],distance[cell]);
                    if (nearest[cell] > farthest) { farthest = nearest[cell]; source = cell; }
                }
            }
        }
        public float LowerBound(int node, int goal)
        {
            float lower = 0;
            foreach (var distance in distances)
                if (math.isfinite(distance[node]) && math.isfinite(distance[goal]))
                    // 浮点长路径相减留出保守余量，避免舍入把启发式推高。
                    lower = math.max(lower, math.abs(distance[node]-distance[goal]) - 0.001f * math.max(distance[node],distance[goal]));
            return lower;
        }
        private static void Swap(int[] heap, int[] slots, int a, int b)
        { int value = heap[a]; heap[a] = heap[b]; heap[b] = value; slots[heap[a]] = a; slots[heap[b]] = b; }
    }
}
