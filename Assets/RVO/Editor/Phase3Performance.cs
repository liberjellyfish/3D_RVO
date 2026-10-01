using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Rvo.Editor
{
    public static class Phase3Performance
    {
        public static void RunDenseDemo()
        {
            var profile = AssetDatabase.LoadAssetAtPath<SimulationProfile>(Phase3DemoBuilder.ProfilePath);
            Phase3DemoBuilder.Bake(profile);
            Run();
            Phase3Validation.RunMatrix();
        }
        // Fixed workload, including initial route requests; synchronous Burst compilation is a CLI flag.
        [MenuItem("Tools/RVO/Profile Phase 3 (600 ticks per tier)")]
        public static void Run()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play before standalone profiling.");
            var profile = AssetDatabase.LoadAssetAtPath<SimulationProfile>(Phase3DemoBuilder.ProfilePath);
            var map = profile.BakedVolume.Load(profile.Volume, profile.Scenario.Radius);
            string folder = "Documentation/RVO/Verification/Phase3/Performance";
            Directory.CreateDirectory(folder);
            var warmSettings = profile.Simulation; warmSettings.AgentCount = 16;
            var warmModules = Phase3ModuleFactory.Create(warmSettings, profile.Scenario, profile.Volume, map, out _, out _);
            using (var warm = new SimulationWorld(warmSettings, profile.Scenario, warmModules)) warm.Step();
            bool sync = Array.IndexOf(Environment.GetCommandLineArgs(), "--burst-force-sync-compilation") >= 0;
            var report = new StringBuilder($"Unity {Application.unityVersion}; {SystemInfo.processorType}; Burst sync={sync}; separate warmup world; no rendering; seed {profile.Scenario.Seed}; obstacles {map.ObstacleCount}\n");
            report.AppendLine("agents,ticks,p50_ms,p95_ms,p99_ms,navigation_mean_ms,neighbor_mean_ms,avoidance_mean_ms,expanded,requests,coarse,fallbacks,never_ready,first_arrived,gc_bytes");
            foreach (int count in new[] { 16, 256, 1024 })
            {
                var settings = profile.Simulation; settings.AgentCount = count; settings.MeasureStages = true;
                var modules = Phase3ModuleFactory.Create(settings, profile.Scenario, profile.Volume, map, out var navigation, out var solver);
                using (var world = new SimulationWorld(settings, profile.Scenario, modules))
                {
                    const int ticks = 600;
                    var times = new double[ticks]; double nav = 0, neighbor = 0, avoidance = 0; long expanded = 0, gc = 0;
                    var metrics = new VolumeRunMetrics(count);
                    var curve = new StringBuilder("tick,total_ms,navigation_ms,neighbor_ms,avoidance_ms,expanded,pending,coarse_fallbacks\n");
                    for (int t = 0; t < ticks; t++)
                    {
                        world.Step(); metrics.Observe(world, navigation, solver);
                        var m = world.LastMetrics; times[t] = m.TotalSimulationMilliseconds;
                        nav += m.PreferredMilliseconds; neighbor += m.NeighborMilliseconds; avoidance += m.AvoidanceMilliseconds;
                        expanded += navigation.LastExpandedNodes; gc += m.ManagedAllocatedBytes;
                        curve.AppendLine(FormattableString.Invariant($"{t + 1},{times[t]:F4},{m.PreferredMilliseconds:F4},{m.NeighborMilliseconds:F4},{m.AvoidanceMilliseconds:F4},{navigation.LastExpandedNodes},{navigation.PendingCount},{navigation.CoarseFallbacks}"));
                    }
                    Array.Sort(times);
                    report.AppendLine(FormattableString.Invariant($"{count},{ticks},{times[299]:F4},{times[569]:F4},{times[593]:F4},{nav/ticks:F4},{neighbor/ticks:F4},{avoidance/ticks:F4},{expanded},{navigation.RequestCount},{navigation.CoarsePaths},{navigation.CoarseFallbacks},{metrics.NeverReady},{metrics.FirstArrived},{gc}"));
                    File.WriteAllText($"{folder}/curve-{count}.csv", curve.ToString());
                    File.WriteAllText(folder + "/summary.csv", report.ToString());
                    Debug.Log($"Phase 3 performance: {count}, p95 {times[569]:F3} ms");
                }
            }
        }
    }
}
