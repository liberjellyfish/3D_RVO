using NUnit.Framework;
using Unity.Mathematics;

namespace Rvo.Tests
{
    public sealed class Phase2NavigationTests
    {
        [Test]
        public void GridUsesRadiusBoundaryAndContinuousCornerChecks()
        {
            var cells = new bool[64]; cells[3 * 8 + 3] = true; cells[4 * 8 + 4] = true;
            var map = new NavigationGrid(8, 8, 1, cells, 0.39f, 1);
            Assert.That(map.Cell(new float2(-3.9f, -3.9f)), Is.EqualTo(0));
            Assert.That(map.Cell(new float2(-4.1f, 0)), Is.EqualTo(-1));
            Assert.That(map.SegmentClear(new float2(-3.9f, 0), new float2(-3, 0), 0.2f), Is.False);
            Assert.That(map.SegmentClear(new float2(-3, -0.5f), new float2(3, -0.5f), 0.35f), Is.False, "Must reject a swept wall crossing.");
            Assert.That(map.CanStep(map.Index(3, 4), map.Index(4, 3)), Is.False, "No diagonal corner cutting.");
            Assert.That(map.IsWalkable(map.Index(2, 3)), Is.True);
            var large = new NavigationGrid(8, 8, 1, cells, 0.6f, 1);
            Assert.That(large.IsWalkable(map.Index(2, 3)), Is.False);
            cells[0] = true;
            Assert.That(map.IsOccupied(0), Is.False, "Published map must own a copy.");
        }

        [Test]
        public void AStarMatchesIndependentDijkstraAndReportsInvalidOrDisconnectedEndpoints()
        {
            var random = new Unity.Mathematics.Random(23);
            for (int sample = 0; sample < 24; sample++)
            {
                var cells = new bool[100];
                for (int i = 0; i < 20; i++) cells[random.NextInt(1, 99)] = true;
                var map = new NavigationGrid(10, 10, 1, cells, 0.3f, 1);
                var finder = new GridPathfinder(map.Count); var path = new float2[map.Count + 2];
                float expected = Dijkstra(map, 0, 99);
                var status = finder.Find(map, map.Center(0), map.Center(99), path, 0, out int count);
                if (float.IsPositiveInfinity(expected)) Assert.That(status, Is.EqualTo(GridPathStatus.NoPath));
                else
                {
                    Assert.That(status, Is.EqualTo(GridPathStatus.Ready));
                    Assert.That(finder.LastGraphCost, Is.EqualTo(expected).Within(1e-4f));
                    for (int i = 1; i < count; i++) Assert.That(map.SegmentClear(path[i - 1], path[i], 0.3f), Is.True);
                }
                Assert.That(finder.Find(map, new float2(100, 100), map.Center(99), path, 0, out _), Is.EqualTo(GridPathStatus.InvalidEndpoint));
                Assert.That(finder.Find(map, map.Center(0), map.Center(0), path, 0, out _), Is.EqualTo(GridPathStatus.Ready));
            }
            var wall = new bool[64]; for (int z = 0; z < 8; z++) wall[z * 8 + 4] = true;
            var split = new NavigationGrid(8, 8, 1, wall, 0.35f, 1);
            Assert.That(new GridPathfinder(64).Find(split, split.Center(0), split.Center(63), new float2[66], 0, out _), Is.EqualTo(GridPathStatus.NoPath));
        }

        [TestCase(ExecutionBackend.Reference)]
        [TestCase(ExecutionBackend.JobsBurst)]
        public void BakedMapRemainsImmutableAndSearchHonorsBudget(ExecutionBackend backend)
        {
            Settings(out var settings, out var scenario, out var config);
            settings.Backend = backend; config.PathExpansionsPerTick = 64;
            var map = NavigationBake.Generate(config, scenario.Radius);
            using (var world = new SimulationWorld(settings, scenario,
                Phase2ModuleFactory.Create(settings, scenario, config, map, out var navigation, out _)))
            {
                for (int tick = 0; tick < 300; tick++)
                {
                    world.Step();
                    Assert.That(navigation.Map, Is.SameAs(map));
                    Assert.That(navigation.LastExpandedNodes, Is.LessThanOrEqualTo(config.PathExpansionsPerTick));
                    Assert.That(QualityEvaluator.Evaluate(world.Snapshot,world.DebugSnapshot,settings.Epsilon).SweptCollisionPairs, Is.Zero);
                    for (int i = 0; i < settings.AgentCount; i++)
                        AssertStaticSweep(map, world.DebugSnapshot.Inputs.Positions[i].xz, world.Snapshot.Positions[i].xz, scenario.Radius);
                }
                Assert.That(navigation.PathCapacityBytes, Is.LessThan(settings.AgentCount * map.Count * 8L));
                world.ScheduleStep();
            }
        }

