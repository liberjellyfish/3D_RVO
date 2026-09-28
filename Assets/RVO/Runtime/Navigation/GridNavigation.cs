using System;
using System.Buffers;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    public struct GridPathInfo
    {
        public int AgentId, RequestId, MapVersion, Count, Cursor;
        public GridPathStatus Status;
        public float2 Goal;
    }

    /// <summary>只读烘焙地图 + 有预算的路径队列；一次运行中绝不刷新障碍。</summary>
    public sealed class GridNavigation : IPreferredVelocityProvider
    {
        public string Name => "Time-sliced weighted A* and visible-goal fast path";
        public bool IsImplemented => true;
        public NavigationGrid Map { get; }
        public NavigationSettings Settings { get; }
        public int ReplanCount { get; private set; }
        public int NoPathCount { get; private set; }
        public int PendingCount { get; private set; }
        public int ArrivedCount { get; private set; }
        public int ReadyCount { get; private set; }
        public int LastDirectPaths { get; private set; }
        public int LastExpandedNodes { get; private set; }
        public long TotalExpandedNodes { get; private set; }
        public long PathCapacityBytes { get; private set; }
        private readonly GridPathfinder[] searches;
        private readonly int[] active;
        private readonly bool[] searching;
        private readonly GridPathInfo[] paths;
        private readonly float2[][] waypoints;
        private readonly float2[] scratch;
        private readonly NavigationLandmarks landmarks;
        private readonly GridPathFollower follower;
        private readonly int[] queue;
        private readonly bool[] queued;
        private int head, tail, queueCount, nextSlot, requestId;
        private bool disposed;
        public GridPathInfo Path(int agent) => paths[agent];
        public float2 Waypoint(int agent, int point) => waypoints[agent][point];

        public GridNavigation(in NavigationSettings settings, int count, float radius, NavigationGrid bakedMap)
        {
            settings.Validate(radius); Settings = settings;
            Map = bakedMap ?? throw new ArgumentNullException(nameof(bakedMap), "必须先烘焙地图。");
            if (Map.Width != settings.Width || Map.Height != settings.Height || Map.CellSize != settings.CellSize ||
                math.abs(Map.ClearanceRadius - radius - settings.SafetyMargin) > 1e-5f) throw new ArgumentException("烘焙地图尺寸或净空不匹配。");
            paths = new GridPathInfo[count]; waypoints = new float2[count][];
            queue = new int[count]; queued = new bool[count]; scratch = new float2[Map.Count + 2];
            int slots = math.min(count, settings.EffectiveSearchSlots);
            searches = new GridPathfinder[slots]; active = new int[slots]; searching = new bool[count];
            for (int slot = 0; slot < slots; slot++) { searches[slot] = new GridPathfinder(Map.Count); active[slot] = -1; }
            landmarks = Map.Landmarks;
            _ = Map.JumpTargets;
            follower = new GridPathFollower(Map,count);
        }
        private void Request(int i, in AgentReadView agents)
        {
            // 目标改变时更新排队请求，并取消旧搜索；不可提交旧目标的结果。
            if (searching[i])
                for (int slot = 0; slot < active.Length; slot++) if (active[slot] == i)
                { active[slot] = -1; searching[i] = false; break; }
            var path = paths[i]; path.AgentId = agents.Ids[i]; path.RequestId = ++requestId;
            path.MapVersion = Map.Version; path.Goal = agents.Goals[i].xz; path.Status = GridPathStatus.Pending;
            path.Count = 0; path.Cursor = 1; ReplanCount++;
            if (Map.SegmentClear(agents.Positions[i].xz, path.Goal, Map.ClearanceRadius))
            {
                scratch[0] = agents.Positions[i].xz; scratch[1] = path.Goal;
                path.Count = 2; path.Status = GridPathStatus.Ready; StorePath(i, 2); LastDirectPaths++;
            }
            else if (!queued[i])
            { queued[i] = true; queue[tail] = i; tail = (tail + 1) % queue.Length; queueCount++; }
            paths[i] = path;
        }
        private void StorePath(int i, int count)
        {
            if (waypoints[i] == null || waypoints[i].Length < count)
            {
                if (waypoints[i] != null) { PathCapacityBytes -= waypoints[i].Length * 8L; ArrayPool<float2>.Shared.Return(waypoints[i]); }
                waypoints[i] = ArrayPool<float2>.Shared.Rent(count); PathCapacityBytes += waypoints[i].Length * 8L;
            }
            Array.Copy(scratch, waypoints[i], count);
        }
        public JobHandle Schedule(in StepContext context, in AgentReadView agents, NativeArray<float3> preferred, JobHandle dependency)
        {
            dependency.Complete(); if (disposed) throw new ObjectDisposedException(nameof(GridNavigation));
            LastDirectPaths = 0;
            for (int i = 0; i < agents.Count; i++)
            {
                var path = paths[i]; float2 position = agents.Positions[i].xz;
                if (path.RequestId == 0 || math.any(path.Goal != agents.Goals[i].xz) ||
                    (path.Status == GridPathStatus.Arrived && math.distance(position, path.Goal) > agents.Parameters[i].ArrivalDistance)) Request(i, agents);
            }
            LastExpandedNodes = 0; int completed = 0, idleSlots = 0;
            while (LastExpandedNodes < Settings.PathExpansionsPerTick && completed < Settings.PathRequestsPerTick)
            {
                int slot = nextSlot; nextSlot = (nextSlot + 1) % active.Length;
                if (active[slot] < 0)
                {
                    while (queueCount > 0)
                    {
                        int i = queue[head]; head = (head + 1) % queue.Length; queueCount--; queued[i] = false;
                        if (paths[i].Status != GridPathStatus.Pending) continue;
                        active[slot] = i; searching[i] = true;
                        searches[slot].Begin(Map, agents.Positions[i].xz, paths[i].Goal, true, Settings.EffectiveHeuristicWeight, landmarks);
                        break;
                    }
                    if (active[slot] < 0) { if (++idleSlots >= active.Length) break; continue; }
                }
                idleSlots = 0;
                int agent = active[slot]; var pathfinder = searches[slot];
                LastExpandedNodes += pathfinder.Advance(math.min(256, Settings.PathExpansionsPerTick - LastExpandedNodes));
                if (pathfinder.Status == GridPathStatus.Pending) continue;
                var path = paths[agent]; path.Status = pathfinder.Status; path.Cursor = 1;
                path.Count = pathfinder.CopyPath(scratch, 0, true);
                if (path.Count > 0)
                {
                    StorePath(agent, path.Count);
                    if (!Map.SegmentClear(agents.Positions[agent].xz, Waypoint(agent, 1), Map.ClearanceRadius))
                    { path.RequestId = 0; path.Status = GridPathStatus.Pending; }
                }
                paths[agent] = path; active[slot] = -1; searching[agent] = false; completed++;
            }
            TotalExpandedNodes += LastExpandedNodes; NoPathCount = PendingCount = ArrivedCount = ReadyCount = 0;
            follower.Execute(context,agents,preferred,Map,Settings,paths,waypoints);
            for (int i = 0; i < agents.Count; i++)
            {
                var path = paths[i]; int advance = follower.Advance(i);
                if (advance == -2)
                { if (!queued[i] && !searching[i]) path.Status = GridPathStatus.Arrived; ArrivedCount++; paths[i] = path; continue; }
                if (advance == -1)
                { Request(i,agents); if (paths[i].Status == GridPathStatus.Pending) PendingCount++; else ReadyCount++; continue; }
                if (path.Status != GridPathStatus.Ready)
                { if (path.Status == GridPathStatus.Pending) PendingCount++; else NoPathCount++; continue; }
                path.Cursor += advance; ReadyCount++; paths[i] = path;
            }
            return default;
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            follower?.Dispose();
            for (int i = 0; i < waypoints.Length; i++) if (waypoints[i] != null)
            { ArrayPool<float2>.Shared.Return(waypoints[i]); waypoints[i] = null; }
            PathCapacityBytes = 0;
        }
    }

    internal sealed class GridScenarioInitializer : StatelessModule, IScenarioInitializer
    {
        public override string Name => "Baked-map spawn sampling";
        private readonly NavigationGrid map;
        public GridScenarioInitializer(NavigationGrid map) { this.map = map; }
        public void Initialize(in SimulationSettings settings, in ScenarioSettings scenario, in AgentInitializationView initialization)
        {
            var agents = initialization; var random = new Unity.Mathematics.Random(scenario.Seed);
            var starts = new SpawnIndex(map, scenario.Radius, settings.AgentCount);
            var goals = new SpawnIndex(map, scenario.Radius, settings.AgentCount);
            for (int i = 0; i < settings.AgentCount; i++)
            {
                agents.Ids[i] = i; agents.Velocities[i] = float3.zero;
                agents.Parameters[i] = new AgentParameters { Radius = scenario.Radius, MaxSpeed = scenario.MaxSpeed, ArrivalDistance = scenario.ArrivalDistance };
                agents.Positions[i] = starts.Pick(agents.Positions, i, settings.PlaneHeight, ref random);
                agents.Goals[i] = goals.Pick(agents.Goals, i, settings.PlaneHeight, ref random);
            }
        }
        // 初始化采样也用空间桶，仅检测局部九桶；起点/目标从烘焙的最大连通域抽取。
        private sealed class SpawnIndex
        {
            private readonly NavigationGrid map;
            private readonly int[] heads, next;
            private readonly int width, height;
            private readonly float spacing;
            public SpawnIndex(NavigationGrid map, float radius, int count)
            {
                this.map = map; spacing = 2 * radius + 0.3f;
                width = (int)math.ceil(map.Width * map.CellSize / spacing); height = (int)math.ceil(map.Height * map.CellSize / spacing);
                heads = new int[width * height]; for (int i = 0; i < heads.Length; i++) heads[i] = -1;
                next = new int[count];
            }
            public float3 Pick(NativeArray<float3> points, int count, float plane, ref Unity.Mathematics.Random random)
            {
                for (int attempt = 0; attempt < math.max(1024,map.SpawnCellCount); attempt++)
                {
                    if (map.SpawnCellCount == 0) break;
                    float2 p = map.Center(map.SpawnCell(random.NextInt(map.SpawnCellCount)));
                    int2 cell = (int2)math.floor((p - map.Min) / spacing); bool valid = true;
                    for (int z = math.max(0,cell.y-1); z <= math.min(height-1,cell.y+1) && valid; z++)
                        for (int x = math.max(0,cell.x-1); x <= math.min(width-1,cell.x+1) && valid; x++)
                            for (int j = heads[z*width+x]; j >= 0; j = next[j])
                                if (math.distancesq(p,points[j].xz) < spacing*spacing) { valid = false; break; }
                    if (!valid) continue;
                    int key = cell.y*width+cell.x; next[count] = heads[key]; heads[key] = count;
                    return new float3(p.x,plane,p.y);
                }
                throw new InvalidOperationException("最大连通区域无法容纳当前数量/半径，请降低档位或重新烘焙。");
            }
        }
    }

    public static class Phase2ModuleFactory
    {
        public static SimulationModules Create(in SimulationSettings settings, in ScenarioSettings scenario,
            in NavigationSettings navigationSettings, NavigationGrid bakedMap, out GridNavigation navigation, out GridAvoidanceSolver solver)
        {
            settings.Validate(); scenario.Validate(settings.AgentCount); navigationSettings.Validate(scenario.Radius);
            if (settings.Dimension != SimulationDimension.PlanarXZ || settings.Avoidance != AvoidanceAlgorithm.ORCA)
                throw new NotSupportedException("Phase 2 需要 PlanarXZ + ORCA。");
            navigation = new GridNavigation(navigationSettings,settings.AgentCount,scenario.Radius,bakedMap);
            solver = new GridAvoidanceSolver(navigation);
            INeighborSearch neighbors = NeighborSearchFactory.Create(settings.NeighborSearch);
            return new SimulationModules(new GridScenarioInitializer(bakedMap),navigation,neighbors,solver,new PlanarEulerIntegrator());
        }
    }
}
