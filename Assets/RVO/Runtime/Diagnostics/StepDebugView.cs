using Unity.Collections;
using Unity.Mathematics;

namespace Rvo
{
    public readonly struct StepDebugView
    {
        public readonly AgentReadView Inputs;
        public readonly NativeArray<VelocityHalfPlane2D>.ReadOnly OrcaConstraints;
        public readonly NeighborReadView Neighbors;
        public readonly NativeArray<float3>.ReadOnly Preferred;
        public readonly NativeArray<SolveStatus>.ReadOnly Status;
        public StepDebugView(in AgentReadView inputs, in NeighborReadView neighbors,
            NativeArray<float3>.ReadOnly preferred, NativeArray<SolveStatus>.ReadOnly status,
            NativeArray<VelocityHalfPlane2D>.ReadOnly orcaConstraints = default)
        {
            OrcaConstraints = orcaConstraints; Inputs = inputs; Neighbors = neighbors; Preferred = preferred; Status = status;
        }
    }
}
