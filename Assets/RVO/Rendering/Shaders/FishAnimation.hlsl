// 所有几何 Pass 共用同一相位、位移和法线，不在 Depth 中退回静态网格。
Texture2D<float4> _AnimationTexture;
float _AnimationMode, _CardMode;
float4 FishAnimationSample(uint item, float phase)
{
    float frame = frac(phase / 6.28318530718) * 64;
    uint a = (uint)frame, b = (a + 1) % 64;
    return lerp(_AnimationTexture.Load(int3(item, a, 0)), _AnimationTexture.Load(int3(item, b, 0)), frac(frame));
}
void DeformFish(inout float3 p, inout float3 n, uint vertex, float phase, float amplitude)
{
    float s = saturate((1 - p.z) / 2.3), displacement, derivative;
    if (_AnimationMode < 0.5)
    {
        float waveSin, waveCos; sincos(7 * s - phase, waveSin, waveCos);
        displacement = s * s * waveSin;
        derivative = -(2 * s * waveSin + 7 * s * s * waveCos) / 2.3;
    }
    else if (_AnimationMode < 1.5)
    {
        float2 wave = FishAnimationSample(vertex, phase).xy;
        displacement = wave.x; derivative = wave.y;
    }
    else
    {
        float bone = s * 23;
        uint a = min((uint)bone, 22u), b = a + 1;
        float w = bone - a;
        float4 row = lerp(FishAnimationSample(a * 3, phase), FishAnimationSample(b * 3, phase), w);
        float4 rowA = FishAnimationSample(a * 3, phase), rowB = FishAnimationSample(b * 3, phase);
        displacement = dot(row, float4(p, 1)) - p.x;
        // 连续蒙皮权重也依赖 Z；法线须包括权重梯度，不能只混合两根骨骼的法线。
        derivative = row.z - (23 / 2.3) * dot(rowB - rowA, float4(p, 1));
    }
    p.x += amplitude * displacement; n.z -= amplitude * derivative * n.x;
}

// 小尺寸卡片的解析体积轮廓，支持完整视向；平面深度只适合远景。
float FishEllipsoid(float3 origin, float3 ray, float3 center, float3 radii, out float3 normal)
{
    float3 o = (origin - center) / radii, d = ray / radii;
    float a = dot(d,d), b = dot(o,d), c = dot(o,o)-1, discriminant = b*b-a*c;
    float t = (-b - sqrt(max(discriminant,0))) / a;
    normal = normalize((origin + t * ray - center) / (radii * radii));
    return discriminant >= 0 && t >= 0 ? t : 1e10;
}
float3 CardNormal(float3 origin, float3 surfacePoint, float phase, float amplitude)
{
    float3 ray = normalize(surfacePoint - origin), bodyN, tailN;
    float body = FishEllipsoid(origin, ray, float3(0,0,0.04), float3(0.28,0.43,0.94), bodyN);
    float tail = FishEllipsoid(origin, ray, float3(amplitude * sin(7-phase),0,-1.04), float3(0.045,0.45,0.25), tailN);
    clip(1e9 - min(body,tail));
    return body < tail ? bodyN : tailN;
}
