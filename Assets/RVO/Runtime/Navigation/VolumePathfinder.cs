using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>26-neighbor A*, sparse bounded workspace. Exhausted workspace is never reported as NoPath.</summary>
    public interface IVolumePathfinder : IDisposable
    {
        VolumePathStatus Status { get; }
        void Begin(NavigationVolume volume, float3 from, float3 to, float heuristicWeight = 1,
            bool allowDirect = true, float3 congestionCenter = default, float congestionRadius = 0,
            NavigationVolume endpointMap = null);
        int Advance(int budget);
        int CopyPath(float3[] output, bool smooth = true);
    }

    public sealed class VolumePathfinder : IVolumePathfinder
    {
        private struct Node { public int Cell, Parent, Heap; public float G, F; }
        private readonly Node[] nodes;
        private readonly int[] heap, reverse;
        private readonly Dictionary<int, int> lookup;
        private NavigationVolume map, geometry;
        private float3 start, goal, penaltyCenter;
        private float weight, penaltyRadius;
        private int count, heapCount, target, result;
        private bool direct;
        public VolumePathStatus Status { get; private set; }
        public float GraphCost { get; private set; }
        public int ExpandedNodes { get; private set; }
        public int VisitedNodes => count;
        public int Capacity => nodes.Length;
        public VolumePathfinder(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            nodes = new Node[capacity]; heap = new int[capacity]; reverse = new int[capacity]; lookup = new Dictionary<int, int>(capacity);
        }
        public void Begin(NavigationVolume volume, float3 from, float3 to, float heuristicWeight = 1,
            bool allowDirect = true, float3 congestionCenter = default, float congestionRadius = 0,
            NavigationVolume endpointMap = null)
        {
            if (!math.isfinite(heuristicWeight) || heuristicWeight < 1 || heuristicWeight > 2) throw new ArgumentOutOfRangeException(nameof(heuristicWeight));
            map = volume; geometry = endpointMap ?? map; start = from; goal = to; weight = heuristicWeight; penaltyCenter = congestionCenter; penaltyRadius = congestionRadius;
            lookup.Clear(); count = heapCount = ExpandedNodes = 0; result = -1; direct = false;
            GraphCost = float.PositiveInfinity; Status = VolumePathStatus.Pending;
            int source = map.Anchor(from, geometry); target = map.Anchor(to, geometry);
            if (source < 0 || target < 0) { Status = VolumePathStatus.InvalidEndpoint; return; }
            if (map.Component(source) != map.Component(target)) { Status = VolumePathStatus.NoPath; return; }
            if (allowDirect && !CrossesPenalty(from, to) && geometry.SegmentClear(from, to))
            { direct = true; GraphCost = math.distance(from, to); Status = VolumePathStatus.Ready; return; }
            Add(source, -1, 0);
        }
        public int Advance(int budget)
        {
            int work = 0;
            while (Status == VolumePathStatus.Pending && heapCount > 0 && work < budget)
            {
                int current = Pop(); Node node = nodes[current]; work++; ExpandedNodes++;
                if (node.Cell == target) { result = current; GraphCost = node.G; Status = VolumePathStatus.Ready; break; }
                for (int z = -1; z <= 1 && Status == VolumePathStatus.Pending; z++)
                    for (int y = -1; y <= 1 && Status == VolumePathStatus.Pending; y++)
                        for (int x = -1; x <= 1 && Status == VolumePathStatus.Pending; x++)
                        {
                            if (x == 0 && y == 0 && z == 0) continue;
                            var direction = new int3(x, y, z); if (!map.Edge(node.Cell, direction, out int cell)) continue;
                            float multiplier = penaltyRadius > 0 && CrossesPenalty(map.Center(node.Cell), map.Center(cell)) ? 9 : 1;
                            float cost = node.G + math.length((float3)direction) * map.CellSize * multiplier;
                            if (!lookup.TryGetValue(cell, out int next)) { Add(cell, current, cost); continue; }
                            if (cost >= nodes[next].G) continue;
                            var update = nodes[next]; update.G = cost; update.F = cost + weight * Heuristic(cell); update.Parent = current;
                            nodes[next] = update; Push(next); // Reopen on better g, including weighted/inconsistent ordering.
                        }
            }
            if (Status == VolumePathStatus.Pending && heapCount == 0) Status = VolumePathStatus.NoPath;
            return work;
        }
        public int CopyPath(float3[] output, bool smooth = true)
        {
            if (Status != VolumePathStatus.Ready) return 0;
            int length = 0;
            if (!direct) for (int i = result; i >= 0; i = nodes[i].Parent) reverse[length++] = nodes[i].Cell;
            if (output.Length < length + 2) throw new ArgumentException("Path output capacity too small.");
            int written = 0; output[written++] = start;
            for (int i = length - 1; i >= 0; i--) output[written++] = map.Center(reverse[i]);
            output[written++] = goal;
            if (!smooth || written <= 2) return written;
            int write = 1;
            for (int i = 2; i < written; i++)
                if (CrossesPenalty(output[write - 1], output[i]) || !geometry.SegmentClear(output[write - 1], output[i])) output[write++] = output[i - 1];
            output[write++] = goal; return write;
        }
        public bool CrossesPenalty(float3 a, float3 b)
        {
            if (penaltyRadius <= 0) return false;
            float3 d = b - a; float t = math.saturate(math.dot(penaltyCenter - a, d) / math.max(1e-10f, math.lengthsq(d)));
            return math.distancesq(a + t * d, penaltyCenter) < penaltyRadius * penaltyRadius;
        }
        // Exact obstacle-free 26-neighbor distance; admissible also with nonnegative congestion costs.
        internal static float GridDistance(int3 delta)
        {
            int3 d = math.abs(delta); int lo = math.cmin(d), hi = math.cmax(d), mid = math.csum(d) - lo - hi;
            return hi + (1.41421356237f - 1) * mid + (1.73205080757f - 1.41421356237f) * lo;
        }
        private float Heuristic(int cell) => GridDistance(map.Cell(cell) - map.Cell(target)) * map.CellSize;
        public void Dispose() { }
        private void Add(int cell, int parent, float cost)
        {
            if (count == nodes.Length) { Status = VolumePathStatus.CapacityExceeded; return; }
            int index = count++; lookup.Add(cell, index);
            nodes[index] = new Node { Cell = cell, Parent = parent, G = cost, F = cost + weight * Heuristic(cell), Heap = -1 }; Push(index);
        }
        private bool Less(int a, int b) => nodes[a].F < nodes[b].F || (nodes[a].F == nodes[b].F &&
            (nodes[a].G > nodes[b].G || (nodes[a].G == nodes[b].G && nodes[a].Cell < nodes[b].Cell)));
        private void Swap(int a, int b)
        { int t = heap[a]; heap[a] = heap[b]; heap[b] = t; nodes[heap[a]].Heap = a; nodes[heap[b]].Heap = b; }
        private void Push(int node)
        {
            int at = nodes[node].Heap;
            if (at < 0) { at = heapCount++; heap[at] = node; nodes[node].Heap = at; }
            while (at > 0) { int p = (at - 1) / 2; if (!Less(heap[at], heap[p])) break; Swap(at, p); at = p; }
        }
        private int Pop()
        {
            int first = heap[0]; nodes[first].Heap = -1; heapCount--;
            if (heapCount == 0) return first;
            heap[0] = heap[heapCount]; nodes[heap[0]].Heap = 0; int at = 0;
            while (2 * at + 1 < heapCount)
            {
                int child = 2 * at + 1; if (child + 1 < heapCount && Less(heap[child + 1], heap[child])) child++;
                if (!Less(heap[child], heap[at])) break; Swap(at, child); at = child;
            }
            return first;
        }
    }
}
