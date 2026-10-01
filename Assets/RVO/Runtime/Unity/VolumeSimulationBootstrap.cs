using System;
using UnityEngine;

namespace Rvo
{
    [DisallowMultipleComponent]
    public sealed class VolumeSimulationBootstrap : MonoBehaviour
    {
        public SimulationProfile Profile;
        public VolumePresenter Presenter;
        [Range(0, 2)] public int AgentTier;
        public bool Paused, CollectQuality;
        public int SelectedAgent;
        [Range(1, 8)] public int MaxStepsPerFrame = 2;
        public SimulationWorld World { get; private set; }
        public VolumeNavigation Navigation { get; private set; }
        public VolumeAvoidanceSolver Solver { get; private set; }
        public QualityMetrics Quality { get; private set; }
        public VolumeRunMetrics Metrics { get; private set; }
        public string LastError { get; private set; }
        private readonly FixedStepClock clock = new FixedStepClock();
        private bool started;
        private ExecutionBackend? backend;
        private NeighborSearchAlgorithm? query;
        private AvoidanceAlgorithm? avoidance;
        private void Start() { started = true; ResetSimulation(); }
        private void OnEnable() { if (started) ResetSimulation(); }
        private void OnDisable() { Release(); }
        private void Release() { World?.Dispose(); World = null; Navigation = null; Solver = null; Presenter?.Clear(); }
        public void ResetSimulation()
        {
            Release(); clock.Reset(); LastError = null; Quality = default;
            try
            {
                if (Profile == null) throw new InvalidOperationException("Assign a Phase 3 profile.");
                Profile.ValidateProfile(); var settings = Profile.Simulation; settings.AgentCount = Profile.CountForTier(AgentTier);
                if (backend.HasValue) settings.Backend = backend.Value;
                if (query.HasValue) settings.NeighborSearch = query.Value;
                if (avoidance.HasValue) settings.Avoidance = avoidance.Value;
                var map = Profile.BakedVolume.Load(Profile.Volume, Profile.Scenario.Radius);
                var modules = Phase3ModuleFactory.Create(settings, Profile.Scenario, Profile.Volume, map, out var navigation, out var solver);
                World = new SimulationWorld(settings, Profile.Scenario, modules); Navigation = navigation; Solver = solver;
                Metrics = new VolumeRunMetrics(settings.AgentCount);
                Presenter?.Initialize(map, World.Snapshot); Present();
            }
            catch (Exception e) { Fail(e); }
        }
        private void Update()
        {
            if (World == null || Paused) return;
            int steps = clock.Advance(Time.unscaledDeltaTime, World.Settings.FixedDeltaTime, Mathf.Clamp(MaxStepsPerFrame, 1, 8));
            for (int i = 0; i < steps && World != null; i++) StepOnce(false);
            if (steps > 0) Present();
        }
        public void StepOnce(bool present = true)
        {
            if (World == null) return;
            try
            {
                World.Step(); Metrics.Observe(World, Navigation, Solver);
                if (CollectQuality) Quality = QualityEvaluator.Evaluate(World.Snapshot, World.DebugSnapshot, 0.0001f, SimulationDimension.Full3D);
                if (present) Present();
            }
            catch (Exception e) { Fail(e); }
        }
        private void Present()
        {
            if (World == null || Presenter == null) return;
            Presenter.SelectedAgent = Mathf.Clamp(SelectedAgent, 0, World.Settings.AgentCount - 1);
            Presenter.Present(World.Snapshot, Navigation, Solver, World);
        }
        private void Fail(Exception e) { LastError = e.Message; Release(); Paused = true; Debug.LogException(e, this); }
        public void SetAgentTier(int tier) { AgentTier = Mathf.Clamp(tier, 0, 2); ResetSimulation(); }
        private void OnGUI()
        {
            bool reset = false; int requestedTier = -1;
            GUILayout.BeginArea(new Rect(12, 12, 360, Mathf.Max(120, Screen.height - 24)), GUI.skin.box);
            GUILayout.Label("Phase 3 · XYZ voxel navigation");
            if (World == null) GUILayout.Label(LastError ?? "Not running");
            else
            {
                GUILayout.Label($"{Navigation.Map.Resolution}³ baked | {World.Settings.AgentCount} agents | Tick {World.Tick}");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(Paused ? "Resume" : "Pause")) Paused = !Paused;
                if (GUILayout.Button("Step")) { Paused = true; StepOnce(); }
                if (GUILayout.Button("Reset")) reset = true;
                GUILayout.EndHorizontal();
                if (World != null)
                {
                    GUILayout.BeginHorizontal(); for (int tier = 0; tier < 3; tier++) if (GUILayout.Button(Profile.CountForTier(tier).ToString())) requestedTier = tier; GUILayout.EndHorizontal();
                    GUILayout.Label($"Current / first arrival: {Metrics.CurrentArrived} / {Metrics.FirstArrived}");
                    GUILayout.Label($"Pending {Navigation.PendingCount} | failed {Navigation.FailedCount} | expanded {Navigation.LastExpandedNodes}");
                    GUILayout.Label($"Step {World.LastMetrics.TotalSimulationMilliseconds:F2} ms | wait max {Metrics.LongestWait:F1}s");
                    GUILayout.Label($"Safety scale {Solver.LastSafetyScale:F3} | limited {Solver.LastLimitedAgents} | pair tests {Solver.LastSafetyPairChecks}");
                    GUILayout.Label($"Yield {Navigation.Traffic.YieldingCount} | recovery replans {Navigation.RecoveryReplans}");
                    if (GUILayout.Button($"Query: {World.Settings.NeighborSearch}")) { query = World.Settings.NeighborSearch == NeighborSearchAlgorithm.BruteForce ? NeighborSearchAlgorithm.SpatialHash : NeighborSearchAlgorithm.BruteForce; reset = true; }
                    if (GUILayout.Button($"Backend: {World.Settings.Backend}")) { backend = World.Settings.Backend == ExecutionBackend.Reference ? ExecutionBackend.JobsBurst : ExecutionBackend.Reference; reset = true; }
                    if (GUILayout.Button($"Avoidance: {World.Settings.Avoidance}")) { avoidance = World.Settings.Avoidance == AvoidanceAlgorithm.ORCA ? AvoidanceAlgorithm.None : AvoidanceAlgorithm.ORCA; reset = true; }
                    if (World != null && World.Settings.Avoidance == AvoidanceAlgorithm.None) GUILayout.Label("STATIC ONLY: agent-agent collisions are not prevented.");
                    CollectQuality = GUILayout.Toggle(CollectQuality, "Independent O(N²) XYZ diagnostics");
                    if (CollectQuality) GUILayout.Label($"Swept pairs {Quality.SweptCollisionPairs} | invalid {Quality.InvalidAgents}");
                    if (Presenter != null)
                    {
                        Presenter.ShowPaths = GUILayout.Toggle(Presenter.ShowPaths, "Paths (selected + first 16)");
                        Presenter.ShowSlice = GUILayout.Toggle(Presenter.ShowSlice, "Voxel slice (bounded window)");
                        Presenter.ShowVelocityPlanes = GUILayout.Toggle(Presenter.ShowVelocityPlanes, "Selected velocity-space planes");
                        if (Presenter.ShowVelocityPlanes) GUILayout.Label("Velocity space above selected sphere: cyan preferred, magenta candidate, green final.");
                        if (GUILayout.Button("Fit volume")) Presenter.Fit();
                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button("<")) SelectedAgent = (SelectedAgent + World.Settings.AgentCount - 1) % World.Settings.AgentCount;
                        GUILayout.Label($"Agent {SelectedAgent}");
                        if (GUILayout.Button(">")) SelectedAgent = (SelectedAgent + 1) % World.Settings.AgentCount;
                        GUILayout.EndHorizontal();
                        if (GUILayout.Button(Presenter.IsFollowing ? "Stop following" : "Follow selected"))
                        {
                            if (Presenter.IsFollowing) Presenter.StopFollowing();
                            else Presenter.Focus(World.Snapshot, Mathf.Clamp(SelectedAgent, 0, World.Settings.AgentCount - 1));
                        }
                    }
                    GUILayout.Label("Right: orbit | wheel: zoom | middle: pan / stop follow");
                    GUILayout.Label($"Dropped catch-up ticks: {clock.DroppedTicks}");
                }
            }
            GUILayout.EndArea();
            if (requestedTier >= 0) { AgentTier = requestedTier; reset = true; }
            if (reset) ResetSimulation();
        }
    }
}
