using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    public sealed class DirectGoalPreferredVelocity : StatelessModule, IPreferredVelocityProvider
    {
        public override string Name => "Direct goal preferred velocity";
        public JobHandle Schedule(in StepContext context, in AgentReadView agents,
            NativeArray<float3> preferred, JobHandle dependency)
        {
            var job = new PreferredJob { Context = context, Agents = agents, Preferred = preferred };
            if (context.Settings.Backend == ExecutionBackend.JobsBurst) return job.Schedule(agents.Count, 64, dependency);
            dependency.Complete();
            for (int i = 0; i < agents.Count; i++) job.Execute(i);
            return default;
        }
        [BurstCompile]
        private struct PreferredJob : IJobParallelFor
        {
            public StepContext Context;
            [ReadOnly] public AgentReadView Agents;
            public NativeArray<float3> Preferred;
            public void Execute(int i)
            {
                float2 delta = Agents.Goals[i].xz - Agents.Positions[i].xz;
                float distance = math.length(delta);
                var parameters = Agents.Parameters[i];
                float speed = distance <= parameters.ArrivalDistance ? 0f : math.min(parameters.MaxSpeed, distance / Context.DeltaTime);
                // 同一手性、确定性的破对称偏好；接近目标时衰减，且始终在求解前应用。
                float bias = Context.Settings.PreferredSideBias * math.min(1, distance / math.max(parameters.Radius * 4, 0.001f));
                float2 direction = math.normalizesafe(delta);
                float2 velocity = math.normalizesafe(direction + bias * new float2(-direction.y, direction.x)) * speed;
                Preferred[i] = new float3(velocity.x, 0, velocity.y);
            }
        }
    }

    public sealed class PassThroughSolver : StatelessModule, IAvoidanceSolver
    {
        public override string Name => "No avoidance";
        public JobHandle Schedule(in StepContext context, in AgentReadView agents,
            NativeArray<float3>.ReadOnly preferred, in NeighborReadView neighbors,
            in MotionOutput output, JobHandle dependency)
        {
            var job = new PassJob { Preferred = preferred, Output = output };
            if (context.Settings.Backend == ExecutionBackend.JobsBurst) return job.Schedule(agents.Count, 64, dependency);
            dependency.Complete();
            for (int i = 0; i < agents.Count; i++) job.Execute(i);
            return default;
        }
        [BurstCompile]
        private struct PassJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3>.ReadOnly Preferred;
            public MotionOutput Output;
            public void Execute(int i) { Output.Velocities[i] = Preferred[i]; Output.Status[i] = SolveStatus.Success; }
        }
    }

    public sealed class PlanarEulerIntegrator : StatelessModule, IMotionIntegrator
    {
        public override string Name => "Planar fixed-step integration";
        public JobHandle Schedule(in StepContext context, in AgentReadView agents,
            NativeArray<float3>.ReadOnly chosenVelocities, NativeArray<float3> nextPositions, JobHandle dependency)
        {
            var job = new IntegrateJob { Context = context, Agents = agents, Velocities = chosenVelocities, Positions = nextPositions };
            if (context.Settings.Backend == ExecutionBackend.JobsBurst) return job.Schedule(agents.Count, 64, dependency);
            dependency.Complete();
            for (int i = 0; i < agents.Count; i++) job.Execute(i);
            return default;
        }
        [BurstCompile]
        private struct IntegrateJob : IJobParallelFor
        {
            public StepContext Context;
            [ReadOnly] public AgentReadView Agents;
            [ReadOnly] public NativeArray<float3>.ReadOnly Velocities;
            public NativeArray<float3> Positions;
            public void Execute(int i)
            {
                float3 position = Agents.Positions[i] + Velocities[i] * Context.DeltaTime;
                position.y = Context.Settings.PlaneHeight;
                Positions[i] = position; // 不修正位置，不平滑或二次修改求解速度。
            }
        }
    }
}
