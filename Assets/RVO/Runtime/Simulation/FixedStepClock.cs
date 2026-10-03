using System;

namespace Rvo
{
    /// <summary>与渲染帧解耦的时钟。追赶超额会显式统计，不改变固定 dt。</summary>
    public sealed class FixedStepClock
    {
        private double accumulator;
        public long DroppedTicks { get; private set; }
        public float Fraction(double step) => (float)Math.Min(1, Math.Max(0, accumulator / step));
        public int Advance(double elapsed, double step, int maxSteps)
        {
            accumulator += elapsed;
            long due = (long)Math.Floor((accumulator + step * 1e-7) / step);
            int steps = (int)Math.Min(due, maxSteps);
            accumulator = Math.Max(0, accumulator - due * step);
            DroppedTicks += due - steps;
            return steps;
        }
        public void Reset() { accumulator = 0; DroppedTicks = 0; }
    }
}
