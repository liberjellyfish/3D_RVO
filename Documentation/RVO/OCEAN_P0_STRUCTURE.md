# OceanLive P0 结构修正与验证

> 历史专题记录：下文的默认参数、实现状态和测试数字对应文中日期及当时版本。当前系统结构与运行配置见 [ARCHITECTURE.md](ARCHITECTURE.md)，最新主场景证据见 [2048 多通路报告](VERIFICATION_REEF_NETWORK2048.md)。

后续画面复核与 P1/P2 实施见 [展示推进记录](OCEAN_P1_P2_PRESENTATION.md)。该轮补齐 indirect 逐对象光照绑定并接入 MotionVectors；下文测试结果和“后续边界”描述的是 P0 当时状态，未在后续轮复跑。

实施日期：2026-10-04。范围为 OceanLive 背景、光程、共享照明及鱼深度的 P0 结构修正，原基线 `f3d5911`。本轮完成结构和数值光学闭环；精制礁石、物种网格和正式 GPU 性能矩阵继续按 P1/P2 推进。

## 职责与数据流

| 模块 | 职责 |
|---|---|
| OceanEnvironment | 单基础相机装配、世界水域、独立水面、光学/背景参数及资源释放；不读取仿真 |
| CausticField | 双时刻 R16F、mip、时钟和质量切换，复用同一时刻的生成结果 |
| WaterOptics.cs / .hlsl | 世界距离标定、线段与有限水域/平均水面求交、RGB Beer 透射 |
| UnderwaterLighting.hlsl | 鱼与接收面共用 URP 主光、太阳路径衰减、阴影和焦散投影；焦散只调制直射，不重复乘 NdotL |
| FishGeometryPass | 显式 RG 鱼深度/法线与 opaque 颜色，声明姿态、可见 ID、args、阴影和附件依赖 |
| OceanCompositePass | opaque 与 sampled depth 之后、透明之前的一次合成；独立输出并交换 cameraColor，显式绑定本相机深度 |

默认 Ocean 不再创建背景 Cube。空深度像素使用低频方向渐变，光学远端由 HorizonDistance 指定，不随 Far Clip 改变；背景不写深度、没有法线或焦散。SurfaceStudyBackground 仅为 SurfaceStudy 保留强花纹内壁实验，普通 Ocean/OceanLive 的该开关为 false，BackgroundMaterial 为 null。

鱼仍沿提交快照、同队列 Compute、插值、剔除、四桶 LOD/args 的准备链路工作。为修复证实的漏深度，所有鱼几何统一改为 RG 内 indirect draw；没有延迟 Compute 却在图外画鱼的混合提交。Prepass 路径补写真正的 depth/normal 附件，Copy 路径通过鱼 opaque 写 active depth 后由 URP 拷贝。鱼与 receiver shader 不算 view fog，全屏阶段只计算一次。

## 参数、镜头与操作

移除 Live 消光乘 0.25。两场景均以水内距离 100 世界单位时 T_RGB=(0.55,0.72,0.82) 标定，sigma=-ln(T)/100≈(0.00597837,0.00328504,0.00198451)。这是项目尺度下的艺术标定，不是实测海水常数。

| 水内距离 | T_R | T_G | T_B |
|---:|---:|---:|---:|
| 100 | 0.550 | 0.720 | 0.820 |
| 300 | 0.166 | 0.373 | 0.551 |
| 600 | 0.028 | 0.139 | 0.304 |

Live 水域仍为 1200×800×1200，但独立水面 y=200，覆盖整个导航区；RenderOnly 水面 y=90。太阳路径影响直射，观察路径影响到相机的透射，两段各衰减一次。共享 receiver 是 P0 漫反射底座，完整 PBR/反射属于 P1。

Live 默认镜头为 (-115,18,-110)，看向 (-65,0,0)，Far Clip=800，关闭后处理并保留 HDR。导航盒仍是结构回归接收面；近中远光学层次可检查，规则盒布局不是最终礁石展示。

打开 `Assets/RVO/Demo/Phase4_OceanLive.unity` 直接 Play。HUD 的 Simulation status colors 单独开启到达白色/旧橙蓝诊断色；默认展示色不随 Arrived 改变。LOD 色独立。Optics view 循环 Final / BeforeFog / WaterDistance / Transmittance / SceneDepth，距离以 HorizonDistance 归一化到红通道。BeforeFog 是 receiver 照明之后、view fog 之前的线性颜色。

新建与已有场景使用同一配置函数。菜单 `Tools/RVO/Apply Ocean P0 Structure` 可重放迁移；遇到未保存的已打开场景需先保存。旧强花纹菜单只作用于 SurfaceStudy。未修改 PC Renderer/RPAsset 或 Phase 1–3 默认配置。

