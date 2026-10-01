using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>Persistent sparse A* workspace. Only the bounded expansion kernel runs in Burst.
    /// Geometry queries avoid duplicating the 256³ component array into every search slot.</summary>
    public sealed class BurstVolumePathfinder : IVolumePathfinder
    {
        private struct Node { public int Cell, Parent, Heap; public float G, F; }
        private struct SearchState
        {
            public int Count, HeapCount, Result, Expanded;
            public float Cost;
            public VolumePathStatus Status;
        }
        private NativeArray<Node> nodes;
        private NativeArray<int> heap;
        private NativeParallelHashMap<int, int> lookup;
        private NativeArray<SearchState> state;
        private NativeArray<uint> coarseEdges;
        private readonly int[] reverse;
        private readonly NavigationVolume fineMap;
        private readonly VolumeQuery fineQuery, coarseQuery;
        private NativeArray<float> landmarks;
        private readonly bool ownsLandmarks;
        private NavigationVolume map, geometry;
        private SearchJob job;
        private float3 start, goal, penaltyCenter;
        private float penaltyRadius;
        private bool direct;
        public VolumePathStatus Status => state[0].Status;
        public float GraphCost => state[0].Cost;
        public int ExpandedNodes => state[0].Expanded;

        public BurstVolumePathfinder(int capacity, NavigationVolume fineMap, VolumeQuery fineQuery, VolumeQuery coarseQuery = default,
            NativeArray<float> coarseLandmarks = default)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.fineMap = fineMap; this.fineQuery = fineQuery; this.coarseQuery = coarseQuery;
            ownsLandmarks = !coarseLandmarks.IsCreated;
            reverse = new int[capacity];
            try
            {
                landmarks = ownsLandmarks ? new NativeArray<float>(0, Allocator.Persistent) : coarseLandmarks;
                nodes = new NativeArray<Node>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                heap = new NativeArray<int>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                lookup = new NativeParallelHashMap<int, int>(capacity, Allocator.Persistent);
                state = new NativeArray<SearchState>(1, Allocator.Persistent);
                // 128 KiB per slot for the default 32³ coarse graph; survives request resets.
                coarseEdges = new NativeArray<uint>(fineMap.Coarse?.Count ?? 0, Allocator.Persistent);
            }
            catch { Dispose(); throw; }
        }
        public void Begin(NavigationVolume volume, float3 from, float3 to, float heuristicWeight = 1,
            bool allowDirect = true, float3 congestionCenter = default, float congestionRadius = 0,
            NavigationVolume endpointMap = null)
        {
            if (!math.isfinite(heuristicWeight) || heuristicWeight < 1 || heuristicWeight > 2) throw new ArgumentOutOfRangeException(nameof(heuristicWeight));
            if (volume != fineMap && volume != fineMap.Coarse) throw new ArgumentException("Search graph must belong to this world.");
            map = volume; geometry = endpointMap ?? map; start = from; goal = to;
            penaltyCenter = congestionCenter; penaltyRadius = congestionRadius; direct = false;
            var s = new SearchState { Result = -1, Cost = float.PositiveInfinity, Status = VolumePathStatus.Pending };
            int source = map.Anchor(from, geometry), target = map.Anchor(to, geometry);
            if (source < 0 || target < 0) s.Status = VolumePathStatus.InvalidEndpoint;
            else if (map.Component(source) != map.Component(target)) s.Status = VolumePathStatus.NoPath;
            else if (allowDirect && !CrossesPenalty(from, to) && geometry.SegmentClear(from, to))
            { direct = true; s.Cost = math.distance(from, to); s.Status = VolumePathStatus.Ready; }
            state[0] = s;
            job = new SearchJob { Nodes = nodes, Heap = heap, Lookup = lookup, State = state,
                EdgeCache = coarseEdges, CacheEdges = volume == fineMap.Coarse,
                Landmarks = landmarks, UseLandmarks = volume == fineMap.Coarse && landmarks.IsCreated && landmarks.Length > 0,
                Query = volume == fineMap ? fineQuery : coarseQuery, Resolution = map.Resolution,
                CellSize = map.CellSize, Source = source, Target = target, Weight = heuristicWeight,
                PenaltyCenter = congestionCenter, PenaltyRadius = congestionRadius };
        }
        public int Advance(int budget)
        {
            if (Status != VolumePathStatus.Pending || budget <= 0) return 0;
            int before = state[0].Expanded; job.Budget = budget;
            job.Schedule().Complete();
            return state[0].Expanded - before;
        }
        public int CopyPath(float3[] output, bool smooth = true)
        {
            if (Status != VolumePathStatus.Ready) return 0;
            int length = 0;
            if (!direct) for (int i = state[0].Result; i >= 0; i = nodes[i].Parent) reverse[length++] = nodes[i].Cell;
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
        private bool CrossesPenalty(float3 a, float3 b) => SearchJob.Crosses(a, b, penaltyCenter, penaltyRadius);
        public void Dispose()
        {
            if (ownsLandmarks && landmarks.IsCreated) landmarks.Dispose();
            landmarks = default;
            if (nodes.IsCreated) nodes.Dispose(); if (heap.IsCreated) heap.Dispose();
            if (lookup.IsCreated) lookup.Dispose(); if (state.IsCreated) state.Dispose();
            if (coarseEdges.IsCreated) coarseEdges.Dispose(); coarseEdges = default;
            nodes = default; heap = default; lookup = default; state = default;
        }
        [BurstCompile]
        private struct SearchJob : IJob
        {
            public NativeArray<Node> Nodes;
            public NativeArray<int> Heap;
            public NativeParallelHashMap<int, int> Lookup;
            public NativeArray<SearchState> State;
            public NativeArray<uint> EdgeCache;
            public bool CacheEdges;
            [ReadOnly] public VolumeQuery Query;
            [ReadOnly] public NativeArray<float> Landmarks;
            public bool UseLandmarks;
            public int Resolution, Source, Target, Budget;
            public float CellSize, Weight, PenaltyRadius;
            public float3 PenaltyCenter;
            private SearchState s;
            private int3 targetCell;
            private int3 Cell(int index) => new int3(index % Resolution, index / Resolution % Resolution, index / (Resolution * Resolution));
            private float3 Center(int3 c) => Query.Min + ((float3)c + 0.5f) * CellSize;
            private float Heuristic(int cell)
            {
                float lower = VolumePathfinder.GridDistance(Cell(cell) - targetCell) * CellSize;
                if (UseLandmarks)
                {
                    int count = Resolution * Resolution * Resolution;
                    for (int k = 0; k < VolumeLandmarks.LandmarkCount; k++)
                        lower = math.max(lower, VolumeLandmarks.Bound(Landmarks[k * count + cell], Landmarks[k * count + Target]));
                }
                return lower;
            }
            public void Execute()
            {
                s = State[0]; targetCell = Cell(Target);
                if (s.Count == 0) { Lookup.Clear(); Add(Source, -1, 0); }
                int work = 0;
                while (s.Status == VolumePathStatus.Pending && s.HeapCount > 0 && work < Budget)
                {
                    int current = Pop(); var node = Nodes[current]; work++; s.Expanded++;
                    if (node.Cell == Target) { s.Result = current; s.Cost = node.G; s.Status = VolumePathStatus.Ready; break; }
                    int3 c = Cell(node.Cell); float3 p = Center(c); uint edges = Edges(node.Cell, c, p);
                    for (int z = -1; z <= 1 && s.Status == VolumePathStatus.Pending; z++)
                        for (int y = -1; y <= 1 && s.Status == VolumePathStatus.Pending; y++)
                            for (int x = -1; x <= 1 && s.Status == VolumePathStatus.Pending; x++)
                            {
                                if (x == 0 && y == 0 && z == 0) continue;
                                int bit = (z + 1) * 9 + (y + 1) * 3 + x + 1;
                                if ((edges & (1u << bit)) == 0) continue;
                                int3 direction = new int3(x, y, z), nextCell = c + direction;
                                float3 q = Center(nextCell);
                                int cell = (nextCell.z * Resolution + nextCell.y) * Resolution + nextCell.x;
                                float multiplier = Crosses(p, q, PenaltyCenter, PenaltyRadius) ? 9 : 1;
                                float cost = node.G + math.sqrt(x * x + y * y + z * z) * CellSize * multiplier;
                                if (!Lookup.TryGetValue(cell, out int next)) { Add(cell, current, cost); continue; }
                                if (cost >= Nodes[next].G) continue;
                                var update = Nodes[next]; update.G = cost; update.F = cost + Weight * Heuristic(cell); update.Parent = current;
                                Nodes[next] = update; Push(next);
                            }
                }
                if (s.Status == VolumePathStatus.Pending && s.HeapCount == 0) s.Status = VolumePathStatus.NoPath;
                State[0] = s;
            }
            private uint Edges(int cell, int3 c, float3 p)
            {
                const uint initialized = 1u << 31;
                uint mask = CacheEdges ? EdgeCache[cell] : 0;
                if ((mask & initialized) != 0) return mask;
                for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
                {
                    if (x == 0 && y == 0 && z == 0) continue;
                    int3 next = c + new int3(x, y, z);
                    if (math.any(next < 0) || math.any(next >= Resolution)) continue;
                    // Exact static segment check, including both centers; cache no dynamic/congestion costs.
                    if (Query.SegmentClear(p, Center(next))) mask |= 1u << ((z + 1) * 9 + (y + 1) * 3 + x + 1);
                }
                if (CacheEdges) EdgeCache[cell] = mask | initialized;
                return mask;
            }
            public static bool Crosses(float3 a, float3 b, float3 center, float radius)
            {
                if (radius <= 0) return false;
                float3 d = b - a; float t = math.saturate(math.dot(center - a, d) / math.max(1e-10f, math.lengthsq(d)));
                return math.distancesq(a + t * d, center) < radius * radius;
            }
            private void Add(int cell, int parent, float cost)
            {
                if (s.Count == Nodes.Length) { s.Status = VolumePathStatus.CapacityExceeded; return; }
                int index = s.Count++; Lookup.Add(cell, index);
                Nodes[index] = new Node { Cell = cell, Parent = parent, G = cost, F = cost + Weight * Heuristic(cell), Heap = -1 }; Push(index);
            }
            private bool Less(int a, int b) => Nodes[a].F < Nodes[b].F || (Nodes[a].F == Nodes[b].F &&
                (Nodes[a].G > Nodes[b].G || (Nodes[a].G == Nodes[b].G && Nodes[a].Cell < Nodes[b].Cell)));
            private void SetHeap(int node, int at) { var n = Nodes[node]; n.Heap = at; Nodes[node] = n; }
            private void Swap(int a, int b)
            { int t = Heap[a]; Heap[a] = Heap[b]; Heap[b] = t; SetHeap(Heap[a], a); SetHeap(Heap[b], b); }
            private void Push(int node)
            {
                int at = Nodes[node].Heap;
                if (at < 0) { at = s.HeapCount++; Heap[at] = node; SetHeap(node, at); }
                while (at > 0) { int p = (at - 1) / 2; if (!Less(Heap[at], Heap[p])) break; Swap(at, p); at = p; }
            }
            private int Pop()
            {
                int first = Heap[0]; SetHeap(first, -1); s.HeapCount--;
                if (s.HeapCount == 0) return first;
                Heap[0] = Heap[s.HeapCount]; SetHeap(Heap[0], 0); int at = 0;
                while (2 * at + 1 < s.HeapCount)
                {
                    int child = 2 * at + 1; if (child + 1 < s.HeapCount && Less(Heap[child + 1], Heap[child])) child++;
                    if (!Less(Heap[child], Heap[at])) break; Swap(at, child); at = child;
                }
                return first;
            }
        }
    }
}
