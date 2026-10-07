using System;
using Unity.Mathematics;

namespace Rvo
{
    public enum ScenarioKind { SingleAgent, HeadOnPair, Crossing, CircleSwap, OpposingGroups, RandomCrowd }
    public enum VolumeSpawnPattern { OpposingBands, DistributedRooms }

    [Serializable]
    public struct ScenarioSettings
    {
        public ScenarioKind Kind;
        public uint Seed;
        public float Extent;
        public float Radius;
        public float MaxSpeed;
        public float ArrivalDistance;
        [UnityEngine.Tooltip("Full3D only: radius varies by ± this fraction. Re-bake the volume after changing it.")]
        [UnityEngine.Range(0, 0.5f)] public float VolumeSizeVariation;
        [UnityEngine.Tooltip("Full3D only: navigation max speed varies by ± this fraction.")]
        [UnityEngine.Range(0, 0.5f)] public float VolumeSpeedVariation;
        public VolumeSpawnPattern VolumeSpawns;
        public float VolumeClearanceRadius => Radius * (1 + VolumeSizeVariation);

        public static ScenarioSettings Default => new ScenarioSettings
        {
            Kind = ScenarioKind.CircleSwap, Seed = 1, Extent = 30f,
            Radius = 0.35f, MaxSpeed = 2f, ArrivalDistance = 0.1f
        };

        public void Validate(int count)
        {
            if (!math.isfinite(VolumeSizeVariation) || !math.isfinite(VolumeSpeedVariation) ||
                VolumeSizeVariation < 0 || VolumeSizeVariation > 0.5f || VolumeSpeedVariation < 0 || VolumeSpeedVariation > 0.5f)
                throw new ArgumentException("Volume size/speed variation must be finite and in 0..0.5.");
            if (!Enum.IsDefined(typeof(ScenarioKind), Kind)) throw new ArgumentException("Unknown scenario.");
            if (!Enum.IsDefined(typeof(VolumeSpawnPattern), VolumeSpawns)) throw new ArgumentException("Unknown volume spawn pattern.");
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
