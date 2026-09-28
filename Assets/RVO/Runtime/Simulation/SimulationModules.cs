using System;
using System.Collections.Generic;

namespace Rvo
{
    /// <summary>Per-world modules; ownership transfers to SimulationWorld on construction, including failure.</summary>
    public sealed class SimulationModules : IDisposable
    {
        public IScenarioInitializer Scenario { get; }
        public IPreferredVelocityProvider PreferredVelocity { get; }
        public INeighborSearch Neighbors { get; }
        public IAvoidanceSolver Avoidance { get; }
        public IMotionIntegrator Integrator { get; }
        private bool disposed;

        public SimulationModules(IScenarioInitializer scenario, IPreferredVelocityProvider preferredVelocity,
            INeighborSearch neighbors, IAvoidanceSolver avoidance, IMotionIntegrator integrator)
        {
            Scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
            PreferredVelocity = preferredVelocity ?? throw new ArgumentNullException(nameof(preferredVelocity));
            Neighbors = neighbors ?? throw new ArgumentNullException(nameof(neighbors));
            Avoidance = avoidance ?? throw new ArgumentNullException(nameof(avoidance));
            Integrator = integrator ?? throw new ArgumentNullException(nameof(integrator));
        }

        public string GetReadinessIssue()
        {
            if (disposed) return "Module bundle has been disposed.";
            var missing = new List<string>();
            foreach (var module in Enumerate())
                if (!module.IsImplemented) missing.Add(module.Name);
            return missing.Count == 0 ? null : "Not implemented: " + string.Join(", ", missing);
        }

        private IEnumerable<ISimulationModule> Enumerate()
        {
            yield return Scenario;
            yield return PreferredVelocity;
            yield return Neighbors;
            yield return Avoidance;
            yield return Integrator;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Module Dispose implementations must be idempotent and must not throw.
            foreach (var module in Enumerate()) module.Dispose();
        }
    }

    public static class Phase1ModuleFactory
    {
        public static SimulationModules Create(in SimulationSettings settings)
        {
            settings.Validate();
            if (settings.Dimension != SimulationDimension.PlanarXZ)
                throw new NotSupportedException("Full3D is reserved for Phase 3; no planar fallback is allowed.");

            INeighborSearch neighbors = NeighborSearchFactory.Create(settings.NeighborSearch);
            IAvoidanceSolver solver;
            switch (settings.Avoidance)
            {
                case AvoidanceAlgorithm.None: solver = new PassThroughSolver(); break;
                case AvoidanceAlgorithm.VO: solver = new VoSolver2D(); break;
                case AvoidanceAlgorithm.RVO: solver = new RvoSolver2D(); break;
                case AvoidanceAlgorithm.ORCA: solver = new OrcaSolver2D(); break;
                default: throw new ArgumentOutOfRangeException(nameof(settings.Avoidance));
            }
            return new SimulationModules(new ScenarioInitializer(), new DirectGoalPreferredVelocity(),
                neighbors, solver, new PlanarEulerIntegrator());
        }
    }
}
