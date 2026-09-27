using System;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;

namespace Rvo.Tests
{
    public sealed class Phase13Tests
    {
        [TestCase(10, 0, 2, 0, 1, 4.5f)]
        [TestCase(10, 1, 2, 0, 1, 5f)]
        [TestCase(10, 1.01f, 2, 0, 1, float.PositiveInfinity)]
        [TestCase(10, 0, -2, 0, 1, float.PositiveInfinity)]
        [TestCase(10, 0, 0, 0, 1, float.PositiveInfinity)]
        [TestCase(0, 0, 0, 0, 1, 0)]
        [TestCase(1, 0, -1, 0, 1, float.PositiveInfinity)]
        [TestCase(1, 0, 1, 0, 1, 0)]
        public void CollisionTimeMatchesAnalyticCases(float px, float pz, float vx, float vz, float radius, float expected)
        {
            float result = VelocityObstacle2D.TimeToCollision(new float2(px, pz), new float2(vx, vz), radius);
            if (float.IsPositiveInfinity(expected)) Assert.That(float.IsPositiveInfinity(result));
            else Assert.That(result, Is.EqualTo(expected).Within(1e-5f));
        }

        [Test]
        public void FiniteHorizonExcludesLaterCollisions()
        {
            Assert.That(VelocityObstacle2D.Contains(new float2(10, 0), new float2(2, 0), 1, 4), Is.False);
            Assert.That(VelocityObstacle2D.Contains(new float2(10, 0), new float2(2, 0), 1, 5), Is.True);
        }

        [Test]
        public void NeighborsUseBoundaryDistanceStableIdsAndReportTruncation()
        {
            var settings = Settings(5); settings.MaxNeighbors = 2; settings.NeighborDistance = 1;
            var points = new[] { float3.zero, new float3(-1,0,0), new float3(1,0,0), new float3(0,0,-1), new float3(0,0,1) };
            using (var world = CustomWorld(settings, points, points, new[] { 90,40,30,20,10 }))
            {
                world.Step();
                var neighbors = world.DebugSnapshot.Neighbors;
                Assert.That(neighbors.Counts[0], Is.EqualTo(2));
                Assert.That(neighbors.DroppedCounts[0], Is.EqualTo(2));
                Assert.That(neighbors.Indices[0], Is.EqualTo(4));
                Assert.That(neighbors.Indices[1], Is.EqualTo(3));
            }
        }

        [Test]
        public void NeighborSearchMatchesIndependentSortAcrossSeeds()
        {
            for (uint seed = 1; seed <= 8; seed++)
            {
                var random = new Unity.Mathematics.Random(seed);
                var points = Enumerable.Range(0, 32).Select(_ => new float3(random.NextFloat(-10,10), 0, random.NextFloat(-10,10))).ToArray();
                var settings = Settings(points.Length); settings.MaxNeighbors = 5; settings.NeighborDistance = 4;
                using (var world = CustomWorld(settings, points, points))
                {
                    world.Step(); var neighbors = world.DebugSnapshot.Neighbors;
                    for (int i = 0; i < points.Length; i++)
                    {
                        int self = i;
                        var expected = Enumerable.Range(0, points.Length).Where(j => j != self && math.distancesq(points[self], points[j]) <= 16)
                            .OrderBy(j => math.distancesq(points[self], points[j])).ThenBy(j => j).ToArray();
                        Assert.That(neighbors.Counts[i], Is.EqualTo(Math.Min(5, expected.Length)));
                        Assert.That(neighbors.DroppedCounts[i], Is.EqualTo(Math.Max(0, expected.Length - 5)));
                        for (int slot = 0; slot < neighbors.Counts[i]; slot++)
                            Assert.That(neighbors.Indices[i * 5 + slot], Is.EqualTo(expected[slot]));
                    }
                }
            }
        }

        [TestCase(AvoidanceAlgorithm.None)]
        [TestCase(AvoidanceAlgorithm.VO)]
        public void SingleAgentArrivesWithoutOvershootAndKeepsPlane(AvoidanceAlgorithm algorithm)
        {
            var settings = Settings(1); settings.Avoidance = algorithm; settings.PlaneHeight = 3;
            var scenario = ScenarioSettings.Default; scenario.Kind = ScenarioKind.SingleAgent; scenario.Extent = 1.13f;
            using (var world = new SimulationWorld(settings, scenario, Phase1ModuleFactory.Create(settings)))
            {
                for (int tick = 0; tick < 150; tick++)
                {
                    world.Step(); var state = world.Snapshot;
                    Assert.That(state.Positions[0].y, Is.EqualTo(3));
                    Assert.That(state.Velocities[0].y, Is.EqualTo(0));
                    Assert.That(math.length(state.Velocities[0]), Is.LessThanOrEqualTo(scenario.MaxSpeed + 1e-5f));
                    Assert.That(state.Positions[0].x, Is.LessThanOrEqualTo(scenario.Extent + 1e-5f));
                }
                Assert.That(math.distance(world.Snapshot.Positions[0], world.Snapshot.Goals[0]), Is.LessThanOrEqualTo(scenario.ArrivalDistance));
                Assert.That(math.length(world.Snapshot.Velocities[0]), Is.EqualTo(0));
                Assert.That(world.DebugSnapshot.Neighbors.Counts[0], Is.Zero);
            }
        }

