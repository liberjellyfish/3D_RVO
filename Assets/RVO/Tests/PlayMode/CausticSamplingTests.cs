using System.Collections;
using System.IO;
using NUnit.Framework;
using Rvo.Rendering;
using UnityEngine;
using UnityEngine.TestTools;

namespace Rvo.Tests
{
    public sealed class CausticSamplingTests
    {
        private const int Size = 512;
        private static Color[] Render(Material material, RenderTexture target, Vector4 domain, string path = null)
        {
            material.SetVector("_ProbeDomain",domain);
            var previous=RenderTexture.active;
            Graphics.Blit(Texture2D.blackTexture,target,material);
            var image=new Texture2D(Size,Size,TextureFormat.RGBAFloat,false,true);
            try
            {
                RenderTexture.active=target; image.ReadPixels(new Rect(0,0,Size,Size),0,0); image.Apply();
                var pixels=image.GetPixels();
                foreach(var p in pixels) Assert.That(float.IsNaN(p.r)||float.IsInfinity(p.r)||p.r<0,Is.False);
                if(path!=null) File.WriteAllBytes(path,image.EncodeToPNG());
                return pixels;
            }
            finally { RenderTexture.active=previous; Object.DestroyImmediate(image); }
        }
        private static double Mean(Color[] pixels)
        { double sum=0; foreach(var p in pixels)sum+=p.r; return sum/pixels.Length; }
        private static double Deviation(Color[] pixels)
        { double mean=Mean(pixels),sum=0; foreach(var p in pixels)sum+=(p.r-mean)*(p.r-mean); return System.Math.Sqrt(sum/pixels.Length); }
        private static double Correlation(Color[] pixels)
        {
            double mean=Mean(pixels),cross=0,a2=0,b2=0;
            for(int y=0;y<Size;y++)for(int x=0;x<Size-64;x++)
            { double a=pixels[y*Size+x].r-mean,b=pixels[y*Size+x+64].r-mean; cross+=a*b; a2+=a*a; b2+=b*b; }
            return cross/System.Math.Sqrt(a2*b2);
        }
        [UnityTest]
        public IEnumerator WorldLockedSamplingPreservesEnergyAndRemovesTilePeriod()
        {
            #if UNITY_EDITOR
            var compute=UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/RVO/Rendering/Shaders/CausticGenerate.compute");
            #else
            Assert.Ignore("Editor GPU fixture."); yield break;
            #endif
            string folder="Documentation/RVO/Verification/ReefRoutes48/Caustics/"+SystemInfo.graphicsDeviceType;
            Directory.CreateDirectory(folder);
            using var field=new CausticField();
            var material=new Material(Shader.Find("Hidden/RVO/Caustic Sampling Probe"));
            var target=new RenderTexture(Size,Size,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear); target.Create();
            var previousBias=Shader.GetGlobalVector("_GlobalMipBias");
            try
            {
                // Graphics.Blit has no URP camera setup. Core.hlsl scales SampleGrad
                // by _GlobalMipBias.y, whose uninitialized value would force mip 0.
                Shader.SetGlobalVector("_GlobalMipBias",new Vector4(0,1,0,0));
                Shader.SetGlobalFloat("_OceanDirect",0);
                var report=new System.Text.StringBuilder("seconds,repeat_mean,stochastic_mean,repeat_std,stochastic_std,repeat_period_correlation,stochastic_period_correlation\n");
                foreach(double seconds in new[] { 0.0,4.0,7.25 })
                {
                    field.Prepare(compute,CausticQuality.Shared512,30,seconds,false);
                    Assert.That(field.Current.mipmapCount,Is.EqualTo(10),"Full filtering chain is required");
                    Shader.SetGlobalTexture("_OceanCausticA",field.Current); Shader.SetGlobalTexture("_OceanCausticB",field.Next);
                    Shader.SetGlobalVector("_OceanCaustic",new Vector4(0.085f,1,field.Blend,(float)seconds));
                    var domain=new Vector4(-4,-4,8,8);
                    Shader.SetGlobalFloat("_OceanStochastic",0); var repeat=Render(material,target,domain,folder+$"/repeat-{seconds:F2}.png");
                    Shader.SetGlobalFloat("_OceanStochastic",1); var random=Render(material,target,domain,folder+$"/plane-{seconds:F2}.png");
                    Assert.That(Mean(random)/Mean(repeat),Is.InRange(0.90,1.10),"Mean energy");
                    Assert.That(Deviation(random)/Deviation(repeat),Is.InRange(0.65,1.10),"Bright filament contrast");
                    Assert.That(Correlation(repeat),Is.GreaterThan(0.99));
                    Assert.That(System.Math.Abs(Correlation(random)),Is.LessThan(0.25),"Old 11.8m period");
                    report.AppendLine(System.FormattableString.Invariant($"{seconds},{Mean(repeat)},{Mean(random)},{Deviation(repeat)},{Deviation(random)},{Correlation(repeat)},{Correlation(random)}"));
                    // Spatial transformations cannot change between interpolation endpoints.
                    Shader.SetGlobalVector("_OceanCaustic",new Vector4(0.085f,1,0,(float)seconds)); var first=Render(material,target,domain);
                    Shader.SetGlobalVector("_OceanCaustic",new Vector4(0.085f,1,1,(float)seconds)); var last=Render(material,target,domain);
                    Shader.SetGlobalVector("_OceanCaustic",new Vector4(0.085f,1,0.5f,(float)seconds)); var middle=Render(material,target,domain);
                    float error=0;
                    for(int i=0;i<middle.Length;i++)error=Mathf.Max(error,Mathf.Abs(middle[i].r-(first[i].r+last[i].r)*0.5f));
                    Assert.That(error,Is.LessThan(0.0001f),"Time interpolation must commute with patch blending");
                    var far=Render(material,target,new Vector4(-256,-256,512,512),folder+"/far-mips.png");
                    Assert.That(Deviation(far),Is.LessThan(Deviation(random)*0.25),"Far mip stability");
                }
                File.WriteAllText(folder+"/metrics.csv",report.ToString());
                // Probe both sides of shared lattice edges and vertices (including negative cells).
                foreach(var point in new[] { new Vector2(0,0),new Vector2(1,0),new Vector2(-1,0),new Vector2(0.5f,0.8660254f),new Vector2(0.7f,0.5196152f) })
                {
                    var a=Render(material,target,new Vector4(point.x-0.00001f,point.y,0,0));
                    var b=Render(material,target,new Vector4(point.x+0.00001f,point.y,0,0));
                    Assert.That(Mathf.Abs(a[0].r-b[0].r),Is.LessThan(0.005f),"Triangle continuity at "+point);
                }
                Shader.SetGlobalFloat("_OceanStochastic",0); Shader.SetGlobalFloat("_OceanDirect",1);
                Shader.SetGlobalFloat("_OceanShortLoop",0);
                foreach(var point in new[] { new Vector2(0,0),new Vector2(0,0.37f),new Vector2(0.61f,0) })
                {
                    var a=Render(material,target,new Vector4(point.x-0.00001f,point.y-0.00001f,0,0));
                    var b=Render(material,target,new Vector4(point.x+0.00001f,point.y+0.00001f,0,0));
                    Assert.That(Mathf.Abs(a[0].r-b[0].r),Is.LessThan(0.005f),"Periodic texture boundary at "+point);
                }
            }
            finally { Shader.SetGlobalVector("_GlobalMipBias",previousBias); Shader.SetGlobalFloat("_OceanDirect",0); Shader.SetGlobalFloat("_OceanStochastic",0); target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(material); }
            yield return null; LogAssert.NoUnexpectedReceived();
        }
    }
}
