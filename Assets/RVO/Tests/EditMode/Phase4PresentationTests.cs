using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Rvo.Rendering;
using Unity.Mathematics;
using UnityEngine;

namespace Rvo.Tests
{
    public sealed class Phase4PresentationTests
    {
        [Test]
        public void DetailedWaveBoundsDerivativesAndTextureLayoutsAreConsistent()
        {
            var mesh = ProceduralFishMesh.CreateDetailed();
            try
            {
                Assert.That(mesh.vertexCount,Is.EqualTo(2056));
                foreach(var vertex in mesh.vertices)
                    for(int frame=0;frame<64;frame++)
                    {
                        float phase=frame*Mathf.PI/32;
                        var wave=FishAnimationBaker.Wave(vertex.z,phase);
                        var deformed=vertex+Vector3.right*(wave.x*0.24f);
                        Assert.That(deformed.magnitude,Is.LessThan(1.7f));
                        if(vertex.z > -1.299f && vertex.z < 0.999f)
                        {
                            float numerical=(FishAnimationBaker.Wave(vertex.z+0.0005f,phase).x-FishAnimationBaker.Wave(vertex.z-0.0005f,phase).x)/0.001f;
                            Assert.That(wave.y,Is.EqualTo(numerical).Within(0.003f));
                        }
                    }
                foreach(var mode in new[] { FishAnimationMode.VertexTexture,FishAnimationMode.BoneTexture })
                {
                    var texture=FishAnimationBaker.Bake(mesh,mode);
                    try
                    {
                        Assert.That(texture.width,Is.EqualTo(mode==FishAnimationMode.VertexTexture ? 2056 : 72));
                        Assert.That(texture.height,Is.EqualTo(64)); Assert.That(texture.mipmapCount,Is.EqualTo(1));
                        Assert.That(texture.isReadable,Is.False);
                    }
                    finally { UnityEngine.Object.DestroyImmediate(texture); }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void BufferLayoutAndFourMeshesMatchContract()
        {
            Assert.That(Marshal.SizeOf<FishGpuData>(), Is.EqualTo(64));
            Assert.That((int)Marshal.OffsetOf<FishGpuData>(nameof(FishGpuData.Identity)), Is.EqualTo(48));
            int[] counts = { 200, 128, 72, 32 };
            for (int lod = 0; lod < 4; lod++)
            {
                var mesh = ProceduralFishMesh.Create(lod);
                try
                {
                    Assert.That(mesh.vertexCount, Is.EqualTo(counts[lod]));
                    foreach (var point in mesh.vertices) Assert.That(point.magnitude + 0.24f, Is.LessThan(1.7f));
                    foreach (var normal in mesh.normals) Assert.That(float.IsNaN(normal.x), Is.False);
                }
                finally { UnityEngine.Object.DestroyImmediate(mesh); }
            }
        }

        [Test]
        public void TransportCrossesBothPolesWithoutRollFlipsAndHandlesReverseStop()
        {
            quaternion frame = quaternion.identity;
            for (int i = 1; i <= 1440; i++)
            {
                float angle = i * math.PI / 360;
                float3 forward = new float3(0, math.sin(angle), math.cos(angle));
                quaternion next = FishFrame.Transport(frame, forward);
                Assert.That(math.all(math.isfinite(next.value)), Is.True);
                Assert.That(math.dot(math.mul(next, new float3(0, 0, 1)), forward), Is.GreaterThan(0.9999f));
                Assert.That(math.abs(math.dot(frame.value, next.value)), Is.GreaterThan(0.999f));
                Assert.That(math.determinant(new float3x3(next)), Is.EqualTo(1).Within(1e-4));
                frame = next;
            }
            Assert.That(FishFrame.Transport(frame, float3.zero).value, Is.EqualTo(frame.value));
            var reverse = FishFrame.Transport(frame, -math.mul(frame, new float3(0, 0, 1)));
            Assert.That(math.dot(math.mul(reverse, new float3(0, 1, 0)), math.mul(frame, new float3(0, 1, 0))), Is.GreaterThan(0.999f));
        }

        [Test]
        public void SnapshotCopiesAndReordersByIdentityAndResetsGeneration()
        {
            using var storage = new AgentStorage(2);
            using var poses = new FishPoseBuffer();
            Fill(storage, 2);
            poses.Capture(new AgentSnapshot(storage.Read, 0, 1, 1f / 30));
            var oldFirst = poses.Current[0];
            var a = storage.Initialization;
            a.Positions[0] = new float3(99, 0, 0);
            Assert.That(poses.Current[0].PositionRadius.x, Is.EqualTo(oldFirst.PositionRadius.x), "No retained source views.");
            a.Ids[0] = 20; a.Ids[1] = 10;
            a.Velocities[0] = new float3(0, 2, 0); a.Velocities[1] = new float3(0, 0, -2);
            poses.Capture(new AgentSnapshot(storage.Read, 1, 1, 1f / 30));
            Assert.That(poses.Previous[1].Identity.x, Is.EqualTo(10));
            Assert.That(poses.Previous[1].PositionRadius, Is.EqualTo(oldFirst.PositionRadius));
            Assert.That(poses.Current[1].Identity.w, Is.EqualTo(oldFirst.Identity.w));
            Assert.That(poses.Current[1].Animation.x, Is.GreaterThan(poses.Previous[1].Animation.x));
            poses.Capture(new AgentSnapshot(storage.Read, 0, 2, 1f / 30));
            Assert.That(poses.Previous[0].PositionRadius, Is.EqualTo(poses.Current[0].PositionRadius));
            Assert.That(poses.Current[0].Identity.y, Is.EqualTo(2));
        }

        [Test]
        public void StoppedIsNotArrivedAndInvalidInputCannotPoisonBounds()
        {
            using var storage = new AgentStorage(2); Fill(storage, 2);
            var a = storage.Initialization; a.Velocities[0] = float3.zero; a.Positions[1] = new float3(float.NaN);
            using var poses = new FishPoseBuffer(); poses.Capture(new AgentSnapshot(storage.Read, 0, 1, 1f / 30));
            Assert.That(poses.Current[0].Identity.z & FishGpuData.Arrived, Is.Zero);
            Assert.That(poses.Current[1].Identity.z & FishGpuData.Invalid, Is.Not.Zero);
            Assert.That(float.IsNaN(poses.WorldBounds.center.x), Is.False);
            a.Goals[0] = a.Positions[0]; poses.Capture(new AgentSnapshot(storage.Read, 1, 1, 1f / 30));
            Assert.That(poses.Current[0].Identity.z & FishGpuData.Arrived, Is.Not.Zero);
        }

        [Test]
        public void RenderingCadenceDoesNotChangeCommittedPoses()
        {
            using var storage = new AgentStorage(1); Fill(storage, 1);
            using var baseline = new FishPoseBuffer(); using var other = new FishPoseBuffer();
            var a = storage.Initialization;
            for (int tick = 0; tick < 120; tick++)
            {
                a.Velocities[0] = new float3(math.sin(tick * 0.07f), math.cos(tick * 0.07f), 0.2f);
                var snapshot = new AgentSnapshot(storage.Read, tick, 1, 1f / 30);
                baseline.Capture(snapshot); other.Capture(snapshot);
                // 渲染只读：在两次提交间任意读多次，不推动 phase / frame。
                for (int frame = 0; frame < tick % 5; frame++) Assert.That(other.Current[0].Rotation, Is.EqualTo(baseline.Current[0].Rotation));
            }
            Assert.That(other.Current[0].Animation, Is.EqualTo(baseline.Current[0].Animation));
        }

        [Test]
        public void WarmSnapshotPackingDoesNotAllocateManagedMemory()
        {
            using var storage = new AgentStorage(256); Fill(storage, 256);
            using var poses = new FishPoseBuffer();
            for (int tick = 0; tick < 8; tick++) poses.Capture(new AgentSnapshot(storage.Read, tick, 1, 1f / 30));
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int tick = 8; tick < 24; tick++) poses.Capture(new AgentSnapshot(storage.Read, tick, 1, 1f / 30));
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - start, Is.Zero);
        }

        [Test]
        public void CaptureAndDisposeRepeatedlyAndRejectStaleTicks()
        {
            using var storage = new AgentStorage(3); Fill(storage, 3);
            using var poses = new FishPoseBuffer();
            for (uint generation = 0; generation < 100; generation++)
            {
                poses.Capture(new AgentSnapshot(storage.Read, 0, generation, 1f / 30));
                Assert.Throws<ArgumentException>(() => poses.Capture(new AgentSnapshot(storage.Read, 0, generation, 1f / 30)));
                poses.Dispose(); poses.Dispose(); Assert.That(poses.Count, Is.Zero);
            }
        }
        private static void Fill(AgentStorage storage, int count)
        {
            var a = storage.Initialization;
            for (int i = 0; i < count; i++)
            {
                a.Ids[i] = (i + 1) * 10; a.Positions[i] = new float3(i * 2, 0, 0);
                a.Velocities[i] = new float3(0, 0, 2); a.Goals[i] = new float3(0, 0, 10);
                a.Parameters[i] = new AgentParameters { Radius = 0.8f, MaxSpeed = 2, ArrivalDistance = 0.1f };
            }
        }
    }
}
