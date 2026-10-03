using UnityEngine;

namespace Rvo.Rendering
{
    [DisallowMultipleComponent]
    public sealed class FishLiveBridge : MonoBehaviour
    {
        public VolumeSimulationBootstrap Source;
        public GpuFishRenderer Renderer;
        private VolumeSimulationBootstrap subscribed;
        private void OnEnable()
        {
            if (Source == null || Renderer == null) return;
            subscribed = Source;
            subscribed.SnapshotCommitted += Capture;
            subscribed.PresentationCleared += Clear;
            TryCaptureCurrent();
        }
        private void Capture(AgentSnapshot snapshot) { if (Renderer != null && Renderer.isActiveAndEnabled) Renderer.Capture(snapshot); }
        private void Clear() { if (Renderer != null) Renderer.Clear(); }
        // 仿真在 Update 提交后再取插值系数，早于 Renderer 的 LateUpdate(100)。
        private void LateUpdate()
        {
            if (Renderer == null || !Renderer.isActiveAndEnabled || subscribed == null) return;
            if (Renderer.Poses.Count == 0) TryCaptureCurrent();
            Renderer.Interpolation = subscribed.PresentationAlpha;
        }
        private void TryCaptureCurrent()
        {
            if (subscribed != null && subscribed.World != null && subscribed.World.State == WorldState.Ready)
                Capture(new AgentSnapshot(subscribed.World.Snapshot, subscribed.World.Tick, subscribed.Generation, subscribed.World.Settings.FixedDeltaTime));
        }
        private void OnDisable()
        {
            if (subscribed != null) { subscribed.SnapshotCommitted -= Capture; subscribed.PresentationCleared -= Clear; }
            subscribed = null; Clear();
        }
    }
}
