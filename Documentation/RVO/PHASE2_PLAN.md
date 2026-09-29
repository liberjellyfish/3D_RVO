# Phase 2：二维导航与避障（收尾基线）

阶段决定（2026-09-29）：用户完成后续人工验证并接受 2D 收尾；256 agent 约 1–2 分钟、1024 agent 约 20 分钟整体完成，后者仍有高频路径长时间滞留。保留当前实现与限制，下一步按 [PHASE3_PLAN.md](PHASE3_PLAN.md) 进入真实 XYZ 导航。本次只更新规划，不修改代码或新增验证结果。

本轮新增：`TrafficRecovery` 通行优先级/主动退让、拥堵区域临时代价、失败请求轮转重试、安全余量回归，以及独立目标十字与静态显示缓存。配置、算法边界和验证步骤见 [PHASE2_TRAFFIC.md](PHASE2_TRAFFIC.md)。下文保留基础搜索/查询/安全证书设计，动态约束的等分责任在启用协调时改为互补优先级比例。

更新：2026-09-29。以当前 512×512 离线烘焙地图为准；旧版运行时随机刷新已移除。历史首版测试见 [VERIFICATION_P2.md](VERIFICATION_P2.md)，本轮证据见 [VERIFICATION_P2_OPTIMIZATION.md](VERIFICATION_P2_OPTIMIZATION.md)。

## 地图与共享索引

- `NavigationGrid` 保存占据、保守方形净空、八邻接边、连通域、最大连通域出生格和合并矩形 BVH。运行时加载带签名/校验的二进制烘焙，修改障碍、半径或安全余量必须重烘焙。
- 运行期间地图不可变。地标和直线跳跃索引首次创建导航世界时计算一次，与地图实例共享；Reset/切换档位复用。当前索引没有写入二进制格式，兼容已有 `.bytes`。
- ALT：四次 Dijkstra，farthest-point 选择地标，取 octile 与地标距离差下界的最大值；非最大连通域退回 octile。距离差留出浮点余量。
- 跳跃索引连接同一邻接掩码区间的直线端点，并允许在目标行/列转向。**保留所有原始八邻接边**，不采用 JPS 的对称剪枝，因此半径净空、禁止穿角与可达性语义不变。它是附加捷径，不宣称实现完整 JPS。
- 512² 地图的地标距离约 4 MiB、跳跃表约 8 MiB（不含数组头）；`RuntimeIndexBytes` 单独统计。首次构建的启动成本必须与稳态耗时分开。

## 寻路调度与路径跟随

`GridNavigation → GridPathfinder → preferred`：

1. 每个新请求先检查连续可见性；能直达目标的 agent 当 Tick 得到路径，不受其他复杂搜索的队首阻塞。
2. 默认四个独立搜索上下文，轮转推进，每次最多 256 个扩展；所有上下文共用 `PathExpansionsPerTick`（默认 4096）。它是主线程分时处理，不是四个并行 Job。
3. 每 Tick 最多完成 `PathRequestsPerTick`（默认 16）个排队搜索；直达路径不消耗这个额度。预算是节点数/请求数，不是硬毫秒预算；平滑、直达检查和跟随也有成本。
4. `PathHeuristicWeight=0` 的旧资产使用有效默认 1.5；设为 1 可作精确图最短路对照，范围 1..2。更优 g 会重开节点；理想算术下权重 w 对未平滑图路径有 w 倍代价上界。它不代表连续空间路径最优，也不保证每条实际路线都更短。
5. 地标只改善下界，捷径只添加等价图边。原 `Find` 保留不使用地标/捷径的精确 A*，与独立 Dijkstra 交叉检查。
6. Agent ID / request ID / map version / goal / status / cursor 仍与路径绑定；目标变化取消旧搜索并更新排队请求，旧目标结果不能写回。
7. 路径数组按实际长度从 `ArrayPool` 租用；四个搜索的 scratch 在 512² 时约 28 MiB，另有共享路径输出 scratch 约 2 MiB。避免为每个 agent 分配整张地图搜索状态。
8. 每 Tick 验证当前 waypoint 可见性；近点前进仍验证下一段。远点前瞻按 agent 错峰每四 Tick 做一次，同手性偏好若指向障碍则退回路径方向。最终速度仍由避障器决定。

