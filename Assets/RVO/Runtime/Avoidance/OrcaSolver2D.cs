using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    public sealed class OrcaSolver2D : IAvoidanceSolver
    {
        public string Name => "ORCA 2D";
        public bool IsImplemented => true;
        private NativeArray<VelocityHalfPlane2D> constraints;
        public NativeArray<VelocityHalfPlane2D>.ReadOnly Constraints => constraints.AsReadOnly();
        public JobHandle Schedule(in StepContext context, in AgentReadView agents,
            NativeArray<float3>.ReadOnly preferred, in NeighborReadView neighbors,
            in MotionOutput output, JobHandle dependency)
        {
            if (!constraints.IsCreated)
                constraints = new NativeArray<VelocityHalfPlane2D>(agents.Count * neighbors.MaxNeighbors, Allocator.Persistent);
            var job = new SolveJob { Context = context, Agents = agents, Preferred = preferred,
                Neighbors = neighbors, Output = output, Constraints = constraints };
            if (context.Settings.Backend == ExecutionBackend.JobsBurst) return job.Schedule(agents.Count, 32, dependency);
            dependency.Complete();
            for (int i = 0; i < agents.Count; i++) job.Execute(i);
            return default;
        }
        public void Dispose() { if (constraints.IsCreated) constraints.Dispose(); constraints = default; }

        [BurstCompile]
        private struct SolveJob : IJobParallelFor
        {
            public StepContext Context;
            [ReadOnly] public AgentReadView Agents;
            [ReadOnly] public NativeArray<float3>.ReadOnly Preferred;
            [ReadOnly] public NeighborReadView Neighbors;
            public MotionOutput Output;
            // 每个 worker 仅写自身的 K 个槽位，互不重叠。
            [NativeDisableParallelForRestriction] public NativeArray<VelocityHalfPlane2D> Constraints;
            public void Execute(int i)
            {
                int start = i * Neighbors.MaxNeighbors, count = Neighbors.Counts[i];
                for (int slot = 0; slot < count; slot++)
                {
                    int j = Neighbors.Indices[start + slot];
                    Constraints[start + slot] = OrcaGeometry2D.Build(Agents.Positions[j].xz - Agents.Positions[i].xz,
                        Agents.Velocities[i].xz, Agents.Velocities[j].xz,
                        Agents.Parameters[i].Radius + Agents.Parameters[j].Radius + Context.Settings.Vo.SafetyMargin,
                        Context.Settings.TimeHorizon, Context.DeltaTime, Agents.Ids[i], Agents.Ids[j], Context.Settings.Epsilon);
                }
                Output.Status[i] = PlanarVelocityOptimizer.Solve(new NativeSlice<VelocityHalfPlane2D>(Constraints, start, count),
                    Preferred[i].xz, Agents.Parameters[i].MaxSpeed, Context.Settings.Epsilon, out float2 velocity);
                Output.Velocities[i] = new float3(velocity.x, 0, velocity.y);
            }
        }
    }
}
