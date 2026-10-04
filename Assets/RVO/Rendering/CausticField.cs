using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Rvo.Rendering
{
    public enum CausticQuality { Off, Shared256, Shared512, DirectReference }

    /// <summary>只拥有焦散纹理与生成时钟；不依赖水域、背景几何或仿真。</summary>
    public sealed class CausticField : IDisposable
    {
        public RenderTexture Current { get; private set; }
        public RenderTexture Next { get; private set; }
        public float Blend { get; private set; }
        public string LastError { get; private set; }
        public long TextureBytes => Current == null ? 0 : Current.width * (long)Current.height * 2 * 2 * 4 / 3;
        private long generatedStep = long.MinValue;
        private bool generatedLoop;
        private int generatedHz;
        private ComputeShader generatedCompute;

        public void Prepare(ComputeShader compute, CausticQuality quality, int updateHz, double seconds, bool shortLoop)
        {
            LastError = null;
            if (quality == CausticQuality.Off || quality == CausticQuality.DirectReference) { Dispose(); return; }
            if (compute == null || !SystemInfo.supportsComputeShaders ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16_SFloat, GraphicsFormatUsage.LoadStore))
            {
                LastError = "Shared caustics require compute and R16F UAV support; select Off or DirectReference.";
                Dispose(); return;
            }
            int resolution = quality == CausticQuality.Shared512 ? 512 : 256;
            if (Current == null || Current.width != resolution)
            {
                Dispose();
                try { Current = Create(resolution); Next = Create(resolution); }
                catch { Dispose(); throw; }
            }
            int hz = Mathf.Clamp(updateHz, 1, 60);
            seconds = Math.Max(0, seconds);
            long step = (long)Math.Floor(seconds * hz);
            Blend = (float)(seconds * hz - step);
            if (step == generatedStep && generatedLoop == shortLoop && generatedHz == hz && generatedCompute == compute) return;
            if (step == generatedStep + 1 && generatedLoop == shortLoop && generatedHz == hz && generatedCompute == compute)
            { var swap = Current; Current = Next; Next = swap; }
            else Generate(compute, Current, step / (double)hz, shortLoop);
            Generate(compute, Next, (step + 1) / (double)hz, shortLoop);
            generatedStep = step; generatedLoop = shortLoop; generatedHz = hz; generatedCompute = compute;
        }

        private static RenderTexture Create(int resolution)
        {
            var texture = new RenderTexture(resolution, resolution, 0, GraphicsFormat.R16_SFloat)
            {
                name = "Shared caustic", enableRandomWrite = true, useMipMap = true, autoGenerateMips = false,
                wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear
            };
            if (!texture.Create()) { DestroyOwned(texture); throw new InvalidOperationException("Cannot create caustic texture."); }
            return texture;
        }

        private static void Generate(ComputeShader compute, RenderTexture target, double seconds, bool shortLoop)
        {
            var command = CommandBufferPool.Get("RVO Ocean Caustic Generate");
            try
            {
                int kernel = compute.FindKernel("Generate");
                command.BeginSample("RVO Ocean Caustic Generate");
                command.SetComputeTextureParam(compute, kernel, "_Result", target);
                command.SetComputeIntParam(compute, "_Resolution", target.width);
                command.SetComputeIntParam(compute, "_ReferenceForm", 0);
                command.SetComputeIntParam(compute, "_ShortLoop", shortLoop ? 1 : 0);
                command.SetComputeIntParam(compute, "_RepairSeam", 1);
                command.SetComputeFloatParam(compute, "_Seconds", (float)(seconds % (shortLoop ? 12 : 480 * Math.PI)));
                command.DispatchCompute(compute, kernel, target.width / 8, target.height / 8, 1);
                command.GenerateMips(target); command.EndSample("RVO Ocean Caustic Generate");
                Graphics.ExecuteCommandBuffer(command);
            }
            finally { CommandBufferPool.Release(command); }
        }

        public void Dispose()
        {
            if (Current != null) { Current.Release(); DestroyOwned(Current); Current = null; }
            if (Next != null) { Next.Release(); DestroyOwned(Next); Next = null; }
            generatedStep = long.MinValue; generatedCompute = null; Blend = 0;
        }
        internal static void DestroyOwned(UnityEngine.Object value)
        { if (value != null) { if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); } }
    }
}
