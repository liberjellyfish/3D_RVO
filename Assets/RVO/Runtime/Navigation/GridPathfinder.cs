using System;
using Unity.Mathematics;

namespace Rvo
{
    public enum GridPathStatus { Pending, Ready, Arrived, NoPath, InvalidEndpoint }

    /// <summary>可跨 Tick 继续的精确 A*；搜索戳避免每次清空整张大图。</summary>
    public sealed class GridPathfinder
    {
        private readonly float[] costs, scores;
        private readonly int[] parents, heap, heapSlots, reverse, stamps;
        private int heapCount, stamp, target, targetNode;
        private NavigationGrid map;
        private float2 startPoint, goalPoint;
        private bool direct;
        private float weight;
        private NavigationLandmarks landmarks;
        private int[] jumps;
        private float2 penaltyCenter;
        private float penaltyRadius;
        public GridPathStatus Status { get; private set; }
        public float LastGraphCost { get; private set; }
        public int ExpandedNodes { get; private set; }
        public GridPathfinder(int capacity)
        {
            costs = new float[capacity]; scores = new float[capacity]; parents = new int[capacity];
            heap = new int[capacity]; heapSlots = new int[capacity]; reverse = new int[capacity];
            stamps = new int[capacity];
        }
        public void Begin(NavigationGrid grid, float2 start, float2 goal, bool allowDirect = true, float heuristicWeight = 1,
            NavigationLandmarks landmarkHeuristic = null, float2 congestionCenter = default, float congestionRadius = 0)
        {
            if (!math.isfinite(heuristicWeight) || heuristicWeight < 1 || heuristicWeight > 2)
                throw new ArgumentOutOfRangeException(nameof(heuristicWeight));
            if (grid == null || grid.Count > costs.Length) throw new ArgumentException("A* scratch 不足。");
            if (!math.isfinite(congestionRadius) || congestionRadius < 0 || !math.all(math.isfinite(congestionCenter)))
                throw new ArgumentException("Invalid congestion region.");
            penaltyCenter = congestionCenter; penaltyRadius = congestionRadius;
            weight = heuristicWeight;
            landmarks = landmarkHeuristic;
            // 拥堵请求使用原始边计费，避免一条长跳跃绕过中间格的代价。
            jumps = landmarkHeuristic == null || penaltyRadius > 0 ? null : grid.JumpTargets;
            map = grid; startPoint = start; goalPoint = goal; heapCount = 0; ExpandedNodes = 0;
            direct = false; LastGraphCost = float.PositiveInfinity; Status = GridPathStatus.Pending;
            int source = map.Anchor(start); target = map.Anchor(goal); targetNode = -1;
            if (source < 0 || target < 0) { Status = GridPathStatus.InvalidEndpoint; return; }
            if (map.Component(source) != map.Component(target)) { Status = GridPathStatus.NoPath; return; }
            if (allowDirect && !CrossesCongestion(start,goal) && map.SegmentClear(start, goal, map.ClearanceRadius))
            { direct = true; LastGraphCost = math.distance(start,goal); Status = GridPathStatus.Ready; return; }
            if (stamp == int.MaxValue) { Array.Clear(stamps, 0, stamps.Length); stamp = 0; }
            stamp++; Touch(source); costs[source] = 0; scores[source] = weight * Heuristic(source); PushOrDecrease(source);
        }
        // 返回本次实际扩展数。预算耗尽只保持 Pending，绝不误报 NoPath。
        public int Advance(int budget)
        {
            int work = 0;
            while (Status == GridPathStatus.Pending && heapCount > 0 && work < budget)
            {
                int current = Pop(); ExpandedNodes++; work++;
                if (current == target)
                { targetNode = current; LastGraphCost = costs[current]; Status = GridPathStatus.Ready; break; }
                int mask = map.EdgeMask(current);
                for (int slot = 0; slot < 8; slot++) if ((mask & (1 << slot)) != 0)
                {
                    int2 d = NavigationGrid.Direction(slot); int next = current + d.y * map.Width + d.x;
                    float stepCost = map.CellSize * (d.x != 0 && d.y != 0 ? 1.41421356237f : 1);
                    Relax(current,next,stepCost);
                    if (jumps == null) continue;
                    int jump = jumps[current*8+slot];
                    int length = math.max(math.abs(jump%map.Width-current%map.Width),math.abs(jump/map.Width-current/map.Width));
                    if (length > 1) Relax(current,jump,length*stepCost);
                    // 在目标行/列处也可转弯，否则长直线可能跳过最佳分支。
                    int dx = target%map.Width-current%map.Width, dz = target/map.Width-current/map.Width;
                    int tx = d.x == 0 ? 0 : dx*d.x, tz = d.y == 0 ? 0 : dz*d.y;
                    if (tx > 1 && tx < length) Relax(current,current+tx*(d.y*map.Width+d.x),tx*stepCost);
                    if (tz > 1 && tz < length && tz != tx) Relax(current,current+tz*(d.y*map.Width+d.x),tz*stepCost);
                }
            }
            if (Status == GridPathStatus.Pending && heapCount == 0) Status = GridPathStatus.NoPath;
            return work;
        }
        private void Relax(int current, int next, float edgeCost)
        {
            Touch(next);
            float multiplier = penaltyRadius > 0 && math.distancesq(map.Center(next),penaltyCenter) < penaltyRadius*penaltyRadius ? 9 : 1;
            float cost = costs[current]+edgeCost*multiplier;
            if (cost >= costs[next]) return;
            // 加权启发式不再一致：更优 g 重新打开节点。
            costs[next] = cost; scores[next] = cost+weight*Heuristic(next);
            parents[next] = current; PushOrDecrease(next);
        }
        public int CopyPath(float2[] output, int offset, bool smooth)
        {
            if (output == null || offset < 0 || (long)offset + map.Count + 2 > output.Length) throw new ArgumentException("路径输出缓冲不足。");
            if (Status != GridPathStatus.Ready) return 0;
            int count = 0; output[offset + count++] = startPoint;
            if (!direct)
            {
                int length = 0;
                for (int node = targetNode; node >= 0; node = parents[node]) reverse[length++] = node;
                for (int i = length - 1; i >= 0; i--) output[offset + count++] = map.Center(reverse[i]);
            }
            output[offset + count++] = goalPoint;
            if (!smooth || count <= 2) return count;
            // 单次线性拉直，不为每个拐点逆向遍历所有剩余节点；每条输出线段均验证净空。
            int write = 1, anchor = 0;
            for (int point = 2; point < count; point++)
                if (CrossesCongestion(output[offset+anchor],output[offset+point]) ||
                    !map.SegmentClear(output[offset + anchor], output[offset + point], map.ClearanceRadius))
                { output[offset + write++] = output[offset + point - 1]; anchor = write - 1; }
            output[offset + write++] = goalPoint;
            return write;
        }
        private bool CrossesCongestion(float2 a, float2 b)
        {
            if (penaltyRadius <= 0) return false;
            float2 d = b-a; float t = math.clamp(math.dot(penaltyCenter-a,d)/math.max(1e-10f,math.lengthsq(d)),0,1);
            return math.distancesq(a+d*t,penaltyCenter) < penaltyRadius*penaltyRadius;
        }
        public GridPathStatus Find(NavigationGrid grid, float2 start, float2 goal, float2[] output, int offset, out int count)
        {
            Begin(grid, start, goal, false); Advance(int.MaxValue); count = CopyPath(output, offset, false); return Status;
        }
        private void Touch(int node)
        {
            if (stamps[node] == stamp) return;
            stamps[node] = stamp; costs[node] = float.PositiveInfinity; parents[node] = heapSlots[node] = -1;
        }
        private float Heuristic(int a)
        {
            int x = math.abs(a % map.Width - target % map.Width), z = math.abs(a / map.Width - target / map.Width);
            float octile = map.CellSize * (math.max(x, z) + 0.41421356237f * math.min(x, z));
            return landmarks == null ? octile : math.max(octile, landmarks.LowerBound(a,target));
        }
        // 相同 f 优先更大的 g，避免空旷地图把整片等分区域都展开；索引使结果稳定。
        private bool Less(int a, int b) => scores[a] < scores[b] || (scores[a] == scores[b] &&
            (costs[a] > costs[b] || (costs[a] == costs[b] && a < b)));
        private void Swap(int a, int b)
        { int temp = heap[a]; heap[a] = heap[b]; heap[b] = temp; heapSlots[heap[a]] = a; heapSlots[heap[b]] = b; }
        private void PushOrDecrease(int node)
        {
            int slot = heapSlots[node];
            if (slot < 0) { slot = heapCount++; heap[slot] = node; heapSlots[node] = slot; }
            while (slot > 0)
            { int parent = (slot - 1) / 2; if (!Less(heap[slot], heap[parent])) break; Swap(slot, parent); slot = parent; }
        }
        private int Pop()
        {
            int result = heap[0]; heapSlots[result] = -1; heapCount--;
            if (heapCount == 0) return result;
            heap[0] = heap[heapCount]; heapSlots[heap[0]] = 0; int slot = 0;
            while (slot * 2 + 1 < heapCount)
            {
                int child = slot * 2 + 1;
                if (child + 1 < heapCount && Less(heap[child + 1], heap[child])) child++;
                if (!Less(heap[child], heap[slot])) break;
                Swap(slot, child); slot = child;
            }
            return result;
        }
    }
}
