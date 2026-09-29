using NUnit.Framework;
using Unity.Mathematics;

namespace Rvo.Tests
{
    public sealed class TrafficRecoveryTests
    {
        [Test]
        public void CongestionCostSelectsAnAlternativeWithoutRemovingReachability()
        {
            var map = new NavigationGrid(24,16,1,new bool[24*16],0.4f,1);
            var finder = new GridPathfinder(map.Count); var path = new float2[map.Count+2];
            finder.Begin(map,new float2(-8,0.5f),new float2(8,0.5f),true,1,map.Landmarks,new float2(0,0.5f),3);
            finder.Advance(int.MaxValue);
            Assert.That(finder.Status,Is.EqualTo(GridPathStatus.Ready));
            int count = finder.CopyPath(path,0,true); float maxSide = 0;
            for (int i = 1; i < count; i++)
            { Assert.That(map.SegmentClear(path[i-1],path[i],map.ClearanceRadius),Is.True); maxSide = math.max(maxSide,math.abs(path[i].y-0.5f)); }
            Assert.That(maxSide,Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void PriorityResponsibilityIsComplementary()
        {
            float2 p = new float2(4,0), va = new float2(1,0), vb = -va;
            var a = OrcaGeometry2D.Build(p,va,vb,1,2,1f/30,0,1,1e-5f,0.2f);
            var b = OrcaGeometry2D.Build(-p,vb,va,1,2,1f/30,1,0,1e-5f,0.8f);
            var equal = OrcaGeometry2D.Build(p,va,vb,1,2,1f/30,0,1,1e-5f);
            Assert.That(math.distance(a.Normal,-b.Normal),Is.LessThan(1e-5f));
            Assert.That(a.Offset+b.Offset,Is.EqualTo(2*equal.Offset).Within(1e-5f));
        }

        [TestCase(ExecutionBackend.Reference,2)]
        [TestCase(ExecutionBackend.JobsBurst,2)]
        [TestCase(ExecutionBackend.JobsBurst,6)]
        public void OpposingAgentsClearSingleLaneCorridor(ExecutionBackend backend,int count)
        {
            var config = Config(32,16); var cells = new bool[32*16];
            for (int x = 12; x < 20; x++) for (int z = 0; z < 16; z++) if (z < 7 || z > 9) cells[z*32+x] = true;
            var scenario = ScenarioSettings.Default; scenario.Radius = 0.8f; scenario.ArrivalDistance = 0.15f;
            var settings = SimulationSettings.Default; settings.AgentCount = count; settings.MaxNeighbors = count-1;
            settings.Avoidance = AvoidanceAlgorithm.ORCA; settings.Backend = backend; settings.TimeHorizon = 2;
            settings.NeighborDistance = 12; settings.PreferredSideBias = 0.08f;
            var map = new NavigationGrid(32,16,1,cells,scenario.Radius+config.SafetyMargin,1);
            var navigation = new GridNavigation(config,count,scenario.Radius,map);
            using (var world = new SimulationWorld(settings,scenario,new SimulationModules(new PairScenario(),navigation,
                new SpatialHashNeighborSearch(),new GridAvoidanceSolver(navigation),new PlanarEulerIntegrator())))
            {
                int yields = 0;
                for (int tick = 0; tick < (count == 2 ? 1200 : 1800); tick++)
                {
                    world.Step(); yields += navigation.YieldingCount;
                    Assert.That(QualityEvaluator.Evaluate(world.Snapshot,world.DebugSnapshot,settings.Epsilon).SweptCollisionPairs,Is.Zero);
                    for (int i = 0; i < count; i++) Assert.That(map.SegmentClear(world.DebugSnapshot.Inputs.Positions[i].xz,
                        world.Snapshot.Positions[i].xz,scenario.Radius),Is.True);
                }
                TestContext.WriteLine($"yield agent-ticks={yields}; positions={world.Snapshot.Positions[0]}, {world.Snapshot.Positions[1]}; stalled={navigation.StalledCount}; replans={navigation.RecoveryReplans}");
                Assert.That(yields,Is.GreaterThan(0));
                for (int i = 0; i < count; i++) Assert.That(math.distance(world.Snapshot.Positions[i],world.Snapshot.Goals[i]),Is.LessThanOrEqualTo(scenario.ArrivalDistance),$"Agent {i}: {world.Snapshot.Positions[i]}");
            }
        }

        [TestCase(ExecutionBackend.Reference)]
        [TestCase(ExecutionBackend.JobsBurst)]
        public void MarginStrandedAgentReturnsToNavigationAndRetries(ExecutionBackend backend)
        {
            var config = Config(16,16); var cells = new bool[16*16]; cells[8*16+8] = true;
            var scenario = ScenarioSettings.Default;
            var settings = SimulationSettings.Default; settings.AgentCount = settings.MaxNeighbors = 1;
            settings.Avoidance = AvoidanceAlgorithm.ORCA; settings.Backend = backend;
            var map = new NavigationGrid(16,16,1,cells,scenario.Radius+config.SafetyMargin,1);
            var navigation = new GridNavigation(config,1,scenario.Radius,map);
            using (var world = new SimulationWorld(settings,scenario,new SimulationModules(new MarginScenario(),navigation,
                new BruteForceNeighborSearch(),new GridAvoidanceSolver(navigation),new PlanarEulerIntegrator())))
            {
                for (int tick = 0; tick < 240; tick++)
                { world.Step(); Assert.That(map.SegmentClear(world.DebugSnapshot.Inputs.Positions[0].xz,world.Snapshot.Positions[0].xz,scenario.Radius),Is.True); }
                Assert.That(navigation.ReplanCount,Is.GreaterThan(1));
                Assert.That(navigation.Path(0).Status,Is.EqualTo(GridPathStatus.Arrived));
            }
        }
        private static NavigationSettings Config(int width, int height)
        {
            var config = NavigationSettings.Default; config.Width = width; config.Height = height;
            config.ObstacleCount = 0; config.ObstacleMinSize = config.ObstacleMaxSize = 1; return config;
        }
        private sealed class PairScenario : StatelessModule, IScenarioInitializer
        {
            public override string Name => "Single lane opposing traffic";
            public void Initialize(in SimulationSettings settings,in ScenarioSettings scenario,in AgentInitializationView view)
            {
                var data = view;
                for (int i = 0; i < settings.AgentCount; i++)
                {
                    float side = i%2 == 0 ? -1 : 1;
                    data.Ids[i] = i; data.Positions[i] = new float3(side*(8+i/2*2.2f),0,0.5f);
                    data.Goals[i] = new float3(-side*(14-i/2*2.2f),0,0.5f);
                    data.Parameters[i] = new AgentParameters { Radius = scenario.Radius,MaxSpeed = scenario.MaxSpeed,ArrivalDistance = scenario.ArrivalDistance };
                }
            }
        }
        private sealed class MarginScenario : StatelessModule, IScenarioInitializer
        {
            public override string Name => "Inside padding, outside physical obstacle";
            public void Initialize(in SimulationSettings settings,in ScenarioSettings scenario,in AgentInitializationView view)
            {
                var data = view; data.Ids[0] = 0; data.Positions[0] = new float3(-0.38f,0,0.5f); data.Goals[0] = new float3(-4,0,0.5f);
                data.Parameters[0] = new AgentParameters { Radius = scenario.Radius,MaxSpeed = scenario.MaxSpeed,ArrivalDistance = scenario.ArrivalDistance };
            }
        }
    }
}
