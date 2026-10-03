using System;
using Rvo.Rendering;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Rvo.Editor
{
    public static class Phase4DemoBuilder
    {
        public const string FixtureScene = "Assets/RVO/Demo/Phase4_RenderOnly.unity";
        public const string LiveScene = "Assets/RVO/Demo/Phase4_Live.unity";
        public const string ComputePath = "Assets/RVO/Rendering/Shaders/FishCulling.compute";
        [MenuItem("Tools/RVO/Create Phase 4 GPU Fish Demos")]
        public static void Create()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play before creating scenes.");
            CreateScene(FixtureScene, false); CreateScene(LiveScene, true); AssetDatabase.SaveAssets();
            Debug.Log("Phase 4 ready: " + FixtureScene + " and " + LiveScene);
        }
        private static void CreateScene(string path, bool live)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null) return;
            Scene previous = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(previous.path))
            {
                if (!Application.isBatchMode || previous.isDirty) throw new InvalidOperationException("Save the unnamed scene before creating the demo.");
                previous = EditorSceneManager.OpenScene(Phase3DemoBuilder.ScenePath);
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                var camera = new GameObject("Fish Camera").AddComponent<Camera>(); camera.tag = "MainCamera";
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.025f, 0.04f, 0.065f);
                camera.transform.position = live ? new Vector3(230, 180, -330) : new Vector3(0, 22, -90);
                camera.transform.LookAt(Vector3.zero); camera.farClipPlane = 3000; camera.nearClipPlane = 0.1f;
                camera.gameObject.AddComponent<VolumeCameraControls>();
                var root = new GameObject(live ? "Live GPU Fish" : "Synthetic GPU Fish");
                var renderer = root.AddComponent<GpuFishRenderer>(); renderer.ViewCamera = camera;
                renderer.FishShader = Shader.Find("RVO/Procedural Fish Indirect");
                renderer.CullingShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
                if (live)
                {
                    var source = root.AddComponent<VolumeSimulationBootstrap>();
                    source.Profile = AssetDatabase.LoadAssetAtPath<SimulationProfile>(Phase3DemoBuilder.ProfilePath);
                    source.AgentTier = 2;
                    var bridge = root.AddComponent<FishLiveBridge>(); bridge.Source = source; bridge.Renderer = renderer;
                    // 静态可视障碍只从不可变烘焙数据生成一次，不回写地图。
                    var map = source.Profile.BakedVolume.Load(source.Profile.Volume, source.Profile.Scenario.Radius);
                    camera.transform.LookAt((Vector3)((map.Min + map.Max) * 0.5f));
                    var environment = new GameObject("Baked obstacles (visual only)");
                    for (int i = 0; i < map.ObstacleCount; i++)
                    {
                        var box = map.Obstacle(i); var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        obstacle.name = "Obstacle " + i; obstacle.transform.SetParent(environment.transform);
                        obstacle.transform.position = (Vector3)((box.Min + box.Max) * 0.5f);
                        obstacle.transform.localScale = (Vector3)(box.Max - box.Min);
                        UnityEngine.Object.DestroyImmediate(obstacle.GetComponent<Collider>());
                    }
                }
                else
                {
                    var fixture = root.AddComponent<FishRenderFixture>(); fixture.Renderer = renderer;
                    var debug = new GameObject("CPU sphere baseline").AddComponent<VolumePresenter>();
                    debug.Material = AssetDatabase.LoadAssetAtPath<Material>("Assets/RVO/Demo/AgentColors.mat");
                    fixture.DebugPresenter = debug; debug.gameObject.SetActive(false);
                    root.AddComponent<FishBenchmarkRecorder>().Fixture = fixture;
                }
                EditorSceneManager.SaveScene(scene, path);
            }
            finally { EditorSceneManager.CloseScene(scene, true); if (previous.IsValid()) SceneManager.SetActiveScene(previous); }
        }
        [MenuItem("Tools/RVO/Build Phase 4 Render Benchmark Player")]
        public static void BuildBenchmark()
        {
            Create();
            bool timing = PlayerSettings.enableFrameTimingStats;
            try
            {
                PlayerSettings.enableFrameTimingStats = true;
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { FixtureScene }, target = BuildTarget.StandaloneWindows64,
                    locationPathName = "Builds/Phase4/Phase4.exe", options = BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Phase 4 Player build failed.");
            }
            finally { PlayerSettings.enableFrameTimingStats = timing; }
        }
    }
}
