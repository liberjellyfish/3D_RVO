# Unity 6 RVO — GPU 鱼群与水下环境

已实现二维 **None / VO / RVO / ORCA、BruteForce / SpatialHash / KdTree、Reference / JobsBurst**；Phase 3 已实现真正 XYZ 体素导航、球形邻居、3D ORCA、连续扫掠安全检查和恢复。Full3D 支持 None / ORCA 与 BruteForce / SpatialHash。两个阶段共用 World / AgentStorage，不使用 Rigidbody/Collider 位置纠正。

2026-09-29：用户完成后续人工验证，反馈 256 agent 约 1–2 分钟完成，1024 agent 在高频路径长时间滞留后约 20 分钟完成。按用户决定，2D 阶段告一段落；这些是整体完成耗时的观察，不是 A* 耗时或正式性能报告。1024 的拥堵长尾保留为已知限制，不阻塞 3D 开工。

2026-10-01：增加到达标识与共享粗图 ALT 启发式，见 [Phase 3 收尾记录](VERIFICATION_P3_CLOSEOUT.md)。2026-10-03：已实现 P4.0–P4.2 的单向快照、稳定三维姿态、GPU 剔除/LOD 和间接程序化鱼绘制；交付独立 render-only / live 场景。使用与证据见 [Phase 4 基础验证](VERIFICATION_P4_FOUNDATION.md)，后续多表示动画、impostor、焦散与海洋方案见 [详细规划](PHASE4_GPU_VISUALIZATION_PLAN.md)。

P4.3 起的同源动画对照、实验卡片、共享焦散与 Render Graph 水下合成已接入。新增 Ocean / OceanLive 场景，操作和保留门槛见 [续阶段实施](PHASE4_CONTINUATION.md)，实际设备结果见 [续阶段验证](VERIFICATION_P4_CONTINUATION.md)。卡片仍默认关闭，真实美术资产与正式 GPU 性能冻结尚未完成。

2026-10-04：水体改为可见的动态表面材质，并减弱雾。先打开 `Phase4_SurfaceStudy.unity` 查看与参考图接近的灰度内箱/球体，或打开 Ocean 场景查看蓝绿版本；[修正说明及真实截图](PHASE4_SURFACE_CORRECTION.md)。

## Phase 4 操作

0. 新水下演示打开 `Assets/RVO/Demo/Phase4_Ocean.unity`；真实仿真用 `Phase4_OceanLive.unity`。菜单 `Create Phase 4 Ocean Demos` 补建；右侧 HUD 切动画、焦散与雾。旧基础场景继续作为对照。
1. 打开 `Assets/RVO/Demo/Phase4_RenderOnly.unity`：默认 1 万合成鱼，不运行导航/ORCA。Inspector 的 `FishRenderFixture` 可设 1–30000 个实例及 `Empty / DebugSpheres / GpuFish` 对照，修改后点击 HUD 的 Reset。支持暂停和固定相机环绕；右键旋转、滚轮缩放、中键平移。
2. `GpuFishRenderer` 提供 Frustum Culling、Force LOD（-1 自动）、LOD 调试色。当前 4 档均为程序化鱼（200/128/72/32 顶点），尚未接入最终 VAT / impostor。相机、Compute 和 Shader 已在场景中引用。
3. 打开 `Assets/RVO/Demo/Phase4_Live.unity`：默认 1024 个真实 Full3D Agent，沿用 Phase 3 地图、求解器和控制 HUD，通过提交事件驱动鱼群。3 万合成实例结果不能作为真实 ORCA 吞吐结论。
4. 场景缺失时使用 `Tools > RVO > Create Phase 4 GPU Fish Demos`；菜单仅补建。`Tools > RVO > Build Phase 4 Render Benchmark Player` 构建到 `Builds/Phase4/Phase4.exe`；基准启动参数和指标口径见验证文档。
5. D3D11 为本轮验证后端。设备需要 Compute / Instancing / Shader Model 4.5；不支持时使用 Phase 3 调试场景。正常渲染无 GPU 回读，验收测试和采样结束截图例外。

## Phase 3 操作

