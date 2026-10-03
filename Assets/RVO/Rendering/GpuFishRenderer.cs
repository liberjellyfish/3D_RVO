using System;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rvo.Rendering
{
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public sealed class GpuFishRenderer : MonoBehaviour
    {
        public Camera ViewCamera;
        public ComputeShader CullingShader;
        public Shader FishShader;
        public bool AutoRender = true, FrustumCulling = true, ShowLodColors;
        [Range(-1, 3)] public int ForceLod = -1;
        public Vector3 LodPixels = new Vector3(80, 24, 8);
        [Range(0, 0.4f)] public float LodHysteresis = 0.15f;
        [Range(0, 1)] public float Interpolation = 1;
        public FishPoseBuffer Poses { get; } = new FishPoseBuffer();
        public string LastError { get; private set; }
        public double UploadMilliseconds { get; private set; }
        public double SubmitMilliseconds { get; private set; }
        public long UploadBytes { get; private set; }
        public long BufferBytes => capacity == 0 ? 0 : capacity * (long)(FishGpuData.Stride * 3 + 16 + 4 * 4) + 4 * GraphicsBuffer.IndirectDrawIndexedArgs.size;
        public GraphicsBuffer Arguments(int lod) => args[lod];
        public GraphicsBuffer VisibleIndices(int lod) => visible[lod];
        public GraphicsBuffer Prepared => prepared;
        public Mesh MeshAt(int lod) => meshes[lod];
        public static bool Supported => SystemInfo.supportsComputeShaders && SystemInfo.supportsInstancing && SystemInfo.graphicsShaderLevel >= 45;
        private GraphicsBuffer previous, current, prepared, history;
        private readonly GraphicsBuffer[] args = new GraphicsBuffer[4], visible = new GraphicsBuffer[4];
        private readonly Mesh[] meshes = new Mesh[4];
        private readonly MaterialPropertyBlock[] properties = new MaterialPropertyBlock[4];
        private readonly Plane[] planes = new Plane[6];
        private readonly Vector4[] planeVectors = new Vector4[6];
        private Material material;
        private CommandBuffer commands;
        private int capacity, uploadedRevision = -1, kernel;
        private static readonly int CountOffset = FindInstanceCountOffset();
        private static readonly ProfilerMarker UploadMarker = new ProfilerMarker("RVO.Fish.Upload");
        private static readonly ProfilerMarker SubmitMarker = new ProfilerMarker("RVO.Fish.Submit");

        public void Capture(in AgentSnapshot snapshot) => Poses.Capture(snapshot);
        private void LateUpdate() { if (AutoRender) Render(); }
        public void Clear() { ReleaseGpu(); Poses.Dispose(); }
        private void OnDisable() => Clear();
        private void OnDestroy() => Clear();

        private bool EnsureResources()
        {
            if (!Supported) { LastError = "GPU fish requires compute shaders and indirect instancing; use the Phase 3 debug presenter on this device."; return false; }
            if (ViewCamera == null || CullingShader == null || FishShader == null) { LastError = "Assign camera, fish shader and culling compute shader."; return false; }
            if (capacity == Poses.Count && material != null) return true;
            ReleaseGpu();
            try
            {
                capacity = Poses.Count;
                previous = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, FishGpuData.Stride);
                current = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, FishGpuData.Stride);
                prepared = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, FishGpuData.Stride);
                history = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 16);
                history.SetData(new uint4[capacity]);
                material = new Material(FishShader) { name = "RVO GPU fish (owned)", enableInstancing = true };
                commands = new CommandBuffer { name = "RVO Fish Prepare / Cull / Args" };
                kernel = CullingShader.FindKernel("Cull");
                for (int lod = 0; lod < 4; lod++)
                {
                    meshes[lod] = ProceduralFishMesh.Create(lod);
                    visible[lod] = new GraphicsBuffer(GraphicsBuffer.Target.Append, capacity, sizeof(uint));
                    args[lod] = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size);
                    args[lod].SetData(new[] { new GraphicsBuffer.IndirectDrawIndexedArgs
                    {
                        indexCountPerInstance = meshes[lod].GetIndexCount(0),
                        startIndex = meshes[lod].GetIndexStart(0), baseVertexIndex = (uint)meshes[lod].GetBaseVertex(0)
                    }});
                    properties[lod] = new MaterialPropertyBlock();
                    properties[lod].SetBuffer("_Fish", prepared); properties[lod].SetBuffer("_Visible", visible[lod]);
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
                using (UploadMarker.Auto()) { previous.SetData(Poses.Previous); current.SetData(Poses.Current); }
                uploadedRevision = Poses.Revision;
                UploadBytes = capacity * FishGpuData.Stride * 2L;
                UploadMilliseconds = Elapsed(start);
            }
            long submitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            using (SubmitMarker.Auto())
            {
                GeometryUtility.CalculateFrustumPlanes(ViewCamera, planes);
                for (int i = 0; i < 6; i++) planeVectors[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
                commands.Clear();
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
                commands.EndSample("RVO Fish Cull / LOD");
                commands.BeginSample("RVO Fish Counter to Args");
                for (int lod = 0; lod < 4; lod++) commands.CopyCounterValue(visible[lod], args[lod], (uint)CountOffset);
                commands.EndSample("RVO Fish Counter to Args");
                // 同一 graphics queue 先准备索引与参数，再提交高层 indirect draw；不混用延迟执行的 Render Graph compute。
                Graphics.ExecuteCommandBuffer(commands);
                for (int lod = 0; lod < 4; lod++)
                {
                    properties[lod].SetFloat("_DebugLod", ShowLodColors ? lod : -1);
                    var renderParams = new RenderParams(material)
                    {
                        camera = ViewCamera, worldBounds = Poses.WorldBounds, matProps = properties[lod],
                        shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false,
                        motionVectorMode = MotionVectorGenerationMode.ForceNoMotion, layer = gameObject.layer
                    };
                    Graphics.RenderMeshIndirect(renderParams, meshes[lod], args[lod]);
                }
            }
            SubmitMilliseconds = Elapsed(submitStart);
        }

        private static string VisibleName(int lod) => lod == 0 ? "_Visible0" : lod == 1 ? "_Visible1" : lod == 2 ? "_Visible2" : "_Visible3";
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
            commands?.Dispose(); commands = null;
            for (int lod = 0; lod < 4; lod++)
            {
                args[lod]?.Dispose(); args[lod] = null; visible[lod]?.Dispose(); visible[lod] = null;
                DestroyOwned(meshes[lod]); meshes[lod] = null; properties[lod] = null;
            }
            DestroyOwned(material); material = null; capacity = 0; uploadedRevision = -1;
        }
        private static void DestroyOwned(UnityEngine.Object asset)
        { if (asset != null) { if (Application.isPlaying) Destroy(asset); else DestroyImmediate(asset); } }
    }
}
