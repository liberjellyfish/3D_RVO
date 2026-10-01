using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    public sealed class VolumeEulerIntegrator : StatelessModule, IMotionIntegrator
    {
        public override string Name => "XYZ fixed-step integration";
        public JobHandle Schedule(in StepContext context, in AgentReadView agents,
            NativeArray<float3>.ReadOnly chosenVelocities, NativeArray<float3> nextPositions, JobHandle dependency)
        {
            var job = new IntegrateJob { DeltaTime = context.DeltaTime, Agents = agents, Velocities = chosenVelocities, Positions = nextPositions };
            if (context.Settings.Backend == ExecutionBackend.JobsBurst) return job.Schedule(agents.Count, 64, dependency);
            dependency.Complete(); for (int i = 0; i < agents.Count; i++) job.Execute(i); return default;
        }
        [BurstCompile]
        private struct IntegrateJob : IJobParallelFor
        {
            public float DeltaTime;
            [ReadOnly] public AgentReadView Agents;
            [ReadOnly] public NativeArray<float3>.ReadOnly Velocities;
            public NativeArray<float3> Positions;
            public void Execute(int i) { Positions[i] = Agents.Positions[i] + Velocities[i] * DeltaTime; }
        }
    }
}
