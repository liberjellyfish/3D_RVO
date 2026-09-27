# Phase 1 工程交付与验证记录

日期：2026-09-27（Asia/Shanghai）。本轮完成 P1.4 RVO、P1.5 ORCA 2D、P1.6 Spatial Hash，以及小规模验证、Jobs/Burst、诊断与报告工具。**正式 100 / 1k / 10k 压测没有执行；Phase 1 整体规模验收和冻结未完成。** Phase 2 体素导航没有提前实现。

## 1. 实现内容

- **真正 RVO**：候选 v 对应相对速度 `2v-v_self-v_other`，由原始有限时域 VO 判断；保留 VO 对照。没有对最终输出做平均。共用候选评估、重叠风险回退、速度圆采样与持久方向缓存。
- **ORCA**：截断圆/锥腿约束、各半责任、最大速度圆内增量优化；接触/重叠使用 dt，完全重合按稳定 ID 选相反方向。不可行时统一松弛所有单位法线约束，以 24 次二分近似最小最大违反量，返回 Infeasible。非法输入显式返回 InvalidInput。
- **Spatial Hash**：完整 int2 key、负坐标 floor、多格枚举、精确距离过滤、最近 K 稳定排序与截断计数；桶占用/候选数可观测。过多空格时精确全扫描，未改变查询语义。BruteForce 始终保留。
- **性能工程**：Reference 与 JobsBurst 可切换；Preferred、查询、求解、积分使用具体 Job；Hash 建桶串行 Burst。N*K 约束和方向缓冲持久复用；优化圆盘方向缓存和每渲染帧一次 Mesh 上传。主线程仿真稳态分配实测为零。
- **诊断**：独立全 pair 扫掠碰撞、重叠、gap、到达、速度变化、持续低速、Fallback/Infeasible/InvalidInput、截断、候选、桶占用和原始 ORCA 约束违反量。原始约束对 Scene 图开放借用，避免重建几何的跨后端数值差异。
- **工具**：显式 BenchmarkRunner、预热/重复、总计时与带同步的阶段计时、CSV/JSON、机器/配置/代码指纹、P50/P95/P99。演示支持四算法、两查询、两后端和偏好切换。三档压测配置已接上真实模块，但没有调用执行。

## 2. 自动化和运行环境

- **EditMode：56/56 通过，0 失败/跳过**。包含 200 组独立边界枚举优化对照、96 组 Hash/BruteForce 属性组合（后端×cell×range×seed）、几何责任方向、圆/腿、并行/冲突约束、重叠/退化、速度上限、异质参数、同 tick 参考/Jobs 轨迹与 Scheduled 释放，以及原有运动/场景/生命周期测试。
- **PlayMode：1/1 通过**。覆盖暂停/单步、三种避障切换、Jobs/Hash/bias 切换、反复重置/停启、共享 Profile 不变、Mesh 和真实像素。
- Unity **6000.0.63f1**，Mathematics **1.3.2**，Collections **2.6.2**，Burst **1.8.25**，URP **17.0.4**。EditMode 用 Null 图形设备，PlayMode 强制 D3D11，实际图像已检查。
- 隔离验证项目为 `Temp/RvoPhase13Validation`，复用本地包缓存；没有关闭/驱动主 Unity 实例。最终副本与全部交付 C# 文件逐字节一致。
- 质量矩阵共 **48 组**，性能配置 **20 组**。最大规模 **64 Agent**，不属于正式档位。
- 初始化/首次调度有持久 Native 分配；零 GC 结论仅指预热后的 World.Step 主线程。未做 Player 构建、跨平台、长期泄漏/内存峰值或正式端到端性能测量。

原始资料：[EditMode XML](Verification/Phase1_EditMode.xml)、[PlayMode XML](Verification/Phase1_PlayMode.xml)、[EditMode 日志](Verification/Phase1_EditMode.log)、[PlayMode 日志](Verification/Phase1_PlayMode.log)、[URP 渲染图](Verification/Phase1_URP.png)、[源码 SHA256](Verification/Phase1/source-sha256.txt)。每个质量/性能目录均含 `ticks.csv` 和 `summary.json`。

## 3. 相同场景质量矩阵

