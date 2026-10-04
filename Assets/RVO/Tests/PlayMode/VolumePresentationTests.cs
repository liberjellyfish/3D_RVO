using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Rvo.Tests
{
    public sealed class VolumePresentationTests
    {
        private static string EvidenceFolder => "Documentation/RVO/Verification/OceanP0/VolumePresentation/" + SystemInfo.graphicsDeviceType;
        [UnityTest]
        public IEnumerator ArrivalColorsDistinguishStoppedAgentsAndRestoreOnGoalChange()
        {
            var root = new GameObject("Arrival colors test");
            var cameraObject = new GameObject("Arrival camera"); var camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 1, -10); camera.transform.LookAt(Vector3.zero);
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.08f, 0.1f, 0.14f);
            var material = new Material(Shader.Find("RVO/Agent Colors"));
            var presenter = root.AddComponent<VolumePresenter>(); presenter.Material = material;
            var settings = VolumeSettings.Default; settings.Resolution = 12;
            var map = VolumeBake.Bake(settings, 0.8f, System.Array.Empty<VolumeBox>());
            using (var storage = new AgentStorage(2))
            try
            {
                var a = storage.Initialization;
                for (int i = 0; i < 2; i++)
                {
                    a.Ids[i] = i; a.Positions[i] = new Unity.Mathematics.float3(i == 0 ? -2 : 2, 0, 0);
                    a.Goals[i] = a.Positions[i] + new Unity.Mathematics.float3(0, i == 0 ? 0 : 2, 0);
                    a.Parameters[i] = new AgentParameters { Radius = 0.8f, MaxSpeed = 2, ArrivalDistance = 0.2f };
                }
                presenter.Initialize(map, storage.Read);
                var mesh = root.GetComponent<MeshFilter>().sharedMesh; var original = mesh.colors;
                presenter.Present(storage.Read, null, null, null);
                var marked = mesh.colors;
                Assert.That(marked[0], Is.EqualTo(Color.white));
                Assert.That(marked[27].maxColorComponent, Is.LessThan(0.1f));
                Assert.That(marked[63], Is.EqualTo(original[63]), "Stopped away from goal must retain its color.");
                root.transform.Find("Static volume").gameObject.SetActive(false);
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    var target = new RenderTexture(800, 450, 24); var texture = new Texture2D(800, 450, TextureFormat.RGB24, false);
                    var previous = RenderTexture.active;
                    try
                    {
                        camera.targetTexture = target; yield return null; yield return null;
                        RenderTexture.active = target; texture.ReadPixels(new Rect(0, 0, 800, 450), 0, 0); texture.Apply();
                        Directory.CreateDirectory(EvidenceFolder);
                        File.WriteAllBytes(Path.Combine(EvidenceFolder,"arrived.png"), texture.EncodeToPNG());
                    }
                    finally { camera.targetTexture = null; RenderTexture.active = previous; target.Release(); Object.Destroy(target); Object.Destroy(texture); }
                }
                a.Goals[0] += new Unity.Mathematics.float3(0, 2, 0);
                presenter.Present(storage.Read, null, null, null);
                CollectionAssert.AreEqual(original, mesh.colors);
                a.Goals[0] = a.Positions[0]; presenter.Initialize(map, storage.Read);
                presenter.Present(storage.Read, null, null, null);
                Assert.That(root.GetComponent<MeshFilter>().sharedMesh.colors[0], Is.EqualTo(Color.white));
            }
            finally { Object.Destroy(root); Object.Destroy(cameraObject); Object.Destroy(material); }
            yield return null; LogAssert.NoUnexpectedReceived();
        }
        [UnityTest]
        public IEnumerator CameraContinuouslyFollowsAndFitStopsFollowing()
        {
            #if UNITY_EDITOR
            var profile = UnityEditor.AssetDatabase.LoadAssetAtPath<SimulationProfile>("Assets/RVO/Demo/08_VolumeNavigation_ORCA.asset");
            #else
            Assert.Ignore("Requires the baked editor demo.");
            SimulationProfile profile = null;
            #endif
            var root = new GameObject("Volume presentation test"); root.SetActive(false);
            var cameraObject = new GameObject("Volume test camera"); var camera = cameraObject.AddComponent<Camera>();
            var controls = cameraObject.AddComponent<VolumeCameraControls>();
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.025f, 0.04f, 0.065f);
            var material = new Material(Shader.Find("RVO/Agent Colors"));
            var presenter = root.AddComponent<VolumePresenter>(); presenter.Material = material; presenter.ViewCamera = camera;
            var driver = root.AddComponent<VolumeSimulationBootstrap>(); driver.Profile = profile; driver.Presenter = presenter; driver.Paused = true;
            try
            {
                root.SetActive(true); yield return null;
                Assert.That(driver.LastError, Is.Null); Assert.That(presenter.ShowPaths, Is.False);
                Assert.That(root.transform.Find("Volume debug").GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.Zero);
                // A fitted overview is saved for visual review of the actual baked demo.
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    var target = new RenderTexture(1400, 900, 24); var texture = new Texture2D(1400, 900, TextureFormat.RGB24, false);
                    var previous = RenderTexture.active;
                    try
                    {
                        camera.targetTexture = target; yield return null; yield return null;
                        RenderTexture.active = target; texture.ReadPixels(new Rect(0, 0, 1400, 900), 0, 0); texture.Apply();
                        Directory.CreateDirectory(EvidenceFolder);
                        File.WriteAllBytes(Path.Combine(EvidenceFolder,"overview.png"), texture.EncodeToPNG());
                    }
                    finally { camera.targetTexture = null; RenderTexture.active = previous; target.Release(); Object.Destroy(target); Object.Destroy(texture); }
                }
                presenter.Focus(driver.World.Snapshot, 0);
                Vector3 initial = camera.transform.position, relative = camera.transform.position - controls.Pivot;
                Quaternion rotation = camera.transform.rotation;
                for (int frame = 0; frame < 45; frame++)
                {
                    for (int step = 0; step < 4; step++) driver.StepOnce();
                    yield return null;
                    Assert.That(Vector3.Distance(camera.transform.position - controls.Pivot, relative), Is.LessThan(0.002f));
                    Assert.That(Quaternion.Angle(rotation, camera.transform.rotation), Is.LessThan(0.001f));
                }
                Assert.That(driver.LastError, Is.Null);
                Assert.That(Vector3.Distance(initial, camera.transform.position), Is.GreaterThan(0.1f));
                presenter.Fit(); Assert.That(controls.Following, Is.False);
                Vector3 fitted = camera.transform.position;
                driver.StepOnce(); yield return null;
                Assert.That(camera.transform.position, Is.EqualTo(fitted));
                driver.ResetSimulation(); Assert.That(driver.LastError, Is.Null);
            }
            finally { Object.Destroy(root); Object.Destroy(cameraObject); Object.Destroy(material); }
            yield return null; LogAssert.NoUnexpectedReceived();
        }
    }
}
