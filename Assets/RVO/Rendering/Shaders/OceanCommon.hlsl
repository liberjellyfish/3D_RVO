#ifndef RVO_OCEAN_COMMON
#define RVO_OCEAN_COMMON
#include "CausticPattern.hlsl"
TEXTURE2D(_OceanCausticA); SAMPLER(sampler_OceanCausticA);
TEXTURE2D(_OceanCausticB);
float4 _OceanMin, _OceanMax, _OceanExtinction, _OceanWaterColor;
float4 _OceanCaustic; // scale, strength, interpolation, seconds
float _OceanEnabled, _OceanDirect, _OceanShortLoop;

float SampleOceanPattern(float2 uv)
{
    if (_OceanDirect > 0.5) return CausticPattern(uv,_OceanCaustic.w,false,_OceanShortLoop>0.5,true);
    return lerp(SAMPLE_TEXTURE2D(_OceanCausticA,sampler_OceanCausticA,uv).r,
                SAMPLE_TEXTURE2D(_OceanCausticB,sampler_OceanCausticA,uv).r,_OceanCaustic.z);
}

float WaterDistance(float3 origin, float3 end)
{
    float3 delta = end-origin;
    float enter = 0, exit = 1;
    [unroll] for (int axis=0; axis<3; axis++)
    {
        if (abs(delta[axis]) < 1e-6)
        {
            if (origin[axis] < _OceanMin[axis] || origin[axis] > _OceanMax[axis]) return 0;
        }
        else
        {
            float a = (_OceanMin[axis]-origin[axis])/delta[axis], b = (_OceanMax[axis]-origin[axis])/delta[axis];
            enter = max(enter,min(a,b)); exit = min(exit,max(a,b));
        }
    }
    return max(0,exit-enter) * length(delta);
}
float3 OceanLighting(float3 position, float3 normal)
{
    if (_OceanEnabled < 0.5 || _OceanCaustic.y <= 0 || any(position < _OceanMin.xyz) || any(position > _OceanMax.xyz)) return 1;
    float depth = max(0,_OceanMax.y-position.y);
    float3 lightDirection = normalize(float3(-0.3,0.8,-0.5));
    float2 uv = (position.xz + depth * lightDirection.xz / lightDirection.y) * _OceanCaustic.x;
    float pattern = SampleOceanPattern(uv);
    return 1 + _OceanCaustic.y * pattern * saturate(dot(normal,lightDirection)) * exp(-depth*0.006);
}
float3 OceanFog(float3 color, float3 origin, float3 surfacePoint)
{
    float distance = WaterDistance(origin,surfacePoint);
    float3 transmittance = exp(-_OceanExtinction.xyz * distance);
    return color * transmittance + _OceanWaterColor.xyz * (1-transmittance);
}
#endif
