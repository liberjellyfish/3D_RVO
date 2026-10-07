# Phase 3 寻路性能与观察体验优化

> 历史专题记录：下文的默认参数、实现状态和测试数字对应文中日期及当时版本。当前系统结构与运行配置见 [ARCHITECTURE.md](ARCHITECTURE.md)，最新主场景证据见 [2048 多通路报告](VERIFICATION_REEF_NETWORK2048.md)。

日期：2026-09-30。Unity 6000.0.63f1，i7-12650H，16 GB 内存；Editor 测试，固定 seed 7、30 Hz。保留已有 Phase 3 工作和 Phase 1/2 实现。

## 瓶颈与取舍

对原 8 障碍地图测量前 600 个 Tick（包含初次寻路请求），256 agent 的导航平均耗时 140.35 ms，邻居 1.01 ms，避障 0.22 ms。主要问题是托管 A* 的 26 邻接扩展/重复 BVH 查询，不是三维半空间优化。原来每渲染帧还允许补算 8 个 Tick，进一步放大卡顿。

因此保留现有持久容量的空间哈希，没有新增 KD-tree。KD-tree 是否划算仍应取决于分布、范围与 K 的测量；本次替换它只能触及很小的一部分开销。ORCA 的动态平面、静态硬约束和独立连续碰撞安全检查均保留。

## 实现

- `BurstVolumePathfinder` 将有预算的 A* 扩展、最小堆与稀疏索引放到 Burst Job。每搜索槽持有可重用的 NativeArray/NativeParallelHashMap；保留原 `VolumePathfinder` 作为 Reference 后端与测试对照。不为每槽复制 256³ 的标签数组。
- 对不可变粗网格缓存每格 26 条边的合法性。默认 32³ 粗图每槽增加 128 KiB，跨请求复用；缓存不包含拥堵代价。细图保留完整几何检查。释放 World 时同时释放搜索工作区、缓存与 BVH。
- 启发式使用三维 26 邻接的无障碍最短距离：坐标差排序为 a≤b≤c，距离为 `c + (√2−1)b + (√3−√2)a`。weight=1 保留精确 A*，默认显式 weight=1.5 不变，仍支持更优 g 时重新入队。
- 粗网格端点连接与路径平滑使用细地图扫掠，粗图内部边继续使用粗地图保守几何。避免合法细图端点落在粗图向外膨胀部分时无谓回退；粗搜索真正失败时仍回退细图。预算耗尽仍是 Pending。
- 帧追赶默认上限改为 2 Tick，可在 Inspector 调整；固定 dt 不变，丢弃的追赶 Tick 仍显示在 HUD。性能不够时仿真时间会落后墙钟，而不是改变碰撞计算步长。
- 演示增加 96 个块体，总计 104 障碍；保留两个高低交错的穿墙开口。新地图已离线烘焙到原资产。小型 8 障碍 fixture 保留给旧几何测试。
- 导航线默认关闭；调试开关仍可打开。球体每帧只读取一次每个 agent 的位置/半径，空调试线网格不再重复上传。
- Follow selected 持续平滑跟随目标，保留轨道旋转和缩放；中键平移、Stop following、Fit volume 退出跟随，Reset 清理跟随状态。增加前后切换 agent 按钮。

## 原地图比较

记录分别位于 `Verification/Phase3/PerformanceBefore` 与 `PerformanceAfterOriginalMap`。这些是缓存优化前的第一轮配对测量，不包含渲染或帧追赶。

| agent | 优化前 P50/P95/P99 ms | Burst A* 后 P50/P95/P99 ms |
|---|---|---|
| 256 | 122.62 / 225.74 / 555.36 | 7.82 / 10.06 / 11.89 |
| 1024 | 125.87 / 267.11 / 319.77 | 8.47 / 10.74 / 11.74 |

这两轮沿用了早期脚本的单横线 `-burst-force-sync-compilation`，实际 Burst 参数应为双横线 `--burst-force-sync-compilation`。原始 CSV 的同步编译表头是旧脚本声明，不能当作同步编译证明；第一档 16 agent 含异步编译过渡，不用来宣称稳定态提升。256/1024 排在 16 档之后。最终加密场景测试已改正参数，并用独立 World 预热后重建测量 World，保留首次寻路工作量。

## 最终场景与验证

最终短基准位于 `Verification/Phase3/PerformanceDense`，含新增的 96 个块体和粗图边缓存，固定前 600 Tick：

