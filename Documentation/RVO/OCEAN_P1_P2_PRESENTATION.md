# OceanLive P0 画面复核与 P1 / P2 推进

2026-10-05 后续更新：水纹材质、20 个礁石代理、彩色鱼纹及随机体型/速度见 [礁石海洋表现优化](REEF_ENHANCEMENT.md)。本页保留 2026-10-04 的历史记录。

日期：2026-10-04。范围对应 [OceanLive 重构规划](OCEANLIVE_RENDER_REARCHITECTURE_REVIEW.md) 第 10 节，和旧 Phase 1 / Phase 2 算法阶段无关。本轮保留工作区已有 P0 修改；按用户要求没有新增或运行自动化测试、数值回归、规模性能矩阵。

## 直接打开

- 新展示场景：`Assets/RVO/Demo/OceanReef/OceanReefLive.unity`，默认真实 1024-agent Live / ORCA，SMAA，无 Bloom。
- 菜单 `Tools/RVO/Open Reef Presentation` 打开并运行展示；底部按钮切拱门、真实鱼跟拍、全景，以及保守代理显示和 None / SMAA / TAA 候选。
- `Tools/RVO/Build Ocean Reef Presentation` 离线重建本目录中的网格、材质、导航和场景。需要先保存当前场景、关闭已打开的 Reef 场景；它会重建生成内容，不是合并手工美术修改。
- 原 `Phase4_OceanLive.unity` 保留规则障碍，继续作为 P0 对照。`Phase4_Ocean.unity` 继续是 RenderOnly；本轮没有把合成鱼统计写成 Live 规模成绩。

## P0 的画面发现与修正

运行旧 Live 场景并保存三个时刻的截图，见 [P0 原始运行](Verification/OceanPresentation/P0/live-02.png) 和 [采集记录](Verification/OceanPresentation/P0/capture.txt)。无几何方向背景有效，但原近景被规则盒体占据，鱼体辨识度弱，不能据此宣布展示质量完成。

新 PBR 近景揭示 indirect 绘制的额外接入问题：自定义 DrawMeshInstancedIndirect 没有普通 MeshRenderer 自动填写的逐对象主光可见标志和 SH 常量。旧固定 ambient 会掩盖直射缺失。`GpuFishRenderer` 现在显式绑定 `unity_LightData` 与环境 SH，并给每个绘制桶绑定 Reef 外观参数。Ocean 的 PBR 接收面共用场景配置的上/侧/下三色环境光，保证鱼与礁石的环境光口径一致；主光、阴影、太阳光程与表面焦散仍共用同一函数，观察雾仍只在合成阶段计算一次。

本轮检查的是实际画面和渲染接入；P0 历史数值结果仍保存在 [P0 记录](OCEAN_P0_STRUCTURE.md)，没有复跑，也没有将它们冒充为修改后数据验收。

## P1 已实现

`OceanReefBuilder` 先生成确定 seed 的闭合礁石网格，布置海床、拱门、侧礁与远景轮廓，再从各 MeshRenderer 的世界 bounds 提取保守 AABB。共有 8 个代理，离线写入专属 `ReefNavigation.bytes` / `ReefVolume.asset` / `ReefNavigation.asset`。不是从原导航盒反向装饰出岩石。

代理经既有 VolumeBake 向外对齐到体素面，所有占据、BVH、路径、平滑、跟随和静态避障仍使用同一组烘焙几何。地图为 128³、cell=0.5，世界范围 64³。代理包含实体网格，但会保守排除部分网格外自由空间；这不是任意凹网格的精确 voxelizer。拱洞由块体间的间隙组成，鱼可以穿洞或绕礁/越过，尚未实现生态群游路线编排。

`ReefFishMesh` 提供同一梭形鱼的 442 / 242 / 116 / 72 顶点四级网格：封闭身体、背腹色、体侧暖色带、眼部、鳃盖色带、胸鳍、背/腹鳍和分叉尾。眼部在前两级保留；四级保留主体与鳍的同源轮廓。薄鳍单独复制反面顶点，身体使用背面剔除；不为每条鱼创建 GameObject。解析游动继续共用于颜色、深度、法线和 motion，颜色 alpha 编码鳍面粗糙度，防止大块尖锐鳍高光。

