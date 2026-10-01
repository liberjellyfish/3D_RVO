using System;
using Unity.Mathematics;

namespace Rvo
{
    [Serializable]
    public struct VolumeSettings
    {
        public int Resolution;
        public float CellSize, SafetyMargin;
        public int SearchSlots, SearchCapacity, ExpansionsPerTick, RequestsPerTick;
        public float HeuristicWeight;
        public bool UseCoarseRoutes;
        public int AcceptanceTicks;
        public static VolumeSettings Default => new VolumeSettings
        {
            Resolution = 256, CellSize = 1, SafetyMargin = 0.08f,
            SearchSlots = 2, SearchCapacity = 262144, ExpansionsPerTick = 4096,
            RequestsPerTick = 16, HeuristicWeight = 1.5f, UseCoarseRoutes = true, AcceptanceTicks = 18000
        };
        public void Validate(float radius)
        {
            if (Resolution < 4 || Resolution > 256 || !math.isfinite(CellSize) || CellSize <= 0 ||
                !math.isfinite(SafetyMargin) || SafetyMargin < 0.001f || !math.isfinite(radius) || radius <= 0 ||
                radius + SafetyMargin >= Resolution * CellSize / 2 ||
                SearchSlots < 1 || SearchSlots > 8 || SearchCapacity < 64 || SearchCapacity > 1048576 ||
                ExpansionsPerTick < 1 || RequestsPerTick < 1 || AcceptanceTicks < 1 ||
                !math.isfinite(HeuristicWeight) || HeuristicWeight < 1 || HeuristicWeight > 2)
                throw new ArgumentException("Volume: resolution 4..256, finite dimensions and positive bounded search budgets required.");
        }
    }

    public enum VolumePathStatus { Pending, Ready, Arrived, NoPath, InvalidEndpoint, CapacityExceeded }
    public struct VolumePathInfo
    {
        public int AgentId, RequestId, MapVersion, Count, Cursor;
        public long RequestedTick, ReadyTick;
        public float3 Goal;
        public VolumePathStatus Status;
    }
}