        [Test]
        public void BinaryBakeRoundTripsAndRejectsStaleOrCorruptedData()
        {
            Settings(out _, out var scenario, out var config);
            var map = NavigationBake.Generate(config,scenario.Radius);
            var data = NavigationBake.Encode(map,config.BakeSignature(scenario.Radius));
            var loaded = NavigationBake.Decode(data,config.BakeSignature(scenario.Radius));
            for (int i = 0; i < map.Count; i++)
            {
                Assert.That(loaded.IsOccupied(i), Is.EqualTo(map.IsOccupied(i)));
                Assert.That(loaded.Clearance(i), Is.EqualTo(map.Clearance(i)));
                Assert.That(loaded.EdgeMask(i), Is.EqualTo(map.EdgeMask(i)));
                Assert.That(loaded.Component(i), Is.EqualTo(map.Component(i)));
            }
            Assert.Throws<System.IO.InvalidDataException>(() => NavigationBake.Decode(data,config.BakeSignature(scenario.Radius+1)));
            data[data.Length/2] ^= 1;
            Assert.Throws<System.IO.InvalidDataException>(() => NavigationBake.Decode(data,config.BakeSignature(scenario.Radius)));
        }

        [TestCase(16)]
        [TestCase(256)]
        [TestCase(1024)]
        public void LargeBakedMapSupportsMultiCellAgentsAndThreeScales(int count)
        {
            Settings(out var settings, out var scenario, out _);
            var config = NavigationSettings.Default;
            settings.AgentCount = count; settings.MaxNeighbors = 48; settings.CellSize = 24;
            settings.NeighborDistance = 56; settings.Backend = ExecutionBackend.JobsBurst;
            scenario.Radius = 2.5f; scenario.MaxSpeed = 12; scenario.ArrivalDistance = 0.5f;
            var map = NavigationBake.Generate(config,scenario.Radius);
            Assert.That(map.Count, Is.EqualTo(512*512));
            using (var world = new SimulationWorld(settings,scenario,
                Phase2ModuleFactory.Create(settings,scenario,config,map,out var navigation,out var solver)))
            {
                for (int tick = 0; tick < 32; tick++)
                {
                    world.Step();
                    Assert.That(navigation.LastExpandedNodes, Is.LessThanOrEqualTo(config.PathExpansionsPerTick));
                    Assert.That(QualityEvaluator.Evaluate(world.Snapshot,world.DebugSnapshot,settings.Epsilon).SweptCollisionPairs, Is.Zero);
                    for (int i = 0; i < count; i++)
                        Assert.That(map.SegmentClear(world.DebugSnapshot.Inputs.Positions[i].xz,world.Snapshot.Positions[i].xz,scenario.Radius), Is.True);
                }
                Assert.That(navigation.Map, Is.SameAs(map));
                Assert.That(navigation.PathCapacityBytes, Is.LessThan(count * 8192L));
                if (count >= 256) Assert.That(solver.LastSafetyPairChecks, Is.LessThan((long)count*(count-1)/2));
            }
        }

        [Test]
        public void SafetyCertificateCatchesPairsOmittedByNeighborQuery()
        {
            Settings(out var settings, out var scenario, out var config);
            settings.AgentCount = 2; settings.MaxNeighbors = 1; settings.NeighborDistance = 0.001f; settings.FixedDeltaTime = 2;
            scenario.Kind = ScenarioKind.HeadOnPair; scenario.Extent = 3;
            config.ObstacleCount = 0;
            var navigation = new GridNavigation(config, 2, scenario.Radius, NavigationBake.Generate(config, scenario.Radius));
            var solver = new GridAvoidanceSolver(navigation);
            using (var world = new SimulationWorld(settings, scenario, new SimulationModules(new ScenarioInitializer(), navigation,
                new BruteForceNeighborSearch(), solver, new PlanarEulerIntegrator())))
            {
                for (int i = 0; i < 10; i++)
                {
                    world.Step();
                    Assert.That(QualityEvaluator.Evaluate(world.Snapshot, world.DebugSnapshot, settings.Epsilon).SweptCollisionPairs, Is.Zero);
                }
                Assert.That(solver.SafetyLimitedTicks, Is.GreaterThan(0));
            }
        }

