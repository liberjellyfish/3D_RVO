using UnityEngine;
using UnityEngine.Rendering;

namespace Rvo.Rendering
{
    /// <summary>Small fixed sample of actual remaining routes; never computes routes itself.</summary>
    public sealed class ReefPathOverlay : MonoBehaviour
    {
        public VolumeSimulationBootstrap Source;
        public bool ShowRoutes = true;
        private readonly LineRenderer[] lines = new LineRenderer[18];
        private readonly Material[] materials = new Material[18];
        private long observedTick=-1;
        private uint generation;
        private void LateUpdate()
        {
            if(Source==null || Source.World==null || !ShowRoutes)
            { foreach(var line in lines) if(line!=null) line.enabled=false; return; }
            var world=Source.World;
            if(generation==Source.Generation && world.Tick>=observedTick && world.Tick-observedTick<3) return;
            observedTick=world.Tick; generation=Source.Generation;
            for(int sample=0;sample<lines.Length;sample++)
            {
                int agent=sample*113%world.Settings.AgentCount;
                var path=Source.Navigation.Path(agent);
                if(path.Status!=VolumePathStatus.Ready || path.Cursor>=path.Count)
                { if(lines[sample]!=null) lines[sample].enabled=false; continue; }
                if(lines[sample]==null)
                {
                    var go=new GameObject("Route sample "+sample); go.transform.SetParent(transform,false);
                    var line=go.AddComponent<LineRenderer>(); lines[sample]=line;
                    materials[sample]=new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                    materials[sample].SetColor("_BaseColor",Color.HSVToRGB(sample/(float)lines.Length,0.65f,1));
                    line.sharedMaterial=materials[sample]; line.useWorldSpace=true; line.widthMultiplier=0.18f;
                    line.shadowCastingMode=ShadowCastingMode.Off; line.receiveShadows=false;
                }
                var display=lines[sample]; display.enabled=true;
                display.positionCount=1+path.Count-path.Cursor;
                display.SetPosition(0,world.Snapshot.Positions[agent]);
                for(int p=path.Cursor;p<path.Count;p++) display.SetPosition(1+p-path.Cursor,Source.Navigation.Waypoint(agent,p));
            }
        }
        private void OnGUI()
        {
            if(GUI.Button(new Rect(Screen.width-218,Screen.height-44,206,30),ShowRoutes ? "Routes: 18 samples (hide)" : "Show 18 sampled routes"))
            { ShowRoutes=!ShowRoutes; observedTick=-1; }
        }
        private void OnDisable()
        {
            for(int i=0;i<lines.Length;i++)
            {
                if(lines[i]!=null) Destroy(lines[i].gameObject);
                if(materials[i]!=null) Destroy(materials[i]);
                lines[i]=null; materials[i]=null;
            }
            observedTick=-1;
        }
    }
}
