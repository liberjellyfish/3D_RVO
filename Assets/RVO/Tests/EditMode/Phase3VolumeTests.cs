using System;
using System.Collections.Generic;
using NUnit.Framework;
using Rvo.Editor;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Rvo.Tests
{
    public sealed class Phase3VolumeTests
    {
        private static VolumeSettings Settings(int n = 12)
        { var s = VolumeSettings.Default; s.Resolution = n; s.SearchCapacity = 8192; return s; }
        private static NavigationVolume Empty(int n = 12) => VolumeBake.Bake(Settings(n), 0.2f, Array.Empty<VolumeBox>());

        [Test]
        public void BakeRoundTripChecksRadiusAndIntegrity()
        {
            var settings = Settings(); var map = VolumeBake.Bake(settings, 0.2f, new[] { new VolumeBox(new float3(-1, -2, -2), new float3(1, 2, 2)) });
            byte[] bytes = VolumeBake.Encode(map); var loaded = VolumeBake.Decode(bytes, settings, 0.2f);
            Assert.That(loaded.StorageBytes, Is.EqualTo(map.StorageBytes));
            for (int i = 0; i < map.Count; i++) Assert.That(loaded.Component(i), Is.EqualTo(map.Component(i)));
            Assert.Throws<System.IO.InvalidDataException>(() => VolumeBake.Decode(bytes, settings, 0.3f));
            bytes[20] ^= 1; Assert.Throws<System.IO.InvalidDataException>(() => VolumeBake.Decode(bytes, settings, 0.2f));
        }
        [Test]
        public void BarrierRequiresYMotionAndEverySmoothedSegmentIsClear()
        {
            var settings = Settings(16);
            var boxes = new[] { new VolumeBox(new float3(-1, -8, -8), new float3(1, 2, 8)) };
            var map = VolumeBake.Bake(settings, 0.2f, boxes); var search = new VolumePathfinder(8192);
            float3 start = new float3(-5.5f, -3.5f, 0.5f), goal = new float3(5.5f, -3.5f, 0.5f);
            search.Begin(map, start, goal, 1, false); Assert.That(search.Advance(1), Is.EqualTo(1)); Assert.That(search.Status, Is.EqualTo(VolumePathStatus.Pending));
            search.Advance(100000); Assert.That(search.Status, Is.EqualTo(VolumePathStatus.Ready));
            var path = new float3[8194]; int count = search.CopyPath(path); bool rose = false;
            for (int i = 1; i < count; i++)
            {
                rose |= path[i].y > 2;
                Assert.That(IndependentClear(map, path[i - 1], path[i]), Is.True, $"Segment {i}");
            }
            Assert.That(rose, Is.True);
            Assert.That(search.GraphCost, Is.EqualTo(Dijkstra(map, map.Anchor(start), map.Anchor(goal))).Within(0.0001f));
        }
        [Test]
        public void DisconnectedInvalidAndCapacityAreDistinctFromPending()
        {
            var settings = Settings(); var map = VolumeBake.Bake(settings, 0.2f,
                new[] { new VolumeBox(new float3(-1, -6, -6), new float3(1, 6, 6)) });
            var search = new VolumePathfinder(64);
            search.Begin(map, new float3(-3, 0, 0), new float3(3, 0, 0)); Assert.That(search.Status, Is.EqualTo(VolumePathStatus.NoPath));
            search.Begin(map, float3.zero, new float3(3, 0, 0)); Assert.That(search.Status, Is.EqualTo(VolumePathStatus.InvalidEndpoint));
            var small = new VolumePathfinder(1); small.Begin(Empty(), new float3(-4), new float3(4), 1, false); small.Advance(1);
            Assert.That(small.Status, Is.EqualTo(VolumePathStatus.CapacityExceeded));
        }
        [Test]
        public void BvhAndNativeSweepAgreeWithIndependentSlabs()
        {
            var settings = Settings(16); var map = VolumeBake.Bake(settings, 0.2f, VolumeBake.DemoBoxes(settings));
            using (var nodes = new NativeArray<VolumeBvhNode>(GetNodes(map), Allocator.Temp))
            {
                var query = new VolumeQuery { Nodes = nodes, Min = map.Min, Max = map.Max, Clearance = map.ClearanceRadius };
                var random = new Unity.Mathematics.Random(12);
                for (int k = 0; k < 1000; k++)
                {
                    float3 a = random.NextFloat3(map.Min, map.Max), b = random.NextFloat3(map.Min, map.Max);
                    bool expected = IndependentClear(map, a, b);
                    Assert.That(map.SegmentClear(a, b), Is.EqualTo(expected)); Assert.That(query.SegmentClear(a, b), Is.EqualTo(expected));
                    if (map.PointClear(a)) Assert.That(IndependentClear(map, a, math.lerp(a, b, query.SafeFraction(a, b))), Is.True);
                }
            }
        }
        // Obtain the public native query from its owner, not a second implementation of BVH construction.
        private static VolumeBvhNode[] GetNodes(NavigationVolume map)
        {
            using (var navigation = new VolumeNavigation(Settings(map.Resolution), map, 1)) return navigation.Query.Nodes.ToArray();
        }
        [TestCase(ExecutionBackend.Reference)]
        [TestCase(ExecutionBackend.JobsBurst)]
        public void HashMatchesBruteForceIncludingVerticalRangeAndKTruncation(ExecutionBackend backend)
        {
            using (var storage = new AgentStorage(80)) using (var bruteBuffers = new NeighborBuffers(80, 5))
            using (var hashBuffers = new NeighborBuffers(80, 5)) using (var brute = new BruteForceNeighborSearch()) using (var hash = new SpatialHashNeighborSearch3D())
            {
                var data = storage.Initialization; var random = new Unity.Mathematics.Random(72);
                for (int i = 0; i < 80; i++) { data.Ids[i] = 100 - i; data.Positions[i] = random.NextFloat3(-4, 4); }
                data.Positions[0] = new float3(0, 0, 0); data.Positions[1] = new float3(0, 3, 0); data.Positions[2] = new float3(0, -3, 0);
                var settings = SimulationSettings.Default; settings.Dimension = SimulationDimension.Full3D; settings.Backend = backend;
                settings.CellSize = 0.9f; settings.NeighborDistance = 3;
                var context = new StepContext(0, settings);
                brute.Schedule(context, storage.Read, bruteBuffers.Write, default).Complete(); hash.Schedule(context, storage.Read, hashBuffers.Write, default).Complete();
                var a = bruteBuffers.Read; var b = hashBuffers.Read;
                for (int i = 0; i < 80; i++)
                {
                    Assert.That(b.Counts[i], Is.EqualTo(a.Counts[i])); Assert.That(b.DroppedCounts[i], Is.EqualTo(a.DroppedCounts[i]));
                    for (int k = 0; k < a.Counts[i]; k++) Assert.That(b.Indices[i * 5 + k], Is.EqualTo(a.Indices[i * 5 + k]));
                }
            }
        }
        [Test]
        public void OrcaPairPlanesAreReciprocalAndConeAxisIsFinite()
        {
            var random = new Unity.Mathematics.Random(3);
            for (int i = 0; i < 300; i++)
            {
                float3 p = math.normalizesafe(random.NextFloat3(-1, 1), new float3(1, 0, 0)) * 4;
                float3 a = i % 2 == 0 ? p : random.NextFloat3(-3, 3), b = -a;
                var ab = OrcaGeometry3D.Build(p, a, b, 1, 2, 1f / 30, 1, 2);
                var ba = OrcaGeometry3D.Build(-p, b, a, 1, 2, 1f / 30, 2, 1);
                Assert.That(math.all(math.isfinite(ab.Normal)), Is.True);
                Assert.That(math.length(ab.Normal), Is.EqualTo(1).Within(0.0002));
                Assert.That(math.length(ab.Normal + ba.Normal), Is.LessThan(0.0002));
                Assert.That(ab.Offset, Is.EqualTo(ba.Offset).Within(0.0002));
            }
        }
        [Test]
        public void VelocityBallProjectionAndDynamicRelaxationPreserveHardPlane()
        {
            using (var planes = new NativeArray<VelocityPlane3D>(3, Allocator.Temp))
            {
                var writable = planes;
                writable[0] = new VelocityPlane3D { Normal = new float3(1, 0, 0), Offset = 0.25f, IsStatic = true };
                writable[1] = new VelocityPlane3D { Normal = new float3(0, 1, 0), Offset = 0.4f };
                var status = VolumeVelocityOptimizer.Solve(new NativeSlice<VelocityPlane3D>(planes, 0, 2), 1, new float3(-1, -1, 0), 2, 1e-5f,
                    out float3 v, out _, out _);
                Assert.That(status, Is.EqualTo(SolveStatus.Success)); Assert.That(math.distance(v, new float3(0.25f, 0.4f, 0)), Is.LessThan(0.0001));
                writable[2] = new VelocityPlane3D { Normal = new float3(0, -1, 0), Offset = 0.4f };
                status = VolumeVelocityOptimizer.Solve(planes, 1, new float3(-1, 0, 0), 2, 1e-5f, out v, out _, out _);
                Assert.That(status, Is.EqualTo(SolveStatus.Fallback)); Assert.That(v.x, Is.GreaterThanOrEqualTo(0.2499f));
                Assert.That(math.length(v), Is.LessThanOrEqualTo(2.0001));
            }
        }
        [TestCase(ExecutionBackend.Reference)]
        [TestCase(ExecutionBackend.JobsBurst)]
        public void VerticalWorldMovesAndSafetyCoversNeighborsOutsideK(ExecutionBackend backend)
        {
            var settings = SimulationSettings.Default; settings.Dimension = SimulationDimension.Full3D;
            settings.Backend = backend; settings.AgentCount = 2; settings.MaxNeighbors = 1; settings.NeighborDistance = 0.01f;
            settings.Avoidance = AvoidanceAlgorithm.ORCA; settings.PreferredSideBias = 0.03f;
            var map = Empty(); var nav = new VolumeNavigation(Settings(), map, 2); var solver = new VolumeAvoidanceSolver(nav, 2, 1);
            var modules = new SimulationModules(new PairInitializer(), nav, new SpatialHashNeighborSearch3D(), solver, new VolumeEulerIntegrator());
            using (var world = new SimulationWorld(settings, ScenarioSettings.Default, modules))
            {
                float previousY = world.Snapshot.Positions[0].y;
                for (int tick = 0; tick < 120; tick++)
                {
                    world.Step(); var quality = QualityEvaluator.Evaluate(world.Snapshot, world.DebugSnapshot, 0.0001f, SimulationDimension.Full3D);
                    Assert.That(quality.SweptCollisionPairs, Is.Zero);
                    for (int i = 0; i < 2; i++) Assert.That(math.length(world.Snapshot.Velocities[i]), Is.LessThanOrEqualTo(2.0001f));
                }
                Assert.That(world.Snapshot.Positions[0].y, Is.GreaterThan(previousY + 0.1f));
                Assert.That(solver.SafetyLimitedTicks, Is.GreaterThan(0));
            }
        }
        [Test]
        public void CancelGoalDoesNotCommitOldSearchAndPureVerticalStaticOnlyArrives()
        {
            var settings = SimulationSettings.Default; settings.Dimension = SimulationDimension.Full3D;
            settings.AgentCount = 1; settings.Avoidance = AvoidanceAlgorithm.None;
            var volume = Settings(); volume.ExpansionsPerTick = 1;
            var map = VolumeBake.Bake(volume, 0.2f, new[] { new VolumeBox(new float3(-1, -6, -6), new float3(1, 1, 6)) });
            using (var nav = new VolumeNavigation(volume, map, 1)) using (var data = new AgentStorage(1))
            {
                var initial = data.Initialization; initial.Ids[0] = 17; initial.Positions[0] = new float3(-3, -3, 0); initial.Goals[0] = new float3(3, -3, 0);
                initial.Parameters[0] = new AgentParameters { Radius = 0.2f, MaxSpeed = 2, ArrivalDistance = 0.1f };
                nav.Schedule(new StepContext(0, settings), data.Read, data.Preferred, default).Complete(); int request = nav.Path(0).RequestId;
                initial.Goals[0] = new float3(-3, 3, 0);
                for (int i = 1; i < 20; i++) nav.Schedule(new StepContext(i, settings), data.Read, data.Preferred, default).Complete();
                Assert.That(nav.Path(0).RequestId, Is.GreaterThan(request)); Assert.That(nav.Path(0).Goal, Is.EqualTo(new float3(-3, 3, 0)));
                Assert.That(data.Preferred[0].y, Is.GreaterThan(0)); Assert.That(data.Preferred[0].x, Is.Zero);
            }
        }
        [Test]
        public void ReferenceAndBurstWorldsAgreeAcrossStaticAndDynamicStages()
        {
            var volume = Settings(24); var scenario = ScenarioSettings.Default; scenario.Kind = ScenarioKind.RandomCrowd;
            scenario.Radius = 0.2f; scenario.MaxSpeed = 2; scenario.Seed = 7;
            var map = VolumeBake.Bake(volume, scenario.Radius, VolumeBake.DemoBoxes(volume));
            var reference = SimulationSettings.Default; reference.Dimension = SimulationDimension.Full3D;
            reference.Avoidance = AvoidanceAlgorithm.ORCA; reference.AgentCount = 16; reference.NeighborDistance = 4;
            reference.CellSize = 2; reference.PreferredSideBias = 0.03f; reference.NeighborSearch = NeighborSearchAlgorithm.BruteForce;
            var jobs = reference; jobs.Backend = ExecutionBackend.JobsBurst; jobs.NeighborSearch = NeighborSearchAlgorithm.SpatialHash;
            var rm = Phase3ModuleFactory.Create(reference, scenario, volume, map, out var rn, out _);
            var jm = Phase3ModuleFactory.Create(jobs, scenario, volume, map, out var jn, out _);
            using (var rw = new SimulationWorld(reference, scenario, rm)) using (var jw = new SimulationWorld(jobs, scenario, jm))
                for (int tick = 0; tick < 120; tick++)
                {
                    rw.Step(); jw.Step();
                    Assert.That(QualityEvaluator.Evaluate(jw.Snapshot, jw.DebugSnapshot, 0.0001f, SimulationDimension.Full3D).SweptCollisionPairs, Is.Zero);
                    for (int i = 0; i < 16; i++)
                    {
                        Assert.That(math.distance(rw.Snapshot.Positions[i], jw.Snapshot.Positions[i]), Is.LessThan(0.003f), $"tick {tick}, agent {i}");
                        Assert.That(IndependentClear(map, jw.DebugSnapshot.Inputs.Positions[i], jw.Snapshot.Positions[i]), Is.True);
                        Assert.That(rn.Path(i).Status, Is.EqualTo(jn.Path(i).Status));
                    }
                }
        }
        [Test]
        public void VelocityOptimizerMatchesEnumeratedActiveSets()
        {
            var random = new Unity.Mathematics.Random(130);
            var planes = new NativeArray<VelocityPlane3D>(6, Allocator.Temp);
            try
            {
                for (int trial = 0; trial < 100; trial++)
                {
                    for (int i = 0; i < planes.Length; i++) planes[i] = new VelocityPlane3D
                    { Normal = math.normalizesafe(random.NextFloat3(-1, 1), new float3(1, 0, 0)), Offset = -random.NextFloat(0.1f, 1) };
                    float3 preferred = random.NextFloat3(-3, 3);
                    var status = VolumeVelocityOptimizer.Solve(planes, planes.Length, preferred, 2, 1e-5f, out var result, out _, out _);
                    Assert.That(status, Is.EqualTo(SolveStatus.Success));
                    float best = float.PositiveInfinity;
                    CheckCandidate(math.normalizesafe(preferred) * math.min(2, math.length(preferred)), planes, preferred, ref best);
                    for (int i = 0; i < planes.Length; i++)
                    {
                        float3 n = planes[i].Normal, center = n * planes[i].Offset;
                        float3 tangent = preferred - math.dot(preferred, n) * n;
                        CheckCandidate(center + math.normalizesafe(tangent) * math.min(math.length(tangent), math.sqrt(4 - math.lengthsq(center))), planes, preferred, ref best);
                        for (int j = 0; j < i; j++)
                        {
                            float3 m = planes[j].Normal, cross = math.cross(n, m); float d2 = math.lengthsq(cross); if (d2 < 1e-8f) continue;
                            float3 line = cross / math.sqrt(d2);
                            float3 point = (planes[i].Offset * math.cross(m, cross) + planes[j].Offset * math.cross(cross, n)) / d2;
                            float circle = 4 - math.lengthsq(point);
                            if (circle >= 0) CheckCandidate(point + line * math.clamp(math.dot(preferred - point, line), -math.sqrt(circle), math.sqrt(circle)), planes, preferred, ref best);
                            for (int k = 0; k < j; k++)
                            {
                                float3 q = planes[k].Normal; float determinant = math.dot(n, math.cross(m, q));
                                if (math.abs(determinant) < 1e-6f) continue;
                                CheckCandidate((planes[i].Offset * math.cross(m, q) + planes[j].Offset * math.cross(q, n) + planes[k].Offset * math.cross(n, m)) / determinant, planes, preferred, ref best);
                            }
                        }
                    }
                    Assert.That(math.distancesq(preferred, result), Is.EqualTo(best).Within(0.001f));
                }
            }
            finally { planes.Dispose(); }
        }
        private static void CheckCandidate(float3 candidate, NativeArray<VelocityPlane3D> planes, float3 preferred, ref float best)
        {
            if (math.lengthsq(candidate) > 4.0001f) return;
            for (int i = 0; i < planes.Length; i++) if (planes[i].Violation(candidate) > 0.0001f) return;
            best = math.min(best, math.distancesq(candidate, preferred));
        }
        [Test]
        public void CoarseBakedRoutesRemainClearInFineGeometry()
        {
            var settings = Settings(64); var map = VolumeBake.Bake(settings, 0.2f, VolumeBake.DemoBoxes(settings));
            Assert.That(map.Coarse, Is.Not.Null);
            map = VolumeBake.Decode(VolumeBake.Encode(map), settings, 0.2f);
            Assert.That(map.Coarse.Resolution, Is.EqualTo(8));
            var search = new VolumePathfinder(8192); var points = new float3[8194];
            search.Begin(map.Coarse, new float3(-26, -8, 0), new float3(26, 8, 0), 1.5f);
            search.Advance(8192); Assert.That(search.Status, Is.EqualTo(VolumePathStatus.Ready));
            int count = search.CopyPath(points);
            for (int i = 1; i < count; i++) Assert.That(IndependentClear(map, points[i - 1], points[i]), Is.True);
        }
        [Test]
        public void BurstSearchMatchesReferenceAcrossBudgetsPenaltiesAndReuse()
        {
            var settings = Settings(16);
            var map = VolumeBake.Bake(settings, 0.2f, new[] { new VolumeBox(new float3(-1, -8, -8), new float3(1, 2, 8)) });
            using (var nav = new VolumeNavigation(settings, map, 1))
            using (var native = new BurstVolumePathfinder(8192, map, nav.Query))
            {
                var reference = new VolumePathfinder(8192); var points = new float3[8194];
                for (int trial = 0; trial < 12; trial++)
                {
                    float3 from = new float3(-5.5f, -3.5f + trial % 3, trial % 4 - 1.5f);
                    float3 goal = new float3(5.5f, -3.5f, -1.5f);
                    float weight = trial % 2 == 0 ? 1 : 1.5f;
                    float3 penalty = new float3(-3, 3, 0); float radius = trial % 3 == 0 ? 2 : 0;
                    reference.Begin(map, from, goal, weight, false, penalty, radius);
                    native.Begin(map, from, goal, weight, false, penalty, radius);
                    while (reference.Status == VolumePathStatus.Pending) reference.Advance(31);
                    int iterations = 0;
                    while (native.Status == VolumePathStatus.Pending && iterations++ < 10000) Assert.That(native.Advance(7), Is.LessThanOrEqualTo(7));
                    Assert.That(native.Status, Is.EqualTo(reference.Status));
                    // Weighted search can choose different tied paths; exact A* must match the oracle cost.
                    if (weight == 1) Assert.That(native.GraphCost, Is.EqualTo(reference.GraphCost).Within(0.0002f));
                    int length = native.CopyPath(points);
                    Assert.That(length, Is.GreaterThan(2));
                    for (int i = 1; i < length; i++) Assert.That(IndependentClear(map, points[i - 1], points[i]), Is.True);
                }
                native.Begin(map, float3.zero, new float3(4));
                Assert.That(native.Status, Is.EqualTo(VolumePathStatus.InvalidEndpoint));
                native.Begin(map, new float3(-4, 4, 0), new float3(-4, 5, 0));
                Assert.That(native.Status, Is.EqualTo(VolumePathStatus.Ready));
                Assert.That(native.CopyPath(points), Is.EqualTo(2));
            }
            using (var nav = new VolumeNavigation(settings, map, 1))
            using (var tiny = new BurstVolumePathfinder(1, map, nav.Query))
            {
                tiny.Begin(map, new float3(-5, -3, 0), new float3(5, -3, 0), 1, false);
                tiny.Advance(1); Assert.That(tiny.Status, Is.EqualTo(VolumePathStatus.CapacityExceeded));
            }
        }
        [Test]
        public void LandmarkBoundsAndBothSearchBackendsMatchIndependentShortestPaths()
        {
            var settings = Settings();
            var map = VolumeBake.Bake(settings, 0.2f,
                new[] { new VolumeBox(new float3(-1, -6, -6), new float3(1, 2, 6)) });
            map.PrepareLandmarks();
            var owner = Empty(); owner.Coarse = map;
            using (var nav = new VolumeNavigation(settings, map, 1))
            using (var ownerNav = new VolumeNavigation(settings, owner, 1))
            using (var distances = new NativeArray<float>(map.Landmarks.Distances, Allocator.TempJob))
            using (var native = new BurstVolumePathfinder(8192, owner, ownerNav.Query, nav.Query, distances))
            {
                var reference = new VolumePathfinder(8192); var points = new float3[8194];
                for (int trial = 0; trial < 4; trial++)
                {
                    float3 from = new float3(-4.5f, trial - 3.5f, 0.5f), goal = new float3(4.5f, -3.5f, trial - 1.5f);
                    int source = map.Anchor(from), target = map.Anchor(goal);
                    float exact = Dijkstra(map, source, target);
                    Assert.That(map.Landmarks.LowerBound(source, target), Is.LessThanOrEqualTo(exact + 0.0001f));
                    reference.Begin(map, from, goal, 1, false); native.Begin(map, from, goal, 1, false);
                    reference.Advance(8192);
                    for (int iteration = 0; iteration < 8192 && native.Status == VolumePathStatus.Pending; iteration++)
                        Assert.That(native.Advance(7), Is.LessThanOrEqualTo(7));
                    Assert.That(native.Status, Is.EqualTo(VolumePathStatus.Ready));
                    Assert.That(reference.GraphCost, Is.EqualTo(exact).Within(0.0002f));
                    Assert.That(native.GraphCost, Is.EqualTo(exact).Within(0.0002f));
                    int length = native.CopyPath(points);
                    for (int i = 1; i < length; i++) Assert.That(IndependentClear(map, points[i - 1], points[i]), Is.True);
                }
                // 回退到细图时必须停止使用粗图距离表，且两次释放不应触发容器错误。
                native.Begin(owner, new float3(-4.5f), new float3(4.5f), 1, false);
                native.Advance(8192);
                Assert.That(native.GraphCost, Is.EqualTo(9 * math.sqrt(3)).Within(0.0002f));
                native.Dispose();
            }
        }
        [Test]
        public void LandmarkBoundsIgnoreDisconnectedComponents()
        {
            var settings = Settings();
            var map = VolumeBake.Bake(settings, 0.2f,
                new[] { new VolumeBox(new float3(-1, -6, -6), new float3(1, 6, 6)) });
            map.PrepareLandmarks();
            int a = map.Anchor(new float3(-4.5f, 0.5f, 0.5f)), b = map.Anchor(new float3(4.5f, 0.5f, 0.5f));
            Assert.That(map.Landmarks.LowerBound(a, b), Is.Zero);
            var search = new VolumePathfinder(8192); search.Begin(map, map.Center(a), map.Center(b));
            Assert.That(search.Status, Is.EqualTo(VolumePathStatus.NoPath));
        }
        [Test]
        public void FineEndpointConnectsSafelyPastCoarseInflation()
        {
            var settings = Settings(64);
            var map = VolumeBake.Bake(settings, 0.2f, new[] { new VolumeBox(new float3(1, -32, -32), new float3(2, 8, 32)) });
            float3 from = new float3(0.5f, 0.5f, 0.5f), goal = new float3(20.5f, 0.5f, 0.5f);
            Assert.That(map.PointClear(from), Is.True); Assert.That(map.Coarse.PointClear(from), Is.False);
            var search = new VolumePathfinder(8192);
            search.Begin(map.Coarse, from, goal, 1.5f, true, default, 0, map);
            search.Advance(8192); Assert.That(search.Status, Is.EqualTo(VolumePathStatus.Ready));
            var points = new float3[8194]; int length = search.CopyPath(points);
            for (int i = 1; i < length; i++) Assert.That(IndependentClear(map, points[i - 1], points[i]), Is.True);
            using (var nav = new VolumeNavigation(settings, map, 1))
            using (var coarseNodes = new NativeArray<VolumeBvhNode>(GetNodes(map.Coarse), Allocator.TempJob))
            using (var landmarks = new NativeArray<float>(map.Coarse.Landmarks.Distances, Allocator.TempJob))
            {
                var coarseQuery = new VolumeQuery { Nodes = coarseNodes, Min = map.Coarse.Min, Max = map.Coarse.Max, Clearance = map.Coarse.ClearanceRadius };
                using (var native = new BurstVolumePathfinder(8192, map, nav.Query, coarseQuery, landmarks))
                    for (int trial = 0; trial < 4; trial++)
                    {
                        float3 a = trial % 2 == 0 ? from : goal, b = trial % 2 == 0 ? goal : from;
                        float radius = trial < 2 ? 0 : 3;
                        search.Begin(map.Coarse, a, b, 1, false, new float3(-4, 12, 4), radius, map);
                        native.Begin(map.Coarse, a, b, 1, false, new float3(-4, 12, 4), radius, map);
                        search.Advance(8192);
                        while (native.Status == VolumePathStatus.Pending) native.Advance(11);
                        Assert.That(native.Status, Is.EqualTo(VolumePathStatus.Ready));
                        Assert.That(native.GraphCost, Is.EqualTo(search.GraphCost).Within(0.001f));
                        length = native.CopyPath(points);
                        for (int i = 1; i < length; i++) Assert.That(IndependentClear(map, points[i - 1], points[i]), Is.True);
                    }
            }
        }
        [Test]
        public void DenseDemoKeepsCrossVolumeRoutesAndForcesDetours()
        {
            var settings = Settings(64);
            var map = VolumeBake.Bake(settings, 0.2f, VolumeBake.DenseDemoBoxes(settings));
            Assert.That(map.ObstacleCount, Is.EqualTo(104));
            float3 from = new float3(-29.5f, 0.5f, 0.5f), to = new float3(29.5f, 0.5f, 0.5f);
            Assert.That(map.SegmentClear(from, to), Is.False);
            var search = new VolumePathfinder(262144);
            search.Begin(map, from, to, 1.5f); search.Advance(262144);
            Assert.That(search.Status, Is.EqualTo(VolumePathStatus.Ready));
            var points = new float3[262146]; int length = search.CopyPath(points);
            for (int i = 1; i < length; i++) Assert.That(IndependentClear(map, points[i - 1], points[i]), Is.True);
        }
        [Test]
        public void FactoryRejectsUnsupportedDimensionAlgorithms()
        {
            var settings = SimulationSettings.Default; var scenario = ScenarioSettings.Default; scenario.Radius = 0.2f; var volume = Settings(); var map = Empty();
            Assert.Throws<NotSupportedException>(() => Phase3ModuleFactory.Create(settings, scenario, volume, map, out _, out _));
            settings.Dimension = SimulationDimension.Full3D; settings.Avoidance = AvoidanceAlgorithm.ORCA; settings.NeighborSearch = NeighborSearchAlgorithm.KdTree;
            Assert.Throws<NotSupportedException>(() => Phase3ModuleFactory.Create(settings, scenario, volume, map, out _, out _));
        }
        private sealed class PairInitializer : StatelessModule, IScenarioInitializer
        {
            public override string Name => "Vertical pair";
            public void Initialize(in SimulationSettings settings, in ScenarioSettings scenario, in AgentInitializationView initialization)
            {
                var a = initialization;
                for (int i = 0; i < 2; i++)
                {
                    a.Ids[i] = i; a.Positions[i] = new float3(0, i == 0 ? -2 : 2, 0); a.Goals[i] = -a.Positions[i]; a.Velocities[i] = float3.zero;
                    a.Parameters[i] = new AgentParameters { Radius = 0.2f, MaxSpeed = 2, ArrivalDistance = 0.1f };
                }
            }
        }
        private static float Dijkstra(NavigationVolume map, int source, int target)
        {
            var distance = new float[map.Count]; var used = new bool[map.Count];
            for (int i = 0; i < distance.Length; i++) distance[i] = float.PositiveInfinity; distance[source] = 0;
            while (true)
            {
                int current = -1;
                for (int i = 0; i < distance.Length; i++) if (!used[i] && (current < 0 || distance[i] < distance[current])) current = i;
                if (current < 0 || !math.isfinite(distance[current])) return float.PositiveInfinity;
                if (current == target) return distance[current]; used[current] = true;
                int3 origin = map.Cell(current);
                for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
                {
                    int3 cell = origin + new int3(x, y, z); if (!map.Contains(cell)) continue; int next = map.Index(cell);
                    if (map.Component(next) < 0 || !IndependentClear(map, map.Center(current), map.Center(next))) continue;
                    distance[next] = math.min(distance[next], distance[current] + math.length(new float3(x, y, z)) * map.CellSize);
                }
            }
        }
        private static bool IndependentClear(NavigationVolume map, float3 a, float3 b)
        {
            for (int axis = 0; axis < 3; axis++)
                if (a[axis] <= map.Min[axis] + map.ClearanceRadius || a[axis] >= map.Max[axis] - map.ClearanceRadius ||
                    b[axis] <= map.Min[axis] + map.ClearanceRadius || b[axis] >= map.Max[axis] - map.ClearanceRadius) return false;
            for (int i = 0; i < map.ObstacleCount; i++)
            {
                var box = map.Obstacle(i); double entry = 0, exit = 1; bool miss = false;
                for (int axis = 0; axis < 3; axis++)
                {
                    double d = (double)b[axis] - a[axis], low = box.Min[axis] - map.ClearanceRadius, high = box.Max[axis] + map.ClearanceRadius;
                    if (d == 0) { if (a[axis] < low || a[axis] > high) miss = true; }
                    else
                    {
                        double first = (low - a[axis]) / d, second = (high - a[axis]) / d;
                        entry = Math.Max(entry, Math.Min(first, second)); exit = Math.Min(exit, Math.Max(first, second));
                    }
                }
                if (!miss && entry <= exit) return false;
            }
            return true;
        }
    }
}
