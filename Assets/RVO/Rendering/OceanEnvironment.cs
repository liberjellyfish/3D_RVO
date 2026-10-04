using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Rvo.Rendering
{
    public enum CausticQuality { Off, Shared256, Shared512, DirectReference }

    /// <summary>单个基础相机的环境表现。共享纹理和 RG 合成均不读取或修改仿真。</summary>
    [RequireComponent(typeof(Camera)), DisallowMultipleComponent]
    public sealed class OceanEnvironment : MonoBehaviour
    {
        public ComputeShader CausticCompute;
        public Shader FogShader, SurfaceShader;
        public Vector3 Center, Size = new Vector3(360, 180, 360);
        public Color WaterColor = new Color(0.015f, 0.07f, 0.11f);
        public Vector3 Extinction = new Vector3(0.004f, 0.002f, 0.0015f);
        public Material BackgroundMaterial;
        public CausticQuality Quality = CausticQuality.Shared256;
        [Range(1,60)] public int UpdateHz = 30;
        [Range(0,4)] public float CausticStrength = 1.5f;
        public float WorldScale = 0.035f;
        public bool Fog = true, Background = true, PauseEnvironment, ShortLoop;
        public bool FixedClock;
        public double EnvironmentSeconds;
        public string LastError { get; private set; }
        public long TextureBytes => a == null ? 0 : a.width * (long)a.height * 2 * 2 * 4 / 3;
        public RenderTexture CausticTexture => a;
        private Camera view;
        private RenderTexture a, b;
        private Material fogMaterial, surfaceMaterial;
        private GameObject background;
        private FogPass pass;
        private long generatedStep = long.MinValue;
        private bool generatedLoop;
        private int generatedHz;
        private float blend;
        private void OnEnable()
        {
            view = GetComponent<Camera>();
            RenderPipelineManager.beginCameraRendering += BeginCamera;
            RenderPipelineManager.endCameraRendering += EndCamera;
        }
        private void Update()
        {
            if (!FixedClock && !PauseEnvironment) EnvironmentSeconds += Time.unscaledDeltaTime;
        }
        public void Prepare()
        {
            if (FogShader == null || SurfaceShader == null) { LastError = "Assign Ocean fog and surface shaders."; return; }
            if (fogMaterial == null)
            {
                fogMaterial = new Material(FogShader) { name = "Ocean fog (owned)" };
                surfaceMaterial = BackgroundMaterial != null ? new Material(BackgroundMaterial) : new Material(SurfaceShader);
                surfaceMaterial.name = "Ocean inward background (owned)";
                surfaceMaterial.SetFloat("_Cull", (float)CullMode.Front);
                if (BackgroundMaterial == null)
                {
                    surfaceMaterial.SetFloat("_PatternSource",0);
                    surfaceMaterial.SetFloat("_PatternMapping",1);
                    surfaceMaterial.SetFloat("_PatternScale",WorldScale);
                }
                surfaceMaterial.renderQueue = 2490;
                pass = new FogPass(fogMaterial);
                background = GameObject.CreatePrimitive(PrimitiveType.Cube); background.name = "Ocean inward background";
                background.transform.SetParent(transform, false);
                Destroy(background.GetComponent<Collider>());
                var renderer = background.GetComponent<MeshRenderer>(); renderer.sharedMaterial = surfaceMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = true;
            }
            background.SetActive(Background);
            // 体积保持世界轴对齐；相机移动不带动水箱，也不改变水内光程。
            background.transform.SetPositionAndRotation(Center, Quaternion.identity); background.transform.localScale = Size;
            if (Quality == CausticQuality.Off || Quality == CausticQuality.DirectReference) { ReleaseTextures(); LastError = null; return; }
            if (CausticCompute == null || !SystemInfo.IsFormatSupported(GraphicsFormat.R16_SFloat, GraphicsFormatUsage.LoadStore))
            { LastError = "Shared caustics require compute and R16F UAV support; select Off or DirectReference."; ReleaseTextures(); return; }
            int resolution = Quality == CausticQuality.Shared512 ? 512 : 256;
            if (a == null || a.width != resolution)
            {
                ReleaseTextures(); a = CreateTexture(resolution); b = CreateTexture(resolution);
            }
            int hz = Mathf.Clamp(UpdateHz,1,60);
            double seconds = Math.Max(0,EnvironmentSeconds);
            long step = (long)Math.Floor(seconds * hz);
            blend = (float)(seconds * hz - step);
            if (step != generatedStep || generatedLoop != ShortLoop || generatedHz != hz)
            {
                if (step == generatedStep + 1 && generatedLoop == ShortLoop && generatedHz == hz)
                { var swap = a; a = b; b = swap; }
                else Generate(a, step / (double)hz);
                Generate(b, (step+1) / (double)hz);
                generatedStep = step; generatedLoop = ShortLoop; generatedHz = hz;
            }
            LastError = null;
        }
        private static RenderTexture CreateTexture(int resolution)
        {
            var texture = new RenderTexture(resolution,resolution,0,GraphicsFormat.R16_SFloat)
            { name = "Shared caustic", enableRandomWrite = true, useMipMap = true, autoGenerateMips = false,
                wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear };
            texture.Create(); return texture;
        }
        private void Generate(RenderTexture target, double seconds)
        {
            var command = CommandBufferPool.Get("RVO Ocean Caustic Generate");
            int kernel = CausticCompute.FindKernel("Generate");
            command.BeginSample("RVO Ocean Caustic Generate");
            command.SetComputeTextureParam(CausticCompute,kernel,"_Result",target);
            command.SetComputeIntParam(CausticCompute,"_Resolution",target.width);
            command.SetComputeIntParam(CausticCompute,"_ReferenceForm",0);
            command.SetComputeIntParam(CausticCompute,"_ShortLoop",ShortLoop ? 1 : 0);
            command.SetComputeIntParam(CausticCompute,"_RepairSeam",1);
            command.SetComputeFloatParam(CausticCompute,"_Seconds",(float)(seconds % (ShortLoop ? 12 : 480 * Math.PI)));
            command.DispatchCompute(CausticCompute,kernel,target.width/8,target.height/8,1);
            command.GenerateMips(target); command.EndSample("RVO Ocean Caustic Generate");
            Graphics.ExecuteCommandBuffer(command); CommandBufferPool.Release(command);
        }
        private void BeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera != view) return;
            var data = camera.GetUniversalAdditionalCameraData();
            if (data.renderType != CameraRenderType.Base || data.cameraStack.Count > 0 || camera.stereoEnabled)
            { LastError = "Ocean currently supports one non-XR base camera without stacking."; return; }
            Prepare(); if (fogMaterial == null) return;
            Shader.SetGlobalVector("_OceanMin",Center-Size*0.5f); Shader.SetGlobalVector("_OceanMax",Center+Size*0.5f);
            Shader.SetGlobalVector("_OceanExtinction",Vector3.Max(Extinction,Vector3.zero)); Shader.SetGlobalColor("_OceanWaterColor",WaterColor);
            Shader.SetGlobalFloat("_OceanEnabled",1);
            Shader.SetGlobalFloat("_OceanDirect",Quality == CausticQuality.DirectReference ? 1 : 0);
            Shader.SetGlobalFloat("_OceanShortLoop",ShortLoop ? 1 : 0);
            Shader.SetGlobalVector("_OceanCaustic",new Vector4(WorldScale,Quality == CausticQuality.Off || LastError != null ? 0 : CausticStrength,blend,(float)(EnvironmentSeconds % (ShortLoop ? 12 : 480 * Math.PI))));
            Shader.SetGlobalTexture("_OceanCausticA",a != null ? a : Texture2D.blackTexture);
            Shader.SetGlobalTexture("_OceanCausticB",b != null ? b : Texture2D.blackTexture);
            if (Fog) data.scriptableRenderer.EnqueuePass(pass);
        }
        private void EndCamera(ScriptableRenderContext context, Camera camera)
        { if (camera == view) Shader.SetGlobalFloat("_OceanEnabled",0); }
        private void ReleaseTextures()
        {
            if (a != null) { a.Release(); Destroy(a); a = null; }
            if (b != null) { b.Release(); Destroy(b); b = null; }
            generatedStep = long.MinValue;
        }
        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= BeginCamera; RenderPipelineManager.endCameraRendering -= EndCamera;
            ReleaseTextures(); Destroy(fogMaterial); Destroy(surfaceMaterial); Destroy(background);
            fogMaterial = null; surfaceMaterial = null; background = null; pass = null;
            Shader.SetGlobalFloat("_OceanEnabled",0);
        }

        // 相机本地注入，避免修改 Phase 1–3 的共享 RendererAsset。URP17 官方样例支持直接 EnqueuePass。
        private sealed class FogPass : ScriptableRenderPass
        {
            private readonly Material material;
            private sealed class PassData { public TextureHandle Color, Depth; public Material Material; }
            public FogPass(Material material)
            {
                this.material = material; renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
                requiresIntermediateTexture = true; ConfigureInput(ScriptableRenderPassInput.Depth);
            }
            public override void RecordRenderGraph(RenderGraph graph, ContextContainer context)
            {
                var resources = context.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer) return;
                var source = resources.activeColorTexture;
                var desc = graph.GetTextureDesc(source); desc.name = "Ocean fog color"; desc.clearBuffer = false; desc.depthBufferBits = DepthBits.None;
                var destination = graph.CreateTexture(desc);
                using (var builder = graph.AddRasterRenderPass<PassData>("RVO Ocean Beer Fog",out var data))
                {
                    data.Color = source; data.Depth = resources.cameraDepthTexture; data.Material = material;
                    builder.UseTexture(data.Color,AccessFlags.Read); builder.UseTexture(data.Depth,AccessFlags.Read);
                    builder.SetRenderAttachment(destination,0,AccessFlags.Write);
                    builder.SetRenderFunc((PassData d, RasterGraphContext c) =>
                        Blitter.BlitTexture(c.cmd,d.Color,new Vector4(1,1,0,0),d.Material,0));
                }
                // 独立目标后交换句柄；禁止把 cameraColor 同时作为采样输入和写入附件。
                resources.cameraColor = destination;
            }
        }
    }
}