Pending 表示预算内尚未得到完整路径，不等同于 NoPath。有限预算不承诺 1024 个复杂请求第一帧全部完成。Arrived 仍参与互惠避障，可能暂时让开目标。

## 动态查询

`NeighborSearchFactory` 支持 BruteForce、SpatialHash、KdTree，枚举旧值不变，新增 KdTree=2。HUD 可循环切换，所有模式支持 Reference/JobsBurst。

KDTree 每 Tick 从同步快照重建平衡树，以最长轴作中位划分；扁平数组、最多八点叶子、escape 索引无递归遍历。范围 AABB 剔除；完全在查询圆内且全部远于已选 K 的子树整体计数。保留精确 `(距离平方, 稳定 ID)` 排序、半径内总数和 DroppedCounts。BucketOccupancy 对 KDTree 为 0；CandidateCounts 统计实际逐点检测，整体计数的点不算逐点候选。

查询不与导航格共用索引。空间哈希仍是默认；KDTree 需要建树成本，不能预设在所有密度/规模下更快。两者的最坏情况仍可能退化，有限 K 仍会遗漏动态约束。

## 避障与局部安全证书

- 静态 BVH 提供方块支撑平面与地图边界，动态邻居提供 ORCA 平面。静态预算保留最近平面，而非 BVH 遍历中遇到的前 K 个。
- 求解器保留静态约束为硬约束；动态冲突无解时仅对动态平面求共同松弛。Phase 1 默认仍沿用所有约束共同松弛；没有将此回退称为 RVO2 LP3。
- 每 Tick 用解析首次接触时间检查动态 pair，用 BVH 检查静态整段运动。JobsBurst 的安全桶范围由最大半径、速度和 dt 推导；Reference 仍遍历全部 pair 作对照。安全查询不受 ORCA 邻居 K/距离截断影响。
- 修复接触余量内静止或分离也返回零的错误：相对位移投影不接近时允许运动；物理重叠初态仍拒绝提交。
- 触发证书时，根据 `r_i+r_j+(speed_i+speed_j)*dt+0.002` 的可达范围构建局部相互作用连通分量。分量内取最小安全时间前缀，统一缩放；分量间在任意 0..1 缩放下不能接触，因此互不拖停。
- 不使用逐 agent 独立停车；安全图包含潜在相互作用，而不仅仅包含当前检测到的碰撞边。它可能保守地合并一整片拥挤群体，但不会因为远处冲突无条件缩放全场。
- `LastSafetyScale` 现在是全场最小分量比例；`LastLimitedAgents` 表示实际被限制的 agent 数。积分器不再改变最终速度。

路径可见性/跟随、组合求解、静态约束、动态查询、扫掠及积分已支持 Burst；A* 搜索、请求与路径缓冲管理仍在主线程。GridPathFollower 每个 agent 只复制至多五个拐点到持久 Native 缓冲，在 Job 中检查可见性并输出游标增量；失效路径在完成边界请求重规划。安全层不是原始 ORCA 的证明，而是本项目瞬时变速模型下额外的同步运动证书。

## 剩余边界和 3D 路线

静态方块仍采用保守方形膨胀，支撑平面不是完整 RVO2 墙段/凸角算法。静态预算截断、通道内对向、目标占道及高密度交通仍可能等待；任意场景最终到达需要优先级/预约/瓶颈交通管理，本轮不宣称解决任意死锁。

保留 float3 数据、模块生命周期、双缓冲 World 与图服务边界。ALT/时间预算可用于后续体素图；KDTree 分轴、静态 BVH、碰撞证书与 ORCA 求解器必须分别实现并重新验证 XYZ 几何，不能只把 float2 换成 float3。下一步先做 Player 性能矩阵、长时间到达/拥堵验收、路径规划 Job 化，再评估分层图与瓶颈预约。

参考：[ALT 原论文](https://www.microsoft.com/en-us/research/publication/computing-the-shortest-path-a-search-meets-graph-theory/)、[Weighted A* 实验说明](https://www.movingai.com/wastar.html)、[RVO2 KDTree](https://github.com/snape/RVO2/blob/main/src/KdTree.cc)、[RVO2 Agent/ORCA](https://github.com/snape/RVO2/blob/main/src/Agent.cc)。本项目实现与回退策略独立，不宣称与 RVO2 逐位等价。
