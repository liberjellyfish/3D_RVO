using System;

namespace Rvo
{
    [Serializable]
    public struct BenchmarkSettings
    {
        public int WarmupTicks;
        public int MeasuredTicks;
        public int Repetitions;
        public bool RenderAgents;
        public bool CollectQualityMetrics;

        public static BenchmarkSettings Default => new BenchmarkSettings
        {
            WarmupTicks = 300, MeasuredTicks = 1800, Repetitions = 3,
            RenderAgents = false, CollectQualityMetrics = true
        };

        public void Validate()
        {
            if (WarmupTicks < 0 || MeasuredTicks <= 0 || Repetitions <= 0)
                throw new ArgumentException("Invalid benchmark duration or repetition count.");
        }
    }

    // 仿真计时由 World 填写；昂贵质量检查在计时区间外。
    [Serializable]
    public struct SimulationMetrics
    {
        public long Tick;
        public double PreferredMilliseconds, NeighborMilliseconds, AvoidanceMilliseconds;
        public double IntegrationMilliseconds, TotalSimulationMilliseconds;
        public long ManagedAllocatedBytes;
        public int OverlappingPairs, ArrivedAgents, FallbackAgents, InfeasibleAgents;
        public int NeighborTruncatedAgents;
        public float MeanNeighborCount, MinimumSeparation;
    }

    public interface IMetricsCollector
    {
        SimulationMetrics Collect(in StepContext context, in AgentReadView agents,
            in NeighborReadView neighbors, in MotionOutput output);
    }

    public interface IBenchmarkReportWriter : IDisposable
    {
        // Called off the measured hot path; metadata includes settings, seed, device and build info.
        void Begin(string metadataJson);
        void Write(in SimulationMetrics sample);
        void Complete();
    }
}
