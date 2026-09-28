using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Rvo.Editor
{
    public static class Phase2DemoBuilder
    {
        public const string ScenePath = "Assets/RVO/Demo/Phase2_Navigation.unity";
        public const string ProfilePath = "Assets/RVO/Demo/07_GridNavigation_ORCA.asset";
        [MenuItem("Tools/RVO/Upgrade Phase 2 Demo to 512 and Bake")]
        public static void UpgradeLargeDemo()
        {
            if (Application.isPlaying) throw new System.InvalidOperationException("请退出 Play 后升级演示。");
            CreateDemoAssets();
            var profile = AssetDatabase.LoadAssetAtPath<SimulationProfile>(ProfilePath);
            ConfigureLarge(profile); NavigationBakeEditor.Bake(profile, false);
            Scene previous = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(previous.path))
            {
                if (!Application.isBatchMode || previous.isDirty) throw new System.InvalidOperationException("请先保存当前场景。");
                previous = EditorSceneManager.OpenScene(Phase13DemoBuilder.ScenePath);
            }
            var loaded = SceneManager.GetSceneByPath(ScenePath);
            bool opened = !loaded.IsValid() || !loaded.isLoaded;
            var scene = opened ? EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive) : loaded;
            try
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.TryGetComponent<SimulationBootstrap>(out var driver))
                    {
                        driver.Profile = profile; driver.CollectQuality = false; driver.AgentTier = 0;
                        driver.HudFontSize = 17;
                        if (driver.MapPresenter != null) driver.MapPresenter.MaxDisplayedPaths = 32;
                    }
                    if (root.TryGetComponent<Camera>(out var camera))
                    {
                        camera.orthographicSize = 300;
                        if (!root.TryGetComponent<NavigationCameraControls>(out _)) root.AddComponent<NavigationCameraControls>();
                    }
                }
                EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            }
            finally { if (opened) EditorSceneManager.CloseScene(scene, true); if (previous.IsValid()) SceneManager.SetActiveScene(previous); }
            AssetDatabase.SaveAssets();
        }
        private static void ConfigureLarge(SimulationProfile profile)
        {
            profile.Navigation = NavigationSettings.Default; profile.Navigation.Enabled = true;
            profile.AgentCountTiers = new Vector3Int(16, 256, 1024);
            profile.Simulation.AgentCount = 16; profile.Simulation.MaxNeighbors = 48;
            profile.Simulation.NeighborDistance = 56; profile.Simulation.TimeHorizon = 2;
            profile.Simulation.CellSize = 24; profile.Simulation.Vo.SafetyMargin = 0.08f;
            profile.Simulation.PreferredSideBias = 0.08f; profile.Simulation.Avoidance = AvoidanceAlgorithm.ORCA;
            profile.Simulation.NeighborSearch = NeighborSearchAlgorithm.SpatialHash;
            profile.Simulation.Backend = ExecutionBackend.JobsBurst;
            profile.Scenario.Kind = ScenarioKind.RandomCrowd; profile.Scenario.Extent = 256;
            profile.Scenario.Radius = 2.5f; profile.Scenario.MaxSpeed = 12; profile.Scenario.ArrivalDistance = 0.5f;
            EditorUtility.SetDirty(profile);
        }
        [MenuItem("Tools/RVO/Create Phase 2 Navigation Demo")]
        public static void CreateDemoAssets()
        {
            const string profilePath = ProfilePath;
            var profile = AssetDatabase.LoadAssetAtPath<SimulationProfile>(profilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<SimulationProfile>();
                ConfigureLarge(profile);
                AssetDatabase.CreateAsset(profile, profilePath);
                NavigationBakeEditor.Bake(profile, false);
            }
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                Scene previous = SceneManager.GetActiveScene();
                if (string.IsNullOrEmpty(previous.path))
                {
                    if (!Application.isBatchMode || previous.isDirty)
                        throw new System.InvalidOperationException("请先保存当前未命名场景，再创建 Phase 2 演示。");
                    previous = EditorSceneManager.OpenScene(Phase13DemoBuilder.ScenePath);
                }
                Scene demo = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                try
                {
                    SceneManager.SetActiveScene(demo);
                    var camera = new GameObject("Navigation Camera").AddComponent<Camera>();
                    camera.tag = "MainCamera"; camera.orthographic = true; camera.orthographicSize = 16;
                    camera.rect = new Rect(0.36f, 0, 0.64f, 1);
                    camera.transform.SetPositionAndRotation(new Vector3(0, 50, 0), Quaternion.Euler(90, 0, 0));
                    camera.backgroundColor = new Color(0.035f, 0.05f, 0.08f); camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.gameObject.AddComponent<NavigationCameraControls>();
                    var driver = new GameObject("RVO Grid Navigation").AddComponent<SimulationBootstrap>();
                    var presenter = driver.gameObject.AddComponent<AgentMeshPresenter>();
                    var map = driver.gameObject.AddComponent<GridMapPresenter>();
                    var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/RVO/Demo/AgentColors.mat");
                    if (material == null) throw new System.InvalidOperationException("缺少 AgentColors.mat，请先创建 Phase 1 演示资源。");
                    presenter.AgentMaterial = material; presenter.ViewCamera = camera; map.Material = material;
                    driver.Profile = profile; driver.Presenter = presenter; driver.MapPresenter = map;
                    driver.CollectQuality = false;
                    EditorSceneManager.SaveScene(demo, ScenePath);
                }
                finally
                {
                    EditorSceneManager.CloseScene(demo, true);
                    if (previous.IsValid()) SceneManager.SetActiveScene(previous);
                }
            }
            AssetDatabase.SaveAssets();
            Debug.Log("Phase 2 demo ready: " + ScenePath);
        }
    }
}
