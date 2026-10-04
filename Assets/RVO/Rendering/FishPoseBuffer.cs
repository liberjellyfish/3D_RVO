using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace Rvo.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    public struct FishGpuData
    {
        public float4 PositionRadius;
        public float4 Rotation;
        public float4 Animation; // phase、振幅、频率、统一缩放；与 FishData.hlsl 保持一致。
        public uint4 Identity;   // stable ID、generation、flags、外观 seed。
        public const int Stride = 64;
        public const uint Arrived = 1, Invalid = 2;
    }

    public static class FishFrame
    {
        public static quaternion Transport(quaternion previous, float3 velocity)
        {
            if (math.lengthsq(velocity) < 1e-6f) return previous;
            float3 forward = math.normalize(velocity), old = math.mul(previous, new float3(0, 0, 1));
            float dot = math.clamp(math.dot(old, forward), -1, 1);
            quaternion delta;
            // 反向时最短弧的轴不唯一，沿历史 up 转半圈，避免随机 roll。
            if (dot < -0.9999f) delta = quaternion.AxisAngle(math.mul(previous, new float3(0, 1, 0)), math.PI);
            else delta = math.normalize(new quaternion(new float4(math.cross(old, forward), 1 + dot)));
            quaternion rotated = math.normalize(math.mul(delta, previous));
            float3 up = math.mul(rotated, new float3(0, 1, 0));
            up = math.normalizesafe(up - forward * math.dot(up, forward), math.mul(rotated, new float3(1, 0, 0)));
            return quaternion.LookRotationSafe(forward, up);
        }
    }

    /// <summary>拥有快照副本；只在提交时打包，绘制帧不再访问仿真 NativeArray。</summary>
    public sealed class FishPoseBuffer : IDisposable
    {
        public NativeArray<FishGpuData> Previous { get; private set; }
        public NativeArray<FishGpuData> Current { get; private set; }
        private NativeArray<FishGpuData> scratch;
        private NativeParallelHashMap<int, int> history;
        private NativeArray<float3> bounds;
        public int Count => Current.IsCreated ? Current.Length : 0;
        public long Tick { get; private set; } = -1;
        public uint Generation { get; private set; }
        public int Revision { get; private set; }
        public int IdentityRevision { get; private set; }
        public Bounds WorldBounds { get; private set; }
        public double PackMilliseconds { get; private set; }
        public double TotalPackMilliseconds { get; private set; }
        private static readonly ProfilerMarker PackMarker = new ProfilerMarker("RVO.Fish.SnapshotPack");

        public void Capture(in AgentSnapshot snapshot)
        {
            if (snapshot.Agents.Count == 0) { Dispose(); return; }
            if (!(snapshot.DeltaTime > 0) || !math.isfinite(snapshot.DeltaTime) || snapshot.Tick < 0)
                throw new ArgumentException("Invalid snapshot clock.");
            bool reset = Count != snapshot.Agents.Count || Generation != snapshot.Generation;
            if (!reset && snapshot.Tick <= Tick) throw new ArgumentException("Snapshot tick must increase within a generation.");
            if (Count != snapshot.Agents.Count) Allocate(snapshot.Agents.Count);
            bool identityChanged = reset;
            if (!identityChanged)
                for (int i = 0; i < Count; i++)
                    if (Current[i].Identity.x != unchecked((uint)snapshot.Agents.Ids[i])) { identityChanged = true; break; }
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            using (PackMarker.Auto())
            {
                new PackJob
                {
                    Agents = snapshot.Agents, Old = Current, Next = scratch, Previous = Previous,
                    History = history, Bounds = bounds, Reset = reset || Tick < 0,
                    Generation = snapshot.Generation, Dt = snapshot.DeltaTime
                }.Schedule().Complete();
                var old = Current; Current = scratch; scratch = old;
            }
            Tick = snapshot.Tick; Generation = snapshot.Generation; Revision++;
            if (identityChanged) IdentityRevision++;
            WorldBounds = new Bounds((bounds[0] + bounds[1]) * 0.5f, math.max(bounds[1] - bounds[0], 0.01f));
            PackMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            TotalPackMilliseconds += PackMilliseconds;
        }

        private void Allocate(int count)
        {
            Dispose();
            try
            {
                Current = new NativeArray<FishGpuData>(count, Allocator.Persistent);
                Previous = new NativeArray<FishGpuData>(count, Allocator.Persistent);
                scratch = new NativeArray<FishGpuData>(count, Allocator.Persistent);
                history = new NativeParallelHashMap<int, int>(count, Allocator.Persistent);
                bounds = new NativeArray<float3>(2, Allocator.Persistent);
            }
            catch { Dispose(); throw; }
        }

        [BurstCompile]
        private struct PackJob : IJob
        {
            public AgentReadView Agents;
            [ReadOnly] public NativeArray<FishGpuData> Old;
            public NativeArray<FishGpuData> Next, Previous;
            public NativeParallelHashMap<int, int> History;
            public NativeArray<float3> Bounds;
            public bool Reset;
            public uint Generation;
            public float Dt;
            public void Execute()
            {
                History.Clear();
                if (!Reset) for (int i = 0; i < Old.Length; i++) History.TryAdd((int)Old[i].Identity.x, i);
                float3 min = new float3(float.PositiveInfinity), max = new float3(float.NegativeInfinity);
                for (int i = 0; i < Next.Length; i++)
                {
                    int id = Agents.Ids[i];
                    uint seed = math.hash(new uint2(unchecked((uint)id), 7));
                    float radius = Agents.Parameters[i].Radius;
                    bool valid = math.all(math.isfinite(Agents.Positions[i])) && math.all(math.isfinite(Agents.Velocities[i])) && math.isfinite(radius) && radius > 0;
                    float3 position = valid ? Agents.Positions[i] : float3.zero;
                    float3 velocity = valid ? Agents.Velocities[i] : float3.zero;
                    float speed = math.length(velocity);
                    float scale = valid ? radius * (0.68f + (seed & 255) / 255f * 0.16f) : 0;
                    int oldIndex = 0;
                    bool found = !Reset && History.TryGetValue(id, out oldIndex);
                    FishGpuData previous = found ? Old[oldIndex] : default;
                    float phase = found ? math.fmod(previous.Animation.x, 2 * math.PI) : (seed & 65535) / 65535f * 2 * math.PI;
                    previous.Animation.x = phase;
                    float frequency = math.clamp(0.6f + speed * 0.45f, 0.6f, 6);
                    float amplitude = math.lerp(found ? previous.Animation.y : 0.08f, math.clamp(0.07f + speed * 0.018f, 0.07f, 0.24f), 1 - math.exp(-8 * Dt));
                    quaternion rotation = FishFrame.Transport(found ? new quaternion(previous.Rotation) : quaternion.identity, velocity);
                    if (found && math.dot(rotation.value, previous.Rotation) < 0) rotation.value = -rotation.value;
                    uint flags = valid ? 0u : FishGpuData.Invalid;
                    if (valid && math.distancesq(position, Agents.Goals[i]) <= math.square(Agents.Parameters[i].ArrivalDistance)) flags |= FishGpuData.Arrived;
                    var next = new FishGpuData
                    {
                        PositionRadius = new float4(position, 1.7f * scale), Rotation = rotation.value,
                        Animation = new float4(phase + (found ? frequency * 2 * math.PI * Dt : 0), amplitude, frequency, scale),
                        Identity = new uint4(unchecked((uint)id), Generation, flags, seed)
                    };
                    if (!found || (previous.Identity.z & FishGpuData.Invalid) != 0) previous = next;
                    Next[i] = next; Previous[i] = previous;
                    // 整批 bounds 同时包住插值两端与最大尾摆，不能只使用碰撞球半径。
                    min = math.min(min, math.min(next.PositionRadius.xyz - next.PositionRadius.w, previous.PositionRadius.xyz - previous.PositionRadius.w));
                    max = math.max(max, math.max(next.PositionRadius.xyz + next.PositionRadius.w, previous.PositionRadius.xyz + previous.PositionRadius.w));
                }
                Bounds[0] = min; Bounds[1] = max;
            }
        }

        public void Dispose()
        {
            if (Current.IsCreated) Current.Dispose(); Current = default;
            if (Previous.IsCreated) Previous.Dispose(); Previous = default;
            if (scratch.IsCreated) scratch.Dispose(); scratch = default;
            if (history.IsCreated) history.Dispose(); history = default;
            if (bounds.IsCreated) bounds.Dispose(); bounds = default;
            Tick = -1;
        }
    }
}
