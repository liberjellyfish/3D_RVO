using System;
using System.Diagnostics;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>O(N) run observations outside the core tick; no per-frame reporting allocations.</summary>
    public sealed class VolumeRunMetrics
    {
        private readonly long[] firstReady, firstArrival;
        private readonly float[] waiting;
        private readonly Stopwatch wall = Stopwatch.StartNew();
        public int FirstArrived { get; private set; }
        public int CurrentArrived { get; private set; }
        public int NeverReady { get; private set; }
        public float LongestWait { get; private set; }
        public double AllFirstArrivalSeconds { get; private set; } = double.NaN;
        public double AllCurrentArrivalSeconds { get; private set; } = double.NaN;
        public double WallSeconds => wall.Elapsed.TotalSeconds;
        public long ReadyTick(int i) => firstReady[i];
        public long ArrivalTick(int i) => firstArrival[i];
        public long LimitedAgentTicks { get; private set; }
        public long ManagedBytes { get; private set; }
        public long PortalCrossings { get; private set; }
        public VolumeRunMetrics(int count)
        {
            NeverReady = count;
            firstReady = new long[count]; firstArrival = new long[count]; waiting = new float[count];
            for (int i = 0; i < count; i++) firstReady[i] = firstArrival[i] = -1;
        }
        public void Observe(SimulationWorld world, VolumeNavigation navigation, VolumeAvoidanceSolver solver)
        {
            var agents = world.Snapshot; var previous = world.DebugSnapshot.Inputs;
            CurrentArrived = NeverReady = 0; float dt = world.Settings.FixedDeltaTime;
            for (int i = 0; i < agents.Count; i++)
            {
                var path = navigation.Path(i);
                if (firstReady[i] < 0 && navigation.FirstReadyTick(i) >= 0) firstReady[i] = navigation.FirstReadyTick(i);
                if (firstReady[i] < 0) NeverReady++;
                bool arrived = math.distance(agents.Positions[i], agents.Goals[i]) <= agents.Parameters[i].ArrivalDistance;
                if (arrived)
                {
                    CurrentArrived++;
                    if (firstArrival[i] < 0) { firstArrival[i] = world.Tick; FirstArrived++; }
                }
                waiting[i] = firstReady[i] >= 0 && !arrived && math.length(agents.Velocities[i]) < agents.Parameters[i].MaxSpeed * 0.1f ? waiting[i] + dt : 0;
                LongestWait = math.max(LongestWait, waiting[i]);
                // Count crossings of the two demo partition midplanes, including repeated passes.
                for (int wallIndex = 0; wallIndex < 2 &&
                    (navigation.Map.ObstacleCount == 8 || navigation.Map.ObstacleCount == 104); wallIndex++)
                {
                    var box = navigation.Map.Obstacle(wallIndex * 4); float x = (box.Min.x + box.Max.x) * 0.5f;
                    if ((previous.Positions[i].x < x) != (agents.Positions[i].x < x)) PortalCrossings++;
                }
            }
            if (FirstArrived == agents.Count && double.IsNaN(AllFirstArrivalSeconds)) AllFirstArrivalSeconds = world.Tick * (double)dt;
            if (CurrentArrived == agents.Count && double.IsNaN(AllCurrentArrivalSeconds)) AllCurrentArrivalSeconds = world.Tick * (double)dt;
            LimitedAgentTicks += solver.LastLimitedAgents; ManagedBytes += world.LastMetrics.ManagedAllocatedBytes;
        }
        public double ReadyPercentile(double percentile, float dt)
        {
            var copy = new long[firstReady.Length]; int count = 0;
            foreach (long tick in firstReady) if (tick >= 0) copy[count++] = tick;
            if (count == 0) return double.NaN; Array.Sort(copy, 0, count);
            return copy[(int)math.clamp(math.ceil(percentile * count) - 1, 0, count - 1)] * dt;
        }
    }
}
