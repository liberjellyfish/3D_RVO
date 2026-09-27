using System;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Mathematics;

namespace Rvo
{
    public enum WorldState { Ready, Scheduled, Faulted, Disposed }

    /// <summary>Owns memory and the tick boundary. No scene, Transform, physics, map, or renderer dependency.</summary>
    public sealed class SimulationWorld : IDisposable
    {
        private readonly SimulationSettings settings;
        private readonly SimulationModules modules;
        private AgentStorage agents;
        private NeighborBuffers neighbors;
        private JobHandle pending;
        private long stepStart, allocationStart;
        public SimulationMetrics LastMetrics { get; private set; }
        private SimulationMetrics metrics;
        private static double Milliseconds(long start) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        private double FinishStage(long start)
        {
            if (!settings.MeasureStages) return double.NaN;
            pending.Complete(); // 阶段计时包含实际工作与等待，绝非仅 Schedule 时间。
            return Milliseconds(start);
        }
        public long Tick { get; private set; }
        public SimulationSettings Settings => settings;
        public WorldState State { get; private set; }

        public AgentReadView Snapshot
        {
            get { Require(WorldState.Ready); return agents.Read; }
        }

        // 与邻居查询对应的是上一步输入，不能用提交后的坐标重画旧 VO。
        public StepDebugView DebugSnapshot
        {
            get
            {
                Require(WorldState.Ready);
                if (Tick == 0) throw new InvalidOperationException("尚未执行仿真步骤。");
                return new StepDebugView(agents.Previous, neighbors.Read, agents.Preferred.AsReadOnly(), agents.Status,
                        modules.Avoidance is OrcaSolver2D orca ? orca.Constraints : default);
            }
        }

        public SimulationWorld(in SimulationSettings settings, in ScenarioSettings scenario,
            SimulationModules modules)
        {
            this.settings = settings;
            this.modules = modules ?? throw new ArgumentNullException(nameof(modules));
            try
            {
                settings.Validate();
                scenario.Validate(settings.AgentCount);
                var issue = modules.GetReadinessIssue();
                if (issue != null) throw new NotSupportedException(issue);
                agents = new AgentStorage(settings.AgentCount);
                neighbors = new NeighborBuffers(settings.AgentCount, settings.MaxNeighbors);
                modules.Scenario.Initialize(settings, scenario, agents.Initialization);
                ValidateInitialState();
                State = WorldState.Ready;
            }
            catch { Dispose(); throw; }
        }

        public void ScheduleStep()
        {
            Require(WorldState.Ready);
            stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
            allocationStart = GC.GetAllocatedBytesForCurrentThread();
            metrics = default;
            var context = new StepContext(Tick, settings);
            var snapshot = agents.Read;
            State = WorldState.Scheduled;
            try
            {
                // 参考与 Jobs 共用阶段边界；正常运行不强制逐阶段 Complete。
                long stage = System.Diagnostics.Stopwatch.GetTimestamp();
                pending = modules.PreferredVelocity.Schedule(context, snapshot, agents.Preferred, default);
                metrics.PreferredMilliseconds = FinishStage(stage);
                stage = System.Diagnostics.Stopwatch.GetTimestamp();
                pending = modules.Neighbors.Schedule(context, snapshot, neighbors.Write, pending);
                metrics.NeighborMilliseconds = FinishStage(stage);
                stage = System.Diagnostics.Stopwatch.GetTimestamp();
                pending = modules.Avoidance.Schedule(context, snapshot, agents.Preferred.AsReadOnly(),
                    neighbors.Read, agents.Output, pending);
                metrics.AvoidanceMilliseconds = FinishStage(stage);
                stage = System.Diagnostics.Stopwatch.GetTimestamp();
                pending = modules.Integrator.Schedule(context, snapshot,
                    agents.Output.Velocities.AsReadOnly(), agents.NextPositions, pending);
                metrics.IntegrationMilliseconds = FinishStage(stage);
            }
            catch
            {
                State = WorldState.Faulted;
                pending.Complete();
                throw;
            }
        }

        public void CompleteStep()
        {
            Require(WorldState.Scheduled);
            try
            {
                pending.Complete();
                // 所有 Agent 求解完才统一交换，避免前面的 Agent 污染后面的输入。
                agents.Commit();
                Tick++;
                metrics.Tick = Tick;
                metrics.TotalSimulationMilliseconds = Milliseconds(stepStart);
                metrics.ManagedAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                LastMetrics = metrics;
                State = WorldState.Ready;
            }
            catch { State = WorldState.Faulted; throw; }
        }

        public void Step() { ScheduleStep(); CompleteStep(); }

        public void Dispose()
        {
            if (State == WorldState.Disposed) return;
            // No owner releases native memory while a job still references it.
            pending.Complete();
            agents?.Dispose();
            neighbors?.Dispose();
            modules.Dispose();
            State = WorldState.Disposed;
        }

        private void Require(WorldState expected)
        {
            if (State != expected) throw new InvalidOperationException($"Expected {expected}; world is {State}.");
        }

        private void ValidateInitialState()
        {
            var read = agents.Read;
            var ids = new HashSet<int>(); // Startup only, never in the step hot path.
            for (int i = 0; i < read.Count; i++)
            {
                var p = read.Parameters[i];
                if (!ids.Add(read.Ids[i]) || !math.all(math.isfinite(read.Positions[i])) ||
                    !math.all(math.isfinite(read.Velocities[i])) || !math.all(math.isfinite(read.Goals[i])) ||
                    !math.isfinite(p.Radius) || p.Radius <= 0 || !math.isfinite(p.MaxSpeed) || p.MaxSpeed <= 0 ||
                    !math.isfinite(p.ArrivalDistance) || p.ArrivalDistance < 0 ||
                    math.length(read.Velocities[i]) > p.MaxSpeed + settings.Epsilon)
                    throw new ArgumentException($"Invalid initial data at agent index {i}.");
                if (settings.Dimension == SimulationDimension.PlanarXZ &&
                    (math.abs(read.Positions[i].y - settings.PlaneHeight) > settings.Epsilon ||
                     math.abs(read.Goals[i].y - settings.PlaneHeight) > settings.Epsilon ||
                     math.abs(read.Velocities[i].y) > settings.Epsilon))
                    throw new ArgumentException($"Agent {i} is not on the configured XZ plane.");
            }
        }
    }
}
