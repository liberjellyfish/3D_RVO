using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    public sealed class SpatialHashNeighborSearch : INeighborSearch
    {
        public string Name => "Spatial hash XZ";
        public bool IsImplemented => true;
        private NativeParallelMultiHashMap<int2, int> buckets;
        public JobHandle Schedule(in StepContext context, in AgentReadView agents,
            in NeighborWriteView neighbors, JobHandle dependency)
        {
            if (!buckets.IsCreated) buckets = new NativeParallelMultiHashMap<int2, int>(agents.Count, Allocator.Persistent);
            var build = new BuildJob { Agents = agents, Buckets = buckets, CellSize = context.Settings.CellSize };
            var query = new QueryJob { Agents = agents, Output = neighbors, Buckets = buckets,
                CellSize = context.Settings.CellSize, Range = context.Settings.NeighborDistance };
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
        public void Dispose() { if (buckets.IsCreated) buckets.Dispose(); buckets = default; }

        [BurstCompile]
        private struct BuildJob : IJob
        {
            [ReadOnly] public AgentReadView Agents;
            public NativeParallelMultiHashMap<int2, int> Buckets;
            public float CellSize;
            public void Execute()
            {
                Buckets.Clear();
                for (int i = 0; i < Agents.Count; i++) Buckets.Add((int2)math.floor(Agents.Positions[i].xz / CellSize), i);
            }
        }
        [BurstCompile]
        private struct QueryJob : IJobParallelFor
        {
            [ReadOnly] public AgentReadView Agents;
            [ReadOnly] public NativeParallelMultiHashMap<int2, int> Buckets;
            [NativeDisableParallelForRestriction] public NeighborWriteView Output;
            public float CellSize, Range;
            public void Execute(int i)
            {
                float2 p = Agents.Positions[i].xz;
                int2 cell = (int2)math.floor(p / CellSize);
                int span = (int)math.ceil(Range / CellSize);
                int count = 0, found = 0, candidates = 0, occupancy = 0;
                // 大范围/很小 cell 时枚举空格反而更慢；保留精确全扫描退路，查询语义不变。
                if ((2.0 * span + 1) * (2.0 * span + 1) > Agents.Count * 8.0)
                {
                    for (int j = 0; j < Agents.Count; j++)
                        NearestNeighbors.Insert(Agents, Output, i, j, Range * Range, ref count, ref found);
                    candidates = Agents.Count - 1;
                    if (Buckets.TryGetFirstValue(cell, out int member, out var iterator))
                        do { occupancy++; } while (Buckets.TryGetNextValue(out member, ref iterator));
                }
                else for (int x = -span; x <= span; x++)
                    for (int z = -span; z <= span; z++)
                    {
                        // 完整 int2 key 比较，hash 冲突不会合并不同格子；负坐标用 floor。
                        if (!Buckets.TryGetFirstValue(cell + new int2(x, z), out int j, out var iterator)) continue;
                        do
                        {
                            if (x == 0 && z == 0) occupancy++;
                            if (j != i) candidates++;
                            NearestNeighbors.Insert(Agents, Output, i, j, Range * Range, ref count, ref found);
                        } while (Buckets.TryGetNextValue(out j, ref iterator));
                    }
                Output.Counts[i] = count; Output.DroppedCounts[i] = found - count;
                Output.CandidateCounts[i] = candidates; Output.BucketOccupancy[i] = occupancy;
            }
        }
    }
}