        [TestCase(ScenarioKind.SingleAgent, 1)]
        [TestCase(ScenarioKind.HeadOnPair, 2)]
        [TestCase(ScenarioKind.Crossing, 16)]
        [TestCase(ScenarioKind.CircleSwap, 16)]
        [TestCase(ScenarioKind.OpposingGroups, 32)]
        [TestCase(ScenarioKind.RandomCrowd, 32)]
        public void ScenariosAreReproducibleAndInitiallySeparated(ScenarioKind kind, int count)
        {
            var settings = Settings(count);
            var scenario = ScenarioSettings.Default; scenario.Kind = kind; scenario.Extent = 12;
            using (var a = new SimulationWorld(settings, scenario, Phase1ModuleFactory.Create(settings)))
            using (var b = new SimulationWorld(settings, scenario, Phase1ModuleFactory.Create(settings)))
            {
                for (int i = 0; i < count; i++)
                {
                    Assert.That(a.Snapshot.Positions[i], Is.EqualTo(b.Snapshot.Positions[i]));
                    Assert.That(a.Snapshot.Goals[i], Is.EqualTo(b.Snapshot.Goals[i]));
                    for (int j = 0; j < i; j++) Assert.That(math.distance(a.Snapshot.Positions[i], a.Snapshot.Positions[j]), Is.GreaterThanOrEqualTo(2 * scenario.Radius));
                }
                for (int tick = 0; tick < 20; tick++) { a.Step(); b.Step(); }
                for (int i = 0; i < count; i++) Assert.That(a.Snapshot.Positions[i], Is.EqualTo(b.Snapshot.Positions[i]));
            }
        }

        [Test]
        public void CrowdedInvalidScenarioFailsAtInitialization()
        {
            var settings = Settings(100);
            var scenario = ScenarioSettings.Default; scenario.Extent = 0.1f;
            Assert.Throws<ArgumentException>(() => new SimulationWorld(settings, scenario, Phase1ModuleFactory.Create(settings)));
        }

        [Test]
        public void VoSelectsVelocityOutsideObservedObstacleAndRespectsUnequalSpeeds()
        {
            var settings = Settings(2); settings.Avoidance = AvoidanceAlgorithm.VO;
            using (var world = CustomWorld(settings, new[] { float3.zero, new float3(4,0,0) },
                new[] { new float3(10,0,0), new float3(4,0,0) }, radii: new[] { 0.3f, 0.7f }, speeds: new[] { 2f, 0.5f }))
            {
                world.Step(); var step = world.DebugSnapshot;
                Assert.That(step.Status[0], Is.EqualTo(SolveStatus.Success));
                Assert.That(VelocityObstacle2D.Contains(new float2(4,0), world.Snapshot.Velocities[0].xz, 1 + settings.Vo.SafetyMargin, settings.TimeHorizon), Is.False);
                Assert.That(math.length(world.Snapshot.Velocities[0]), Is.LessThanOrEqualTo(2f + 1e-5f));
                Assert.That(math.length(world.Snapshot.Velocities[1]), Is.LessThanOrEqualTo(0.5f + 1e-5f));
            }
        }

        [Test]
        public void OverlapFallbackSeparatesCoincidentAgentsWithoutNan()
        {
            var settings = Settings(2); settings.Avoidance = AvoidanceAlgorithm.VO;
            using (var world = CustomWorld(settings, new[] { float3.zero, float3.zero }, new[] { float3.zero, float3.zero }))
            {
                world.Step();
                Assert.That(world.DebugSnapshot.Status[0], Is.EqualTo(SolveStatus.Fallback));
                Assert.That(world.DebugSnapshot.Status[1], Is.EqualTo(SolveStatus.Fallback));
                Assert.That(world.Snapshot.Positions[0].x, Is.LessThan(0));
                Assert.That(world.Snapshot.Positions[1].x, Is.GreaterThan(0));
                Assert.That(math.all(math.isfinite(world.Snapshot.Positions[0])), Is.True);
            }
        }

