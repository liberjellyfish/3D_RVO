using System;
using Unity.Mathematics;

namespace Rvo
{
    public sealed class ScenarioInitializer : StatelessModule, IScenarioInitializer
    {
        public override string Name => "Seeded planar scenarios";

        public void Initialize(in SimulationSettings settings, in ScenarioSettings scenario,
            in AgentInitializationView agents)
        {
            var ids = agents.Ids;
            var positions = agents.Positions;
            var velocities = agents.Velocities;
            var goals = agents.Goals;
            var parameters = agents.Parameters;
            int count = settings.AgentCount;
            float extent = scenario.Extent;
            var random = new Unity.Mathematics.Random(scenario.Seed);
            for (int i = 0; i < count; i++)
            {
                float2 position;
                switch (scenario.Kind)
                {
                    case ScenarioKind.SingleAgent: position = new float2(-extent, 0); break;
                    case ScenarioKind.HeadOnPair: position = new float2(i == 0 ? -extent : extent, 0); break;
                    case ScenarioKind.CircleSwap:
                        float angle = 2f * math.PI * i / count;
                        position = new float2(math.cos(angle), math.sin(angle)) * extent;
                        break;
                    case ScenarioKind.OpposingGroups:
                    case ScenarioKind.Crossing:
                        int groups = scenario.Kind == ScenarioKind.Crossing ? 4 : 2;
                        int group = i % groups;
                        int groupSize = (count + groups - 1) / groups;
                        position = Grid(i / groups, groupSize, extent * 0.25f) + new float2(-extent * 0.7f, 0);
                        float rotation = group * 2f * math.PI / groups;
                        position = new float2(position.x * math.cos(rotation) - position.y * math.sin(rotation),
                            position.x * math.sin(rotation) + position.y * math.cos(rotation));
                        break;
                    default:
                        // 分层随机：每个格子一个点，保留足够间隙，避免高密度下无限拒绝采样。
                        int columns = (int)math.ceil(math.sqrt(count));
                        float spacing = 2f * extent / columns;
                        float jitter = math.max(0, (spacing - 2f * scenario.Radius) * 0.3f);
                        position = Grid(i, count, extent) + random.NextFloat2(-jitter, jitter);
                        break;
                }
                ids[i] = i;
                positions[i] = new float3(position.x, settings.PlaneHeight, position.y);
                goals[i] = new float3(-position.x, settings.PlaneHeight, -position.y);
                velocities[i] = float3.zero;
                parameters[i] = new AgentParameters
                {
                    Radius = scenario.Radius, MaxSpeed = scenario.MaxSpeed, ArrivalDistance = scenario.ArrivalDistance
                };
            }
            if (scenario.Kind == ScenarioKind.RandomCrowd)
            {
                // 目标取初始可站立点的置换，避免独立随机目标互相重叠。
                for (int i = 0; i < count; i++) goals[i] = positions[i];
                for (int i = count - 1; i > 0; i--)
                {
                    int j = random.NextInt(i + 1);
                    float3 goal = goals[i]; goals[i] = goals[j]; goals[j] = goal;
                }
            }
            // 初始化阶段一次性验证；重叠恢复测试用自定义初态，不混入普通场景。
            for (int i = 0; i < count; i++)
                for (int j = 0; j < i; j++)
                    if (math.distance(positions[i], positions[j]) < 2f * scenario.Radius)
                        throw new ArgumentException("场景初始位置重叠：请增大 Extent 或减小 Radius / AgentCount。");
        }

        private static float2 Grid(int index, int count, float halfExtent)
        {
            int columns = (int)math.ceil(math.sqrt(count));
            int rows = (count + columns - 1) / columns;
            float spacing = 2f * halfExtent / columns;
            return new float2(index % columns - (columns - 1) * 0.5f,
                index / columns - (rows - 1) * 0.5f) * spacing;
        }
    }
}
