using System;
using System.Globalization;
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
        public int WarmupFrames = 120, SampleFrames = 600;
        public bool RunOnStart;
        private struct Sample { public int Frame; public long Tick, Dropped, Bytes; public double FrameMs, Pack, Upload, Submit, GpuMs; public ulong TimingStamp; }
        private Sample[] samples;
        private readonly FrameTiming[] timing = new FrameTiming[1];
        private int frame;
        private double lastPackTotal;
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
            }
            if (RunOnStart) Begin();
        }
        [ContextMenu("Begin render-only benchmark")]
        public void Begin()
        {
            if (Fixture == null || !Application.isPlaying) return;
            ReleaseTarget();
            samples = new Sample[Mathf.Max(1, SampleFrames)]; frame = 0; lastPackTotal = Fixture.Renderer.Poses.TotalPackMilliseconds;
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; Application.runInBackground = true; Fixture.ShowHud = false;
            if (offscreen)
            {
                benchmarkCamera = Fixture.Renderer.ViewCamera;
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
            if (frame++ < WarmupFrames) { lastPackTotal = Fixture.Renderer.Poses.TotalPackMilliseconds; return; }
            var renderer = Fixture.Renderer;
            int index = frame - WarmupFrames - 1;
            uint available = FrameTimingManager.GetLatestTimings(1, timing);
            samples[index] = new Sample
            {
                Frame = Time.frameCount, Tick = Fixture.Tick, Dropped = Fixture.DroppedTicks,
                FrameMs = Time.unscaledDeltaTime * 1000.0,
                Pack = Fixture.Mode != FishFixtureMode.GpuFish ? 0 : renderer.Poses.TotalPackMilliseconds - lastPackTotal,
                Upload = Fixture.Mode == FishFixtureMode.GpuFish ? renderer.UploadMilliseconds : 0,
                Submit = Fixture.Mode == FishFixtureMode.GpuFish ? renderer.SubmitMilliseconds : 0,
                Bytes = Fixture.Mode == FishFixtureMode.GpuFish ? renderer.UploadBytes : 0,
                // FrameTiming 返回延迟结果，保留它自己的时间戳，不伪装成当前 CPU 帧的 GPU 时间。
                GpuMs = available > 0 && timing[0].gpuFrameTime > 0 ? timing[0].gpuFrameTime : -1,
                TimingStamp = available > 0 ? timing[0].frameStartTimestamp : 0
            };
            lastPackTotal = renderer.Poses.TotalPackMilliseconds;
            if (index + 1 == samples.Length) Finish();
        }
        private void Finish()
        {
            Directory.CreateDirectory(output);
            var csv = new StringBuilder("frame,tick,dropped_ticks,frame_ms,pack_ms,upload_ms,submit_ms,upload_bytes,latest_gpu_ms,gpu_timing_timestamp\n");
            foreach (var s in samples)
                csv.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F5},{4:F5},{5:F5},{6:F5},{7},{8:F5},{9}\n", s.Frame,s.Tick,s.Dropped,s.FrameMs,s.Pack,s.Upload,s.Submit,s.Bytes,s.GpuMs,s.TimingStamp);
            File.WriteAllText(Path.Combine(output, "frames.csv"), csv.ToString());
            File.WriteAllText(Path.Combine(output, "environment.json"), JsonUtility.ToJson(new Metadata
            {
                unity = Application.unityVersion, gpu = SystemInfo.graphicsDeviceName, api = SystemInfo.graphicsDeviceType.ToString(), cpu = SystemInfo.processorType,
                mode = Fixture.Mode.ToString(), agents = Fixture.AgentCount, width = Screen.width, height = Screen.height,
                warmup = WarmupFrames, frames = samples.Length, gpuMemoryMB = SystemInfo.graphicsMemorySize, bufferBytes = Fixture.Renderer.BufferBytes,
                culling = Fixture.Renderer.FrustumCulling, forceLod = Fixture.Renderer.ForceLod, orbit = Fixture.OrbitCamera,
                editor = Application.isEditor, pipeline = QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline.name : "Graphics default",
                renderPath = offscreen ? "Explicit SRP RenderTexture (no presentation)" : "Window (must remain visible)",
                gpuTimingNote = "-1 means unavailable; latest_gpu_ms is delayed and keyed by gpu_timing_timestamp. Synthetic render-only, not ORCA throughput."
            }, true));
            CaptureImage();
            Debug.Log("Phase 4 benchmark saved: " + output);
            samples = null;
            ReleaseTarget();
            if (quit) Application.Quit(0);
        }
        private void OnDisable() { samples = null; ReleaseTarget(); }
        private void ReleaseTarget()
        {
            if (benchmarkTarget == null) return;
            if (benchmarkCamera != null) { benchmarkCamera.targetTexture = previousTarget; benchmarkCamera.enabled = cameraWasEnabled; }
            benchmarkTarget.Release(); Destroy(benchmarkTarget); benchmarkTarget = null; offscreenRequest = null;
        }
        private void CaptureImage()
        {
            var camera = Fixture.Renderer.ViewCamera;
            var target = new RenderTexture(960, 540, 24);
            var texture = new Texture2D(960, 540, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                // 截图在采样结束后进行；它的同步读回不计入基准，也不进入交互热路径。
                if (Fixture.Mode == FishFixtureMode.GpuFish) Fixture.Renderer.Render();
                RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
                RenderTexture.active = target; texture.ReadPixels(new Rect(0, 0, 960, 540), 0, 0); texture.Apply();
                File.WriteAllBytes(Path.Combine(output, "frame.png"), texture.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; target.Release(); Destroy(target); Destroy(texture); }
        }
        [Serializable] private sealed class Metadata
        {
            public string unity, gpu, api, cpu, mode, pipeline, gpuTimingNote, renderPath;
            public int agents, width, height, warmup, frames, gpuMemoryMB, forceLod;
            public long bufferBytes;
            public bool culling, orbit, editor;
        }
    }
}
