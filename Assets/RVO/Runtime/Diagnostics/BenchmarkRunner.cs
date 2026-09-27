using System;
using System.IO;
using Unity.Burst;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Rvo
{
    [Serializable]
    public sealed class BenchmarkReport
    {
        public string Utc, UnityVersion, OS, CPU, GPU, SourceRevision;
        public int SystemMemoryMB, Workers;
        public bool Editor, DevelopmentBuild, BurstEnabled, QualityCollected, FormalScaleRun, NeighborCoverageAdequate;
        public float ConservativeNeighborDistance;
        public string TimingDefinition = "Main-thread wall time, Schedule through Complete; quality/render/IO excluded. Stage fences only when MeasureStages=true.";
        public string AllocationDefinition = "GC.GetAllocatedBytesForCurrentThread in Step; excludes workers, diagnostics and presentation. Native capacity estimate excludes allocator/hash overhead.";
        public SimulationSettings Simulation;
        public ScenarioSettings Scenario;
        public BenchmarkSettings Benchmark;
        public double P50Ms, P95Ms, P99Ms, MeanPreferredMs, MeanNeighborMs, MeanAvoidanceMs, MeanIntegrationMs;
        public long MaxStepManagedBytes, EstimatedNativePayloadBytes;
        public int CollisionTicks, SweptPairTicks, FallbackAgentTicks, InfeasibleAgentTicks, InvalidAgentTicks;
        public int FinalArrived, FinalStalled, MaxBucketOccupancy, TruncatedAgentTicks, NumericViolationAgentTicks;
        public double MeanDeltaVelocity, MeanCandidates, MeanNeighbors, MeanMovingFraction;
        public float MinimumGap = float.PositiveInfinity, MaxConstraintViolation, MaxSuccessConstraintViolation;
        public double FirstAllArrivedSeconds = -1, MeanPathLength;
    }

    /// <summary>显式调用的纯仿真 runner；不会在启动/导入时自动执行规模压测。</summary>
    public static class BenchmarkRunner
    {
        public static BenchmarkReport Run(SimulationSettings settings, ScenarioSettings scenario,
            BenchmarkSettings benchmark, string directory, string sourceRevision)
        {
            settings.Validate(); scenario.Validate(settings.AgentCount); benchmark.Validate();
            if (benchmark.RenderAgents) throw new ArgumentException("纯仿真 runner 不绘图；端到端测量请使用演示与 Profiler。");
            Directory.CreateDirectory(directory);
            int sampleCount = checked(benchmark.MeasuredTicks * benchmark.Repetitions);
            var samples = new SimulationMetrics[sampleCount];
            var quality = new QualityMetrics[sampleCount];
            var candidates = new long[sampleCount]; var counts = new long[sampleCount];
            var stalled = new int[sampleCount]; var moving = new int[sampleCount];
            var report = new BenchmarkReport { Utc = DateTime.UtcNow.ToString("O"), UnityVersion = Application.unityVersion,
                OS = SystemInfo.operatingSystem, CPU = SystemInfo.processorType, GPU = SystemInfo.graphicsDeviceName,
                SystemMemoryMB = SystemInfo.systemMemorySize, Workers = JobsUtility.JobWorkerCount,
                Editor = Application.isEditor, DevelopmentBuild = Debug.isDebugBuild, BurstEnabled = BurstCompiler.IsEnabled,
                Simulation = settings, Scenario = scenario, Benchmark = benchmark, SourceRevision = sourceRevision,
                QualityCollected = benchmark.CollectQualityMetrics, FormalScaleRun = settings.AgentCount >= 100 };
            report.ConservativeNeighborDistance = 2 * scenario.MaxSpeed * math.max(settings.TimeHorizon, settings.FixedDeltaTime)
                + 2 * scenario.Radius + settings.Vo.SafetyMargin;
            report.NeighborCoverageAdequate = settings.NeighborDistance >= report.ConservativeNeighborDistance;
            int n = settings.AgentCount, k = settings.MaxNeighbors;
            report.EstimatedNativePayloadBytes = n * (4L + 6 * 12L + 12 + 4 + 16L) + n * (long)k * 8;
            if (settings.Avoidance == AvoidanceAlgorithm.ORCA) report.EstimatedNativePayloadBytes += n * (long)k * 16;
            if (settings.NeighborSearch == NeighborSearchAlgorithm.SpatialHash) report.EstimatedNativePayloadBytes += n * 12L;
            if (settings.Avoidance == AvoidanceAlgorithm.VO || settings.Avoidance == AvoidanceAlgorithm.RVO)
                report.EstimatedNativePayloadBytes += settings.Vo.AngleSamples * 8L;
            int cursor = 0;
            for (int repeat = 0; repeat < benchmark.Repetitions; repeat++)
            {
                var slowSeconds = new float[n]; var lengths = new double[n];
                using (var world = new SimulationWorld(settings, scenario, Phase1ModuleFactory.Create(settings)))
                {
                    for (int t = 0; t < benchmark.WarmupTicks; t++) world.Step();
                    for (int t = 0; t < benchmark.MeasuredTicks; t++, cursor++)
                    {
                        world.Step(); samples[cursor] = world.LastMetrics;
                        var state = world.Snapshot; var step = world.DebugSnapshot;
                        if (benchmark.CollectQualityMetrics)
                        {
                            quality[cursor] = QualityEvaluator.Evaluate(state, step, settings.Epsilon);
                            for (int i = 0; i < n; i++)
                            {
                                if (!math.all(math.isfinite(state.Positions[i])) || !math.all(math.isfinite(state.Velocities[i])) ||
                                    math.abs(state.Positions[i].y - settings.PlaneHeight) > settings.Epsilon || math.abs(state.Velocities[i].y) > settings.Epsilon ||
                                    math.length(state.Velocities[i]) > state.Parameters[i].MaxSpeed + 3e-5f) report.NumericViolationAgentTicks++;
                                bool arrived = math.distance(state.Positions[i], state.Goals[i]) <= state.Parameters[i].ArrivalDistance + settings.Epsilon;
                                slowSeconds[i] = !arrived && math.length(state.Velocities[i]) < 0.05f ? slowSeconds[i] + settings.FixedDeltaTime : 0;
                                if (slowSeconds[i] >= 2) stalled[cursor]++;
                                lengths[i] += math.distance(state.Positions[i], step.Inputs.Positions[i]);
                                if (settings.Avoidance == AvoidanceAlgorithm.ORCA)
                                    for (int slot = 0; slot < step.Neighbors.Counts[i]; slot++)
                                    {
                                        var plane = step.OrcaConstraints[i * k + slot];
                                        float violation = plane.Offset - math.dot(plane.Normal, state.Velocities[i].xz);
                                        report.MaxConstraintViolation = math.max(report.MaxConstraintViolation, violation);
                                        if (step.Status[i] == SolveStatus.Success)
                                            report.MaxSuccessConstraintViolation = math.max(report.MaxSuccessConstraintViolation, violation);
                                    }
                            }
                            if (quality[cursor].Arrived == n && report.FirstAllArrivedSeconds < 0)
                                report.FirstAllArrivedSeconds = world.Tick * (double)settings.FixedDeltaTime;
                        }
                        for (int i = 0; i < n; i++)
                        {
                            candidates[cursor] += step.Neighbors.CandidateCounts[i]; counts[cursor] += step.Neighbors.Counts[i];
                            report.MaxBucketOccupancy = math.max(report.MaxBucketOccupancy, step.Neighbors.BucketOccupancy[i]);
                            if (math.length(state.Velocities[i]) >= 0.05f) moving[cursor]++;
                        }
                    }
                }
                foreach (double length in lengths) report.MeanPathLength += length / (n * benchmark.Repetitions);
            }
            var times = new double[sampleCount];
            using (var csv = new StreamWriter(Path.Combine(directory, "ticks.csv")))
            {
                csv.WriteLine("sample,tick,total_ms,preferred_ms,neighbor_ms,avoidance_ms,integration_ms,main_thread_gc_bytes,arrived,swept_pairs,overlap_pairs,min_gap,fallback,infeasible,invalid,truncated,mean_dv,stalled_2s,candidates,neighbors,moving");
                for (int i = 0; i < sampleCount; i++)
                {
                    var m = samples[i]; var q = quality[i]; times[i] = m.TotalSimulationMilliseconds;
                    report.MaxStepManagedBytes = Math.Max(report.MaxStepManagedBytes, m.ManagedAllocatedBytes);
                    report.MeanPreferredMs += m.PreferredMilliseconds / sampleCount; report.MeanNeighborMs += m.NeighborMilliseconds / sampleCount;
                    report.MeanAvoidanceMs += m.AvoidanceMilliseconds / sampleCount; report.MeanIntegrationMs += m.IntegrationMilliseconds / sampleCount;
                    if (q.SweptCollisionPairs > 0) report.CollisionTicks++;
                    report.SweptPairTicks += q.SweptCollisionPairs; report.FallbackAgentTicks += q.FallbackAgents;
                    report.InfeasibleAgentTicks += q.InfeasibleAgents; report.InvalidAgentTicks += q.InvalidAgents;
                    report.TruncatedAgentTicks += q.TruncatedAgents; report.MinimumGap = math.min(report.MinimumGap, q.MinimumSeparation);
                    report.MeanDeltaVelocity += q.MeanSpeedChange / sampleCount;
                    report.MeanCandidates += candidates[i] / (double)(n * sampleCount);
                    report.MeanNeighbors += counts[i] / (double)(n * sampleCount);
                    report.MeanMovingFraction += moving[i] / (double)(n * sampleCount);
                    csv.WriteLine(FormattableString.Invariant($"{i},{m.Tick},{m.TotalSimulationMilliseconds:R},{m.PreferredMilliseconds:R},{m.NeighborMilliseconds:R},{m.AvoidanceMilliseconds:R},{m.IntegrationMilliseconds:R},{m.ManagedAllocatedBytes},{q.Arrived},{q.SweptCollisionPairs},{q.OverlappingPairs},{q.MinimumSeparation:R},{q.FallbackAgents},{q.InfeasibleAgents},{q.InvalidAgents},{q.TruncatedAgents},{q.MeanSpeedChange:R},{stalled[i]},{candidates[i]},{counts[i]},{moving[i]}"));
                }
            }
            report.FinalArrived = quality[sampleCount - 1].Arrived; report.FinalStalled = stalled[sampleCount - 1];
            Array.Sort(times); report.P50Ms = Percentile(times, 0.50); report.P95Ms = Percentile(times, 0.95); report.P99Ms = Percentile(times, 0.99);
            if (!benchmark.CollectQualityMetrics)
            {
                report.CollisionTicks = report.SweptPairTicks = report.FallbackAgentTicks = report.InfeasibleAgentTicks = report.InvalidAgentTicks = -1;
                report.FinalArrived = report.FinalStalled = report.NumericViolationAgentTicks = report.TruncatedAgentTicks = -1;
                report.MinimumGap = -1; report.MeanDeltaVelocity = report.MeanPathLength = -1;
            }
            if (!benchmark.CollectQualityMetrics || settings.Avoidance != AvoidanceAlgorithm.ORCA)
                report.MaxConstraintViolation = report.MaxSuccessConstraintViolation = -1;
            if (!float.IsFinite(report.MinimumGap)) report.MinimumGap = -1; // N=1 无 pair，结合 N 解读。
            if (!settings.MeasureStages) report.MeanPreferredMs = report.MeanNeighborMs = report.MeanAvoidanceMs = report.MeanIntegrationMs = -1;
            File.WriteAllText(Path.Combine(directory, "summary.json"), JsonUtility.ToJson(report, true));
            return report;
        }
        private static double Percentile(double[] sorted, double fraction) => sorted[(int)Math.Ceiling((sorted.Length - 1) * fraction)];
    }
}
