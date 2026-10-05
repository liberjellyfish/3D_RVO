using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Rvo
{
    public static class Phase3ModuleFactory
    {
        public static SimulationModules Create(in SimulationSettings settings, in ScenarioSettings scenario,
            in VolumeSettings volumeSettings, NavigationVolume map, out VolumeNavigation navigation, out VolumeAvoidanceSolver solver)
        {
            settings.Validate(); scenario.Validate(settings.AgentCount); volumeSettings.Validate(scenario.VolumeClearanceRadius);
            if (settings.Dimension != SimulationDimension.Full3D || (settings.Avoidance != AvoidanceAlgorithm.None && settings.Avoidance != AvoidanceAlgorithm.ORCA))
                throw new NotSupportedException("Phase 3 requires Full3D + None (static-only) or ORCA.");
            if (settings.NeighborSearch == NeighborSearchAlgorithm.KdTree) throw new NotSupportedException("XYZ KDTree is not implemented; use BruteForce or SpatialHash.");
            if (map == null || map.Resolution != volumeSettings.Resolution || map.CellSize != volumeSettings.CellSize ||
                math.abs(map.ClearanceRadius - scenario.VolumeClearanceRadius - volumeSettings.SafetyMargin) > 1e-6f) throw new ArgumentException("Baked volume does not match the profile. Re-bake after changing size variation.");
            navigation = new VolumeNavigation(volumeSettings, map, settings.AgentCount, settings.Backend); solver = null;
            try
            {
                solver = new VolumeAvoidanceSolver(navigation, settings.AgentCount, settings.MaxNeighbors);
                INeighborSearch neighbors = settings.NeighborSearch == NeighborSearchAlgorithm.BruteForce
                    ? (INeighborSearch)new BruteForceNeighborSearch() : new SpatialHashNeighborSearch3D();
                return new SimulationModules(new VolumeScenarioInitializer(map), navigation, neighbors, solver, new VolumeEulerIntegrator());
            }
            catch { solver?.Dispose(); navigation.Dispose(); throw; }
        }
    }
    internal sealed class VolumeScenarioInitializer : StatelessModule, IScenarioInitializer
    {
        private readonly NavigationVolume map;
        public override string Name => "Baked XYZ spawn sampling";
        public VolumeScenarioInitializer(NavigationVolume map) { this.map = map; }
        public void Initialize(in SimulationSettings settings, in ScenarioSettings scenario, in AgentInitializationView initialization)
        {
            var a = initialization; var random = new Unity.Mathematics.Random(scenario.Seed);
            for (int i = 0; i < settings.AgentCount; i++)
            {
                a.Ids[i] = i; a.Velocities[i] = float3.zero;
                // Separate stable stream: appearance/count tiers never consume the spawn RNG.
                var traits = Unity.Mathematics.Random.CreateFromIndex(math.hash(new uint2(scenario.Seed, (uint)i)) % uint.MaxValue);
                float radius = scenario.Radius * traits.NextFloat(1 - scenario.VolumeSizeVariation, 1 + scenario.VolumeSizeVariation);
                float speed = scenario.MaxSpeed * traits.NextFloat(1 - scenario.VolumeSpeedVariation, 1 + scenario.VolumeSpeedVariation);
                a.Parameters[i] = new AgentParameters { Radius = radius, MaxSpeed = speed, ArrivalDistance = scenario.ArrivalDistance };
                // Alternating directions across the fixed apertures; prefix stable across all three count tiers.
                a.Positions[i] = Pick(a.Positions, a.Parameters, i, i % 2 == 0, radius, ref random);
                a.Goals[i] = Pick(a.Goals, a.Parameters, i, i % 2 != 0, radius, ref random);
            }
        }
        private float3 Pick(NativeArray<float3> points, NativeArray<AgentParameters> parameters, int count, bool left, float radius, ref Unity.Mathematics.Random random)
        {
            for (int attempt = 0; attempt < 16384; attempt++)
            {
                int cell = random.NextInt(map.Count); if (map.Component(cell) != map.LargestComponent) continue;
                float3 p = map.Center(cell); float normalizedX = (p.x - map.Min.x) / (map.Max.x - map.Min.x);
                if (left ? normalizedX > 0.2f : normalizedX < 0.8f) continue;
                bool free = true;
                for (int j = 0; j < count; j++)
                {
                    float spacing = radius + parameters[j].Radius + 0.3f;
                    if (math.distancesq(p, points[j]) < spacing * spacing) { free = false; break; }
                }
                if (free) return p;
            }
            throw new InvalidOperationException("Cannot place separated starts/goals in the largest volume component.");
        }
    }
}