1. 打开 `Assets/RVO/Demo/Phase3_Volume.unity`，Play，选择 16 / 256 / 1024。默认地图 256³、104 个障碍、两个高低错开的穿墙口。
2. **白色球体 + 深色腰带表示当前已到达**；彩色表示尚未到达，包括寻路排队、行进、拥堵。标识与路径开关独立，离开终点后恢复原色。
3. `First route ready` 区分首次寻路积压与后续拥堵；`Pending` 也包括恢复重规划。首路径就绪不等于实际开始移动，移动仍受局部避障约束。
4. `Follow selected` 持续跟随；右键旋转、滚轮缩放、中键平移。`Fit volume` 返回全景。路径/切片/速度平面按需打开。
5. 默认每 Tick 4096 节点、16 个完成请求、两个搜索槽不变。粗图的 4 地标表在该地图首次使用时建立，Reset 复用；无须重烘焙资产。关闭粗图或精确细图对照仍可使用原几何启发式。
6. `Tools > RVO > Profile Phase 3 startup (1024 agents)` 单独测全员首次路径就绪；性能报告区分仿真秒、CPU Tick 与无渲染墙钟，不把它们当作实际 FPS。

## Phase 2 操作

1. 打开 `Assets/RVO/Demo/Phase2_Navigation.unity`，点击 Play。当前地图为 512×512，450 个随机矩形（4..16 格），Agent 半径 2.5、速度 12，档位为 16 / 256 / 1024。
2. 地图运行期间保持静态。修改 Profile 的地图、半径或安全余量后，用 `Tools > RVO > Upgrade Phase 2 Demo to 512 and Bake` 恢复标准演示并重烘焙；自定义参数用 Profile Inspector 的 Bake 操作。前者会重置演示参数，请保留自定义配置副本。
3. HUD 的档位按钮、Reset、Query、Backend 都重新创建世界，复用相同烘焙地图。Query 循环 BruteForce → SpatialHash → KdTree；默认仍为 SpatialHash，KDTree 可用于对照。切换不会写回 Profile。
4. `Pending` 是正在排队/计算路径，`Ready` 是已有可跟随路径，`No path` 是失败状态。直达目标当 Tick 启动；复杂路径默认四上下文轮转，每 Tick 4096 个节点、16 个排队请求。`PathHeuristicWeight=0` 使用默认 1.5，设为 1 作精确最短路对照。
5. `Safety min scale` 是本步最小局部退让比例，`Limited agents` 是实际受影响数量。新增 `Stalled / Yielding / Recovery replans`：窄道对向使用临时通行优先级与主动退让，持续受阻可触发带临时代价的重规划。失败请求现在限频轮转重试，安全余量内的合法 agent 可返回导航净空。
6. 默认隐藏路径，终点十字独立显示：深色内芯、白描边、高于 agent 平面，到达后仍覆盖可见。HUD 的 Paths / Goal crosses 独立切换。目标未变化且路径隐藏时复用 Mesh。大规模性能观察关闭 `Quality checks (O(N²))`；它是独立全量诊断，不是关闭生产安全检查。
7. 后续需要回归 2D 时，可运行 `TrafficRecoveryTests`、`Phase2OptimizationTests` 和 PlayMode 的 `NavigationDriverTests`；完整导航回归另有 `Phase2NavigationTests`。历史检查与用户收尾反馈见[交通改进记录](PHASE2_TRAFFIC.md)，不是本轮待执行任务。

当前交通协调、状态恢复、配置对照和人工验收见 [PHASE2_TRAFFIC.md](PHASE2_TRAFFIC.md)。基础实现与内存权衡见 [Phase 2 设计](PHASE2_PLAN.md)，上一轮证据索引见 [优化验证报告](VERIFICATION_P2_OPTIMIZATION.md)。[首版报告](VERIFICATION_P2.md) 仅作历史记录。

局部退让已实现，但任意拥堵最终到达仍无保证；完整路口/时空预约、完整墙段 ORCA 与 Player 正式性能矩阵作为历史待办保留，后续按需要开展。Phase 1 的正式 100 / 1k / 10k 验收没有被针对性检查替代，也不再作为进入 Phase 3 的前置要求。

## Unity 操作步骤

