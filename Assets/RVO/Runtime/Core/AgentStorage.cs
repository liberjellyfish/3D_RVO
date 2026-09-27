using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Rvo
{
    /// <summary>Single owner of persistent SoA storage. Never passed into a Burst job.</summary>
    internal sealed class AgentStorage : IDisposable
    {
        private NativeArray<int> ids;
        private NativeArray<float3> positions, velocities, goals, nextPositions, nextVelocities;
        private NativeArray<AgentParameters> parameters;
        private NativeArray<float3> preferred;
        private NativeArray<SolveStatus> status;

        public AgentReadView Read => new AgentReadView(ids, positions, velocities, goals, parameters);
        public AgentReadView Previous => new AgentReadView(ids, nextPositions, nextVelocities, goals, parameters);
        public NativeArray<SolveStatus>.ReadOnly Status => status.AsReadOnly();
        public NativeArray<float3> Preferred => preferred;
        public NativeArray<float3> NextPositions => nextPositions;
        public MotionOutput Output => new MotionOutput { Velocities = nextVelocities, Status = status };
        public AgentInitializationView Initialization => new AgentInitializationView
        {
            Ids = ids, Positions = positions, Velocities = velocities, Goals = goals, Parameters = parameters
        };

        public AgentStorage(int count)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            try
            {
                ids = Allocate<int>(count);
                positions = Allocate<float3>(count);
                velocities = Allocate<float3>(count);
                goals = Allocate<float3>(count);
                parameters = Allocate<AgentParameters>(count);
                nextPositions = Allocate<float3>(count);
                nextVelocities = Allocate<float3>(count);
                preferred = Allocate<float3>(count);
                status = Allocate<SolveStatus>(count);
            }
            catch { Dispose(); throw; }
        }

        public void Commit()
        {
            var previousPositions = positions;
            positions = nextPositions;
            nextPositions = previousPositions;
            var previousVelocities = velocities;
            velocities = nextVelocities;
            nextVelocities = previousVelocities;
        }

        public void Dispose()
        {
            Release(ref ids);
            Release(ref positions);
            Release(ref velocities);
            Release(ref goals);
            Release(ref parameters);
            Release(ref nextPositions);
            Release(ref nextVelocities);
            Release(ref preferred);
            Release(ref status);
        }

        private static NativeArray<T> Allocate<T>(int count) where T : struct =>
            new NativeArray<T>(count, Allocator.Persistent, NativeArrayOptions.ClearMemory);

        private static void Release<T>(ref NativeArray<T> array) where T : struct
        {
            if (array.IsCreated) array.Dispose();
            array = default;
        }
    }
}
