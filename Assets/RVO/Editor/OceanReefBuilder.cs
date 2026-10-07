using System;
using System.Collections.Generic;
using System.IO;
using Rvo.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Rvo.Editor
{
    /// <summary>先规划保守障碍体，再在体积内生成礁石网格；运行时不生成导航数据。</summary>
    public static class OceanReefBuilder
    {
        public const string Folder = "Assets/RVO/Demo/OceanReef";
        public const string ScenePath = Folder + "/OceanReefLive.unity";
        private static readonly List<VolumeBox> proxies = new List<VolumeBox>();

        [MenuItem("Tools/RVO/Build Ocean Reef Presentation")]
        public static void Build()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play before rebuilding the reef.");
            var previous = SceneManager.GetActiveScene();
            if (previous.isDirty) throw new InvalidOperationException("Save the current scene before rebuilding the reef.");
            if (string.IsNullOrEmpty(previous.path))
            {
                if (!Application.isBatchMode) throw new InvalidOperationException("Save the unnamed scene before rebuilding the reef.");
                previous = EditorSceneManager.OpenScene(Phase4DemoBuilder.OceanLiveScene);
            }
            if (SceneManager.GetSceneByPath(ScenePath).isLoaded) throw new InvalidOperationException("Close the reef scene before rebuilding it.");
            Directory.CreateDirectory(Folder); AssetDatabase.Refresh();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            try
            {
                proxies.Clear();
                var terrain = new GameObject("Authored reef meshes").transform;
                var stone = Material("Reef limestone", new Color(0.29f,0.32f,0.25f),0.22f);
                var sand = Material("Sand",new Color(0.38f,0.37f,0.27f),0.12f);
                var paleStone = Material("Pale coral limestone",new Color(0.42f,0.40f,0.30f),0.18f);
                foreach(var piece in ReefRouteLayout.Pieces())
                    Rock(terrain,piece.Name,piece.Center,piece.Size,piece.Name=="Network seabed" ? sand : piece.Pale ? paleStone : stone,piece.Seed);
                if(proxies.Count!=48) throw new InvalidOperationException("Network layout must contain 48 planned solids.");
                var profile = BakeProfile();
                var root = new GameObject("Live reef navigation");
                var source = root.AddComponent<VolumeSimulationBootstrap>(); source.Profile=profile; source.AgentTier=2; source.ShowHud=true;
                root.AddComponent<ReefPathOverlay>().Source=source;
                var fish = root.AddComponent<GpuFishRenderer>(); fish.ReefAppearance=true; fish.EnableMotionVectors=true;
                fish.LodPixels=new Vector3(60,22,7); fish.LodHysteresis=0.2f;
                fish.FishShader=Shader.Find("RVO/Procedural Fish Indirect");
                fish.CullingShader=AssetDatabase.LoadAssetAtPath<ComputeShader>(Phase4DemoBuilder.ComputePath);
                var bridge=root.AddComponent<FishLiveBridge>(); bridge.Source=source; bridge.Renderer=fish;
                root.AddComponent<FishBenchmarkRecorder>().Live=bridge;
                var camera=new GameObject("Reef camera").AddComponent<Camera>(); camera.tag="MainCamera";
                camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(0.015f,0.07f,0.11f);
                camera.nearClipPlane=0.1f; camera.farClipPlane=420; camera.allowHDR=true; camera.allowMSAA=false; camera.fieldOfView=55;
                camera.transform.position=new Vector3(-92,74,-100); camera.transform.LookAt(new Vector3(0,-2,0));
                camera.gameObject.AddComponent<VolumeCameraControls>().Pivot=new Vector3(0,1,0); fish.ViewCamera=camera;
                var water=camera.gameObject.AddComponent<OceanEnvironment>();
                water.Size=new Vector3(420,240,420); water.WaterSurfaceHeight=65; water.HorizonDistance=250;
                water.Extinction=WaterOptics.Calibrate(new Vector3(0.55f,0.72f,0.82f),100);
                // Hoskins/joltz0r turbulence feeds the lit receivers, shared once across all fish/rocks.
                water.Quality=CausticQuality.Shared512; water.CausticStrength=3.2f; water.WorldScale=0.085f;
                water.FogShader=Shader.Find("RVO/Ocean Beer Fog");
                water.CausticCompute=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/RVO/Rendering/Shaders/CausticGenerate.compute");
                var controls=camera.gameObject.AddComponent<OceanDemoControls>(); controls.Fish=fish; controls.ShowHud=false;
                var presentation=camera.gameObject.AddComponent<OceanPresentation>(); presentation.enabled=false;
                presentation.Fish=fish; presentation.Pipeline=Pipeline();
                var debug=new GameObject("Baked navigation proxies (debug only)"); debug.SetActive(false);
                var map=profile.BakedVolume.Load(profile.Volume,profile.Scenario.VolumeClearanceRadius);
                var debugMat=Material("Proxy diagnostic",new Color(0.6f,0.22f,0.08f),0);
                for(int i=0;i<map.ObstacleCount;i++)
                {
                    var box=map.Obstacle(i); var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.name="Conservative proxy "+i; cube.transform.SetParent(debug.transform);
                    cube.transform.position=(Vector3)((box.Min+box.Max)*0.5f); cube.transform.localScale=(Vector3)(box.Max-box.Min);
                    UnityEngine.Object.DestroyImmediate(cube.GetComponent<Collider>()); cube.GetComponent<MeshRenderer>().sharedMaterial=debugMat;
                }
                presentation.DebugProxies=debug.transform; presentation.enabled=true;
                var sun=new GameObject("Reef sun").AddComponent<Light>(); sun.type=LightType.Directional;
                sun.intensity=2.2f; sun.color=new Color(1,0.96f,0.86f); sun.shadows=LightShadows.Soft;
                sun.transform.rotation=Quaternion.Euler(48,38,0); RenderSettings.sun=sun;
                RenderSettings.ambientMode=AmbientMode.Trilight;
                RenderSettings.ambientSkyColor=new Color(0.16f,0.24f,0.27f);
                RenderSettings.ambientEquatorColor=new Color(0.08f,0.13f,0.14f);
                RenderSettings.ambientGroundColor=new Color(0.035f,0.05f,0.045f);
                RenderSettings.skybox=null;
                EditorSceneManager.SaveScene(scene,ScenePath); AssetDatabase.SaveAssets();
                Debug.Log("Ocean reef presentation built: "+ScenePath);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene,true);
                if(previous.IsValid()) SceneManager.SetActiveScene(previous);
            }
        }

        private static Material Material(string name, Color color, float smoothness)
        {
            string path=Folder+"/"+name+".mat";
            var value=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(value==null) { value=new Material(Shader.Find("RVO/Underwater Receiver")); AssetDatabase.CreateAsset(value,path); }
            value.SetColor("_BaseColor",color); value.SetFloat("_ReefDetail",1); value.SetFloat("_Smoothness",smoothness);
            value.SetFloat("_CausticGain",1.8f);
            EditorUtility.SetDirty(value); return value;
        }

        private static UniversalRenderPipelineAsset Pipeline()
        {
            string path=Folder+"/OceanPipeline.asset";
            var asset=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
            if(asset==null)
            {
                AssetDatabase.CopyAsset("Assets/Settings/PC_RPAsset.asset",path);
                asset=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
            }
            asset.shadowDistance=160; asset.msaaSampleCount=1; asset.renderScale=1;
            EditorUtility.SetDirty(asset); return asset;
        }

        private static SimulationProfile BakeProfile()
        {
            string path=Folder+"/ReefNavigation.asset";
            var profile=AssetDatabase.LoadAssetAtPath<SimulationProfile>(path);
            if(profile==null) { profile=ScriptableObject.CreateInstance<SimulationProfile>(); AssetDatabase.CreateAsset(profile,path); }
            Phase3DemoBuilder.Configure(profile); profile.Volume.Resolution=192; profile.Volume.CellSize=0.5f;
            profile.Scenario.Radius=0.65f; profile.Scenario.MaxSpeed=5; profile.Scenario.Extent=96;
            profile.Scenario.VolumeSizeVariation=0.3f; profile.Scenario.VolumeSpeedVariation=0.25f;
            profile.Scenario.VolumeSpawns=VolumeSpawnPattern.DistributedRooms;
            profile.AgentCountTiers=new Vector3Int(128,1024,2048);
            var map=VolumeBake.Bake(profile.Volume,profile.Scenario.VolumeClearanceRadius,proxies.ToArray());
            string bytes=Folder+"/ReefNavigation.bytes";
            // Unity may memory-map a loaded TextAsset on Windows. Release that view
            // and replace a complete file instead of truncating the live baked asset.
            var previousBytes=AssetDatabase.LoadAssetAtPath<TextAsset>(bytes);
            if(previousBytes!=null) Resources.UnloadAsset(previousBytes);
            string temporary=bytes+".tmp";
            File.WriteAllBytes(temporary,VolumeBake.Encode(map));
            if(File.Exists(bytes)) File.Replace(temporary,bytes,null); else File.Move(temporary,bytes);
            AssetDatabase.ImportAsset(bytes,ImportAssetOptions.ForceSynchronousImport);
            string volumePath=Folder+"/ReefVolume.asset";
            var volume=AssetDatabase.LoadAssetAtPath<BakedNavigationVolume>(volumePath);
            if(volume==null) { volume=ScriptableObject.CreateInstance<BakedNavigationVolume>(); AssetDatabase.CreateAsset(volume,volumePath); }
            volume.SetData(AssetDatabase.LoadAssetAtPath<TextAsset>(bytes),map); profile.BakedVolume=volume;
            EditorUtility.SetDirty(volume); EditorUtility.SetDirty(profile); return profile;
        }

        private static void Rock(Transform root,string name,Vector3 center,Vector3 size,Material material,int seed)
        {
            const int rings=18,sides=28;
            var vertices=new List<Vector3>(); var triangles=new List<int>();
            for(int j=0;j<=rings;j++) for(int k=0;k<=sides;k++)
            {
                float latitude=j*Mathf.PI/rings,longitude=k*2*Mathf.PI/sides;
                var p=new Vector3(Mathf.Sin(latitude)*Mathf.Cos(longitude),Mathf.Cos(latitude),Mathf.Sin(latitude)*Mathf.Sin(longitude));
                // 低频形变保持闭合和块体体积，固定 seed 确保重建可复现。
                float noise=Mathf.PerlinNoise(p.x*2.7f+seed,p.y*2.7f+p.z*1.2f+seed*0.31f);
                float radius=0.84f+0.16f*noise;
                p=new Vector3(SignedPower(p.x),SignedPower(p.y),SignedPower(p.z))*radius;
                vertices.Add(Vector3.Scale(p,size)*0.5f);
            }
            for(int j=0;j<rings;j++) for(int k=0;k<sides;k++)
            {
                int a=j*(sides+1)+k,b=a+sides+1;
                triangles.Add(a); triangles.Add(a+1); triangles.Add(b);
                triangles.Add(a+1); triangles.Add(b+1); triangles.Add(b);
            }
            string path=Folder+"/"+name+".asset";
            var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(mesh==null) { mesh=new Mesh(); AssetDatabase.CreateAsset(mesh,path); } else mesh.Clear();
            mesh.name=name; mesh.SetVertices(vertices); mesh.SetTriangles(triangles,0); mesh.RecalculateNormals(); mesh.RecalculateBounds(); EditorUtility.SetDirty(mesh);
            var go=new GameObject(name); go.transform.SetParent(root); go.transform.position=center;
            go.AddComponent<MeshFilter>().sharedMesh=mesh; var renderer=go.AddComponent<MeshRenderer>(); renderer.sharedMaterial=material;
            proxies.Add(new VolumeBox(center-size*0.5f,center+size*0.5f));
        }
        private static float SignedPower(float value) => Mathf.Sign(value)*Mathf.Pow(Mathf.Abs(value),0.72f);
    }
}
