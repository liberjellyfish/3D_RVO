using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Rvo.Editor
{
    public static class Phase3DemoBuilder
    {
        public const string ProfilePath = "Assets/RVO/Demo/08_VolumeNavigation_ORCA.asset";
        public const string ScenePath = "Assets/RVO/Demo/Phase3_Volume.unity";
        [MenuItem("Tools/RVO/Create Phase 3 Volume Demo (256 cubed)")]
        public static void CreateDemoAssets()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play before baking.");
            var profile = AssetDatabase.LoadAssetAtPath<SimulationProfile>(ProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<SimulationProfile>(); Configure(profile);
                AssetDatabase.CreateAsset(profile, ProfilePath);
            }
            if (profile.BakedVolume == null) Bake(profile);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                Scene previous = SceneManager.GetActiveScene();
                if (string.IsNullOrEmpty(previous.path))
                {
                    if (!Application.isBatchMode || previous.isDirty) throw new InvalidOperationException("Save the current unnamed scene before creating the demo.");
                    previous = EditorSceneManager.OpenScene(Phase13DemoBuilder.ScenePath);
                }
                Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                try
                {
                    SceneManager.SetActiveScene(scene);
                    var camera = new GameObject("Volume Camera").AddComponent<Camera>(); camera.tag = "MainCamera";
                    camera.rect = new Rect(0.3f, 0, 0.7f, 1); camera.backgroundColor = new Color(0.025f, 0.04f, 0.065f);
                    camera.clearFlags = CameraClearFlags.SolidColor; camera.gameObject.AddComponent<VolumeCameraControls>();
                    camera.transform.SetPositionAndRotation(new Vector3(230, 180, -330), Quaternion.Euler(24, -35, 0)); camera.farClipPlane = 3000;
                    var driver = new GameObject("RVO Volume Navigation").AddComponent<VolumeSimulationBootstrap>();
                    var presenter = driver.gameObject.AddComponent<VolumePresenter>(); presenter.ViewCamera = camera;
                    presenter.Material = AssetDatabase.LoadAssetAtPath<Material>("Assets/RVO/Demo/AgentColors.mat");
                    driver.Profile = profile; driver.Presenter = presenter;
                    EditorSceneManager.SaveScene(scene, ScenePath);
                }
                finally { EditorSceneManager.CloseScene(scene, true); if (previous.IsValid()) SceneManager.SetActiveScene(previous); }
            }
            AssetDatabase.SaveAssets(); Debug.Log("Phase 3 ready: " + ScenePath);
        }
        public static void Configure(SimulationProfile profile)
        {
            profile.Simulation = SimulationSettings.Default; profile.Simulation.Dimension = SimulationDimension.Full3D;
            profile.Simulation.Avoidance = AvoidanceAlgorithm.ORCA; profile.Simulation.AgentCount = 16;
            profile.Simulation.NeighborSearch = NeighborSearchAlgorithm.SpatialHash; profile.Simulation.Backend = ExecutionBackend.JobsBurst;
            profile.Simulation.MaxNeighbors = 48; profile.Simulation.NeighborDistance = 16; profile.Simulation.CellSize = 8;
            profile.Simulation.TimeHorizon = 2; profile.Simulation.PreferredSideBias = 0.03f;
            profile.Simulation.Vo.SafetyMargin = 0.08f;
            profile.Scenario = ScenarioSettings.Default; profile.Scenario.Kind = ScenarioKind.RandomCrowd;
            profile.Scenario.Seed = 7; profile.Scenario.Radius = 0.8f; profile.Scenario.MaxSpeed = 8;
            profile.Scenario.ArrivalDistance = 0.2f; profile.Scenario.Extent = 128;
            profile.Navigation.Enabled = false; profile.Volume = VolumeSettings.Default;
            profile.AgentCountTiers = new Vector3Int(16, 256, 1024);
        }
        public static void RebuildDemo()
        {
            var profile = AssetDatabase.LoadAssetAtPath<SimulationProfile>(ProfilePath);
            if (profile == null) { CreateDemoAssets(); return; }
            Configure(profile); Bake(profile); CreateDemoAssets();
        }
        public static void Bake(SimulationProfile profile)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play before baking.");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var map = VolumeBake.Bake(profile.Volume, profile.Scenario.VolumeClearanceRadius, VolumeBake.DenseDemoBoxes(profile.Volume));
            string path = Path.ChangeExtension(AssetDatabase.GetAssetPath(profile), null) + "_Volume";
            File.WriteAllBytes(path + ".bytes", VolumeBake.Encode(map)); AssetDatabase.ImportAsset(path + ".bytes", ImportAssetOptions.ForceSynchronousImport);
            var asset = AssetDatabase.LoadAssetAtPath<BakedNavigationVolume>(path + ".asset");
            if (asset == null) { asset = ScriptableObject.CreateInstance<BakedNavigationVolume>(); AssetDatabase.CreateAsset(asset, path + ".asset"); }
            asset.SetData(AssetDatabase.LoadAssetAtPath<TextAsset>(path + ".bytes"), map); profile.BakedVolume = asset;
            EditorUtility.SetDirty(asset); EditorUtility.SetDirty(profile); AssetDatabase.SaveAssets();
            Debug.Log($"Baked XYZ volume: {asset.Summary}, {timer.Elapsed.TotalSeconds:F2}s");
        }
        [MenuItem("Tools/RVO/Rebake Selected Phase 3 Profile")]
        public static void RebakeSelected()
        {
            if (!(Selection.activeObject is SimulationProfile profile) || profile.Simulation.Dimension != SimulationDimension.Full3D)
                throw new InvalidOperationException("Select a Full3D SimulationProfile.");
            Bake(profile);
        }
    }
}
