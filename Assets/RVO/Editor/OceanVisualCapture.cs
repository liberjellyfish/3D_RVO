using System;
using System.IO;
using Rvo.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rvo.Editor
{
    /// <summary>运行真实场景并保存画面；不调用测试框架或采集性能矩阵。</summary>
    [InitializeOnLoad]
    public static class OceanVisualCapture
    {
        private const string Key = "RVO.VisualCapture";
        private static double started;
        private static int captured;
        private static bool configuredShot;
        private static RenderTexture target;
        private static int renderedFrame = -1;
        static OceanVisualCapture() { EditorApplication.update += Update; }

        [MenuItem("Tools/RVO/Open Reef Presentation")]
        public static void OpenForReview()
        {
            if (EditorApplication.isPlaying) return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(OceanReefBuilder.ScenePath);
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            EditorApplication.EnterPlaymode();
        }

        public static void Begin()
        {
            string scene = Argument("-rvo-scene", Phase4DemoBuilder.OceanLiveScene);
            string output = Argument("-rvo-capture", "Documentation/RVO/Verification/OceanPresentation/P0");
            Directory.CreateDirectory(output);
            EditorSceneManager.OpenScene(scene);
            SessionState.SetString(Key, output);
            EditorApplication.EnterPlaymode();
        }

        private static string Argument(string name, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            return fallback;
        }

        private static void Update()
        {
            string output = SessionState.GetString(Key, "");
            if (output.Length == 0 || !EditorApplication.isPlaying || EditorApplication.isCompiling) return;
            // A hidden editor with no active Game view can stop scheduling player frames.
            // Captures must advance simulation/animation, not save the same frame three times.
            EditorApplication.QueuePlayerLoopUpdate();
            if (started == 0) started = EditorApplication.timeSinceStartup;
            try
            {
                var fish = UnityEngine.Object.FindFirstObjectByType<GpuFishRenderer>();
                var source = UnityEngine.Object.FindFirstObjectByType<VolumeSimulationBootstrap>();
                if (source != null && source.LastError != null) throw new InvalidOperationException(source.LastError);
                if (fish == null || (!configuredShot && fish.Poses.Count == 0)) return;
                if (!configuredShot)
                {
                    configuredShot=true;
                    if (int.TryParse(Argument("-rvo-shot","-1"),out int shot) && shot >= 0)
                        fish.ViewCamera.GetComponent<OceanPresentation>()?.SelectShot(shot);
                    fish.ViewCamera.GetComponent<OceanPresentation>()?.SetAA(Argument("-rvo-aa","SMAA"));
                    ConfigureStudy(fish,source);
                    Application.targetFrameRate=30;
                    target=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);
                    fish.ViewCamera.targetTexture=target; fish.ViewCamera.enabled=false;
                }
                // 持续出帧，使显示历史和 AA 真正运行；每隔四秒只做一次 PNG 读回。
                if (renderedFrame != Time.frameCount)
                {
                    fish.Render();
                    RenderPipeline.SubmitRenderRequest(fish.ViewCamera,new RenderPipeline.StandardRequest { destination=target });
                    renderedFrame=Time.frameCount;
                }
                if (EditorApplication.timeSinceStartup - started < 4 * (captured + 1)) return;
                Capture(Path.Combine(output, $"live-{captured:00}.png"));
                File.AppendAllText(Path.Combine(output, "capture.txt"),
                    $"{DateTime.UtcNow:O} frame={Time.frameCount} tick={source?.World?.Tick} agents={fish.Poses.Count} camera={fish.ViewCamera.transform.position} api={SystemInfo.graphicsDeviceType}\n");
                if (++captured < 3) return;
                fish.ViewCamera.targetTexture=null; target.Release(); UnityEngine.Object.DestroyImmediate(target);
                SessionState.EraseString(Key); EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e); SessionState.EraseString(Key); EditorApplication.Exit(1);
            }
        }

        private static void ConfigureStudy(GpuFishRenderer fish, VolumeSimulationBootstrap source)
        {
            string study=Argument("-rvo-study","");
            var water=fish.ViewCamera.GetComponent<OceanEnvironment>();
            water.StochasticCaustics=Argument("-rvo-repeat","0")!="1";
            if(Argument("-rvo-proxies","0")=="1")
            {
                fish.ViewCamera.GetComponent<OceanPresentation>().DebugProxies.gameObject.SetActive(true);
                GameObject.Find("Authored reef meshes")?.SetActive(false);
            }
            if(study.Length==0) return;
            source.Paused=true; fish.enabled=false;
            GameObject.Find("Authored reef meshes")?.SetActive(false);
            var plane=GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.name="Caustic inspection plane"; plane.transform.position=new Vector3(0,-10,0);
            plane.transform.localScale=Vector3.one*18;
            plane.GetComponent<Renderer>().sharedMaterial=AssetDatabase.LoadAssetAtPath<Material>(OceanReefBuilder.Folder+"/Sand.mat");
            var camera=fish.ViewCamera;
            Vector3 eye=study=="top" ? new Vector3(0,65,0) : study=="grazing" ? new Vector3(0,-6,-60) : new Vector3(0,3,-18);
            camera.transform.position=eye; camera.transform.LookAt(new Vector3(0,-10,0), study=="top" ? Vector3.forward : Vector3.up);
            water.EnvironmentSeconds=4;
        }

        private static void Capture(string path)
        {
            var previousActive = RenderTexture.active;
            var image = new Texture2D(1280, 720, TextureFormat.RGB24, false, false);
            try
            {
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); image.Apply();
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previousActive; UnityEngine.Object.DestroyImmediate(image);
            }
        }
    }
}
