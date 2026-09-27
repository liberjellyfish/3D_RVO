using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>VO / RVO 共用候选搜索；只在速度障碍映射处区分算法。</summary>
    public class SampledVelocitySolver2D : IAvoidanceSolver
    {
        public string Name => reciprocal ? "Sampled finite-horizon RVO" : "Sampled finite-horizon VO";
        public bool IsImplemented => true;
        private readonly bool reciprocal;
        private NativeArray<float2> directions;
        protected SampledVelocitySolver2D(bool reciprocal) { this.reciprocal = reciprocal; }
        public void Dispose() { if (directions.IsCreated) directions.Dispose(); directions = default; }

        public JobHandle Schedule(in StepContext context, in AgentReadView agents,
            NativeArray<float3>.ReadOnly preferred, in NeighborReadView neighbors,
            in MotionOutput output, JobHandle dependency)
        {
            if (!directions.IsCreated)
            {
                directions = new NativeArray<float2>(context.Settings.Vo.AngleSamples, Allocator.Persistent);
                for (int angle = 0; angle < directions.Length; angle++)
                {
                    float radians = angle * (2f * math.PI / directions.Length);
                    directions[angle] = new float2(math.cos(radians), math.sin(radians));
                }
            }
            var job = new SampleJob { Context = context, Agents = agents, Neighbors = neighbors,
                Preferred = preferred, Output = output, Reciprocal = reciprocal, Directions = directions };
            if (context.Settings.Backend == ExecutionBackend.JobsBurst) return job.Schedule(agents.Count, 8, dependency);
            dependency.Complete();
            for (int i = 0; i < agents.Count; i++) job.Execute(i);
            return default;
        }

        [BurstCompile]
        private struct SampleJob : IJobParallelFor
        {
            public StepContext Context;
            [ReadOnly] public AgentReadView Agents;
            [ReadOnly] public NeighborReadView Neighbors;
            [ReadOnly] public NativeArray<float3>.ReadOnly Preferred;
            [ReadOnly] public NativeArray<float2> Directions;
            public MotionOutput Output;
            public bool Reciprocal;
            public void Execute(int i)
            {
                var search = new CandidateSearch(Context, Agents, Neighbors, i, Preferred[i].xz, Reciprocal);
                search.Consider(Preferred[i].xz);
                if (!search.PreferredIsSafe)
                {
                    search.Consider(float2.zero);
                    search.Consider(Agents.Velocities[i].xz);
                    float2 heading = math.normalizesafe(Preferred[i].xz, new float2(1, 0));
                    for (int ring = 1; ring <= Context.Settings.Vo.SpeedSamples; ring++)
                    {
                        float speed = Agents.Parameters[i].MaxSpeed * ring / Context.Settings.Vo.SpeedSamples;
                        for (int angle = 0; angle < Directions.Length; angle++)
                        {
                            float2 d = Directions[angle];
                            search.Consider(new float2(heading.x * d.x - heading.y * d.y,
                                heading.y * d.x + heading.x * d.y) * speed);
                        }
                    }
                }
                Output.Velocities[i] = new float3(search.Best.x, 0, search.Best.y);
                Output.Status[i] = search.HasSafeCandidate ? SolveStatus.Success : SolveStatus.Fallback;
            }
        }

        private struct CandidateSearch
        {
            private readonly StepContext context;
            private readonly AgentReadView agents;
            private readonly NeighborReadView neighbors;
            private readonly int index;
            private readonly float2 preferred;
            private readonly bool reciprocal;
            private float bestCost, bestRisk;
            public float2 Best { get; private set; }
            public bool HasSafeCandidate { get; private set; }
            public bool PreferredIsSafe => HasSafeCandidate && bestCost == 0f;

            public CandidateSearch(in StepContext context, in AgentReadView agents,
                in NeighborReadView neighbors, int index, float2 preferred, bool reciprocal)
            {
                this.context = context; this.agents = agents; this.neighbors = neighbors;
                this.index = index; this.preferred = preferred; this.reciprocal = reciprocal;
                bestCost = bestRisk = float.PositiveInfinity;
                Best = float2.zero; HasSafeCandidate = false;
            }

            public void Consider(float2 candidate)
            {
                float risk = 0;
                for (int slot = 0; slot < neighbors.Counts[index]; slot++)
                {
                    int other = neighbors.Indices[index * neighbors.MaxNeighbors + slot];
                    float2 p = agents.Positions[other].xz - agents.Positions[index].xz;
                    // RVO = 1/2 (VO + v_self)：在原始 VO 中测试 2v - v_self - v_other。
                    float2 relative = ReciprocalVelocityObstacle2D.Relative(candidate, agents.Velocities[index].xz,
                        agents.Velocities[other].xz, reciprocal);
                    float radius = agents.Parameters[index].Radius + agents.Parameters[other].Radius
                        + context.Settings.Vo.SafetyMargin;
                    float distance = math.length(p);
                    if (distance < radius)
                    {
                        // 已重叠时 VO 无安全速度：按一步分离需求评估，而非所有候选都得到 t=0。
                        // 完全重合用稳定 ID 产生相反方向，避免零向量和两者同向逃逸。
                        float2 outward = distance > context.Settings.Epsilon ? -p / distance :
                            new float2(agents.Ids[index] < agents.Ids[other] ? -1 : 1, 0);
                        float missing = (radius - distance) / context.DeltaTime - math.dot(relative, outward);
                        risk += 1f + math.max(0, missing);
                    }
                    else
                    {
                        float collisionTime = VelocityObstacle2D.TimeToCollision(p, relative, radius);
                        if (collisionTime <= context.Settings.TimeHorizon)
                            risk += 1f / (context.DeltaTime + collisionTime);
                    }
                }
                float cost = math.lengthsq(candidate - preferred);
                // 先比较安全性，再比较期望速度偏差；等分保持固定采样顺序，不引入随机抖动。
                bool safe = risk == 0;
                if ((safe && !HasSafeCandidate) || (safe == HasSafeCandidate &&
                    (risk < bestRisk || (risk == bestRisk && cost < bestCost))))
                {
                    Best = candidate; bestRisk = risk; bestCost = cost; HasSafeCandidate = safe;
                }
            }
        }
    }
    public sealed class VoSolver2D : SampledVelocitySolver2D { public VoSolver2D() : base(false) { } }
    public sealed class RvoSolver2D : SampledVelocitySolver2D { public RvoSolver2D() : base(true) { } }
    public static class ReciprocalVelocityObstacle2D
    {
        public static float2 Relative(float2 candidate, float2 self, float2 other, bool reciprocal) =>
            reciprocal ? 2 * candidate - self - other : candidate - other;
    }
}
