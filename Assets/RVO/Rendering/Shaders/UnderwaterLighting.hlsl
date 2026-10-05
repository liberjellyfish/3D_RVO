#ifndef RVO_UNDERWATER_LIGHTING
#define RVO_UNDERWATER_LIGHTING
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "OceanCommon.hlsl"
float4 _OceanAmbientSky, _OceanAmbientEquator, _OceanAmbientGround;

float3 UnderwaterSunTransmittance(float3 position, float3 lightDirection)
{
    if (_OceanEnabled < 0.5) return 1;
    float depth = max(0, _OceanWaterSurfaceHeight - position.y);
    // 太阳路径和观察路径各算一次；低角度太阳限制投影，避免除零。
    return WaterTransmittance(depth / max(lightDirection.y, 0.1));
}
float ReceiverCaustic(float3 position, float3 lightDirection)
{
    if (_OceanEnabled < 0.5 || _OceanCaustic.y <= 0 || lightDirection.y <= 0.05 ||
        position.y > _OceanWaterSurfaceHeight || any(position < _OceanMin.xyz) || any(position > _OceanMax.xyz)) return 1;
    float depth = max(0, _OceanWaterSurfaceHeight - position.y);
    float2 uv = (position.xz + depth * lightDirection.xz / max(lightDirection.y, 0.1)) * _OceanCaustic.x;
    return 1 + _OceanCaustic.y * SampleOceanPattern(uv) * exp(-depth * 0.006) * smoothstep(0.05, 0.2, lightDirection.y);
}
float3 ShadeUnderwaterReceiver(float3 albedo, float3 position, float3 normal)
{
    Light sun = GetMainLight(TransformWorldToShadowCoord(position));
    float ndotl = saturate(dot(normal, sun.direction));
    float3 direct = sun.color * sun.distanceAttenuation * sun.shadowAttenuation * ndotl;
    direct *= UnderwaterSunTransmittance(position, sun.direction) * ReceiverCaustic(position, sun.direction);
    // 焦散只调制直射；不再乘 ambient 或最终 fogged color，也不重复乘 NdotL。
    return albedo * (float3(0.22, 0.22, 0.22) + direct);
}

float3 ShadeUnderwaterPbr(float3 albedo, float3 position, float3 normal, float smoothness, float occlusion, float causticGain)
{
    float3 view = GetWorldSpaceNormalizeViewDir(position);
    BRDFData brdf;
    half alpha = 1;
    InitializeBRDFData(albedo, 0, half3(0.04,0.04,0.04), smoothness, alpha, brdf);
    Light sun = GetMainLight(TransformWorldToShadowCoord(position));
    // 共享光路和阴影，焦散只进入直射辐照度；观察雾仍由合成 Pass 唯一负责。
    half3 irradiance = sun.color * sun.distanceAttenuation * sun.shadowAttenuation;
    irradiance *= UnderwaterSunTransmittance(position, sun.direction) * (1 + causticGain * (ReceiverCaustic(position, sun.direction) - 1));
    half3 direct = LightingPhysicallyBased(brdf, irradiance, sun.direction, 1, normal, view);
    // 场景的三色环境光显式共享给 indirect 鱼和普通 Mesh，避免未烘焙 SH 令一类接收面全黑。
    half3 ambient = _OceanEnabled > 0.5
        ? lerp(_OceanAmbientEquator.rgb, normal.y >= 0 ? _OceanAmbientSky.rgb : _OceanAmbientGround.rgb, abs(normal.y))
        : max(SampleSH(normal),0);
    half3 diffuseGI = ambient * brdf.diffuse * occlusion;
    half3 reflection = GlossyEnvironmentReflection(reflect(-view, normal), brdf.perceptualRoughness, occlusion);
    half fresnel = Pow4(1 - saturate(dot(normal, view)));
    return direct + diffuseGI + EnvironmentBRDFSpecular(brdf, fresnel) * reflection;
}
#endif
