# 水体表现修正：表面材质先于蓝色雾

> 历史专题记录：下文的默认参数、实现状态和测试数字对应文中日期及当时版本。当前系统结构与运行配置见 [ARCHITECTURE.md](ARCHITECTURE.md)，最新主场景证据见 [2048 多通路报告](VERIFICATION_REEF_NETWORK2048.md)。

更新：2026-10-04。用户明确：提供的 Hoskins / joltz0r 程序图案应呈现在水中环境的外表材质上，目标是参考图中的流动明暗纹理。上一版将它主要作为弱光照乘数，叠加强雾后变成近乎均匀的蓝色背景，视觉目标不正确。本次先纠正表现，不继续追加粒子或其它效果。

## 采用的结构

1. **表面图案**：`OceanSurface.shader` 使用原式 `clamp(base + intensity, 0, 1)` 构造材质颜色。原始强度公式、动画时间和稳定代数移植保留；强度与底色可调。原式是显示颜色组合，在 Linear 项目中先重建 sRGB 组合，再转换一次到 linear 参与光照，避免重复 gamma 转换。
2. **映射**：普通 UV 直接对应用户代码的 0–1 UV；世界三向投影按法线四次方权重混合 YZ/XZ/XY，使侧壁、顶面和曲面都有图案，而非全部退化成 XZ 条纹。三向投影跨锐角不保证严格无缝，曲面混合也会使亮纹变软，这些是当前近似。
3. **计算来源**：独立材质默认 DirectReference，拖到 Mesh 上即可看到动画。Ocean 场景使用 SharedOcean，继续复用两张 R16F 纹理及时间插值。三向映射的共享路径最多六次纹理采样，不在每个表面像素重复运行三份五轮三角迭代。
4. **形体与深度**：表面颜色之后施加主方向光、环境亮度和主光阴影；新增静态环境 ShadowCaster。颜色、DepthOnly、DepthNormals、ShadowCaster 均遵守真实几何深度。保持 `ZTest LEqual / ZWrite On`，无透明叠层。鱼的阴影策略保持关闭，不把静态物体阴影当作鱼 shadow proxy 已完成。
5. **雾**：仍是一次独立的 Beer 合成；默认消光从 `(0.022,0.009,0.006)` 降至 `(0.004,0.002,0.0015)`，水色更深。Live 按其尺度取四分之一消光系数。灰度对照场景完全关闭雾，防止靠雾掩盖表面错误。

鱼上的 `OceanLighting` 保留独立、较轻的照明调制；鱼的花纹/原色与外部水体材质不混为一套全屏染色。

## 场景与使用

- `Assets/RVO/Demo/Phase4_SurfaceStudy.unity`：灰度内箱、三球、主光与真实投影。用于与用户参考图对照，无导航或仿真；默认 Shared512/30 Hz、Fog 关闭。材质为 `Phase4_SurfaceStudy.mat`。
- `Phase4_Ocean.unity` / `Phase4_OceanLive.unity`：共享 `Phase4_OceanSurface.mat` 作为背景/障碍表面材质，保留鱼群与独立水下消光。
- `Tools > RVO > Create Phase 4 Surface Material Study`：仅创建缺失对照场景。
- `Tools > RVO > Apply Surface Pattern Look to Ocean Demos`：显式更新两个 Ocean 演示的表面材质、弱雾设置和主方向光，不重建仿真或导航数据。该命令是应用演示预设，已有自定义材质值会被更新。

材质参数：

| 参数 | 作用 |
|---|---|
| Pattern base / highlight | 蓝绿原式底色或灰度参考色；不影响几何与仿真 |
| Pattern gain | 亮纹强度；原式对照用 1，演示用 1.8 |
| Mapping | UV 或 WorldTriplanar |
| UV tiling / world tiles per unit | UV 的重复次数或每世界单位的 tile 数 |
| Pattern source | 独立 DirectReference 或相机环境 SharedOcean |
| Shape lighting | 0 为原式颜色对照，1 为完全应用当前形体光照 |

相机 `OceanEnvironment.BackgroundMaterial` 提供背景材质模板；运行时只为 Cull Front 内壁创建自有副本，禁用时释放。常规物体共享原材质，Cull Back；不用全局材质注册表。修改模板后重新启用环境/进入 Play 即可重建内壁副本。

## 实际验证

D3D11 完整 PlayMode **10/10** 通过，包含新增材质测试以及上一阶段鱼群/水体/仿真回归。[测试结果](Verification/Phase4Surface/PlayMode.xml)。新增测试显式关闭雾，检查表面亮度分布、随时间变化、共享纹理与逐像素原函数的图像差异，并分别拍摄顶面和侧壁。

D3D12 完整 PlayMode 同样 **10/10** 通过：[D3D12 结果](Verification/Phase4Surface/PlayMode-D3D12.xml)。当前对照截图及 metrics.json 来自最后一次 D3D12 运行；其共享/直接误差与 D3D11 相同，时间差指标仅有约 6×10⁻⁸ 的差别。

实际设备截图：

- [灰度共享纹理](Verification/Phase4Surface/shared-gray.png)、[灰度直接计算](Verification/Phase4Surface/direct-gray.png)、[另一时刻](Verification/Phase4Surface/shared-gray-later.png)。
- [蓝绿材质](Verification/Phase4Surface/shared-ocean-color.png)、[顶面](Verification/Phase4Surface/ceiling.png)、[侧壁](Verification/Phase4Surface/side-wall.png)。
- [图像统计](Verification/Phase4Surface/metrics.json)：仅用于功能与本镜头的视觉一致性检查，不是全方向质量达标或 GPU 性能结论。

D3D11 固定镜头的灰度 P90–P10 为 **0.3765**；两个时刻的平均绝对差为 **0.1751**；Shared512 与 DirectReference 的平均绝对差为 **0.00833**（0–1 显示像素值）。这说明该镜头下图案明显、会流动且共享路径接近直接路径，不代表曲面/接缝已全方向验收。

修正后的 Windows 非 Development Player 构建成功，1 万鱼的运行冒烟和截图完成：[鱼群与表面纹理](Verification/Phase4Surface/Ocean-10000/frame.png)、[实际材质/消光参数](Verification/Phase4Surface/Ocean-10000/environment.json)、[600 帧原始数据](Verification/Phase4Surface/Ocean-10000/frames.csv)。有效 GPU timestamp 仍未取得，此运行不作性能达标依据。

上一版 [18 次短测](VERIFICATION_P4_CONTINUATION.md) 属于 **2026-10-03 的旧表面/强雾实现**。本次材质增加了三向采样和静态阴影，不能沿用其数据宣称新画面的性能。GPU 时间戳仍需单独验证；本次首先验证用户要求的表面视觉目标。

## 参考依据

图案依据用户直接提供的源码与截图。三向投影依据 [Unity 官方 Triplanar 说明](https://docs.unity.cn/Packages/com.unity.shadergraph%4012.1/manual/Triplanar-Node) 及当前项目本地 URP/ShaderGraph 包；颜色处理依据 [Unity 6 色彩空间](https://docs.unity.com/en-us/engine/6000.0/manual/materials-and-shaders/graphics-color/color-spaces/color-spaces) 和 [RenderTextureReadWrite](https://docs.unity.com/zh-cn/engine/6000.0/script-reference/unityengine/rendertexturereadwrite)。这些资料解释实现机制，不替代本机性能测试。
