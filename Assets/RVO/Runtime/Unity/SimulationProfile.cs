using UnityEngine;

namespace Rvo
{
    [CreateAssetMenu(menuName = "RVO/Simulation Profile", fileName = "RvoProfile")]
    public sealed class SimulationProfile : ScriptableObject
    {
        public SimulationSettings Simulation = SimulationSettings.Default;
        public ScenarioSettings Scenario = ScenarioSettings.Default;
        public BenchmarkSettings Benchmark = BenchmarkSettings.Default;

        public void ValidateProfile()
        {
            Simulation.Validate();
            Scenario.Validate(Simulation.AgentCount);
            Benchmark.Validate();
        }
    }
}