## 验证证据

| 检查 | 最终结果 |
|---|---|
| 全部 EditMode | 117/117 |
| 完整 PlayMode D3D11 | 14/14 |
| 完整 PlayMode D3D12 | 14/14 |
| 每个 API 的 36 组光学样本 | 最大距离误差约 5.73×10⁻⁶，RGB 透射率/最终颜色最大误差约 2.39×10⁻⁷ |

D3D12 首次运行曾因 Windows 占用历史截图文件而中断；新证据按 API 分目录后复跑通过，没有删除断言或扩大数值容差。

修改前在真实 D3D11 冻结 1024-agent Live、Tick 0、环境 4 秒、1280×720、关闭后处理。前后全景使用旧镜头 (230,180,-330)、看向原点、Far Clip=3000，新默认近景另存。

- [真实 Live 基线](Verification/OceanP0/baseline-live.png)、[基线运行](Verification/OceanP0/Baseline.xml)。原始 PNG 是线性颜色读回，不与 sRGB 预览直接比亮度。
- [同相机 P0 线性图](Verification/OceanP0/p0-Direct3D11-live.png)、[sRGB 预览](Verification/OceanP0/p0-Direct3D11-live-srgb.png)、[默认近景](Verification/OceanP0/p0-Direct3D11-live-default-srgb.png)。预览仅转 sRGB，没有曝光/Bloom/调色。
- [D3D11 数值](Verification/OceanP0/Direct3D11-optics.json)、[D3D12 数值](Verification/OceanP0/Direct3D12-optics.json)。
- [全部 EditMode](Verification/OceanP0/EditMode.xml)、[全部 PlayMode D3D11](Verification/OceanP0/PlayMode-D3D11.xml)、[全部 PlayMode D3D12](Verification/OceanP0/PlayMode-D3D12.xml)。

每个 API 测 3 条深度路径×3 距离（8/16/32）×2 相机位置×2 接收对象，共 36 组。路径为 SSAO 关的 opaque depth copy、SSAO 开的 normals prepass、SSAO 关的强制 depth prepass。参照对象由 CPU 对同一鱼网格执行同一形变，使用 Unlit 白材质；再独立做三角形射线求交，给出解析期望光程。每组导出雾前颜色、距离、T_RGB 和最终颜色，验证实际鱼深度与单次 Beer 合成。

首次检查发现旧鱼提交在三条路径都读成空深度、距离=100，颜色却可见；参照 Mesh 正确。显式 RG 提交修复后，D3D11 最大距离误差约 5.73×10⁻⁶ 世界单位，T_RGB/最终颜色最大误差约 2.39×10⁻⁷；D3D12 的实际误差见 JSON。容差仍为距离 0.04、T 0.004、最终颜色 0.006，未扩大容差掩盖原故障。

新增检查还覆盖背景深度=0、Far Clip 不改变 horizon、域顶不改变独立水面、显示历史同帧不推进及 generation/cut/reset/释放失效。原有动画/卡片/遮挡、30000 索引、100 次 buffer/纹理重建、表现开关不改变仿真等回归继续执行。新 SurfaceStudy 和旧回归截图按 API 存在 OceanP0/SurfaceStudy、Regression，历史目录不覆盖。

未进行 Frame Debugger/RenderDoc 人工抓帧，未宣称 MSAA、XR、相机堆叠、多相机、透明物体或完整 PBR 已验收。本轮证据覆盖非 XR 单基础相机、URP Render Graph、单采样 opaque 鱼与 Mesh 的 D3D11/12 光学链路。

## 显示历史与后续边界

PreviousDisplay 保存前一显示帧的实际插值/动画姿态，仿真 endpoints 不能替代它。新增 64N 字节 buffer，每显示帧复制一次，同帧重复请求不推进。generation、身份槽位、容量/表示重建、相机/投影变化、漏显示帧和显式 InvalidateDisplayHistory（camera cut）均使历史失效；重置帧 previous=current。renderer 同时保存前一非抖动 ViewProjection。

MotionVectors Pass、完整动态遮挡历史、TAA 和 LOD 过渡仍属 P2。历史 buffer 不代表已产生运动向量，未启用 TAA。submit_ms 现在覆盖 GPU 准备命令的 CPU 调度，不包含完整 RG 几何提交/执行；元数据记录此变化，不与旧高层提交数据混用。

P1 继续精制礁石/洞穴、离线保守代理、物种网格与 PBR，P2 再测有效 GPU 时间和规模稳定性。CausticField 本轮由唯一 Ocean 相机拥有；跨相机世界共享、独立 Ocean 质量资产、透明介质和体积散射不在本轮范围。

