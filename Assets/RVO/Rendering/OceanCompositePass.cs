using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Rvo.Rendering
{
    /// <summary>不透明颜色/深度之后的一次合成；背景只写颜色，不制造实体深度。</summary>
    internal sealed class OceanCompositePass : ScriptableRenderPass
    {
        private readonly Material material;
        private sealed class PassData { public TextureHandle Color, Depth; public Material Material; }
        public OceanCompositePass(Material material)
        {
            this.material = material;
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            requiresIntermediateTexture = true;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }
        public override void RecordRenderGraph(RenderGraph graph, ContextContainer context)
        {
            var resources = context.Get<UniversalResourceData>();
            if (resources.isActiveTargetBackBuffer) return;
            var source = resources.activeColorTexture;
            var desc = graph.GetTextureDesc(source);
            desc.name = "Ocean composite color"; desc.clearBuffer = false; desc.depthBufferBits = DepthBits.None;
            var destination = graph.CreateTexture(desc);
            using (var builder = graph.AddRasterRenderPass<PassData>("RVO Ocean Opaque Composite", out var data))
            {
                data.Color = source; data.Depth = resources.cameraDepthTexture; data.Material = material;
                builder.UseTexture(data.Color, AccessFlags.Read); builder.UseTexture(data.Depth, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData d, RasterGraphContext c) =>
                {
                    // 显式绑定声明过依赖的深度，避免依赖其他相机/Pass 遗留的全局纹理。
                    c.cmd.SetGlobalTexture("_OceanSceneDepth", d.Depth);
                    Blitter.BlitTexture(c.cmd, d.Color, new Vector4(1, 1, 0, 0), d.Material, 0);
                });
            }
            // 输入与输出必须分离；透明和后处理随后读取本次合成结果。
            resources.cameraColor = destination;
        }
    }
}
