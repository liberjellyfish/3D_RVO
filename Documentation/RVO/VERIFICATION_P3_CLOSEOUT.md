# Phase 3 收尾：到达标识与首路径延迟

日期：2026-10-01。Unity 6000.0.63f1，i7-12650H，Editor，JobsBurst，同一 256³ / 104 障碍地图、seed 7、30 Hz；没有重烘焙或更改演示预算。

## 实现与边界

- 到达 agent 使用**白色球体 + 深色腰带**，未到达继续使用原来的 ID 颜色。复用同一 Mesh 和材质，不增加 GameObject、Draw Call 或逐帧动画。只在到达状态变化时上传颜色；依据提交后的位置与 ArrivalDistance 判断，零速不代表到达，离开终点/更换目标会恢复原色。
- HUD 增加颜色图例与 `First route ready`。它统计全程首次拿到路径的数量；`Pending` 则也包含后续重规划，二者不能混同为堵塞。首路径就绪也不保证立即移动，局部避障仍可能要求等待。
- `VolumeLandmarks` 在小型粗图上预处理 4 个地标的精确图距离，用三角不等式给 A* 提供绕墙下界，和原 26 邻接几何下界取最大值。最远点采样覆盖最大连通域；无穷距离忽略，保留浮点余量。拥堵代价只增加边成本，因此无需重建静态下界。
- 一次性边检查及 Dijkstra 也通过 Burst 执行。默认 32³ 粗图的托管表为 512 KiB，随加载地图缓存；每个 Jobs 世界只复制一份 512 KiB Native 表供所有搜索槽只读借用，Dispose 时按所有权释放。初始化临时工作区随构建释放。没有为 1024 agent 分别分配搜索工作区或距离表。
- 粗图下界只服务粗图搜索，回退细图仍用细图自身几何启发式；粗路径、端点连接、平滑与安全证书仍做原几何检查。默认 weight=1.5、两槽、4096 节点/Tick、16 完成请求/Tick 都保持不变。weight=1 的细图精确对照仍可用。

采用共享启发式的原因是本场景目标分散，但反复穿过同一组墙洞。地标表跨目标共享绕墙信息；无需引入多目标 flow field、每 agent 常驻线程或更复杂的任务队列。分批预算仍用于控制单步工作量，本次主要减少总搜索量。

依据：[Microsoft Research ALT 原论文](https://www.microsoft.com/en-us/research/publication/computing-the-shortest-path-a-search-meets-graph-theory/)、[A* Pathfinding Project 地标启发式实现说明](https://arongranberg.com/astar/documentation/stable/heuristicopt.html)。本项目二维阶段已有同类下界；三维版本限定在小型粗图上，并用独立 Dijkstra 与几何检查验证。

## 1024 首路径就绪对照

原始数据：[before.csv](Verification/Phase3/Startup/before.csv)、[after.csv](Verification/Phase3/Startup/after.csv)、[运行环境](Verification/Phase3/Startup/environment.txt)。两个版本使用相同地图、seed、预算和后端。

| 指标 | 优化前 | 优化后 |
|---|---:|---:|
| 全员首路径就绪所需 Tick | 2053 | 283 |
| 首路径 P50 / P95 仿真秒 | 32.767 / 64.267 | 4.967 / 8.933 |
| 最晚首路径仿真秒 | 68.400 | 9.400 |
| 此期间累计扩展节点 | 8,409,088 | 1,158,952 |
| 初始化 ms | 51.149 | 45.120 |
| 含初始化的无渲染墙钟 ms | 11,353.355 | 2,222.635 |
| CPU Tick P95 / 最大 ms | 6.982 / 25.851 | 11.006 / 25.182 |
| 未就绪 / 路径失败 | 0 / 0 | 0 / 0 |

最晚首路径延迟降低约 **86%**，同阶段总墙钟降低约 **80%**。单 Tick 的 P95 没有降低：预算相同，增强启发式和更密集的路径完成处理增加了单步工作，但需要执行的寻路 Tick 大幅减少。这里没有宣称提升 GPU FPS，也没有用总到达时间冒充 A* 计算时间。

计时排除 Unity 导入、Burst 首次编译及地图解码。独立世界预热 Burst 后，优化版重新解码地图，强制冷建地标表；计时包含这次地标构建、世界/agent 初始化及首路径等待阶段全部仿真。`ReadyTick` 为请求完成时的零起始 Tick，因此与执行 Tick 数相差 1。墙钟没有限帧或实际渲染，交互模式还受渲染与丢弃追赶 Tick 影响。

## 验证与下一轮

最终 20 项 `Phase3VolumeTests` 通过：[EditMode-Closeout.xml](Verification/Phase3/EditMode-Closeout.xml)。覆盖 ALT 与独立 Dijkstra 成本、Reference/Burst 对照、预算、重复请求/拥堵代价、断连、粗细图回退与独立段检查。此前全套 109 项运行中 108 项通过，唯一失败是新测试错误地把粗图 BVH 传给细图回退；修正测试接线后该测试连同全部 Phase 3 用例重跑通过。旧阶段的 89 项回归已通过，原始记录保留在 [初轮 XML](Verification/Phase3/EditMode-Closeout-Initial.xml)。

运行矩阵三档均全员当前到达，未残留未就绪路径，core 主线程测得 GC 分配为 0；原始 [矩阵与每 agent 时间](Verification/Phase3/CloseoutMatrix/matrix.txt) 同目录保留。

| agent | 最终到达 Tick | 仿真秒 | 无渲染墙钟秒 | 全程 Tick P95 ms | 抽检碰撞对 |
|---|---:|---:|---:|---:|---:|
| 16 | 1995 | 66.500 | 0.421 | 0.174 | 0 |
| 256 | 2358 | 78.600 | 6.090 | 3.529 | 0 |
| 1024 | 2518 | 83.933 | 17.851 | 6.989 | 0 |

该矩阵墙钟从世界初始化完成后计时，包含质量观察，不含渲染。16 档每 Tick 独立检查，256/1024 每 30 Tick 抽检；不能视为对大规模全部轨迹逐步穷举检查。生产用连续扫掠安全证书始终执行。增强启发式会改变加权 A* 的路线与交通时序，不保证每个 agent 的行程都缩短，也不宣称任意场景都能最终到达。

实际图形 PlayMode 的 **2 项测试通过**：[PlayMode-Closeout.xml](Verification/Phase3/PlayMode-Closeout.xml)，Direct3D 11。覆盖到达/未到达的静止球体对照、目标变化恢复原色、Reset，以及持续跟随/相机偏移和 Fit。已查看实际 [终点标识截图](Verification/Phase3/Presentation/arrived.png)：左边白色深腰带为到达，右边彩色同样静止但尚未到达。此项不测稳定态渲染 FPS。

Phase 4 的最小范围与只读边界见 [渲染专题规划](PHASE4_PRESENTATION.md)；本次未增加 GPU/VAT/海洋实现。

重跑入口：`Tools > RVO > Profile Phase 3 startup (1024 agents)`、`Tools > RVO > Run Phase 3 Matrix (60s wall cap per tier)`。批处理使用 `--burst-force-sync-compilation`，测试分别选择 `Rvo.Tests.Phase3VolumeTests` 与 `Rvo.Tests.VolumePresentationTests`；渲染测试需启用图形设备。
