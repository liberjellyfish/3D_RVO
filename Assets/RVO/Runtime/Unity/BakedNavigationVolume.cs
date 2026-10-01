using System;
using UnityEngine;

namespace Rvo
{
    public sealed class BakedNavigationVolume : ScriptableObject
    {
        [SerializeField] private TextAsset data;
        [SerializeField] private string summary;
        private NavigationVolume cached;
        public string Summary => summary;
        public void SetData(TextAsset bytes, NavigationVolume map)
        {
            data = bytes; summary = $"{map.Resolution}³ | {map.FreeCells} free | {map.ObstacleCount} boxes | {map.StorageBytes / 1048576.0:F2} MiB";
            cached = null;
        }
        public NavigationVolume Load(in VolumeSettings settings, float radius)
        {
            settings.Validate(radius);
            if (data == null) throw new InvalidOperationException("Missing baked XYZ volume. Use Tools/RVO/Create Phase 3 Volume Demo.");
            if (cached == null || cached.Resolution != settings.Resolution || cached.CellSize != settings.CellSize ||
                Unity.Mathematics.math.abs(cached.ClearanceRadius - radius - settings.SafetyMargin) > 1e-6f) cached = VolumeBake.Decode(data.bytes, settings, radius);
            return cached;
        }
    }
}
