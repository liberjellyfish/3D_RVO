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
            TEXTURE2D_X_FLOAT(_OceanSceneDepth);
            #include "WaterOptics.hlsl"
            float4 _OceanComposite; // fog, directional background, optical horizon distance, debug view
            float4 _OceanHorizonTop, _OceanHorizonBottom;
            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float depth = SAMPLE_TEXTURE2D_X(_OceanSceneDepth, sampler_PointClamp, uv).r;
                #if UNITY_REVERSED_Z
                    bool empty = depth < 1e-6;
                #else
                    bool empty = depth > 1 - 1e-6;
                #endif
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
                float3 direction = normalize(surfacePoint - origin);
                // 空像素的光学远端独立于 Far Clip；背景没有法线、深度或焦散。
                if (empty) surfacePoint = origin + direction * _OceanComposite.z;
                float distance = WaterDistance(origin, surfacePoint);
                float3 transmittance = WaterTransmittance(distance);
                if (_OceanComposite.w > 3.5) return half4(empty ? 0 : length(surfacePoint - origin) / _OceanComposite.z, 0, 0, 1);
                if (_OceanComposite.w > 2.5) return half4(transmittance, 1);
                if (_OceanComposite.w > 1.5) return half4(distance / _OceanComposite.z, 0, 0, 1);
                if (empty && _OceanComposite.y > 0.5 && distance > 0)
                    color = lerp(_OceanHorizonBottom.rgb, _OceanHorizonTop.rgb, smoothstep(-1, 1, direction.y));
                if (_OceanComposite.w > 0.5 || _OceanComposite.x < 0.5) return half4(color, 1);
                return half4(color * transmittance + _OceanWaterColor.rgb * (1 - transmittance), 1);
            }
            ENDHLSL
        }
    }
}
