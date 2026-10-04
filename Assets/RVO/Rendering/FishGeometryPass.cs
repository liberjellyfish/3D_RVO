using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Rvo.Rendering
{
    /// <summary>显式纳入 URP 的鱼几何 Pass；颜色、深度与法线共用同一姿态和可见列表。</summary>
    internal sealed class FishGeometryPass : ScriptableRenderPass
    {
        private readonly GpuFishRenderer owner;
        private readonly bool prepass;
        private sealed class PassData { public GpuFishRenderer Owner; public int ShaderPass; }
        public FishGeometryPass(GpuFishRenderer owner, bool prepass)
        {
            this.owner = owner; this.prepass = prepass;
            renderPassEvent = prepass ? RenderPassEvent.AfterRenderingPrePasses : RenderPassEvent.AfterRenderingOpaques;
        }
        public override void RecordRenderGraph(RenderGraph graph, ContextContainer context)
        {
            var resources = context.Get<UniversalResourceData>();
            bool normals = prepass && resources.cameraNormalsTexture.IsValid();
            var depth = prepass ? resources.cameraDepthTexture : resources.activeDepthTexture;
            // Copy 路径的 cameraDepth 是 R32 颜色纹理；它由 opaque 后的 URP copy 生成。
            if (!depth.IsValid() || (prepass && graph.GetTextureDesc(depth).depthBufferBits == DepthBits.None)) return;
            using (var builder = graph.AddRasterRenderPass<PassData>(prepass ? "RVO Fish Depth / Normals" : "RVO Fish Opaque", out var data))
            {
                data.Owner = owner; data.ShaderPass = owner.GeometryShaderPass(prepass, normals);
                builder.SetRenderAttachmentDepth(depth, AccessFlags.ReadWrite);
                if (!prepass) builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                else if (normals) builder.SetRenderAttachment(resources.cameraNormalsTexture, 0, AccessFlags.ReadWrite);
                builder.UseBuffer(graph.ImportBuffer(owner.Prepared), AccessFlags.Read);
                for (int lod = 0; lod < 4; lod++)
                {
                    builder.UseBuffer(graph.ImportBuffer(owner.VisibleIndices(lod)), AccessFlags.Read);
                    builder.UseBuffer(graph.ImportBuffer(owner.Arguments(lod)), AccessFlags.Read);
                }
                if (!prepass && resources.mainShadowsTexture.IsValid()) builder.UseTexture(resources.mainShadowsTexture, AccessFlags.Read);
                builder.SetRenderFunc((PassData d, RasterGraphContext c) => d.Owner.DrawGeometry(c.cmd, d.ShaderPass));
            }
        }
    }
}
