using System.Collections;
using System.IO;
using NUnit.Framework;
using Rvo.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Rvo.Tests
{
    public sealed class Phase4SurfaceTests
    {
        private static string EvidenceFolder => "Documentation/RVO/Verification/OceanP0/SurfaceStudy/" + SystemInfo.graphicsDeviceType;
        [System.Serializable] private sealed class Metrics
        {
            public string api;
            public float contrast_p90_p10;
            public double time_mae, shared_direct_mae;
        }
        private static Color[] Capture(Camera camera, RenderTexture target, string name)
        {
            RenderPipeline.SubmitRenderRequest(camera,new RenderPipeline.StandardRequest { destination=target });
            var previous=RenderTexture.active;
            var texture=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
            try
            {
                RenderTexture.active=target; texture.ReadPixels(new Rect(0,0,target.width,target.height),0,0); texture.Apply();
                string folder=EvidenceFolder;
                Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder,name+".png"),texture.EncodeToPNG());
                return texture.GetPixels();
            }
            finally { RenderTexture.active=previous; Object.Destroy(texture); }
        }

        [UnityTest]
        public IEnumerator SurfacePatternIsVisibleOnWallsCurvesAndWithoutFog()
        {
            #if UNITY_EDITOR
            const string path="Assets/RVO/Demo/Phase4_SurfaceStudy.unity";
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(path,new LoadSceneParameters(LoadSceneMode.Additive));
            #else
            Assert.Ignore("Requires the surface study scene asset."); yield break;
            #endif
            var scene=SceneManager.GetSceneByName("Phase4_SurfaceStudy");
            Camera camera=null;
            foreach(var root in scene.GetRootGameObjects()) if(root.TryGetComponent<Camera>(out var candidate))camera=candidate;
            Assert.That(camera,Is.Not.Null);
            var water=camera.GetComponent<OceanEnvironment>(); camera.enabled=false;
            var target=new RenderTexture(768,768,24); camera.targetTexture=target; target.Create();
            var material=new Material(water.BackgroundMaterial);
            water.BackgroundMaterial=material;
            foreach(var root in scene.GetRootGameObjects())
                if(root.TryGetComponent<MeshRenderer>(out var renderer))renderer.sharedMaterial=material;
            try
            {
                water.FixedClock=true; water.EnvironmentSeconds=4; water.Fog=false;
                water.enabled=false; yield return null; water.enabled=true;
                var first=Capture(camera,target,"shared-gray");
                var luminance=new float[first.Length];
                for(int i=0;i<first.Length;i++)luminance[i]=first[i].grayscale;
                System.Array.Sort(luminance);
                float contrast=luminance[luminance.Length*9/10]-luminance[luminance.Length/10];
                Assert.That(contrast,Is.GreaterThan(0.12f),"Surface must show a visible pattern without fog.");
                water.EnvironmentSeconds=5.5;
                var animated=Capture(camera,target,"shared-gray-later");
                double movement=0;
                for(int i=0;i<first.Length;i++)movement+=Mathf.Abs(first[i].grayscale-animated[i].grayscale);
                Assert.That(movement/first.Length,Is.GreaterThan(0.005),"Pattern must animate on the surfaces.");
                water.EnvironmentSeconds=4;
                material.SetFloat("_PatternSource",1);
                water.enabled=false; yield return null; water.enabled=true;
                var direct=Capture(camera,target,"direct-gray");
                double difference=0;
                for(int i=0;i<first.Length;i++)difference+=Mathf.Abs(first[i].grayscale-direct[i].grayscale);
                Assert.That(difference/first.Length,Is.LessThan(0.08),"Shared and direct surface patterns should remain visually comparable.");
                material.SetFloat("_PatternSource",0); material.SetColor("_BaseColor",new Color(0,0.35f,0.5f));
                water.enabled=false; yield return null; water.enabled=true;
                Capture(camera,target,"shared-ocean-color");
                camera.transform.LookAt(new Vector3(0,6,3)); Capture(camera,target,"ceiling");
                camera.transform.LookAt(new Vector3(-6,1,3)); Capture(camera,target,"side-wall");
                File.WriteAllText(Path.Combine(EvidenceFolder,"metrics.json"),
                    JsonUtility.ToJson(new Metrics { api=SystemInfo.graphicsDeviceType.ToString(),contrast_p90_p10=contrast,
                        time_mae=movement/first.Length,shared_direct_mae=difference/first.Length },true));
            }
            finally { camera.targetTexture=null; target.Release(); Object.Destroy(target); Object.Destroy(material); }
            yield return SceneManager.UnloadSceneAsync(scene);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
