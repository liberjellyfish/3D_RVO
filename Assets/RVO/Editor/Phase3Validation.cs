using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Rvo.Editor
{
    public static class Phase3Validation
    {
        [MenuItem("Tools/RVO/Run Phase 3 Matrix (60s wall cap per tier)")]
        public static void RunMatrix() => Run(60);
        [MenuItem("Tools/RVO/Run Phase 3 Full Tick Acceptance")]
        public static void RunFullAcceptance() => Run(double.PositiveInfinity);
        private static void Run(double wallCap)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play before standalone validation.");
            var profile = AssetDatabase.LoadAssetAtPath<SimulationProfile>(Phase3DemoBuilder.ProfilePath);
            if (profile == null) throw new InvalidOperationException("Create and bake the Phase 3 demo first.");
            profile.ValidateProfile(); var map = profile.BakedVolume.Load(profile.Volume, profile.Scenario.VolumeClearanceRadius);
            const string folder = "Documentation/RVO/Verification/Phase3"; Directory.CreateDirectory(folder);
            var summary = new StringBuilder();
            summary.AppendLine($"Unity {Application.unityVersion}; CPU {SystemInfo.processorType}; RAM {SystemInfo.systemMemorySize} MiB");
            summary.AppendLine($"Editor batch={Application.isBatchMode}; seed={profile.Scenario.Seed}; volume={map.Resolution}³; mapBytes={map.StorageBytes}");
            summary.AppendLine($"Predeclared tick limit={profile.Volume.AcceptanceTicks}; wall cap={wallCap}; no rendering; Burst synchronous compilation requested.");
            summary.AppendLine("tier,backend,stop,ticks,sim_s,wall_s,first_arrived,current_arrived,never_ready,ready_p50_s,ready_p95_s,ready_max_s,all_first_s,all_current_s,max_wait_s,core_p50_ms,core_p95_ms,core_p99_ms,gc_bytes,requests,recovery_replans,limited_agent_ticks,sampled_collision_pairs,portal_crossings");
            foreach (int count in new[] { profile.AgentCountTiers.x, profile.AgentCountTiers.y, profile.AgentCountTiers.z })
            {
                var settings = profile.Simulation; settings.AgentCount = count; settings.MeasureStages = true;
                var modules = Phase3ModuleFactory.Create(settings, profile.Scenario, profile.Volume, map, out var navigation, out var solver);
                using (var world = new SimulationWorld(settings, profile.Scenario, modules))
                {
                    var metrics = new VolumeRunMetrics(count); var timings = new double[profile.Volume.AcceptanceTicks];
                    var curve = new StringBuilder("tick,sim_s,wall_s,first,current,pending,failed,longest_wait_s,core_ms,navigation_ms,neighbors_ms,avoidance_ms,integration_ms,expanded,pair_checks,limited\n");
                    long collisions = 0; string stop = "TickLimit"; int executed = 0;
                    try
                    {
                        while (executed < profile.Volume.AcceptanceTicks)
                        {
                            world.Step(); timings[executed++] = world.LastMetrics.TotalSimulationMilliseconds; metrics.Observe(world, navigation, solver);
                            if (count <= 16 || world.Tick % 30 == 0)
                                collisions += QualityEvaluator.Evaluate(world.Snapshot, world.DebugSnapshot, 0.0001f, SimulationDimension.Full3D).SweptCollisionPairs;
                            if (world.Tick == 1 || world.Tick % 30 == 0)
                            {
                                var m = world.LastMetrics;
                                curve.AppendLine(FormattableString.Invariant($"{world.Tick},{world.Tick * settings.FixedDeltaTime:F4},{metrics.WallSeconds:F4},{metrics.FirstArrived},{metrics.CurrentArrived},{navigation.PendingCount},{navigation.FailedCount},{metrics.LongestWait:F4},{m.TotalSimulationMilliseconds:F4},{m.PreferredMilliseconds:F4},{m.NeighborMilliseconds:F4},{m.AvoidanceMilliseconds:F4},{m.IntegrationMilliseconds:F4},{navigation.LastExpandedNodes},{solver.LastSafetyPairChecks},{solver.LastLimitedAgents}"));
                            }
                            if (metrics.CurrentArrived == count) { stop = "AllCurrentArrived"; break; }
                            if (metrics.WallSeconds >= wallCap) { stop = "WallCapIncomplete"; break; }
                        }
                    }
                    catch (Exception error) { stop = "Exception:" + error.GetType().Name; File.WriteAllText($"{folder}/error-{count}.txt", error.ToString()); }
                    File.WriteAllText($"{folder}/curve-{count}.csv", curve.ToString());
                    var arrivals = new StringBuilder("agent,first_ready_tick,first_arrival_tick\n");
                    for (int i = 0; i < count; i++) arrivals.AppendLine($"{i},{metrics.ReadyTick(i)},{metrics.ArrivalTick(i)}");
                    File.WriteAllText($"{folder}/agents-{count}.csv", arrivals.ToString());
                    Array.Sort(timings, 0, executed);
                    summary.AppendLine(FormattableString.Invariant($"{count},{settings.Backend},{stop},{executed},{executed * settings.FixedDeltaTime:F3},{metrics.WallSeconds:F3},{metrics.FirstArrived},{metrics.CurrentArrived},{metrics.NeverReady},{metrics.ReadyPercentile(0.5, settings.FixedDeltaTime):F3},{metrics.ReadyPercentile(0.95, settings.FixedDeltaTime):F3},{metrics.ReadyPercentile(1, settings.FixedDeltaTime):F3},{metrics.AllFirstArrivalSeconds:F3},{metrics.AllCurrentArrivalSeconds:F3},{metrics.LongestWait:F3},{Percentile(timings, executed, 0.5):F3},{Percentile(timings, executed, 0.95):F3},{Percentile(timings, executed, 0.99):F3},{metrics.ManagedBytes},{navigation.RequestCount},{navigation.RecoveryReplans},{metrics.LimitedAgentTicks},{collisions},{metrics.PortalCrossings}"));
                    File.WriteAllText(folder + "/matrix.txt", summary.ToString()); Debug.Log($"Phase 3 {count}: {stop}, first/current {metrics.FirstArrived}/{metrics.CurrentArrived}, tick {executed}");
                }
            }
        }
        private static double Percentile(double[] values, int count, double p) => count == 0 ? double.NaN : values[Math.Max(0, (int)Math.Ceiling(count * p) - 1)];
    }
}
