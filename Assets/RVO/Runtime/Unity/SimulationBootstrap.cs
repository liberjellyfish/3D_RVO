using System;
using UnityEngine;

namespace Rvo
{
    [DisallowMultipleComponent]
    public sealed class SimulationBootstrap : MonoBehaviour
    {
        public SimulationProfile Profile;
        public AgentMeshPresenter Presenter;
        public SimulationProfile[] DemoProfiles;
        public bool Paused;
        public bool ShowControls = true;
        public bool CollectQuality = true;
        [Min(1)] public int MaxCatchUpSteps = 8;
        [Min(0)] public int SelectedAgent;
        public SimulationWorld World { get; private set; }
        public QualityMetrics Quality { get; private set; }
        public int CollisionTicks { get; private set; }
        public int FallbackTicks { get; private set; }
        public string LastError { get; private set; }
        public long DroppedTicks => clock.DroppedTicks;
        private readonly FixedStepClock clock = new FixedStepClock();
        private bool started;
        private AvoidanceAlgorithm? algorithmOverride;
        private NeighborSearchAlgorithm? neighborOverride;
        private ExecutionBackend? backendOverride;
        private float? biasOverride;

        private void Start() { started = true; ResetSimulation(); }
        private void OnEnable() { if (started) ResetSimulation(); }
        private void OnDisable() { World?.Dispose(); World = null; }

        private void Update()
        {
            if (World == null || Paused) return;
            int steps = clock.Advance(Time.unscaledDeltaTime, World.Settings.FixedDeltaTime, Mathf.Max(1, MaxCatchUpSteps));
            for (int i = 0; i < steps && World != null; i++) StepSimulation(false);
            if (steps > 0 && World != null && Presenter != null) Presenter.Present(World.Snapshot);
        }

        [ContextMenu("Reset Simulation")]
        public void ResetSimulation()
        {
            if (!Application.isPlaying) { ValidateConfiguration(); return; }
            World?.Dispose(); World = null;
            clock.Reset(); Quality = default; CollisionTicks = FallbackTicks = 0; LastError = null;
            try
            {
                if (Profile == null) throw new InvalidOperationException("请指定 SimulationProfile。");
                Profile.ValidateProfile();
                // Profile 仅在重置时读取，Inspector 改值不会悄悄改变核心设置。
                var settings = Profile.Simulation;
                if (algorithmOverride.HasValue) settings.Avoidance = algorithmOverride.Value;
                if (neighborOverride.HasValue) settings.NeighborSearch = neighborOverride.Value;
                if (backendOverride.HasValue) settings.Backend = backendOverride.Value;
                if (biasOverride.HasValue) settings.PreferredSideBias = biasOverride.Value;
                World = new SimulationWorld(settings, Profile.Scenario, Phase1ModuleFactory.Create(settings));
                if (Presenter != null) Presenter.Initialize(World.Snapshot);
            }
            catch (Exception error) { Fail(error); }
        }

        public void SetPaused(bool paused) { Paused = paused; }

        [ContextMenu("Single Step")]
        public void StepOnce() => StepSimulation(true);

        private void StepSimulation(bool present)
        {
            if (World == null) return;
            try
            {
                World.Step();
                if (CollectQuality)
                {
                    Quality = QualityEvaluator.Evaluate(World.Snapshot, World.DebugSnapshot, World.Settings.Epsilon);
                    if (Quality.SweptCollisionPairs > 0) CollisionTicks++;
                    if (Quality.FallbackAgents > 0) FallbackTicks++;
                }
                if (present && Presenter != null) Presenter.Present(World.Snapshot);
            }
            catch (Exception error) { Fail(error); }
        }

        public void SetAvoidance(int algorithm)
        {
            // 只覆盖本次运行设置，Reset 保留所选算法，不修改共享资产。
            algorithmOverride = (AvoidanceAlgorithm)algorithm;
            ResetSimulation();
        }

        public void SetNeighborSearch(int algorithm) { neighborOverride = (NeighborSearchAlgorithm)algorithm; ResetSimulation(); }
        public void SetBackend(int backend) { backendOverride = (ExecutionBackend)backend; ResetSimulation(); }
        public void SetSideBias(float bias) { biasOverride = bias; ResetSimulation(); }

