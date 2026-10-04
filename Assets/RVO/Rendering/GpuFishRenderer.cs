using System;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Rvo.Rendering
{
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public sealed class GpuFishRenderer : MonoBehaviour
    {
        public Camera ViewCamera;
        public ComputeShader CullingShader;
        public Shader FishShader;
        public bool DetailedNearMesh;
        public bool ReefAppearance;
        public bool EnableMotionVectors;
        public FishAnimationMode NearAnimation;
        public FishFarMode FarRepresentation;
        public bool EnableFarCards;
        public bool AutoRender = true, FrustumCulling = true, ShowLodColors, ShowStatusColors;
        [Range(-1, 3)] public int ForceLod = -1;
        public Vector3 LodPixels = new Vector3(80, 24, 8);
        [Range(0, 0.4f)] public float LodHysteresis = 0.15f;
        [Range(0, 1)] public float Interpolation = 1;
        public FishPoseBuffer Poses { get; } = new FishPoseBuffer();
        public string LastError { get; private set; }
        public double UploadMilliseconds { get; private set; }
        public double SubmitMilliseconds { get; private set; }
        public long UploadBytes { get; private set; }
        public long BufferBytes => capacity == 0 ? 0 : capacity * (long)(FishGpuData.Stride * 4 + 16 + 4 * 4) + 4 * GraphicsBuffer.IndirectDrawIndexedArgs.size;
        public GraphicsBuffer Arguments(int lod) => args[lod];
        public GraphicsBuffer VisibleIndices(int lod) => visible[lod];
        public GraphicsBuffer Prepared => prepared;
        public GraphicsBuffer PreviousDisplay => previousDisplay;
        public bool HasDisplayHistory { get; private set; }
        public Matrix4x4 PreviousViewProjection { get; private set; }
        public Mesh MeshAt(int lod) => meshes[lod];
        public long AnimationBytes { get; private set; }
        public static bool Supported => SystemInfo.supportsComputeShaders && SystemInfo.supportsInstancing && SystemInfo.graphicsShaderLevel >= 45;
        private GraphicsBuffer previous, current, prepared, previousDisplay, history;
        private int displayFrame = -1;
        private int lastRenderedFrame = -1;
        private uint displayGeneration;
        private int displayIdentityRevision;
        private Camera displayCamera;
        private FishGeometryPass depthPass, colorPass;
        private FishMotionPass motionPass;
        private int submissionFrame = -1;
        private Matrix4x4 displayViewProjection, displayProjection;
        private readonly GraphicsBuffer[] args = new GraphicsBuffer[4], visible = new GraphicsBuffer[4];
        private readonly Mesh[] meshes = new Mesh[4];
        private readonly MaterialPropertyBlock[] properties = new MaterialPropertyBlock[4];
        private readonly Plane[] planes = new Plane[6];
        private readonly Vector4[] planeVectors = new Vector4[6];
        private Material material;
        private Texture2D animationTexture;
        private bool builtDetailed, builtCards, builtReef;
        private FishAnimationMode builtAnimation;
        private FishFarMode builtFar;
        private CommandBuffer commands;
        private int capacity, uploadedRevision = -1, kernel, historyKernel;
        private uint uploadedGeneration;
        private int uploadedIdentityRevision;
        private int forwardShaderPass, depthShaderPass, normalsShaderPass, motionShaderPass;
        private static readonly int CountOffset = FindInstanceCountOffset();
        private static readonly ProfilerMarker UploadMarker = new ProfilerMarker("RVO.Fish.Upload");
        private static readonly ProfilerMarker SubmitMarker = new ProfilerMarker("RVO.Fish.Submit");

        public void Capture(in AgentSnapshot snapshot) => Poses.Capture(snapshot);
        private void OnEnable() => RenderPipelineManager.beginCameraRendering += BeginCamera;
        private void LateUpdate() { if (AutoRender) Render(); }
        public void Clear() { ReleaseGpu(); Poses.Dispose(); }
        public void InvalidateDisplayHistory() { displayFrame = -1; HasDisplayHistory = false; }
        private void OnDisable() { RenderPipelineManager.beginCameraRendering -= BeginCamera; Clear(); }
        private void OnDestroy() => Clear();

        private bool EnsureResources()
        {
            if (!Supported) { LastError = "GPU fish requires compute shaders and indirect instancing; use the Phase 3 debug presenter on this device."; return false; }
            if (ViewCamera == null || CullingShader == null || FishShader == null) { LastError = "Assign camera, fish shader and culling compute shader."; return false; }
            if (capacity == Poses.Count && material != null && builtDetailed == DetailedNearMesh && builtAnimation == NearAnimation
                && builtFar == FarRepresentation && builtCards == EnableFarCards && builtReef == ReefAppearance) return true;
            ReleaseGpu();
            try
            {
                capacity = Poses.Count;
                builtDetailed = DetailedNearMesh; builtAnimation = NearAnimation; builtFar = FarRepresentation; builtCards = EnableFarCards;
                builtReef = ReefAppearance;
                previous = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, FishGpuData.Stride);
                current = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, FishGpuData.Stride);
                prepared = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, FishGpuData.Stride);
                previousDisplay = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, FishGpuData.Stride);
                history = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 16);
                history.SetData(new uint4[capacity]);
                material = new Material(FishShader) { name = "RVO GPU fish (owned)", enableInstancing = true };
                material.SetFloat("_Cull", ReefAppearance ? (float)CullMode.Back : (float)CullMode.Off);
                material.SetFloat("_ReefFish", ReefAppearance ? 1 : 0);
                forwardShaderPass = material.FindPass("Fish Forward"); depthShaderPass = material.FindPass("Fish Depth"); normalsShaderPass = material.FindPass("Fish Normals");
                if (forwardShaderPass < 0 || depthShaderPass < 0 || normalsShaderPass < 0)
                    throw new InvalidOperationException("Fish shader must provide Forward, Depth and Normals passes.");
                depthPass = new FishGeometryPass(this, true); colorPass = new FishGeometryPass(this, false);
                motionShaderPass = material.FindPass("Fish Motion"); motionPass = new FishMotionPass(this);
                commands = new CommandBuffer { name = "RVO Fish Prepare / Cull / Args" };
                kernel = CullingShader.FindKernel("Cull");
                historyKernel = CullingShader.FindKernel("CopyDisplayHistory");
                for (int lod = 0; lod < 4; lod++)
                {
                    meshes[lod] = ReefAppearance ? ReefFishMesh.Create(lod) : lod == 0 && DetailedNearMesh ? ProceduralFishMesh.CreateDetailed()
                        : lod >= 2 && EnableFarCards && FarRepresentation != FishFarMode.Mesh
                        ? FishAnimationBaker.CreateCard(lod == 3 ? FishFarMode.Billboard : FarRepresentation) : ProceduralFishMesh.Create(lod);
                    visible[lod] = new GraphicsBuffer(GraphicsBuffer.Target.Append, capacity, sizeof(uint));
                    args[lod] = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size);
                    args[lod].SetData(new[] { new GraphicsBuffer.IndirectDrawIndexedArgs
                    {
                        indexCountPerInstance = meshes[lod].GetIndexCount(0),
                        startIndex = meshes[lod].GetIndexStart(0), baseVertexIndex = (uint)meshes[lod].GetBaseVertex(0)
                    }});
                    properties[lod] = new MaterialPropertyBlock();
                    properties[lod].SetFloat("_ReefFish", ReefAppearance ? 1 : 0);
                    properties[lod].SetBuffer("_Fish", prepared); properties[lod].SetBuffer("_Visible", visible[lod]);
                    properties[lod].SetBuffer("_PreviousDisplay", previousDisplay);
                    properties[lod].SetFloat("_CardMode", !ReefAppearance && lod >= 2 && EnableFarCards ? (int)(lod == 3 && FarRepresentation != FishFarMode.Mesh ? FishFarMode.Billboard : FarRepresentation) : 0);
                    properties[lod].SetFloat("_AnimationMode", lod == 0 ? (int)NearAnimation : 0);
                }
                animationTexture = FishAnimationBaker.Bake(meshes[0], NearAnimation);
                if (animationTexture != null)
                {
                    properties[0].SetTexture("_AnimationTexture", animationTexture);
                    AnimationBytes = (long)animationTexture.width * animationTexture.height * (NearAnimation == FishAnimationMode.VertexTexture ? 4 : 8);
                }
                LastError = null;
                return true;
            }
            catch { ReleaseGpu(); throw; }
        }

        public void Render()
        {
            UploadBytes = 0; UploadMilliseconds = 0; SubmitMilliseconds = 0;
            if (Poses.Count == 0 || !EnsureResources()) return;
            if (uploadedRevision != Poses.Revision)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                // 连续单 Tick 时旧 current 正好就是新 previous，交换 GPU 端点可省去一半上传。
                // 追赶多 Tick、换代或重排身份时仍上传完整双端点，不猜测丢失的中间快照。
                bool adjacent = uploadedRevision >= 0 && Poses.Revision == uploadedRevision + 1
                    && uploadedGeneration == Poses.Generation && uploadedIdentityRevision == Poses.IdentityRevision;
                using (UploadMarker.Auto())
                {
                    if (adjacent) { var old = previous; previous = current; current = old; }
                    else previous.SetData(Poses.Previous);
                    current.SetData(Poses.Current);
                }
                uploadedRevision = Poses.Revision;
                uploadedGeneration = Poses.Generation; uploadedIdentityRevision = Poses.IdentityRevision;
                UploadBytes = capacity * FishGpuData.Stride * (adjacent ? 1L : 2L);
                UploadMilliseconds = Elapsed(start);
            }
            long submitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            using (SubmitMarker.Auto())
            {
                GeometryUtility.CalculateFrustumPlanes(ViewCamera, planes);
                for (int i = 0; i < 6; i++) planeVectors[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
                commands.Clear();
                bool resetDisplay = displayFrame < 0 || lastRenderedFrame < 0 || Time.frameCount > lastRenderedFrame + 1 || displayGeneration != Poses.Generation ||
                    displayIdentityRevision != Poses.IdentityRevision || displayCamera != ViewCamera || displayProjection != ViewCamera.nonJitteredProjectionMatrix;
                bool nextDisplayFrame = displayFrame != Time.frameCount;
                if (nextDisplayFrame || resetDisplay)
                {
                    HasDisplayHistory = !resetDisplay;
                    PreviousViewProjection = resetDisplay ? ViewCamera.nonJitteredProjectionMatrix * ViewCamera.worldToCameraMatrix : displayViewProjection;
                    if (HasDisplayHistory) CopyDisplayHistory();
                }
                commands.BeginSample("RVO Fish Cull / LOD");
                commands.SetComputeBufferParam(CullingShader, kernel, "_Previous", previous);
                commands.SetComputeBufferParam(CullingShader, kernel, "_Current", current);
                commands.SetComputeBufferParam(CullingShader, kernel, "_Prepared", prepared);
                commands.SetComputeBufferParam(CullingShader, kernel, "_LodHistory", history);
                for (int lod = 0; lod < 4; lod++)
                {
                    commands.SetBufferCounterValue(visible[lod], 0);
                    commands.SetComputeBufferParam(CullingShader, kernel, VisibleName(lod), visible[lod]);
                }
                commands.SetComputeIntParam(CullingShader, "_Count", capacity);
                commands.SetComputeIntParam(CullingShader, "_CullEnabled", FrustumCulling ? 1 : 0);
                commands.SetComputeIntParam(CullingShader, "_ForceLod", Mathf.Clamp(ForceLod, -1, 3));
                commands.SetComputeIntParam(CullingShader, "_Orthographic", ViewCamera.orthographic ? 1 : 0);
                commands.SetComputeIntParam(CullingShader, "_CrossCards", !ReefAppearance && EnableFarCards && (FarRepresentation == FishFarMode.CrossQuads || FarRepresentation == FishFarMode.CrossTriangles) ? 1 : 0);
                commands.SetComputeFloatParam(CullingShader, "_Alpha", Mathf.Clamp01(Interpolation));
                commands.SetComputeFloatParam(CullingShader, "_Hysteresis", Mathf.Clamp(LodHysteresis, 0, 0.4f));
                commands.SetComputeFloatParam(CullingShader, "_ProjectionScale", ViewCamera.pixelHeight * Mathf.Abs(ViewCamera.projectionMatrix.m11));
                commands.SetComputeFloatParam(CullingShader, "_NearClip", ViewCamera.nearClipPlane);
                float z = Mathf.Max(0.1f, LodPixels.z), y = Mathf.Max(z, LodPixels.y), x = Mathf.Max(y, LodPixels.x);
                commands.SetComputeVectorParam(CullingShader, "_LodPixels", new Vector4(x, y, z, 0));
                commands.SetComputeVectorParam(CullingShader, "_CameraPosition", ViewCamera.transform.position);
                commands.SetComputeVectorParam(CullingShader, "_CameraForward", ViewCamera.transform.forward);
                commands.SetComputeVectorArrayParam(CullingShader, "_Planes", planeVectors);
                commands.DispatchCompute(CullingShader, kernel, (capacity + 127) / 128, 1, 1);
                if (resetDisplay) CopyDisplayHistory();
                commands.EndSample("RVO Fish Cull / LOD");
                commands.BeginSample("RVO Fish Counter to Args");
                for (int lod = 0; lod < 4; lod++) commands.CopyCounterValue(visible[lod], args[lod], (uint)CountOffset);
                commands.EndSample("RVO Fish Counter to Args");
                // Compute 仍在同一 graphics queue 立即完成；所有几何绘制随后统一进入 Render Graph。
                Graphics.ExecuteCommandBuffer(commands);
                displayFrame = Time.frameCount; displayGeneration = Poses.Generation; displayCamera = ViewCamera;
                displayIdentityRevision = Poses.IdentityRevision;
                displayProjection = ViewCamera.nonJitteredProjectionMatrix;
                displayViewProjection = displayProjection * ViewCamera.worldToCameraMatrix;
                for (int lod = 0; lod < 4; lod++)
                {
                    properties[lod].SetFloat("_DebugLod", ShowLodColors ? lod : -1);
                    properties[lod].SetFloat("_DebugAppearance", ShowStatusColors ? 1 : 0);
                    properties[lod].SetFloat("_HasDisplayHistory", HasDisplayHistory ? 1 : 0);
                    BindLighting(properties[lod]);
                }
                submissionFrame = Time.frameCount;
            }
            SubmitMilliseconds = Elapsed(submitStart);
        }

        private static string VisibleName(int lod) => lod == 0 ? "_Visible0" : lod == 1 ? "_Visible1" : lod == 2 ? "_Visible2" : "_Visible3";
        private void BeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera != ViewCamera || submissionFrame != Time.frameCount || material == null || (camera.cullingMask & (1 << gameObject.layer)) == 0) return;
            var data = camera.GetUniversalAdditionalCameraData();
            if (data.renderType != CameraRenderType.Base || camera.stereoEnabled || data.cameraStack.Count > 0) return;
            lastRenderedFrame = Time.frameCount;
            data.scriptableRenderer.EnqueuePass(depthPass); data.scriptableRenderer.EnqueuePass(colorPass);
            // 卡片与 MSAA 尚无等价速度/深度语义，只在单采样真实网格路径提交。
            if (EnableMotionVectors && motionShaderPass >= 0 && (ReefAppearance || !EnableFarCards || FarRepresentation == FishFarMode.Mesh)
                && (!camera.allowMSAA || UniversalRenderPipeline.asset.msaaSampleCount == 1)) data.scriptableRenderer.EnqueuePass(motionPass);
        }
        internal void DrawMotion(RasterCommandBuffer command) => DrawGeometry(command, motionShaderPass);
        private void BindLighting(MaterialPropertyBlock block)
        {
            // Indirect draw 没有 MeshRenderer 自动填写的逐对象光照常量；缺失时主光与 SH 都会为零。
            var sun=RenderSettings.sun;
            bool receivesSun=sun == null || (sun.cullingMask & (1 << gameObject.layer)) != 0;
            block.SetVector("unity_LightData",new Vector4(0,0,receivesSun ? 1 : 0,0));
            var sh=RenderSettings.ambientProbe;
            block.SetVector("unity_SHAr",new Vector4(sh[0,3],sh[0,1],sh[0,2],sh[0,0]-sh[0,6]));
            block.SetVector("unity_SHAg",new Vector4(sh[1,3],sh[1,1],sh[1,2],sh[1,0]-sh[1,6]));
            block.SetVector("unity_SHAb",new Vector4(sh[2,3],sh[2,1],sh[2,2],sh[2,0]-sh[2,6]));
            block.SetVector("unity_SHBr",new Vector4(sh[0,4],sh[0,5],sh[0,6]*3,sh[0,7]));
            block.SetVector("unity_SHBg",new Vector4(sh[1,4],sh[1,5],sh[1,6]*3,sh[1,7]));
            block.SetVector("unity_SHBb",new Vector4(sh[2,4],sh[2,5],sh[2,6]*3,sh[2,7]));
            block.SetVector("unity_SHC",new Vector4(sh[0,8],sh[1,8],sh[2,8],1));
        }
        internal void DrawGeometry(RasterCommandBuffer command, int shaderPass)
        {
            for (int lod = 0; lod < 4; lod++)
                command.DrawMeshInstancedIndirect(meshes[lod], 0, material, shaderPass, args[lod], 0, properties[lod]);
        }
        internal int GeometryShaderPass(bool prepass, bool normals) => prepass ? (normals ? normalsShaderPass : depthShaderPass) : forwardShaderPass;
        private void CopyDisplayHistory()
        {
            commands.SetComputeIntParam(CullingShader, "_Count", capacity);
            commands.SetComputeBufferParam(CullingShader, historyKernel, "_Prepared", prepared);
            commands.SetComputeBufferParam(CullingShader, historyKernel, "_PreviousDisplay", previousDisplay);
            commands.DispatchCompute(CullingShader, historyKernel, (capacity + 127) / 128, 1, 1);
        }
        private static int FindInstanceCountOffset()
        {
            // Unity 6 的公开成员是属性，不能 Marshal.OffsetOf；用实际平台结构定位 count，避免写死五个 uint。
            using var probe = new NativeArray<GraphicsBuffer.IndirectDrawIndexedArgs>(
                new[] { new GraphicsBuffer.IndirectDrawIndexedArgs { instanceCount = 0x7135ABCD } }, Allocator.Temp);
            var words = probe.Reinterpret<uint>(GraphicsBuffer.IndirectDrawIndexedArgs.size);
            for (int i = 0; i < words.Length; i++) if (words[i] == 0x7135ABCD) return i * sizeof(uint);
            throw new NotSupportedException("Cannot locate indirect instance count in the platform layout.");
        }
        private static double Elapsed(long start) => (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        private void ReleaseGpu()
        {
            previous?.Dispose(); previous = null; current?.Dispose(); current = null;
            prepared?.Dispose(); prepared = null; history?.Dispose(); history = null;
            previousDisplay?.Dispose(); previousDisplay = null; InvalidateDisplayHistory(); displayCamera = null; lastRenderedFrame = -1;
            commands?.Dispose(); commands = null;
            for (int lod = 0; lod < 4; lod++)
            {
                args[lod]?.Dispose(); args[lod] = null; visible[lod]?.Dispose(); visible[lod] = null;
                DestroyOwned(meshes[lod]); meshes[lod] = null; properties[lod] = null;
            }
            DestroyOwned(material); material = null; capacity = 0; uploadedRevision = -1;
            depthPass = null; colorPass = null; motionPass = null; submissionFrame = -1;
            DestroyOwned(animationTexture); animationTexture = null; AnimationBytes = 0;
        }
        private static void DestroyOwned(UnityEngine.Object asset)
        { if (asset != null) { if (Application.isPlaying) Destroy(asset); else DestroyImmediate(asset); } }
    }
}
