using Unity.Mathematics;

namespace Rvo
{
    public struct QualityMetrics
    {
        public int Arrived, OverlappingPairs, SweptCollisionPairs, FallbackAgents, TruncatedAgents, InfeasibleAgents, InvalidAgents, SlowUnarrived;
        public float MeanSpeedChange;
        public float MinimumSeparation;
    }

    public static class QualityEvaluator
    {
        /// <summary>调试用 O(N²) 独立检测；不复用可能截断的避障邻居，不计入算法计时。</summary>
        public static QualityMetrics Evaluate(in AgentReadView current, in StepDebugView step, float tolerance,
            SimulationDimension dimension = SimulationDimension.PlanarXZ)
        {
            var result = new QualityMetrics { MinimumSeparation = float.PositiveInfinity };
            for (int i = 0; i < current.Count; i++)
            {
                if (math.distance(current.Positions[i], current.Goals[i]) <= current.Parameters[i].ArrivalDistance + tolerance)
                    result.Arrived++;
                else if (math.length(current.Velocities[i]) < 0.05f) result.SlowUnarrived++;
                result.MeanSpeedChange += math.distance(current.Velocities[i], step.Inputs.Velocities[i]) / current.Count;
                if (step.Status[i] == SolveStatus.Infeasible) result.InfeasibleAgents++;
                if (step.Status[i] == SolveStatus.InvalidInput) result.InvalidAgents++;
                if (step.Status[i] == SolveStatus.Fallback) result.FallbackAgents++;
                if (step.Neighbors.DroppedCounts[i] > 0) result.TruncatedAgents++;
                for (int j = 0; j < i; j++)
                {
                    float radius = current.Parameters[i].Radius + current.Parameters[j].Radius;
                    float3 end = current.Positions[i] - current.Positions[j];
                    if (dimension == SimulationDimension.PlanarXZ) end.y = 0;
                    float separation = math.length(end) - radius;
                    result.MinimumSeparation = math.min(result.MinimumSeparation, separation);
                    if (separation < -tolerance) result.OverlappingPairs++;
                    float3 start = step.Inputs.Positions[i] - step.Inputs.Positions[j];
                    if (dimension == SimulationDimension.PlanarXZ) start.y = 0;
                    float3 delta = end - start;
                    float lengthSquared = math.lengthsq(delta);
                    // 相对运动线段的最近点可发现一步内穿越后又分开的碰撞。
                    float t = lengthSquared == 0 ? 0 : math.saturate(-math.dot(start, delta) / lengthSquared);
                    if (math.length(start + t * delta) < radius - tolerance) result.SweptCollisionPairs++;
                }
            }
            return result;
        }
    }
}
