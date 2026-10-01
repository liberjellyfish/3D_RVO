using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    internal static class NearestNeighbors
    {
        public static void Insert(in AgentReadView agents, in NeighborWriteView output,
            int self, int other, float rangeSquared, ref int count, ref int found, bool full3D = false)
        {
            if (self == other) return;
            float3 delta = agents.Positions[other] - agents.Positions[self];
            float distance = full3D ? math.lengthsq(delta) : math.lengthsq(delta.xz);
            if (distance > rangeSquared) return;
            found++;
            int start = self * output.MaxNeighbors, slot = count;
            var indices = output.Indices; var distances = output.DistanceSquared;
            while (slot > 0)
            {
                int previous = start + slot - 1;
                if (distances[previous] < distance || (distances[previous] == distance && agents.Ids[indices[previous]] < agents.Ids[other])) break;
                if (slot < output.MaxNeighbors)
                { indices[start + slot] = indices[previous]; distances[start + slot] = distances[previous]; }
                slot--;
            }
            if (slot < output.MaxNeighbors) { indices[start + slot] = other; distances[start + slot] = distance; }
            count = math.min(count + 1, output.MaxNeighbors);
        }
    }

    public sealed class BruteForceNeighborSearch : StatelessModule, INeighborSearch
    {
        public override string Name => "Brute force";
        public JobHandle Schedule(in StepContext context, in AgentReadView agents,
            in NeighborWriteView neighbors, JobHandle dependency)
        {
            var job = new QueryJob { Agents = agents, Output = neighbors,
                RangeSquared = context.Settings.NeighborDistance * context.Settings.NeighborDistance,
                Full3D = context.Settings.Dimension == SimulationDimension.Full3D };
            if (context.Settings.Backend == ExecutionBackend.JobsBurst) return job.Schedule(agents.Count, 32, dependency);
            dependency.Complete();
            for (int i = 0; i < agents.Count; i++) job.Execute(i);
            return default;
        }
        [BurstCompile]
        private struct QueryJob : IJobParallelFor
        {
            [ReadOnly] public AgentReadView Agents;
            // i 只写自己的 N*K 切片和统计槽位。
            [NativeDisableParallelForRestriction] public NeighborWriteView Output;
            public float RangeSquared;
            public bool Full3D;
            public void Execute(int i)
            {
                int count = 0, found = 0;
                for (int j = 0; j < Agents.Count; j++) NearestNeighbors.Insert(Agents, Output, i, j, RangeSquared, ref count, ref found, Full3D);
                Output.Counts[i] = count; Output.DroppedCounts[i] = found - count;
                Output.CandidateCounts[i] = Agents.Count - 1; Output.BucketOccupancy[i] = 0;
            }
        }
    }
}
