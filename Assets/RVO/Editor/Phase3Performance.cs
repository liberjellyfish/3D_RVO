using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Rvo.Editor
{
    public static class Phase3Performance
    {
        [MenuItem("Tools/RVO/Profile Phase 3 startup (1024 agents)")]
        public static void RunStartup()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play before standalone profiling.");
            var profile = AssetDatabase.LoadAssetAtPath<SimulationProfile>(Phase3DemoBuilder.ProfilePath);
            var map = profile.BakedVolume.Load(profile.Volume, profile.Scenario.VolumeClearanceRadius);
            var settings = profile.Simulation; settings.AgentCount = 16;
            var warmModules = Phase3ModuleFactory.Create(settings, profile.Scenario, profile.Volume, map, out _, out _);
            using (var warm = new SimulationWorld(settings, profile.Scenario, warmModules)) warm.Step();
            // 重载地图，避免预热世界掩盖共享索引的首次构建成本。
            map = profile.BakedVolume.Load(profile.Volume, profile.Scenario.VolumeClearanceRadius);
            settings.AgentCount = 1024;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var modules = Phase3ModuleFactory.Create(settings, profile.Scenario, profile.Volume, map, out var navigation, out var solver);
            using (var world = new SimulationWorld(settings, profile.Scenario, modules))
            {
                double setup = timer.Elapsed.TotalMilliseconds;
                var metrics = new VolumeRunMetrics(settings.AgentCount);
                var times = new System.Collections.Generic.List<double>(); long expanded = 0;
                do
                {
                    world.Step(); metrics.Observe(world, navigation, solver);
                    times.Add(world.LastMetrics.TotalSimulationMilliseconds); expanded += navigation.LastExpandedNodes;
                } while (metrics.NeverReady > 0 && world.Tick < 6000 && timer.Elapsed.TotalSeconds < 90);
                times.Sort();
                string folder = "Documentation/RVO/Verification/Phase3/Startup"; Directory.CreateDirectory(folder);
                bool sync = Array.IndexOf(Environment.GetCommandLineArgs(), "--burst-force-sync-compilation") >= 0;
                File.WriteAllText(folder + "/environment.txt", $"Unity {Application.unityVersion}; CPU {SystemInfo.processorType}; seed {profile.Scenario.Seed}; obstacles {map.ObstacleCount}; backend {settings.Backend}; Burst sync={sync}\n" +
                    "No rendering; separate warmup world; cold landmark tables; map decode and Burst compilation excluded; setup included in wall time.\n");
                string report = "agents,ticks,ready_p50_sim_s,ready_p95_sim_s,ready_max_sim_s,setup_ms,wall_including_setup_ms,tick_p95_ms,tick_max_ms,expanded,never_ready,failed,fallbacks\n" +
                    FormattableString.Invariant($"1024,{world.Tick},{metrics.ReadyPercentile(0.5,settings.FixedDeltaTime):F3},{metrics.ReadyPercentile(0.95,settings.FixedDeltaTime):F3},{metrics.ReadyPercentile(1,settings.FixedDeltaTime):F3},{setup:F3},{timer.Elapsed.TotalMilliseconds:F3},{times[(int)(times.Count*0.95)]:F3},{times[times.Count-1]:F3},{expanded},{metrics.NeverReady},{navigation.FailedCount},{navigation.CoarseFallbacks}\n");
                File.WriteAllText(folder + "/summary.csv", report); Debug.Log(report);
            }
        }
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
            var map = profile.BakedVolume.Load(profile.Volume, profile.Scenario.VolumeClearanceRadius);
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