        internal static void Settings(out SimulationSettings simulation, out ScenarioSettings scenario, out NavigationSettings navigation)
        {
            simulation = SimulationSettings.Default; simulation.AgentCount = 16; simulation.MaxNeighbors = 15;
            simulation.Avoidance = AvoidanceAlgorithm.ORCA; simulation.NeighborSearch = NeighborSearchAlgorithm.SpatialHash;
            simulation.TimeHorizon = 2; simulation.NeighborDistance = 24; simulation.PreferredSideBias = 0.08f;
            scenario = ScenarioSettings.Default; scenario.Kind = ScenarioKind.RandomCrowd; scenario.ArrivalDistance = 0.15f;
            navigation = NavigationSettings.Default; navigation.Enabled = true;
            navigation.Width = 32; navigation.Height = 24; navigation.ObstacleCount = 55;
            navigation.ObstacleMinSize = navigation.ObstacleMaxSize = 1; navigation.SafetyMargin = 0.04f;
        }

        private static float Dijkstra(NavigationGrid map, int start, int goal)
        {
            var distances = new float[map.Count]; var visited = new bool[map.Count];
            for (int i = 0; i < map.Count; i++) distances[i] = float.PositiveInfinity;
            distances[start] = 0;
            for (int iteration = 0; iteration < map.Count; iteration++)
            {
                int best = -1;
                for (int i = 0; i < map.Count; i++) if (!visited[i] && (best < 0 || distances[i] < distances[best])) best = i;
                if (best < 0 || float.IsPositiveInfinity(distances[best])) break;
                if (best == goal) return distances[best]; visited[best] = true;
                for (int i = 0; i < map.Count; i++) if (!visited[i] && map.CanStep(best, i))
                    distances[i] = math.min(distances[i], distances[best] + math.distance(map.Center(best), map.Center(i)));
            }
            return distances[goal];
        }

        // 独立于生产 slab：线段到方块四条边的最短距离，直接检查物理圆盘半径。
        private static void AssertStaticSweep(NavigationGrid map, float2 a, float2 b, float radius)
        {
            Assert.That(math.cmin(math.min(b - map.Min, map.Max - b)), Is.GreaterThanOrEqualTo(radius));
            for (int cell = 0; cell < map.Count; cell++) if (map.IsOccupied(cell))
            {
                map.Bounds(cell, out float2 lo, out float2 hi);
                float2 c = new float2(lo.x, hi.y), d = new float2(hi.x, lo.y);
                float distance = math.min(math.min(SegmentDistance(a, b, lo, c), SegmentDistance(a, b, c, hi)),
                    math.min(SegmentDistance(a, b, hi, d), SegmentDistance(a, b, d, lo)));
                Assert.That(distance, Is.GreaterThanOrEqualTo(radius - 1e-5f), $"Static collision with cell {cell}");
                Assert.That(math.all(b > lo) && math.all(b < hi), Is.False);
            }
        }
        private static float SegmentDistance(float2 a, float2 b, float2 c, float2 d)
        {
            float2 u = b - a, v = d - c; float cross = u.x * v.y - u.y * v.x;
            if (math.abs(cross) > 1e-8f)
            {
                float2 w = c - a; float s = (w.x * v.y - w.y * v.x) / cross, t = (w.x * u.y - w.y * u.x) / cross;
                if (s >= 0 && s <= 1 && t >= 0 && t <= 1) return 0;
            }
            return math.min(math.min(PointSegment(a, c, d), PointSegment(b, c, d)), math.min(PointSegment(c, a, b), PointSegment(d, a, b)));
        }
        private static float PointSegment(float2 p, float2 a, float2 b)
        { float2 d = b - a; return math.distance(p, a + d * math.clamp(math.dot(p - a, d) / math.max(1e-20f, math.lengthsq(d)), 0, 1)); }
    }
}
