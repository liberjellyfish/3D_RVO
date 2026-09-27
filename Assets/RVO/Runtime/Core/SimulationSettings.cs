using System;
using Unity.Mathematics;

namespace Rvo
{
    public enum SimulationDimension { PlanarXZ, Full3D }
    public enum AvoidanceAlgorithm { None, VO, RVO, ORCA }
    public enum NeighborSearchAlgorithm { BruteForce, SpatialHash }
    public enum ExecutionBackend { Reference, JobsBurst }
    public enum SolveStatus { NotSolved, Success, Fallback, Infeasible, InvalidInput }

    /// <summary>Value snapshot: changing an authoring asset never mutates a running world.</summary>
    [Serializable]
    public struct SimulationSettings
    {
        public SimulationDimension Dimension;
        public AvoidanceAlgorithm Avoidance;
        public NeighborSearchAlgorithm NeighborSearch;
        public int AgentCount;
        public int MaxNeighbors;
        public float FixedDeltaTime;
        public float PlaneHeight;
        public float NeighborDistance;
        public float TimeHorizon;
        public float CellSize;
        public float Epsilon;
        public VoSamplingSettings Vo;
        public ExecutionBackend Backend;
        public bool MeasureStages;
        // 求解前旋转期望方向，0 保留原始对称场景。
        public float PreferredSideBias;

        public static SimulationSettings Default => new SimulationSettings
        {
            Dimension = SimulationDimension.PlanarXZ,
            Avoidance = AvoidanceAlgorithm.VO,
            NeighborSearch = NeighborSearchAlgorithm.BruteForce,
            AgentCount = 100,
            MaxNeighbors = 32,
            FixedDeltaTime = 1f / 30f,
            NeighborDistance = 10f,
            TimeHorizon = 5f,
            CellSize = 5f,
            Epsilon = 1e-5f,
            Vo = VoSamplingSettings.Default
        };

        public void Validate()
        {
            if (!Enum.IsDefined(typeof(SimulationDimension), Dimension) ||
                !Enum.IsDefined(typeof(AvoidanceAlgorithm), Avoidance) ||
                !Enum.IsDefined(typeof(NeighborSearchAlgorithm), NeighborSearch) ||
                !Enum.IsDefined(typeof(ExecutionBackend), Backend))
                throw new ArgumentException("Unknown simulation mode.");
            if (AgentCount <= 0 || MaxNeighbors <= 0)
                throw new ArgumentOutOfRangeException(nameof(AgentCount), "Counts must be positive.");
            if ((long)AgentCount * MaxNeighbors > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(MaxNeighbors), "Neighbor storage exceeds index range.");
            RequirePositive(FixedDeltaTime, nameof(FixedDeltaTime));
            RequirePositive(NeighborDistance, nameof(NeighborDistance));
            RequirePositive(TimeHorizon, nameof(TimeHorizon));
            RequirePositive(CellSize, nameof(CellSize));
            RequirePositive(Epsilon, nameof(Epsilon));
            Vo.Validate();
            if (!math.isfinite(PreferredSideBias) || math.abs(PreferredSideBias) > 0.25f)
                throw new ArgumentOutOfRangeException(nameof(PreferredSideBias));
            if (!math.isfinite(PlaneHeight)) throw new ArgumentException("PlaneHeight must be finite.");
        }

        private static void RequirePositive(float value, string name)
        {
            if (!math.isfinite(value) || value <= 0f) throw new ArgumentOutOfRangeException(name);
        }
    }

    public readonly struct StepContext
    {
        public readonly long Tick;
        public readonly SimulationSettings Settings;
        public float DeltaTime => Settings.FixedDeltaTime;
        public double SimulationTime => Tick * (double)DeltaTime;

        public StepContext(long tick, in SimulationSettings settings)
        {
            Tick = tick;
            Settings = settings;
        }
    }
}
