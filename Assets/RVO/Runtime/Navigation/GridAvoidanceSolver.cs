using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>Burst 并行组合求解；独立、无 K 截断的安全空间哈希覆盖整段扫掠。</summary>
    public sealed class GridAvoidanceSolver : IAvoidanceSolver
    {
        public string Name => "Parallel grid ORCA / BVH / swept hash";
        public bool IsImplemented => true;
        public float LastSafetyScale { get; private set; } = 1;
        public int SafetyLimitedTicks { get; private set; }
        public long LastSafetyPairChecks { get; private set; }
        public int LastStaticTruncations { get; private set; }
        public int LastLimitedAgents { get; private set; }
        private readonly GridNavigation navigation;
        private NativeArray<VelocityHalfPlane2D> constraints;
        private NativeArray<ObstacleNode> nodes;
        private NativeArray<float> limits;
        private NativeArray<float> staticDistances;
        private NativeArray<int> pairChecks, droppedStatic;
        private NativeArray<int> components;
        private NativeArray<float> componentLimits;
        private NativeParallelMultiHashMap<int2,int> safetyBuckets;
        private float maxRadius, maxSpeed, safetyCellSize;
        private int stride;
        public GridAvoidanceSolver(GridNavigation navigation) { this.navigation = navigation; }
        private void Initialize(in AgentReadView agents, int maxNeighbors, float dt)
        {
            if (constraints.IsCreated) return;
            try
            {
                stride = maxNeighbors + navigation.Settings.MaxStaticConstraints + 4;
                constraints = new NativeArray<VelocityHalfPlane2D>(checked(agents.Count * stride),Allocator.Persistent);
                staticDistances = new NativeArray<float>(checked(agents.Count * navigation.Settings.MaxStaticConstraints),Allocator.Persistent);
                nodes = new NativeArray<ObstacleNode>(navigation.Map.NodeCount,Allocator.Persistent);
                for (int i = 0; i < nodes.Length; i++) nodes[i] = navigation.Map.Node(i);
                limits = new NativeArray<float>(agents.Count,Allocator.Persistent);
                pairChecks = new NativeArray<int>(agents.Count,Allocator.Persistent);
                droppedStatic = new NativeArray<int>(agents.Count,Allocator.Persistent);
                components = new NativeArray<int>(agents.Count,Allocator.Persistent);
                componentLimits = new NativeArray<float>(agents.Count,Allocator.Persistent);
                safetyBuckets = new NativeParallelMultiHashMap<int2,int>(agents.Count,Allocator.Persistent);
                for (int i = 0; i < agents.Count; i++)
                { maxRadius = math.max(maxRadius,agents.Parameters[i].Radius); maxSpeed = math.max(maxSpeed,agents.Parameters[i].MaxSpeed); }
                // 一步最大相对扫掠半径决定桶宽，与 ORCA 的长时间感知半径分开。
                safetyCellSize = 2 * (maxRadius + maxSpeed * dt) + 0.002f;
            }
            catch { Dispose(); throw; }
        }
        public JobHandle Schedule(in StepContext context, in AgentReadView agents,
            NativeArray<float3>.ReadOnly preferred, in NeighborReadView neighbors, in MotionOutput motionOutput, JobHandle dependency)
        {
            if (!constraints.IsCreated) { dependency.Complete(); Initialize(agents,neighbors.MaxNeighbors,context.DeltaTime); }
            var solve = new SolveJob { Context = context, Agents = agents, Preferred = preferred, Neighbors = neighbors,
                Output = motionOutput, Constraints = constraints, Nodes = nodes, Stride = stride,
                MaxStatic = navigation.Settings.MaxStaticConstraints, Margin = navigation.Settings.SafetyMargin,
                Horizon = math.max(context.DeltaTime,navigation.Settings.StaticTimeHorizon),
                MapMin = navigation.Map.Min, MapMax = navigation.Map.Max, DroppedStatic = droppedStatic };
            solve.StaticDistances = staticDistances; solve.Priorities = navigation.TrafficPriorities;
            var build = new SafetyBuildJob { Agents = agents, Buckets = safetyBuckets, CellSize = safetyCellSize };
            var safety = new SafetyJob { Agents = agents, Velocities = motionOutput.Velocities, Nodes = nodes,
                Buckets = safetyBuckets, CellSize = safetyCellSize, MaxRadius = maxRadius, MaxSpeed = maxSpeed,
                DeltaTime = context.DeltaTime, Margin = navigation.Settings.SafetyMargin, Limits = limits, PairChecks = pairChecks,
                MapMin = navigation.Map.Min, MapMax = navigation.Map.Max, BruteForce = context.Settings.Backend == ExecutionBackend.Reference };
            var islands = new SafetyIslandsJob { Agents = agents, Buckets = safetyBuckets,
                CellSize = safetyCellSize, MaxRadius = maxRadius, MaxSpeed = maxSpeed,
                DeltaTime = context.DeltaTime, Parents = components, ComponentLimits = componentLimits,
                Limits = limits, Output = motionOutput };
            if (context.Settings.Backend == ExecutionBackend.JobsBurst)
            {
                JobHandle solved = default, built = default, checkedMotion = default;
                try
                {
                    solved = solve.Schedule(agents.Count,32,dependency);
                    built = build.Schedule(dependency);
                    checkedMotion = safety.Schedule(agents.Count,32,JobHandle.CombineDependencies(solved,built));
                    checkedMotion.Complete();
                }
                catch { solved.Complete(); built.Complete(); checkedMotion.Complete(); throw; }
            }
            else
            {
                dependency.Complete(); build.Execute();
                for (int i = 0; i < agents.Count; i++) solve.Execute(i);
                for (int i = 0; i < agents.Count; i++) safety.Execute(i);
            }
            float scale = 1; LastSafetyPairChecks = 0; LastStaticTruncations = 0; LastLimitedAgents = 0;
            for (int i = 0; i < agents.Count; i++)
            { scale = math.min(scale,limits[i]); LastSafetyPairChecks += pairChecks[i]; LastStaticTruncations += droppedStatic[i]; }
            if (scale < 0) throw new InvalidOperationException("非法初始重叠或非有限运动数据，拒绝提交仿真步骤。");
            LastSafetyScale = scale;
            if (scale < 1)
            {
                SafetyLimitedTicks++;
                if (context.Settings.Backend == ExecutionBackend.JobsBurst) islands.Schedule().Complete();
                else islands.Execute();
                for (int i = 0; i < agents.Count; i++) if (componentLimits[components[i]] < 1) LastLimitedAgents++;
            }
            return default;
        }

        // 只在安全证书触发时构建本步相互作用连通分量。不同分量在任意 [0,1] 缩放下
        // 均不可接触；分量内统一取时间前缀，保留相对轨迹，避免不安全的逐 agent 停车。
        [BurstCompile]
        private struct SafetyIslandsJob : IJob
        {
            [ReadOnly] public AgentReadView Agents;
            [ReadOnly] public NativeParallelMultiHashMap<int2,int> Buckets;
            [ReadOnly] public NativeArray<float> Limits;
            public NativeArray<int> Parents;
            public NativeArray<float> ComponentLimits;
            public MotionOutput Output;
            public float CellSize, MaxRadius, MaxSpeed, DeltaTime;
            public void Execute()
            {
                for (int i = 0; i < Agents.Count; i++) { Parents[i] = i; ComponentLimits[i] = 1; }
                for (int i = 0; i < Agents.Count; i++)
                {
                    float2 p = Agents.Positions[i].xz;
                    float range = Agents.Parameters[i].Radius + MaxRadius + (Agents.Parameters[i].MaxSpeed + MaxSpeed) * DeltaTime + 0.002f;
                    int2 lo = (int2)math.floor((p-range)/CellSize), hi = (int2)math.floor((p+range)/CellSize);
                    for (int z = lo.y; z <= hi.y; z++) for (int x = lo.x; x <= hi.x; x++)
                        if (Buckets.TryGetFirstValue(new int2(x,z),out int j,out var iterator))
                            do
                            {
                                if (j >= i) continue;
                                float reach = Agents.Parameters[i].Radius + Agents.Parameters[j].Radius +
                                    (Agents.Parameters[i].MaxSpeed + Agents.Parameters[j].MaxSpeed) * DeltaTime + 0.002f;
                                if (math.distancesq(p, Agents.Positions[j].xz) > reach*reach) continue;
                                int a = Root(i), b = Root(j);
                                if (a != b) Parents[math.max(a,b)] = math.min(a,b);
                            } while (Buckets.TryGetNextValue(out j,ref iterator));
                }
                for (int i = 0; i < Agents.Count; i++)
                { int root = Root(i); Parents[i] = root; ComponentLimits[root] = math.min(ComponentLimits[root], Limits[i]); }
                for (int i = 0; i < Agents.Count; i++)
                {
                    float scale = ComponentLimits[Parents[i]];
                    if (scale >= 1) continue;
                    Output.Velocities[i] *= scale;
                    if (Output.Status[i] == SolveStatus.Success) Output.Status[i] = SolveStatus.Fallback;
                }
            }
            private int Root(int i)
            {
                while (Parents[i] != i) { Parents[i] = Parents[Parents[i]]; i = Parents[i]; }
                return i;
            }
        }

        [BurstCompile]
        private struct SolveJob : IJobParallelFor
        {
            public StepContext Context;
            [ReadOnly] public AgentReadView Agents;
            [ReadOnly] public NativeArray<float3>.ReadOnly Preferred;
            [ReadOnly] public NeighborReadView Neighbors;
            [ReadOnly] public NativeArray<ObstacleNode> Nodes;
            [ReadOnly] public NativeArray<float>.ReadOnly Priorities;
            public MotionOutput Output;
            [NativeDisableParallelForRestriction] public NativeArray<VelocityHalfPlane2D> Constraints;
            [NativeDisableParallelForRestriction] public NativeArray<float> StaticDistances;
            public NativeArray<int> DroppedStatic;
            public int Stride, MaxStatic;
            public float Margin, Horizon;
            public float2 MapMin, MapMax;
            public void Execute(int i)
            {
                int start = i*Stride, count = 0, staticCount = 0, dropped = 0;
                float2 position = Agents.Positions[i].xz; float radius = Agents.Parameters[i].Radius + Margin;
                float reach = Agents.Parameters[i].MaxSpeed * Horizon;
                int index = 0;
                while (index < Nodes.Length)
                {
                    var node = Nodes[index]; float2 separation = position - math.clamp(position,node.Min-radius,node.Max+radius);
                    float distanceSq = math.lengthsq(separation);
                    if (distanceSq > reach*reach) { index = node.Escape; continue; }
                    index++; if (node.Leaf == 0) continue;
                    float gap = math.sqrt(distanceSq);
                    if (gap < 1e-7f) continue; // 接触边界交给全量扫掠安全层，禁止生成零法线。
                    // BVH 遍历顺序不代表危险程度，预算内保留最近的静态平面。
                    int insert = staticCount, distanceStart = i * MaxStatic;
                    if (staticCount == MaxStatic) dropped++;
                    while (insert > 0 && StaticDistances[distanceStart+insert-1] > distanceSq)
                    {
                        if (insert < MaxStatic)
                        { StaticDistances[distanceStart+insert] = StaticDistances[distanceStart+insert-1]; Constraints[start+insert] = Constraints[start+insert-1]; }
                        insert--;
                    }
                    if (insert < MaxStatic)
                    {
                        StaticDistances[distanceStart+insert] = distanceSq;
                        Constraints[start+insert] = new VelocityHalfPlane2D { Normal = separation/gap, Offset = -gap/Horizon, SourceId = -index };
                    }
                    staticCount = math.min(MaxStatic, staticCount+1);
                }
                count = staticCount;
                float2 low = position-MapMin-radius, high = MapMax-radius-position;
                Constraints[start+count++] = new VelocityHalfPlane2D { Normal = new float2(1,0), Offset = -low.x/Horizon, SourceId = -1 };
                Constraints[start+count++] = new VelocityHalfPlane2D { Normal = new float2(-1,0), Offset = -high.x/Horizon, SourceId = -1 };
                Constraints[start+count++] = new VelocityHalfPlane2D { Normal = new float2(0,1), Offset = -low.y/Horizon, SourceId = -1 };
                Constraints[start+count++] = new VelocityHalfPlane2D { Normal = new float2(0,-1), Offset = -high.y/Horizon, SourceId = -1 };
                for (int slot = 0; slot < Neighbors.Counts[i]; slot++)
                {
                    int j = Neighbors.Indices[i*Neighbors.MaxNeighbors+slot];
                    Constraints[start+count++] = OrcaGeometry2D.Build(Agents.Positions[j].xz-position,Agents.Velocities[i].xz,Agents.Velocities[j].xz,
                        Agents.Parameters[i].Radius+Agents.Parameters[j].Radius+Context.Settings.Vo.SafetyMargin,
                        Context.Settings.TimeHorizon,Context.DeltaTime,Agents.Ids[i],Agents.Ids[j],Context.Settings.Epsilon,
                        Priorities[j]/(Priorities[i]+Priorities[j]));
                }
                var status = PlanarVelocityOptimizer.Solve(new NativeSlice<VelocityHalfPlane2D>(Constraints,start,count),
                    Preferred[i].xz,Agents.Parameters[i].MaxSpeed,Context.Settings.Epsilon,out float2 velocity,staticCount+4);
                DroppedStatic[i] = dropped;
                Output.Status[i] = dropped > 0 && status == SolveStatus.Success ? SolveStatus.Fallback : status;
                Output.Velocities[i] = new float3(velocity.x,0,velocity.y);
            }
        }
        [BurstCompile]
        private struct SafetyBuildJob : IJob
        {
            [ReadOnly] public AgentReadView Agents;
            public NativeParallelMultiHashMap<int2,int> Buckets;
            public float CellSize;
            public void Execute()
            { Buckets.Clear(); for (int i = 0; i < Agents.Count; i++) Buckets.Add((int2)math.floor(Agents.Positions[i].xz/CellSize),i); }
        }
        [BurstCompile]
        private struct SafetyJob : IJobParallelFor
        {
            [ReadOnly] public AgentReadView Agents;
            [ReadOnly] public NativeArray<float3> Velocities;
            [ReadOnly] public NativeArray<ObstacleNode> Nodes;
            [ReadOnly] public NativeParallelMultiHashMap<int2,int> Buckets;
            public NativeArray<float> Limits;
            public NativeArray<int> PairChecks;
            public float CellSize, MaxRadius, MaxSpeed, DeltaTime, Margin;
            public float2 MapMin, MapMax;
            public bool BruteForce;
            public void Execute(int i)
            {
                float2 p = Agents.Positions[i].xz, end = p + Velocities[i].xz*DeltaTime;
                float radius = Agents.Parameters[i].Radius;
                float limit = 1; int checks = 0;
                if (!math.all(math.isfinite(p)) || !math.all(math.isfinite(end)) || math.any(p < MapMin+radius) || math.any(p > MapMax-radius))
                { Limits[i] = -1; PairChecks[i] = 0; return; }
                float2 displacement = end-p;
                for (int axis = 0; axis < 2; axis++)
                {
                    if (end[axis] <= MapMin[axis]+radius+Margin && displacement[axis] < 0)
                        limit = math.min(limit,SafePrefix((MapMin[axis]+radius+Margin-p[axis])/displacement[axis]));
                    if (end[axis] >= MapMax[axis]-radius-Margin && displacement[axis] > 0)
                        limit = math.min(limit,SafePrefix((MapMax[axis]-radius-Margin-p[axis])/displacement[axis]));
                }
                int index = 0;
                while (index < Nodes.Length)
                {
                    var node = Nodes[index];
                    if (!ObstacleBvh.SegmentBox(p,end,node.Min-radius-Margin,node.Max+radius+Margin,out float enter)) { index = node.Escape; continue; }
                    index++; if (node.Leaf == 0) continue;
                    if (math.all(p > node.Min-radius) && math.all(p < node.Max+radius)) { limit = -1; break; }
                    // 起点在额外安全余量内时，允许远离/沿边移动；仍独立验证完整物理扫掠。
                    // 否则 enter=0 会让合法初态永远无法退回导航净空。
                    float2 separation = p-math.clamp(p,node.Min-radius,node.Max+radius);
                    if (enter == 0 && math.dot(displacement,separation) >= 0 &&
                        !ObstacleBvh.SegmentBox(p,end,node.Min-radius,node.Max+radius,out _)) continue;
                    limit = math.min(limit,SafePrefix(enter));
                }
                if (BruteForce)
                { for (int j = 0; j < i; j++) { checks++; limit = math.min(limit,Pair(i,j,p,end)); } }
                else
                {
                    float range = radius+MaxRadius+(Agents.Parameters[i].MaxSpeed+MaxSpeed)*DeltaTime+0.002f;
                    int2 lo = (int2)math.floor((p-range)/CellSize), hi = (int2)math.floor((p+range)/CellSize);
                    for (int z = lo.y; z <= hi.y; z++) for (int x = lo.x; x <= hi.x; x++)
                        if (Buckets.TryGetFirstValue(new int2(x,z),out int j,out var iterator))
                            do { if (j < i) { checks++; limit = math.min(limit,Pair(i,j,p,end)); } }
                            while (Buckets.TryGetNextValue(out j,ref iterator));
                }
                Limits[i] = limit; PairChecks[i] = checks;
            }
            private float Pair(int i, int j, float2 p, float2 end)
            {
                float2 other = Agents.Positions[j].xz, otherEnd = other+Velocities[j].xz*DeltaTime;
                return PairSafePrefix(p,other,end,otherEnd,Agents.Parameters[i].Radius+Agents.Parameters[j].Radius);
            }
        }
        // 解析求首次接触时间，替代最坏 24 次全局二分扫掠。double 减少相对位移消减误差。
        public static float PairSafePrefix(float2 a, float2 b, float2 nextA, float2 nextB, float radius)
        {
            double2 p = (double2)a-(double2)b, d = ((double2)nextA-(double2)nextB)-p;
            double squared = math.lengthsq(p), guard = radius+0.001;
            if (squared < (double)radius*radius) return -1;
            double aa = math.lengthsq(d), bb = math.dot(p,d);
            // 接触余量内的静止/分离运动可安全继续，不能无条件返回 0 锁死整组。
            if (aa < 1e-20 || bb >= 0) return 1;
            double c = squared-guard*guard;
            if (c <= 0) return 0;
            double discriminant = bb*bb-aa*c;
            if (discriminant <= 0) return 1;
            double contact = c/(-bb+math.sqrt(discriminant)); // 稳定形式，避免两个近似大数相减。
            return contact <= 1 ? SafePrefix((float)contact) : 1;
        }
        private static float SafePrefix(float contact) => math.clamp((contact-1e-5f)*0.98f,0,1);
        public void Dispose()
        {
            if (constraints.IsCreated) constraints.Dispose(); constraints = default;
            if (nodes.IsCreated) nodes.Dispose(); nodes = default;
            if (limits.IsCreated) limits.Dispose(); limits = default;
            if (staticDistances.IsCreated) staticDistances.Dispose(); staticDistances = default;
            if (pairChecks.IsCreated) pairChecks.Dispose(); pairChecks = default;
            if (droppedStatic.IsCreated) droppedStatic.Dispose(); droppedStatic = default;
            if (components.IsCreated) components.Dispose(); components = default;
            if (componentLimits.IsCreated) componentLimits.Dispose(); componentLimits = default;
            if (safetyBuckets.IsCreated) safetyBuckets.Dispose(); safetyBuckets = default;
        }
    }
}
