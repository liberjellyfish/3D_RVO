# Unity 3D_RVO：导航、避障与 GPU 鱼群

更新：2026-10-07。当前项目使用 Unity 6000.0.63f1 / URP 17.0.4，以同一仿真 World 支持二维算法实验、二维网格导航、三维体素导航；最新展示是 2048 条真实鱼在多房间礁石空间内寻路和避障。本文是操作入口，完整框架、数据流和源码阅读路线见 **[项目整体结构与数据流](ARCHITECTURE.md)**。

## 快速运行

1. 用 Unity **6000.0.63f1** 打开项目，等待包导入和编译。
2. 打开 [Assets/RVO/Demo/OceanReef/OceanReefLive.unity](../../Assets/RVO/Demo/OceanReef/OceanReefLive.unity)，进入 Play；菜单 **Tools → RVO → Open Reef Presentation** 也会打开并运行。
3. 当前场景默认真实 Full3D / ORCA / SpatialHash / JobsBurst，2048 agents，192³、0.5 m 体素，96 m 范围、48 个障碍代理。数量档位是 **128 / 1024 / 2048**。
4. 左侧 HUD 可暂停、单步、重置、切档位、查询或后端，并显示路径等待、行进/到达、粗细图统计、Tick、仿真秒、耗时和掉 Tick。切换这些运行模式会重新创建世界。
5. 底部切总览/跟拍/俯视、显示导航代理、切 None/SMAA/TAA 候选；右下角开关 18 条实际剩余路径。默认 SMAA。
6. 右键旋转、滚轮缩放、中键平移。需要算法细节可改开 [Phase3_Volume](../../Assets/RVO/Demo/Phase3_Volume.unity)，用球体、路径、体素切片和速度空间平面观察。

`EditorBuildSettings` 默认仍是 SampleScene。编辑器直接打开上述场景即可；若要构建 Player，需要显式选择演示场景或使用已有的 Phase 4 专用构建菜单，不能依赖默认 Build Settings 得到 Reef 展示。

## 各场景的用途

| 入口 | 用途 |
| --- | --- |
| [OceanReefLive](../../Assets/RVO/Demo/OceanReef/OceanReefLive.unity) | 最新多通路真实导航鱼群 |
| [Phase3_Volume](../../Assets/RVO/Demo/Phase3_Volume.unity) | 三维核心算法与调试展示，原 16/256/1024 档 |
| [Phase2_Navigation](../../Assets/RVO/Demo/Phase2_Navigation.unity) | 二维障碍地图、ORCA 与交通恢复 |
| [Phase13_Demo](../../Assets/RVO/Demo/Phase13_Demo.unity) | None / VO / RVO / ORCA 二维对照 |
| [Phase4_RenderOnly](../../Assets/RVO/Demo/Phase4_RenderOnly.unity) / [Phase4_Ocean](../../Assets/RVO/Demo/Phase4_Ocean.unity) | 合成渲染夹具；最多 30000，未运行寻路/ORCA |
| [Phase4_Live](../../Assets/RVO/Demo/Phase4_Live.unity) / [Phase4_OceanLive](../../Assets/RVO/Demo/Phase4_OceanLive.unity) | 真实仿真桥接及规则障碍/水下光学回归 |
| [Phase4_SurfaceStudy](../../Assets/RVO/Demo/Phase4_SurfaceStudy.unity) | 无导航的表面材质研究 |

## 改配置与生成资产

主场景 Profile 为 [ReefNavigation.asset](../../Assets/RVO/Demo/OceanReef/ReefNavigation.asset)。当前 `Simulation.AgentCount=16` 会被场景 `AgentTier=2` 的档位数量覆盖，实际运行 2048。

