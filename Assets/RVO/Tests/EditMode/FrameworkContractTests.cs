using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo.Tests
{
    public sealed class FrameworkContractTests
    {
        [Test]
        public void InvalidConfigurationIsRejected()
        {
            var settings = SimulationSettings.Default;
            settings.FixedDeltaTime = float.NaN;
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
            settings = SimulationSettings.Default;
            settings.AgentCount = int.MaxValue;
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
        }

        [TestCase(AvoidanceAlgorithm.RVO)]
        [TestCase(AvoidanceAlgorithm.ORCA)]
        public void Phase1AlgorithmsAreReady(AvoidanceAlgorithm algorithm)
        {
            var settings = SimulationSettings.Default;
            settings.Avoidance = algorithm; settings.AgentCount = 2;
            using (var modules = Phase1ModuleFactory.Create(settings))
            {
                Assert.That(modules.GetReadinessIssue(), Is.Null);
                using (var world = new SimulationWorld(settings, ScenarioSettings.Default, modules)) world.Step();
            }
        }

        [Test]
        public void Full3DDoesNotFallBackToPlanarModules()
        {
            var settings = SimulationSettings.Default;
            settings.Dimension = SimulationDimension.Full3D;
            Assert.Throws<NotSupportedException>(() => Phase1ModuleFactory.Create(settings));
        }

        [Test]
        public void WorldCommitsTogetherAndRejectsInvalidLifecycleCalls()
        {
            var settings = SimulationSettings.Default;
            settings.AgentCount = 1;
            var modules = new SimulationModules(new FakeStage(), new FakeStage(),
                new FakeStage(), new FakeStage(), new FakeStage());
            var world = new SimulationWorld(settings, ScenarioSettings.Default, modules);
            try
            {
                var previous = world.Snapshot;
                world.ScheduleStep();
                Assert.That(previous.Positions[0].x, Is.EqualTo(0f));
                Assert.That(previous.Velocities[0].x, Is.EqualTo(0f));
                Assert.That(world.Tick, Is.EqualTo(0));
                Assert.Throws<InvalidOperationException>(() => world.ScheduleStep());
                Assert.Throws<InvalidOperationException>(() => { var ignored = world.Snapshot; });
                world.CompleteStep();
                Assert.That(world.Tick, Is.EqualTo(1));
                Assert.That(world.Snapshot.Positions[0].x, Is.EqualTo(7f));
                Assert.That(world.Snapshot.Velocities[0].x, Is.EqualTo(1f));
                Assert.Throws<InvalidOperationException>(() => world.CompleteStep());
                world.Step();
                Assert.That(world.Tick, Is.EqualTo(2));
                Assert.That(world.Snapshot.Positions[0].x, Is.EqualTo(14f));
            }
            finally { world.Dispose(); }
            Assert.DoesNotThrow(() => world.Dispose());
            Assert.Throws<InvalidOperationException>(() => world.Step());
        }

        // Synchronous fixed-value test doubles. These intentionally contain no simulation algorithms.
        private sealed class FakeStage : IScenarioInitializer, IPreferredVelocityProvider,
            INeighborSearch, IAvoidanceSolver, IMotionIntegrator
        {
            public string Name => "Contract test double";
            public bool IsImplemented => true;
            public void Dispose() { }

            public void Initialize(in SimulationSettings settings, in ScenarioSettings scenario,
                in AgentInitializationView agents)
            {
                // Copy borrowed handles: do not mutate readonly view fields themselves.
                var ids = agents.Ids;
                var positions = agents.Positions;
                var velocities = agents.Velocities;
                var goals = agents.Goals;
                var parameters = agents.Parameters;
                for (int i = 0; i < ids.Length; i++)
                {
                    ids[i] = i;
                    positions[i] = new float3(0, settings.PlaneHeight, 0);
                    velocities[i] = float3.zero;
                    goals[i] = new float3(100, settings.PlaneHeight, 0);
                    parameters[i] = new AgentParameters { Radius = 0.5f, MaxSpeed = 2f };
                }
            }

            public JobHandle Schedule(in StepContext context, in AgentReadView agents,
                NativeArray<float3> preferred, JobHandle dependency)
            {
                dependency.Complete();
                preferred[0] = new float3(1, 0, 0);
                return default;
            }

            public JobHandle Schedule(in StepContext context, in AgentReadView agents,
                in NeighborWriteView neighbors, JobHandle dependency)
            {
                dependency.Complete();
                var counts = neighbors.Counts;
                var dropped = neighbors.DroppedCounts;
                counts[0] = 0;
                dropped[0] = 0;
                return default;
            }

            public JobHandle Schedule(in StepContext context, in AgentReadView agents,
                NativeArray<float3>.ReadOnly preferred, in NeighborReadView neighbors,
                in MotionOutput output, JobHandle dependency)
            {
                dependency.Complete();
                var velocities = output.Velocities;
                var status = output.Status;
                velocities[0] = preferred[0];
                status[0] = SolveStatus.Success;
                return default;
            }

            public JobHandle Schedule(in StepContext context, in AgentReadView agents,
                NativeArray<float3>.ReadOnly chosenVelocities, NativeArray<float3> nextPositions,
                JobHandle dependency)
            {
                dependency.Complete();
                nextPositions[0] = agents.Positions[0] + new float3(7, 0, 0);
                return default;
            }
        }
    }
}
