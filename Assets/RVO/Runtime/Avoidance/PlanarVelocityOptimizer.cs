using Unity.Collections;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>增量二维凸优化。不可行时最小化最大半平面违反量，再接近期望速度。</summary>
    public static class PlanarVelocityOptimizer
    {
        public static SolveStatus Solve(NativeSlice<VelocityHalfPlane2D> planes, float2 preferred,
            float speed, float epsilon, out float2 result, int hardPlaneCount = 0)
        {
            result = float2.zero;
            if (!math.all(math.isfinite(preferred)) || !math.isfinite(speed) || speed <= 0 ||
                !math.isfinite(epsilon) || epsilon <= 0 || hardPlaneCount < 0 || hardPlaneCount > planes.Length) return SolveStatus.InvalidInput;
            for (int i = 0; i < planes.Length; i++)
                if (!math.all(math.isfinite(planes[i].Normal)) || !math.isfinite(planes[i].Offset) ||
                    math.abs(math.lengthsq(planes[i].Normal) - 1) > 1e-3f) return SolveStatus.InvalidInput;
            if (TrySolve(planes, preferred, speed, epsilon, 0, hardPlaneCount, out result)) return SolveStatus.Success;

            // 统一松弛量保证有界回退，不把停车或部分满足伪装成安全解。
            float low = 0, high = 0;
            for (int i = hardPlaneCount; i < planes.Length; i++) high = math.max(high, planes[i].Offset + (hardPlaneCount > 0 ? speed : 0));
            // 静态平面保留完整责任；拥挤时只松弛动态 ORCA，不允许回退把 agent 推进墙内。
            float2 best = float2.zero;
            if (hardPlaneCount > 0 && !TrySolve(planes, preferred, speed, epsilon, high, hardPlaneCount, out best))
            { result = float2.zero; return SolveStatus.Infeasible; }
            for (int iteration = 0; iteration < 24; iteration++)
            {
                float middle = (low + high) * 0.5f;
                if (TrySolve(planes, preferred, speed, epsilon, middle, hardPlaneCount, out float2 candidate))
                { high = middle; best = candidate; }
                else low = middle;
            }
            result = best;
            return SolveStatus.Infeasible;
        }

        private static bool TrySolve(NativeSlice<VelocityHalfPlane2D> planes, float2 preferred,
            float speed, float epsilon, float slack, int hardPlaneCount, out float2 result)
        {
            result = math.lengthsq(preferred) > speed * speed ? math.normalizesafe(preferred) * speed : preferred;
            for (int i = 0; i < planes.Length; i++)
            {
                var plane = planes[i]; float offset = plane.Offset - (i < hardPlaneCount ? 0 : slack);
                if (math.dot(plane.Normal, result) >= offset - epsilon) continue;
                if (offset > speed) return false;
                float2 center = plane.Normal * offset;
                float2 tangent = new float2(-plane.Normal.y, plane.Normal.x);
                float radiusSquared = speed * speed - offset * offset;
                if (radiusSquared < 0) return false;
                float bound = math.sqrt(radiusSquared), left = -bound, right = bound;
                for (int j = 0; j < i; j++)
                {
                    float denominator = math.dot(planes[j].Normal, tangent);
                    float numerator = planes[j].Offset - (j < hardPlaneCount ? 0 : slack) - math.dot(planes[j].Normal, center);
                    // 近乎平行只用机器量级判定，避免把可行但狭窄的夹角随意抹掉。
                    if (math.abs(denominator) < 1e-7f)
                    { if (numerator > epsilon) return false; }
                    else if (denominator > 0) left = math.max(left, numerator / denominator);
                    else right = math.min(right, numerator / denominator);
                    if (left > right) return false;
                }
                result = center + tangent * math.clamp(math.dot(tangent, preferred - center), left, right);
            }
            return true;
        }
    }
}