所有行：seed=1，dt=1/30 s，900 Tick（30 s），horizon=5 s，radius=0.35，maxSpeed=2，margin=0.02，VO/RVO=72角×5环，neighborDistance=30，K=max(1,N−1)，JobsBurst + SpatialHash。Extent 依次为 3/6/8/10/12/10。质量检测每 Tick 完整检查所有 pair；没有依赖被截断的邻居列表。

`碰撞 Tick` 是至少一个扫掠碰撞 pair 的 Tick 数，不是独立事件数。`停滞` 为最终未到达且连续 2 s 速度 <0.05 m/s 的 Agent 数；未到达不等于停滞。`|Δv|` 是每步每 Agent 平均速度变化（m/s），包含启动/刹车/已到达时间，不是频域抖动指标。`不可行` 是 Agent-Tick 总数。

### bias=0：保留原始对称行为

| 场景 | N | 算法 | 到达 | 碰撞 Tick | 最小 gap | 平均 \|Δv\| | 最终停滞 | 不可行 |
|---|---:|---|---:|---:|---:|---:|---:|---:|
| SingleAgent | 1 | None | 1 | 0 | 无 pair | 0.004444 | 0 | 0 |
| SingleAgent | 1 | VO | 1 | 0 | 无 pair | 0.004444 | 0 | 0 |
| SingleAgent | 1 | RVO | 1 | 0 | 无 pair | 0.004444 | 0 | 0 |
| SingleAgent | 1 | ORCA | 1 | 0 | 无 pair | 0.004444 | 0 | 0 |
| HeadOnPair | 2 | None | 2 | 12 | -0.699994 | 0.004444 | 0 | 0 |
| HeadOnPair | 2 | VO | 2 | 0 | 0.025302 | 0.039441 | 0 | 0 |
| HeadOnPair | 2 | RVO | 2 | 0 | 0.029092 | 0.004773 | 0 | 0 |
| HeadOnPair | 2 | ORCA | 0 | 0 | 0.047404 | 0.002504 | 2 | 0 |
| Crossing | 4 | None | 4 | 16 | -0.699996 | 0.004444 | 0 | 0 |
| Crossing | 4 | VO | 4 | 0 | 0.021115 | 0.054536 | 0 | 0 |
| Crossing | 4 | RVO | 4 | 0 | 0.020775 | 0.005227 | 0 | 0 |
| Crossing | 4 | ORCA | 0 | 0 | 0.037154 | 0.002260 | 4 | 0 |
| CircleSwap | 16 | None | 16 | 54 | -0.700000 | 0.004444 | 0 | 0 |
| CircleSwap | 16 | VO | 16 | 0 | 0.014378 | 0.153699 | 0 | 0 |
| CircleSwap | 16 | RVO | 16 | 0 | 0.020190 | 0.011308 | 0 | 0 |
| CircleSwap | 16 | ORCA | 0 | 0 | 0.027730 | 0.003620 | 16 | 0 |
| OpposingGroups | 32 | None | 32 | 263 | -0.698926 | 0.004444 | 0 | 0 |
| OpposingGroups | 32 | VO | 32 | 60 | -0.072549 | 0.449494 | 0 | 0 |
| OpposingGroups | 32 | RVO | 22 | 0 | 0.014289 | 0.340944 | 0 | 0 |
| OpposingGroups | 32 | ORCA | 29 | 0 | 0.019621 | 0.012072 | 0 | 3944 |
| RandomCrowd | 32 | None | 32 | 183 | -0.692447 | 0.004306 | 0 | 0 |
| RandomCrowd | 32 | VO | 32 | 4 | -0.005983 | 0.072919 | 0 | 0 |
| RandomCrowd | 32 | RVO | 32 | 0 | 0.020368 | 0.065300 | 0 | 0 |
| RandomCrowd | 32 | ORCA | 32 | 0 | 0.020001 | 0.009784 | 0 | 81 |

### bias=0.05：相同求解前方向偏好

该偏好对所有算法同样施加，不在求解后改变速度。它不是完整死锁消解：ORCA 对向两 Agent 可到达，但 Crossing/CircleSwap 仍停滞。VO 的碰撞可能增加，因此不能把它说成普遍质量改善。

