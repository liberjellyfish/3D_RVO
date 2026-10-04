using System;
using UnityEngine;

namespace Rvo.Rendering
{
    /// <summary>以世界距离和线性 RGB 透射率标定均匀介质，供配置与独立数值验收使用。</summary>
    public static class WaterOptics
    {
        public static Vector3 Calibrate(Vector3 transmittance, float distance)
        {
            if (!(distance > 0) || float.IsNaN(distance) || float.IsInfinity(distance)) throw new ArgumentOutOfRangeException(nameof(distance));
            for (int axis = 0; axis < 3; axis++)
                if (!(transmittance[axis] > 0 && transmittance[axis] <= 1)) throw new ArgumentOutOfRangeException(nameof(transmittance));
            return new Vector3(-Mathf.Log(transmittance.x), -Mathf.Log(transmittance.y), -Mathf.Log(transmittance.z)) / distance;
        }
        public static Vector3 Transmittance(Vector3 extinction, float distance) => new Vector3(
            Mathf.Exp(-Mathf.Max(0, extinction.x) * Mathf.Max(0, distance)),
            Mathf.Exp(-Mathf.Max(0, extinction.y) * Mathf.Max(0, distance)),
            Mathf.Exp(-Mathf.Max(0, extinction.z) * Mathf.Max(0, distance)));
    }
}
