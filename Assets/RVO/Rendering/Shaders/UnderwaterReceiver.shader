Shader "RVO/Underwater Receiver"
{
    Properties
    {
        _BaseColor("Receiver albedo",Color)=(0.18,0.24,0.23,1)
        _Smoothness("Smoothness",Range(0,1))=0.2
        _ReefDetail("Reef material variation",Range(0,1))=0
        _CausticGain("Water turbulence on lit surface",Range(0,4))=1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull",Float)=2
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Cull [_Cull] ZTest LEqual ZWrite On
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "UnderwaterLighting.hlsl"
        CBUFFER_START(UnityPerMaterial)
        float4 _BaseColor;
        float _Cull, _Smoothness, _ReefDetail, _CausticGain;
        CBUFFER_END
        struct A { float4 positionOS:POSITION; float3 normalOS:NORMAL; float2 uv:TEXCOORD0; };
        struct V { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 normalWS:TEXCOORD1; float2 uv:TEXCOORD2; };
        V Vert(A input) { V o; o.positionWS=TransformObjectToWorld(input.positionOS.xyz); o.positionCS=TransformWorldToHClip(o.positionWS); o.normalWS=TransformObjectToWorldNormal(input.normalOS); o.uv=input.uv; return o; }
        float3 Normal(V i) { return normalize(i.normalWS) * (_Cull > 0.5 && _Cull < 1.5 ? -1 : 1); }
        half4 Frag(V i):SV_Target
        {
            float3 n = Normal(i);
            // 米级低频层理随世界坐标固定，避免远处岩石上的高频纹理闪烁。
            float strata = 0.9 + 0.1 * sin(i.positionWS.y * 1.7 + sin(i.positionWS.x * 0.23) + sin(i.positionWS.z * 0.31));
            float3 albedo = _BaseColor.rgb * lerp(1, strata, _ReefDetail);
            albedo = lerp(albedo, albedo * float3(0.7,0.85,0.58), _ReefDetail * saturate(n.y) * 0.32);
            float3 color = _ReefDetail > 0 ? ShadeUnderwaterPbr(albedo, i.positionWS, n, _Smoothness, 1, _CausticGain)
                : ShadeUnderwaterReceiver(albedo, i.positionWS, n);
            return half4(color, 1);
        }
        half4 Depth(V i):SV_Target { return 0; }
        half4 Normals(V i):SV_Target { return half4(Normal(i),0); }
        ENDHLSL
        Pass { Name "Ocean Surface" Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            ENDHLSL
        }
        Pass { Name "Ocean Depth" Tags { "LightMode"="DepthOnly" } ColorMask R
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Depth
            ENDHLSL
        }
        Pass { Name "Ocean Normals" Tags { "LightMode"="DepthNormalsOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Normals
            ENDHLSL
        }
        Pass
        {
            Name "Ocean Shadow" Tags { "LightMode"="ShadowCaster" } ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment Depth
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            float3 _LightDirection, _LightPosition;
            V ShadowVert(A input)
            {
                V o=Vert(input);
                float3 direction=_LightDirection;
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    direction=normalize(_LightPosition-o.positionWS);
                #endif
                o.positionCS=TransformWorldToHClip(ApplyShadowBias(o.positionWS,Normal(o),direction));
                #if UNITY_REVERSED_Z
                    o.positionCS.z=min(o.positionCS.z,UNITY_NEAR_CLIP_VALUE*o.positionCS.w);
                #else
                    o.positionCS.z=max(o.positionCS.z,UNITY_NEAR_CLIP_VALUE*o.positionCS.w);
                #endif
                return o;
            }
            ENDHLSL
        }
    }
}

