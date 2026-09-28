using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;

namespace Rvo.Tests
{
    public sealed class Phase2ThroughputTests
    {
        [TestCase(16, NeighborSearchAlgorithm.SpatialHash)]
        [TestCase(256, NeighborSearchAlgorithm.SpatialHash)]
        [TestCase(1024, NeighborSearchAlgorithm.SpatialHash)]
        [TestCase(1024, NeighborSearchAlgorithm.KdTree)]
        public void RecordLargeMapThroughput(int count, NeighborSearchAlgorithm query)
        {
            var settings = SimulationSettings.Default;
            settings.AgentCount = count; settings.MaxNeighbors = 48;
            settings.Avoidance = AvoidanceAlgorithm.ORCA;
            settings.NeighborSearch = query;
            settings.Backend = ExecutionBackend.JobsBurst;
            settings.NeighborDistance = 56; settings.CellSize = 24;
            settings.TimeHorizon = 2; settings.PreferredSideBias = 0.08f;
            settings.Vo.SafetyMargin = 0.08f;
            var scenario = ScenarioSettings.Default;
            scenario.Radius = 2.5f; scenario.MaxSpeed = 12; scenario.ArrivalDistance = 0.5f;
            var config = NavigationSettings.Default;
            var map = NavigationBake.Generate(config, scenario.Radius);
            var rows = new System.Text.StringBuilder("tick,pending,arrived,moving,limited_ticks,expanded,total_expanded,step_ms\n");
            var times = new double[360];
            using (var world = new SimulationWorld(settings, scenario,
                Phase2ModuleFactory.Create(settings, scenario, config, map, out var navigation, out var solver)))
            {
                for (int tick = 0; tick < times.Length; tick++)
                {
                    var watch = Stopwatch.StartNew(); world.Step(); watch.Stop();
                    times[tick] = watch.Elapsed.TotalMilliseconds;
                    Assert.That(navigation.LastExpandedNodes, Is.LessThanOrEqualTo(config.PathExpansionsPerTick));
                    int moving = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (math.lengthsq(world.Snapshot.Velocities[i]) > 0.01f) moving++;
                        Assert.That(map.SegmentClear(world.DebugSnapshot.Inputs.Positions[i].xz,
                            world.Snapshot.Positions[i].xz, scenario.Radius), Is.True);
                    }
                    if (tick % 30 == 0 || tick == times.Length - 1)
                        Assert.That(QualityEvaluator.Evaluate(world.Snapshot, world.DebugSnapshot, settings.Epsilon).SweptCollisionPairs, Is.Zero);
                    rows.AppendLine(FormattableString.Invariant($"{tick+1},{navigation.PendingCount},{navigation.ArrivedCount},{moving},{solver.SafetyLimitedTicks},{navigation.LastExpandedNodes},{navigation.TotalExpandedNodes},{times[tick]:F4}"));
                }
            }
            Directory.CreateDirectory("Documentation/RVO/Verification/Phase2Optimization");
            File.WriteAllText($"Documentation/RVO/Verification/Phase2Optimization/throughput-{count}-{query}.csv", rows.ToString());
            System.Array.Sort(times, 30, times.Length - 30);
            TestContext.WriteLine($"N={count}, steady P50={times[195]:F3}ms, P95={times[343]:F3}ms (Editor, excluding quality checks)");
        }

        [Test]
        public void RecordStageCosts()
        {
            var settings = SimulationSettings.Default; settings.AgentCount = 1024; settings.MaxNeighbors = 48;
            settings.Avoidance = AvoidanceAlgorithm.ORCA; settings.NeighborSearch = NeighborSearchAlgorithm.KdTree;
            settings.Backend = ExecutionBackend.JobsBurst; settings.MeasureStages = true;
            settings.NeighborDistance = 56; settings.CellSize = 24; settings.TimeHorizon = 2; settings.PreferredSideBias = 0.08f;
            var scenario = ScenarioSettings.Default; scenario.Radius = 2.5f; scenario.MaxSpeed = 12; scenario.ArrivalDistance = 0.5f;
            var config = NavigationSettings.Default; var map = NavigationBake.Generate(config,scenario.Radius);
            var watch = Stopwatch.StartNew();
            using (var world = new SimulationWorld(settings,scenario,
                Phase2ModuleFactory.Create(settings,scenario,config,map,out var navigation,out _)))
            {
                long setup = watch.ElapsedMilliseconds;
                var rows = new System.Text.StringBuilder("tick,preferred_ms,neighbors_ms,avoidance_ms,integration_ms,total_ms,gc_bytes,pending\n");
                for (int tick = 0; tick < 600; tick++)
                {
                    world.Step(); var m = world.LastMetrics;
                    rows.AppendLine(FormattableString.Invariant($"{tick+1},{m.PreferredMilliseconds:F4},{m.NeighborMilliseconds:F4},{m.AvoidanceMilliseconds:F4},{m.IntegrationMilliseconds:F4},{m.TotalSimulationMilliseconds:F4},{m.ManagedAllocatedBytes},{navigation.PendingCount}"));
                }
                Directory.CreateDirectory("Documentation/RVO/Verification/Phase2Optimization");
                File.WriteAllText("Documentation/RVO/Verification/Phase2Optimization/stages.csv",rows.ToString());
                File.WriteAllText("Documentation/RVO/Verification/Phase2Optimization/setup.txt",$"Cold world/landmarks setup excluding map bake: {setup} ms\n");
            }
        }
    }
}
