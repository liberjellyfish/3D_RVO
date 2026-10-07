# 验证、测量与复现指南

更新：2026-10-07。本页说明**当前代码**的检查入口；历史 XML/CSV 只证明报告对应版本和配置的结果。本次文档整理未启动 Unity、未重新运行算法测试或性能矩阵。整体框架见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 1. 按所改模块选择检查

Unity 的 **Window → General → Test Runner** 可选 EditMode / PlayMode，相关程序集为 `Rvo.Tests.EditMode` 与 `Rvo.Tests.PlayMode`。先用类名过滤相关检查，涉及多层行为时再运行相应回归。

| 改动范围 | 主要测试类 |
| --- | --- |
| World、配置、双缓冲与释放 | `FrameworkContractTests`、`Phase13Tests` |
| 二维 VO/RVO/ORCA、最近 K | `Phase1AlgorithmTests`、`Phase13Tests` |
| 二维地图、路径、KDTree、安全 | `Phase2NavigationTests`、`Phase2OptimizationTests` |
| 二维交通协调 | `TrafficRecoveryTests` |
| 三维地图、A*、粗细图、球体 ORCA、安全 | `Phase3VolumeTests` |
| 个体差异、多房间和通口 | `ReefVariationTests` |
| 姿态、GPU 数据布局、变形、快照隔离 | `Phase4PresentationTests` |
| 暂停/单步、球体调试显示、生命周期 | `DriverTests`、`NavigationDriverTests`、`VolumePresentationTests` |
| GPU 索引、间接绘制、仿真独立性 | `Phase4GpuTests` |
| 动画/卡片/海洋材质 | `Phase4OceanTests`、`Phase4SurfaceTests` |
| 真实鱼深度、RGB 光程、背景、显示历史 | `OceanP0Tests` |
| 随机水纹、mip、接缝、时间插值 | `CausticSamplingTests` |
| 当前 2048 场景、HUD 和实际抽样路径 | `ReefNetworkPresentationTests` |

实际测试代码位于 [EditMode](../../Assets/RVO/Tests/EditMode) 和 [PlayMode](../../Assets/RVO/Tests/PlayMode)。GPU/像素/深度测试需要真实图形设备，不能用 `-nographics` 验收。

完整 EditMode **不等于只跑小规模正确性**：`Phase2ThroughputTests` 含普通 TestCase 的 16/256/1024 档吞吐记录，以及阶段测量；`Phase1SurveyTests` 也会导出调查数据。很多测试会写 `Verification` 或 `Documentation/RVO/Verification`，同一路径可能覆盖旧输出。正式复现应使用脚本约定的新目录，或在独立测试副本中运行并保存原证据。

## 2. 当前 Reef 网络复现

脚本 [RunReefNetwork2048.ps1](RunReefNetwork2048.ps1) 默认 Unity 路径是 `D:/unityhub/Editor/Unity.exe`，可用 `-Unity` 指定本机安装。批处理前关闭占用该项目的 Unity 实例，在项目根目录运行；输出目录需尚不存在。

```powershell
./Documentation/RVO/RunReefNetwork2048.ps1 -Stage Tests -Output Documentation/RVO/Verification/ReefNetwork2048/Retest
./Documentation/RVO/RunReefNetwork2048.ps1 -Stage Profile -Output Documentation/RVO/Verification/ReefNetwork2048/Reprofile
./Documentation/RVO/RunReefNetwork2048.ps1 -Stage Capture -Output Documentation/RVO/Verification/ReefNetwork2048/Recapture
```

| Stage | 实际行为 |
| --- | --- |
| Tests | 跑 `ReefVariationTests;Phase3VolumeTests;TrafficRecoveryTests` 的 EditMode，再跑 D3D11 `ReefNetworkPresentationTests` |
| Profile | 调用 `ReefPerformance.BuildAndRun`，**先重建当前保存的场景/导航资产**，再测当前最大数量档 |
| Capture | 在 D3D11 连续采集总览、俯视和保守代理画面 |

Tests 阶段没有包含 `CausticSamplingTests`，要检查水纹必须另选该 PlayMode 类或使用旧脚本的对应测试入口。Profile 应先保存自定义资产；它会应用当前 `OceanReefBuilder` / `ReefRouteLayout` 的生成预设。

编辑器菜单 **Tools → RVO → Profile Reef (largest tier, 1800 ticks x 3)** 直接测现有 Profile。默认取 `AgentCountTiers.z`，当前为 2048；脚本 Profile 的 BuildAndRun 还会重建资产。`-reef-agents` 可覆盖数量，`-reef-repeats` 可覆盖 1–10 次重复。

当前测量采用独立 16-agent 小世界预热，随后每次创建新 World，共用 `BakedNavigationVolume.Load()` 缓存的地图与已预热地标。每次连续 1800 Tick（三次默认重复），保留首批路径工作。**不是每次重新解码、冷建地标的启动基准。**

输出 summary、curve、agents、spawns、portals、bypasses 与 environment。Tick 计时来自 World，观测、写 CSV、路线统计在计时之外。静态整段检查每 Tick 覆盖全体 agent；独立成对动态质量检查每 30 Tick 抽样，不能解释为独立逐 Tick 全 pair 验证。生产安全层始终运行。

当前保存的正式证据是 [Final/summary.csv](Verification/ReefNetwork2048/Final/summary.csv)，完整解释见 [2048 报告](VERIFICATION_REEF_NETWORK2048.md)。本次没有重跑。

## 3. 其他入口及历史脚本边界

