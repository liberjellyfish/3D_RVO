using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Rvo.Editor
{
    public static class Phase13DemoBuilder
    {
        public const string ScenePath = "Assets/RVO/Demo/Phase13_Demo.unity";

        [MenuItem("Tools/RVO/Create Phase 1.3 Demo Assets")]
        public static void CreateDemoAssets()
        {
            const string folder = "Assets/RVO/Demo";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/RVO", "Demo");
            var profiles = new[]
            {
                Profile("01_HeadOn_VO", ScenarioKind.HeadOnPair, 2, 6, AvoidanceAlgorithm.VO),
                Profile("02_Single_None", ScenarioKind.SingleAgent, 1, 5, AvoidanceAlgorithm.None),
                Profile("03_Crossing_VO", ScenarioKind.Crossing, 4, 8, AvoidanceAlgorithm.VO),
                Profile("04_Circle16_VO", ScenarioKind.CircleSwap, 16, 10, AvoidanceAlgorithm.VO),
                Profile("05_Opposing32_VO", ScenarioKind.OpposingGroups, 32, 12, AvoidanceAlgorithm.VO),
                Profile("06_Random32_VO", ScenarioKind.RandomCrowd, 32, 10, AvoidanceAlgorithm.VO)
            };
            string materialPath = folder + "/AgentColors.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(Shader.Find("RVO/Agent Colors"));
                AssetDatabase.CreateAsset(material, materialPath);
            }
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                // 在独立附加场景中生成，保存后关闭；不替换用户当前场景或未保存内容。
                Scene previous = SceneManager.GetActiveScene();
                if (string.IsNullOrEmpty(previous.path))
                    throw new System.InvalidOperationException("请先保存当前未命名场景，再创建演示场景。已有演示可直接打开。");
                Scene demo = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                SceneManager.SetActiveScene(demo);
                var camera = new GameObject("RVO Camera").AddComponent<Camera>();
                camera.tag = "MainCamera"; camera.orthographic = true;
                camera.rect = new Rect(0.36f, 0, 0.64f, 1);
                camera.transform.SetPositionAndRotation(new Vector3(0, 50, 0), Quaternion.Euler(90, 0, 0));
                camera.orthographicSize = 10; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.035f, 0.05f, 0.08f);
                var driver = new GameObject("RVO Simulation").AddComponent<SimulationBootstrap>();
                var presenter = driver.gameObject.AddComponent<AgentMeshPresenter>();
                presenter.AgentMaterial = material; presenter.ViewCamera = camera;
                driver.Presenter = presenter; driver.Profile = profiles[0]; driver.DemoProfiles = profiles;
                EditorSceneManager.SaveScene(demo, ScenePath);
                EditorSceneManager.CloseScene(demo, true);
                if (previous.IsValid()) SceneManager.SetActiveScene(previous);
            }
            AssetDatabase.SaveAssets();
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            Debug.Log("Phase 1.3 demo ready: " + ScenePath);
        }

        private static SimulationProfile Profile(string name, ScenarioKind kind, int count, float extent, AvoidanceAlgorithm algorithm)
        {
            string path = "Assets/RVO/Demo/" + name + ".asset";
            var profile = AssetDatabase.LoadAssetAtPath<SimulationProfile>(path);
            if (profile != null) return profile;
            profile = ScriptableObject.CreateInstance<SimulationProfile>();
            profile.Simulation.AgentCount = count;
            profile.Simulation.MaxNeighbors = Mathf.Max(1, count - 1);
            profile.Simulation.NeighborDistance = 30;
            profile.Simulation.Avoidance = algorithm;
            profile.Scenario.Kind = kind; profile.Scenario.Extent = extent;
            AssetDatabase.CreateAsset(profile, path);
            return profile;
        }
    }
}
