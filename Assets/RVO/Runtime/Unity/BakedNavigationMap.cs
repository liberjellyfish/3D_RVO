using System;
using UnityEngine;

namespace Rvo
{
    public sealed class BakedNavigationMap : ScriptableObject
    {
        [SerializeField] private TextAsset data;
        [SerializeField] private int width, height, occupiedCells, rectangles;
        [SerializeField] private float clearanceRadius;
        [SerializeField] private string bakeSummary;
        private NavigationGrid cached;
        private uint cachedSignature;
        public TextAsset Data => data;
        public string Summary => bakeSummary;
        public void SetData(TextAsset source, NavigationGrid map, double milliseconds)
        {
            data = source; width = map.Width; height = map.Height; occupiedCells = map.ObstacleCount;
            rectangles = map.RectangleCount; clearanceRadius = map.ClearanceRadius;
            bakeSummary = $"{width} x {height} | {occupiedCells} cells | {rectangles} rectangles | {milliseconds:F1} ms bake";
            cached = null;
        }
        public NavigationGrid Load(in NavigationSettings settings, float radius)
        {
            settings.Validate(radius);
            if (data == null) throw new InvalidOperationException("缺少预烘焙地图；选择 Profile 并在 Inspector 点击 Bake。");
            uint signature = settings.BakeSignature(radius);
            if (cached == null || signature != cachedSignature)
            { cached = NavigationBake.Decode(data.bytes, signature); cachedSignature = signature; }
            return cached;
        }
    }
}
