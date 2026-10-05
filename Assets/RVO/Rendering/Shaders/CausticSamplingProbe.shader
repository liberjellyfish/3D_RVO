Shader "Hidden/RVO/Caustic Sampling Probe"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "OceanCommon.hlsl"
            float4 _ProbeDomain;
            struct A { float4 p:POSITION; float2 uv:TEXCOORD0; };
            struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };
            V Vert(A i) { V o; o.p=TransformObjectToHClip(i.p.xyz); o.uv=i.uv; return o; }
            float4 Frag(V i):SV_Target
            {
                float value=SampleOceanPattern(i.uv*_ProbeDomain.zw+_ProbeDomain.xy);
                return float4(value,value,value,1);
            }
            ENDHLSL
        }
    }
}
