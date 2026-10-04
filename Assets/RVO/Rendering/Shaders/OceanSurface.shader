Shader "RVO/Ocean Surface"
{
    Properties
    {
        _BaseColor("Pattern base (sRGB)",Color)=(0,0.35,0.5,1)
        _PatternColor("Pattern highlight (sRGB)",Color)=(1,1,1,1)
        _PatternGain("Pattern gain",Range(0,4))=1
        _PatternScale("UV tiling / world tiles per unit",Float)=1
        [Enum(UV,0,WorldTriplanar,1)] _PatternMapping("Mapping",Float)=0
        [Enum(SharedOcean,0,DirectReference,1)] _PatternSource("Pattern source",Float)=1
        _LightingWeight("Shape lighting",Range(0,1))=0.75
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull",Float)=2
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Cull [_Cull] ZTest LEqual ZWrite On
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "OceanCommon.hlsl"
        CBUFFER_START(UnityPerMaterial)
        float4 _BaseColor, _PatternColor;
        float _Cull, _PatternGain, _PatternScale, _PatternMapping, _PatternSource, _LightingWeight;
        CBUFFER_END
        struct A { float4 positionOS:POSITION; float3 normalOS:NORMAL; float2 uv:TEXCOORD0; };
        struct V { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 normalWS:TEXCOORD1; float2 uv:TEXCOORD2; };
        V Vert(A input) { V o; o.positionWS=TransformObjectToWorld(input.positionOS.xyz); o.positionCS=TransformWorldToHClip(o.positionWS); o.normalWS=TransformObjectToWorldNormal(input.normalOS); o.uv=input.uv; return o; }
        float3 Normal(V i) { return normalize(i.normalWS) * (_Cull > 0.5 && _Cull < 1.5 ? -1 : 1); }
        float SurfacePattern(float2 uv)
        {
            // 直接模式可独立作为普通材质使用；共享模式由相机环境提供低清动态纹理。
            if (_PatternSource > 0.5) return CausticPattern(uv,_OceanEnabled > 0.5 ? _OceanCaustic.w : _Time.y,false,false,false);
            return _OceanEnabled > 0.5 && _OceanCaustic.y > 0 ? SampleOceanPattern(uv) : 0;
        }
        half4 Frag(V i):SV_Target
        {
            float3 n=Normal(i);
            float pattern;
            if (_PatternMapping < 0.5) pattern=SurfacePattern(i.uv*_PatternScale);
            else
            {
                // 侧壁不能共用 XZ 投影；按法线混合三个平面，曲面也保持世界纹理尺度。
                float3 weights=n*n; weights*=weights; weights/=weights.x+weights.y+weights.z;
                float3 p=i.positionWS*_PatternScale;
                pattern=SurfacePattern(p.zy)*weights.x+SurfacePattern(p.xz)*weights.y+SurfacePattern(p.xy)*weights.z;
            }
            float3 baseColor=_BaseColor.rgb, highlight=_PatternColor.rgb;
            #ifndef UNITY_COLORSPACE_GAMMA
                baseColor=LinearToSRGB(baseColor); highlight=LinearToSRGB(highlight);
            #endif
            // 忠实保留原式 clamp(colour + base, 0, 1)，然后只做一次 sRGB → linear。
            float3 color=saturate(baseColor+pattern*_PatternGain*highlight);
            #ifndef UNITY_COLORSPACE_GAMMA
                color=SRGBToLinear(color);
            #endif
            Light sun=GetMainLight(TransformWorldToShadowCoord(i.positionWS));
            float3 lighting=0.18+0.82*saturate(dot(n,sun.direction))*sun.color*sun.shadowAttenuation;
            return half4(color*lerp(float3(1,1,1),lighting,_LightingWeight),1);
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
