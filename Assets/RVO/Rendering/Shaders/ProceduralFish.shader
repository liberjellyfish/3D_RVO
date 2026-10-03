Shader "RVO/Procedural Fish Indirect"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
        #include "UnityIndirect.cginc"
        #include "FishData.hlsl"
        StructuredBuffer<FishData> _Fish;
        StructuredBuffer<uint> _Visible;
        float _DebugLod;
        struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; uint instanceID : SV_InstanceID; };
        struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 color : TEXCOORD1; float longitudinal : TEXCOORD2; };
        Varyings Vert(Attributes input)
        {
            InitIndirectDrawArgs(0);
            uint slot = _Visible[GetIndirectInstanceID(input.instanceID)];
            FishData fish = _Fish[slot];
            float3 p = input.positionOS;
            float s = saturate((1 - p.z) / 2.3);
            float phase = 7 * s - fish.animation.x;
            float waveSin, waveCos; sincos(phase, waveSin, waveCos);
            float amplitude = fish.animation.y;
            p.x += amplitude * s * s * waveSin;
            // x'=x+d(z) 的逆转置法线，颜色/深度/法线 Pass 共用变形。
            float derivative = -amplitude * (2 * s * waveSin + 7 * s * s * waveCos) / 2.3;
            float3 normal = input.normalOS;
            normal.z -= derivative * normal.x;
            Varyings output;
            output.positionCS = TransformWorldToHClip(fish.positionRadius.xyz + FishRotate(fish.rotation, p * fish.animation.w));
            output.normalWS = normalize(FishRotate(fish.rotation, normal));
            float hue = (fish.identity.w & 255u) / 255.0;
            output.color = lerp(float3(0.12, 0.42, 0.66), float3(0.85, 0.49, 0.16), hue);
            if ((fish.identity.z & 1u) != 0) output.color = 0.9;
            if (_DebugLod >= 0)
                output.color = _DebugLod < 0.5 ? float3(1,0.3,0.2) : _DebugLod < 1.5 ? float3(0.2,1,0.3) : _DebugLod < 2.5 ? float3(0.3,0.5,1) : float3(1,0.8,0.2);
            output.longitudinal = s;
            return output;
        }
        half4 Frag(Varyings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
        {
            float3 n = normalize(input.normalWS) * IS_FRONT_VFACE(frontFace, 1, -1);
            float light = 0.32 + 0.68 * saturate(dot(n, normalize(float3(-0.3, 0.8, -0.5))));
            float bands = 0.9 + 0.1 * cos(input.longitudinal * 28);
            return half4(input.color * light * bands, 1);
        }
        half4 DepthFrag(Varyings input) : SV_Target { return 0; }
        half4 NormalFrag(Varyings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
        { return half4(normalize(input.normalWS) * IS_FRONT_VFACE(frontFace, 1, -1), 0); }
        ENDHLSL
        Pass
        {
            Name "Fish Forward"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
        Pass
        {
            Name "Fish Depth"
            Tags { "LightMode"="DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DepthFrag
            ENDHLSL
        }
        Pass
        {
            Name "Fish Normals"
            Tags { "LightMode"="DepthNormalsOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment NormalFrag
            ENDHLSL
        }
    }
}
