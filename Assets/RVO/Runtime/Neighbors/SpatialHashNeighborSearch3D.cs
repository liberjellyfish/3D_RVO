using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    public sealed class SpatialHashNeighborSearch3D : INeighborSearch
    {
        public string Name => "Spatial hash XYZ";
        public bool IsImplemented => true;
        private NativeParallelMultiHashMap<int3, int> buckets;
        public JobHandle Schedule(in StepContext context, in AgentReadView agents, in NeighborWriteView neighbors, JobHandle dependency)
        {
            if (!buckets.IsCreated) buckets = new NativeParallelMultiHashMap<int3, int>(agents.Count, Allocator.Persistent);
            var build = new BuildJob { Agents = agents, Buckets = buckets, CellSize = context.Settings.CellSize };
            var query = new QueryJob { Agents = agents, Output = neighbors, Buckets = buckets,
                CellSize = context.Settings.CellSize, Range = context.Settings.NeighborDistance };
            if (context.Settings.Backend == ExecutionBackend.JobsBurst)
            {
                var built = build.Schedule(dependency);
                try { return query.Schedule(agents.Count, 32, built); } catch { built.Complete(); throw; }
            }
            dependency.Complete(); build.Execute(); for (int i = 0; i < agents.Count; i++) query.Execute(i); return default;
        }
        public void Dispose() { if (buckets.IsCreated) buckets.Dispose(); buckets = default; }
        [BurstCompile]
        private struct BuildJob : IJob
        {
            [ReadOnly] public AgentReadView Agents;
            public NativeParallelMultiHashMap<int3, int> Buckets;
            public float CellSize;
            public void Execute()
            {
                Buckets.Clear(); for (int i = 0; i < Agents.Count; i++) Buckets.Add((int3)math.floor(Agents.Positions[i] / CellSize), i);
            }
        }
        [BurstCompile]
        private struct QueryJob : IJobParallelFor
        {
            [ReadOnly] public AgentReadView Agents;
            [ReadOnly] public NativeParallelMultiHashMap<int3, int> Buckets;
            [NativeDisableParallelForRestriction] public NeighborWriteView Output;
            public float CellSize, Range;
            public void Execute(int i)
            {
                float3 p = Agents.Positions[i]; int3 cell = (int3)math.floor(p / CellSize);
                int3 low = (int3)math.floor((p - Range) / CellSize), high = (int3)math.floor((p + Range) / CellSize);
                int count = 0, found = 0, candidates = 0, occupancy = 0;
                if (Buckets.TryGetFirstValue(cell, out int member, out var members))
                    do { occupancy++; } while (Buckets.TryGetNextValue(out member, ref members));
                if ((double)(high.x - low.x + 1) * (high.y - low.y + 1) * (high.z - low.z + 1) > Agents.Count * 8.0)
                {
                    for (int j = 0; j < Agents.Count; j++) NearestNeighbors.Insert(Agents, Output, i, j, Range * Range, ref count, ref found, true);
                    candidates = Agents.Count - 1;
                }
                else for (int z = low.z; z <= high.z; z++) for (int y = low.y; y <= high.y; y++) for (int x = low.x; x <= high.x; x++)
                {
                    float3 boxMin = new float3(x, y, z) * CellSize;
                    if (math.distancesq(p, math.clamp(p, boxMin, boxMin + CellSize)) > Range * Range) continue;
                    if (!Buckets.TryGetFirstValue(new int3(x, y, z), out int j, out var it)) continue;
                    do
                    {
                        if (j != i) candidates++;
                        NearestNeighbors.Insert(Agents, Output, i, j, Range * Range, ref count, ref found, true);
                    } while (Buckets.TryGetNextValue(out j, ref it));
                }
                Output.Counts[i] = count; Output.DroppedCounts[i] = found - count;
                Output.CandidateCounts[i] = candidates; Output.BucketOccupancy[i] = occupancy;
            }
        }
    }
}
