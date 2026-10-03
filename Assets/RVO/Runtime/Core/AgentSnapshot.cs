namespace Rvo
{
    /// <summary>提交边界的只读借用；消费者必须在回调返回前复制，不得跨 Tick 保存 Agents。</summary>
    public readonly struct AgentSnapshot
    {
        public readonly AgentReadView Agents;
        public readonly long Tick;
        public readonly uint Generation;
        public readonly float DeltaTime;
        public double SimulationTime => Tick * (double)DeltaTime;

        public AgentSnapshot(in AgentReadView agents, long tick, uint generation, float deltaTime)
        { Agents = agents; Tick = tick; Generation = generation; DeltaTime = deltaTime; }
    }
}