| 场景 | 算法 | 到达 | 碰撞 Tick | 平均 \|Δv\| | 最终停滞 | 不可行 |
|---|---|---:|---:|---:|---:|---:|
| HeadOnPair | VO | 2 | 0 | 0.020408 | 0 | 0 |
| HeadOnPair | RVO | 2 | 0 | 0.005157 | 0 | 0 |
| HeadOnPair | ORCA | 2 | 0 | 0.005283 | 0 | 0 |
| Crossing | VO | 4 | 0 | 0.035542 | 0 | 0 |
| Crossing | RVO | 4 | 0 | 0.005214 | 0 | 0 |
| Crossing | ORCA | 0 | 0 | 0.002260 | 4 | 0 |
| CircleSwap | VO | 16 | 0 | 0.159128 | 0 | 0 |
| CircleSwap | RVO | 16 | 0 | 0.007340 | 0 | 0 |
| CircleSwap | ORCA | 0 | 0 | 0.003620 | 16 | 0 |
| OpposingGroups | VO | 29 | 76 | 0.343057 | 3 | 0 |
| OpposingGroups | RVO | 23 | 0 | 0.247776 | 0 | 0 |
| OpposingGroups | ORCA | 30 | 0 | 0.012695 | 0 | 3570 |
| RandomCrowd | VO | 32 | 16 | 0.065777 | 0 | 0 |
| RandomCrowd | RVO | 32 | 0 | 0.056161 | 0 | 0 |
| RandomCrowd | ORCA | 32 | 0 | 0.010162 | 0 | 145 |

**结果解释**：RVO 在本次矩阵中碰撞数为零且对称相遇速度变化小于 VO，但 32 对向群体仅到达 22/23 个；仍存在较大的速度变化和持续运动却无法到达。ORCA 全矩阵未检测到碰撞，成功解的最大实际约束违反量为 **9.84966755e-06**；稠密场景仍有大量 Infeasible Agent-Tick，回退不提供安全保证。完全对称 Crossing/CircleSwap 中的低抖动主要来自停住，不能当成高质量通过。

初始完全重合的专用测试在两后端均恢复间距，并逐步核对 `p_next=p+v*dt`；第一步明确为 Infeasible。此场景不混入上面的正常初态碰撞统计。

修复过的诊断问题：原先在主线程按输入快照重建 Burst 的约束，锥腿临界分支/浮点差异导致约 5.2e-4 的假残差。现直接读取求解时保存的原始约束；几何本身另由解析测试验证。没有通过提高容差或移除碰撞检测使测试通过。

采样方向缓存与 Burst 改变浮点运算次序，VO 当前轨迹与历史 P1.3 调查不同。应使用本报告内相同输入比较，不拿历史数值与当前 ORCA 做直接排名。

## 4. 小规模性能

机器：**12th Gen Intel(R) Core(TM) i7-12650H**，RAM 16115 MB，Job workers 15。Windows Editor，Burst 启用并强制同步编译，容器安全检查启用。未做 Player / FPS 承诺。质量检查、渲染和报告写盘不计入 World.Step；系统噪声与主编辑器仍可能影响 wall time。

32 Agent：OpposingGroups、Extent=12、bias=0.05、BruteForce、K=31、range=30，预热90、测量180、重复3。以下总耗时关闭阶段同步（540 样本）：

| 算法 | 后端 | P50 ms | P95 ms | P99 ms | 最大稳态 Step GC bytes |
|---|---|---:|---:|---:|---:|
| VO | Reference | 19.5568 | 26.0546 | 26.6861 | 0 |
| VO | JobsBurst | 0.8773 | 1.0280 | 1.1480 | 0 |
| RVO | Reference | 26.3923 | 30.2048 | 31.1291 | 0 |
| RVO | JobsBurst | 0.8548 | 1.0110 | 1.2550 | 0 |
| ORCA | Reference | 1.0428 | 1.3903 | 1.5229 | 0 |
| ORCA | JobsBurst | 0.1242 | 0.1565 | 0.1722 | 0 |

打开阶段同步的另一轮采样显示，Reference 的 VO/RVO 求解分别约 19.75/24.87 ms，占主要开销；JobsBurst 降至约 0.79/0.82 ms。ORCA 求解约从 0.70 降至 0.077 ms。三组使用相同采样分辨率/K/范围，没有为提速降低它们。阶段计时增加 Complete 次数，不能把它的总时间与正常流水直接混用。

