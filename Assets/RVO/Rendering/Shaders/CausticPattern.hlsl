#ifndef RVO_CAUSTIC_PATTERN
#define RVO_CAUSTIC_PATTERN
// 用户提供的 David Hoskins / joltz0r turbulence 参考；保留署名，许可状态见文档。
float CausticRaw(float2 uv, float seconds, bool referenceForm, bool shortLoop)
{
    float2 p = frac(uv) * 6.28318530718 - 250;
    float2 i = p;
    float c = 1;
    [unroll] for (int n=0; n<5; n++)
    {
        float harmonic = n == 0 ? -3 : n == 1 ? -2 : n == 2 ? -1 : n == 3 ? 1 : 2;
        float t = shortLoop ? seconds * (6.28318530718 / 12) * harmonic + 23 * (1 - 3.5/(n+1))
                            : (seconds * 0.5 + 23) * (1 - 3.5/(n+1));
        i = p + float2(cos(t-i.x)+sin(t+i.y), sin(t-i.y)+cos(t+i.x));
        float a = sin(i.x+t), b = cos(i.y+t);
        if (referenceForm) c += 1 / length(float2(p.x/(a/0.005),p.y/(b/0.005)));
        else c += abs(a*b) / max(0.005 * sqrt(p.x*p.x*b*b+p.y*p.y*a*a), 1e-12);
    }
    c = 1.17 - pow(c/5,1.4);
    c *= c; c *= c; c *= c;
    return min(c, 16);
}
float CausticPattern(float2 uv, float seconds, bool referenceForm, bool shortLoop, bool repairSeam)
{
    uv = frac(uv);
    // 仅在 tile 边缘混合相对两端。原公式的线性 p 项并非严格周期，不能声称 frac 自然无缝。
    float2 blend = repairSeam ? smoothstep(0.90,1,uv) : 0;
    float2 clampedUv = min(uv,0.999999);
    float a = CausticRaw(clampedUv,seconds,referenceForm,shortLoop);
    float b = blend.x > 0 ? CausticRaw(float2(0,clampedUv.y),seconds,referenceForm,shortLoop) : a;
    float c = blend.y > 0 ? CausticRaw(float2(clampedUv.x,0),seconds,referenceForm,shortLoop) : a;
    float d = blend.x > 0 && blend.y > 0 ? CausticRaw(0,seconds,referenceForm,shortLoop) : a;
    return lerp(lerp(a,b,blend.x),lerp(c,d,blend.x),blend.y);
}
#endif
