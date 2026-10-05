using System;
using Rvo.Rendering;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.Universal;

namespace Rvo.Editor
{
    public static class Phase4DemoBuilder
    {
        public const string FixtureScene = "Assets/RVO/Demo/Phase4_RenderOnly.unity";
        public const string LiveScene = "Assets/RVO/Demo/Phase4_Live.unity";
        public const string OceanScene = "Assets/RVO/Demo/Phase4_Ocean.unity";
        public const string OceanLiveScene = "Assets/RVO/Demo/Phase4_OceanLive.unity";
        public const string SurfaceStudyScene = "Assets/RVO/Demo/Phase4_SurfaceStudy.unity";
        public const string ComputePath = "Assets/RVO/Rendering/Shaders/FishCulling.compute";
        [MenuItem("Tools/RVO/Create Phase 4 GPU Fish Demos")]
        public static void Create()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play before creating scenes.");
            CreateScene(FixtureScene, false); CreateScene(LiveScene, true); AssetDatabase.SaveAssets();
            Debug.Log("Phase 4 ready: " + FixtureScene + " and " + LiveScene);
        }
        [MenuItem("Tools/RVO/Create Phase 4 Ocean Demos")]
        public static void CreateOcean()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play before creating scenes.");
            CreateScene(OceanScene, false, true); CreateScene(OceanLiveScene, true, true); AssetDatabase.SaveAssets();
        }
        private static void CreateScene(string path, bool live, bool ocean = false)
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
                Material oceanSurface = null;
                if (ocean)
                {
                    renderer.DetailedNearMesh = true;
                    var water = camera.gameObject.AddComponent<OceanEnvironment>();
                    water.CausticCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/RVO/Rendering/Shaders/CausticGenerate.compute");
                    water.FogShader = Shader.Find("RVO/Ocean Beer Fog"); water.SurfaceShader = Shader.Find("RVO/Ocean Surface");
                    ConfigureOceanP0(water, live);
                    if (live) water.Size = new Vector3(1200,800,1200);
                    oceanSurface = ReceiverMaterial();
                    AddOceanLight();
                    camera.gameObject.AddComponent<OceanDemoControls>().Fish = renderer;
                }
                if (live)
                {
                    var source = root.AddComponent<VolumeSimulationBootstrap>();
                    source.Profile = AssetDatabase.LoadAssetAtPath<SimulationProfile>(Phase3DemoBuilder.ProfilePath);
                    source.AgentTier = 2;
                    var bridge = root.AddComponent<FishLiveBridge>(); bridge.Source = source; bridge.Renderer = renderer;
                    if (ocean) root.AddComponent<FishBenchmarkRecorder>().Live = bridge;
                    // 静态可视障碍只从不可变烘焙数据生成一次，不回写地图。
                    var map = source.Profile.BakedVolume.Load(source.Profile.Volume, source.Profile.Scenario.VolumeClearanceRadius);
                    camera.transform.LookAt((Vector3)((map.Min + map.Max) * 0.5f));
                    if (ocean) ConfigureOceanLiveCamera(camera);
                    var environment = new GameObject("Baked obstacles (visual only)");
                    for (int i = 0; i < map.ObstacleCount; i++)
                    {
                        var box = map.Obstacle(i); var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        obstacle.name = "Obstacle " + i; obstacle.transform.SetParent(environment.transform);
                        obstacle.transform.position = (Vector3)((box.Min + box.Max) * 0.5f);
                        obstacle.transform.localScale = (Vector3)(box.Max - box.Min);
                        UnityEngine.Object.DestroyImmediate(obstacle.GetComponent<Collider>());
                        if (oceanSurface != null) obstacle.GetComponent<MeshRenderer>().sharedMaterial = oceanSurface;
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

        private static void ConfigureSurfaceMaterial(Material material, Color baseColor, float scale)
        {
            material.SetColor("_BaseColor",baseColor); material.SetColor("_PatternColor",Color.white);
            material.SetFloat("_PatternGain",1.8f); material.SetFloat("_PatternMapping",1);
            material.SetFloat("_PatternScale",scale); material.SetFloat("_PatternSource",0);
            material.SetFloat("_LightingWeight",0.75f);
            EditorUtility.SetDirty(material);
        }

        private static void AddOceanLight()
        {
            var sun = new GameObject("Ocean Sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.3f; sun.color = Color.white;
            sun.transform.rotation = Quaternion.Euler(48,-28,0); sun.shadows = LightShadows.Soft;
            RenderSettings.sun = sun;
        }

        private static Material ReceiverMaterial()
        {
            const string path = "Assets/RVO/Demo/Phase4_UnderwaterReceiver.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            material = new Material(Shader.Find("RVO/Underwater Receiver"));
            material.SetColor("_BaseColor", new Color(0.25f, 0.32f, 0.3f));
            AssetDatabase.CreateAsset(material, path); return material;
        }

        private static void ConfigureOceanP0(OceanEnvironment water, bool live)
        {
            water.SurfaceStudyBackground = false; water.BackgroundMaterial = null;
            water.WaterSurfaceHeight = live ? 200 : 90; water.HorizonDistance = 600;
            water.Extinction = WaterOptics.Calibrate(new Vector3(0.55f, 0.72f, 0.82f), 100);
            water.WaterColor = new Color(0.015f, 0.07f, 0.11f);
            water.CausticStrength = 0.4f; water.Background = true; water.Fog = true;
        }

        private static void ConfigureOceanLiveCamera(Camera camera)
        {
            camera.transform.position = new Vector3(-115, 18, -110);
            var pivot = new Vector3(-65, 0, 0);
            camera.transform.LookAt(pivot); camera.farClipPlane = 800;
            camera.GetComponent<VolumeCameraControls>().Pivot = pivot;
        }

        [MenuItem("Tools/RVO/Apply Ocean P0 Structure")]
        public static void ApplyOceanP0()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play before updating scenes.");
            CreateOcean(); CreateSurfaceStudy();
            var material = ReceiverMaterial();
            foreach (string path in new[] { OceanScene, OceanLiveScene, SurfaceStudyScene })
            {
                var previous = SceneManager.GetActiveScene();
                var scene = SceneManager.GetSceneByPath(path);
                bool opened = !scene.IsValid() || !scene.isLoaded;
                if (!opened && scene.isDirty) throw new InvalidOperationException("Save the Ocean scene before applying P0.");
                if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                SceneManager.SetActiveScene(scene);
                try
                {
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        var water = root.GetComponent<OceanEnvironment>();
                        if (water != null)
                        {
                            if (path == SurfaceStudyScene) water.SurfaceStudyBackground = true;
                            else ConfigureOceanP0(water, path == OceanLiveScene);
                            var camera = water.GetComponent<Camera>();
                            camera.allowHDR = true;
                            camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
                            if (path == OceanLiveScene) ConfigureOceanLiveCamera(camera);
                        }
                        if (path != SurfaceStudyScene)
                            foreach (var receiver in root.GetComponentsInChildren<MeshRenderer>()) receiver.sharedMaterial = material;
                        if (root.TryGetComponent<Light>(out var sun) && sun.type == LightType.Directional) RenderSettings.sun = sun;
                    }
                    EditorSceneManager.SaveScene(scene);
                }
                finally { if (opened) EditorSceneManager.CloseScene(scene, true); if (previous.IsValid()) SceneManager.SetActiveScene(previous); }
            }
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Tools/RVO/Apply Surface Pattern Look to Surface Study")]
        public static void ApplySurfaceLook()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play before updating scenes.");
            CreateSurfaceStudy();
            var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/RVO/Demo/Phase4_SurfaceStudy.mat");
            ConfigureSurfaceMaterial(material,new Color(0,0.35f,0.5f),0.008f);
            foreach(string path in new[] { SurfaceStudyScene })
            {
                var previous = SceneManager.GetActiveScene();
                var scene = SceneManager.GetSceneByPath(path);
                bool opened = !scene.IsValid() || !scene.isLoaded;
                if(!opened && scene.isDirty) throw new InvalidOperationException("Save the Ocean scene before applying its surface preset.");
                if(opened) scene = EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);
                SceneManager.SetActiveScene(scene);
                try
                {
                    bool hasSun = false;
                    foreach(var root in scene.GetRootGameObjects())
                    {
                        if(root.GetComponent<Light>() != null) hasSun = true;
                        var water = root.GetComponent<OceanEnvironment>();
                        if(water == null) continue;
                        water.BackgroundMaterial = material;
                        water.SurfaceStudyBackground = true;
                        water.WaterColor = new Color(0.015f,0.07f,0.11f);
                        water.Extinction = new Vector3(0.004f,0.002f,0.0015f);
                    }
                    if(!hasSun) AddOceanLight();
                    EditorSceneManager.SaveScene(scene);
                }
                finally { if(opened) EditorSceneManager.CloseScene(scene,true); if(previous.IsValid()) SceneManager.SetActiveScene(previous); }
            }
            CreateSurfaceStudy(); AssetDatabase.SaveAssets();
        }

        [MenuItem("Tools/RVO/Create Phase 4 Surface Material Study")]
        public static void CreateSurfaceStudy()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play before creating scenes.");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SurfaceStudyScene) != null) return;
            var previous = SceneManager.GetActiveScene();
            if(string.IsNullOrEmpty(previous.path))
            {
                if(!Application.isBatchMode || previous.isDirty) throw new InvalidOperationException("Save the unnamed scene before creating the study.");
                previous=EditorSceneManager.OpenScene(Phase3DemoBuilder.ScenePath);
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            try
            {
                const string materialPath = "Assets/RVO/Demo/Phase4_SurfaceStudy.mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if(material == null)
                {
                    material = new Material(Shader.Find("RVO/Ocean Surface"));
                    ConfigureSurfaceMaterial(material,new Color(0.08f,0.08f,0.08f),0.085f);
                    AssetDatabase.CreateAsset(material,materialPath);
                }
                var camera = new GameObject("Surface Study Camera").AddComponent<Camera>();
                camera.tag="MainCamera"; camera.nearClipPlane=0.1f; camera.farClipPlane=60;
                camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=Color.black;
                camera.transform.position=new Vector3(0,1,-3); camera.transform.LookAt(new Vector3(0,1,5));
                camera.gameObject.AddComponent<VolumeCameraControls>();
                var water=camera.gameObject.AddComponent<OceanEnvironment>();
                water.CausticCompute=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/RVO/Rendering/Shaders/CausticGenerate.compute");
                water.FogShader=Shader.Find("RVO/Ocean Beer Fog"); water.SurfaceShader=Shader.Find("RVO/Ocean Surface");
                water.BackgroundMaterial=material; water.Size=new Vector3(12,8,16); water.Center=new Vector3(0,2,3);
                water.SurfaceStudyBackground=true;
                water.Fog=false; water.Quality=CausticQuality.Shared512; water.UpdateHz=30;
                AddOceanLight();
                var centers=new[] { new Vector3(-2,-0.75f,3),new Vector3(2,-0.75f,4),new Vector3(1.6f,3,5) };
                for(int i=0;i<centers.Length;i++)
                {
                    var sphere=GameObject.CreatePrimitive(PrimitiveType.Sphere); sphere.name="Pattern sphere "+(i+1);
                    sphere.transform.position=centers[i]; sphere.transform.localScale=Vector3.one*2.5f;
                    sphere.GetComponent<MeshRenderer>().sharedMaterial=material;
                    UnityEngine.Object.DestroyImmediate(sphere.GetComponent<Collider>());
                }
                EditorSceneManager.SaveScene(scene,SurfaceStudyScene);
            }
            finally { EditorSceneManager.CloseScene(scene,true); if(previous.IsValid()) SceneManager.SetActiveScene(previous); }
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

        [MenuItem("Tools/RVO/Build Phase 4 Ocean Benchmark Player")]
        public static void BuildOceanBenchmark()
        { BuildOcean(false); }

        [MenuItem("Tools/RVO/Build Phase 4 Ocean Live Benchmark Player")]
        public static void BuildOceanLiveBenchmark()
        { BuildOcean(true); }

        private static void BuildOcean(bool live)
        {
            CreateOcean();
            bool timing = PlayerSettings.enableFrameTimingStats;
            try
            {
                PlayerSettings.enableFrameTimingStats = true;
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { live ? OceanLiveScene : OceanScene }, target = BuildTarget.StandaloneWindows64,
                    locationPathName = live ? "Builds/Phase4OceanLive/Phase4OceanLive.exe" : "Builds/Phase4Ocean/Phase4Ocean.exe", options = BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Ocean Player build failed.");
            }
            finally { PlayerSettings.enableFrameTimingStats = timing; }
        }
    }
}
