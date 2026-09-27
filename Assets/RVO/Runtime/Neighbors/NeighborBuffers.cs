using System;
using Unity.Collections;

namespace Rvo
{
    /// <summary>Flat N*K layout: index = agentIndex * MaxNeighbors + slot. Indices refer to storage, not IDs.</summary>
    public struct NeighborWriteView
    {
        public int MaxNeighbors;
        public NativeArray<int> Counts;
        [NativeDisableParallelForRestriction]
        public NativeArray<int> Indices;
        [NativeDisableParallelForRestriction]
        public NativeArray<float> DistanceSquared;
        // Number of in-radius candidates omitted by the K limit, per agent.
        public NativeArray<int> DroppedCounts;
        public NativeArray<int> CandidateCounts, BucketOccupancy;
    }

    public readonly struct NeighborReadView
    {
        public readonly int MaxNeighbors;
        public readonly NativeArray<int>.ReadOnly Counts, Indices, DroppedCounts, CandidateCounts, BucketOccupancy;
        public readonly NativeArray<float>.ReadOnly DistanceSquared;

        public NeighborReadView(in NeighborWriteView view)
        {
            MaxNeighbors = view.MaxNeighbors;
            Counts = view.Counts.AsReadOnly();
            Indices = view.Indices.AsReadOnly();
            DistanceSquared = view.DistanceSquared.AsReadOnly();
            DroppedCounts = view.DroppedCounts.AsReadOnly();
            CandidateCounts = view.CandidateCounts.AsReadOnly();
            BucketOccupancy = view.BucketOccupancy.AsReadOnly();
        }
    }

    internal sealed class NeighborBuffers : IDisposable
    {
        private NeighborWriteView view;
        public NeighborWriteView Write => view;
        public NeighborReadView Read => new NeighborReadView(view);

        public NeighborBuffers(int count, int maxNeighbors)
        {
            if (count <= 0 || maxNeighbors <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            int length = checked(count * maxNeighbors);
            try
            {
                view.MaxNeighbors = maxNeighbors;
                view.Counts = new NativeArray<int>(count, Allocator.Persistent);
                view.Indices = new NativeArray<int>(length, Allocator.Persistent);
                view.DistanceSquared = new NativeArray<float>(length, Allocator.Persistent);
                view.DroppedCounts = new NativeArray<int>(count, Allocator.Persistent);
                view.CandidateCounts = new NativeArray<int>(count, Allocator.Persistent);
                view.BucketOccupancy = new NativeArray<int>(count, Allocator.Persistent);
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (view.Counts.IsCreated) view.Counts.Dispose();
            if (view.Indices.IsCreated) view.Indices.Dispose();
            if (view.DistanceSquared.IsCreated) view.DistanceSquared.Dispose();
            if (view.DroppedCounts.IsCreated) view.DroppedCounts.Dispose();
            if (view.CandidateCounts.IsCreated) view.CandidateCounts.Dispose();
            if (view.BucketOccupancy.IsCreated) view.BucketOccupancy.Dispose();
            view = default;
        }
    }
}


