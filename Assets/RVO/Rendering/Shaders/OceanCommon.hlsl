#ifndef RVO_OCEAN_COMMON
#define RVO_OCEAN_COMMON
#include "WaterOptics.hlsl"
#include "CausticPattern.hlsl"
TEXTURE2D(_OceanCausticA); SAMPLER(sampler_OceanCausticA);
TEXTURE2D(_OceanCausticB);
float4 _OceanCaustic; // scale, strength, interpolation, seconds
float _OceanDirect, _OceanShortLoop, _OceanStochastic;

float SampleOceanTile(float2 uv, float2 dx, float2 dy)
{
    if (_OceanDirect > 0.5) return CausticPattern(uv,_OceanCaustic.w,false,_OceanShortLoop>0.5,true);
    return lerp(SAMPLE_TEXTURE2D_GRAD(_OceanCausticA,sampler_OceanCausticA,uv,dx,dy).r,
                SAMPLE_TEXTURE2D_GRAD(_OceanCausticB,sampler_OceanCausticA,uv,dx,dy).r,_OceanCaustic.z);
}

float3 OceanTileHash(float2 vertex)
{
    // Integer hash: stable for negative world cells too, independent of time/camera/receiver.
    uint2 p = asuint(int2(vertex));
    uint h = p.x * 1597334677u ^ p.y * 3812015801u;
    h ^= h >> 16; h *= 2246822519u; h ^= h >> 13;
    uint3 v = uint3(h, h * 3266489917u + 374761393u, h * 668265263u + 2246822519u);
    v ^= v >> 15;
    return float3(v & 0x00ffffffu) / 16777216.0;
}

float SampleOceanPatch(float2 uv, float2 vertex, float2 dx, float2 dy)
{
    float3 random = OceanTileHash(vertex);
    float angle = random.z * 6.28318530718;
    float s, c; sincos(angle,s,c);
    float2x2 rotation = float2x2(c,-s,s,c);
    // The same affine transform and gradients sample both animation endpoints.
    return SampleOceanTile(mul(rotation,uv) + random.xy,
                           mul(rotation,dx),mul(rotation,dy));
}

float SampleOceanPattern(float2 uv)
{
    float2 dx = ddx(uv), dy = ddy(uv);
    if (_OceanStochastic < 0.5) return SampleOceanTile(uv,dx,dy);
    // Equilateral lattice; each vertex owns a fixed patch on all its incident triangles.
    float2 skew = float2(uv.x - uv.y * 0.57735026919, uv.y * 1.15470053838);
    float2 cell = floor(skew), f = frac(skew);
    float2 a, b, c;
    float3 weights;
    if (f.x + f.y < 1)
    {
        a=cell; b=cell+float2(1,0); c=cell+float2(0,1);
        weights=float3(1-f.x-f.y,f.x,f.y);
    }
    else
    {
        a=cell+1; b=cell+float2(0,1); c=cell+float2(1,0);
        weights=float3(f.x+f.y-1,1-f.x,1-f.y);
    }
    // Sharpen the partition of unity to retain bright filaments without boosting mean
    // energy or clamping negative variance corrections. This is not histogram synthesis.
    weights=weights*weights*weights;
    weights/=dot(weights,1);
    return dot(weights,float3(SampleOceanPatch(uv,a,dx,dy),
                              SampleOceanPatch(uv,b,dx,dy),SampleOceanPatch(uv,c,dx,dy)));
}

#endif
