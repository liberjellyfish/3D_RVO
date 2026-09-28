using System;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Rvo.Tests
{
    public sealed class Phase2OptimizationTests
    {
        [TestCase(ExecutionBackend.Reference)]
        [TestCase(ExecutionBackend.JobsBurst)]
        public void KdTreeMatchesIndependentSortWhileAgentsMove(ExecutionBackend backend)
        {
            var random = new Unity.Mathematics.Random(741);
            foreach (int count in new[] { 1, 8, 9, 97, 256 })
            foreach (float range in new[] { 0.1f, 5f, 1000f })
            {
                var points = new float3[count]; var goals = new float3[count];
                var ids = Enumerable.Range(0,count).Select(i => count-i).ToArray();
                for (int i = 0; i < count; i++)
                {
                    // 重合、共线、簇状和负坐标；ID 故意不同于槽位。
                    points[i] = i % 4 == 0 ? float3.zero : i % 4 == 1 ? new float3(i,0,0) : new float3(random.NextFloat(-20,20),0,random.NextFloat(-20,20));
                    goals[i] = -points[i] + new float3(3,0,7);
                }
                var settings = Phase13Tests.Settings(count); settings.MaxNeighbors = 7;
                settings.NeighborDistance = range; settings.Backend = backend; settings.NeighborSearch = NeighborSearchAlgorithm.KdTree;
                using (var world = Phase13Tests.CustomWorld(settings,points,goals,ids))
                for (int tick = 0; tick < 5; tick++)
                {
                    world.Step(); var input = world.DebugSnapshot.Inputs; var neighbors = world.DebugSnapshot.Neighbors;
                    for (int i = 0; i < count; i++)
                    {
                        int self = i;
                        var expected = Enumerable.Range(0,count).Where(j => j != self && math.distancesq(input.Positions[j].xz,input.Positions[self].xz) <= range*range)
                            .OrderBy(j => math.distancesq(input.Positions[j].xz,input.Positions[self].xz)).ThenBy(j => ids[j]).ToArray();
                        Assert.That(neighbors.Counts[i], Is.EqualTo(math.min(7,expected.Length)));
                        Assert.That(neighbors.DroppedCounts[i], Is.EqualTo(math.max(0,expected.Length-7)));
                        for (int slot = 0; slot < neighbors.Counts[i]; slot++) Assert.That(neighbors.Indices[i*7+slot], Is.EqualTo(expected[slot]));
                    }
                }
            }
        }

        [Test]
        public void WeightedLandmarkSearchRespectsCostBoundAndEveryPathSegment()
        {
            var random = new Unity.Mathematics.Random(321);
            for (int trial = 0; trial < 12; trial++)
            {
                var occupied = new bool[24*24];
                for (int i = 0; i < 100; i++) occupied[random.NextInt(occupied.Length)] = true;
                var map = new NavigationGrid(24,24,1,occupied,0.3f,1);
                for (int pair = 0; pair < 12; pair++)
                {
                    int a = map.SpawnCell(random.NextInt(map.SpawnCellCount)), b = map.SpawnCell(random.NextInt(map.SpawnCellCount));
                    var exact = new GridPathfinder(map.Count); exact.Begin(map,map.Center(a),map.Center(b),false); exact.Advance(int.MaxValue);
                    Assert.That(map.Landmarks.LowerBound(a,b), Is.LessThanOrEqualTo(exact.LastGraphCost+1e-4f));
                    foreach (float weight in new[] { 1f, 1.2f, 2f })
                    {
                        var fast = new GridPathfinder(map.Count); fast.Begin(map,map.Center(a),map.Center(b),false,weight,map.Landmarks);
                        while (fast.Status == GridPathStatus.Pending) Assert.That(fast.Advance(7), Is.LessThanOrEqualTo(7));
                        Assert.That(fast.Status, Is.EqualTo(GridPathStatus.Ready));
                        Assert.That(fast.LastGraphCost, Is.LessThanOrEqualTo(exact.LastGraphCost*weight+1e-4f));
                        var path = new float2[map.Count+2]; int length = fast.CopyPath(path,0,true);
                        for (int i = 1; i < length; i++) Assert.That(map.SegmentClear(path[i-1],path[i],map.ClearanceRadius), Is.True);
                    }
                }
            }
        }

        [Test]
        public void NearContactCanRestOrSeparateButCannotApproachOrOverlap()
        {
            float2 a = float2.zero, b = new float2(1.0005f,0);
            Assert.That(GridAvoidanceSolver.PairSafePrefix(a,b,a,b,1), Is.EqualTo(1));
            Assert.That(GridAvoidanceSolver.PairSafePrefix(a,b,a-new float2(1,0),b,1), Is.EqualTo(1));
            Assert.That(GridAvoidanceSolver.PairSafePrefix(a,b,a+new float2(1,0),b,1), Is.Zero);
            Assert.That(GridAvoidanceSolver.PairSafePrefix(a,new float2(0.99f,0),a,b,1), Is.EqualTo(-1));
        }

        [Test]
        public void AnalyticSafetyPrefixPassesIndependentClosestApproachCheck()
        {
            var random = new Unity.Mathematics.Random(3451);
            for (int i = 0; i < 10000; i++)
            {
                float2 a = random.NextFloat2(-20,20), b = random.NextFloat2(-20,20);
                float radius = random.NextFloat(0.1f,3);
                if (math.distance(a,b) < radius) continue;
                float2 da = random.NextFloat2(-10,10), db = random.NextFloat2(-10,10);
                float scale = GridAvoidanceSolver.PairSafePrefix(a,b,a+da,b+db,radius);
                Assert.That(scale, Is.InRange(0f,1f));
                double2 p = (double2)a-b, d = ((double2)da-db)*scale;
                double t = math.clamp(-math.dot(p,d)/math.max(1e-30,math.lengthsq(d)),0,1);
                Assert.That(math.length(p+d*t), Is.GreaterThanOrEqualTo(radius-1e-5));
            }
        }

        [Test]
        public void ChangedGoalCancelsAnInFlightSearch()
        {
            const int count = 1;
            var config = NavigationSettings.Default; config.Width = config.Height = 32; config.ObstacleCount = 0;
            config.ObstacleMinSize = config.ObstacleMaxSize = 1; config.PathExpansionsPerTick = 1;
            var cells = new bool[32*32]; for (int z = 0; z < 29; z++) cells[z*32+16] = true;
            var map = new NavigationGrid(32,32,1,cells,0.35f+config.SafetyMargin,1);
            using (var ids = new NativeArray<int>(count,Allocator.TempJob))
            using (var positions = new NativeArray<float3>(count,Allocator.TempJob))
            using (var velocities = new NativeArray<float3>(count,Allocator.TempJob))
            using (var goals = new NativeArray<float3>(count,Allocator.TempJob))
            using (var parameters = new NativeArray<AgentParameters>(count,Allocator.TempJob))
            using (var preferred = new NativeArray<float3>(count,Allocator.TempJob))
            using (var navigation = new GridNavigation(config,count,0.35f,map))
            {
                var p = positions; p[0] = new float3(-8,0,-10);
                var g = goals; g[0] = new float3(8,0,-10);
                var a = parameters; a[0] = new AgentParameters { Radius = 0.35f, MaxSpeed = 2, ArrivalDistance = 0.1f };
                var view = new AgentReadView(ids,positions,velocities,goals,parameters);
                var settings = Phase13Tests.Settings(count);
                navigation.Schedule(new StepContext(0,settings),view,preferred,default).Complete();
                Assert.That(navigation.Path(0).Status, Is.EqualTo(GridPathStatus.Pending));
                int request = navigation.Path(0).RequestId;
                g[0] = new float3(-8,0,-5);
                for (int tick = 1; tick < 20; tick++) navigation.Schedule(new StepContext(tick,settings),view,preferred,default).Complete();
                Assert.That(navigation.Path(0).Goal, Is.EqualTo(g[0].xz));
                Assert.That(navigation.Path(0).RequestId, Is.GreaterThan(request));
                Assert.That(navigation.Path(0).Status, Is.EqualTo(GridPathStatus.Ready));
                Assert.That(preferred[0].z, Is.GreaterThan(1));
            }
        }

        [Test]
        public void CrowdedFallbackPreservesHardWallConstraints()
        {
            using (var planes = new NativeArray<VelocityHalfPlane2D>(2,Allocator.Temp))
            {
                var p = planes;
                p[0] = new VelocityHalfPlane2D { Normal = new float2(1,0), Offset = 0 };
                p[1] = new VelocityHalfPlane2D { Normal = new float2(-1,0), Offset = 1 };
                Assert.That(PlanarVelocityOptimizer.Solve(p,new float2(-1,1),2,1e-5f,out var velocity,1), Is.EqualTo(SolveStatus.Infeasible));
                Assert.That(velocity.x, Is.GreaterThanOrEqualTo(-1e-5f));
                Assert.That(velocity.y, Is.GreaterThan(0.9f), "Keep tangential progress along the wall.");
            }
        }

        [TestCase(ExecutionBackend.Reference)]
        [TestCase(ExecutionBackend.JobsBurst)]
        public void LocalSafetyConflictDoesNotStopDistantAgent(ExecutionBackend backend)
        {
            var config = NavigationSettings.Default; config.Width = config.Height = 64;
            config.ObstacleCount = 0; config.ObstacleMinSize = config.ObstacleMaxSize = 1;
            var settings = Phase13Tests.Settings(3); settings.Backend = backend;
            settings.Avoidance = AvoidanceAlgorithm.ORCA; settings.NeighborDistance = 0.001f; settings.FixedDeltaTime = 1;
            var scenario = ScenarioSettings.Default; scenario.Radius = 0.35f; scenario.MaxSpeed = 2;
            var map = NavigationBake.Generate(config,scenario.Radius);
            var navigation = new GridNavigation(config,3,scenario.Radius,map); var solver = new GridAvoidanceSolver(navigation);
            var starts = new[] { new float3(-1,0,0), new float3(1,0,0), new float3(-10,0,15) };
            var goals = new[] { new float3(10,0,0), new float3(-10,0,0), new float3(10,0,15) };
            using (var world = new SimulationWorld(settings,scenario,new SimulationModules(new FixedScenario(starts,goals),navigation,
                new SpatialHashNeighborSearch(),solver,new PlanarEulerIntegrator())))
            {
                world.Step();
                Assert.That(solver.LastLimitedAgents, Is.EqualTo(2));
                Assert.That(solver.LastSafetyScale, Is.LessThan(1));
                Assert.That(world.Snapshot.Velocities[2].x, Is.EqualTo(2).Within(1e-5));
                Assert.That(QualityEvaluator.Evaluate(world.Snapshot,world.DebugSnapshot,settings.Epsilon).SweptCollisionPairs, Is.Zero);
            }
        }

        [Test]
        public void VisibleGoalsBypassAnExpensiveQueuedSearch()
        {
            var config = NavigationSettings.Default; config.Width = config.Height = 32; config.ObstacleCount = 0; config.ObstacleMinSize = config.ObstacleMaxSize = 1;
            config.PathExpansionsPerTick = config.PathRequestsPerTick = 1;
            var cells = new bool[32*32]; for (int z = 0; z < 29; z++) cells[z*32+16] = true;
            var map = new NavigationGrid(32,32,1,cells,0.35f+config.SafetyMargin,1);
            var starts = new[] { new float3(-8,0,-10), new float3(-10,0,5), new float3(10,0,5) };
            var goals = new[] { new float3(8,0,-10), new float3(-10,0,10), new float3(10,0,10) };
            var settings = Phase13Tests.Settings(3); var scenario = ScenarioSettings.Default;
            var navigation = new GridNavigation(config,3,scenario.Radius,map);
            using (var world = new SimulationWorld(settings,scenario,new SimulationModules(new FixedScenario(starts,goals),navigation,
                new BruteForceNeighborSearch(),new PassThroughSolver(),new PlanarEulerIntegrator())))
            {
                world.Step(); Assert.That(navigation.Path(0).Status, Is.EqualTo(GridPathStatus.Pending));
                Assert.That(navigation.LastDirectPaths, Is.EqualTo(2)); Assert.That(navigation.LastExpandedNodes, Is.EqualTo(1));
                Assert.That(world.Snapshot.Velocities[1].z, Is.GreaterThan(1)); Assert.That(world.Snapshot.Velocities[2].z, Is.GreaterThan(1));
            }
        }

        [TestCase(ExecutionBackend.Reference)]
        [TestCase(ExecutionBackend.JobsBurst)]
        public void SingleAgentRoundsWallCornerAndArrives(ExecutionBackend backend)
        {
            var config = NavigationSettings.Default; config.Width = config.Height = 32; config.ObstacleCount = 0;
            config.ObstacleMinSize = config.ObstacleMaxSize = 1;
            var cells = new bool[32*32]; for (int z = 0; z < 22; z++) cells[z*32+16] = true;
            var scenario = ScenarioSettings.Default;
            var map = new NavigationGrid(32,32,1,cells,scenario.Radius+config.SafetyMargin,1);
            var settings = Phase13Tests.Settings(1); settings.Avoidance = AvoidanceAlgorithm.ORCA;
            settings.Backend = backend; settings.PreferredSideBias = 0.08f;
            var navigation = new GridNavigation(config,1,scenario.Radius,map); var solver = new GridAvoidanceSolver(navigation);
            using (var world = new SimulationWorld(settings,scenario,new SimulationModules(
                new FixedScenario(new[] { new float3(-4,0,-5) },new[] { new float3(4,0,-5) }),navigation,
                new KdTreeNeighborSearch(),solver,new PlanarEulerIntegrator())))
            {
                for (int tick = 0; tick < 900; tick++)
                {
                    world.Step();
                    Assert.That(map.SegmentClear(world.DebugSnapshot.Inputs.Positions[0].xz,world.Snapshot.Positions[0].xz,scenario.Radius), Is.True);
                }
                Assert.That(math.distance(world.Snapshot.Positions[0],world.Snapshot.Goals[0]), Is.LessThanOrEqualTo(scenario.ArrivalDistance));
            }
        }

        private sealed class FixedScenario : StatelessModule, IScenarioInitializer
        {
            private readonly float3[] starts, goals;
            public override string Name => "Controlled navigation fixture";
            public FixedScenario(float3[] starts, float3[] goals) { this.starts = starts; this.goals = goals; }
            public void Initialize(in SimulationSettings settings, in ScenarioSettings scenario, in AgentInitializationView view)
            {
                var output = view;
                for (int i = 0; i < starts.Length; i++)
                {
                    output.Ids[i] = i; output.Positions[i] = starts[i]; output.Goals[i] = goals[i];
                    output.Parameters[i] = new AgentParameters { Radius = scenario.Radius, MaxSpeed = scenario.MaxSpeed, ArrivalDistance = scenario.ArrivalDistance };
                }
            }
        }
    }
}
