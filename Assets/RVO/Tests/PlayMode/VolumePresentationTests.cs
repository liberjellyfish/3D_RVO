using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Rvo.Tests
{
    public sealed class VolumePresentationTests
    {
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
                        Directory.CreateDirectory("Documentation/RVO/Verification/Phase3/Presentation");
                        File.WriteAllBytes("Documentation/RVO/Verification/Phase3/Presentation/overview.png", texture.EncodeToPNG());
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