        [Test]
        public void SweptQualityCheckFindsBetweenTickCrossing()
        {
            var settings = Settings(2); settings.FixedDeltaTime = 1;
            var points = new[] { new float3(-1,0,0), new float3(1,0,0) };
            using (var world = CustomWorld(settings, points, new[] { points[1], points[0] }))
            {
                world.Step();
                var quality = QualityEvaluator.Evaluate(world.Snapshot, world.DebugSnapshot, 1e-5f);
                Assert.That(quality.OverlappingPairs, Is.Zero);
                Assert.That(quality.SweptCollisionPairs, Is.EqualTo(1));
            }
        }

        [Test]
        public void ClockIsIndependentOfRenderRateAndCountsDroppedTicks()
        {
            foreach (int fps in new[] { 30, 60, 144 })
            {
                var clock = new FixedStepClock(); int ticks = 0;
                for (int frame = 0; frame < fps * 10; frame++) ticks += clock.Advance(1.0 / fps, 1f / 30f, 8);
                Assert.That(ticks, Is.InRange(299, 300));
                Assert.That(clock.DroppedTicks, Is.Zero);
            }
            var stalled = new FixedStepClock();
            Assert.That(stalled.Advance(1, 0.1, 2), Is.EqualTo(2));
            Assert.That(stalled.DroppedTicks, Is.EqualTo(8));
        }

        [TestCase(AvoidanceAlgorithm.None, true)]
        [TestCase(AvoidanceAlgorithm.VO, false)]
        public void HeadOnComparison(AvoidanceAlgorithm algorithm, bool expectCollision)
        {
            var settings = Settings(2); settings.Avoidance = algorithm;
            var scenario = ScenarioSettings.Default; scenario.Kind = ScenarioKind.HeadOnPair; scenario.Extent = 6;
            int collisions = 0; QualityMetrics last = default;
            using (var world = new SimulationWorld(settings, scenario, Phase1ModuleFactory.Create(settings)))
            {
                for (int tick = 0; tick < 600; tick++)
                {
                    world.Step(); last = QualityEvaluator.Evaluate(world.Snapshot, world.DebugSnapshot, settings.Epsilon);
                    collisions += last.SweptCollisionPairs;
                }
                TestContext.WriteLine($"HeadOn {algorithm}: swept pair-ticks={collisions}, arrived={last.Arrived}");
                Assert.That(collisions > 0, Is.EqualTo(expectCollision));
                Assert.That(last.Arrived, Is.EqualTo(2));
            }
        }

        internal static SimulationSettings Settings(int count)
        {
            var settings = SimulationSettings.Default;
            settings.AgentCount = count; settings.Avoidance = AvoidanceAlgorithm.None;
            settings.NeighborDistance = 30; settings.MaxNeighbors = math.max(1, count - 1);
            return settings;
        }

        internal static SimulationWorld CustomWorld(SimulationSettings settings, float3[] positions, float3[] goals,
            int[] ids = null, float[] radii = null, float[] speeds = null)
        {
            IAvoidanceSolver solver = settings.Avoidance == AvoidanceAlgorithm.ORCA ? (IAvoidanceSolver)new OrcaSolver2D() :
                settings.Avoidance == AvoidanceAlgorithm.RVO ? new RvoSolver2D() :
                settings.Avoidance == AvoidanceAlgorithm.VO ? new VoSolver2D() : new PassThroughSolver();
            return new SimulationWorld(settings, ScenarioSettings.Default, new SimulationModules(
                new FixedScenario(positions, goals, ids, radii, speeds), new DirectGoalPreferredVelocity(),
                settings.NeighborSearch == NeighborSearchAlgorithm.SpatialHash ? (INeighborSearch)new SpatialHashNeighborSearch() : new BruteForceNeighborSearch(), solver, new PlanarEulerIntegrator()));
        }

        private sealed class FixedScenario : StatelessModule, IScenarioInitializer
        {
            private readonly float3[] positions, goals;
            private readonly int[] ids;
            private readonly float[] radii, speeds;
            public override string Name => "Test initial state";
            public FixedScenario(float3[] positions, float3[] goals, int[] ids, float[] radii, float[] speeds)
            { this.positions = positions; this.goals = goals; this.ids = ids; this.radii = radii; this.speeds = speeds; }
            public void Initialize(in SimulationSettings settings, in ScenarioSettings scenario, in AgentInitializationView agents)
            {
                var p = agents.Positions; var g = agents.Goals; var id = agents.Ids; var parameters = agents.Parameters;
                for (int i = 0; i < positions.Length; i++)
                {
                    p[i] = positions[i]; g[i] = goals[i]; id[i] = ids == null ? i : ids[i];
                    parameters[i] = new AgentParameters { Radius = radii == null ? 0.35f : radii[i],
                        MaxSpeed = speeds == null ? 2 : speeds[i], ArrivalDistance = 0.05f };
                }
            }
        }
    }
}
