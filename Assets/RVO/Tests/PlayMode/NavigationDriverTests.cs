using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Rvo.Tests
{
    public sealed class NavigationDriverTests
    {
        [UnityTest]
        public IEnumerator BakedNavigationDriverSwitchesTiersRendersAndReleases()
        {
            var profile = ScriptableObject.CreateInstance<SimulationProfile>();
            profile.Simulation.AgentCount = 16; profile.Simulation.MaxNeighbors = 15;
            profile.Simulation.Avoidance = AvoidanceAlgorithm.ORCA; profile.Simulation.Backend = ExecutionBackend.JobsBurst;
            profile.Simulation.NeighborSearch = NeighborSearchAlgorithm.SpatialHash;
            profile.Simulation.TimeHorizon = 2; profile.Simulation.NeighborDistance = 24; profile.Simulation.PreferredSideBias = 0.08f;
            profile.Scenario.Kind = ScenarioKind.RandomCrowd; profile.Navigation.Enabled = true;
            profile.Scenario.Radius = 2.5f; profile.Scenario.MaxSpeed = 12;
            profile.Simulation.NeighborDistance = 56; profile.Simulation.CellSize = 24;
            #if UNITY_EDITOR
            profile.BakedMap = UnityEditor.AssetDatabase.LoadAssetAtPath<BakedNavigationMap>("Assets/RVO/Demo/07_GridNavigation_ORCA_Map.asset");
            #else
            Assert.Ignore("This editor fixture requires the baked demo asset.");
            #endif
            Assert.That(profile.BakedMap, Is.Not.Null, "Generate + Bake the 512 demo first.");
            var go = new GameObject("Navigation driver test"); go.SetActive(false);
            var cameraObject = new GameObject("Navigation test camera"); var camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true; camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.05f, 0.08f);
            var material = new Material(Shader.Find("RVO/Agent Colors"));
            var driver = go.AddComponent<SimulationBootstrap>(); var presenter = go.AddComponent<AgentMeshPresenter>();
            var map = go.AddComponent<GridMapPresenter>(); map.Material = material;
            presenter.AgentMaterial = material; presenter.ViewCamera = camera;
            driver.Profile = profile; driver.Presenter = presenter; driver.MapPresenter = map; driver.Paused = true;
            try
            {
                go.SetActive(true); yield return null;
                Assert.That(driver.LastError, Is.Null); Assert.That(driver.Navigation, Is.Not.Null);
                int version = driver.Navigation.Map.Version; var baked = driver.Navigation.Map;
                Assert.That(driver.Navigation.Map.Version, Is.EqualTo(version));
                driver.StepOnce(); Assert.That(driver.Navigation.Map.Version, Is.EqualTo(version));
                for (int i = 0; i < 120; i++) driver.StepOnce();
                Assert.That(driver.CollisionTicks, Is.Zero); Assert.That(driver.LastError, Is.Null);
                Assert.That(go.GetComponentsInChildren<MeshFilter>().Length, Is.EqualTo(3));
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    var target = new RenderTexture(1200, 800, 24); var image = new Texture2D(1200, 800, TextureFormat.RGB24, false);
                    var previous = RenderTexture.active;
                    try
                    {
                        camera.targetTexture = target; yield return null; yield return null;
                        RenderTexture.active = target; image.ReadPixels(new Rect(0, 0, 1200, 800), 0, 0); image.Apply();
                        int obstaclePixels = 0, agentPixels = 0;
                        foreach (var pixel in image.GetPixels32())
                        {
                            if (pixel.r > 65 && pixel.r < 110 && pixel.g > 85 && pixel.g < 135 && pixel.b > 110 && pixel.b < 160) obstaclePixels++;
                            if (Mathf.Max(pixel.r, Mathf.Max(pixel.g, pixel.b)) > 200) agentPixels++;
                        }
                        Assert.That(obstaclePixels, Is.GreaterThan(500)); Assert.That(agentPixels, Is.GreaterThan(100));
                        Directory.CreateDirectory("Documentation/RVO/Verification/Phase2Optimization");
                        File.WriteAllBytes("Documentation/RVO/Verification/Phase2Optimization/Navigation.png", image.EncodeToPNG());
                    }
                    finally { camera.targetTexture = null; RenderTexture.active = previous; target.Release(); Object.Destroy(target); Object.Destroy(image); }
                }
                driver.SetAgentTier(1); Assert.That(driver.World.Settings.AgentCount, Is.EqualTo(profile.AgentCountTiers.y));
                Assert.That(driver.Navigation.Map, Is.SameAs(baked));
                Assert.That(profile.Simulation.AgentCount, Is.EqualTo(16));
                Assert.That(profile.Navigation.Seed, Is.EqualTo(7));
                for (int i = 0; i < 3; i++) { driver.ResetSimulation(); driver.StepOnce(); }
                yield return null; // 延迟销毁旧 Mesh/GameObject。
                Assert.That(go.GetComponentsInChildren<MeshFilter>().Length, Is.EqualTo(3));
                var world = driver.World; go.SetActive(false); Assert.That(world.State, Is.EqualTo(WorldState.Disposed));
                go.SetActive(true); yield return null; Assert.That(driver.World.Tick, Is.Zero);
                Assert.That(driver.LastError, Is.Null);
            }
            finally { Object.Destroy(go); Object.Destroy(cameraObject); Object.Destroy(material); Object.Destroy(profile); }
            yield return null; LogAssert.NoUnexpectedReceived();
        }
    }
}
