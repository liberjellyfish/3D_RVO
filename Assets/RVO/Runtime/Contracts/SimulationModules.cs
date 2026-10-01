using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    // These are main-thread scheduling interfaces. Jobs contain concrete unmanaged structs, never interfaces.
    public interface ISimulationModule : IDisposable
    {
        string Name { get; }
        bool IsImplemented { get; }
    }

    public interface IPreferredVelocityProvider : ISimulationModule
    {
        JobHandle Schedule(in StepContext context, in AgentReadView agents,
            NativeArray<float3> preferred, JobHandle dependency);
    }

    public interface INeighborSearch : ISimulationModule
    {
        // Build spatial index, then query. Output sorted by (distanceSquared, stable ID), excludes self.
        JobHandle Schedule(in StepContext context, in AgentReadView agents,
            in NeighborWriteView neighbors, JobHandle dependency);
    }

    public interface IAvoidanceSolver : ISimulationModule
    {
        // Reads only tick t; writes all chosen velocities/statuses. No movement or rendering here.
        JobHandle Schedule(in StepContext context, in AgentReadView agents,
            NativeArray<float3>.ReadOnly preferred, in NeighborReadView neighbors,
            in MotionOutput output, JobHandle dependency);
    }

    public interface IMotionIntegrator : ISimulationModule
    {
        JobHandle Schedule(in StepContext context, in AgentReadView agents,
            NativeArray<float3>.ReadOnly chosenVelocities, NativeArray<float3> nextPositions,
            JobHandle dependency);
    }

    public interface IScenarioInitializer : ISimulationModule
    {
        // Synchronous startup only. Must initialize all entries with unique IDs and valid finite data.
        void Initialize(in SimulationSettings settings, in ScenarioSettings scenario,
            in AgentInitializationView agents);
    }

    public interface ISimulationPresenter
    {
        // Called after CompleteStep. Rendering must not change simulation state or retain views.
        void Present(in AgentReadView agents);
    }

    public interface IPlanarConstraintDebugSource
    {
        NativeArray<VelocityHalfPlane2D>.ReadOnly Constraints { get; }
    }
}
