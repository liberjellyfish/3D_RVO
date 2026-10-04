Shader "RVO/Procedural Fish Indirect"
{
    Properties
    {
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull",Float)=0
        [HideInInspector] _ReefFish("Reef appearance",Float)=0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Cull [_Cull]
        ZTest LEqual
        ZWrite On
        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
        #include "UnityIndirect.cginc"
        #include "FishData.hlsl"
        #include "FishAnimation.hlsl"
        #include "UnderwaterLighting.hlsl"
        StructuredBuffer<FishData> _Fish;
        StructuredBuffer<FishData> _PreviousDisplay;
        StructuredBuffer<uint> _Visible;
        float _DebugLod, _DebugAppearance, _ReefFish, _HasDisplayHistory;
        struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; float4 color : COLOR; uint instanceID : SV_InstanceID; uint vertexID : SV_VertexID; };
        struct Varyings {
            float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 color : TEXCOORD1; float longitudinal : TEXCOORD2;
            float3 positionWS : TEXCOORD3; float3 localPoint : TEXCOORD4; nointerpolation float3 localCamera : TEXCOORD5;
            nointerpolation float4 rotation : TEXCOORD6; nointerpolation float2 animation : TEXCOORD7;
            float smoothness:TEXCOORD8;
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
            output.color = lerp(float3(0.26, 0.42, 0.46), float3(0.42, 0.53, 0.5), hue);
            if (_ReefFish > 0.5) output.color = input.color.rgb * lerp(0.88,1.12,hue);
            if (_DebugAppearance > 0.5)
            {
                output.color = lerp(float3(0.12, 0.42, 0.66), float3(0.85, 0.49, 0.16), hue);
                if ((fish.identity.z & 1u) != 0) output.color = 0.9;
            }
            if (_DebugLod >= 0)
                output.color = _DebugLod < 0.5 ? float3(1,0.3,0.2) : _DebugLod < 1.5 ? float3(0.2,1,0.3) : _DebugLod < 2.5 ? float3(0.3,0.5,1) : float3(1,0.8,0.2);
            output.longitudinal = s;
            output.smoothness = lerp(0.16,0.48,input.color.a);
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
            float bands = _ReefFish > 0.5 ? 1 : 0.9 + 0.1 * cos(input.longitudinal * 28);
            // 鳃盖窄带随屏幕导数展宽，缩小时自动淡出。
            float gill = 1 - smoothstep(0.015,0.035 + fwidth(input.localPoint.z),abs(input.localPoint.z-0.49));
            float3 albedo = input.color * bands * (1 - _ReefFish * gill * 0.3);
            return half4(_ReefFish > 0.5 ? ShadeUnderwaterPbr(albedo,input.positionWS,n,input.smoothness,1)
                : ShadeUnderwaterReceiver(albedo, input.positionWS, n), 1);
        }
        half4 DepthFrag(Varyings input) : SV_Target { SurfaceNormal(input); return 0; }
        half4 NormalFrag(Varyings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
        { return half4(SurfaceNormal(input) * (_CardMode > 0.5 ? 1 : IS_FRONT_VFACE(frontFace, 1, -1)), 0); }

        struct MotionVaryings
        {
            float4 positionCS:SV_POSITION;
            float4 currentCS:TEXCOORD0;
            float4 previousCS:TEXCOORD1;
        };
        MotionVaryings MotionVert(Attributes input)
        {
            InitIndirectDrawArgs(0);
            uint slot = _Visible[GetIndirectInstanceID(input.instanceID)];
            FishData fish = _Fish[slot], previous = _PreviousDisplay[slot];
            float3 p=input.positionOS, n=input.normalOS, oldP=p, oldN=n;
            DeformFish(p,n,input.vertexID,fish.animation.x,fish.animation.y);
            DeformFish(oldP,oldN,input.vertexID,previous.animation.x,previous.animation.y);
            float3 world=fish.positionRadius.xyz+FishRotate(fish.rotation,p*fish.animation.w);
            float3 oldWorld=previous.positionRadius.xyz+FishRotate(previous.rotation,oldP*previous.animation.w);
            MotionVaryings o;
            o.positionCS=TransformWorldToHClip(world);
            o.currentCS=mul(_NonJitteredViewProjMatrix,float4(world,1));
            o.previousCS=_HasDisplayHistory > 0.5 ? mul(_PrevViewProjMatrix,float4(oldWorld,1)) : o.currentCS;
            return o;
        }
        half4 MotionFrag(MotionVaryings i):SV_Target
        {
            // 与 URP 的 UV 速度约定一致，indirect draw 不依赖 Renderer 的 MotionVectorsParams。
            float2 velocity=(i.currentCS.xy/i.currentCS.w-i.previousCS.xy/i.previousCS.w)*0.5;
            #if UNITY_UV_STARTS_AT_TOP
                velocity.y=-velocity.y;
            #endif
            return half4(velocity,0,0);
        }
        ENDHLSL
        Pass
        {
            Name "Fish Forward"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
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
        Pass
        {
            Name "Fish Motion" Tags { "LightMode"="MotionVectors" } ColorMask RG ZWrite Off
            HLSLPROGRAM
            #pragma vertex MotionVert
            #pragma fragment MotionFrag
            ENDHLSL
        }
    }
}
