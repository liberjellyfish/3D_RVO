using System;
using Unity.Mathematics;

namespace Rvo
{
    [Serializable]
    public struct NavigationSettings
    {
        public bool Enabled;
        public int Width, Height;
        public float CellSize;
        // 障碍数量是随机矩形的数量，不再是单个占据格数量。
        public int ObstacleCount, ObstacleMinSize, ObstacleMaxSize;
        public uint Seed;
        public float SafetyMargin, StaticTimeHorizon;
        public int PathExpansionsPerTick, PathRequestsPerTick, MaxStaticConstraints;
        // 0 兼容旧资产；有效默认值为 4 / 1.5。1 保留精确 A* 对照。
        public int PathSearchSlots;
        public float PathHeuristicWeight;
        public bool DisableTrafficRecovery;
        public float StallSeconds, RecoveryCooldownSeconds;
        public float EffectiveStallSeconds => StallSeconds == 0 ? 1.25f : StallSeconds;
        public float EffectiveRecoveryCooldown => RecoveryCooldownSeconds == 0 ? 4 : RecoveryCooldownSeconds;
        public int EffectiveSearchSlots => PathSearchSlots == 0 ? 4 : PathSearchSlots;
        public float EffectiveHeuristicWeight => PathHeuristicWeight == 0 ? 1.5f : PathHeuristicWeight;
        public static NavigationSettings Default => new NavigationSettings
        {
            Width = 512, Height = 512, CellSize = 1, ObstacleCount = 450,
            ObstacleMinSize = 4, ObstacleMaxSize = 16, Seed = 7,
            SafetyMargin = 0.08f, StaticTimeHorizon = 0.5f,
            PathExpansionsPerTick = 4096, PathRequestsPerTick = 16, MaxStaticConstraints = 48
        };
        public void Validate(float radius)
        {
            if (Width < 4 || Height < 4 || Width > 512 || Height > 512 ||
                !math.isfinite(CellSize) || CellSize <= 0 || !math.isfinite(math.max(Width, Height) * CellSize) || Seed == 0 ||
                ObstacleCount < 0 || ObstacleCount > Width * Height / 3 || ObstacleMinSize < 1 ||
                ObstacleMaxSize < ObstacleMinSize || ObstacleMaxSize >= math.min(Width, Height) ||
                !math.isfinite(SafetyMargin) || SafetyMargin < 0.001f ||
                !math.isfinite(StaticTimeHorizon) || StaticTimeHorizon <= 0 ||
                !math.isfinite(radius) || radius <= 0 || radius + SafetyMargin >= math.min(Width, Height) * CellSize * 0.5f ||
                PathExpansionsPerTick < 1 || PathRequestsPerTick < 1 || MaxStaticConstraints < 4 || MaxStaticConstraints > 256 ||
                !math.isfinite(StallSeconds) || StallSeconds < 0 || !math.isfinite(RecoveryCooldownSeconds) || RecoveryCooldownSeconds < 0 ||
                PathSearchSlots < 0 || PathSearchSlots > 16 || !math.isfinite(PathHeuristicWeight) ||
                (PathHeuristicWeight != 0 && (PathHeuristicWeight < 1 || PathHeuristicWeight > 2)))
                throw new ArgumentException("导航参数无效：地图 4..512，半径须能放入地图，障碍尺寸及搜索预算须为正。");
        }
        // 只有影响地图内容/净空的参数进入签名；换档、速度及寻路预算不需要重新烘焙。
        public uint BakeSignature(float radius)
        {
            uint hash = 2166136261;
            Mix(ref hash, (uint)Width); Mix(ref hash, (uint)Height); Mix(ref hash, math.asuint(CellSize));
            Mix(ref hash, (uint)ObstacleCount); Mix(ref hash, (uint)ObstacleMinSize); Mix(ref hash, (uint)ObstacleMaxSize);
            Mix(ref hash, Seed); Mix(ref hash, math.asuint(radius)); Mix(ref hash, math.asuint(SafetyMargin));
            return hash;
        }
        private static void Mix(ref uint hash, uint value) { unchecked { hash = (hash ^ value) * 16777619; } }
    }
}
