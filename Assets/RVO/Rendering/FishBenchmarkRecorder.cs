using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rvo.Rendering
{
    /// <summary>仅显式启动时记录。先存内存、结束再写盘，避免 IO 污染采样。</summary>
    [DefaultExecutionOrder(200)]
    public sealed class FishBenchmarkRecorder : MonoBehaviour
    {
        public FishRenderFixture Fixture;
        public FishLiveBridge Live;
        private GpuFishRenderer Fish => Fixture != null ? Fixture.Renderer : Live.Renderer;
        private long Tick => Fixture != null ? Fixture.Tick : Live.Source.World.Tick;
        private long Dropped => Fixture != null ? Fixture.DroppedTicks : Live.Source.DroppedTicks;
        private bool DrawFish => Fixture == null || Fixture.Mode == FishFixtureMode.GpuFish;
        private double liveCpuMs;
        private void ObserveTick(AgentSnapshot snapshot) => liveCpuMs += Live.Source.World.LastMetrics.TotalSimulationMilliseconds;
        public int WarmupFrames = 120, SampleFrames = 600;
        public bool RunOnStart;
        private struct Sample { public int Frame; public long Tick, Dropped, Bytes; public double FrameMs, Pack, Upload, Submit, GpuMs, SimulationCpu; public ulong TimingStamp; }
        private Sample[] samples;
        private readonly FrameTiming[] timing = new FrameTiming[1];
        private int frame;
        private double lastPackTotal;
        private readonly HashSet<ulong> gpuTimestamps = new HashSet<ulong>();
        private bool quit;
        private string output;
        private bool offscreen, cameraWasEnabled;
        private RenderTexture benchmarkTarget, previousTarget;
        private Camera benchmarkCamera;
        private RenderPipeline.StandardRequest offscreenRequest;
        private void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-rvo-benchmark") { RunOnStart = true; quit = true; }
                if (args[i] == "-rvo-offscreen") offscreen = true;
                if (i + 1 < args.Length && args[i] == "-rvo-output") output = args[++i];
                if (i + 1 < args.Length && args[i] == "-rvo-frames" && int.TryParse(args[i + 1], out int count)) SampleFrames = Mathf.Max(1, count);
                if (i + 1 < args.Length && args[i] == "-rvo-warmup" && int.TryParse(args[i + 1], out int warmup)) WarmupFrames = Mathf.Max(1, warmup);
            }
            if (RunOnStart) Begin();
        }
        [ContextMenu("Begin render-only benchmark")]
        public void Begin()
        {
            if ((Fixture == null && Live == null) || !Application.isPlaying) return;
            ReleaseTarget();
            samples = new Sample[Mathf.Max(1, SampleFrames)]; frame = 0; lastPackTotal = Fish.Poses.TotalPackMilliseconds;
            gpuTimestamps.Clear();
            if (Live != null) { Live.Source.SnapshotCommitted -= ObserveTick; Live.Source.SnapshotCommitted += ObserveTick; Live.Source.ShowHud = false; }
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; Application.runInBackground = true;
            if (Fixture != null) Fixture.ShowHud = false;
            if (offscreen)
            {
                benchmarkCamera = Fish.ViewCamera;
                previousTarget = benchmarkCamera.targetTexture; cameraWasEnabled = benchmarkCamera.enabled;
                benchmarkTarget = new RenderTexture(Screen.width, Screen.height, 24);
                benchmarkCamera.targetTexture = benchmarkTarget; benchmarkCamera.enabled = false;
                offscreenRequest = new RenderPipeline.StandardRequest { destination = benchmarkTarget };
            }
            if (string.IsNullOrEmpty(output)) output = Path.Combine(Application.dataPath, "../Documentation/RVO/Verification/Phase4/Benchmark", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        }
        private void LateUpdate()
        {
            if (samples == null) return;
            FrameTimingManager.CaptureFrameTimings();
            // 隐藏窗口可能跳过自动相机渲染；离屏基准显式出帧，且关闭该相机的自动绘制以免重复。
            if (benchmarkTarget != null) RenderPipeline.SubmitRenderRequest(benchmarkCamera, offscreenRequest);
            if (frame++ < WarmupFrames) { lastPackTotal = Fish.Poses.TotalPackMilliseconds; liveCpuMs = 0; return; }
            var renderer = Fish;
            int index = frame - WarmupFrames - 1;
            uint available = FrameTimingManager.GetLatestTimings(1, timing);
            // GetLatestTimings 可多次返回同一 GPU 帧；重复时间戳不能重复计入分位数。
            bool validGpu = available > 0 && timing[0].gpuFrameTime > 0 && !double.IsNaN(timing[0].gpuFrameTime)
                && !double.IsInfinity(timing[0].gpuFrameTime) && timing[0].frameStartTimestamp != 0
                && gpuTimestamps.Add(timing[0].frameStartTimestamp);
            samples[index] = new Sample
            {
                Frame = Time.frameCount, Tick = Tick, Dropped = Dropped, SimulationCpu = liveCpuMs,
                FrameMs = Time.unscaledDeltaTime * 1000.0,
                Pack = DrawFish ? renderer.Poses.TotalPackMilliseconds - lastPackTotal : 0,
                Upload = DrawFish ? renderer.UploadMilliseconds : 0,
                Submit = DrawFish ? renderer.SubmitMilliseconds : 0,
                Bytes = DrawFish ? renderer.UploadBytes : 0,
                // FrameTiming 返回延迟结果，保留它自己的时间戳，不伪装成当前 CPU 帧的 GPU 时间。
                GpuMs = validGpu ? timing[0].gpuFrameTime : -1,
                TimingStamp = available > 0 ? timing[0].frameStartTimestamp : 0
            };
            lastPackTotal = renderer.Poses.TotalPackMilliseconds;
            liveCpuMs = 0;
            if (index + 1 == samples.Length) Finish();
        }
        private void Finish()
        {
            Directory.CreateDirectory(output);
            var csv = new StringBuilder("frame,tick,dropped_ticks,frame_ms,pack_ms,upload_ms,submit_ms,upload_bytes,latest_gpu_ms,gpu_timing_timestamp,simulation_cpu_ms\n");
            foreach (var s in samples)
                csv.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F5},{4:F5},{5:F5},{6:F5},{7},{8:F5},{9},{10:F5}\n", s.Frame,s.Tick,s.Dropped,s.FrameMs,s.Pack,s.Upload,s.Submit,s.Bytes,s.GpuMs,s.TimingStamp,s.SimulationCpu);
            File.WriteAllText(Path.Combine(output, "frames.csv"), csv.ToString());
            var gpu = new List<double>();
            foreach (var sample in samples) if (sample.GpuMs > 0) gpu.Add(sample.GpuMs);
            gpu.Sort();
            File.WriteAllText(Path.Combine(output,"gpu-summary.json"), JsonUtility.ToJson(new GpuSummary
            {
                validUniqueSamples=gpu.Count, totalCpuSamples=samples.Length,
                p50=Percentile(gpu,0.5), p95=Percentile(gpu,0.95), p99=Percentile(gpu,0.99),
                status=gpu.Count == 0 ? "Unavailable; no GPU performance conclusion" : "Unique delayed GPU frames; coverage must be reviewed"
            },true));
            var ocean = Fish.ViewCamera.GetComponent<OceanEnvironment>();
            File.WriteAllText(Path.Combine(output, "environment.json"), JsonUtility.ToJson(new Metadata
            {
                unity = Application.unityVersion, gpu = SystemInfo.graphicsDeviceName, api = SystemInfo.graphicsDeviceType.ToString(), cpu = SystemInfo.processorType,
                mode = Fixture != null ? Fixture.Mode.ToString() : "LiveORCA", agents = Fixture != null ? Fixture.AgentCount : Live.Source.World.Settings.AgentCount, width = Screen.width, height = Screen.height,
                warmup = WarmupFrames, frames = samples.Length, gpuMemoryMB = SystemInfo.graphicsMemorySize, bufferBytes = Fish.BufferBytes,
                culling = Fish.FrustumCulling, forceLod = Fish.ForceLod, orbit = Fixture != null && Fixture.OrbitCamera,
                animation = Fish.NearAnimation.ToString(), far = Fish.FarRepresentation.ToString(), animationBytes = Fish.AnimationBytes,
                fixedReplayClock = Fixture != null && Fixture.FixedReplayClock,
                ocean = ocean != null && ocean.enabled, fog = ocean != null && ocean.enabled && ocean.Fog,
                caustic = ocean != null ? ocean.Quality.ToString() : "None", causticHz = ocean != null ? ocean.UpdateHz : 0,
                causticBytes = ocean != null ? ocean.TextureBytes : 0,
                surfaceMaterial = ocean != null && ocean.SurfaceStudyBackground && ocean.BackgroundMaterial != null ? ocean.BackgroundMaterial.name : "Directional horizon (no geometry)",
                surfaceGain = ocean != null && ocean.BackgroundMaterial != null ? ocean.BackgroundMaterial.GetFloat("_PatternGain") : 1,
                surfaceScale = ocean != null && ocean.BackgroundMaterial != null ? ocean.BackgroundMaterial.GetFloat("_PatternScale") : 0,
                fogExtinction = ocean != null ? ocean.Extinction : Vector3.zero,
                waterColor = ocean != null ? ocean.WaterColor : Color.black,
                waterSurfaceHeight = ocean != null ? ocean.WaterSurfaceHeight : 0,
                horizonDistance = ocean != null ? ocean.HorizonDistance : 0,
                statusColors = Fish.ShowStatusColors, displayHistory = Fish.HasDisplayHistory,
                nearVertices = DrawFish ? Fish.MeshAt(0).vertexCount : 0,
                editor = Application.isEditor, pipeline = QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline.name : "Graphics default",
                renderPath = offscreen ? "Explicit SRP RenderTexture (no presentation)" : "Window (must remain visible)",
                submissionModel = "Immediate compute + RenderGraph geometry; submit_ms covers preparation, not full graph execution.",
                gpuTimingNote = "-1 means unavailable or duplicate; percentiles use unique delayed GPU timestamps. Fixed replay is not wall-clock simulation."
            }, true));
            CaptureImage();
            Debug.Log("Phase 4 benchmark saved: " + output);
            if (Live != null) Live.Source.SnapshotCommitted -= ObserveTick;
            samples = null;
            ReleaseTarget();
            if (quit) Application.Quit(0);
        }
        private static double Percentile(List<double> values,double fraction)
            => values.Count == 0 ? -1 : values[Math.Max(0,(int)Math.Ceiling(values.Count*fraction)-1)];
        [Serializable] private sealed class GpuSummary
        {
            public int validUniqueSamples,totalCpuSamples;
            public double p50,p95,p99;
            public string status;
        }
        private void OnDisable() { samples = null; ReleaseTarget(); if (Live != null) Live.Source.SnapshotCommitted -= ObserveTick; }
        private void ReleaseTarget()
        {
            if (benchmarkTarget == null) return;
            if (benchmarkCamera != null) { benchmarkCamera.targetTexture = previousTarget; benchmarkCamera.enabled = cameraWasEnabled; }
            benchmarkTarget.Release(); Destroy(benchmarkTarget); benchmarkTarget = null; offscreenRequest = null;
        }
        private void CaptureImage()
        {
            var camera = Fish.ViewCamera;
            var target = new RenderTexture(Screen.width, Screen.height, 24);
            var texture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            var previousCameraTarget = camera.targetTexture;
            try
            {
                // 截图在采样结束后进行；它的同步读回不计入基准，也不进入交互热路径。
                camera.targetTexture = target;
                if (DrawFish) Fish.Render();
                RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
                RenderTexture.active = target; texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); texture.Apply();
                File.WriteAllBytes(Path.Combine(output, "frame.png"), texture.EncodeToPNG());
            }
            finally { camera.targetTexture = previousCameraTarget; RenderTexture.active = previous; target.Release(); Destroy(target); Destroy(texture); }
        }
        [Serializable] private sealed class Metadata
        {
            public string unity, gpu, api, cpu, mode, pipeline, gpuTimingNote, renderPath, animation, far, caustic, surfaceMaterial;
            public string submissionModel;
            public float surfaceGain, surfaceScale;
            public Vector3 fogExtinction;
            public float waterSurfaceHeight, horizonDistance;
            public Color waterColor;
            public int agents, width, height, warmup, frames, gpuMemoryMB, forceLod, causticHz, nearVertices;
            public long bufferBytes, animationBytes, causticBytes;
            public bool culling, orbit, editor, fixedReplayClock, ocean, fog, statusColors, displayHistory;
        }
    }
}
