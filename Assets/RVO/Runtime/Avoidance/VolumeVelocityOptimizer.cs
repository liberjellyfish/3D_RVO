using Unity.Collections;
using Unity.Mathematics;

namespace Rvo
{
    public static class VolumeVelocityOptimizer
    {
        public static SolveStatus Solve(NativeSlice<VelocityPlane3D> planes, int hardCount, float3 preferred,
            float speed, float tolerance, out float3 velocity, out float residual, out int failedPlane)
        {
            velocity = float3.zero; residual = 0; failedPlane = -1;
            if (!math.all(math.isfinite(preferred)) || !math.isfinite(speed) || speed < 0) return SolveStatus.InvalidInput;
            if (TrySolve(planes, planes.Length, hardCount, 0, preferred, speed, tolerance, out velocity, out failedPlane))
            { residual = Residual(planes, velocity); return SolveStatus.Success; }
            int initialFailure = failedPlane;
            if (!TrySolve(planes, hardCount, hardCount, 0, preferred, speed, tolerance, out velocity, out failedPlane)) return SolveStatus.Infeasible;
            // Only dynamic planes are relaxed. Bound the search using an already feasible static solution.
            float low = 0, high = tolerance;
            for (int i = hardCount; i < planes.Length; i++) high = math.max(high, planes[i].Violation(velocity) + tolerance);
            float3 best = velocity;
            for (int iteration = 0; iteration < 16; iteration++)
            {
                float mid = (low + high) * 0.5f;
                if (TrySolve(planes, planes.Length, hardCount, mid, preferred, speed, tolerance, out float3 candidate, out _))
                { high = mid; best = candidate; } else low = mid;
            }
            velocity = best; residual = Residual(planes, velocity); failedPlane = initialFailure; return SolveStatus.Fallback;
        }
        private static float Bound(VelocityPlane3D plane, int index, int hardCount, float slack) => plane.Offset - (index < hardCount ? 0 : slack);
        private static float Residual(NativeSlice<VelocityPlane3D> planes, float3 v)
        { float r = 0; for (int i = 0; i < planes.Length; i++) r = math.max(r, planes[i].Violation(v)); return r; }
        private static bool TrySolve(NativeSlice<VelocityPlane3D> planes, int count, int hardCount, float slack,
            float3 preferred, float speed, float tolerance, out float3 result, out int failure)
        {
            result = math.normalizesafe(preferred) * math.min(speed, math.length(preferred)); failure = -1;
            for (int i = 0; i < count; i++)
            {
                var plane = planes[i]; float b = Bound(plane, i, hardCount, slack);
                if (math.dot(plane.Normal, result) >= b - tolerance) continue;
                if (!OnPlane(planes, i, hardCount, slack, preferred, speed, tolerance, out result)) { failure = i; return false; }
            }
            for (int i = 0; i < count; i++) if (Bound(planes[i], i, hardCount, slack) - math.dot(planes[i].Normal, result) > tolerance * 4)
            { failure = i; return false; }
            return math.all(math.isfinite(result)) && math.lengthsq(result) <= speed * speed + tolerance * math.max(1, speed) * 4;
        }
        private static bool OnPlane(NativeSlice<VelocityPlane3D> planes, int index, int hardCount, float slack,
            float3 preferred, float speed, float tolerance, out float3 result)
        {
            float3 normal = planes[index].Normal; float b = Bound(planes[index], index, hardCount, slack);
            result = float3.zero;
            if (math.abs(b) > speed) return false;
            float3 center = normal * b, tangent = preferred - math.dot(preferred, normal) * normal;
            result = center + math.normalizesafe(tangent) * math.min(math.length(tangent), math.sqrt(math.max(0, speed * speed - b * b)));
            for (int j = 0; j < index; j++)
            {
                float3 n = planes[j].Normal; float bound = Bound(planes[j], j, hardCount, slack);
                if (math.dot(n, result) >= bound - tolerance) continue;
                float3 cross = math.cross(normal, n); float length2 = math.lengthsq(cross);
                if (length2 < 1e-12f) return false;
                float3 direction = cross / math.sqrt(length2);
                float3 inPlane = n - math.dot(n, normal) * normal;
                float3 point = center + inPlane * ((bound - math.dot(n, center)) / length2);
                float projection = math.dot(point, direction);
                float discriminant = projection * projection + speed * speed - math.lengthsq(point);
                if (discriminant < -tolerance) return false;
                float root = math.sqrt(math.max(0, discriminant)), lo = -projection - root, hi = -projection + root;
                for (int k = 0; k < j; k++)
                {
                    float numerator = Bound(planes[k], k, hardCount, slack) - math.dot(planes[k].Normal, point);
                    float denominator = math.dot(planes[k].Normal, direction);
                    if (math.abs(denominator) < 1e-7f) { if (numerator > tolerance) return false; continue; }
                    float t = numerator / denominator; if (denominator > 0) lo = math.max(lo, t); else hi = math.min(hi, t);
                    if (lo > hi + tolerance) return false;
                }
                result = point + math.clamp(math.dot(preferred - point, direction), lo, math.max(lo, hi)) * direction;
            }
            return true;
        }
    }
}
