using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>连续运动的局部通行协调：等待老化、稳定通行租约、退让点及限频拥堵重规划。
    /// 不是离散 MAPF/PIBT，不替代 ORCA 或整步安全证书。</summary>
    internal sealed class TrafficRecovery : IDisposable
    {
        private readonly NavigationGrid map;
        private readonly NavigationSettings settings;
        private readonly float[] blockedFor;
        private readonly double[] nextReplan, leaseEnd, nextYield;
        private readonly int[] yieldTo, next;
        private readonly float2[] retreat, passageDirection;
        private readonly bool[] hasRetreat;
        private readonly Dictionary<int2,int> buckets;
        private NativeArray<float> priorities;
        private float bucketSize, maxRadius;
        public NativeArray<float>.ReadOnly Priorities => priorities.AsReadOnly();
        public int StalledCount { get; private set; }
        public int YieldingCount { get; private set; }
        public int YieldEvents { get; private set; }
        public bool IsYielding(int i) => yieldTo[i] >= 0;
        public TrafficRecovery(NavigationGrid map, in NavigationSettings settings, int count)
        {
            this.map = map; this.settings = settings;
            blockedFor = new float[count]; nextReplan = new double[count]; leaseEnd = new double[count]; nextYield = new double[count];
            yieldTo = new int[count]; next = new int[count]; retreat = new float2[count]; passageDirection = new float2[count];
            hasRetreat = new bool[count]; buckets = new Dictionary<int2,int>(count);
            for (int i = 0; i < count; i++) yieldTo[i] = -1;
            priorities = new NativeArray<float>(count,Allocator.Persistent);
        }
        public void ResetAgent(int i) { yieldTo[i] = -1; hasRetreat[i] = false; blockedFor[i] = 0; nextReplan[i] = nextYield[i] = 0; }
        public bool ShouldReplan(int i, double time) => !settings.DisableTrafficRecovery && yieldTo[i] < 0 &&
            blockedFor[i] >= settings.EffectiveStallSeconds * 2 && time >= nextReplan[i];
        public void Replanned(int i, double time) => nextReplan[i] = time + settings.EffectiveRecoveryCooldown;
        public void Apply(in StepContext context, in AgentReadView agents, NativeArray<float3> preferred)
        {
            StalledCount = YieldingCount = 0; double time = context.SimulationTime;
            for (int i = 0; i < agents.Count; i++)
            {
                var parameter = agents.Parameters[i]; maxRadius = math.max(maxRadius,parameter.Radius);
                bool arrived = math.distance(agents.Positions[i].xz,agents.Goals[i].xz) <= parameter.ArrivalDistance;
                float desiredSpeed = math.length(preferred[i]);
                float progress = math.dot(agents.Velocities[i].xz,math.normalizesafe(preferred[i].xz));
                if (desiredSpeed > parameter.MaxSpeed*0.1f && progress < parameter.MaxSpeed*0.15f)
                    blockedFor[i] += context.DeltaTime;
                else blockedFor[i] = math.max(0,blockedFor[i]-context.DeltaTime*2);
                if (arrived) blockedFor[i] = 0;
                if (blockedFor[i] >= settings.EffectiveStallSeconds) StalledCount++;
                priorities[i] = settings.DisableTrafficRecovery ? 1 : arrived ? 0.2f : desiredSpeed < 0.01f ? 0.5f : 1+math.min(2,blockedFor[i]/10);
                int winner = yieldTo[i];
                if (winner < 0) continue;
                float2 relative = agents.Positions[i].xz-agents.Positions[winner].xz;
                float clearance = parameter.Radius+agents.Parameters[winner].Radius;
                bool passed = math.dot(relative,passageDirection[i]) < -clearance*1.5f;
                if (settings.DisableTrafficRecovery || time >= leaseEnd[i] || passed || math.length(relative) > clearance*8)
                { yieldTo[i] = -1; hasRetreat[i] = false; nextYield[i] = time+1; blockedFor[i] = 0; }
            }
            if (settings.DisableTrafficRecovery) return;
            for (int i = 0; i < agents.Count; i++) if (yieldTo[i] >= 0)
            { priorities[i] = 0.1f; priorities[yieldTo[i]] = math.max(8,priorities[yieldTo[i]]); }

            // 每六 Tick 协调（默认 dt 下为 5 Hz），物理避障和安全证书仍每 Tick 执行。
            if (context.Tick % 6 == 0)
            {
                bucketSize = math.max(map.CellSize, maxRadius*6); buckets.Clear();
                for (int i = 0; i < agents.Count; i++)
                { int2 key = (int2)math.floor(agents.Positions[i].xz/bucketSize); next[i] = buckets.TryGetValue(key,out int head) ? head : -1; buckets[key] = i; }
                for (int i = 0; i < agents.Count; i++)
                {
                    if (yieldTo[i] >= 0 || time < nextYield[i]) continue;
                    float2 direction = math.normalizesafe(preferred[i].xz);
                    int blocker = FindPriorityBlocker(i,agents,preferred,direction);
                    if (blocker < 0) continue;
                    float2 winnerDirection = math.normalizesafe(preferred[blocker].xz);
                    if (math.lengthsq(winnerDirection) < 0.1f) continue;
                    yieldTo[i] = blocker; passageDirection[i] = winnerDirection;
                    leaseEnd[i] = time + 8; priorities[i] = 0.1f; priorities[blocker] = math.max(8,priorities[blocker]); YieldEvents++;
                }
                for (int i = 0; i < agents.Count; i++) if (yieldTo[i] >= 0)
                {
                    float threshold = math.max(0.1f,agents.Parameters[i].Radius*0.4f);
                    float laneGap = math.abs(OrcaGeometry2D.Det(agents.Positions[i].xz-agents.Positions[yieldTo[i]].xz,passageDirection[i]));
                    float passWidth = agents.Parameters[i].Radius+agents.Parameters[yieldTo[i]].Radius;
                    if (hasRetreat[i] && laneGap > passWidth*1.2f && math.distance(agents.Positions[i].xz,retreat[i]) < threshold) continue;
                    if (!hasRetreat[i] || math.distance(agents.Positions[i].xz,retreat[i]) < threshold ||
                        !map.SegmentClear(agents.Positions[i].xz,retreat[i],map.ClearanceRadius))
                        hasRetreat[i] = TryRetreat(i,yieldTo[i],agents,out retreat[i]);
                }
            }
            for (int i = 0; i < agents.Count; i++) if (yieldTo[i] >= 0)
            {
                YieldingCount++;
                // 只有经过静态验证的退让点才产生 preferred；所有速度继续经过 ORCA。
                float2 delta = hasRetreat[i] ? retreat[i]-agents.Positions[i].xz : float2.zero;
                float2 velocity = math.normalizesafe(delta)*math.min(agents.Parameters[i].MaxSpeed,math.length(delta)/context.DeltaTime);
                preferred[i] = new float3(velocity.x,0,velocity.y);
            }
        }
        private int FindPriorityBlocker(int self, in AgentReadView agents, NativeArray<float3> preferred, float2 direction)
        {
            float2 p = agents.Positions[self].xz; int2 cell = (int2)math.floor(p/bucketSize);
            int best = -1; float bestDistance = float.PositiveInfinity;
            for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
            {
                if (!buckets.TryGetValue(cell+new int2(x,z),out int head)) continue;
                for (int j = head; j >= 0; j = next[j])
                {
                    if (j == self || yieldTo[j] >= 0) continue;
                    if (priorities[j] < priorities[self] || (priorities[j] == priorities[self] && agents.Ids[j] > agents.Ids[self])) continue;
                    float2 delta = agents.Positions[j].xz-p;
                    float r = agents.Parameters[self].Radius+agents.Parameters[j].Radius, distance = math.lengthsq(delta);
                    if (distance > r*r*9 || distance >= bestDistance) continue;
                    float2 otherDirection = math.normalizesafe(preferred[j].xz);
                    if (math.dot(otherDirection,-delta) <= r*0.2f) continue; // 对方没有向自己行进。
                    // 对方会在到达自己之前停在独立终点时，不要反复把已到达者赶离终点。
                    float travel = math.min(math.distance(agents.Positions[j].xz,agents.Goals[j].xz),agents.Parameters[j].MaxSpeed*1.5f);
                    float closest = math.clamp(math.dot(-delta,otherDirection),0,travel);
                    if (math.lengthsq(delta+otherDirection*closest) > r*r*1.1f) continue;
                    bool idle = math.lengthsq(direction) < 0.1f;
                    bool opposing = math.dot(direction,otherDirection) < -0.25f;
                    if (!idle && !opposing && blockedFor[self] < settings.EffectiveStallSeconds) continue;
                    // 持续受阻才介入；窄道对向可提前介入，防止两端同时深入。
                    float2 side = new float2(-otherDirection.y,otherDirection.x)*r;
                    bool narrow = !map.SegmentClear(p,p+side,map.ClearanceRadius) && !map.SegmentClear(p,p-side,map.ClearanceRadius);
                    if (!idle && !narrow && blockedFor[self] < settings.EffectiveStallSeconds) continue;
                    if (!map.SegmentClear(p,agents.Positions[j].xz,map.ClearanceRadius)) continue;
                    best = j; bestDistance = distance;
                }
            }
            return best;
        }
        private bool TryRetreat(int self, int winner, in AgentReadView agents, out float2 target)
        {
            float2 p = agents.Positions[self].xz, direction = passageDirection[self], side = new float2(-direction.y,direction.x);
            float r = agents.Parameters[self].Radius+agents.Parameters[winner].Radius;
            float best = float.NegativeInfinity; target = p;
            // 优先退出通行带；无侧向空间时沿通行方向后退，下一次更新继续寻找出口。
            for (int forward = 0; forward <= 3; forward++) for (int lateral = -2; lateral <= 2; lateral++)
            {
                if (forward == 0 && lateral == 0) continue;
                float2 candidate = p + direction*(forward*r) + side*(lateral*r*0.8f);
                int cell = map.Cell(candidate);
                if (!map.IsWalkable(cell) || !map.SegmentClear(p,candidate,map.ClearanceRadius)) continue;
                if (!Vacant(candidate,self,agents)) continue;
                float sideGap = math.abs(OrcaGeometry2D.Det(candidate-agents.Positions[winner].xz,direction));
                float score = math.min(sideGap/r,2)*5 + math.min(map.Clearance(cell)/r,2) - math.distance(p,candidate)/r;
                if (score > best) { best = score; target = candidate; }
            }
            return math.isfinite(best);
        }
        private bool Vacant(float2 p, int self, in AgentReadView agents)
        {
            int2 cell = (int2)math.floor(p/bucketSize);
            for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
            {
                if (!buckets.TryGetValue(cell+new int2(x,z),out int head)) continue;
                for (int j = head; j >= 0; j = next[j]) if (j != self)
                {
                    float r = agents.Parameters[self].Radius+agents.Parameters[j].Radius+0.1f;
                    if (math.distancesq(p,agents.Positions[j].xz) < r*r) return false;
                    if (hasRetreat[j] && yieldTo[j] >= 0 && math.distancesq(p,retreat[j]) < r*r) return false;
                }
            }
            return true;
        }
        public void Dispose() { if (priorities.IsCreated) priorities.Dispose(); priorities = default; }
    }
}
