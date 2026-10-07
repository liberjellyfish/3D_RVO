using System.Collections;
using System.IO;
using NUnit.Framework;
using Rvo.Rendering;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Rvo.Tests
{
    public sealed class ReefNetworkPresentationTests
    {
        [UnityTest]
        public IEnumerator NetworkSceneRuns2048WithLiveStrategyHudAndSampledRoutes()
        {
            #if UNITY_EDITOR
            const string path="Assets/RVO/Demo/OceanReef/OceanReefLive.unity";
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(path,new LoadSceneParameters(LoadSceneMode.Additive));
            #else
            Assert.Ignore("Editor scene fixture."); yield break;
            #endif
            var scene=SceneManager.GetSceneByName("OceanReefLive");
            VolumeSimulationBootstrap source=null; ReefPathOverlay routes=null;
            foreach(var root in scene.GetRootGameObjects()) if(root.TryGetComponent<VolumeSimulationBootstrap>(out var value))
            { source=value; routes=root.GetComponent<ReefPathOverlay>(); }
            Assert.That(source,Is.Not.Null); source.Paused=true;
            yield return null;
            source.ResetSimulation();
            Assert.That(source.LastError,Is.Null);
            Assert.That(source.World.Settings.AgentCount,Is.EqualTo(2048));
            Assert.That(source.ShowHud,Is.True);
            Assert.That(source.Profile.Scenario.VolumeSpawns,Is.EqualTo(VolumeSpawnPattern.DistributedRooms));
            Assert.That(routes,Is.Not.Null); Assert.That(routes.ShowRoutes,Is.True);
            for(int tick=0;tick<120;tick++)
            {
                source.StepOnce(false);
                if(tick%15==0) yield return null;
            }
            yield return null;
            Assert.That(source.LastError,Is.Null);
            Assert.That(source.Metrics.NeverReady,Is.Zero);
            Assert.That(source.Navigation.FailedCount,Is.Zero);
            int visible=0;
            foreach(var line in routes.GetComponentsInChildren<LineRenderer>()) if(line.enabled && line.positionCount>=2) visible++;
            Assert.That(visible,Is.GreaterThanOrEqualTo(6),"Several independently selected remaining paths must be visible.");
            const string folder="Documentation/RVO/Verification/ReefNetwork2048";
            Directory.CreateDirectory(folder);
            File.WriteAllText(folder+"/presentation-state.txt",$"agents={source.World.Settings.AgentCount}\nHUD={source.ShowHud}\nplanner={source.Navigation.Name}\ncoarse={source.Profile.Volume.UseCoarseRoutes}\nweight={source.Profile.Volume.HeuristicWeight}\ntick={source.World.Tick}\nneverReady={source.Metrics.NeverReady}\nfailed={source.Navigation.FailedCount}\nvisibleSampleRoutes={visible}\n");
            yield return SceneManager.UnloadSceneAsync(scene);
            yield return null; LogAssert.NoUnexpectedReceived();
        }
    }
}