`UnderwaterLighting.hlsl` 增加共享 URP BRDF 照明；礁石采用低频世界坐标层理与粗糙表面，鱼采用较光滑的身体。焦散只调制有阴影遮挡的直射光，未叠加到环境光、albedo 或最终雾色。反射仍依赖环境输入；本轮没有烘焙洞穴反射探针或实现局部体积散射。

`OceanPresentation` 使用独立 `OceanPipeline.asset`（阴影距离 160，单采样）和相机设置，退出时恢复原管线与 AA。三个镜头包含真实鱼跟拍，跟拍查询同一保守代理缩短遮挡方向上的镜头距离，不改变鱼的位置、速度或 ORCA。导航代理默认隐藏，可独立显示。

## P2 已实现与限制

- `FishMotionPass` 在 URP 相机速度之后、后处理之前写入 URP motion color，以真实 active depth 遮挡，使用当前网格的前后显示姿态和解析形变。资源依赖显式声明。只支持非 XR、单基础相机、单采样 Mesh 路径；实验卡片不启用该 motion 路径。
- 身份、generation、资源、相机切换及 camera cut 清理历史；补上相机漏渲染帧检测。相机切镜头同时重置 TAA。历史首次失效时输出零速度。
- 连续单 Tick 快照复用 GPU 旧 current 为新 previous，只上传新端点；多 Tick 追赶、重排或重建仍上传双端点。GPU 同步折返旧端点相位，避免跨 2π 插值倒转。该路径按代码减少一半端点上传字节，尚无耗时收益测量。
- 以同源轮廓四级 Mesh 和 20% LOD 滞回降低几何突变。未实现 LOD 短交叉淡化或 Hi-Z；不宣称已消除所有亚像素闪烁。
- 默认 SMAA；TAA 候选已经持续出帧，近景截图仍可见软化的尾鳍/轮廓，因此不选为默认，亦不认定无拖影或高频焦散历史污染。MSAA resolve 路径未验收。
- `FishBenchmarkRecorder` 对延迟 GPU 时间戳去重，新增有效样本数和 P50/P95/P99 汇总；无有效样本明确写 `-1`，不会把重复/无效采样当作 GPU 性能。采集仍需显式启动，本轮未运行。

## 画面证据和验收边界

所有本轮 PNG 来自 Unity 6000.0.63f1 / D3D11 的实际 Live 场景，1280×720，SRP 显式 RenderTexture；PNG 按 sRGB 输出。每组记录实际 Tick、agent 数、相机位置和 API。P0 首组为三个时刻单次渲染；后续复核组持续出帧，使运动历史与 AA 运行，每隔约四秒保存一张。

- [近景复核](Verification/OceanPresentation/ReviewedNear/live-02.png)：可辨眼部、鳃盖、背腹色和鳍，亮处与阴影中的鱼有可见明暗差异；[运行记录](Verification/OceanPresentation/ReviewedNear/capture.txt)。
- [最终拱门画面](Verification/OceanPresentation/ReviewedReef/live-01.png) 与 [运行记录](Verification/OceanPresentation/ReviewedReef/capture.txt)。
- [TAA 候选近景](Verification/OceanPresentation/TaaCandidate/live-01.png) 与 [运行记录](Verification/OceanPresentation/TaaCandidate/capture.txt)。不同运行的调度/Tick 不同，这些不是严格同步的画质对照或性能证据。

已完成编译和短时实际出帧检查；没有执行新的数据测试。P1 已落地可运行的展示基线，但地形美术精修、生态群游、长时间复杂路线的视觉验收仍待推进。P2 已补 motion / AA 候选 / 上传优化 / 计时统计基础，但有效 GPU 时间、1k/10k/30k 独立规模矩阵、各 pass 成本、长时间资源稳定性和严格同轨迹 AA 对照仍未验收。它们按本轮不做数据/性能测试的要求保留，不标记 P1/P2 全部退出条件完成。

当前留存的是离屏实际渲染截图，不是可见 Player 帧率证据或连续视频；不能据静态图宣称时间闪烁与拖影已全面排除。

交付前另打开 Unity 编辑器 Game 视图确认默认场景实际运行；SMAA、镜头与代理按钮可见。最终三组目录保存对应 `unity.log`，检查未见 C# 编译、Shader、Render Graph 或运行异常。这仍不等同于 Player 性能或长时间稳定性验收。
