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
        [Range(0, 2)] public int AgentTier;
        public SimulationWorld World { get; private set; }
        public QualityMetrics Quality { get; private set; }
        public int CollisionTicks { get; private set; }
        public int FallbackTicks { get; private set; }
        public string LastError { get; private set; }
        public GridNavigation Navigation { get; private set; }
        public GridAvoidanceSolver NavigationSolver { get; private set; }
        public GridMapPresenter MapPresenter;
        [Range(14, 24)] public int HudFontSize = 17;
        public long DroppedTicks => clock.DroppedTicks;
        private readonly FixedStepClock clock = new FixedStepClock();
        private bool started;
        private AvoidanceAlgorithm? algorithmOverride;
        private NeighborSearchAlgorithm? neighborOverride;
        private ExecutionBackend? backendOverride;
        private float? biasOverride;
        private GUISkin hudSkin;
        private Vector2 hudScroll;

        private void Start() { started = true; ResetSimulation(); }
        private void OnEnable() { if (started) ResetSimulation(); }
        private void OnDisable() { World?.Dispose(); World = null; Navigation = null; NavigationSolver = null; MapPresenter?.Clear(); }
        private void OnDestroy() { if (hudSkin != null) Destroy(hudSkin); }

        private void Update()
        {
            if (World == null || Paused) return;
            int steps = clock.Advance(Time.unscaledDeltaTime, World.Settings.FixedDeltaTime, Mathf.Max(1, MaxCatchUpSteps));
            for (int i = 0; i < steps && World != null; i++) StepSimulation(false);
            if (steps > 0 && World != null && Presenter != null) Presenter.Present(World.Snapshot);
            if (steps > 0) PresentMap();
        }

        [ContextMenu("Reset Simulation")]
        public void ResetSimulation()
        {
            if (!Application.isPlaying) { ValidateConfiguration(); return; }
            World?.Dispose(); World = null;
            Navigation = null; NavigationSolver = null; MapPresenter?.Clear();
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
                SimulationModules modules;
                if (Profile.Navigation.Enabled)
                {
                    var navigationSettings = Profile.Navigation;
                    settings.AgentCount = Profile.CountForTier(Mathf.Clamp(AgentTier, 0, 2));
                    var bakedMap = Profile.BakedMap.Load(navigationSettings, Profile.Scenario.Radius);
                    modules = Phase2ModuleFactory.Create(settings, Profile.Scenario, navigationSettings, bakedMap, out var navigation, out var solver);
                    Navigation = navigation; NavigationSolver = solver;
                }
                else modules = Phase1ModuleFactory.Create(settings);
                World = new SimulationWorld(settings, Profile.Scenario, modules);
                if (Presenter != null) Presenter.Initialize(World.Snapshot);
                PresentMap();
                FitMap();
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
                if (present) PresentMap();
            }
            catch (Exception error) { Fail(error); }
        }

        public void SetAvoidance(int algorithm)
        {
            if (Profile != null && Profile.Navigation.Enabled && algorithm != (int)AvoidanceAlgorithm.ORCA) return;
            // 只覆盖本次运行设置，Reset 保留所选算法，不修改共享资产。
            algorithmOverride = (AvoidanceAlgorithm)algorithm;
            ResetSimulation();
        }

        public void SetNeighborSearch(int algorithm) { neighborOverride = (NeighborSearchAlgorithm)algorithm; ResetSimulation(); }
        public void SetBackend(int backend) { backendOverride = (ExecutionBackend)backend; ResetSimulation(); }
        public void SetSideBias(float bias) { biasOverride = bias; ResetSimulation(); }
        public void SetAgentTier(int tier)
        {
            if (tier < 0 || tier > 2) throw new ArgumentOutOfRangeException(nameof(tier));
            AgentTier = tier; ResetSimulation();
        }
        public void FitMap()
        {
            if (Navigation == null || Presenter == null || Presenter.ViewCamera == null) return;
            var camera = Presenter.ViewCamera;
            camera.orthographicSize = Mathf.Max(Navigation.Map.Height * Navigation.Map.CellSize * 0.55f,
                Navigation.Map.Width * Navigation.Map.CellSize * 0.55f / camera.aspect);
            camera.transform.position = new Vector3(0, World.Settings.PlaneHeight + 50, 0);
        }
        public void FocusSelectedAgent()
        {
            if (World == null || Presenter == null || Presenter.ViewCamera == null) return;
            int i = Mathf.Clamp(SelectedAgent, 0, World.Snapshot.Count - 1);
            Presenter.ViewCamera.transform.position = (Vector3)World.Snapshot.Positions[i] + Vector3.up * 50;
            Presenter.ViewCamera.orthographicSize = Mathf.Max(10, World.Snapshot.Parameters[i].Radius * 12);
        }
        private void PresentMap()
        {
            if (World != null && Navigation != null && MapPresenter != null)
            {
                MapPresenter.SelectedAgent = SelectedAgent;
                MapPresenter.Present(Navigation, World.Snapshot, World.Settings.PlaneHeight);
            }
        }

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
                using (var modules = Profile.Navigation.Enabled
                    ? Phase2ModuleFactory.Create(Profile.Simulation, Profile.Scenario, Profile.Navigation,
                        Profile.BakedMap.Load(Profile.Navigation, Profile.Scenario.Radius), out _, out _)
                    : Phase1ModuleFactory.Create(Profile.Simulation))
                    Debug.Log(modules.GetReadinessIssue() ?? "RVO 配置有效，模块已就绪。", this);
            }
            catch (Exception error) { Debug.LogError(error.Message, this); }
        }

        private void Fail(Exception error)
        {
            LastError = error.Message;
            World?.Dispose(); World = null;
            Navigation = null; NavigationSolver = null;
            Paused = true;
            Debug.LogError("RVO: " + error.Message, this);
        }

        private void OnGUI()
        {
            if (!ShowControls) return;
            int requestedTier = -1;
            bool navigationMode = Profile != null && Profile.Navigation.Enabled;
            if (hudSkin == null) hudSkin = Instantiate(GUI.skin);
            hudSkin.label.fontSize = HudFontSize; hudSkin.label.wordWrap = true;
            hudSkin.button.fontSize = HudFontSize; hudSkin.button.fixedHeight = HudFontSize + 16;
            hudSkin.toggle.fontSize = HudFontSize;
            var previousSkin = GUI.skin; GUI.skin = hudSkin;
            GUILayout.BeginArea(new Rect(12, 12, Mathf.Max(220, Mathf.Min(430, Screen.width * 0.36f - 24)), Mathf.Max(80, Screen.height - 24)), GUI.skin.box);
            hudScroll = GUILayout.BeginScrollView(hudScroll);
            GUILayout.Label(navigationMode ? "RVO · Phase 2 / Grid A*" : "RVO · Phase 1 / XZ");
            GUILayout.Label(World == null ? LastError ?? "Not running" :
                $"{World.Settings.Avoidance} | Agents {World.Settings.AgentCount} | Tick {World.Tick}");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Paused ? "Resume" : "Pause")) SetPaused(!Paused);
            if (GUILayout.Button("Step")) { Paused = true; StepOnce(); }
            if (GUILayout.Button("Reset")) ResetSimulation();
            GUILayout.EndHorizontal();
            if (!navigationMode)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("None (reset)")) SetAvoidance((int)AvoidanceAlgorithm.None);
                if (GUILayout.Button("VO (reset)")) SetAvoidance((int)AvoidanceAlgorithm.VO);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("RVO (reset)")) SetAvoidance((int)AvoidanceAlgorithm.RVO);
                if (GUILayout.Button("ORCA (reset)")) SetAvoidance((int)AvoidanceAlgorithm.ORCA);
                GUILayout.EndHorizontal();
            }
            else if (Navigation != null)
            {
                GUILayout.Label($"{Navigation.Map.Width} x {Navigation.Map.Height} | Baked / static");
                GUILayout.BeginHorizontal();
                for (int tier = 0; tier < 3; tier++)
                    if (GUILayout.Button($"{(tier == AgentTier ? "● " : "")}{Profile.CountForTier(tier)}")) requestedTier = tier;
                GUILayout.EndHorizontal();
                GUILayout.Label($"Arrived {Navigation.ArrivedCount} | Pending {Navigation.PendingCount} | No path {Navigation.NoPathCount}");
                GUILayout.Label($"Ready {Navigation.ReadyCount} | Direct/tick {Navigation.LastDirectPaths} | A* weight {Navigation.Settings.EffectiveHeuristicWeight:F2}");
                GUILayout.Label($"Search nodes/tick {Navigation.LastExpandedNodes} | Requests {Navigation.ReplanCount}");
                GUILayout.Label($"Safety min scale {NavigationSolver.LastSafetyScale:F3} | Limited agents {NavigationSolver.LastLimitedAgents} | Ticks {NavigationSolver.SafetyLimitedTicks}");
                GUILayout.Label($"Safety pair checks {NavigationSolver.LastSafetyPairChecks} | Static truncated {NavigationSolver.LastStaticTruncations}");
                GUILayout.Label($"Seed {Navigation.Settings.Seed} | Step {World.LastMetrics.TotalSimulationMilliseconds:F2} ms");
                GUILayout.Label("New map: exit Play, then Generate + Bake in Profile.");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Fit map")) FitMap();
                if (GUILayout.Button("Focus agent")) FocusSelectedAgent();
                GUILayout.EndHorizontal();
                GUILayout.Label("Wheel: zoom | Middle drag: pan");
                if (MapPresenter != null)
                {
                    bool showPaths = GUILayout.Toggle(MapPresenter.ShowPaths, $"Paths (max {MapPresenter.MaxDisplayedPaths} + selected)");
                    if (showPaths != MapPresenter.ShowPaths) { MapPresenter.ShowPaths = showPaths; PresentMap(); }
                }
            }
            if (World != null)
            {
                if (GUILayout.Button($"Query: {World.Settings.NeighborSearch}"))
                    SetNeighborSearch(((int)World.Settings.NeighborSearch + 1) % 3);
                if (GUILayout.Button($"Backend: {World.Settings.Backend}"))
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
            GUILayout.Label(Navigation == null ? "Scene view: select the RVO object to inspect constraints." :
                "Scene view: select the RVO object to inspect paths.");
            if (DemoProfiles != null)
                for (int i = 0; i < DemoProfiles.Length; i++)
                    if (GUILayout.Button(DemoProfiles[i].name)) SelectProfile(i);
            GUILayout.EndScrollView(); GUILayout.EndArea(); GUI.skin = previousSkin;
            // 绘制结束后再切档；错误配置销毁世界时，不在同一 GUI 事件中继续读取它。
            if (requestedTier >= 0) SetAgentTier(requestedTier);
        }
    }
}