1. 用 Unity **6000.0.63f1** 打开项目，等待编译。打开 `Assets/RVO/Demo/Phase13_Demo.unity`（沿用原场景路径）。
2. 点击 Play。默认仍是双 Agent / VO，以保留旧演示对照。Game 面板可 Pause、Step、Reset；Step 精确前进一个固定 Tick。
3. 点击 None / VO / RVO / ORCA 按钮，从**同一初态**重新开始；场景按钮切换单 Agent、对向、四向交叉、16 圆周交换、32 对向群体和 32 随机群体。
4. `Query` 按钮切换 BruteForce / SpatialHash；`Backend` 切换 Reference / JobsBurst；`Side bias` 切换 0 / 0.05。均重置世界，不写回配置资产；Reset 保留覆盖值，切换场景恢复该 Profile 默认值。
5. 建议先看 HeadOn / None 的碰撞，再看 VO / RVO；ORCA 在 bias=0 时会对称停住，切换 bias=0.05 后可绕行到达。**bias=0.05 仍不能保证四向交叉/圆周场景在 900 Tick 内到达**，这属于已记录的算法限制。
6. 选择 Hierarchy 的 `RVO Simulation`，暂停后切到 Scene，开启 Gizmos，在 Inspector 设置 Selected Agent。青色为输入速度，绿色为 preferred，黄色为求解速度；红点为对应算法禁区，ORCA 紫线为真实半平面边界，箭头指向可行侧。
7. 观察 Arrived、Overlaps、Swept pairs、Collision ticks、Fallback、Infeasible、Slow、平均 |Δv|、Truncated agents 和最小 gap。负 gap 是重叠。HUD Slow 为当前低速未到达；报告 stalled 要求连续 2 秒低于 0.05 m/s。
8. 比较时固定场景、seed、Tick 数、K、感知距离和 bias，不能仅比较运行秒数或肉眼效果。关闭 Quality checks 可排除 O(N²) 全量诊断开销；再次开启后的累计数只包含开启期间。
9. 多次 Reset、禁用/启用对象，再退出 Play，Console 应无 Job 依赖、越界或 Native 容器泄漏错误。
10. `Window > General > Test Runner`：先运行 EditMode `Rvo.Tests.EditMode`，再运行 PlayMode `Rvo.Tests.PlayMode`。EditMode 自动生成 `Verification/Phase1` 小规模 CSV/JSON；PlayMode 验证真实材质像素、切换和释放，生成 `Verification/Phase1.png`。这些测试不执行 100/1k/10k 压测。

若场景丢失，可用原菜单 `Tools > RVO > Create Phase 1.3 Demo Assets` 补建；该菜单名为兼容历史保留，只补缺失资产。原 `SampleScene` 未改动。

## 配置与指标

- dt=1/30、time horizon=5、半径=0.35、最大速度=2 为演示基线；所有算法共用额外距离余量 0.02。
- VO / RVO 使用 72 个角度 × 5 个速度环，另评估 preferred、当前速度和零速度。采样近似与风险回退不提供普遍无碰撞保证。
- RVO 使用 `2*v_candidate-v_self-v_other` 检测互惠障碍；ORCA 使用半平面与速度圆的连续优化。无可行解输出有界最小最大违反量回退，并标记 Infeasible。
- PreferredSideBias 在**求解前**改变方向偏好，不对最终速度平滑。0 保留完全对称对照；它不是导航或完整死锁解决方案。
- CellSize 只影响查询成本。Hash 可能因高密度或小 N 比暴力更慢；极多空格时精确回退全扫描。K 截断和感知距离不足均会破坏互惠安全前提。
- `MeasureStages` 开启后每阶段等待完成，汇报执行+等待的 wall time，会增加同步开销；正式总耗时应关闭它另测。未测阶段汇总为 -1。
- `BenchmarkRunner.Run` 支持预热、重复、逐 Tick CSV、P50/P95/P99、质量和机器元数据。主线程 GC 不等于整个应用 GC；Native 字段是有效载荷估算，不是峰值。IMGUI 和诊断/写盘不在仿真热路径统计内。

三档 `Configurations/Phase1_*.asset` 已接好 ORCA / SpatialHash / JobsBurst。**仅保留，未运行。** `Tools > RVO > Run Selected Profile Benchmark (explicit)` 会实际执行选中配置；暂缓正式压测时不要对三档资产调用此菜单。

算法依据：[原始 RVO](https://gamma-web.iacs.umd.edu/RVO/)、[ORCA 项目与论文](https://gamma-web.iacs.umd.edu/ORCA/)、[RVO2 官方实现](https://github.com/snape/RVO2/blob/main/src/Agent.cc)。本项目优化器使用自行实现的半平面增量求解与共同松弛二分回退，不宣称轨迹或不可行回退与 RVO2 完全一致。

进一步阅读：[架构](ARCHITECTURE.md)、[路线](ROADMAP.md)、[验证规范](VALIDATION.md)。
