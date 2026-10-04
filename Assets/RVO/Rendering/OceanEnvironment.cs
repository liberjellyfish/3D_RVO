using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Rvo.Rendering
{
    public enum OceanDebugView { Final, BeforeFog, WaterDistance, Transmittance, SceneDepth }

    /// <summary>单个基础相机的环境装配；介质、焦散和合成独立，不读取或修改仿真。</summary>
    [RequireComponent(typeof(Camera)), DisallowMultipleComponent]
    public sealed class OceanEnvironment : MonoBehaviour
    {
        [Header("Water domain (world units)")]
        public Vector3 Center, Size = new Vector3(360, 180, 360);
        public float WaterSurfaceHeight = 90;
        public Color WaterColor = new Color(0.015f, 0.07f, 0.11f);
        public Vector3 Extinction = new Vector3(0.004f, 0.002f, 0.0015f);
        [Header("Directional background (no geometry)")]
        public Color HorizonTop = new Color(0.035f, 0.13f, 0.2f), HorizonBottom = new Color(0.006f, 0.025f, 0.05f);
        [Min(1)] public float HorizonDistance = 600;
        public bool Fog = true, Background = true;
        public Shader FogShader;
        public OceanDebugView DebugView;
        [Header("Receiver caustics")]
        public ComputeShader CausticCompute;
        public CausticQuality Quality = CausticQuality.Shared256;
        [Range(1, 60)] public int UpdateHz = 30;
        [Range(0, 4)] public float CausticStrength = 1.5f;
        public float WorldScale = 0.035f;
        public bool PauseEnvironment, ShortLoop, FixedClock;
        public double EnvironmentSeconds;
        [Header("Legacy surface study only")]
        public Shader SurfaceShader;
        public Material BackgroundMaterial;
        public bool SurfaceStudyBackground;
        public string LastError { get; private set; }
        public long TextureBytes => field.TextureBytes;
        public RenderTexture CausticTexture => field.Current;
        private readonly CausticField field = new CausticField();
        private Camera view;
        private Material compositeMaterial, studyMaterial;
        private GameObject studyBackground;
        private OceanCompositePass pass;

        private void OnEnable()
        {
            view = GetComponent<Camera>();
            RenderPipelineManager.beginCameraRendering += BeginCamera;
            RenderPipelineManager.endCameraRendering += EndCamera;
        }
        private void Update()
        { if (!FixedClock && !PauseEnvironment) EnvironmentSeconds += Time.unscaledDeltaTime; }

        public void Prepare()
        {
            LastError = null;
            if (FogShader == null) { LastError = "Assign the Ocean composite shader."; return; }
            if (compositeMaterial == null)
            {
                compositeMaterial = new Material(FogShader) { name = "Ocean composite (owned)" };
                pass = new OceanCompositePass(compositeMaterial);
            }
            // 强表面花纹仅保留在参考实验；生产海洋从不创建背景 Cube。
            if (SurfaceStudyBackground && studyBackground == null && BackgroundMaterial != null)
            {
                studyMaterial = new Material(BackgroundMaterial) { name = "Surface study inward wall (owned)", renderQueue = 2490 };
                studyMaterial.SetFloat("_Cull", (float)CullMode.Front);
                studyBackground = GameObject.CreatePrimitive(PrimitiveType.Cube);
                studyBackground.name = "Surface study inward wall";
                studyBackground.transform.SetParent(transform, false);
                CausticField.DestroyOwned(studyBackground.GetComponent<Collider>());
                var renderer = studyBackground.GetComponent<MeshRenderer>(); renderer.sharedMaterial = studyMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
            if (studyBackground != null)
            {
                studyBackground.SetActive(SurfaceStudyBackground && Background);
                studyBackground.transform.SetPositionAndRotation(Center, Quaternion.identity);
                studyBackground.transform.localScale = Size;
            }
            field.Prepare(CausticCompute, Quality, UpdateHz, EnvironmentSeconds, ShortLoop);
            LastError = field.LastError;
        }

        private void BeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera != view) return;
            var data = camera.GetUniversalAdditionalCameraData();
            if (data.renderType != CameraRenderType.Base || data.cameraStack.Count > 0 || camera.stereoEnabled)
            { LastError = "Ocean currently supports one non-XR base camera without stacking."; return; }
            Prepare(); if (compositeMaterial == null) return;
            Shader.SetGlobalVector("_OceanMin", Center - Size * 0.5f);
            Shader.SetGlobalVector("_OceanMax", Center + Size * 0.5f);
            Shader.SetGlobalFloat("_OceanWaterSurfaceHeight", WaterSurfaceHeight);
            Shader.SetGlobalVector("_OceanExtinction", Vector3.Max(Extinction, Vector3.zero));
            Shader.SetGlobalColor("_OceanWaterColor", WaterColor);
            Shader.SetGlobalFloat("_OceanEnabled", 1);
            Shader.SetGlobalColor("_OceanAmbientSky", RenderSettings.ambientSkyColor * RenderSettings.ambientIntensity);
            Shader.SetGlobalColor("_OceanAmbientEquator", RenderSettings.ambientEquatorColor * RenderSettings.ambientIntensity);
            Shader.SetGlobalColor("_OceanAmbientGround", RenderSettings.ambientGroundColor * RenderSettings.ambientIntensity);
            Shader.SetGlobalFloat("_OceanDirect", Quality == CausticQuality.DirectReference ? 1 : 0);
            Shader.SetGlobalFloat("_OceanShortLoop", ShortLoop ? 1 : 0);
            Shader.SetGlobalVector("_OceanCaustic", new Vector4(WorldScale, Quality == CausticQuality.Off || LastError != null ? 0 : CausticStrength,
                field.Blend, (float)(EnvironmentSeconds % (ShortLoop ? 12 : 480 * Math.PI))));
            Shader.SetGlobalTexture("_OceanCausticA", field.Current != null ? field.Current : Texture2D.blackTexture);
            Shader.SetGlobalTexture("_OceanCausticB", field.Next != null ? field.Next : Texture2D.blackTexture);
            compositeMaterial.SetVector("_OceanComposite", new Vector4(Fog ? 1 : 0,
                Background && !SurfaceStudyBackground ? 1 : 0, Mathf.Max(1, HorizonDistance), (int)DebugView));
            compositeMaterial.SetColor("_OceanHorizonTop", HorizonTop);
            compositeMaterial.SetColor("_OceanHorizonBottom", HorizonBottom);
            // 相机局部注入保留既有 URP 提交链路，不改动 Phase 1–3 的共享 Renderer。
            if (Fog || (Background && !SurfaceStudyBackground) || DebugView != OceanDebugView.Final)
                data.scriptableRenderer.EnqueuePass(pass);
        }
        private void EndCamera(ScriptableRenderContext context, Camera camera)
        { if (camera == view) Shader.SetGlobalFloat("_OceanEnabled", 0); }
        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= BeginCamera;
            RenderPipelineManager.endCameraRendering -= EndCamera;
            field.Dispose();
            CausticField.DestroyOwned(compositeMaterial); CausticField.DestroyOwned(studyMaterial); CausticField.DestroyOwned(studyBackground);
            compositeMaterial = null; studyMaterial = null; studyBackground = null; pass = null;
            Shader.SetGlobalFloat("_OceanEnabled", 0);
        }
    }
}
