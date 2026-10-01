using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>Low-frequency local yield leases; no teleporting and no permanent edits to the navigation graph.</summary>
    public sealed class VolumeTrafficRecovery : IDisposable
    {
        private readonly NavigationVolume map;
        private readonly float3[] last, targets;
        private readonly double[] leaseUntil, retryAt;
        private NativeArray<float> priorities;
        private readonly float[] waiting;
        private int cursor;
        public int YieldingCount { get; private set; }
        public float LongestWait { get; private set; }
        public NativeArray<float>.ReadOnly Priorities => priorities.AsReadOnly();
        public bool IsYielding(int i, double time) => leaseUntil[i] > time;
        public float3 Target(int i) => targets[i];
        public float Wait(int i) => waiting[i];
        public void Reset(int i) { waiting[i] = 0; leaseUntil[i] = retryAt[i] = 0; priorities[i] = 1; }
        public bool ShouldReplan(int i, double time) => waiting[i] > 3 && time >= retryAt[i] && time >= leaseUntil[i];
        public void Replanned(int i, double time) { retryAt[i] = time + 4; }
        public VolumeTrafficRecovery(NavigationVolume map, int count)
        {
            this.map = map; last = new float3[count]; targets = new float3[count]; leaseUntil = new double[count];
            retryAt = new double[count]; waiting = new float[count]; priorities = new NativeArray<float>(count, Allocator.Persistent);
            for (int i = 0; i < count; i++) priorities[i] = 1;
        }
        public void Apply(in StepContext context, in AgentReadView agents, NativeArray<float3> preferred)
        {
            for (int i = 0; i < agents.Count; i++)
            {
                bool wantsMotion = math.lengthsq(preferred[i]) > 1e-8f && math.distance(agents.Positions[i], agents.Goals[i]) > agents.Parameters[i].ArrivalDistance;
                bool stalled = context.Tick > 0 && math.distance(last[i], agents.Positions[i]) < agents.Parameters[i].MaxSpeed * context.DeltaTime * 0.1f;
                waiting[i] = wantsMotion && stalled ? waiting[i] + context.DeltaTime : 0;
                LongestWait = math.max(LongestWait, waiting[i]); last[i] = agents.Positions[i];
                // Stable for a half-second coordination epoch; both agents see the same priority snapshot.
                if (context.Tick % 15 == 0) priorities[i] = 1 + math.min(60, waiting[i]);
            }
            if (context.Tick % 15 == 0)
            {
                int budget = math.min(8, agents.Count);
                for (int scan = 0; scan < agents.Count && budget > 0; scan++)
                {
                    int i = cursor; cursor = (cursor + 1) % agents.Count;
                    if (context.SimulationTime < leaseUntil[i] || context.SimulationTime < retryAt[i]) continue;
                    int winner = -1; float range = agents.Parameters[i].Radius * 8;
                    for (int j = 0; j < agents.Count; j++)
                    {
                        if (j == i || waiting[j] < 1.25f || math.distancesq(agents.Positions[i], agents.Positions[j]) > range * range) continue;
                        bool ahead = priorities[j] > priorities[i] || (priorities[j] == priorities[i] && agents.Ids[j] < agents.Ids[i]);
                        if (ahead && (winner < 0 || priorities[j] > priorities[winner] ||
                            (priorities[j] == priorities[winner] && agents.Ids[j] < agents.Ids[winner]))) winner = j;
                    }
                    if (winner < 0) continue; budget--; retryAt[i] = context.SimulationTime + 2;
                    float3 direction = math.normalizesafe(agents.Goals[winner] - agents.Positions[winner], new float3(1, 0, 0));
                    float3 side = OrcaGeometry3D.Side(direction), up = math.cross(direction, side);
                    for (int option = 0; option < 6; option++)
                    {
                        float3 axis = option < 2 ? side : option < 4 ? up : direction;
                        if ((option & 1) != 0) axis = -axis;
                        float3 target = agents.Positions[i] + axis * agents.Parameters[i].Radius * 5;
                        if (!map.SegmentClear(agents.Positions[i], target)) continue;
                        bool clear = true;
                        for (int j = 0; j < agents.Count && clear; j++) if (j != i)
                        {
                            float spacing = agents.Parameters[i].Radius + agents.Parameters[j].Radius + 0.2f;
                            clear = math.distancesq(target, agents.Positions[j]) > spacing * spacing &&
                                (leaseUntil[j] <= context.SimulationTime || math.distancesq(target, targets[j]) > spacing * spacing);
                        }
                        if (!clear) continue;
                        targets[i] = target; leaseUntil[i] = context.SimulationTime + 2; break;
                    }
                }
            }
            YieldingCount = 0;
            for (int i = 0; i < agents.Count; i++) if (leaseUntil[i] > context.SimulationTime)
            {
                float3 delta = targets[i] - agents.Positions[i];
                if (!map.SegmentClear(agents.Positions[i], targets[i])) { leaseUntil[i] = 0; continue; }
                preferred[i] = math.normalizesafe(delta) * math.min(agents.Parameters[i].MaxSpeed, math.length(delta) / context.DeltaTime);
                YieldingCount++;
            }
        }
        public void Dispose() { if (priorities.IsCreated) priorities.Dispose(); priorities = default; }
    }
}
