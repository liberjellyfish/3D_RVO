using System;
using UnityEngine;

namespace Rvo.Rendering
{
    /// <summary>仅演示装配与质量切换，不进入快照/GPU 核心。</summary>
    public sealed class OceanDemoControls : MonoBehaviour
    {
        public GpuFishRenderer Fish;
        public bool ShowHud = true;
        private OceanEnvironment ocean;
        private FishRenderFixture fixture;
        private void Start()
        {
            ocean = GetComponent<OceanEnvironment>();
            fixture = Fish != null ? Fish.GetComponent<FishRenderFixture>() : null;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-rvo-benchmark") ShowHud = false;
                if (args[i] == "-rvo-no-ocean") ocean.enabled = false;
                if (args[i] == "-rvo-no-fog") ocean.Fog = false;
                if (args[i] == "-rvo-short-loop") ocean.ShortLoop = true;
                if (i+1 >= args.Length) continue;
                if (args[i] == "-rvo-animation" && Enum.TryParse(args[i+1],true,out FishAnimationMode animation)) Fish.NearAnimation = animation;
                if (args[i] == "-rvo-far" && Enum.TryParse(args[i+1],true,out FishFarMode far)) { Fish.FarRepresentation = far; Fish.EnableFarCards = far != FishFarMode.Mesh; }
                if (args[i] == "-rvo-lod" && int.TryParse(args[i+1],out int lod)) Fish.ForceLod = Mathf.Clamp(lod,-1,3);
                if (args[i] == "-rvo-caustic" && Enum.TryParse(args[i+1],true,out CausticQuality quality)) ocean.Quality = quality;
                if (args[i] == "-rvo-caustic-hz" && int.TryParse(args[i+1],out int hz)) ocean.UpdateHz = Mathf.Clamp(hz,1,60);
            }
        }
        private void LateUpdate()
        {
            if (fixture != null && fixture.FixedReplayClock)
            {
                ocean.FixedClock = true;
                ocean.EnvironmentSeconds = (fixture.Tick + Fish.Interpolation) * FishRenderFixture.Step;
            }
        }
        private void OnGUI()
        {
            if (!ShowHud || ocean == null || Fish == null) return;
            GUILayout.BeginArea(new Rect(Screen.width-300,12,288,300),GUI.skin.box);
            GUILayout.Label("Phase 4 · Ocean / representation lab");
            ocean.Fog = GUILayout.Toggle(ocean.Fog,"Beer fog (one opaque composite)");
            ocean.Background = GUILayout.Toggle(ocean.Background,"Inward background");
            ocean.PauseEnvironment = GUILayout.Toggle(ocean.PauseEnvironment,"Pause environment clock");
            if (GUILayout.Button("Caustic: " + ocean.Quality)) ocean.Quality = (CausticQuality)(((int)ocean.Quality+1)%4);
            if (GUILayout.Button("Near: " + Fish.NearAnimation)) Fish.NearAnimation = (FishAnimationMode)(((int)Fish.NearAnimation+1)%3);
            if (GUILayout.Button("Far experiment: " + Fish.FarRepresentation))
            { Fish.FarRepresentation = (FishFarMode)(((int)Fish.FarRepresentation+1)%4); Fish.EnableFarCards = Fish.FarRepresentation != FishFarMode.Mesh; }
            Fish.ShowLodColors = GUILayout.Toggle(Fish.ShowLodColors,"LOD colors");
            GUILayout.Label("Fish shadows off · card depth is planar");
            if (ocean.LastError != null) GUILayout.Label(ocean.LastError);
            GUILayout.EndArea();
        }
    }
}
