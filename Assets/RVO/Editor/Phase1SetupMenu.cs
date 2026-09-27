using UnityEditor;
using UnityEngine;

namespace Rvo.Editor
{
    public static class Phase1SetupMenu
    {
        [MenuItem("Tools/RVO/Create Phase 1 Benchmark Profiles")]
        public static void CreateBenchmarkProfiles()
        {
            const string directory = "Assets/RVO/Configurations";
            if (!AssetDatabase.IsValidFolder(directory)) AssetDatabase.CreateFolder("Assets/RVO", "Configurations");
            foreach (int count in new[] { 100, 1000, 10000 })
            {
                string path = $"{directory}/Phase1_{count}.asset";
                if (AssetDatabase.LoadMainAssetAtPath(path) != null) continue;
                var profile = ScriptableObject.CreateInstance<SimulationProfile>();
                profile.Simulation.AgentCount = count;
                profile.Simulation.Avoidance = AvoidanceAlgorithm.ORCA;
                profile.Simulation.NeighborSearch = NeighborSearchAlgorithm.SpatialHash;
                profile.Simulation.Backend = ExecutionBackend.JobsBurst;
                profile.Scenario.Kind = ScenarioKind.OpposingGroups;
                profile.Scenario.Extent = Mathf.Ceil(Mathf.Sqrt(count)) * 1.5f;
                AssetDatabase.CreateAsset(profile, path);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("RVO: 100 / 1k / 10k configuration assets created; ready to run explicitly; formal scale validation is NOT completed.");
        }

        [MenuItem("GameObject/RVO/Phase 1 Framework", false, 10)]
        public static void CreateFrameworkObject(MenuCommand command)
        {
            var go = new GameObject("RVO Phase 1 Framework");
            GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create RVO Framework");
            go.AddComponent<SimulationBootstrap>();
            Selection.activeGameObject = go;
        }
    }
}
