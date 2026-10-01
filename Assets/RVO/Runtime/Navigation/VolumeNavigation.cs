using System;
using System.Buffers;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    public sealed class VolumeNavigation : IPreferredVelocityProvider
    {
        public string Name => "Budgeted sparse XYZ A*";
        public bool IsImplemented => true;
        public NavigationVolume Map { get; }
        public VolumeSettings Settings { get; }
        public VolumeQuery Query { get; private set; }
        public int PendingCount { get; private set; }
        public int ArrivedCount { get; private set; }
        public int FailedCount { get; private set; }
        public int LastExpandedNodes { get; private set; }
        public int RequestCount { get; private set; }
        public int RecoveryReplans { get; private set; }
        public int LastDirectPaths { get; private set; }
        public int CoarsePaths { get; private set; }
        public int CoarseFallbacks { get; private set; }
        public long PathCapacityBytes { get; private set; }
        public VolumeTrafficRecovery Traffic { get; }
        private readonly IVolumePathfinder[] searches;
        private VolumeQuery coarseQuery;
        private readonly int[] active, queue;
        private readonly bool[] queued;
        private readonly bool[] coarseSearch;
        private readonly long[] firstReadyTicks;
        private readonly VolumePathInfo[] paths;
        private readonly float3[][] waypoints;
        private readonly float3[] scratch, penaltyCenters;
        private readonly double[] penaltyUntil, retryAt;
        private NativeArray<float3> points;
        private NativeArray<int> counts, advances;
        private NativeArray<float4> penalties;
        private NativeArray<float> coarseLandmarks;
        private int head, tail, queuedCount, nextSlot, recoveryCursor, retryCursor;
        private bool disposed;
        public VolumePathInfo Path(int i) => paths[i];
        public float3 Waypoint(int agent, int index) => waypoints[agent][index];
        public long FirstReadyTick(int agent) => firstReadyTicks[agent];
        public VolumeNavigation(in VolumeSettings settings, NavigationVolume map, int count, ExecutionBackend backend = ExecutionBackend.Reference)
        {
            Map = map; Settings = settings;
            paths = new VolumePathInfo[count]; waypoints = new float3[count][];
            queue = new int[count]; queued = new bool[count]; penaltyCenters = new float3[count]; penaltyUntil = new double[count]; retryAt = new double[count];
            searches = new IVolumePathfinder[math.min(count, settings.SearchSlots)]; active = new int[searches.Length];
            coarseSearch = new bool[searches.Length]; firstReadyTicks = new long[count];
            for (int i = 0; i < count; i++) firstReadyTicks[i] = -1;
            scratch = new float3[settings.SearchCapacity + 2];
            try
            {
                if (settings.UseCoarseRoutes && settings.HeuristicWeight > 1 && map.Coarse != null) map.Coarse.PrepareLandmarks();
                // 每个世界一份原生表，搜索槽只借用；释放时先槽、后表。
                if (backend == ExecutionBackend.JobsBurst)
                    coarseLandmarks = new NativeArray<float>(map.Coarse?.Landmarks?.Distances ?? Array.Empty<float>(), Allocator.Persistent);
                Query = new VolumeQuery { Nodes = new NativeArray<VolumeBvhNode>(map.Nodes, Allocator.Persistent), Min = map.Min, Max = map.Max, Clearance = map.ClearanceRadius };
                if (backend == ExecutionBackend.JobsBurst && map.Coarse != null)
                    coarseQuery = new VolumeQuery { Nodes = new NativeArray<VolumeBvhNode>(map.Coarse.Nodes, Allocator.Persistent),
                        Min = map.Coarse.Min, Max = map.Coarse.Max, Clearance = map.Coarse.ClearanceRadius };
                for (int i = 0; i < searches.Length; i++)
                {
                    searches[i] = backend == ExecutionBackend.JobsBurst
                        ? (IVolumePathfinder)new BurstVolumePathfinder(settings.SearchCapacity, map, Query, coarseQuery, coarseLandmarks)
                        : new VolumePathfinder(settings.SearchCapacity);
                    active[i] = -1;
                }
                points = new NativeArray<float3>(count * 5, Allocator.Persistent); counts = new NativeArray<int>(count, Allocator.Persistent);
                advances = new NativeArray<int>(count, Allocator.Persistent); penalties = new NativeArray<float4>(count, Allocator.Persistent);
                Traffic = new VolumeTrafficRecovery(map, count);
            }
            catch { Dispose(); throw; }
        }
        private void Request(int i, in StepContext context, in AgentReadView agents)
        {
            for (int slot = 0; slot < active.Length; slot++) if (active[slot] == i) active[slot] = -1;
            var p = new VolumePathInfo { AgentId = agents.Ids[i], RequestId = ++RequestCount, MapVersion = Map.Version,
                Goal = agents.Goals[i], Cursor = 1, RequestedTick = context.Tick, ReadyTick = -1, Status = VolumePathStatus.Pending };
            if (context.SimulationTime >= penaltyUntil[i] && Map.SegmentClear(agents.Positions[i], p.Goal))
            {
                scratch[0] = agents.Positions[i]; scratch[1] = p.Goal; Store(i, 2);
                p.Count = 2; p.Status = VolumePathStatus.Ready; p.ReadyTick = context.Tick; LastDirectPaths++;
                if (firstReadyTicks[i] < 0) firstReadyTicks[i] = context.Tick;
            }
            else if (!queued[i]) { queued[i] = true; queue[tail] = i; tail = (tail + 1) % queue.Length; queuedCount++; }
            paths[i] = p;
        }
        private void Store(int i, int count)
        {
            if (waypoints[i] == null || waypoints[i].Length < count)
            {
                if (waypoints[i] != null) { PathCapacityBytes -= waypoints[i].Length * 12L; ArrayPool<float3>.Shared.Return(waypoints[i]); }
                waypoints[i] = ArrayPool<float3>.Shared.Rent(count); PathCapacityBytes += waypoints[i].Length * 12L;
            }
            Array.Copy(scratch, waypoints[i], count);
        }
        public JobHandle Schedule(in StepContext context, in AgentReadView agents, NativeArray<float3> preferred, JobHandle dependency)
        {
            dependency.Complete(); LastDirectPaths = 0;
            for (int i = 0; i < agents.Count; i++)
            {
                var path = paths[i]; bool changed = math.any(path.Goal != agents.Goals[i]);
                if (changed) { penaltyUntil[i] = 0; Traffic.Reset(i); }
                if (path.RequestId == 0 || changed || (path.Status == VolumePathStatus.Arrived && math.distance(agents.Positions[i], path.Goal) > agents.Parameters[i].ArrivalDistance))
                    Request(i, context, agents);
            }
            // Retry only endpoints that can have recovered; immutable NoPath/capacity failures need a new goal/reset.
            int retries = 0;
            for (int scanned = 0; scanned < agents.Count && retries < 4; scanned++)
            {
                int i = retryCursor; retryCursor = (retryCursor + 1) % agents.Count;
                if (paths[i].Status == VolumePathStatus.InvalidEndpoint && context.SimulationTime >= retryAt[i])
                { Request(i, context, agents); retryAt[i] = context.SimulationTime + 2; retries++; }
            }
            LastExpandedNodes = 0; int completed = 0, idle = 0;
            while (LastExpandedNodes < Settings.ExpansionsPerTick && completed < Settings.RequestsPerTick)
            {
                int slot = nextSlot; nextSlot = (nextSlot + 1) % active.Length;
                if (active[slot] < 0)
                {
                    while (queuedCount > 0)
                    {
                        int i = queue[head]; head = (head + 1) % queue.Length; queuedCount--; queued[i] = false;
                        if (paths[i].Status != VolumePathStatus.Pending) continue;
                        active[slot] = i;
                        coarseSearch[slot] = Settings.UseCoarseRoutes && Settings.HeuristicWeight > 1 && Map.Coarse != null;
                        searches[slot].Begin(coarseSearch[slot] ? Map.Coarse : Map, agents.Positions[i], paths[i].Goal, Settings.HeuristicWeight, true,
                            penaltyCenters[i], context.SimulationTime < penaltyUntil[i] ? agents.Parameters[i].Radius * 6 : 0, Map); break;
                    }
                    if (active[slot] < 0) { if (++idle == active.Length) break; continue; }
                }
                idle = 0; var search = searches[slot]; int agent = active[slot];
                LastExpandedNodes += search.Advance(math.min(256, Settings.ExpansionsPerTick - LastExpandedNodes));
                if (search.Status == VolumePathStatus.Pending) continue;
                if (coarseSearch[slot] && search.Status != VolumePathStatus.Ready)
                {
                    coarseSearch[slot] = false; CoarseFallbacks++;
                    search.Begin(Map, agents.Positions[agent], paths[agent].Goal, Settings.HeuristicWeight, true,
                        penaltyCenters[agent], context.SimulationTime < penaltyUntil[agent] ? agents.Parameters[agent].Radius * 6 : 0);
                    continue;
                }
                var path = paths[agent]; path.Status = search.Status; path.Count = search.CopyPath(scratch); path.Cursor = 1;
                if (path.Count > 0)
                {
                    if (coarseSearch[slot]) CoarsePaths++;
                    if (firstReadyTicks[agent] < 0) firstReadyTicks[agent] = context.Tick;
                    Store(agent, path.Count); path.ReadyTick = context.Tick;
                    if (!Map.SegmentClear(agents.Positions[agent], waypoints[agent][1])) path.RequestId = 0;
                }
                paths[agent] = path; active[slot] = -1; completed++;
            }
            for (int i = 0; i < agents.Count; i++)
            {
                var path = paths[i]; counts[i] = path.Status == VolumePathStatus.Ready ? math.min(5, path.Count - path.Cursor) : 0;
                for (int p = 0; p < counts[i]; p++) points[i * 5 + p] = waypoints[i][path.Cursor + p];
                penalties[i] = new float4(penaltyCenters[i], context.SimulationTime < penaltyUntil[i] ? agents.Parameters[i].Radius * 6 : 0);
            }
            var job = new FollowJob { Context = context, Agents = agents, Preferred = preferred, Points = points,
                Counts = counts, Advances = advances, Query = Query, Penalties = penalties };
            if (context.Settings.Backend == ExecutionBackend.JobsBurst) job.Schedule(agents.Count, 32).Complete();
            else for (int i = 0; i < agents.Count; i++) job.Execute(i);
            PendingCount = ArrivedCount = FailedCount = 0;
            for (int i = 0; i < agents.Count; i++)
            {
                var path = paths[i]; int advance = advances[i];
                if (advance == -2) path.Status = VolumePathStatus.Arrived;
                else if (advance == -1 && context.SimulationTime >= retryAt[i]) { path.RequestId = 0; retryAt[i] = context.SimulationTime + 0.5; }
                else if (advance >= 0) path.Cursor += advance;
                paths[i] = path;
                if (path.Status == VolumePathStatus.Arrived) ArrivedCount++;
                else if (path.Status == VolumePathStatus.Pending) PendingCount++;
                else if (path.Status != VolumePathStatus.Ready) FailedCount++;
            }
            if (context.Settings.Avoidance == AvoidanceAlgorithm.ORCA)
            {
                Traffic.Apply(context, agents, preferred);
                int budget = 4;
                for (int scanned = 0; scanned < agents.Count && budget > 0; scanned++)
                {
                    int i = recoveryCursor; recoveryCursor = (recoveryCursor + 1) % agents.Count;
                    if (paths[i].Status != VolumePathStatus.Ready || !Traffic.ShouldReplan(i, context.SimulationTime)) continue;
                    penaltyCenters[i] = agents.Positions[i] + math.normalizesafe(preferred[i]) * agents.Parameters[i].Radius * 4;
                    penaltyUntil[i] = context.SimulationTime + 8; Traffic.Replanned(i, context.SimulationTime);
                    Request(i, context, agents); RecoveryReplans++; budget--;
                }
            }
            return default;
        }
        [BurstCompile]
        private struct FollowJob : IJobParallelFor
        {
            public StepContext Context;
            [ReadOnly] public AgentReadView Agents;
            [ReadOnly] public NativeArray<float3> Points;
            [ReadOnly] public NativeArray<float4> Penalties;
            [ReadOnly] public NativeArray<int> Counts;
            [ReadOnly] public VolumeQuery Query;
            public NativeArray<int> Advances;
            public NativeArray<float3> Preferred;
            public void Execute(int i)
            {
                Preferred[i] = float3.zero; Advances[i] = 0; float3 p = Agents.Positions[i]; var parameters = Agents.Parameters[i];
                if (math.distance(p, Agents.Goals[i]) <= parameters.ArrivalDistance) { Advances[i] = -2; return; }
                if (Counts[i] == 0) return;
                int at = i * 5, cursor = 0;
                if (!Query.SegmentClear(p, Points[at])) { Advances[i] = -1; return; }
                while (cursor < Counts[i] - 1 && math.distance(p, Points[at + cursor]) < parameters.Radius * 0.25f &&
                    Query.SegmentClear(p, Points[at + cursor + 1])) cursor++;
                for (int j = Counts[i] - 1; j > cursor; j--)
                    if (!Crosses(p, Points[at + j], Penalties[i]) && Query.SegmentClear(p, Points[at + j])) { cursor = j; break; }
                float3 delta = Points[at + cursor] - p; float length = math.length(delta);
                float3 direction = math.normalizesafe(delta);
                float bias = Context.Settings.PreferredSideBias * math.min(1, length / math.max(parameters.Radius * 4, 0.001f));
                float3 velocity = math.normalizesafe(direction + bias * OrcaGeometry3D.Side(direction)) * math.min(parameters.MaxSpeed, length / Context.DeltaTime);
                if (!Query.SegmentClear(p, p + velocity * math.min(0.5f, length / parameters.MaxSpeed)))
                    velocity = direction * math.min(parameters.MaxSpeed, length / Context.DeltaTime);
                Preferred[i] = velocity; Advances[i] = cursor;
            }
            private static bool Crosses(float3 a, float3 b, float4 region)
            {
                if (region.w <= 0) return false;
                float3 d = b - a; float t = math.saturate(math.dot(region.xyz - a, d) / math.max(1e-10f, math.lengthsq(d)));
                return math.distancesq(a + t * d, region.xyz) < region.w * region.w;
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            for (int i = 0; i < searches.Length; i++) searches[i]?.Dispose();
            if (coarseLandmarks.IsCreated) coarseLandmarks.Dispose();
            if (coarseQuery.Nodes.IsCreated) coarseQuery.Nodes.Dispose(); coarseQuery = default;
            var query = Query; if (query.Nodes.IsCreated) query.Nodes.Dispose(); Query = default;
            if (points.IsCreated) points.Dispose(); if (counts.IsCreated) counts.Dispose();
            if (advances.IsCreated) advances.Dispose(); if (penalties.IsCreated) penalties.Dispose(); Traffic?.Dispose();
            for (int i = 0; i < waypoints.Length; i++) if (waypoints[i] != null) { ArrayPool<float3>.Shared.Return(waypoints[i]); waypoints[i] = null; }
        }
    }
}
