using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Rvo.Rendering
{
    /// <summary>Ocean 场景独立质量资产和镜头；退出后恢复原管线，不改共享资产。</summary>
    [DefaultExecutionOrder(90)]
    [RequireComponent(typeof(Camera))]
    public sealed class OceanPresentation : MonoBehaviour
    {
        public UniversalRenderPipelineAsset Pipeline;
        public GpuFishRenderer Fish;
        public Transform DebugProxies;
        private RenderPipelineAsset previousPipeline;
        private Camera view;
        private UniversalAdditionalCameraData cameraData;
        private bool previousPost;
        private AntialiasingMode previousAA;
        private AntialiasingQuality previousQuality;
        private int shot;
        private int followedSlot = -1;
        private uint followedId, followedGeneration;
        private string aaLabel = "SMAA";
        private VolumeCameraControls cameraControls;
        private NavigationVolume navigation;
        private float followDistance;

        private void OnEnable()
        {
            view = GetComponent<Camera>(); cameraData = view.GetUniversalAdditionalCameraData();
            cameraControls = GetComponent<VolumeCameraControls>();
            previousPipeline = QualitySettings.renderPipeline;
            if (Pipeline != null) QualitySettings.renderPipeline = Pipeline;
            previousPost = cameraData.renderPostProcessing; previousAA = cameraData.antialiasing; previousQuality = cameraData.antialiasingQuality;
            cameraData.renderPostProcessing = true;
            cameraData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            cameraData.antialiasingQuality = AntialiasingQuality.High;
        }

        public void SelectShot(int index)
        {
            shot = (index % 3 + 3) % 3;
            Vector3[] eyes = { new Vector3(-42,3,-29), new Vector3(-25,1,-9), new Vector3(-57,24,-53) };
            Vector3[] targets = { new Vector3(0,1,0), new Vector3(3,0,0), new Vector3(2,0,0) };
            view.transform.position = eyes[shot]; view.transform.LookAt(targets[shot]);
            var controls = cameraControls;
            if (controls != null) { controls.StopFollowing(); controls.Pivot = targets[shot]; }
            followedSlot = -1;
            if (shot == 1 && Fish != null && Fish.Poses.Count > 0)
            {
                float closest = float.PositiveInfinity;
                for (int i=0;i<Fish.Poses.Count;i++)
                {
                    var pose=Fish.Poses.Current[i];
                    if ((pose.Identity.z & FishGpuData.Invalid) != 0) continue;
                    float distance=Vector3.SqrMagnitude((Vector3)pose.PositionRadius.xyz-new Vector3(-20,3,0));
                    if (distance < closest) { closest=distance; followedSlot=i; }
                }
                if (followedSlot >= 0)
                {
                    var pose=Fish.Poses.Current[followedSlot]; followedId=pose.Identity.x; followedGeneration=pose.Identity.y;
                    var target=(Vector3)pose.PositionRadius.xyz;
                    view.transform.position=target+new Vector3(-2.8f,0.9f,-2.6f);
                    followDistance=Vector3.Distance(view.transform.position,target);
                    view.transform.LookAt(target);
                    if (controls != null) controls.BeginFollow(target);
                    var source=Fish.GetComponent<VolumeSimulationBootstrap>();
                    if (source != null && source.Profile != null)
                        navigation=source.Profile.BakedVolume.Load(source.Profile.Volume,source.Profile.Scenario.Radius);
                }
            }
            Fish?.InvalidateDisplayHistory();
            cameraData.resetHistory = true;
        }

        private void LateUpdate()
        {
            if (followedSlot < 0 || Fish == null || followedSlot >= Fish.Poses.Count) return;
            var pose=Fish.Poses.Current[followedSlot];
            if (pose.Identity.x != followedId || pose.Identity.y != followedGeneration)
            { followedSlot=-1; cameraControls?.StopFollowing(); return; }
            var before=Fish.Poses.Previous[followedSlot];
            var target=Vector3.Lerp((Vector3)before.PositionRadius.xyz,(Vector3)pose.PositionRadius.xyz,Fish.Interpolation);
            cameraControls?.UpdateFollowTarget(target);
            Vector3 eye=target+(view.transform.position-target).normalized*followDistance;
            if (navigation != null && !navigation.SegmentClear(target,eye))
            {
                // 跟拍只查询既有保守代理，缩短镜头与主体距离，不推动鱼或修改导航。
                float low=0,high=1;
                for(int i=0;i<10;i++)
                {
                    float fraction=(low+high)*0.5f;
                    if(navigation.SegmentClear(target,Vector3.Lerp(target,eye,fraction))) low=fraction; else high=fraction;
                }
                eye=Vector3.Lerp(target,eye,low*0.95f);
            }
            view.transform.position=eye;
            if(Vector3.SqrMagnitude(target-eye)>0.0001f) view.transform.LookAt(target);
        }

        private void OnGUI()
        {
            if (GUI.Button(new Rect(12,Screen.height-44,150,30),"Ocean camera " + (shot+1))) SelectShot(shot+1);
            if (DebugProxies != null && GUI.Button(new Rect(174,Screen.height-44,150,30),"Navigation proxies"))
                DebugProxies.gameObject.SetActive(!DebugProxies.gameObject.activeSelf);
            if (GUI.Button(new Rect(336,Screen.height-44,180,30),"AA: "+aaLabel))
            {
                if (cameraData.antialiasing == AntialiasingMode.SubpixelMorphologicalAntiAliasing) SetAA("None");
                else if (cameraData.antialiasing == AntialiasingMode.None) SetAA("TAA");
                else SetAA("SMAA");
            }
        }

        public void SetAA(string mode)
        {
            bool temporal=mode == "TAA" && Fish != null && Fish.EnableMotionVectors && Fish.ReefAppearance
                && Pipeline != null && Pipeline.msaaSampleCount == 1;
            cameraData.antialiasing=temporal ? AntialiasingMode.TemporalAntiAliasing : mode == "None"
                ? AntialiasingMode.None : AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            aaLabel=temporal ? "TAA (candidate)" : mode == "None" ? "None" : "SMAA";
            cameraData.resetHistory=true; Fish?.InvalidateDisplayHistory();
        }

        private void OnDisable()
        {
            if (QualitySettings.renderPipeline == Pipeline) QualitySettings.renderPipeline = previousPipeline;
            if (cameraData == null) return;
            cameraData.renderPostProcessing = previousPost; cameraData.antialiasing = previousAA; cameraData.antialiasingQuality = previousQuality;
        }
    }
}
