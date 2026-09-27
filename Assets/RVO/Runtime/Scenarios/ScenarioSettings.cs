using System;
using Unity.Mathematics;

namespace Rvo
{
    public enum ScenarioKind { SingleAgent, HeadOnPair, Crossing, CircleSwap, OpposingGroups, RandomCrowd }

    [Serializable]
    public struct ScenarioSettings
    {
        public ScenarioKind Kind;
        public uint Seed;
        public float Extent;
        public float Radius;
        public float MaxSpeed;
        public float ArrivalDistance;

        public static ScenarioSettings Default => new ScenarioSettings
        {
            Kind = ScenarioKind.CircleSwap, Seed = 1, Extent = 30f,
            Radius = 0.35f, MaxSpeed = 2f, ArrivalDistance = 0.1f
        };

        public void Validate(int count)
        {
            if (!Enum.IsDefined(typeof(ScenarioKind), Kind)) throw new ArgumentException("Unknown scenario.");
            if (Seed == 0) throw new ArgumentException("Seed must be nonzero.");
            if (!math.isfinite(Extent) || !math.isfinite(Radius) || !math.isfinite(MaxSpeed) ||
                !math.isfinite(ArrivalDistance) || Extent <= 0 || Radius <= 0 ||
                MaxSpeed <= 0 || ArrivalDistance < 0)
                throw new ArgumentException("Invalid scenario dimensions or agent parameters.");
            if (Kind == ScenarioKind.SingleAgent && count != 1)
                throw new ArgumentException("SingleAgent requires AgentCount = 1.");
            if (Kind == ScenarioKind.HeadOnPair && count != 2)
                throw new ArgumentException("HeadOnPair requires AgentCount = 2.");
        }
    }
}
