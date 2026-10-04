using System.Collections;
using System.IO;
using NUnit.Framework;
using Rvo.Rendering;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Rvo.Tests
{
    public sealed class Phase4OceanTests
    {
        private static string EvidenceFolder => "Documentation/RVO/Verification/OceanP0/Regression/" + SystemInfo.graphicsDeviceType;
        private static ComputeShader Compute(string name)
        {
            #if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/RVO/Rendering/Shaders/"+name);
            #else
            Assert.Ignore("Editor asset fixture."); return null;
            #endif
        }
        private static Color[] Read(RenderTexture target, string evidence = null)
        {
            var old = RenderTexture.active;
            var texture = new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false,true);
            try
            {
                RenderTexture.active=target; texture.ReadPixels(new Rect(0,0,target.width,target.height),0,0); texture.Apply();
                if (evidence != null)
                {
                    string folder=EvidenceFolder;
                    Directory.CreateDirectory(folder);
                    File.WriteAllBytes(Path.Combine(folder,evidence),texture.EncodeToPNG());
                }
                return texture.GetPixels();
            }
            finally { RenderTexture.active=old; Object.Destroy(texture); }
        }
        [UnityTest]
        public IEnumerator CausticFloat32ReferenceStableAndLoopAreFinite()
        {
            var shader=Compute("CausticGenerate.compute");
            var target=new RenderTexture(128,128,0,RenderTextureFormat.RFloat) { enableRandomWrite=true }; target.Create();
            try
            {
                shader.SetTexture(0,"_Result",target); shader.SetInt("_Resolution",128); shader.SetInt("_RepairSeam",0);
                shader.SetInt("_ShortLoop",0); shader.SetFloat("_Seconds",7.25f);
                shader.SetInt("_ReferenceForm",1); shader.Dispatch(0,16,16,1); var reference=Read(target);
                shader.SetInt("_ReferenceForm",0); shader.Dispatch(0,16,16,1); var stable=Read(target,"caustic.png");
                float error=0;
                for(int i=0;i<stable.Length;i++)
                {
                    Assert.That(float.IsNaN(stable[i].r)||float.IsInfinity(stable[i].r),Is.False);
                    error=Mathf.Max(error,Mathf.Abs(reference[i].r-stable[i].r));
                }
                Assert.That(error,Is.LessThan(0.003f),"float32 reference versus stable algebra");
                shader.SetInt("_ShortLoop",1); shader.SetFloat("_Seconds",0); shader.Dispatch(0,16,16,1); var first=Read(target);
                shader.SetFloat("_Seconds",12); shader.Dispatch(0,16,16,1); var last=Read(target);
                float loopError=0;
                for(int i=0;i<first.Length;i++) loopError=Mathf.Max(loopError,Mathf.Abs(first[i].r-last[i].r));
                Assert.That(loopError,Is.LessThan(0.03f),"harmonic 12-second variant only");
                Directory.CreateDirectory(EvidenceFolder);
                File.WriteAllText(Path.Combine(EvidenceFolder,"caustic-error.txt"),$"referenceMax={error:R}\nloopMax={loopError:R}\n");
            }
            finally { target.Release(); Object.Destroy(target); }
            yield return null; LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator AnimationCardsDepthAndOceanRenderOnDevice()
        {
            var root=new GameObject("Ocean test");
            var camera=root.AddComponent<Camera>(); camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=Color.black;
            camera.transform.position=new Vector3(0,0,-8); camera.farClipPlane=100;
            var renderer=root.AddComponent<GpuFishRenderer>(); renderer.ViewCamera=camera; renderer.AutoRender=false;
            renderer.CullingShader=Compute("FishCulling.compute"); renderer.FishShader=Shader.Find("RVO/Procedural Fish Indirect");
            renderer.DetailedNearMesh=true; renderer.ForceLod=0;
            var target=new RenderTexture(256,256,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear); target.Create(); camera.targetTexture=target;
            var ocean=root.AddComponent<OceanEnvironment>(); ocean.FogShader=Shader.Find("RVO/Ocean Beer Fog"); ocean.SurfaceShader=Shader.Find("RVO/Ocean Surface");
            ocean.CausticCompute=Compute("CausticGenerate.compute"); ocean.enabled=false;
            using var storage=new AgentStorage(1); var agent=storage.Initialization;
            agent.Ids[0]=1; agent.Positions[0]=float3.zero; agent.Velocities[0]=new float3(2,0,0); agent.Goals[0]=new float3(10,0,0);
            agent.Parameters[0]=new AgentParameters { Radius=1,MaxSpeed=2,ArrivalDistance=0.1f };
            renderer.Capture(new AgentSnapshot(storage.Read,0,1,1f/30));
            GameObject wall=null; Material wallMaterial=null;
            try
            {
                Color[] reference=null;
                foreach(FishAnimationMode mode in System.Enum.GetValues(typeof(FishAnimationMode)))
                {
                    renderer.NearAnimation=mode; yield return null;
                    renderer.Render(); RenderPipeline.SubmitRenderRequest(camera,new RenderPipeline.StandardRequest { destination=target });
                    var pixels=Read(target,"fish-"+mode+".png");
                    int lit=0; double error=0;
                    for(int i=0;i<pixels.Length;i++) { if(pixels[i].maxColorComponent>0.03f)lit++; if(reference!=null) error+=Mathf.Abs(pixels[i].r-reference[i].r); }
                    Assert.That(lit,Is.GreaterThan(30));
                    if(reference==null)reference=pixels; else Assert.That(error/pixels.Length,Is.LessThan(0.01));
                }
                renderer.NearAnimation=FishAnimationMode.Procedural; renderer.EnableFarCards=true;
                foreach(FishFarMode mode in new[] { FishFarMode.CrossQuads,FishFarMode.CrossTriangles,FishFarMode.Billboard })
                {
                    renderer.FarRepresentation=mode; renderer.ForceLod=2; yield return null;
                    renderer.Render(); RenderPipeline.SubmitRenderRequest(camera,new RenderPipeline.StandardRequest { destination=target });
                    var pixels=Read(target,"fish-"+mode+".png"); int lit=0;
                    foreach(var p in pixels)if(p.maxColorComponent>0.03f)lit++;
                    Assert.That(lit,Is.GreaterThan(20),mode.ToString());
                }
                renderer.FarRepresentation=FishFarMode.CrossQuads;
                foreach(float axial in new[] { 1f,0.84f,0.75f })
                {
                    camera.transform.position=new Vector3(axial,0,-Mathf.Sqrt(1-axial*axial))*8;
                    camera.transform.LookAt(Vector3.zero); yield return null; renderer.Render();
                    var args=new GraphicsBuffer.IndirectDrawIndexedArgs[1];
                    renderer.Arguments(axial > 0.8f ? 1 : 2).GetData(args);
                    Assert.That(args[0].instanceCount,Is.EqualTo(1),"axial fallback hysteresis");
                }
                camera.transform.SetPositionAndRotation(new Vector3(0,0,-8),Quaternion.identity);
                // 用前方黑色不透明墙验证 LEqual，而不是仅检查 Shader 声明。
                wall=GameObject.CreatePrimitive(PrimitiveType.Cube); wall.transform.position=new Vector3(0,0,-3); wall.transform.localScale=new Vector3(8,8,0.2f);
                wallMaterial=new Material(Shader.Find("Universal Render Pipeline/Unlit")); wallMaterial.SetColor("_BaseColor",Color.black);
                wall.GetComponent<Renderer>().sharedMaterial=wallMaterial;
                renderer.ForceLod=0; yield return null;
                renderer.Render(); RenderPipeline.SubmitRenderRequest(camera,new RenderPipeline.StandardRequest { destination=target });
                var occluded=Read(target); Assert.That(occluded[128*256+128].maxColorComponent,Is.LessThan(0.005f));
                wall.SetActive(false); ocean.enabled=true; ocean.FixedClock=true; ocean.Size=new Vector3(40,30,40);
                ocean.Extinction=new Vector3(0.12f,0.07f,0.04f);
                yield return null;
                renderer.Render(); RenderPipeline.SubmitRenderRequest(camera,new RenderPipeline.StandardRequest { destination=target });
                var fogged=Read(target,"ocean.png"); Assert.That(ocean.LastError,Is.Null);
                Assert.That(ocean.CausticTexture,Is.Not.Null);
                Assert.That(fogged[128*256+128].b,Is.GreaterThan(0.01f));
                // 黑色接收面隔离 Beer 距离；相机在水外时只能计算线段在箱体中的长度。
                wall.SetActive(true); wall.transform.position=new Vector3(0,0,-1);
                ocean.Background=false; ocean.Quality=CausticQuality.Off; ocean.Size=Vector3.one*4;
                ocean.Extinction=Vector3.one*0.3f; ocean.WaterColor=new Color(0.2f,0.4f,0.6f);
                foreach(float cameraZ in new[] { -8f,-1.5f })
                {
                    camera.transform.position=new Vector3(0,0,cameraZ); yield return null;
                    renderer.Render(); RenderPipeline.SubmitRenderRequest(camera,new RenderPipeline.StandardRequest { destination=target });
                    var pixels=Read(target); float distance=cameraZ < -2 ? 0.9f : 0.4f;
                    float expected=0.6f*(1-Mathf.Exp(-0.3f*distance));
                    Assert.That(pixels[128*256+128].b,Is.EqualTo(expected).Within(0.015f),$"water-only Euclidean segment; pixel={pixels[128*256+128]} min={Shader.GetGlobalVector("_OceanMin")} max={Shader.GetGlobalVector("_OceanMax")}");
                }
                ocean.Center=Vector3.right*100; yield return null;
                renderer.Render(); RenderPipeline.SubmitRenderRequest(camera,new RenderPipeline.StandardRequest { destination=target });
                Assert.That(Read(target)[128*256+128].b,Is.LessThan(0.005f),"ray missing water must not be fogged");
                ocean.Quality=CausticQuality.Shared256;
                for(int i=0;i<100;i++) { ocean.enabled=false; yield return null; ocean.enabled=true; ocean.Prepare(); }
            }
            finally { camera.targetTexture=null; target.Release(); Object.Destroy(target); Object.Destroy(wall); Object.Destroy(wallMaterial); Object.Destroy(root); }
            yield return null; LogAssert.NoUnexpectedReceived();
        }
    }
}
