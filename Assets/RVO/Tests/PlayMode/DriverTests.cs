using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Rvo.Tests
{
    public sealed class DriverTests
    {
        [UnityTest]
        public IEnumerator PauseStepResetDisableAndPresentationWorkTogether()
        {
            var profile = ScriptableObject.CreateInstance<SimulationProfile>();
            profile.Simulation.AgentCount = 2;
            profile.Scenario.Kind = ScenarioKind.HeadOnPair;
            profile.Scenario.Extent = 6;
            var go = new GameObject("Driver test");
            go.SetActive(false);
            var cameraObject = new GameObject("Test camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true; camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f,0.05f,0.08f);
            var material = new Material(Shader.Find("RVO/Agent Colors"));
            var driver = go.AddComponent<SimulationBootstrap>();
            var presenter = go.AddComponent<AgentMeshPresenter>();
            driver.Profile = profile; driver.Presenter = presenter; driver.Paused = true; driver.ShowControls = false;
            presenter.AgentMaterial = material; presenter.ViewCamera = camera;
            try
            {
                go.SetActive(true);
                yield return null;
                Assert.That(driver.World, Is.Not.Null);
                Assert.That(driver.World.Tick, Is.Zero);
                driver.StepOnce();
                Assert.That(driver.World.Tick, Is.EqualTo(1));
                Assert.That(go.GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.EqualTo(34));
                for (int i = 0; i < 90; i++) driver.StepOnce();

                // 有图形设备时同时检查实际像素并保存图；-nographics 路径不伪造渲染结果。
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    var target = new RenderTexture(960, 600, 24);
                    var image = new Texture2D(960, 600, TextureFormat.RGB24, false);
                    var previous = RenderTexture.active;
                    try
                    {
                        camera.targetTexture = target;
                        // 等待正常相机渲染，兼容 URP；不调用只面向内置管线的 Camera.Render。
                        yield return null;
                        yield return null;
                        RenderTexture.active = target;
                        image.ReadPixels(new Rect(0,0,960,600), 0,0); image.Apply();
                        int coloredPixels = 0;
                        foreach (var pixel in image.GetPixels32())
                            if (pixel.r > 100 || pixel.g > 100 || pixel.b > 100) coloredPixels++;
                        Assert.That(coloredPixels, Is.GreaterThan(50), "Agent material did not render.");
                        Directory.CreateDirectory("Verification");
                        File.WriteAllBytes("Verification/Phase1.png", image.EncodeToPNG());
                    }
                    finally
                    {
                        camera.targetTexture = null; RenderTexture.active = previous;
                        target.Release(); Object.Destroy(target); Object.Destroy(image);
                    }
                }

                driver.SetAvoidance((int)AvoidanceAlgorithm.None);
                Assert.That(profile.Simulation.Avoidance, Is.EqualTo(AvoidanceAlgorithm.VO));
                Assert.That(driver.World.Settings.Avoidance, Is.EqualTo(AvoidanceAlgorithm.None));
                foreach (var algorithm in new[] { AvoidanceAlgorithm.VO, AvoidanceAlgorithm.RVO, AvoidanceAlgorithm.ORCA })
                {
                    driver.SetAvoidance((int)algorithm); driver.SetBackend((int)ExecutionBackend.JobsBurst);
                    driver.SetNeighborSearch((int)NeighborSearchAlgorithm.SpatialHash); driver.SetSideBias(0.05f);
                    for (int i = 0; i < 4; i++) { driver.ResetSimulation(); driver.StepOnce(); }
                    Assert.That(driver.LastError, Is.Null);
                    Assert.That(driver.World.Settings.Avoidance, Is.EqualTo(algorithm));
                }
                Assert.That(profile.Simulation.NeighborSearch, Is.EqualTo(NeighborSearchAlgorithm.BruteForce));
                Assert.That(profile.Simulation.Backend, Is.EqualTo(ExecutionBackend.Reference));
                Assert.That(profile.Simulation.PreferredSideBias, Is.Zero);
                Assert.That(driver.World.Tick, Is.EqualTo(1));
                var disposed = driver.World;
                go.SetActive(false);
                Assert.That(disposed.State, Is.EqualTo(WorldState.Disposed));
                Assert.That(driver.World, Is.Null);
                go.SetActive(true);
                yield return null;
                Assert.That(driver.World.Tick, Is.Zero);
                driver.SetPaused(false);
                yield return new WaitForSecondsRealtime(0.15f);
                driver.SetPaused(true);
                Assert.That(driver.World.Tick, Is.GreaterThan(0));
                long tick = driver.World.Tick;
                yield return null;
                Assert.That(driver.World.Tick, Is.EqualTo(tick));
            }
            finally
            {
                Object.Destroy(go); Object.Destroy(cameraObject); Object.Destroy(material); Object.Destroy(profile);
            }
            yield return null;
            LogAssert.NoUnexpectedReceived();
        }
    }
}
