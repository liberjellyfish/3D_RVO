using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    public sealed class VolumeAvoidanceSolver : IAvoidanceSolver
    {
        public string Name => "XYZ static planes + ORCA + synchronous sweep certificate";
        public bool IsImplemented => true;
        private const int StaticCapacity = 38;
        private readonly VolumeNavigation navigation;
        private readonly int stride;
        private NativeArray<VelocityPlane3D> planes;
        private NativeArray<int> counts, hardCounts, failed, truncated, parents;
        private NativeArray<float> residuals, scales, fractions;
        private NativeArray<float3> candidates;
        private NativeParallelMultiHashMap<int3, int> safetyBuckets;
        private NativeArray<int> safetyStats;
        public long SafetyLimitedTicks { get; private set; }
        public int LastLimitedAgents => safetyStats[0];
        public int LastSafetyPairChecks => safetyStats[1];
        public float LastSafetyScale { get; private set; } = 1;
        public int LastStaticTruncations { get; private set; }
        public VolumeAvoidanceSolver(VolumeNavigation navigation, int agents, int neighbors)
        {
            this.navigation = navigation; stride = neighbors + StaticCapacity;
            try
            {
                planes = new NativeArray<VelocityPlane3D>(checked(agents * stride), Allocator.Persistent);
                counts = new NativeArray<int>(agents, Allocator.Persistent); hardCounts = new NativeArray<int>(agents, Allocator.Persistent);
                failed = new NativeArray<int>(agents, Allocator.Persistent); truncated = new NativeArray<int>(agents, Allocator.Persistent);
                parents = new NativeArray<int>(agents, Allocator.Persistent); residuals = new NativeArray<float>(agents, Allocator.Persistent);
                scales = new NativeArray<float>(agents, Allocator.Persistent); fractions = new NativeArray<float>(agents, Allocator.Persistent);
                candidates = new NativeArray<float3>(agents, Allocator.Persistent); safetyStats = new NativeArray<int>(3, Allocator.Persistent);
                safetyBuckets = new NativeParallelMultiHashMap<int3, int>(agents, Allocator.Persistent);
            }
            catch { Dispose(); throw; }
        }
        public JobHandle Schedule(in StepContext context, in AgentReadView agents, NativeArray<float3>.ReadOnly preferred,
            in NeighborReadView neighbors, in MotionOutput output, JobHandle dependency)
        {
            var solve = new SolveJob { Context = context, Agents = agents, Preferred = preferred, Neighbors = neighbors, Output = output,
                Query = navigation.Query, Priorities = navigation.Traffic.Priorities, Planes = planes, Counts = counts, HardCounts = hardCounts,
                Residuals = residuals, Failed = failed, Truncated = truncated, Candidates = candidates, Stride = stride };
            var safety = new SafetyJob { Agents = agents, Output = output, Query = navigation.Query, Parents = parents, Scales = scales,
                Fractions = fractions, Buckets = safetyBuckets, Stats = safetyStats, DeltaTime = context.DeltaTime,
                Dynamic = context.Settings.Avoidance == AvoidanceAlgorithm.ORCA, BruteForce = context.Settings.Backend == ExecutionBackend.Reference };
            if (context.Settings.Backend == ExecutionBackend.JobsBurst)
            {
                var solved = solve.Schedule(agents.Count, 16, dependency);
                try { safety.Schedule(solved).Complete(); } catch { solved.Complete(); throw; }
            }
            else
            { dependency.Complete(); for (int i = 0; i < agents.Count; i++) solve.Execute(i); safety.Execute(); }
            if (safetyStats[2] != 0) throw new InvalidOperationException("Volume safety certificate rejected an invalid/overlapping input state.");
            LastSafetyScale = 1; LastStaticTruncations = 0;
            for (int i = 0; i < agents.Count; i++) { LastSafetyScale = math.min(LastSafetyScale, fractions[i]); LastStaticTruncations += truncated[i]; }
            if (LastLimitedAgents > 0) SafetyLimitedTicks++;
            return default;
        }
        public VolumeStepDebug CopyDebug(SimulationWorld world, int agent)
        {
            var step = world.DebugSnapshot;
            var copy = new VolumeStepDebug { InputTick = world.Tick - 1, AgentId = step.Inputs.Ids[agent], Position = step.Inputs.Positions[agent],
                Preferred = step.Preferred[agent], Candidate = candidates[agent], Final = world.Snapshot.Velocities[agent],
                Residual = residuals[agent], FailedPlane = failed[agent], SafetyScale = fractions[agent],
                Planes = new VelocityPlane3D[counts[agent]], Neighbors = new float3[step.Neighbors.Counts[agent]] };
            for (int i = 0; i < copy.Planes.Length; i++) copy.Planes[i] = planes[agent * stride + i];
            for (int i = 0; i < copy.Neighbors.Length; i++) copy.Neighbors[i] = step.Inputs.Positions[step.Neighbors.Indices[agent * step.Neighbors.MaxNeighbors + i]];
            return copy;
        }
        [BurstCompile]
        private struct SolveJob : IJobParallelFor
        {
            public StepContext Context;
            [ReadOnly] public AgentReadView Agents;
            [ReadOnly] public NativeArray<float3>.ReadOnly Preferred;
            [ReadOnly] public NeighborReadView Neighbors;
            [ReadOnly] public NativeArray<float>.ReadOnly Priorities;
            [ReadOnly] public VolumeQuery Query;
            public MotionOutput Output;
            [NativeDisableParallelForRestriction] public NativeArray<VelocityPlane3D> Planes;
            public NativeArray<int> Counts, HardCounts, Failed, Truncated;
            public NativeArray<float> Residuals;
            public NativeArray<float3> Candidates;
            public int Stride;
            public void Execute(int i)
            {
                int start = i * Stride, count = 0, seen = 0;
                float3 p = Agents.Positions[i]; float speed = Agents.Parameters[i].MaxSpeed, horizon = math.max(0.5f, Context.DeltaTime);
                for (int axis = 0; axis < 3; axis++)
                {
                    float3 normal = float3.zero; normal[axis] = 1;
                    Planes[start + count++] = new VelocityPlane3D { Normal = normal, Offset = -(p[axis] - Query.Min[axis] - Query.Clearance) / horizon, IsStatic = true, SourceId = -1 - axis * 2 };
                    Planes[start + count++] = new VelocityPlane3D { Normal = -normal, Offset = -(Query.Max[axis] - Query.Clearance - p[axis]) / horizon, IsStatic = true, SourceId = -2 - axis * 2 };
                }
                float reach = speed * horizon;
                for (int node = 0; node < Query.Nodes.Length;)
                {
                    var box = Query.Nodes[node]; float3 low = box.Min - Query.Clearance, high = box.Max + Query.Clearance;
                    if (math.distancesq(p, math.clamp(p, low, high)) > reach * reach) { node = box.Escape; continue; }
                    node++; if (box.Obstacle < 0) continue;
                    float3 gaps = math.max(low - p, p - high); int axis = gaps.x >= gaps.y && gaps.x >= gaps.z ? 0 : gaps.y >= gaps.z ? 1 : 2;
                    float3 normal = float3.zero; normal[axis] = p[axis] < low[axis] ? -1 : 1;
                    var plane = new VelocityPlane3D { Normal = normal, Offset = -gaps[axis] / horizon, IsStatic = true, SourceId = box.Obstacle };
                    seen++;
                    // Retain nearest support planes, independent of BVH traversal order.
                    int at = count;
                    while (at > 6 && Planes[start + at - 1].Offset < plane.Offset)
                    { if (at < StaticCapacity) Planes[start + at] = Planes[start + at - 1]; at--; }
                    if (at < StaticCapacity) Planes[start + at] = plane;
                    count = math.min(StaticCapacity, count + 1);
                }
                int hard = count; HardCounts[i] = hard; Truncated[i] = math.max(0, seen - (StaticCapacity - 6));
                if (Context.Settings.Avoidance == AvoidanceAlgorithm.ORCA)
                    for (int k = 0; k < Neighbors.Counts[i]; k++)
                    {
                        int j = Neighbors.Indices[i * Neighbors.MaxNeighbors + k];
                        float share = Priorities[j] / (Priorities[i] + Priorities[j]);
                        Planes[start + count++] = OrcaGeometry3D.Build(Agents.Positions[j] - p, Agents.Velocities[i], Agents.Velocities[j],
                            Agents.Parameters[i].Radius + Agents.Parameters[j].Radius + Context.Settings.Vo.SafetyMargin,
                            Context.Settings.TimeHorizon, Context.DeltaTime, Agents.Ids[i], Agents.Ids[j], share);
                    }
                Counts[i] = count;
                Output.Status[i] = VolumeVelocityOptimizer.Solve(new NativeSlice<VelocityPlane3D>(Planes, start, count), hard,
                    Preferred[i], speed, Context.Settings.Epsilon, out float3 velocity, out float residual, out int failure);
                Output.Velocities[i] = Candidates[i] = velocity; Residuals[i] = residual; Failed[i] = failure;
            }
        }
        [BurstCompile]
        private struct SafetyJob : IJob
        {
            [ReadOnly] public AgentReadView Agents;
            [ReadOnly] public VolumeQuery Query;
            public MotionOutput Output;
            public NativeArray<int> Parents, Stats;
            public NativeArray<float> Scales, Fractions;
            public NativeParallelMultiHashMap<int3, int> Buckets;
            public float DeltaTime;
            public bool Dynamic, BruteForce;
            public void Execute()
            {
                Stats[0] = Stats[1] = Stats[2] = 0; float maximumReach = 0;
                for (int i = 0; i < Agents.Count; i++)
                {
                    Parents[i] = i; Scales[i] = 1;
                    maximumReach = math.max(maximumReach, Agents.Parameters[i].Radius + Agents.Parameters[i].MaxSpeed * DeltaTime);
                    float3 p = Agents.Positions[i], v = Output.Velocities[i];
                    if (!math.all(math.isfinite(v)) || !Query.SegmentClear(p, p)) Stats[2]++;
                    Fractions[i] = Query.SafeFraction(p, p + v * DeltaTime);
                }
                if (Dynamic)
                {
                    float cellSize = 2 * maximumReach + 0.002f;
                    Buckets.Clear(); for (int i = 0; i < Agents.Count; i++) Buckets.Add((int3)math.floor(Agents.Positions[i] / cellSize), i);
                    for (int i = 0; i < Agents.Count; i++)
                    {
                        if (BruteForce) { for (int j = 0; j < i; j++) Pair(i, j); continue; }
                        int3 c = (int3)math.floor(Agents.Positions[i] / cellSize);
                        for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
                        {
                            if (!Buckets.TryGetFirstValue(c + new int3(x, y, z), out int j, out var it)) continue;
                            do { if (j < i) Pair(i, j); } while (Buckets.TryGetNextValue(out j, ref it));
                        }
                    }
                }
                for (int i = 0; i < Agents.Count; i++) { int root = Root(i); Scales[root] = math.min(Scales[root], Fractions[i]); }
                for (int i = 0; i < Agents.Count; i++)
                {
                    float scale = Scales[Root(i)]; Fractions[i] = scale;
                    if (scale < 1) { Output.Velocities[i] *= scale; Stats[0]++; }
                }
            }
            private int Root(int i)
            { while (Parents[i] != i) { Parents[i] = Parents[Parents[i]]; i = Parents[i]; } return i; }
            private void Pair(int i, int j)
            {
                Stats[1]++;
                float3 p = Agents.Positions[i] - Agents.Positions[j];
                float radius = Agents.Parameters[i].Radius + Agents.Parameters[j].Radius;
                float d2 = math.lengthsq(p), reach = radius + (Agents.Parameters[i].MaxSpeed + Agents.Parameters[j].MaxSpeed) * DeltaTime + 0.002f;
                if (d2 > reach * reach) return;
                int a = Root(i), b = Root(j); if (a != b) Parents[math.max(a, b)] = math.min(a, b);
                if (d2 < (radius - 0.0001f) * (radius - 0.0001f)) { Stats[2]++; return; }
                float3 d = (Output.Velocities[i] - Output.Velocities[j]) * DeltaTime;
                float dot = math.dot(p, d); if (dot >= 0) return;
                float aa = math.lengthsq(d), c = d2 - (radius + 0.001f) * (radius + 0.001f);
                float discriminant = dot * dot - aa * c; if (discriminant < 0 || aa < 1e-12f) return;
                float contact = c <= 0 ? 0 : c / (-dot + math.sqrt(discriminant));
                if (contact > 1) return;
                float fraction = math.max(0, contact - 0.0001f / math.max(math.sqrt(aa), 0.0001f));
                Fractions[i] = math.min(Fractions[i], fraction); Fractions[j] = math.min(Fractions[j], fraction);
            }
        }
        public void Dispose()
        {
            if (planes.IsCreated) planes.Dispose(); if (counts.IsCreated) counts.Dispose(); if (hardCounts.IsCreated) hardCounts.Dispose();
            if (failed.IsCreated) failed.Dispose(); if (truncated.IsCreated) truncated.Dispose(); if (parents.IsCreated) parents.Dispose();
            if (residuals.IsCreated) residuals.Dispose(); if (scales.IsCreated) scales.Dispose(); if (fractions.IsCreated) fractions.Dispose();
            if (candidates.IsCreated) candidates.Dispose(); if (safetyStats.IsCreated) safetyStats.Dispose(); if (safetyBuckets.IsCreated) safetyBuckets.Dispose();
            planes = default; counts = hardCounts = failed = truncated = parents = default;
            residuals = scales = fractions = default; candidates = default; safetyStats = default; safetyBuckets = default;
        }
    }
    public sealed class VolumeStepDebug
    {
        public long InputTick;
        public int AgentId, FailedPlane;
        public float3 Position, Preferred, Candidate, Final;
        public float Residual, SafetyScale;
        public VelocityPlane3D[] Planes;
        public float3[] Neighbors;
    }
}
