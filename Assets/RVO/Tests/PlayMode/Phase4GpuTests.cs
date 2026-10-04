using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Rvo.Rendering;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Rvo.Tests
{
    public sealed class Phase4GpuTests
    {
        [UnityTest]
        public IEnumerator LiveBridgeObservesEveryCommitWithoutChangingSimulation()
        {
            #if UNITY_EDITOR
            var profile = UnityEditor.AssetDatabase.LoadAssetAtPath<SimulationProfile>("Assets/RVO/Demo/08_VolumeNavigation_ORCA.asset");
            #else
            Assert.Ignore("Requires the editor demo profile.");
            SimulationProfile profile = null;
            #endif
            var baselineObject = new GameObject("Simulation without rendering"); baselineObject.SetActive(false);
            var visualObject = new GameObject("Simulation with snapshot bridge"); visualObject.SetActive(false);
            var baseline = baselineObject.AddComponent<VolumeSimulationBootstrap>(); baseline.Profile = profile; baseline.Paused = true;
            var source = visualObject.AddComponent<VolumeSimulationBootstrap>(); source.Profile = profile; source.Paused = true;
            var renderer = visualObject.AddComponent<GpuFishRenderer>(); renderer.AutoRender = false;
            var bridge = visualObject.AddComponent<FishLiveBridge>(); bridge.Source = source; bridge.Renderer = renderer;
            try
            {
                baselineObject.SetActive(true); visualObject.SetActive(true); yield return null;
                Assert.That(source.LastError, Is.Null);
                for (int tick = 1; tick <= 60; tick++)
                {
                    baseline.StepOnce(false); source.StepOnce(false);
                    Assert.That(renderer.Poses.Tick, Is.EqualTo(tick), "Every catch-up commit must be consumed.");
                    for (int i = 0; i < source.World.Snapshot.Count; i++)
                    {
                        Assert.That(source.World.Snapshot.Positions[i], Is.EqualTo(baseline.World.Snapshot.Positions[i]));
                        Assert.That(source.World.Snapshot.Velocities[i], Is.EqualTo(baseline.World.Snapshot.Velocities[i]));
                    }
                    if (tick % 4 == 0) yield return null;
                }
                uint generation = source.Generation;
                source.ResetSimulation();
                Assert.That(renderer.Poses.Generation, Is.GreaterThan(generation));
                Assert.That(renderer.Poses.Tick, Is.Zero);
                source.enabled = false; Assert.That(renderer.Poses.Count, Is.Zero);
            }
            finally { Object.Destroy(baselineObject); Object.Destroy(visualObject); }
            yield return null; LogAssert.NoUnexpectedReceived();
        }

        private static GpuFishRenderer Create(out GameObject root, out Camera camera)
        {
            if (!GpuFishRenderer.Supported) Assert.Ignore("Requires a real graphics device with compute shaders.");
            root = new GameObject("GPU fish test");
            camera = new GameObject("GPU test camera").AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            camera.transform.position = new Vector3(0, 0, -12); camera.farClipPlane = 1000;
            var renderer = root.AddComponent<GpuFishRenderer>(); renderer.ViewCamera = camera;
            renderer.FishShader = Shader.Find("RVO/Procedural Fish Indirect");
            #if UNITY_EDITOR
            renderer.CullingShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/RVO/Rendering/Shaders/FishCulling.compute");
            #endif
            Assert.That(renderer.CullingShader, Is.Not.Null); return renderer;
        }
        private static void Fill(AgentStorage storage, int count)
        {
            var a = storage.Initialization;
            for (int i = 0; i < count; i++)
            {
                a.Ids[i] = i * 7 + 11; a.Positions[i] = new float3((i % 5 - 2) * 1.8f, (i / 5 % 5 - 2) * 1.2f, i / 25 * 0.02f);
                a.Velocities[i] = new float3(2, 0, 0); a.Goals[i] = new float3(0, 100, 0);
                a.Parameters[i] = new AgentParameters { Radius = 0.7f, MaxSpeed = 2, ArrivalDistance = 0.1f };
            }
        }
        // GPU 回读只用于验收，不进入正常渲染热路径。
        private static uint Count(GpuFishRenderer renderer, int lod)
        {
            var values = new GraphicsBuffer.IndirectDrawIndexedArgs[1]; renderer.Arguments(lod).GetData(values); return values[0].instanceCount;
        }

        [UnityTest]
        public IEnumerator IndirectBucketsDrawPixelsAndClearAfterCameraTurn()
        {
            var renderer = Create(out var root, out var camera);
            var target = new RenderTexture(640, 400, 24); camera.targetTexture = target;
            using var storage = new AgentStorage(25); Fill(storage, 25);
            try
            {
                renderer.Capture(new AgentSnapshot(storage.Read, 0, 1, 1f / 30)); renderer.ForceLod = 0;
                yield return null; yield return null; yield return null;
                Assert.That(renderer.LastError, Is.Null);
                Assert.That(Count(renderer, 0), Is.EqualTo(25));
                // batchmode 的测试循环不保证相机自动出帧；显式走当前 SRP 的完整渲染请求。
                renderer.Render();
                RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
                var texture = new Texture2D(640, 400, TextureFormat.RGB24, false);
                var old = RenderTexture.active;
                try
                {
                    RenderTexture.active = target; texture.ReadPixels(new Rect(0, 0, 640, 400), 0, 0); texture.Apply();
                    int lit = 0; foreach (var pixel in texture.GetPixels32()) if (pixel.r + pixel.g + pixel.b > 80) lit++;
                    Directory.CreateDirectory("Documentation/RVO/Verification/Phase4Surface/Regression");
                    File.WriteAllBytes("Documentation/RVO/Verification/Phase4Surface/Regression/fish.png", texture.EncodeToPNG());
                    var gpu = new FishGpuData[25]; renderer.Prepared.GetData(gpu);
                    File.WriteAllText("Documentation/RVO/Verification/Phase4Surface/Regression/gpu-debug.txt", $"Lit={lit}, bounds={renderer.Poses.WorldBounds}, pos={gpu[0].PositionRadius}, q={gpu[0].Rotation}, anim={gpu[0].Animation}, supported={renderer.FishShader.isSupported}, camera={camera.pixelRect}");
                    Assert.That(lit, Is.GreaterThan(100), "Valid args alone do not prove an actual shader draw.");
                }
                finally { RenderTexture.active = old; Object.Destroy(texture); }
                for (int lod = 1; lod < 4; lod++)
                {
                    renderer.ForceLod = lod; yield return null; yield return null;
                    for (int bucket = 0; bucket < 4; bucket++) Assert.That(Count(renderer, bucket), Is.EqualTo(bucket == lod ? 25 : 0));
                }
                camera.transform.rotation = Quaternion.Euler(0, 180, 0); yield return null; yield return null;
                for (int lod = 0; lod < 4; lod++) Assert.That(Count(renderer, lod), Is.Zero);
                camera.transform.rotation = Quaternion.identity;
                using var distances = new AgentStorage(4); Fill(distances, 4);
                var a = distances.Initialization;
                float[] depth = { -12.05f, 0, 40, 300 };
                for (int i = 0; i < 4; i++) a.Positions[i] = new float3(0, 0, depth[i]);
                renderer.ForceLod = -1;
                renderer.Capture(new AgentSnapshot(distances.Read, 0, 2, 1f / 30));
                yield return null; yield return null;
                for (int lod = 0; lod < 4; lod++) Assert.That(Count(renderer, lod), Is.EqualTo(1), "Automatic LOD / near-plane intersection.");
            }
            finally { camera.targetTexture = null; target.Release(); Object.Destroy(target); Object.Destroy(root); Object.Destroy(camera.gameObject); }
            yield return null; LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator ThirtyThousandIndicesAreUniqueAndConservativeAgainstCpuFrustum()
        {
            var renderer = Create(out var root, out var camera);
            using var storage = new AgentStorage(30000); Fill(storage, 30000);
            try
            {
                renderer.FrustumCulling = false; renderer.ForceLod = 3;
                renderer.Capture(new AgentSnapshot(storage.Read, 0, 1, 1f / 30));
                yield return null; yield return null;
                Assert.That(Count(renderer, 3), Is.EqualTo(30000));
                var indices = new uint[30000]; renderer.VisibleIndices(3).GetData(indices);
                var unique = new HashSet<uint>(indices); Assert.That(unique.Count, Is.EqualTo(30000));
                Assert.That(unique.Contains(29999), Is.True);
                renderer.FrustumCulling = true; renderer.ForceLod = -1;
                camera.transform.rotation = Quaternion.Euler(0, 35, 0);
                yield return null; yield return null;
                var seen = new HashSet<uint>();
                for (int lod = 0; lod < 4; lod++)
                {
                    int count = (int)Count(renderer, lod); var bucket = new uint[count];
                    if (count > 0) renderer.VisibleIndices(lod).GetData(bucket, 0, 0, count);
                    foreach (uint index in bucket) Assert.That(seen.Add(index), Is.True, "An instance must belong to one bucket.");
                }
                var planes = GeometryUtility.CalculateFrustumPlanes(camera);
                for (int i = 0; i < 30000; i++)
                {
                    var sphere = renderer.Poses.Current[i].PositionRadius; bool visible = true;
                    foreach (var plane in planes) if (plane.GetDistanceToPoint(sphere.xyz) < -sphere.w + 0.001f) visible = false;
                    if (visible) Assert.That(seen.Contains((uint)i), Is.True, $"CPU sphere {i} at {sphere} must not disappear; GPU visible={seen.Count}.");
                }
                for (uint reset = 2; reset < 102; reset++)
                {
                    renderer.Clear(); renderer.Capture(new AgentSnapshot(storage.Read, 0, reset, 1f / 30));
                    yield return null;
                }
                Assert.That(renderer.LastError, Is.Null);
            }
            finally { Object.Destroy(root); Object.Destroy(camera.gameObject); }
            yield return null; LogAssert.NoUnexpectedReceived();
        }
    }
}
