using Unity.Collections;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>Phase 1 uses discs in XZ; Phase 3 uses spheres. No acceleration limit in Phase 1.</summary>
    public struct AgentParameters
    {
        public float Radius;
        public float MaxSpeed;
        public float ArrivalDistance;
    }

    /// <summary>Read-only current-tick snapshot. Valid only until commit/disposal.</summary>
    public readonly struct AgentReadView
    {
        public readonly NativeArray<int>.ReadOnly Ids;
        public readonly NativeArray<float3>.ReadOnly Positions;
        public readonly NativeArray<float3>.ReadOnly Velocities;
        public readonly NativeArray<float3>.ReadOnly Goals;
        public readonly NativeArray<AgentParameters>.ReadOnly Parameters;
        public int Count => Positions.Length;

        public AgentReadView(NativeArray<int> ids, NativeArray<float3> positions,
            NativeArray<float3> velocities, NativeArray<float3> goals,
            NativeArray<AgentParameters> parameters)
        {
            Ids = ids.AsReadOnly();
            Positions = positions.AsReadOnly();
            Velocities = velocities.AsReadOnly();
            Goals = goals.AsReadOnly();
            Parameters = parameters.AsReadOnly();
        }
    }

    /// <summary>Borrowed initialization buffers. Fill every entry; never dispose or retain these arrays.</summary>
    public struct AgentInitializationView
    {
        public NativeArray<int> Ids;
        public NativeArray<float3> Positions;
        public NativeArray<float3> Velocities;
        public NativeArray<float3> Goals;
        public NativeArray<AgentParameters> Parameters;
    }

    /// <summary>Borrowed output arrays. Every active index must be written on every tick.</summary>
    public struct MotionOutput
    {
        public NativeArray<float3> Velocities;
        public NativeArray<SolveStatus> Status;
    }
}
