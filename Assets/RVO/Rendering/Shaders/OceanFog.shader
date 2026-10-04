Shader "RVO/Ocean Beer Fog"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "OceanCommon.hlsl"
            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float depth = SampleSceneDepth(uv);
                #if !UNITY_REVERSED_Z
                    depth = lerp(UNITY_NEAR_CLIP_VALUE,1,depth);
                #endif
                float3 surfacePoint = ComputeWorldSpacePosition(uv,depth,UNITY_MATRIX_I_VP);
                float3 origin = _WorldSpaceCameraPos;
                if (unity_OrthoParams.w > 0.5)
                {
                    #if UNITY_REVERSED_Z
                        origin = ComputeWorldSpacePosition(uv,1,UNITY_MATRIX_I_VP);
                    #else
                        origin = ComputeWorldSpacePosition(uv,UNITY_NEAR_CLIP_VALUE,UNITY_MATRIX_I_VP);
                    #endif
                }
                float3 color = SAMPLE_TEXTURE2D_X(_BlitTexture,sampler_LinearClamp,uv).rgb;
                return half4(OceanFog(color,origin,surfacePoint),1);
            }
            ENDHLSL
        }
    }
}