| 入口 | 用途与边界 |
| --- | --- |
| `Run Selected Profile Benchmark (explicit)` | 显式运行选中 Phase 1 Profile 的 BenchmarkRunner；1k/10k 配置是真实测量入口 |
| `Profile Phase 3 startup (1024 agents)` | 首路径就绪延迟及初始化口径，区别于总到达时间 |
| `Profile Phase 3 (600 ticks per tier)` | 三维短阶段成本，包含首批路径 |
| `Run Phase 3 Matrix (60s wall cap per tier)` | 按墙钟上限跑到达矩阵 |
| `Run Phase 3 Full Tick Acceptance` | 按配置 AcceptanceTicks 跑更完整矩阵 |
| [RunReefRoutes48.ps1](RunReefRoutes48.ps1) | 保留水纹/导航检查与采集入口；Profile 调用当前 Builder 和最大档，**不会复原旧 64 m、1024、20/48 障碍地图对照** |
| [RunPhase4Benchmarks.ps1](RunPhase4Benchmarks.ps1) | 合成/Live Player 渲染 case 的串行执行与输出收集；依赖已构建的 Player，参数见脚本和阶段报告 |

旧 `ReefRoutes48` 对照要恢复报告对应的源代码、生成器、Profile 和地图资产再测。仅在当前代码运行旧脚本无法复现历史配置。历史 100/1k/10k 的完整算法、场景、Player 矩阵没有因为工具存在或针对性测试通过而自动完成。

## 4. 质量检查的定义

| 项目 | 检查原则 |
| --- | --- |
| 运动/输入 | 有限数值、速度上限、二维 Y 不变量、三维初始球体间距 |
| 邻居 | 完整 XYZ/XZ 距离、边界、负坐标、同距离稳定 ID、最近 K 和截断统计 |
| 路径 | 端点状态、请求取消、预算、容量、独立最短路对照、全部段净空、粗图失败的细图回退 |
| 求解 | 速度圆/球、平面可行侧、退化、硬约束和动态松弛 |
| 连续安全 | 相邻 Tick 之间的扫掠运动；离散位置没重叠不能证明一步内没对穿 |
| 恢复 | 等待、退让/租约、限频重规划、有限 Tick 内到达与未到达者 |
| 表现 | 只读复制、ID/Generation、同 Tick 仿真独立性、GPU buffer/索引、真实像素/深度/法线/motion |
| 生命周期 | 多次 Reset、停用/启用、退出场景、资源重建、完成 Job 后释放 |

独立质量不能只复用 ORCA 的已截断邻居集合。小世界全 pair、规模测量抽样需要明确覆盖范围。HUD 的 `Independent O(N²) XYZ diagnostics` 可关闭以排除诊断成本，它不关闭生产静态/动态安全层。三维 None 关闭 agent-agent 防护，不能纳入 ORCA 的动态安全结论。

## 5. 性能报告口径

必须区分固定仿真时间、实际墙钟、CPU Tick、显示帧循环和有效 GPU 时间。记录 Unity/包/代码版本、地图资产/几何、seed、半径/速度差异、查询范围/K、后端、搜索预算、渲染/诊断开关、硬件、分辨率、图形 API、构建模式、预热与采样范围。

- `MeasureStages=true` 每阶段 Complete，含执行与等待，也增加同步；正式总耗时应同时明确是否开启该模式。
- 节点预算不是硬毫秒预算；路径平滑、队列和恢复也有成本。冷启动解码、地标构建、Burst 编译必须与预热后的 Tick 区分。
- 记录首次路径 Ready 的 P50/P95/最大值、NeverReady、首次/当前到达、未到达者、最长等待和重规划；不能只统计成功样本。
- 随时间记录移动比例，避免大部分采样都在测已到达静止个体。
- GC 字段通常是 World.Step 主线程分配，不代表整个应用零 GC；Native/GPU 字节字段是自有有效载荷，不是峰值或总 VRAM。
- `FishBenchmarkRecorder` 的 `submit_ms` 是 CPU 准备调度口径，不包含完整 RG/GPU 绘制。GPU 延迟时间戳须去重，无效/重复为 -1；无有效样本不能推导 GPU 成本或 FPS。
- 合成固定回放能匹配显示轨迹，但不证明 Live 仿真具有相同实时率；离屏图像不包含窗口呈现成本。
- 性能比较固定同地图、初态、画质与检查范围，不能把不同密度/几何的结果直接相除作加速倍数。

正式渲染验收仍需有效 GPU 抓帧/分项、可见 Player 稳态运行、同轨迹 AA/LOD 质量对照，以及长期资源趋势。旧报告保留未测和失败，不把功能实现视为所有预算通过。

<a id="phase2-evidence"></a>
## 6. 历史二维优化原始证据

此索引从已删除的重复页 `VERIFICATION_P2_OPTIMIZATION.md` 合并而来，避免丢失其唯一的证据链接。这些结果对应当时二维版本，不是当前三维主场景的重新测量。

- [优化基线 XML](Verification/Phase2Optimization/baseline.xml)
- [最终导航专项 XML](Verification/Phase2Optimization/FinalNavigation.xml)
- [PlayMode XML](Verification/Phase2Optimization/PlayMode.xml)
- [1024 SpatialHash CSV](Verification/Phase2Optimization/Final/throughput-1024-SpatialHash.csv)
- [1024 KdTree CSV](Verification/Phase2Optimization/Final/throughput-1024-KdTree.csv)

详细二维设计在 [PHASE2_PLAN](PHASE2_PLAN.md)，交通恢复及后续结果在 [PHASE2_TRAFFIC](PHASE2_TRAFFIC.md)，旧运行时刷新首版在 [VERIFICATION_P2](VERIFICATION_P2.md)。其他历史报告统一由 [README 索引](README.md) 进入。
