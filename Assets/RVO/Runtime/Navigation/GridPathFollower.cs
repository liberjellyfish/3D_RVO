using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>小窗口路径跟随。托管路径只复制至多五个拐点，几何检查按 agent 并行。</summary>
    internal sealed class GridPathFollower : IDisposable
    {
        private NativeArray<float2> points;
        private NativeArray<int> counts, advances;
        private NativeArray<ObstacleNode> nodes;
        public int Advance(int agent) => advances[agent]; // -1 失效，-2 到达，其余为游标增量。
        public GridPathFollower(NavigationGrid map, int count)
        {
            try
            {
                points = new NativeArray<float2>(checked(count*5),Allocator.Persistent);
                counts = new NativeArray<int>(count,Allocator.Persistent);
                advances = new NativeArray<int>(count,Allocator.Persistent);
                nodes = new NativeArray<ObstacleNode>(map.NodeCount,Allocator.Persistent);
                for (int i = 0; i < nodes.Length; i++) nodes[i] = map.Node(i);
            }
            catch { Dispose(); throw; }
        }
        public void Execute(in StepContext context, in AgentReadView agents, NativeArray<float3> preferred,
            NavigationGrid map, in NavigationSettings settings, GridPathInfo[] paths, float2[][] waypoints)
        {
            for (int i = 0; i < paths.Length; i++)
            {
                var path = paths[i]; int count = path.Status == GridPathStatus.Ready ? math.min(5,path.Count-path.Cursor) : 0;
                counts[i] = count;
                for (int point = 0; point < count; point++) points[i*5+point] = waypoints[i][path.Cursor+point];
            }
            var job = new FollowJob { Context = context, Agents = agents, Preferred = preferred, Points = points,
                Counts = counts, Advances = advances, Nodes = nodes, MapMin = map.Min, MapMax = map.Max,
                Radius = map.ClearanceRadius, CellSize = map.CellSize, Horizon = settings.StaticTimeHorizon };
            if (context.Settings.Backend == ExecutionBackend.JobsBurst) job.Schedule(agents.Count,32).Complete();
            else for (int i = 0; i < agents.Count; i++) job.Execute(i);
        }
        [BurstCompile]
        private struct FollowJob : IJobParallelFor
        {
            public StepContext Context;
            [ReadOnly] public AgentReadView Agents;
            [ReadOnly] public NativeArray<float2> Points;
            [ReadOnly] public NativeArray<int> Counts;
            [ReadOnly] public NativeArray<ObstacleNode> Nodes;
            public NativeArray<int> Advances;
            public NativeArray<float3> Preferred;
            public float2 MapMin, MapMax;
            public float Radius, CellSize, Horizon;
            public void Execute(int i)
            {
                Preferred[i] = float3.zero; Advances[i] = 0;
                float2 position = Agents.Positions[i].xz, goal = Agents.Goals[i].xz;
                var parameters = Agents.Parameters[i]; float goalDistance = math.distance(position,goal);
                if (goalDistance <= parameters.ArrivalDistance) { Advances[i] = -2; return; }
                int count = Counts[i]; if (count == 0) return;
                int start = i*5, cursor = 0;
                if (!Clear(position,Points[start])) { Advances[i] = -1; return; }
                while (cursor < count-1 && math.distance(position,Points[start+cursor]) < math.max(CellSize*0.15f,parameters.Radius*0.25f)
                    && Clear(position,Points[start+cursor+1])) cursor++;
                if ((Context.Tick+i)%4 == 0)
                    for (int point = count-1; point > cursor; point--)
                        if (Clear(position,Points[start+point])) { cursor = point; break; }
                float2 delta = Points[start+cursor]-position;
                float length = math.length(delta), speed = math.min(parameters.MaxSpeed,length/Context.DeltaTime);
                float2 direction = math.normalizesafe(delta);
                float bias = Context.Settings.PreferredSideBias*math.min(1,goalDistance/(parameters.Radius*4));
                float2 velocity = math.normalizesafe(direction+bias*new float2(-direction.y,direction.x))*speed;
                float lookTime = math.min(Horizon,length/math.max(speed,1e-5f));
                if (bias != 0 && !Clear(position,position+velocity*lookTime)) velocity = direction*speed;
                Preferred[i] = new float3(velocity.x,0,velocity.y); Advances[i] = cursor;
            }
            private bool Clear(float2 a, float2 b)
            {
                if (math.any(a <= MapMin+Radius) || math.any(a >= MapMax-Radius) ||
                    math.any(b <= MapMin+Radius) || math.any(b >= MapMax-Radius)) return false;
                int index = 0;
                while (index < Nodes.Length)
                {
                    var node = Nodes[index];
                    if (!ObstacleBvh.SegmentBox(a,b,node.Min-Radius,node.Max+Radius,out _)) index = node.Escape;
                    else if (node.Leaf != 0) return false;
                    else index++;
                }
                return true;
            }
        }
        public void Dispose()
        {
            if (points.IsCreated) points.Dispose(); points = default;
            if (counts.IsCreated) counts.Dispose(); counts = default;
            if (advances.IsCreated) advances.Dispose(); advances = default;
            if (nodes.IsCreated) nodes.Dispose(); nodes = default;
        }
    }
}
