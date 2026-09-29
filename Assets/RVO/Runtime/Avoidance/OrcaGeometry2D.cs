using Unity.Mathematics;

namespace Rvo
{
    /// <summary>动态圆盘 ORCA：可行侧 dot(n, v) >= offset。双方各承担一半责任。</summary>
    public static class OrcaGeometry2D
    {
        public static VelocityHalfPlane2D Build(float2 p, float2 selfVelocity, float2 otherVelocity,
            float radius, float horizon, float dt, int selfId, int otherId, float epsilon, float responsibility = 0.5f)
        {
            float2 relative = selfVelocity - otherVelocity;
            float distanceSquared = math.lengthsq(p);
            float2 normal, correction;
            if (distanceSquared > radius * radius)
            {
                float2 w = relative - p / horizon;
                float projection = math.dot(w, p);
                if (projection < 0 && projection * projection > radius * radius * math.lengthsq(w))
                {
                    normal = math.normalizesafe(w);
                    correction = (radius / horizon - math.length(w)) * normal;
                }
                else
                {
                    float leg = math.sqrt(math.max(0, distanceSquared - radius * radius));
                    float2 tangent = Det(p, w) > 0
                        ? new float2(p.x * leg - p.y * radius, p.x * radius + p.y * leg) / distanceSquared
                        : -new float2(p.x * leg + p.y * radius, -p.x * radius + p.y * leg) / distanceSquared;
                    normal = new float2(-tangent.y, tangent.x);
                    correction = math.dot(relative, tangent) * tangent - relative;
                }
            }
            else
            {
                // 已接触/重叠使用真实 dt；不能用较长 horizon 延迟分离。
                float2 w = relative - p / dt;
                float length = math.length(w);
                float2 fallback = distanceSquared > epsilon * epsilon ? -math.normalize(p)
                    : new float2(selfId < otherId ? -1 : 1, 0);
                normal = length > epsilon ? w / length : fallback;
                correction = (radius / dt - length) * normal;
            }
            return new VelocityHalfPlane2D { Normal = normal,
                Offset = math.dot(normal, selfVelocity + responsibility * correction), SourceId = otherId };
        }

        public static float Det(float2 a, float2 b) => a.x * b.y - a.y * b.x;
    }
}