跨后端不承诺长期逐位一致。32 Agent 测量窗口中，VO 参考/Jobs 的碰撞 Tick 为 216/210（3次累计）；RVO 和 ORCA 两后端均为零。ORCA 不可行 Agent-Tick 为 5625/5913；RVO 到达均为2，VO为3/2。8 Agent 240 Tick 的直接轨迹对照误差限为0.005，但复杂群体会放大浮点分支差异；优化未被描述成完全轨迹等价。

64 Agent 查询测试：RandomCrowd，稀疏 Extent=30 / 密集 Extent=5，range=4、cell=2、K=12、horizon=0.5，预热20、测量120、重复3，阶段同步开启：

| 分布 | 后端 | 查询 | 平均查询 ms | 平均候选/Agent | 最大自身桶占用 | 截断 Agent-Tick |
|---|---|---|---:|---:|---:|---:|
| 稀疏 | Reference | BruteForce | 0.1412 | 63.00 | 0 | 0 |
| 稀疏 | Reference | SpatialHash | 0.0602 | 1.32 | 2 | 0 |
| 稀疏 | JobsBurst | BruteForce | 0.0232 | 63.00 | 0 | 0 |
| 稀疏 | JobsBurst | SpatialHash | 0.0269 | 1.32 | 2 | 0 |
| 密集 | Reference | BruteForce | 0.4366 | 63.00 | 0 | 21957 |
| 密集 | Reference | SpatialHash | 0.4868 | 45.64 | 8 | 21957 |
| 密集 | JobsBurst | BruteForce | 0.0764 | 63.00 | 0 | 21960 |
| 密集 | JobsBurst | SpatialHash | 0.0757 | 45.65 | 8 | 21960 |

Hash 在稀疏参考后端减少候选并有实际收益；小 N 下 Burst 暴力扫描已经很便宜，Hash 建桶/查询开销可能抵消收益，密集也可能更慢。不能外推 10k 结果。该64 Agent组扫掠碰撞为0，但密集K截断大量发生，仍不构成一般安全保证；0.5 s horizon 仅为这组查询/性能配置，未用于上面的主质量矩阵。

## 5. 尚存限制与后续边界

1. ORCA 是局部速度约束，不会自动解除多方对称停滞；RVO 采样也仍有抖动/未到达。到达 Agent 保持存在，目标阻挡没有导航解决方案。
2. 不可行、初态重叠、感知距离不足、邻居截断/非互惠、浮点误差均破坏安全前提。算法无加速度/转向约束，不能在求解后插值速度并继续沿用安全结论。
3. 三档压测配置 range=10/horizon=5 等原参数仅为开发预设；报告会标记其是否满足保守邻居覆盖距离。没有证明它们是最优/足够参数。
4. 全 pair 质量检测为 O(N²)，不适合直接宣称 10k 端到端开销已优化。长时报告还需持续活动目标策略，避免测到大量已经静止的 Agent。
5. Native 有效载荷估算不含 allocator/hash 内部开销，不是峰值；长期内存分析、Player、多平台、正式100/1k/10k、端到端渲染测量仍待完成。
6. Phase 2 只维护 Preferred/空间查询/约束源的独立边界，没有实现单层或三维体素、A*、静态墙约束。

## 6. 可照做的验证

按 [README 的10步](README.md#unity-操作步骤) 操作即可复核。本轮报告自动测试可从 Test Runner 运行全部 EditMode 和 PlayMode 生成；不要点三档配置的显式 Benchmark 菜单。

建议人工顺序：HeadOn/None（碰撞对照）→ VO → RVO → ORCA/bias0（停滞）→ ORCA/bias0.05（到达）；再切 Crossing、CircleSwap、OpposingGroups 和 RandomCrowd，分别观察碰撞与未到达。用相同 Tick 比较，注意切换场景会恢复该资产默认值。暂停选择 Agent，检查紫色 ORCA 边界可行侧和状态；多次 Reset/停启后检查 Console。

算法参考：[RVO 原始项目](https://gamma-web.iacs.umd.edu/RVO/)、[ORCA 原始项目](https://gamma-web.iacs.umd.edu/ORCA/)、[RVO2](https://github.com/snape/RVO2/blob/main/src/Agent.cc)。本实现的共同松弛回退与 RVO2 LP3 不同，独立边界枚举验证的是可行凸优化部分，不宣称已完成官方 RVO2 全轨迹一致性认证。