        public void SelectProfile(int index)
        {
            Profile = DemoProfiles[index];
            algorithmOverride = null; neighborOverride = null; backendOverride = null; biasOverride = null;
            ResetSimulation();
        }

        [ContextMenu("Validate RVO Configuration")]
        public void ValidateConfiguration()
        {
            try
            {
                if (Profile == null) throw new InvalidOperationException("请指定 SimulationProfile。");
                Profile.ValidateProfile();
                using (var modules = Phase1ModuleFactory.Create(Profile.Simulation))
                    Debug.Log(modules.GetReadinessIssue() ?? "RVO 配置有效，模块已就绪。", this);
            }
            catch (Exception error) { Debug.LogError(error.Message, this); }
        }

        private void Fail(Exception error)
        {
            LastError = error.Message;
            World?.Dispose(); World = null;
            Paused = true;
            Debug.LogError("RVO: " + error.Message, this);
        }

        private void OnGUI()
        {
            if (!ShowControls) return;
            GUILayout.BeginArea(new Rect(12, 12, Mathf.Min(340, Screen.width * 0.36f - 24), 640), GUI.skin.box);
            GUILayout.Label("RVO · Phase 1 / XZ");
            GUILayout.Label(World == null ? LastError ?? "Not running" :
                $"{World.Settings.Avoidance} | Agents {World.Settings.AgentCount} | Tick {World.Tick}");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Paused ? "Resume" : "Pause")) SetPaused(!Paused);
            if (GUILayout.Button("Step")) { Paused = true; StepOnce(); }
            if (GUILayout.Button("Reset")) ResetSimulation();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("None (reset)")) SetAvoidance((int)AvoidanceAlgorithm.None);
            if (GUILayout.Button("VO (reset)")) SetAvoidance((int)AvoidanceAlgorithm.VO);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("RVO (reset)")) SetAvoidance((int)AvoidanceAlgorithm.RVO);
            if (GUILayout.Button("ORCA (reset)")) SetAvoidance((int)AvoidanceAlgorithm.ORCA);
            GUILayout.EndHorizontal();
            if (World != null)
            {
                if (GUILayout.Button($"Query: {World.Settings.NeighborSearch} (switch/reset)"))
                    SetNeighborSearch(1 - (int)World.Settings.NeighborSearch);
                if (GUILayout.Button($"Backend: {World.Settings.Backend} (switch/reset)"))
                    SetBackend(1 - (int)World.Settings.Backend);
                if (GUILayout.Button($"Side bias: {World.Settings.PreferredSideBias:F2} (switch/reset)"))
                    SetSideBias(World.Settings.PreferredSideBias == 0 ? 0.05f : 0);
            }
            CollectQuality = GUILayout.Toggle(CollectQuality, "Quality checks (O(N²))");
            if (CollectQuality)
            {
                GUILayout.Label($"Arrived {Quality.Arrived} | Overlaps {Quality.OverlappingPairs}");
                GUILayout.Label($"Swept pairs {Quality.SweptCollisionPairs} | Collision ticks {CollisionTicks}");
                GUILayout.Label($"Fallback {Quality.FallbackAgents} | Fallback ticks {FallbackTicks}");
                GUILayout.Label($"Infeasible {Quality.InfeasibleAgents} | Slow (not arrived) {Quality.SlowUnarrived}");
                GUILayout.Label($"Mean |Δv| {Quality.MeanSpeedChange:F4}");
                GUILayout.Label($"Truncated agents {Quality.TruncatedAgents} | Gap {Quality.MinimumSeparation:F3}");
            }
            GUILayout.Label($"Dropped catch-up ticks {DroppedTicks}");
            GUILayout.Label("Scene view: select the RVO object to inspect constraints.");
            if (DemoProfiles != null)
                for (int i = 0; i < DemoProfiles.Length; i++)
                    if (GUILayout.Button(DemoProfiles[i].name)) SelectProfile(i);
            GUILayout.EndArea();
        }
    }
}
