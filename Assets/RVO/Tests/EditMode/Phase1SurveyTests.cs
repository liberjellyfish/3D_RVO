using System;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;

namespace Rvo.Tests
{
    public sealed class Phase1SurveyTests
    {
        private static readonly string Revision = Rvo.Editor.Phase1ValidationMenu.SourceFingerprint();
        [TestCase(AvoidanceAlgorithm.None)]
        [TestCase(AvoidanceAlgorithm.VO)]
        [TestCase(AvoidanceAlgorithm.RVO)]
        [TestCase(AvoidanceAlgorithm.ORCA)]
        public void SameScenarioQualityMatrix(AvoidanceAlgorithm algorithm)
        {
            var kinds = new[] { ScenarioKind.SingleAgent, ScenarioKind.HeadOnPair, ScenarioKind.Crossing,
                ScenarioKind.CircleSwap, ScenarioKind.OpposingGroups, ScenarioKind.RandomCrowd };
            var counts = new[] { 1, 2, 4, 16, 32, 32 };
            var extents = new[] { 3f, 6f, 8f, 10f, 12f, 10f };
            foreach (float bias in new[] { 0f, 0.05f })
            for (int i = 0; i < kinds.Length; i++)
            {
                var s = Phase13Tests.Settings(counts[i]); s.Avoidance = algorithm; s.Backend = ExecutionBackend.JobsBurst;
                s.PreferredSideBias = bias; s.NeighborSearch = NeighborSearchAlgorithm.SpatialHash;
                var scenario = ScenarioSettings.Default; scenario.Kind = kinds[i]; scenario.Extent = extents[i];
                var benchmark = new BenchmarkSettings { MeasuredTicks = 900, Repetitions = 1, CollectQualityMetrics = true };
                string name = $"{kinds[i]}_{algorithm}_bias{(bias == 0 ? "0" : "005")}";
                var report = BenchmarkRunner.Run(s, scenario, benchmark, Path.Combine("Verification/Phase1/Quality", name), Revision);
                Assert.That(report.FormalScaleRun, Is.False);
                Assert.That(report.NumericViolationAgentTicks, Is.Zero);
                Assert.That(report.InvalidAgentTicks, Is.Zero);
                Assert.That(report.MaxSuccessConstraintViolation, Is.LessThan(4e-5));
                // 群体结果是调查，不以隐藏失败的宽松断言声称无碰撞。
                if (algorithm == AvoidanceAlgorithm.ORCA && (kinds[i] == ScenarioKind.HeadOnPair || kinds[i] == ScenarioKind.Crossing))
                    Assert.That(report.CollisionTicks, Is.Zero);
                TestContext.WriteLine(FormattableString.Invariant($"{name}: arrived={report.FinalArrived}/{counts[i]}, collisionTicks={report.CollisionTicks}, minGap={report.MinimumGap:F6}, meanDv={report.MeanDeltaVelocity:F6}, stalled={report.FinalStalled}, infeasible={report.InfeasibleAgentTicks}"));
            }
        }

        [Test]
        public void SmallPerformanceComparisonKeepsInputsAndQualityFixed()
        {
            foreach (var algorithm in new[] { AvoidanceAlgorithm.VO, AvoidanceAlgorithm.RVO, AvoidanceAlgorithm.ORCA })
            foreach (var backend in new[] { ExecutionBackend.Reference, ExecutionBackend.JobsBurst })
            foreach (bool stages in new[] { false, true })
            {
                var s = Phase13Tests.Settings(32); s.Avoidance = algorithm; s.Backend = backend; s.MeasureStages = stages; s.PreferredSideBias = 0.05f;
                var scenario = ScenarioSettings.Default; scenario.Kind = ScenarioKind.OpposingGroups; scenario.Extent = 12;
                var b = new BenchmarkSettings { WarmupTicks = 90, MeasuredTicks = 180, Repetitions = 3, CollectQualityMetrics = true };
                string name = $"{algorithm}_{backend}_stages{stages}";
                var r = BenchmarkRunner.Run(s, scenario, b, Path.Combine("Verification/Phase1/Performance", name), Revision);
                Assert.That(r.NumericViolationAgentTicks, Is.Zero); Assert.That(r.FormalScaleRun, Is.False);
                Assert.That(r.MaxStepManagedBytes, Is.Zero, "Steady simulation path allocated managed memory");
                TestContext.WriteLine(FormattableString.Invariant($"{name}: p50={r.P50Ms:F4} p95={r.P95Ms:F4} p99={r.P99Ms:F4} ms, allocated={r.MaxStepManagedBytes}"));
            }
        }

        [Test]
        public void SmallHashComparisonReportsSparseAndDenseCandidateCosts()
        {
            foreach (bool dense in new[] { false, true })
            foreach (var backend in new[] { ExecutionBackend.Reference, ExecutionBackend.JobsBurst })
            foreach (var search in new[] { NeighborSearchAlgorithm.BruteForce, NeighborSearchAlgorithm.SpatialHash })
            {
                var s = Phase13Tests.Settings(64); s.Avoidance = AvoidanceAlgorithm.ORCA; s.Backend = backend;
                s.NeighborSearch = search; s.NeighborDistance = 4; s.CellSize = 2; s.TimeHorizon = 0.5f; s.MaxNeighbors = 12;
                s.MeasureStages = true; s.PreferredSideBias = 0.05f;
                var scenario = ScenarioSettings.Default; scenario.Kind = ScenarioKind.RandomCrowd; scenario.Extent = dense ? 5 : 30;
                var b = new BenchmarkSettings { WarmupTicks = 20, MeasuredTicks = 120, Repetitions = 3, CollectQualityMetrics = true };
                string name = $"Hash64_{backend}_{search}_dense{dense}";
                var r = BenchmarkRunner.Run(s, scenario, b, Path.Combine("Verification/Phase1/Performance", name), Revision);
                Assert.That(r.NumericViolationAgentTicks, Is.Zero); Assert.That(r.MaxStepManagedBytes, Is.Zero);
                TestContext.WriteLine(FormattableString.Invariant($"{name}: neighborMs={r.MeanNeighborMs:F4}, candidates={r.MeanCandidates:F2}, bucket={r.MaxBucketOccupancy}, collisions={r.CollisionTicks}, truncation={r.TruncatedAgentTicks}"));
            }
        }
    }
}
