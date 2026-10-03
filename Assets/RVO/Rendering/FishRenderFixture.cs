using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Rvo.Rendering
{
    public enum FishFixtureMode { Empty, DebugSpheres, GpuFish }

    /// <summary>确定性合成快照，专用于渲染能力测量，不运行导航或 ORCA。</summary>
    public sealed class FishRenderFixture : MonoBehaviour
    {
        public GpuFishRenderer Renderer;
        public VolumePresenter DebugPresenter;
        [Range(1, 30000)] public int AgentCount = 10000;
        public FishFixtureMode Mode = FishFixtureMode.GpuFish;
        public Vector3 Extent = new Vector3(45, 20, 45);
        public bool Paused, OrbitCamera, ShowHud = true;
        public const float Step = 1f / 30;
        public long Tick { get; private set; }
        public long DroppedTicks => clock.DroppedTicks;
        public uint Generation { get; private set; }
        public AgentReadView View => new AgentReadView(ids, positions, velocities, goals, parameters);
        private NativeArray<int> ids;
        private NativeArray<float3> positions, velocities, goals;
        private NativeArray<AgentParameters> parameters;
        private readonly FixedStepClock clock = new FixedStepClock();
        private bool started;
        private void Start() { started = true; ReadArguments(); ResetFixture(); }
        private void OnEnable() { if (started) ResetFixture(); }
        private void OnDisable() { Release(); Renderer?.Clear(); DebugPresenter?.Clear(); }
        private void ReadArguments()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-rvo-count" && int.TryParse(args[i + 1], out int count)) AgentCount = Mathf.Clamp(count, 1, 30000);
                if (args[i] == "-rvo-mode" && Enum.TryParse(args[i + 1], true, out FishFixtureMode mode)) Mode = mode;
            }
        }
        public void ResetFixture()
        {
            Release(); Renderer?.Clear(); DebugPresenter?.Clear(); clock.Reset(); Tick = 0; Generation++;
            int count = Mathf.Clamp(AgentCount, 1, 30000);
            try
            {
                ids = new NativeArray<int>(count, Allocator.Persistent);
                positions = new NativeArray<float3>(count, Allocator.Persistent);
                velocities = new NativeArray<float3>(count, Allocator.Persistent);
                goals = new NativeArray<float3>(count, Allocator.Persistent);
                parameters = new NativeArray<AgentParameters>(count, Allocator.Persistent);
                Evaluate();
                if (DebugPresenter != null)
                {
                    DebugPresenter.gameObject.SetActive(Mode == FishFixtureMode.DebugSpheres);
                    if (Mode == FishFixtureMode.DebugSpheres)
                    {
                        var settings = VolumeSettings.Default; settings.Resolution = 4; settings.CellSize = 30;
                        settings.UseCoarseRoutes = false;
                        DebugPresenter.Initialize(VolumeBake.Bake(settings, 0.7f, Array.Empty<VolumeBox>()), View);
                        DebugPresenter.transform.Find("Static volume").gameObject.SetActive(false);
                    }
                }
                if (Renderer != null) Renderer.enabled = Mode == FishFixtureMode.GpuFish;
                Present();
            }
            catch { Release(); throw; }
        }
        private void Update()
        {
            if (!positions.IsCreated) return;
            int steps = Paused ? 0 : clock.Advance(Time.unscaledDeltaTime, Step, 2);
            for (int i = 0; i < steps; i++) StepOnce(false);
            if (steps > 0 && Mode == FishFixtureMode.DebugSpheres && DebugPresenter != null) DebugPresenter.Present(View, null, null, null);
            if (Renderer != null)
            {
                Renderer.Interpolation = Paused ? 1 : clock.Fraction(Step);
                if (OrbitCamera && Renderer.ViewCamera != null)
                {
                    float time = (Tick + Renderer.Interpolation) * Step;
                    var camera = Renderer.ViewCamera.transform;
                    camera.position = new Vector3(Mathf.Sin(time * 0.12f) * 90, 25 + Mathf.Sin(time * 0.08f) * 15, -Mathf.Cos(time * 0.12f) * 90);
                    camera.LookAt(Vector3.zero);
                }
            }
        }
        public void StepOnce(bool presentDebug = true)
        {
            Tick++; Evaluate();
            if (Mode == FishFixtureMode.GpuFish && Renderer != null) Renderer.Capture(new AgentSnapshot(View, Tick, Generation, Step));
            if (presentDebug && Mode == FishFixtureMode.DebugSpheres && DebugPresenter != null) DebugPresenter.Present(View, null, null, null);
        }
        private void Present()
        {
            if (Mode == FishFixtureMode.GpuFish && Renderer != null) Renderer.Capture(new AgentSnapshot(View, Tick, Generation, Step));
            if (Mode == FishFixtureMode.DebugSpheres && DebugPresenter != null) DebugPresenter.Present(View, null, null, null);
        }
        private void Evaluate() => new FixtureJob { Ids = ids, Positions = positions, Velocities = velocities, Goals = goals, Parameters = parameters, Extent = Extent, Time = Tick * Step }.Schedule(ids.Length, 128).Complete();
        [BurstCompile]
        private struct FixtureJob : IJobParallelFor
        {
            public NativeArray<int> Ids;
            public NativeArray<float3> Positions, Velocities, Goals;
            public NativeArray<AgentParameters> Parameters;
            public float3 Extent;
            public float Time;
            public void Execute(int index)
            {
                uint hash = math.hash(new uint2((uint)index, 7));
                var random = new Unity.Mathematics.Random(hash | 1);
                float3 center = random.NextFloat3(-Extent, Extent);
                float phase = random.NextFloat(0, 2 * math.PI), t = Time * 0.4f + phase;
                Positions[index] = center + new float3(3 * math.cos(t), 2 * math.sin(t * 2), 3 * math.sin(t));
                Velocities[index] = new float3(-1.2f * math.sin(t), 1.6f * math.cos(t * 2), 1.2f * math.cos(t));
                Goals[index] = center + new float3(100, 0, 0); Ids[index] = index * 3 + 17;
                Parameters[index] = new AgentParameters { Radius = random.NextFloat(0.45f, 0.85f), MaxSpeed = 3, ArrivalDistance = 0.1f };
            }
        }
        private void OnGUI()
        {
            if (!ShowHud) return;
            GUILayout.BeginArea(new Rect(12, 12, 340, 210), GUI.skin.box);
            GUILayout.Label($"Phase 4 render-only · {Mode} · {AgentCount}");
            GUILayout.Label("Synthetic snapshot: no navigation / ORCA");
            GUILayout.Label($"Tick {Tick} | dropped {DroppedTicks}");
            if (Renderer != null) GUILayout.Label($"Pack {Renderer.Poses.PackMilliseconds:F2} | upload {Renderer.UploadMilliseconds:F2} | submit {Renderer.SubmitMilliseconds:F2} ms");
            if (Renderer != null && Renderer.LastError != null) GUILayout.Label(Renderer.LastError);
            Paused = GUILayout.Toggle(Paused, "Pause"); OrbitCamera = GUILayout.Toggle(OrbitCamera, "Fixed camera orbit");
            if (GUILayout.Button("Reset fixture")) ResetFixture();
            GUILayout.EndArea();
        }
        private void Release()
        {
            if (ids.IsCreated) ids.Dispose(); ids = default;
            if (positions.IsCreated) positions.Dispose(); positions = default;
            if (velocities.IsCreated) velocities.Dispose(); velocities = default;
            if (goals.IsCreated) goals.Dispose(); goals = default;
            if (parameters.IsCreated) parameters.Dispose(); parameters = default;
        }
    }
}
