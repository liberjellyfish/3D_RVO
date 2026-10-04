using System.Collections;
using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using Rvo.Rendering;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Rvo.Tests
{
    public sealed class OceanP0Tests
    {
        private const string Evidence = "Documentation/RVO/Verification/OceanP0";

        private static Color[] Read(RenderTexture target, string name)
        {
            var previous = RenderTexture.active;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); texture.Apply();
                if (name != null)
                {
                    Directory.CreateDirectory(Evidence);
                    File.WriteAllBytes(Path.Combine(Evidence, name + ".png"), texture.EncodeToPNG());
                    if (name.Contains("live"))
                    {
                        var preview = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                        try
                        {
                            var colors = texture.GetPixels();
                            for (int i = 0; i < colors.Length; i++) colors[i] = colors[i].gamma;
                            preview.SetPixels(colors); preview.Apply();
                            File.WriteAllBytes(Path.Combine(Evidence, name + "-srgb.png"), preview.EncodeToPNG());
                        }
                        finally { Object.Destroy(preview); }
                    }
                }
                return texture.GetPixels();
            }
            finally { RenderTexture.active = previous; Object.Destroy(texture); }
        }

        [UnityTest]
        public IEnumerator FrozenOceanLiveEvidence()
        {
            #if UNITY_EDITOR
            const string path = "Assets/RVO/Demo/Phase4_OceanLive.unity";
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            #else
            Assert.Ignore("Editor scene fixture."); yield break;
            #endif
            var scene = SceneManager.GetSceneByName("Phase4_OceanLive");
            Camera camera = null; GpuFishRenderer fish = null; VolumeSimulationBootstrap source = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.TryGetComponent<Camera>(out var c)) camera = c;
                if (root.TryGetComponent<GpuFishRenderer>(out var f)) fish = f;
                if (root.TryGetComponent<VolumeSimulationBootstrap>(out var s)) source = s;
            }
            Assert.That(source, Is.Not.Null); source.Paused = true;
            yield return null;
            source.ResetSimulation(); source.ShowHud = false;
            Assert.That(source.LastError, Is.Null);
            camera.enabled = false; camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
            camera.GetComponent<OceanDemoControls>().ShowHud = false;
            var ocean = camera.GetComponent<OceanEnvironment>(); ocean.FixedClock = true; ocean.EnvironmentSeconds = 4;
            fish.AutoRender = false; fish.Interpolation = 1;
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            target.Create(); camera.targetTexture = target;
            bool baseline = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-ocean-p0-baseline") >= 0;
            string prefix = baseline ? "baseline" : "p0-" + SystemInfo.graphicsDeviceType;
            // 对比证据一直使用旧全景镜头；新默认近景另存，不混淆相机与结构收益。
            Vector3 defaultPosition = camera.transform.position; Quaternion defaultRotation = camera.transform.rotation;
            float defaultFarClip = camera.farClipPlane; camera.farClipPlane = 3000;
            camera.transform.position = new Vector3(230, 180, -330); camera.transform.LookAt(Vector3.zero);
            try
            {
                fish.Render();
                RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
                Read(target, prefix + "-live");
                File.WriteAllText(Path.Combine(Evidence, prefix + "-live.json"),
                    $"{{\"api\":\"{SystemInfo.graphicsDeviceType}\",\"agents\":{fish.Poses.Count},\"tick\":{source.World.Tick},\"width\":1280,\"height\":720,\"postProcessing\":false,\"environmentSeconds\":4,\"cameraPosition\":\"{camera.transform.position}\",\"extinction\":{{\"r\":{ocean.Extinction.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},\"g\":{ocean.Extinction.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},\"b\":{ocean.Extinction.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}}}}");
                Assert.That(fish.Poses.Count, Is.EqualTo(1024));
                if (!baseline)
                {
                    camera.transform.SetPositionAndRotation(defaultPosition, defaultRotation);
                    camera.farClipPlane = defaultFarClip;
                    fish.Render();
                    RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
                    Read(target, prefix + "-live-default");
                }
            }
            finally { camera.targetTexture = null; target.Release(); Object.Destroy(target); }
            yield return SceneManager.UnloadSceneAsync(scene);
            LogAssert.NoUnexpectedReceived();
        }

        [System.Serializable] private sealed class OpticalSample
        {
            public string path, receiver;
            public float cameraDistance, cameraX, expectedDistance, sampledDistance, rgbError, fogError;
        }
        [System.Serializable] private sealed class OpticalEvidence
        {
            public string api;
            public List<OpticalSample> samples = new List<OpticalSample>();
        }
        private static float RayMeshDistance(Ray ray, Vector3[] vertices, int[] indices)
        {
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < indices.Length; i += 3)
            {
                var a = vertices[indices[i]]; var e1 = vertices[indices[i + 1]] - a; var e2 = vertices[indices[i + 2]] - a;
                var p = Vector3.Cross(ray.direction, e2); float determinant = Vector3.Dot(e1, p);
                if (Mathf.Abs(determinant) < 1e-7f) continue;
                var offset = ray.origin - a; float u = Vector3.Dot(offset, p) / determinant;
                var q = Vector3.Cross(offset, e1); float v = Vector3.Dot(ray.direction, q) / determinant;
                float t = Vector3.Dot(e2, q) / determinant;
                if (u >= 0 && v >= 0 && u + v <= 1 && t >= 0) nearest = Mathf.Min(nearest, t);
            }
            return nearest;
        }

        [UnityTest]
        public IEnumerator FishAndReferenceMeshShareSingleRgbFogAcrossDepthPaths()
        {
            #if UNITY_EDITOR
            var rendererSource = UnityEditor.AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/PC_Renderer.asset");
            var culling = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/RVO/Rendering/Shaders/FishCulling.compute");
            #else
            Assert.Ignore("Editor pipeline fixture."); yield break;
            #endif
            var oldPipeline = QualitySettings.renderPipeline;
            var evidence = new OpticalEvidence { api = SystemInfo.graphicsDeviceType.ToString() };
            using var storage = new AgentStorage(1);
            var agent = storage.Initialization;
            agent.Ids[0] = 17; agent.Positions[0] = float3.zero; agent.Goals[0] = new float3(10, 0, 0);
            agent.Velocities[0] = new float3(2, 0, 0);
            agent.Parameters[0] = new AgentParameters { Radius = 1, MaxSpeed = 2, ArrivalDistance = 0.1f };
            var root = new GameObject("P0 optical fixture");
            var camera = root.AddComponent<Camera>(); camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            camera.orthographic = true; camera.orthographicSize = 1; camera.nearClipPlane = 0.1f; camera.farClipPlane = 100;
            var fish = root.AddComponent<GpuFishRenderer>(); fish.AutoRender = false; fish.ViewCamera = camera;
            fish.FishShader = Shader.Find("RVO/Procedural Fish Indirect"); fish.CullingShader = culling; fish.DetailedNearMesh = true; fish.ForceLod = 0;
            fish.Capture(new AgentSnapshot(storage.Read, 0, 1, 1f / 30));
            var ocean = root.AddComponent<OceanEnvironment>(); ocean.FogShader = Shader.Find("RVO/Ocean Beer Fog");
            ocean.Background = false; ocean.Quality = CausticQuality.Off; ocean.FixedClock = true;
            ocean.Size = Vector3.one * 200; ocean.WaterSurfaceHeight = 90; ocean.HorizonDistance = 100;
            ocean.Extinction = WaterOptics.Calibrate(new Vector3(0.2f, 0.4f, 0.65f), 32);
            ocean.WaterColor = new Color(0.04f, 0.12f, 0.2f);
            var target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            target.Create(); camera.targetTexture = target;
            var mesh = ProceduralFishMesh.CreateDetailed();
            var pose = fish.Poses.Current[0]; var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                var point = vertices[i]; point.x += FishAnimationBaker.Wave(point.z, pose.Animation.x).x * pose.Animation.y;
                vertices[i] = math.mul(new quaternion(pose.Rotation), (float3)point * pose.Animation.w);
            }
            mesh.vertices = vertices; mesh.RecalculateBounds();
            var reference = new GameObject("CPU-deformed reference fish mesh");
            reference.AddComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit")); material.SetColor("_BaseColor", Color.white);
            reference.AddComponent<MeshRenderer>().sharedMaterial = material; reference.SetActive(false);
            const int pixel = 64 * 128 + 64;
            try
            {
                foreach (string path in new[] { "copy-no-ssao", "normals-ssao", "forced-prepass-no-ssao" })
                {
                    var rendererData = Object.Instantiate(rendererSource);
                    if (path != "normals-ssao") rendererData.rendererFeatures.Clear();
                    rendererData.copyDepthMode = path == "forced-prepass-no-ssao" ? CopyDepthMode.ForcePrepass : CopyDepthMode.AfterOpaques;
                    var pipeline = UniversalRenderPipelineAsset.Create(rendererData);
                    pipeline.supportsHDR = true; pipeline.supportsCameraDepthTexture = true; pipeline.msaaSampleCount = 1;
                    try
                    {
                        QualitySettings.renderPipeline = pipeline; yield return null;
                        foreach (float distance in new[] { 8f, 16f, 32f })
                        foreach (float cameraX in new[] { 0f, 0.12f })
                        foreach (bool drawFish in new[] { true, false })
                        {
                            camera.transform.SetPositionAndRotation(new Vector3(cameraX, 0, -distance), Quaternion.identity);
                            reference.SetActive(!drawFish); yield return null;
                            if (drawFish) fish.Render();
                            string label = drawFish ? "fish" : "mesh";
                            string image = distance == 8 && cameraX == 0 ? evidence.api + "-" + path + "-" + label : null;
                            Color Capture(OceanDebugView mode)
                            {
                                ocean.DebugView = mode;
                                RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
                                return Read(target, image == null ? null : image + "-" + mode)[pixel];
                            }
                            var raw = Capture(OceanDebugView.BeforeFog);
                            var opticalDistance = Capture(OceanDebugView.WaterDistance).r * ocean.HorizonDistance;
                            var sampledT = Capture(OceanDebugView.Transmittance);
                            var final = Capture(OceanDebugView.Final);
                            // CPU 三角形求交独立于 sampled depth，能识别鱼缺席 prepass/copy 的错误。
                            float expectedDistance = RayMeshDistance(camera.ViewportPointToRay(new Vector3(64.5f / 128, 64.5f / 128, 0)), vertices, mesh.triangles);
                            Assert.That(float.IsInfinity(expectedDistance), Is.False);
                            var expectedT = WaterOptics.Transmittance(ocean.Extinction, expectedDistance);
                            float rgbError = Mathf.Max(Mathf.Abs(sampledT.r - expectedT.x), Mathf.Abs(sampledT.g - expectedT.y), Mathf.Abs(sampledT.b - expectedT.z));
                            var expected = new Color(raw.r * expectedT.x + ocean.WaterColor.r * (1 - expectedT.x),
                                raw.g * expectedT.y + ocean.WaterColor.g * (1 - expectedT.y), raw.b * expectedT.z + ocean.WaterColor.b * (1 - expectedT.z));
                            float fogError = Mathf.Max(Mathf.Abs(final.r - expected.r), Mathf.Abs(final.g - expected.g), Mathf.Abs(final.b - expected.b));
                            evidence.samples.Add(new OpticalSample { path = path, receiver = label, cameraDistance = distance, cameraX = cameraX,
                                expectedDistance = expectedDistance, sampledDistance = opticalDistance, rgbError = rgbError, fogError = fogError });
                        }
                    }
                    finally { QualitySettings.renderPipeline = oldPipeline; Object.Destroy(pipeline); Object.Destroy(rendererData); }
                    yield return null;
                }
                File.WriteAllText(Path.Combine(Evidence, evidence.api + "-optics.json"), JsonUtility.ToJson(evidence, true));
                foreach (var sample in evidence.samples)
                {
                    Assert.That(sample.sampledDistance, Is.EqualTo(sample.expectedDistance).Within(0.04f), sample.path + "/" + sample.receiver);
                    Assert.That(sample.rgbError, Is.LessThan(0.004f), sample.path + "/" + sample.receiver);
                    Assert.That(sample.fogError, Is.LessThan(0.006f), "one view extinction only: " + sample.path + "/" + sample.receiver);
                }
            }
            finally
            {
                QualitySettings.renderPipeline = oldPipeline; camera.targetTexture = null; target.Release();
                Object.Destroy(target); Object.Destroy(root); Object.Destroy(reference); Object.Destroy(material); Object.Destroy(mesh);
            }
            yield return null; LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator DirectionalBackgroundHasNoGeometryAndIndependentOpticalHorizon()
        {
            var root = new GameObject("P0 empty water"); var camera = root.AddComponent<Camera>(); camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            var ocean = root.AddComponent<OceanEnvironment>(); ocean.FogShader = Shader.Find("RVO/Ocean Beer Fog");
            ocean.Quality = CausticQuality.Off; ocean.Size = Vector3.one * 2000; ocean.WaterSurfaceHeight = 500;
            ocean.HorizonDistance = 80; ocean.FixedClock = true;
            var target = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            target.Create(); camera.targetTexture = target;
            Color Capture()
            {
                RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
                return Read(target, null)[32 * 64 + 32];
            }
            try
            {
                camera.farClipPlane = 100; var first = Capture(); camera.farClipPlane = 1000; var second = Capture();
                Assert.That(Vector4.Distance(first, second), Is.LessThan(0.0002f));
                Assert.That(first.maxColorComponent, Is.GreaterThan(0.01f));
                Assert.That(root.GetComponentsInChildren<MeshRenderer>().Length, Is.Zero);
                ocean.DebugView = OceanDebugView.SceneDepth;
                Assert.That(Capture().r, Is.EqualTo(0).Within(1e-5), "Background must not manufacture sampled geometry depth.");
                camera.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
                ocean.WaterSurfaceHeight = 5; ocean.DebugView = OceanDebugView.WaterDistance;
                Assert.That(Capture().r * 80, Is.EqualTo(5).Within(0.02f), "WaterSurfaceHeight is independent of domain top.");
                ocean.Size *= 2;
                Assert.That(Capture().r * 80, Is.EqualTo(5).Within(0.02f));
            }
            finally { camera.targetTexture = null; target.Release(); Object.Destroy(target); Object.Destroy(root); }
            yield return null; LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator DisplayHistoryTracksRenderedFramesAndResetsAtGenerationOrCut()
        {
            #if UNITY_EDITOR
            var culling = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/RVO/Rendering/Shaders/FishCulling.compute");
            #else
            Assert.Ignore("Editor compute fixture."); yield break;
            #endif
            using var storage = new AgentStorage(1); var agent = storage.Initialization;
            agent.Ids[0] = 1; agent.Goals[0] = new float3(10, 0, 0); agent.Velocities[0] = new float3(2, 0, 0);
            agent.Parameters[0] = new AgentParameters { Radius = 1, MaxSpeed = 2, ArrivalDistance = 0.1f };
            var root = new GameObject("P0 display history"); var camera = root.AddComponent<Camera>(); camera.enabled = false;
            camera.transform.position = new Vector3(0, 0, -8);
            var fish = root.AddComponent<GpuFishRenderer>(); fish.AutoRender = false; fish.ViewCamera = camera;
            fish.CullingShader = culling; fish.FishShader = Shader.Find("RVO/Procedural Fish Indirect");
            var history = new FishGpuData[1];
            try
            {
                fish.Capture(new AgentSnapshot(storage.Read, 0, 1, 1f / 30)); fish.Render();
                Assert.That(fish.HasDisplayHistory, Is.False);
                yield return null;
                agent.Positions[0] = new float3(1, 0, 0);
                fish.Capture(new AgentSnapshot(storage.Read, 1, 1, 1f / 30)); fish.Interpolation = 0.3f; fish.Render();
                Assert.That(fish.HasDisplayHistory, Is.True);
                fish.PreviousDisplay.GetData(history); Assert.That(history[0].PositionRadius.x, Is.EqualTo(0).Within(1e-5));
                fish.Interpolation = 0.8f; fish.Render();
                fish.PreviousDisplay.GetData(history); Assert.That(history[0].PositionRadius.x, Is.EqualTo(0).Within(1e-5), "Second request in one frame must retain previous frame.");
                yield return null; fish.Render(); fish.PreviousDisplay.GetData(history);
                Assert.That(history[0].PositionRadius.x, Is.EqualTo(0.8f).Within(1e-5));
                fish.Capture(new AgentSnapshot(storage.Read, 0, 2, 1f / 30)); fish.Render();
                Assert.That(fish.HasDisplayHistory, Is.False);
                fish.InvalidateDisplayHistory(); fish.Render(); Assert.That(fish.HasDisplayHistory, Is.False);
                fish.enabled = false; Assert.That(fish.BufferBytes, Is.Zero); Assert.That(fish.PreviousDisplay, Is.Null);
            }
            finally { Object.Destroy(root); }
            yield return null; LogAssert.NoUnexpectedReceived();
        }
    }
}

