using Unity.Mathematics;

namespace Rvo
{
    public static class OrcaGeometry3D
    {
        // Finite-horizon sphere/cone construction, following the RVO2-3D geometric formulation.
        // Reference: https://github.com/snape/RVO2-3D/blob/main/src/Agent.cc (Apache-2.0).
        public static VelocityPlane3D Build(float3 relativePosition, float3 ownVelocity, float3 otherVelocity,
            float radius, float horizon, float dt, int ownId, int otherId, float share = 0.5f)
        {
            float3 p = relativePosition, v = ownVelocity - otherVelocity;
            float d2 = math.lengthsq(p), r2 = radius * radius;
            float3 normal, correction;
            if (d2 > r2)
            {
                float3 w = v - p / horizon; float w2 = math.lengthsq(w), dot = math.dot(w, p);
                if (dot < 0 && dot * dot > r2 * w2)
                {
                    float length = math.sqrt(w2); normal = w / length;
                    correction = (radius / horizon - length) * normal;
                }
                else
                {
                    float b = math.dot(p, v), c = math.lengthsq(v) - math.lengthsq(math.cross(p, v)) / (d2 - r2);
                    float t = (b + math.sqrt(math.max(0, b * b - d2 * c))) / d2;
                    float3 projected = v - t * p; float length = math.length(projected);
                    float3 axis = p / math.sqrt(d2); float sine = radius / math.sqrt(d2);
                    // The cone axis has infinitely many nearest normals. Select a reciprocal stable one.
                    float3 side = Side(axis) * (ownId < otherId ? 1 : -1);
                    // Side(-axis) == Side(axis), so reversing pair order reverses the complete normal.
                    normal = length > 1e-6f ? projected / length : -sine * axis + math.sqrt(1 - sine * sine) * side;
                    correction = (radius * t - length) * normal;
                }
            }
            else
            {
                float3 w = v - p / dt; float length = math.length(w);
                normal = math.normalizesafe(w, new float3(ownId < otherId ? 1 : -1, 0, 0));
                correction = (radius / dt - length) * normal;
            }
            return new VelocityPlane3D { Normal = normal, Offset = math.dot(normal, ownVelocity + share * correction), SourceId = otherId };
        }
        public static float3 Side(float3 direction)
        {
            float3 axis = math.abs(direction.y) < 0.9f ? new float3(0, 1, 0) : new float3(1, 0, 0);
            // Projection of a reference axis is unchanged when direction is reversed.
            return math.normalizesafe(axis - math.dot(axis, direction) * direction, new float3(0, 0, 1));
        }
    }
}
