using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Rvo.Editor
{
    // Same seed, budgets and tier on both maps. Observation and CSV work is outside Step timing.
    public static class ReefPerformance
    {
        public static void BuildAndRun()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(Phase4DemoBuilder.OceanLiveScene);
            OceanReefBuilder.Build();
            Run();
        }
        public static string Argument(string key, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
            return fallback;
        }

        [MenuItem("Tools/RVO/Profile Reef (largest tier, 1800 ticks x 3)")]
        public static void Run()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play before profiling.");
            var profile = AssetDatabase.LoadAssetAtPath<SimulationProfile>(OceanReefBuilder.Folder + "/ReefNavigation.asset");
            string folder = Argument("-reef-output", "Documentation/RVO/Verification/ReefNetwork2048/Performance");
            if (File.Exists(folder + "/summary.csv")) throw new IOException("Choose a fresh evidence directory.");
            Directory.CreateDirectory(folder);
            var settings = profile.Simulation; settings.AgentCount = 16; settings.MeasureStages = true;
            var warmMap = profile.BakedVolume.Load(profile.Volume, profile.Scenario.VolumeClearanceRadius);
            var warmModules = Phase3ModuleFactory.Create(settings, profile.Scenario, profile.Volume, warmMap, out _, out _);
            using (var warm = new SimulationWorld(settings, profile.Scenario, warmModules)) for (int i = 0; i < 5; i++) warm.Step();
            settings.AgentCount = int.Parse(Argument("-reef-agents",profile.AgentCountTiers.z.ToString()));
            const int ticks = 1800;
            var summary = new StringBuilder("run,obstacles,agents,ticks,setup_ms,p50_ms,p95_ms,p99_ms,nav_mean_ms,neighbors_mean_ms,avoidance_mean_ms,integration_mean_ms,ready_p50_s,ready_p95_s,ready_max_s,never_ready,first_arrived,current_arrived,max_wait_s,requests,coarse,fallbacks,replans,expanded,limited_agent_ticks,sampled_swept_pairs,static_violations,gc_bytes\n");
            int repeats=Mathf.Clamp(int.Parse(Argument("-reef-repeats","3")),1,10);
            for (int run = 1; run <= repeats; run++)
            {
                var map = profile.BakedVolume.Load(profile.Volume, profile.Scenario.VolumeClearanceRadius);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var modules = Phase3ModuleFactory.Create(settings, profile.Scenario, profile.Volume, map, out var navigation, out var solver);
                using (var world = new SimulationWorld(settings, profile.Scenario, modules))
                {
                    double setup = clock.Elapsed.TotalMilliseconds;
                    var metrics = new VolumeRunMetrics(settings.AgentCount);
                    var routeEvidence=new ReefRouteEvidence(world.Snapshot,map);
                    var times = new double[ticks]; double nav = 0, neighbors = 0, avoidance = 0, integration = 0;
                    long expanded = 0, collisions = 0, violations = 0;
                    var curve = new StringBuilder("tick,total_ms,navigation_ms,neighbors_ms,avoidance_ms,integration_ms,expanded,pending,failed,coarse,fallbacks,replans,first_arrived,current_arrived,never_ready,safety_pair_checks,limited\n");
                    for (int t = 0; t < ticks; t++)
                    {
                        world.Step(); metrics.Observe(world, navigation, solver);
                        routeEvidence.Observe(world);
                        var m = world.LastMetrics; times[t] = m.TotalSimulationMilliseconds;
                        nav += m.PreferredMilliseconds; neighbors += m.NeighborMilliseconds;
                        avoidance += m.AvoidanceMilliseconds; integration += m.IntegrationMilliseconds;
                        expanded += navigation.LastExpandedNodes;
                        // Static swept checks every tick; pair quality sampled every 30 ticks, never presented as exhaustive.
                        var agents = world.Snapshot; var before = world.DebugSnapshot.Inputs;
                        for (int i = 0; i < agents.Count; i++)
                            if (!map.SegmentClear(before.Positions[i], agents.Positions[i])) violations++;
                        if (world.Tick % 30 == 0)
                            collisions += QualityEvaluator.Evaluate(agents, world.DebugSnapshot, 0.0001f, SimulationDimension.Full3D).SweptCollisionPairs;
                        curve.AppendLine(FormattableString.Invariant($"{world.Tick},{times[t]:F5},{m.PreferredMilliseconds:F5},{m.NeighborMilliseconds:F5},{m.AvoidanceMilliseconds:F5},{m.IntegrationMilliseconds:F5},{navigation.LastExpandedNodes},{navigation.PendingCount},{navigation.FailedCount},{navigation.CoarsePaths},{navigation.CoarseFallbacks},{navigation.RecoveryReplans},{metrics.FirstArrived},{metrics.CurrentArrived},{metrics.NeverReady},{solver.LastSafetyPairChecks},{solver.LastLimitedAgents}"));
                    }
                    Array.Sort(times);
                    summary.AppendLine(FormattableString.Invariant($"{run},{map.ObstacleCount},{settings.AgentCount},{ticks},{setup:F3},{times[899]:F4},{times[1709]:F4},{times[1781]:F4},{nav/ticks:F4},{neighbors/ticks:F4},{avoidance/ticks:F4},{integration/ticks:F4},{metrics.ReadyPercentile(0.5,settings.FixedDeltaTime):F3},{metrics.ReadyPercentile(0.95,settings.FixedDeltaTime):F3},{metrics.ReadyPercentile(1,settings.FixedDeltaTime):F3},{metrics.NeverReady},{metrics.FirstArrived},{metrics.CurrentArrived},{metrics.LongestWait:F3},{navigation.RequestCount},{navigation.CoarsePaths},{navigation.CoarseFallbacks},{navigation.RecoveryReplans},{expanded},{metrics.LimitedAgentTicks},{collisions},{violations},{metrics.ManagedBytes}"));
                    routeEvidence.Write(folder,run);
                    File.WriteAllText($"{folder}/curve-{run}.csv", curve.ToString());
                    var agentsCsv = new StringBuilder("agent,first_ready_tick,first_arrival_tick,status,x,y,z,goal_x,goal_y,goal_z,anchor\n");
                    for (int i = 0; i < settings.AgentCount; i++)
                    {
                        var p=world.Snapshot.Positions[i]; var g=world.Snapshot.Goals[i];
                        agentsCsv.AppendLine(FormattableString.Invariant($"{i},{metrics.ReadyTick(i)},{metrics.ArrivalTick(i)},{navigation.Path(i).Status},{p.x},{p.y},{p.z},{g.x},{g.y},{g.z},{map.Anchor(p)}"));
                    }
                    File.WriteAllText($"{folder}/agents-{run}.csv", agentsCsv.ToString());
                    File.WriteAllText(folder + "/summary.csv", summary.ToString());
                    File.WriteAllText(folder + "/environment.txt", $"Unity={Application.unityVersion}; CPU={SystemInfo.processorType}; editor={Application.isEditor}; seed={profile.Scenario.Seed}; backend={settings.Backend}; dt={settings.FixedDeltaTime}; resolution={map.Resolution}; cell={map.CellSize}; obstacles={map.ObstacleCount}; coarseCell={map.Coarse?.CellSize}; freeCells={map.FreeCells}; mapBytes={map.StorageBytes}\nBudget: expansions={profile.Volume.ExpansionsPerTick}, requests={profile.Volume.RequestsPerTick}, slots={profile.Volume.SearchSlots}, weight={profile.Volume.HeuristicWeight}.\nNo rendering. Separate 16-agent warmup, shared immutable decoded map with prewarmed landmarks. Each run creates an independent world; setup includes spawning and native search storage, excludes decode, initial landmark preparation and Burst compilation. Tick timing includes startup requests and stage completion fences. Pair safety sampled every 30 ticks; static swept safety every tick. 1800 ticks = 60 simulated seconds.\n");
                    Debug.Log($"Reef run {run}: obstacles={map.ObstacleCount}, tick P95={times[1709]:F3}, ready={settings.AgentCount-metrics.NeverReady}, arrived={metrics.FirstArrived}, static violations={violations}");
                }
            }
        }
    }
}
