#ifndef RVO_FISH_DATA
#define RVO_FISH_DATA
struct FishData
{
    float4 positionRadius;
    float4 rotation;
    float4 animation;
    uint4 identity;
};
float3 FishRotate(float4 q, float3 v) { return v + 2 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }
#endif
