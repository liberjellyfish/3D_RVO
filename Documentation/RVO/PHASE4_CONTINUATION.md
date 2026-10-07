# Phase 4.3 起：动画表示、水下环境与验证

> 历史专题记录：下文的默认参数、实现状态和测试数字对应文中日期及当时版本。当前系统结构与运行配置见 [ARCHITECTURE.md](ARCHITECTURE.md)，最新主场景证据见 [2048 多通路报告](VERIFICATION_REEF_NETWORK2048.md)。

更新：2026-10-03。基于 Unity 6000.0.63f1 / URP 17.0.4。前置实现及历史测试保留在 [P4.0–4.2](VERIFICATION_P4_FOUNDATION.md)。本文件区分可运行功能、实验候选和完整验收，不能将代码交付解释为整个 P4.3–4.7 的质量/性能门槛已通过。

**2026-10-04 用户纠正视觉目标：表面材质应清晰呈现流动纹理。** 已按 [表面表现修正](PHASE4_SURFACE_CORRECTION.md) 更新 Ocean 材质和默认弱雾，并增加灰度内箱/球体对照。下文的初版性能与视觉记录不代表修正后的最终外观。

## 实施顺序与决策

1. 在原快照边界上扩展 renderer，自有动画纹理按表示变化重建；不修改导航、ORCA、碰撞半径或提交时序。
2. 用同一体型、轴向、原点和连续相位做近景动画隔离对照。无外部 DCC 动画资产，因此先采用程序化 2056 顶点鱼；不据此替代真实 VAT/Bone 资产选型。
3. 远景 3 quad / 3 triangle / 单 billboard 作为显式实验。发布默认保留低模 Mesh；尚未证明卡片在等画质下稳定节省 10% GPU 时间。
4. 焦散独立生成共享纹理，鱼和静态环境使用变形后的世界坐标采样；一次全屏消光统一处理不透明画面。
5. 新建 Ocean RenderOnly / Live 场景，旧场景和共享 URP 资产保持原有设置。使用设备测试、Player 原始数据和截图记录真实完成程度。

## 入口和开关

- `Assets/RVO/Demo/Phase4_Ocean.unity`：1 万合成鱼、独立环境；可设置至 3 万。右侧 HUD 切换雾、背景、焦散档位、近景动画和远景实验。
- `Assets/RVO/Demo/Phase4_OceanLive.unity`：原 Full3D 导航/ORCA，默认 1024。烘焙障碍共享 Ocean Surface 材质，保持可见。
- 菜单 `Tools > RVO > Create Phase 4 Ocean Demos` 只补建缺失场景；不会覆盖用户修改。
- `Build Phase 4 Ocean Benchmark Player` / `Build Phase 4 Ocean Live Benchmark Player` 分别输出 `Builds/Phase4Ocean/Phase4Ocean.exe` 和 `Builds/Phase4OceanLive/Phase4OceanLive.exe`。

`GpuFishRenderer` 的 `DetailedNearMesh` 仅替换 LOD0；三个近景模式均在同一网格上运行。LOD1–3 默认仍为原 128/72/32 顶点 Mesh。强制 LOD 可用于隔离成本；LOD 调试色仅用于诊断。

## P4.3：同源动画实验

`FishAnimationBaker` 在资源建立时生成共享纹理，运行中无逐鱼 Animator。每次切换动画会释放旧资源并重新上传端点，此时不是稳态性能样本。

| 模式 | 实现 | 存储与限制 |
|---|---|---|
| Procedural | travelling-wave 位移，解析导数修正法线 | 无动画纹理；默认 |
| VertexTexture | 2056 顶点 × 64 帧；RGHalf 存 X 位移和导数 | 526336 B，无 mip、linear、精确 Load 两帧；这是针对单轴波形压缩的 VAT，不能代表通用 position+normal VAT 的带宽 |
| BoneTexture | 24 个采样截面 × 3 行矩阵 × 64 帧 RGBAHalf | 36864 B；当前波形只需读取首行，双权重、双时间帧；仿射剪切骨架实验，不是完整刚体骨骼蒙皮 |

幅度在实例端应用，phase 使用快照的连续积分值，纹理时间索引显式 wrap。骨骼权重沿 Z 变化，法线包括权重梯度。Forward / DepthOnly / DepthNormalsOnly 共享同一变形入口，避免只在颜色 Pass 摆尾。

近景仍是程序化验证鱼，艺术品质、精制薄鳍/眼睛、真实 DCC VAT/Bone 多 clip 与完整 ShadowCaster 不属于已完成验收。当前所有鱼关闭实时投影；没有用主相机剔除集合冒充光源可见集。

## P4.4：远景实验和保守默认

交叉面为 12 顶点/6 三角形或 9 顶点/3 三角形；LOD3 为 4 顶点/2 三角形 billboard。交叉面端视 `abs(dot(view, forward)) > 0.88` 回 LOD1，降至 0.80 以下才退出，历史与 ID/generation 一起校验。尺寸 LOD 历史独立保存，避免回退后把尺寸滞回锁在高档。

卡片用身体与尾部椭球的解析视线交点构造轮廓及法线，不需要外部纹理，支持头/尾/背/腹视向。它是**解析体积 impostor 实验**，不是原计划中的多视图烘焙 atlas；身体曲率、鳍、深度和尾部波形均是近似。平面深度写入会产生近景遮挡误差，因此不应强制卡片作为常规近景表示。三角形卡片还可能裁掉宽处轮廓，保留作失败候选而非自动启用。

