using Unity.Mathematics;

namespace Rvo
{
    /// <summary>Planar feasible side: dot(Normal, velocityXZ) >= Offset; Normal must be unit length.</summary>
    public struct VelocityHalfPlane2D
    {
        public float2 Normal;
        public float Offset;
        public int SourceId;
    }

    /// <summary>Phase 3 contract only: dot(Normal, velocity) >= Offset.</summary>
    public struct VelocityPlane3D
    {
        public float3 Normal;
        public float Offset;
        public int SourceId;
    }

    // Geometric constraint building and numerical solving remain separate from scheduling/storage.
    public interface IPlanarVelocityOptimizer
    {
        SolveStatus Solve(Unity.Collections.NativeSlice<VelocityHalfPlane2D> constraints,
            float2 preferredVelocity, float maxSpeed, float epsilon, out float2 velocity);
    }

    public interface ISpatialVelocityOptimizer
    {
        SolveStatus Solve(Unity.Collections.NativeSlice<VelocityPlane3D> constraints,
            float3 preferredVelocity, float maxSpeed, float epsilon, out float3 velocity);
    }
}
