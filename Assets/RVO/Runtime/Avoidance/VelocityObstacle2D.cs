using Unity.Mathematics;

namespace Rvo
{
    /// <summary>纯几何：p = otherPosition - selfPosition，v = selfVelocity - otherVelocity。</summary>
    public static class VelocityObstacle2D
    {
        public static float TimeToCollision(float2 p, float2 v, float combinedRadius)
        {
            // 求 |p - v*t|² = r² 的最早非负根；使用 double 中间量降低擦边消减误差。
            double c = (double)p.x * p.x + (double)p.y * p.y - (double)combinedRadius * combinedRadius;
            double b = (double)p.x * v.x + (double)p.y * v.y;
            if (c < 0) return 0;
            if (b <= 0) return float.PositiveInfinity; // 分离或相对静止。
            double a = (double)v.x * v.x + (double)v.y * v.y;
            double discriminant = b * b - a * c;
            if (discriminant < 0) return float.PositiveInfinity;
            // 与 (b - sqrt(discriminant))/a 等价，近接触时更稳定。
            return (float)(c / (b + System.Math.Sqrt(discriminant)));
        }

        public static bool Contains(float2 p, float2 relativeVelocity, float radius, float horizon) =>
            TimeToCollision(p, relativeVelocity, radius) <= horizon;
    }
}
