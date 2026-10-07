# P1.1–P1.3 交付与验证记录

> 历史专题记录：下文的默认参数、实现状态和测试数字对应文中日期及当时版本。当前系统结构与运行配置见 [ARCHITECTURE.md](ARCHITECTURE.md)，最新主场景证据见 [2048 多通路报告](VERIFICATION_REEF_NETWORK2048.md)。

范围：Unity 6000.0.63f1，float3 存储，XZ 圆盘，主线程同步参考实现；None / VO 与 BruteForce 已完成。RVO / ORCA / SpatialHash / Jobs / 正式压测不在本轮完成范围。

## 实现说明

- **P1.1**：6 类可复现场景、直达目标期望速度、无避障对照、固定步长积分、到达减速、暂停/单步/重置、动态 Mesh 圆盘与场景切换。到达 Agent 仍留在世界中参与查询。
- **P1.2**：暴力扫描距离范围，按距离平方和稳定 ID 原位插入最近 K 结果，包含距离边界，排除自身，输出被截断数量；常规 Tick 不创建托管邻居集合。
- **P1.3**：解析碰撞时间几何与有限时域 VO、速度圆盘采样、对安全候选最小化期望速度偏差；无安全采样候选时最小化碰撞时间风险，再比较速度偏差。初始重叠采用分离需求回退，完全重合用稳定 ID 选择相反方向。
- **可视化/质量**：上一步快照的速度空间、邻居连线、当前/期望/选定速度；独立 O(N²) pair 检测，包括一步内扫掠碰撞，不依赖 K 截断后的避障邻居。

关键中文注释集中在同一步快照、双缓冲提交、插入排序、碰撞方程、重叠回退、配置值快照和资源释放边界。没有为单个 Agent 创建 MonoBehaviour，也未引入 ECS、物理碰撞修正或额外服务管理层。

## 自动化结果

测试日期：2026-09-27。隔离项目位于 `Temp/RvoPhase13Validation`，使用现有本地包缓存；未关闭或驱动用户已打开的 Unity 主实例。

- **EditMode：36 / 36 通过。** 覆盖配置/生命周期、解析碰撞时间、有限时域、邻居最近 K 与独立排序对照、目标到达、平面与速度不变量、6 类场景复现、非法初态拒绝、异质半径/速度、重叠恢复、扫掠检测、固定时钟、None/VO 对向对照、演示资产和群体调查。
- **PlayMode：1 / 1 通过。** 覆盖暂停、单步、恢复、重复重置、停用释放、重新启用、算法切换不修改配置资产、Mesh 顶点数和真实像素检查。
- Runtime、Editor、EditMode、PlayMode 四个程序集均编译成功。
- 实际渲染在 Direct3D 11 下验证，包含内置管线修复验证和项目的 **URP 17.0.4 / GraphicsSettings / QualitySettings**；最终结果以 URP 为准。
- 未进行 Player 打包、多平台验证、长期内存压力或 1k/10k 性能测试。PlayMode 的资源验证覆盖多次重置和停启，不代表长期泄漏分析。

原始文件：[EditMode XML](Verification/Phase13_EditMode.xml)、[PlayMode XML](Verification/Phase13_PlayMode.xml)、[实际 URP 渲染图](Verification/Phase13_URP.png)。旧 `FrameworkTests.xml` 是 P1.0 历史结果，不能当本轮算法测试。

## 质量调查：正确记录 VO 的局限

固定 dt = 1/30 秒，seed = 1，最大速度 2，半径 0.35，VO 72 个角度 × 5 个速度环，额外安全距离 0.02，time horizon = 5 秒，邻居距离 30，K = N−1（N=1 时为 1）。

| 场景 | Agent | 步数 | 到达数 | 发生扫掠碰撞的步数 | 出现回退的步数 | 最小离散表面间隙 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| HeadOn / None | 2 | 600 | 2 | 12 | — | — |
| HeadOn / VO | 2 | 600 | 2 | 0 | — | — |
| Crossing / VO | 4 | 900 | 4 | 0 | 0 | 0.021737 |
| CircleSwap / VO | 16 | 900 | 16 | 16 | 33 | -0.009249 |
| OpposingGroups / VO | 32 | 900 | 30 | 197 | 273 | -0.157276 |
| RandomCrowd / VO | 32 | 900 | 32 | 0 | 4 | 0.008106 |

“碰撞步数”是存在至少一个碰撞 pair 的 Tick 数，不是独立碰撞事件数；HeadOn 两 Agent 的 pair-ticks 与碰撞步数一致。“最小间隙”是离散 Tick 末尾间隙，不代替扫掠判定；负值表示重叠。“—”为该对照未汇总的字段，不能解读为零。

群体调查测试断言数值有限、速度上界和 XZ 不变量；测试通过**不表示这些场景无碰撞**。CircleSwap 和 OpposingGroups 的穿透及未到达均如实保留。VO 把邻居速度视为不变，而同一群体中的邻居也在调整速度；有限采样和不可行局面也会触发回退。本轮不通过位置投影、隐形 Collider 或改名为 RVO 来掩盖这些结果。

下一步 P1.4 用完全相同输入实现并比较 RVO 的互惠行为，然后在 P1.5 对照 ORCA。增加采样分辨率只提高速度搜索精度，不能等价替代 RVO/ORCA，也不能保证修复群体死锁。

## 手动验收步骤

1. 打开 `Assets/RVO/Demo/Phase13_Demo.unity`，点击 Play。
2. 默认 HeadOn / VO：应看见两个有色圆盘完成交换；暂停后 Step 只加一个 Tick。600 Tick 后应到达 2 个、Collision ticks = 0。
3. 点击 `None (reset)`：按同一初态重新运行。600 Tick 内应有 Collision ticks > 0，证明诊断不是总显示零。
4. 点击 `02_Single_None`：单 Agent 到达并停止。暂停后修改 Profile 的 PlaneHeight，Reset，再检查位置 y 与速度 y。
5. 依次运行另外四个 VO 场景。以固定 Tick 数比较上表，不以渲染经过多少秒比较；若要精确停在 900 Tick，用自动测试。
6. 选择 Hierarchy 的 `RVO Simulation`，切到 Scene 并打开 Gizmos。暂停后选择 Selected Agent 查看速度空间：红点为禁区、青色为输入速度、绿色为期望速度、黄色为解。
7. 将 MaxNeighbors 降到 1 并 Reset，检查 Truncated agents；恢复时用原配置值。运行中对配置资产的修改会保存到资产，建议先复制预设再调参；HUD 的算法按钮不改资产。
8. 多次 Reset、禁用/启用入口对象，再退出 Play；Console 不应出现 NativeArray 未释放、重复释放或越界异常。
9. 打开 `Window > General > Test Runner`，EditMode 运行 `Rvo.Tests.EditMode`，PlayMode 运行 `Rvo.Tests.PlayMode`。完整记录包含 36 个 EditMode 用例和 1 个 PlayMode 用例。

如果使用生成菜单而当前是未命名场景，先保存该场景；已交付的 Demo 不需重新生成。当前 100 / 1k / 10k 压测预设仍选择未来 ORCA / SpatialHash，直接运行会明确提示尚未实现，这是预期行为。

## 后续导航方向

已把路线改为 **单层体素导航 → 完整三维体素导航**。Phase 2 开始设计体素占据、净空、连通性、A* 和路径跟随，独立输出 Preferred Velocity；局部避障保持当前接口。Phase 4 再细化稀疏化、流式/局部更新和完整导航体工具链，本轮未提前实现。
