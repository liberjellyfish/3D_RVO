#ifndef RVO_OCEAN_COMMON
#define RVO_OCEAN_COMMON
#include "WaterOptics.hlsl"
#include "CausticPattern.hlsl"
TEXTURE2D(_OceanCausticA); SAMPLER(sampler_OceanCausticA);
TEXTURE2D(_OceanCausticB);
float4 _OceanCaustic; // scale, strength, interpolation, seconds
float _OceanDirect, _OceanShortLoop;

float SampleOceanPattern(float2 uv)
{
    if (_OceanDirect > 0.5) return CausticPattern(uv,_OceanCaustic.w,false,_OceanShortLoop>0.5,true);
    return lerp(SAMPLE_TEXTURE2D(_OceanCausticA,sampler_OceanCausticA,uv).r,
                SAMPLE_TEXTURE2D(_OceanCausticB,sampler_OceanCausticA,uv).r,_OceanCaustic.z);
}

#endif
