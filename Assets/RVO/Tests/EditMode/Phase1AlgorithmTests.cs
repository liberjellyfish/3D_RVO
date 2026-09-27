using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Rvo.Tests
{
    public sealed class Phase1AlgorithmTests
    {
        [Test]
        public void RvoUsesReciprocalObstacleRatherThanAveragingTheOutput()
        {
            float2 p = new float2(5, 0), candidate = new float2(0.5f, 0), self = new float2(1, 0), other = float2.zero;
            Assert.That(VelocityObstacle2D.Contains(p, ReciprocalVelocityObstacle2D.Relative(candidate, self, other, false), 1, 10));
            Assert.That(VelocityObstacle2D.Contains(p, ReciprocalVelocityObstacle2D.Relative(candidate, self, other, true), 1, 10), Is.False);
            Assert.That(ReciprocalVelocityObstacle2D.Relative(self, self, other, true), Is.EqualTo(self - other));
        }

        [Test]
        public void OrcaCircleConstraintSplitsResponsibilityAndUsesHorizon()
        {
            var a = OrcaGeometry2D.Build(new float2(10, 0), float2.zero, float2.zero, 1, 5, 0.1f, 1, 2, 1e-5f);
            var b = OrcaGeometry2D.Build(new float2(-10, 0), float2.zero, float2.zero, 1, 5, 0.1f, 2, 1, 1e-5f);
            Assert.That(math.distance(a.Normal, new float2(-1, 0)), Is.LessThan(1e-6));
            Assert.That(a.Offset, Is.EqualTo(-0.9f).Within(1e-6));
            Assert.That(math.distance(a.Normal, -b.Normal), Is.LessThan(1e-6));
            Assert.That(a.Offset, Is.EqualTo(b.Offset));
            var differentDt = OrcaGeometry2D.Build(new float2(10, 0), float2.zero, float2.zero, 1, 5, 0.01f, 1, 2, 1e-5f);
            Assert.That(a.Offset, Is.EqualTo(differentDt.Offset));
        }

        [Test]
        public void OrcaLegConstraintsAreReciprocalAndTangentToCollisionCone()
        {
            foreach (float side in new[] { -0.1f, 0.1f })
            {
                float2 v = new float2(2, side), p = new float2(4, 0);
                var a = OrcaGeometry2D.Build(p, v, -v, 1, 5, 0.1f, 1, 2, 1e-5f);
                var b = OrcaGeometry2D.Build(-p, -v, v, 1, 5, 0.1f, 2, 1, 1e-5f);
                Assert.That(math.distance(a.Normal, -b.Normal), Is.LessThan(1e-5));
                Assert.That(a.Offset, Is.EqualTo(b.Offset).Within(1e-5));
                float2 corrected = v + (a.Offset - math.dot(a.Normal, v)) * a.Normal;
                float2 relative = 2 * corrected;
                float closest = math.length(p - relative * (math.dot(p, relative) / math.lengthsq(relative)));
                Assert.That(closest, Is.EqualTo(1).Within(1e-5));
            }
        }

        [Test]
        public void OverlapUsesTimestepAndCoincidentIdsChooseOppositeNormals()
        {
            var a = OrcaGeometry2D.Build(float2.zero, float2.zero, float2.zero, 1, 5, 0.1f, 1, 2, 1e-5f);
            var b = OrcaGeometry2D.Build(float2.zero, float2.zero, float2.zero, 1, 5, 0.1f, 2, 1, 1e-5f);
            Assert.That(math.distance(a.Normal, -b.Normal), Is.Zero);
            Assert.That(a.Offset, Is.EqualTo(5).Within(1e-6));
            var zeroW = OrcaGeometry2D.Build(new float2(0.5f, 0), new float2(5, 0), float2.zero, 1, 5, 0.1f, 1, 2, 1e-5f);
            Assert.That(math.all(math.isfinite(zeroW.Normal)), Is.True);
        }

        [Test]
        public void OptimizerHandlesCircleParallelConflictAndInvalidInput()
        {
            using (var storage = new NativeArray<VelocityHalfPlane2D>(2, Allocator.Temp))
            {
                var p = storage;
                p[0] = Plane(1, 0, 0.5f); p[1] = Plane(1, 0, 0.7f);
                Assert.That(PlanarVelocityOptimizer.Solve(p, new float2(-2, 1), 1, 1e-5f, out var v), Is.EqualTo(SolveStatus.Success));
                Assert.That(v.x, Is.EqualTo(0.7f).Within(1e-5)); Assert.That(math.length(v), Is.LessThanOrEqualTo(1.00001f));
                p[1] = Plane(-1, 0, 0.5f);
                Assert.That(PlanarVelocityOptimizer.Solve(p, float2.zero, 1, 1e-5f, out v), Is.EqualTo(SolveStatus.Infeasible));
                Assert.That(math.abs(v.x), Is.LessThan(2e-5));
                p[0] = Plane(1, 0, 4); p[1] = Plane(1, 0, 4);
                Assert.That(PlanarVelocityOptimizer.Solve(p, float2.zero, 1, 1e-5f, out v), Is.EqualTo(SolveStatus.Infeasible));
                Assert.That(v.x, Is.EqualTo(1).Within(2e-5));
                p[0] = Plane(0, 0, 0);
                Assert.That(PlanarVelocityOptimizer.Solve(p, float2.zero, 1, 1e-5f, out v), Is.EqualTo(SolveStatus.InvalidInput));
            }
            using (var empty = new NativeArray<VelocityHalfPlane2D>(0, Allocator.Temp))
            {
                Assert.That(PlanarVelocityOptimizer.Solve(empty, new float2(3, 4), 2, 1e-5f, out var v), Is.EqualTo(SolveStatus.Success));
                Assert.That(math.distance(v, new float2(1.2f, 1.6f)), Is.LessThan(1e-5));
            }
        }

        [Test]
        public void OptimizerMatchesIndependentBoundaryEnumeration()
        {
            var random = new Unity.Mathematics.Random(9127);
            using (var p = new NativeArray<VelocityHalfPlane2D>(12, Allocator.Temp))
                for (int trial = 0; trial < 200; trial++)
                {
                    float2 witness = random.NextFloat2(-0.5f, 0.5f);
                    for (int i = 0; i < p.Length; i++)
                    {
                        float angle = random.NextFloat(-math.PI, math.PI);
                        float2 n = new float2(math.cos(angle), math.sin(angle));
                        var writable = p;
                        writable[i] = new VelocityHalfPlane2D { Normal = n, Offset = math.dot(n, witness) - random.NextFloat(0.01f, 1) };
                    }
                    float2 preferred = random.NextFloat2(-3, 3);
                    Assert.That(PlanarVelocityOptimizer.Solve(p, preferred, 2, 1e-6f, out var actual), Is.EqualTo(SolveStatus.Success));
                    float2 expected = EnumerateOptimum(p, preferred, 2);
                    Assert.That(math.distance(actual, expected), Is.LessThan(2e-4f), $"trial {trial}");
                    foreach (var plane in p) Assert.That(math.dot(plane.Normal, actual), Is.GreaterThanOrEqualTo(plane.Offset - 3e-5));
                }
        }

        // 独立参考：枚举投影、圆/线交点和线/线交点；不调用增量求解器。
        private static float2 EnumerateOptimum(NativeArray<VelocityHalfPlane2D> p, float2 preferred, float speed)
        {
            var candidates = new List<float2> { preferred, math.normalizesafe(preferred) * speed, float2.zero };
            for (int i = 0; i < p.Length; i++)
            {
                var a = p[i]; float2 center = a.Normal * a.Offset;
                float2 tangent = new float2(-a.Normal.y, a.Normal.x);
                candidates.Add(preferred + (a.Offset - math.dot(a.Normal, preferred)) * a.Normal);
                if (math.abs(a.Offset) <= speed)
                {
                    float t = math.sqrt(speed * speed - a.Offset * a.Offset);
                    candidates.Add(center + t * tangent); candidates.Add(center - t * tangent);
                }
                for (int j = 0; j < i; j++)
                {
                    var b = p[j]; double det = (double)a.Normal.x * b.Normal.y - (double)a.Normal.y * b.Normal.x;
                    if (Math.Abs(det) < 1e-8) continue;
                    candidates.Add(new float2((float)((a.Offset * (double)b.Normal.y - b.Offset * (double)a.Normal.y) / det),
                        (float)((a.Normal.x * (double)b.Offset - b.Normal.x * (double)a.Offset) / det)));
                }
            }
            float best = float.PositiveInfinity; float2 result = float2.zero;
            foreach (var v in candidates)
            {
                if (math.length(v) > speed + 1e-5) continue;
                bool valid = true;
                foreach (var plane in p) if (math.dot(plane.Normal, v) < plane.Offset - 1e-5) valid = false;
                if (valid && math.distancesq(v, preferred) < best) { best = math.distancesq(v, preferred); result = v; }
            }
            Assert.That(float.IsFinite(best)); return result;
        }
        private static VelocityHalfPlane2D Plane(float x, float y, float offset) =>
            new VelocityHalfPlane2D { Normal = new float2(x, y), Offset = offset };

        [TestCase(ExecutionBackend.Reference)]
        [TestCase(ExecutionBackend.JobsBurst)]
        public void HashExactlyMatchesBruteForceAcrossCellsSeedsTiesAndTruncation(ExecutionBackend backend)
        {
            foreach (float cell in new[] { 0.25f, 1f, 3f, 10f })
            foreach (float range in new[] { 0.5f, 1f, 4.5f })
            for (uint seed = 1; seed <= 4; seed++)
            {
                var random = new Unity.Mathematics.Random(seed);
                var points = new float3[32]; var ids = new int[32];
                for (int i = 0; i < points.Length; i++) { points[i] = new float3(random.NextInt(-8, 9) * 0.5f, 0, random.NextInt(-8, 9) * 0.5f); ids[i] = 200 - i * 3; }
                var s = Phase13Tests.Settings(32); s.Backend = backend; s.CellSize = cell; s.NeighborDistance = range; s.MaxNeighbors = 5;
                using (var brute = Phase13Tests.CustomWorld(s, points, points, ids))
                {
                    s.NeighborSearch = NeighborSearchAlgorithm.SpatialHash;
                    using (var hash = Phase13Tests.CustomWorld(s, points, points, ids))
                    {
                        brute.Step(); hash.Step(); var a = brute.DebugSnapshot.Neighbors; var b = hash.DebugSnapshot.Neighbors;
                        for (int i = 0; i < 32; i++)
                        {
                            Assert.That(b.Counts[i], Is.EqualTo(a.Counts[i])); Assert.That(b.DroppedCounts[i], Is.EqualTo(a.DroppedCounts[i]));
                            for (int k = 0; k < a.Counts[i]; k++)
                            { int slot = i * 5 + k; Assert.That(b.Indices[slot], Is.EqualTo(a.Indices[slot])); Assert.That(b.DistanceSquared[slot], Is.EqualTo(a.DistanceSquared[slot])); }
                        }
                    }
                }
            }
        }

        [TestCase(AvoidanceAlgorithm.None)]
        [TestCase(AvoidanceAlgorithm.VO)]
        [TestCase(AvoidanceAlgorithm.RVO)]
        [TestCase(AvoidanceAlgorithm.ORCA)]
        public void JobsMatchReferenceAndWorldMayDisposeWhileScheduled(AvoidanceAlgorithm algorithm)
        {
            var s = Phase13Tests.Settings(8); s.Avoidance = algorithm; s.PreferredSideBias = 0.05f;
            var scenario = ScenarioSettings.Default; scenario.Extent = 8;
            using (var reference = new SimulationWorld(s, scenario, Phase1ModuleFactory.Create(s)))
            {
                s.Backend = ExecutionBackend.JobsBurst; s.NeighborSearch = NeighborSearchAlgorithm.SpatialHash;
                using (var jobs = new SimulationWorld(s, scenario, Phase1ModuleFactory.Create(s)))
                {
                    for (int t = 0; t < 240; t++)
                    {
                        reference.Step(); jobs.Step();
                        for (int i = 0; i < 8; i++)
                            Assert.That(math.distance(reference.Snapshot.Positions[i], jobs.Snapshot.Positions[i]), Is.LessThan(0.005f), $"{algorithm}, tick {t}");
                    }
                    jobs.ScheduleStep();
                    Assert.Throws<InvalidOperationException>(() => { var ignored = jobs.Snapshot; });
                }
            }
        }

        [TestCase(ExecutionBackend.Reference)]
        [TestCase(ExecutionBackend.JobsBurst)]
        public void OrcaRecoversCoincidentAgentsWithoutPositionProjection(ExecutionBackend backend)
        {
            var s = Phase13Tests.Settings(2); s.Avoidance = AvoidanceAlgorithm.ORCA; s.Backend = backend;
            var positions = new[] { float3.zero, float3.zero };
            using (var world = Phase13Tests.CustomWorld(s, positions, new[] { new float3(-5, 0, 0), new float3(5, 0, 0) }))
            {
                for (int t = 0; t < 40; t++)
                {
                    world.Step();
                    for (int i = 0; i < 2; i++)
                    {
                        Assert.That(math.length(world.Snapshot.Velocities[i]), Is.LessThanOrEqualTo(2.00001f));
                        Assert.That(math.distance(world.Snapshot.Positions[i], world.DebugSnapshot.Inputs.Positions[i] + world.Snapshot.Velocities[i] * s.FixedDeltaTime), Is.LessThan(1e-6));
                    }
                    if (t == 0) Assert.That(world.DebugSnapshot.Status[0], Is.EqualTo(SolveStatus.Infeasible));
                }
                Assert.That(math.distance(world.Snapshot.Positions[0], world.Snapshot.Positions[1]), Is.GreaterThan(0.7f));
            }
        }
    }
}
