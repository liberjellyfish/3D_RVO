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
        public VolumeSettings Volume = VolumeSettings.Default;
        public BakedNavigationVolume BakedVolume;
        [Tooltip("Phase 2 三档 Agent 数量；X/Y/Z 分别为低/中/高档，切档复用同一烘焙地图。")]
        public Vector3Int AgentCountTiers = new Vector3Int(16, 256, 1024);
        public int CountForTier(int tier) => tier == 0 ? AgentCountTiers.x : tier == 1 ? AgentCountTiers.y : AgentCountTiers.z;

        public void ValidateProfile()
        {
            Simulation.Validate();
            Scenario.Validate(Simulation.AgentCount);
            Benchmark.Validate();
            if (Simulation.Dimension == SimulationDimension.Full3D)
            {
                Volume.Validate(Scenario.Radius);
                if (Navigation.Enabled || (Simulation.Avoidance != AvoidanceAlgorithm.None && Simulation.Avoidance != AvoidanceAlgorithm.ORCA) ||
                    Simulation.NeighborSearch == NeighborSearchAlgorithm.KdTree)
                    throw new System.ArgumentException("Full3D requires volume navigation, None/ORCA, and BruteForce/SpatialHash.");
                if (AgentCountTiers.x < 1 || AgentCountTiers.y < AgentCountTiers.x || AgentCountTiers.z < AgentCountTiers.y)
                    throw new System.ArgumentException("Agent tiers must be increasing positive counts.");
                if (BakedVolume == null) throw new System.ArgumentException("Bake the Full3D volume first.");
                BakedVolume.Load(Volume, Scenario.Radius);
                return;
            }
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
