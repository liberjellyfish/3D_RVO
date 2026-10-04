using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Rvo.Rendering
{
    /// <summary>在 URP 相机速度之后补齐鱼的实际显示姿态速度，并以当前场景深度遮挡。</summary>
    internal sealed class FishMotionPass : ScriptableRenderPass
    {
        private readonly GpuFishRenderer owner;
        private sealed class Data { public GpuFishRenderer Owner; }
        public FishMotionPass(GpuFishRenderer owner)
        {
            this.owner = owner;
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
            ConfigureInput(ScriptableRenderPassInput.Motion);
        }
        public override void RecordRenderGraph(RenderGraph graph, ContextContainer context)
        {
            var resources = context.Get<UniversalResourceData>();
            if (!resources.motionVectorColor.IsValid() || !resources.activeDepthTexture.IsValid()) return;
            using var builder = graph.AddRasterRenderPass<Data>("RVO Fish Motion", out var data);
            data.Owner = owner;
            builder.AllowPassCulling(false);
            builder.SetRenderAttachment(resources.motionVectorColor, 0, AccessFlags.ReadWrite);
            // ZTest 使用已经包含鱼和礁石的真实深度；不以只含动态对象的 motion depth 判遮挡。
            builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
            builder.UseBuffer(graph.ImportBuffer(owner.Prepared), AccessFlags.Read);
            builder.UseBuffer(graph.ImportBuffer(owner.PreviousDisplay), AccessFlags.Read);
            for (int lod=0;lod<4;lod++)
            {
                builder.UseBuffer(graph.ImportBuffer(owner.VisibleIndices(lod)), AccessFlags.Read);
                builder.UseBuffer(graph.ImportBuffer(owner.Arguments(lod)), AccessFlags.Read);
            }
            builder.SetRenderFunc((Data d, RasterGraphContext c) => d.Owner.DrawMotion(c.cmd));
        }
    }
}