| agent | P50 ms | P95 ms | P99 ms | 导航均值 ms | 邻居均值 ms | 避障均值 ms |
|---|---:|---:|---:|---:|---:|---:|
| 16 | 0.17 | 3.93 | 12.79 | 0.55 | 0.10 | 0.03 |
| 256 | 4.32 | 6.83 | 14.43 | 3.24 | 1.02 | 0.26 |
| 1024 | 5.09 | 7.79 | 14.36 | 3.46 | 1.30 | 0.63 |

长跑结果位于 `Verification/Phase3/DenseMatrix`，每档上限 18000 Tick / 60 秒墙钟，所有档位都在上限内全员同时到达：

| agent | 完成 Tick | 仿真秒 | 无渲染墙钟秒 | 全程 Tick P95 ms | 抽检碰撞对 |
|---|---:|---:|---:|---:|---:|
| 16 | 1616 | 53.87 | 0.59 | 0.23 | 0 |
| 256 | 2755 | 91.83 | 5.36 | 5.16 | 0 |
| 1024 | 4349 | 144.97 | 33.61 | 7.57 | 0 |

所有档位 core 主线程测得 GC 分配为 0，无失败路径或未就绪路径留到结束。16 档每 Tick 做独立动态碰撞检查；256/1024 每 30 Tick 抽检，不能解释为独立逐步遍历了全部大规模轨迹。逐 Tick 安全证书仍始终执行。墙钟包含独立质量观察开销；仿真秒是移动/等待时间，不能与 CPU 耗时混淆。

验证：全部 107 项 EditMode 测试通过（`EditMode-Optimization.xml`），涵盖旧阶段回归、Dijkstra 最优成本、独立几何检查、原始/Jobs 后端对照。最终缓存改动另行重跑的 18 项 Phase 3 用例全部通过（`EditMode-Final.xml`），覆盖缓存跨请求、改变拥堵代价、端点处于粗图膨胀区域、预算及容量状态。

图形 PlayMode 测试通过（`PlayMode-Final.xml`，Direct3D 11 / RTX 4060 Laptop）：检查默认无路径线、45 个渲染帧/180 Tick 的持续跟随、相机相对偏移与旋转保持、Fit 退出跟随以及 Reset。实际渲染的 [场景截图](Verification/Phase3/Presentation/overview.png) 已查看，确认加密障碍与关闭导航线后的场景。此测试验证交互行为，不测量稳定态渲染 FPS。

这些数字均指 Editor 中 CPU 仿真 Tick，不等于渲染帧率，也不是 Player Build 基准。最终地图与原基线不同，不将其 P95 直接相除宣称同图加速比。首次 Editor Burst 编译可能另有启动成本。

## 参考依据

- [Unity NativeContainer 与 Job System](https://docs.unity.cn/Manual/JobSystemNativeContainer.html)：Burst 的数据类型约束与 NativeContainer 数据通路，是迁移托管搜索热点的依据。
- [Moving AI Lab：A* 与网格启发式](https://www.movingai.com/astar.html)：按图的合法移动选择启发式；本文三维距离公式是对 26 邻接图的直接推导，并用独立 Dijkstra 测试验证。
- [RVO2-3D 官方 Agent.cc](https://github.com/snape/RVO2-3D/blob/main/src/Agent.cc)：核对低维线性规划结构；实测求解占比小，本次没有替换 ORCA 或删减安全检查。
- [Unity 固定更新与追赶](https://docs.unity3d.com/6000.0/Documentation/Manual/fixed-updates.html)：低帧率下多次固定更新的累积效应；本项目使用自己的 FixedStepClock，应用同样的有界追赶原则。

## 重跑

关闭测试副本的 Unity 后，以本机 Unity 执行：

```powershell
& 'D:/unityhub/Editor/Unity.exe' -batchmode -nographics --burst-force-sync-compilation `
  -projectPath '<isolated-project>' -executeMethod Rvo.Editor.Phase3Performance.Run -quit -logFile '<log>'
```

菜单 `Tools/RVO/Profile Phase 3 (600 ticks per tier)` 也可执行测量。`Phase3Performance.RunDenseDemo` 会重新烘焙演示，再执行短基准及每档 60 秒墙钟上限的运行矩阵。测试用 `-runTests -testPlatform EditMode`；观察跟随的测试类是 `Rvo.Tests.VolumePresentationTests`，截图需要有图形设备。
