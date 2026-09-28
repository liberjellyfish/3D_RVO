using UnityEngine;

namespace Rvo
{
    [CreateAssetMenu(menuName = "RVO/Simulation Profile", fileName = "RvoProfile")]
    public sealed class SimulationProfile : ScriptableObject
    {
        public SimulationSettings Simulation = SimulationSettings.Default;
        public ScenarioSettings Scenario = ScenarioSettings.Default;
        public BenchmarkSettings Benchmark = BenchmarkSettings.Default;
        public NavigationSettings Navigation = NavigationSettings.Default;
        public BakedNavigationMap BakedMap;
        [Tooltip("Phase 2 三档 Agent 数量；X/Y/Z 分别为低/中/高档，切档复用同一烘焙地图。")]
        public Vector3Int AgentCountTiers = new Vector3Int(16, 256, 1024);
        public int CountForTier(int tier) => tier == 0 ? AgentCountTiers.x : tier == 1 ? AgentCountTiers.y : AgentCountTiers.z;

        public void ValidateProfile()
        {
            Simulation.Validate();
            Scenario.Validate(Simulation.AgentCount);
            Benchmark.Validate();
            if (Navigation.Enabled)
            {
                Navigation.Validate(Scenario.Radius);
                if (AgentCountTiers.x < 1 || AgentCountTiers.y < AgentCountTiers.x || AgentCountTiers.z < AgentCountTiers.y)
                    throw new System.ArgumentException("Agent 三档数量应为递增正整数。");
                if (BakedMap == null) throw new System.ArgumentException("请先在 Profile Inspector 中 Bake 地图，再启动仿真。");
                BakedMap.Load(Navigation, Scenario.Radius);
                if (Simulation.Dimension != SimulationDimension.PlanarXZ || Simulation.Avoidance != AvoidanceAlgorithm.ORCA)
                    throw new System.ArgumentException("Phase 2 配置需要 XZ + ORCA。");
            }
        }
    }
}
