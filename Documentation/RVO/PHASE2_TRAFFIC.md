# Phase 2：通行协调、请求恢复与目标显示

更新：2026-09-29。本轮以实施为主，不重做完整规模基准。沿用离线地图、ALT/加权 A*、Burst 路径跟随、动态查询、ORCA 和逐 Tick 扫掠安全证书。

## 为什么不是直接把所有前方 agent 烘焙成墙

移动 agent 逐帧改图会造成反复失效，并可能将唯一窄道封成无路。每段预存备用路径也不保证备用路没有相同瓶颈，会增加内存和初始搜索成本。本轮采用局部通行协调，持续受阻时再限频增加临时路线代价；占据、连通域和烘焙签名保持不变。

依据与实现边界：

- [A* Pathfinding Project 的局部避障文档](https://arongranberg.com/astar/documentation/beta/localavoidance.html) 明确区分局部避障与全局寻路，并提供按优先级分配避让责任的规则。此处使用相同的互补比例 `otherPriority / (selfPriority + otherPriority)`；Phase 1 默认仍各承担 0.5。
- [PIBT 原作者实现](https://github.com/Kei18/pibt2) 的重点是迭代优先级和协调；[WHCA* 文档](https://w9-pathfinding.readthedocs.io/stable/mapf/WHCAStar.html) 使用有限窗口和预约表。这些方法不能不加处理就套在当前连续速度模型上。本轮不是 PIBT、WHCA* 或具有完备保证的时空预约。

## 新增行为

### 1. 局部通行与退让

`TrafficRecovery` 在 preferred 阶段工作。持续没有沿期望方向取得足够进展时累积受阻时间；恢复进展则衰减。默认阈值 1.25 秒，等待越久优先级越高，同分由稳定 Agent ID 决定。

每六 Tick（默认 dt=1/30 时 5 Hz）使用局部空间桶找阻挡关系。持续受阻、窄道对向，以及正在挡住来者的闲置 agent 可以触发协调。可见性检查排除隔墙邻居；还判断来者是否会在到达自己之前停在其独立终点，避免误驱赶已到达者。

较低优先级者获得最长八秒的退让状态，优先寻找通行带侧面的可见空位；侧面没有空间时沿获准通行者的方向后退，随后继续寻找出口。候选点检查静态净空、当前占用及局部已有退让目标，减少多个 agent 选择相同位置。已退出通行带的 agent 在退让点等待。对方通过、远离或状态超时后释放，不每帧交换左右与通行方向。

默认优先级：普通移动 1..3（含等待老化）、未取得方向者 0.5、已到达者 0.2、退让者 0.1、临时获准通行者至少 8。低优先级不代表可穿透；全部 preferred 仍经过 ORCA、静态硬约束和原扫掠安全证书，积分器不投影或传送位置。

### 2. 持续受阻后带临时代价重规划

受阻超过两倍阈值，且不在退让状态时，允许对行进方向前方半径 `4 × agentRadius` 的区域增加临时代价。区域内目标格的边代价乘 9，仍可通过；没有替代路线时不会因为这一代价被判成 NoPath。

区域默认持续八秒，每 agent 重规划默认至少间隔四秒，全场每 Tick 最多新增四个恢复请求。恢复请求轮转检查并排到已有队列尾部。每次 A* 复制区域参数，跨 Tick 搜索过程中不改变代价。

有临时代价的请求关闭长跳跃边，防止跳过中间格的代价；路径拉直及跟随器的远点前瞻不能抄近道穿过该区域。相邻 waypoint 仍可推进，因此只有一条路线时可正常穿过代价区。普通请求仍使用上一轮的 ALT/跳跃优化。

### 3. 不再永久挂在失败状态

`InvalidEndpoint` / `NoPath` 现在有约一秒加错峰的重试，最多四个/Tick，并使用轮转游标，避免高编号 agent 被低编号重复请求饿死。目标改变立即取消旧请求并清除对应恢复状态。

如果当前位置物理合法，但落在导航半径额外安全余量中，寻找附近有物理净空的可走格，产生回归该格的 preferred。只有恢复导航净空后才重新正常寻路。静态安全证书允许离开余量边界的运动，但仍验证整段物理扫掠，不能因此穿墙。

真实不可达目标仍然报告失败并限频重试，不伪造一条路径；Pending 仍可能是有限预算下的正常排队。显示 `Stalled` / `Yielding` / `Recovery replans` 辅助区分原因。

### 4. 显示与开销

- Phase 2 场景和新建 Presenter 默认 `ShowPaths=false`，`ShowGoals=true`。HUD 中二者独立切换。
- 终点是按半径缩放的实心十字：白描边、深色饱和内芯，颜色仍按稳定 ID 对应 agent；十字高于圆盘，圆盘到达后仍可看到覆盖在其上的标记。
- 十字用三角形几何实现宽度，避免依赖单像素线宽。在隐藏路径且目标未改变时，网格和目标 Mesh 缓存复用，不再每帧清空、重算边界和上传。
- 协调使用持久数组与复用空间桶，只每六 Tick 搜索局部候选；没有每帧全 pair 拥堵扫描。恢复搜索仍使用原全局节点预算。
- 本轮未量化新的 FPS 或大规模毫秒收益；极密集桶和频繁恢复请求仍会增加开销，不能把实现优化当作已测性能结论。

## 配置与回退对照

`NavigationSettings.DisableTrafficRecovery=true` 可关闭通行协调和拥堵代价重规划，保留失败重试、净空恢复和显示改进，用于同 seed 对比。`StallSeconds=0` 使用 1.25 秒，`RecoveryCooldownSeconds=0` 使用四秒；非零可指定正值。这些配置不改变 BakeSignature，无需重烘焙，运行中的世界需 Reset 才读取新配置。

## 验证入口与人工步骤

新增 EditMode 类 `TrafficRecoveryTests`：临时代价绕行、互补责任、双后端单车道对向、六 agent 对向，以及双后端安全余量内的请求恢复。已有 `Phase2OptimizationTests` / `Phase2NavigationTests` 继续验证查询、路径和扫掠。

首轮 `Targeted.xml` 为 18/18 通过；扩展检查 `TargetedFinal.xml` 为 27/28，六 agent 在 1800 Tick 的全员最终到达断言失败，安全断言通过，暴露了终点误退让问题。随后增加来者终点距离判断并完成预算轮转；最终 [RecoveryFinal.xml](Verification/Phase2Traffic/RecoveryFinal.xml) **19/19 通过**，包括六 agent 全员到达和逐 Tick 动态/静态扫掠检查。保留失败报告，不将其当成通过证据。

PlayMode `NavigationDriverTests` 新增：默认隐藏路径、目标独立切换、目标几何位于 agent 平面上方。首次运行在旧的障碍颜色像素范围断言失败；保存帧后确认障碍正常绘制，原因是 Linear 项目输出到 sRGB RenderTexture 的颜色编码。测试现按实际色彩空间计算期望颜色，保留像素数量要求。最终 [PlayModeFinal.xml](Verification/Phase2Traffic/PlayModeFinal.xml) **1/1 通过**，覆盖实际渲染、切换档位、Reset、停用释放和重新启用；[Navigation.png](Verification/Phase2Traffic/Navigation.png) 为本轮截图。保留早期失败报告用于诊断。

本轮没有重跑完整测试套件或大规模性能基准，也没有证明所有拥堵布局均可解。以下人工验收仍需执行，尤其是到达后近距离十字覆盖效果和长时间高密度路口通行。

1. 打开 `Phase2_Navigation.unity`，Play，先看 16 档：没有长路径线，所有目标有深色十字；等 agent 到达后放大观察，十字应盖在圆盘上且不闪烁。切换 Paths 不应隐藏目标；切换 Goal crosses 只影响十字。
2. 固定 seed，分别选 256/1024 档。聚焦路口，观察 Stalled 上升后 Yielding 是否出现、是否有人退到侧面/后方、释放后是否恢复路线。十字与 agent 分离时，它可能正主动让路，并非已经从仿真移除。
3. 选择长时间静止者，Inspector 读取 Path 状态/请求号。Pending 应随队列推进变 Ready；失败状态请求号应限频增长。目标本身非法时不能要求最终到达，应先修正目标。
4. Test Runner 运行 `TrafficRecoveryTests` 与 `Phase2OptimizationTests`；需要完整导航回归再运行 `Phase2NavigationTests`。六 agent 测试保留严格全员到达断言，若失败，请保存 XML 中的最终位置、受阻数和恢复计数作为复现。
5. 性能对比固定地图/出生 seed、档位、后端和 Tick 数。关闭 O(N²) Quality checks 和 Paths，在 Profiler 分别观察 Preferred、Avoidance、Mesh 上传和 GC；不要比较不同数量正在运动的两帧。复制 Profile 切换 DisableTrafficRecovery，Reset 后做有/无协调对照。
6. 若要进入 3D 前验收：至少补做多入口路口、无侧袋死胡同、目标占道、半径/速度差异、不同 seed 与长时间 1024 档。记录最终到达率和最长等待时间，而不只看是否碰撞。

## 明确限制

局部退让不是完整路口预约；真正无侧袋、无退路、目标必须永久占据瓶颈或多通道循环依赖时，仍可能无法解堵。八秒租约和等待老化降低抖动，但不构成普遍无死锁证明。复杂验证如继续失败，可保留此实现并按上述步骤复现，不应仅增加运行 Tick 或弱化断言来宣称通过。

下一步 3D 可以复用请求生命周期、恢复状态、预算和数据所有权；退让方向采样、净空、空间桶、ORCA 约束和扫掠证书都必须升级为真实 XYZ 几何。建议将人工交通验收作为进入 3D 前的门槛。
