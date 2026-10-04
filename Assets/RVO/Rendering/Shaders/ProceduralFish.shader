Shader "RVO/Procedural Fish Indirect"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        ZTest LEqual
        ZWrite On
        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
        #include "UnityIndirect.cginc"
        #include "FishData.hlsl"
        #include "FishAnimation.hlsl"
        #include "OceanCommon.hlsl"
        StructuredBuffer<FishData> _Fish;
        StructuredBuffer<uint> _Visible;
        float _DebugLod;
        struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; uint instanceID : SV_InstanceID; uint vertexID : SV_VertexID; };
        struct Varyings {
            float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 color : TEXCOORD1; float longitudinal : TEXCOORD2;
            float3 positionWS : TEXCOORD3; float3 localPoint : TEXCOORD4; nointerpolation float3 localCamera : TEXCOORD5;
            nointerpolation float4 rotation : TEXCOORD6; nointerpolation float2 animation : TEXCOORD7;
        };
        Varyings Vert(Attributes input)
        {
            InitIndirectDrawArgs(0);
            uint slot = _Visible[GetIndirectInstanceID(input.instanceID)];
            FishData fish = _Fish[slot];
            float3 p = input.positionOS;
            float s = saturate((1 - p.z) / 2.3);
            float3 normal = input.normalOS;
            float4 inverseRotation = float4(-fish.rotation.xyz, fish.rotation.w);
            float3 localCamera = FishRotate(inverseRotation, (_WorldSpaceCameraPos - fish.positionRadius.xyz) / fish.animation.w);
            if (unity_OrthoParams.w > 0.5)
                localCamera = FishRotate(inverseRotation, UNITY_MATRIX_V[2].xyz) * 10000;
            if (_CardMode > 2.5)
            {
                float3 view = normalize(localCamera);
                float3 axis = float3(0,0,1) - view * view.z;
                axis = dot(axis,axis) > 0.001 ? normalize(axis) : normalize(cross(view,float3(0,1,0)));
                p = axis * p.z + normalize(cross(view,axis)) * p.y;
            }
            if (_CardMode < 0.5) DeformFish(p, normal, input.vertexID, fish.animation.x, fish.animation.y);
            Varyings output;
            output.positionWS = fish.positionRadius.xyz + FishRotate(fish.rotation, p * fish.animation.w);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            output.localPoint = p; output.localCamera = localCamera; output.rotation = fish.rotation; output.animation = fish.animation.xy;
            output.normalWS = normalize(FishRotate(fish.rotation, normal));
            float hue = (fish.identity.w & 255u) / 255.0;
            output.color = lerp(float3(0.12, 0.42, 0.66), float3(0.85, 0.49, 0.16), hue);
            if ((fish.identity.z & 1u) != 0) output.color = 0.9;
            if (_DebugLod >= 0)
                output.color = _DebugLod < 0.5 ? float3(1,0.3,0.2) : _DebugLod < 1.5 ? float3(0.2,1,0.3) : _DebugLod < 2.5 ? float3(0.3,0.5,1) : float3(1,0.8,0.2);
            output.longitudinal = s;
            return output;
        }
        float3 SurfaceNormal(Varyings input)
        {
            if (_CardMode > 0.5) return FishRotate(input.rotation, CardNormal(input.localCamera, input.localPoint, input.animation.x, input.animation.y));
            return normalize(input.normalWS);
        }
        half4 Frag(Varyings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
        {
            float3 n = SurfaceNormal(input) * (_CardMode > 0.5 ? 1 : IS_FRONT_VFACE(frontFace, 1, -1));
            float light = 0.32 + 0.68 * saturate(dot(n, normalize(float3(-0.3, 0.8, -0.5))));
            float bands = 0.9 + 0.1 * cos(input.longitudinal * 28);
            return half4(input.color * light * bands * OceanLighting(input.positionWS, n), 1);
        }
        half4 DepthFrag(Varyings input) : SV_Target { SurfaceNormal(input); return 0; }
        half4 NormalFrag(Varyings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
        { return half4(SurfaceNormal(input) * (_CardMode > 0.5 ? 1 : IS_FRONT_VFACE(frontFace, 1, -1)), 0); }
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
