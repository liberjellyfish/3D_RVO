using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>每 Tick 重建平衡 KDTree。连续数组、无递归查询、精确半径计数与最近 K。</summary>
    public sealed class KdTreeNeighborSearch : INeighborSearch
    {
        private struct Node
        {
            public float2 Min, Max;
            public int Start, Count, Escape;
        }
        public string Name => "Balanced dynamic KDTree XZ";
        public bool IsImplemented => true;
        private NativeArray<Node> nodes;
        private NativeArray<int> indices;
        public JobHandle Schedule(in StepContext context, in AgentReadView agents,
            in NeighborWriteView neighbors, JobHandle dependency)
        {
            if (!indices.IsCreated)
            {
                try
                {
                    indices = new NativeArray<int>(agents.Count, Allocator.Persistent);
                    nodes = new NativeArray<Node>(checked(agents.Count * 2 - 1), Allocator.Persistent);
                }
                catch { Dispose(); throw; }
            }
            var build = new BuildJob { Agents = agents, Indices = indices, Nodes = nodes };
            var query = new QueryJob { Agents = agents, Indices = indices, Nodes = nodes,
                Output = neighbors, RangeSquared = context.Settings.NeighborDistance * context.Settings.NeighborDistance };
            if (context.Settings.Backend == ExecutionBackend.JobsBurst)
            {
                var built = build.Schedule(dependency);
                try { return query.Schedule(agents.Count, 32, built); }
                catch { built.Complete(); throw; }
            }
            dependency.Complete(); build.Execute();
            for (int i = 0; i < agents.Count; i++) query.Execute(i);
            return default;
        }
        public void Dispose()
        {
            if (nodes.IsCreated) nodes.Dispose(); nodes = default;
            if (indices.IsCreated) indices.Dispose(); indices = default;
        }
        [BurstCompile]
        private struct BuildJob : IJob
        {
            [ReadOnly] public AgentReadView Agents;
            public NativeArray<int> Indices;
            public NativeArray<Node> Nodes;
            public void Execute()
            {
                for (int i = 0; i < Agents.Count; i++) Indices[i] = i;
                Nodes[0] = new Node { Count = Agents.Count, Escape = Nodes.Length };
                int index = 0;
                while (index < Nodes.Length)
                {
                    var node = Nodes[index];
                    node.Min = node.Max = Agents.Positions[Indices[node.Start]].xz;
                    for (int i = node.Start + 1; i < node.Start + node.Count; i++)
                    {
                        float2 p = Agents.Positions[Indices[i]].xz;
                        node.Min = math.min(node.Min, p); node.Max = math.max(node.Max, p);
                    }
                    Nodes[index] = node;
                    if (node.Count <= 8) { index = node.Escape; continue; }
                    int left = node.Count / 2, middle = node.Start + left;
                    Select(node.Start, node.Start + node.Count - 1, middle, node.Max.x-node.Min.x >= node.Max.y-node.Min.y ? 0 : 1);
                    int rightNode = index + 2 * left;
                    Nodes[index+1] = new Node { Start = node.Start, Count = left, Escape = rightNode };
                    Nodes[rightNode] = new Node { Start = middle, Count = node.Count-left, Escape = node.Escape };
                    index++;
                }
            }
            private bool Less(int a, int b, int axis)
            {
                float x = Agents.Positions[a].xz[axis], y = Agents.Positions[b].xz[axis];
                return x < y || (x == y && Agents.Ids[a] < Agents.Ids[b]);
            }
            private void Select(int low, int high, int middle, int axis)
            {
                while (low < high)
                {
                    int pivot = Indices[(low+high)/2], i = low, j = high;
                    while (i <= j)
                    {
                        while (Less(Indices[i],pivot,axis)) i++;
                        while (Less(pivot,Indices[j],axis)) j--;
                        if (i > j) break;
                        int temp = Indices[i]; Indices[i++] = Indices[j]; Indices[j--] = temp;
                    }
                    if (middle <= j) high = j;
                    else if (middle >= i) low = i;
                    else break;
                }
            }
        }
        [BurstCompile]
        private struct QueryJob : IJobParallelFor
        {
            [ReadOnly] public AgentReadView Agents;
            [ReadOnly] public NativeArray<int> Indices;
            [ReadOnly] public NativeArray<Node> Nodes;
            [NativeDisableParallelForRestriction] public NeighborWriteView Output;
            public float RangeSquared;
            public void Execute(int self)
            {
                float2 p = Agents.Positions[self].xz;
                int count = 0, found = 0, candidates = 0, index = 0;
                while (index < Nodes.Length)
                {
                    var node = Nodes[index];
                    float lower = math.distancesq(p, math.clamp(p,node.Min,node.Max));
                    if (lower > RangeSquared) { index = node.Escape; continue; }
                    // 完全落入半径、但比当前 K 个更远的子树可整体计数，无需逐点插入。
                    if (count == Output.MaxNeighbors && lower > Output.DistanceSquared[self*Output.MaxNeighbors+count-1] &&
                        math.lengthsq(math.max(math.abs(p-node.Min),math.abs(p-node.Max))) <= RangeSquared)
                    { found += node.Count; index = node.Escape; continue; }
                    if (node.Count > 8) { index++; continue; }
                    for (int slot = node.Start; slot < node.Start + node.Count; slot++)
                    {
                        int other = Indices[slot]; if (other != self) candidates++;
                        NearestNeighbors.Insert(Agents,Output,self,other,RangeSquared,ref count,ref found);
                    }
                    index = node.Escape;
                }
                Output.Counts[self] = count; Output.DroppedCounts[self] = found-count;
                Output.CandidateCounts[self] = candidates; Output.BucketOccupancy[self] = 0;
            }
        }
    }

    public static class NeighborSearchFactory
    {
        public static INeighborSearch Create(NeighborSearchAlgorithm algorithm)
        {
            switch (algorithm)
            {
                case NeighborSearchAlgorithm.BruteForce: return new BruteForceNeighborSearch();
                case NeighborSearchAlgorithm.SpatialHash: return new SpatialHashNeighborSearch();
                case NeighborSearchAlgorithm.KdTree: return new KdTreeNeighborSearch();
                default: throw new System.ArgumentOutOfRangeException(nameof(algorithm));
            }
        }
    }
}