所有卡片 alpha clip；颜色/深度/法线共享裁剪。无 alpha blending，不引入鱼群透明排序。未完成全方向像素误差与 overdraw/GPU 配对收益评估，`EnableFarCards` 默认关闭。

## P4.5：焦散与假水

用户提供的 David Hoskins / joltz0r turbulence 在 `CausticPattern.hlsl` 保留署名。原运算和稳定代数式均保留在 Compute 验证入口；实际共享生成使用 float32 稳定式。输出为强度而非已着蓝色的鱼 albedo。

- 默认 256² R16F 两端点纹理，30 Hz 生成并在显示帧插值；512² 为高档，Off / DirectReference 可隔离成本。纹理 Repeat、完整 mip、连续世界 UV，运行时不回读。
- 生成与鱼绘制位于同一 graphics queue。只在环境步变化时生成下一端点，跳时钟或切质量时重建两个端点；暂停环境时不反复 dispatch。
- 原公式中线性 p 项并非严格周期，tile 最后 10% 向起边平滑混合是明确的艺术接缝修补，不能声称原函数天然无缝。
- 原运动驱动的共同周期为 `480π` 秒；可选 12 秒整数谐波变体是另一种运动。没有把原动画直接截成短循环，也没有交付离线 TextureArray。
- 256² 两纹理含 mip 有效载荷约 0.333 MiB，512² 约 1.333 MiB；不含驱动分配粒度或颜色/深度 RT。

六面背景是 outward cube + Cull Front 的不透明内壁，保持轴对齐世界位置，不随相机移动。背景排在普通不透明物体之后，具备真实深度和深度/法线 Pass。它不是六层透明水体。

`OceanCommon.WaterDistance` 用线段与 AABB 的交段长度处理相机在内、在外、擦边及平行射线。消光为 RGB `exp(-σ*d)`，混合经验水色；它是 Beer 消光与艺术散射近似，不是真实体积光输运。鱼、墙、背景统一经一次 RG 合成，避免双重雾。

## P4.6：Render Graph 和深度策略

`OceanEnvironment` 绑定相机，在 `beginCameraRendering` 准备共享纹理并注入自身 FogPass。URP 17.0.4 的 `BlitAndSwapColorRendererFeature` 样例明确允许直接 `EnqueuePass`，因此不为单相机演示新增全局注册表或修改旧 RendererData。

FogPass 请求深度和中间颜色；显式 `UseTexture` 声明 color/depth，只写独立目标，再交换 `cameraColor`，不作原位读写或多余拷回。相机作用域结束清除环境启用标记。支持一个非 XR 的 Base Camera；相机堆叠明确拒绝，多相机并发尚未验收。

| 对象/Pass | ZTest | ZWrite | 原因 |
|---|---|---|---|
| 鱼主体/alpha clip 卡片 | LEqual | On | 前景遮挡及后续深度采样 |
| 内壁/静态障碍 | LEqual | On | 水内几何深度；背景不覆盖前景 |
| 全屏水下合成 | Always | Off | 读取既有深度计算光程，不能用全屏三角形改写几何深度 |

原鱼 Shader 省略 ZTest 时 Unity 默认就是 LEqual，本次显式写出是明确契约，不应将其描述为修复“以前完全无深度测试”。[Unity 6 ZTest](https://docs.unity.com/zh-cn/engine/6000.0/manual/materials-and-shaders/shaders/reference/sl-reference/sl-commands/sl-ztest)、[官方 Render Graph 接入](https://docs.unity.com/en-us/engine/6000.0/manual/render-pipelines/universal-render-pipeline/customizing-urp/render-graph/write-render-pass)。

当前沿用 PC URP Forward+、SSAO、MSAA=1、depth/opaque texture；没有偷偷关闭额外 Pass 取得性能。粒子、近鱼 shadow proxy、独立 URP 质量资产和多视图 atlas 仍是后续候选，不因基础合成完成就标为验收通过。

## P4.7：复现口径

基准同时支持 Fixture 与 LiveBridge，CSV 的 `simulation_cpu_ms` 为显示帧内 Live 提交的 CPU Tick 总耗时（合成夹具为零）；GPU 时间仍以独立 timestamp 关联，-1 是不可用。CPU frame、打包、上传、提交都不是 GPU pass 时间。

通用：`-rvo-benchmark -rvo-offscreen -rvo-warmup 300 -rvo-frames 1800 -rvo-output <绝对目录>`。

合成对照可加 `-rvo-fixed-replay`：按每显示帧 1/60 秒固定推进夹具/环境，跨模式样本姿态可复现；这种回放时钟不能用于真实仿真实时率。正常 Live 不接受固定回放驱动，按墙钟执行。其它参数：

| 参数 | 值 |
|---|---|
| `-rvo-count` | 合成 1–30000 |
| `-rvo-animation` | Procedural / VertexTexture / BoneTexture |
| `-rvo-lod` | -1 / 0 / 1 / 2 / 3 |
| `-rvo-far` | Mesh / CrossQuads / CrossTriangles / Billboard |
| `-rvo-caustic` | Off / Shared256 / Shared512 / DirectReference |
| `-rvo-caustic-hz` | 1–60 |
| 独立开关 | `-rvo-no-ocean` / `-rvo-no-fog` / `-rvo-short-loop` |

所有 Player 串行运行。正式性能冻结还需要有效 GPU timestamp/capture、重复配对运行、全方向质量基准、长期资源趋势以及 D3D12 覆盖。不能把无 GPU 时间的离屏短测用于选定最终表示组合。

本轮实际测试与原始证据见 [续阶段验证记录](VERIFICATION_P4_CONTINUATION.md)。