- 改运行参数后 Reset 读取新设置；HUD 的算法/查询/后端选择不写回 Profile。
- 改障碍、体素尺寸、最大体型或导航安全余量后须重烘焙；只改速度无需重烘焙。
- **Tools → RVO → Build Ocean Reef Presentation** 重建当前 Reef 场景、网格、材质、Profile 和导航资产。生成器默认值在 `OceanReefBuilder` / `ReefRouteLayout`，该菜单会应用这些预设，手工修改生成资产后直接重建会被覆盖。
- **Rebake Selected Phase 3 Profile**（以及 Inspector 的 Bake XYZ demo volume）使用 `DenseDemoBoxes` 生成规则演示障碍，不能用于保留 Reef 或自定义障碍布局；Reef 使用自己的 Builder。自定义布局需把自己的代理交给 `VolumeBake.Bake()` 并更新资产。二维 **Bake Selected Navigation Profile** 也按导航参数生成地图。操作前保存场景、退出 Play，Reef 重建还要求关闭已打开的 Reef 场景。
- 旧阶段的 Create 菜单主要补建缺失演示；不同菜单的覆盖范围不同，以其源码为准。

## 阅读顺序

| 文档 | 用途 |
| --- | --- |
| **[ARCHITECTURE.md](ARCHITECTURE.md)** | 当前整体说明：目录/依赖、启动、Tick、数据所有权、寻路、避障、安全、GPU、水下渲染、阅读切口 |
| [VALIDATION.md](VALIDATION.md) | 测试分类、当前复现入口、计时与质量口径 |
| [DOCUMENTATION_CLEANUP.md](DOCUMENTATION_CLEANUP.md) | 本次删除/合并依据及历史文档使用方式 |
| [VERIFICATION_REEF_NETWORK2048.md](VERIFICATION_REEF_NETWORK2048.md) | 当前 96 m、多通口、2048 布局的测试、流量与 CPU 测量 |
| [VERIFICATION_REEF_ROUTES48.md](VERIFICATION_REEF_ROUTES48.md) | 焦散去重复实现和旧 64 m、1024 布局的对照证据 |
| [PHASE2_PLAN.md](PHASE2_PLAN.md)、[PHASE2_TRAFFIC.md](PHASE2_TRAFFIC.md) | 保留的二维导航/交通详细设计 |

历史实施/验证索引：

| 主题 | 报告 |
| --- | --- |
| 早期二维运动与算法 | [P1.1–1.3](VERIFICATION_P13.md)、[Phase 1](VERIFICATION_P1.md) |
| 二维首版与优化 | [首版](VERIFICATION_P2.md)、[交通改进](PHASE2_TRAFFIC.md)、[优化原始证据索引](VALIDATION.md#phase2-evidence) |
| 三维寻路性能与首路径等待 | [性能优化](VERIFICATION_P3_OPTIMIZATION.md)、[收尾](VERIFICATION_P3_CLOSEOUT.md) |
| GPU 基础桥接和 LOD | [P4.0–4.2](VERIFICATION_P4_FOUNDATION.md) |
| 动画、卡片、焦散和初版海洋 | [实施](PHASE4_CONTINUATION.md)、[验证](VERIFICATION_P4_CONTINUATION.md) |
| 表面材质与光学重构 | [SurfaceStudy](PHASE4_SURFACE_CORRECTION.md)、[Ocean P0](OCEAN_P0_STRUCTURE.md) |
| 礁石、PBR、Motion/AA、上传复用 | [展示推进](OCEAN_P1_P2_PRESENTATION.md)、[体型/速度/条纹](REEF_ENHANCEMENT.md) |

历史报告保留当时地图、默认参数、代码路径和性能口径。例如旧报告的 128³、1024 agents、背景 Cube、RenderMeshIndirect、224N GPU 字节都不自动代表当前主场景。最新实现以 ARCHITECTURE 与工作区代码为准，历史数据以报告关联的原始证据为准。

## 当前结果与边界

已保存的 2048 正式三次报告记录：全员在约 35.3 秒仿真时间到达，CPU Tick P95 12.830–13.014 ms，逐 Tick 静态扫掠违规为 0，每 30 Tick 抽样成对碰撞为 0；详见 [报告](VERIFICATION_REEF_NETWORK2048.md)。这是 Editor 纯仿真数据，不能解释为整帧/GPU FPS。本次文档整理未重新运行这些测试。

核心仍采用有界、静态、保守 AABB 体素图、球体代理和瞬时速度模型。三维支持 None/ORCA，None 只保护静态障碍；局部恢复没有普遍无死锁保证。渲染支持单 Base Camera、非 XR、单采样 Mesh 主线，TAA/卡片/纹理动画仍有候选范围与验收限制。详细边界见 [架构第 15 节](ARCHITECTURE.md#reading)。
