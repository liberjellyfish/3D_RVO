#ifndef RVO_WATER_OPTICS
#define RVO_WATER_OPTICS
float4 _OceanMin, _OceanMax, _OceanExtinction, _OceanWaterColor;
float _OceanWaterSurfaceHeight, _OceanEnabled;

float WaterDistance(float3 origin, float3 end)
{
    float3 waterMax = _OceanMax.xyz;
    waterMax.y = min(waterMax.y, _OceanWaterSurfaceHeight);
    if (any(waterMax < _OceanMin.xyz)) return 0;
    float3 delta = end - origin;
    float enter = 0, exit = 1;
    [unroll] for (int axis = 0; axis < 3; axis++)
    {
        if (abs(delta[axis]) < 1e-6)
        {
            if (origin[axis] < _OceanMin[axis] || origin[axis] > waterMax[axis]) return 0;
        }
        else
        {
            float a = (_OceanMin[axis] - origin[axis]) / delta[axis];
            float b = (waterMax[axis] - origin[axis]) / delta[axis];
            enter = max(enter, min(a, b)); exit = min(exit, max(a, b));
        }
    }
    return max(0, exit - enter) * length(delta);
}
float3 WaterTransmittance(float distance)
{ return exp(-max(_OceanExtinction.xyz, 0) * max(distance, 0)); }
float3 OceanFog(float3 color, float3 origin, float3 surfacePoint)
{
    float3 transmittance = WaterTransmittance(WaterDistance(origin, surfacePoint));
    return color * transmittance + _OceanWaterColor.xyz * (1 - transmittance);
}
#endif
