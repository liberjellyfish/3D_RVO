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
    /// <summary>先创作礁石网格，再从其世界包围盒离线烘焙保守代理；运行时不生成导航数据。</summary>
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
                // 每块礁石是独立闭合体，包围盒可保守阻挡；拱洞由块体之间的自由空间组成。
                Rock(terrain,"Seabed",new Vector3(0,-49,0),new Vector3(150,62,150),sand,3);
                Rock(terrain,"Arch left",new Vector3(0,-5,-20),new Vector3(22,42,23),stone,11);
                Rock(terrain,"Arch right",new Vector3(2,-5,21),new Vector3(25,42,24),stone,17);
                // Shorten the crown in X: its old edge and the western needle left a
                // sub-voxel free slit. A moving fish could enter it but fail to anchor
                // a new path. The wider side buffer now contains navigable cell centers.
                Rock(terrain,"Arch crown",new Vector3(1,15,0),new Vector3(22,15,62),stone,23);
                Rock(terrain,"Reef west",new Vector3(-29,-10,31),new Vector3(32,33,25),stone,29);
                Rock(terrain,"Reef east",new Vector3(33,-10,-25),new Vector3(33,31,27),stone,31);
                Rock(terrain,"Distant crest",new Vector3(41,-5,30),new Vector3(24,45,23),stone,37);
                Rock(terrain,"Foreground shelf",new Vector3(-38,-13,-28),new Vector3(26,22,20),stone,41);
                // Staggered corridors inside the navigable 64m cube; leave the end spawn bands open.
                var paleStone = Material("Pale coral limestone",new Color(0.42f,0.40f,0.30f),0.18f);
                Rock(terrain,"West needle",new Vector3(-17,-4,-9),new Vector3(7,33,8),stone,43);
                Rock(terrain,"West low ridge",new Vector3(-17,-13,8),new Vector3(9,16,13),paleStone,47);
                Rock(terrain,"West outer spur",new Vector3(-15,-10,25),new Vector3(8,24,8),stone,53);
                Rock(terrain,"East needle",new Vector3(18,-1,8),new Vector3(7,39,8),paleStone,59);
                Rock(terrain,"East low ridge",new Vector3(17,-12,-10),new Vector3(9,18,12),stone,61);
                Rock(terrain,"East outer spur",new Vector3(16,-8,-27),new Vector3(8,29,7),paleStone,67);
                // A lower side arch offers an underpass and a route above its crown.
                Rock(terrain,"Side arch south",new Vector3(15,-12,21),new Vector3(7,20,5),stone,71);
                Rock(terrain,"Side arch north",new Vector3(15,-12,31),new Vector3(7,20,5),stone,73);
                Rock(terrain,"Side arch crown",new Vector3(15,-2,26),new Vector3(8,5,15),paleStone,79);
                Rock(terrain,"Central stepping reef",new Vector3(1,-17,0),new Vector3(9,10,7),paleStone,83);
                Rock(terrain,"Western seabed rubble",new Vector3(-10,-20,-1),new Vector3(6,6,9),stone,89);
                Rock(terrain,"Eastern seabed rubble",new Vector3(10,-20,15),new Vector3(7,7,6),paleStone,97);
                BuildRouteReefs(terrain,stone,paleStone);
                if (proxies.Count != 48) throw new InvalidOperationException("Reef route layout must contain 48 proxies.");
                var profile = BakeProfile();
                var root = new GameObject("Live reef navigation");
                var source = root.AddComponent<VolumeSimulationBootstrap>(); source.Profile=profile; source.AgentTier=2; source.ShowHud=false;
                var fish = root.AddComponent<GpuFishRenderer>(); fish.ReefAppearance=true; fish.EnableMotionVectors=true;
                fish.LodPixels=new Vector3(60,22,7); fish.LodHysteresis=0.2f;
                fish.FishShader=Shader.Find("RVO/Procedural Fish Indirect");
                fish.CullingShader=AssetDatabase.LoadAssetAtPath<ComputeShader>(Phase4DemoBuilder.ComputePath);
                var bridge=root.AddComponent<FishLiveBridge>(); bridge.Source=source; bridge.Renderer=fish;
                root.AddComponent<FishBenchmarkRecorder>().Live=bridge;
                var camera=new GameObject("Reef camera").AddComponent<Camera>(); camera.tag="MainCamera";
                camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(0.015f,0.07f,0.11f);
                camera.nearClipPlane=0.1f; camera.farClipPlane=420; camera.allowHDR=true; camera.allowMSAA=false; camera.fieldOfView=55;
                camera.transform.position=new Vector3(-42,3,-29); camera.transform.LookAt(new Vector3(0,1,0));
                camera.gameObject.AddComponent<VolumeCameraControls>().Pivot=new Vector3(0,1,0); fish.ViewCamera=camera;
                var water=camera.gameObject.AddComponent<OceanEnvironment>();
                water.Size=new Vector3(420,240,420); water.WaterSurfaceHeight=65; water.HorizonDistance=250;
                water.Extinction=WaterOptics.Calibrate(new Vector3(0.55f,0.72f,0.82f),100);
                // Hoskins/joltz0r turbulence feeds the lit receivers, shared once across all fish/rocks.
                water.Quality=CausticQuality.Shared512; water.CausticStrength=3.2f; water.WorldScale=0.085f;
                water.FogShader=Shader.Find("RVO/Ocean Beer Fog");
                water.CausticCompute=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/RVO/Rendering/Shaders/CausticGenerate.compute");
                camera.gameObject.AddComponent<OceanDemoControls>().Fish=fish;
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

        private static void BuildRouteReefs(Transform terrain, Material stone, Material paleStone)
        {
            // 16 blocks: paired elevated windows on each side of the central crown.
            // Opposite windows have different sill heights, so the two upper branches
            // rise/fall between x=-16 and x=16. End spawn bands (|x|>=19.2) stay untouched.
            for (int side=0;side<2;side++) for (int branch=0;branch<2;branch++)
            {
                float x=side==0 ? -16 : 16, z=branch==0 ? -21 : 21;
                float sill=(side==branch) ? 17 : 12;
                string name=$"Upper route {side}-{branch}";
                int seed=101+side*37+branch*13;
                // Root the piers below the seabed; elevated windows must not become
                // floating rocks. Their full mesh bounds also enter the navigation bake.
                Rock(terrain,name+" south pier",new Vector3(x,2,z-6),new Vector3(4,56,4),stone,seed);
                Rock(terrain,name+" north pier",new Vector3(x,2,z+6),new Vector3(4,56,4),stone,seed+2);
                Rock(terrain,name+" crown",new Vector3(x,29,z),new Vector3(4,4,16),paleStone,seed+4);
                Rock(terrain,name+" sill",new Vector3(x,sill,z),new Vector3(4,4,11),paleStone,seed+6);
            }
            // Eight ribs frame the lower parallel lanes. Short x thicknesses leave
            // broad chambers between the windows and the original outer needles.
            float[] columns={-12,-5,5,12};
            for(int i=0;i<columns.Length;i++) for(int side=0;side<2;side++)
                Rock(terrain,$"Lower lane rib {i}-{side}",new Vector3(columns[i],-5,side==0 ? -6 : 6),
                    new Vector3(2,12,2),stone,211+i*11+side*3);
            // Four staggered fins make an S-shaped alternative below the main arch.
            // They stop below y=0: fish can change depth instead of entering a long tube.
            for(int i=0;i<columns.Length;i++)
                Rock(terrain,$"Staggered lower fin {i}",new Vector3(columns[i],-7,i%2==0 ? -2 : 2),
                    new Vector3(2,10,4.5f),paleStone,271+i*7);
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
            Phase3DemoBuilder.Configure(profile); profile.Volume.Resolution=128; profile.Volume.CellSize=0.5f;
            profile.Scenario.Radius=0.65f; profile.Scenario.MaxSpeed=5; profile.Scenario.Extent=64;
            profile.Scenario.VolumeSizeVariation=0.3f; profile.Scenario.VolumeSpeedVariation=0.25f;
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
            var bounds=renderer.bounds;
            proxies.Add(new VolumeBox(bounds.min,bounds.max));
        }
        private static float SignedPower(float value) => Mathf.Sign(value)*Mathf.Pow(Mathf.Abs(value),0.72f);
    }
}
