using System;
using Unity.Mathematics;

namespace Rvo
{
    [Serializable]
    public struct VoSamplingSettings
    {
        public int AngleSamples;
        public int SpeedSamples;
        public float SafetyMargin;
        public static VoSamplingSettings Default => new VoSamplingSettings
        {
            AngleSamples = 72, SpeedSamples = 5, SafetyMargin = 0.02f
        };

        public void Validate()
        {
            if (AngleSamples < 4 || SpeedSamples < 1 || !math.isfinite(SafetyMargin) || SafetyMargin < 0)
                throw new ArgumentException("VO 需要至少 4 个角度、1 个速度环和非负安全余量。");
        }
    }
}
