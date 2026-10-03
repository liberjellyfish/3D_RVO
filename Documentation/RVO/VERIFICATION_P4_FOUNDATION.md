# Phase 4.0–4.2：GPU 鱼群基础链路与验证

更新：2026-10-03。Unity 6000.0.63f1 / URP 17.0.4；本轮交付基础实现、演示场景与测量入口。后续终态设计见 [详细规划](PHASE4_GPU_VISUALIZATION_PLAN.md)。VAT/Bone、impostor、Ocean 和 P4.7 正式性能矩阵未实施。

## 1. 交付与使用

| 阶段 | 已交付 | 范围 |
|---|---|---|
| P4.0 夹具与口径 | RenderOnly / Live 场景；Empty、旧球体、GPU 鱼对照；固定 seed、相机环绕、CSV/JSON | 合成夹具最多 30000；Live 默认沿用 Phase 3 的 1024，不改变导航容量或性能结论 |
| P4.1 快照与姿态 | 提交事件、独立副本、ID/generation 历史、parallel transport、暂停/Reset/禁用释放 | 显示插值与动画只写表现层；核心不感知 GPU |
| P4.2 GPU 主链路 | 三份状态 Buffer、保守视锥球、屏幕 LOD/滞回、四桶索引与 args、间接鱼绘制 | 四档为 200/128/72/32 顶点程序化低模；近景真实资产和远景卡片尚待选择 |

- 打开 `Assets/RVO/Demo/Phase4_RenderOnly.unity` 并 Play。`FishRenderFixture` 的 Count、Mode 修改后用 HUD Reset 应用；支持 Pause 和固定相机环绕。`GpuFishRenderer` 可关闭剔除、强制 LOD（-1 为自动）或显示 LOD 颜色。
- `Assets/RVO/Demo/Phase4_Live.unity` 运行真实 Full3D 仿真；静态障碍从原烘焙地图生成可视立方体，不添加碰撞纠正。鱼的缩放和尾鳍是视觉形状，碰撞代理仍为原球体。
- 菜单 `Tools > RVO > Create Phase 4 GPU Fish Demos` 仅补建缺失场景；已有自定义场景不会重写。`Tools > RVO > Build Phase 4 Render Benchmark Player` 输出 `Builds/Phase4/Phase4.exe`，构建临时启用 Frame Timing Stats，结束恢复设置。
- 主支持 D3D11，需要 Compute、Instancing、Shader Model 4.5。不满足时 `LastError` 给出原因，手动使用 Phase 3 调试场景；当前不维护第二套自动渲染回退。D3D12 未验证。

## 2. 边界、时序与内存

`Rvo.Rendering` 单向引用 `Rvo.Runtime`。仿真只新增 `AgentSnapshot` 数据契约与 Bootstrap 提交/清空事件，未修改导航、邻居、ORCA 或积分器。Snapshot 包装借用 `AgentReadView`，不是可跨 Tick 保留的深拷贝。每次提交（包括同帧追赶的每一步）都调用 Bridge；它通过 Burst Job 在回调返回前完成副本，渲染帧不访问仿真 NativeArray。订阅异常记录到 Console，不改变已提交世界状态。

姿态以稳定 ID 查找历史，generation 或数量变化重建；零速保留方向，反向采用历史 up，正常转向用最短弧 parallel transport 并重新正交化。四元数统一半球，动画相位在两端一致换周期，幅度低通。到达标识按目标距离判断，停住不等于到达。无效位置/速度/半径标记为不可见。

`Bootstrap.Update → FishLiveBridge.LateUpdate → GpuFishRenderer.LateUpdate(100) → Recorder.LateUpdate(200)`；显示只插值前后已提交状态，最多落后一个固定 Tick，不外推物理。暂停显示最新端点。LOD 历史按 slot 存储并校验 ID/generation；slot 重排可重新选 LOD，不错误继承另一条鱼的滞回。

每条 `FishGpuData` 为 64 B：

| 字节 | C# / HLSL 字段 | 内容 |
|---|---|---|
| 0–15 | PositionRadius / float4 | 世界位置、保守视觉包围球半径 |
| 16–31 | Rotation / float4 | quaternion |
| 32–47 | Animation / float4 | 相位、振幅、频率、统一缩放 |
| 48–63 | Identity / uint4 | 稳定 ID、generation、flags、外观 seed |

CPU 持有 Current / Previous / scratch 和 ID 查找表；GPU 持有 Previous / Current / Prepared（各 64N B）、LOD history（16N B）、四份索引（共 16N B）与四个平台 args。D3D11 上 GPU Buffer 有效载荷为 `224N + 80` B：1 万约 2.14 MiB，3 万约 6.41 MiB。它不等于总 VRAM，未包含网格、驱动副本、URP RenderTexture 等。

本期每次有新提交时同时上传两个端点，未变化的渲染帧不上传；同帧多个 Tick 只上传最终端点。3 万一次上传 3.84 MB，30 Hz 连续提交理论上为 115.2 MB/s。保留这种简单实现，暂不引入上传 ring、静态/动态分拆或自定义 fence；以后只按实测热点优化。

使用同一 graphics queue 执行持久 CommandBuffer：清 append counter → Compute 插值/球体视锥/LOD → Counter Copy 到 args → `RenderMeshIndirect` 四次提交。Compute 每组 128 线程。整批 worldBounds 包含两个端点和最大变形；屏幕尺寸以球体前端深度保守估算，默认阈值 80/24/8 px、滞回 15%。Args 使用 Unity 平台结构，运行时从结构定位 instanceCount 偏移，不硬编码 5 个 uint。没有 CPU 可见数回读，也没有混用延迟 Render Graph Compute。本期限定单 Game Camera。

鱼 Shader 共用 ForwardOnly / DepthOnly / DepthNormalsOnly 的 travelling-wave 变形及导数法线修正；使用固定简易光照、种子配色和到达色。薄鳍双面，主体 opaque，无 alpha blending，无阴影与运动矢量，不声称完成最终 Lit/PBR 材质。原 PC URP 的 Forward+、SSAO、深度/颜色拷贝仍影响总帧成本。

## 3. 已运行验证

| 检查 | 实际结果 | 证据 |
|---|---|---|
| 完整 EditMode | 116/116 通过，含 7 项新表现层测试 | [EditMode.xml](Verification/Phase4/EditMode.xml) |
| 完整 PlayMode（真实 D3D11 设备） | 7/7 通过，含 3 项新 GPU/桥接测试 | [PlayMode.xml](Verification/Phase4/PlayMode.xml) |
| Windows x64 非 Development Player | 最终源码构建成功，运行时 Shader/Compute 可用 | 本地 `Logs/Phase4-Build.log`、下节 Player 数据 |
| 实际像素 | 25 条鱼产生有效像素，四桶强制档正确，相机转身后计数归零 | [鱼群截图](Verification/Phase4/fish.png)、[检查值](Verification/Phase4/gpu-debug.txt) |

新增检查具体覆盖：

- 64 B 布局、四档网格有限值与变形包围界；连续穿极点、反向、零速姿态；借用视图复制后隔离、ID 重排和换代；到达/停住区别、无效数据过滤。
- 预热后快照打包主线程托管分配为零；100 次 CPU 创建/释放；两次提交之间改变显示读取次数不改变姿态。未用实际 30/60/144 Hz Player 回放完成帧率矩阵，也不据此声称整个 Unity 帧零 GC。
- 两个真实 Full3D World 对比 60 Tick，启用 Bridge 后位置/速度与无 Bridge 基线完全相同；同帧多个提交逐一捕获，Reset/禁用正确清空。
- 30000 个可见索引唯一且不越界；混合桶无重复；与 CPU 球体视锥比较无明确内侧误剔除（平面边界保留 0.001 浮点容差）；近裁剪面相交与四档自动 LOD；100 次 GPU Clear/重建无测试日志错误。

GPU 同步回读仅出现在验收测试；Player 截图在测量完成后进行。100 次资源重建测试不是外部显存峰值/长期趋势测量。实际 args 与像素验证了主链路可用，但仍不能替代 RenderDoc/Frame Debugger 的逐 Pass 执行与硬件计数审计。

## 4. Player 短基线

设备：RTX 4060 Laptop GPU（报告显存 7957 MB）、i7-12650H、Windows x64 非 Development Player、D3D11、1920×1080、PC_RPAsset。默认固定相机、自动 LOD、视锥剔除开启；每档仅 1 次、预热 120 帧、采样 600 帧。实际使用显式 SRP 离屏路径，不包含窗口呈现；属于运行与计数口径的短测，不是正式性能验收。

| 模式 | 数量 | CPU 循环 frame_ms P50 / P95 / P99 | 采样秒数 | 采样期丢 Tick 增量 | 自有 GPU Buffer |
|---|---:|---:|---:|---:|---:|
| Empty | 10000 | 0.315 / 1.555 / 2.260 | 0.333 | 0 | 0 |
| DebugSpheres | 10000 | 0.381 / 29.574 / 30.977 | 2.137 | 0 | 0（不包含旧球体 Mesh） |
| GpuFish | 10000 | 0.612 / 2.204 / 13.405 | 0.713 | 0 | 2,240,080 B |
| GpuFish | 30000 | 0.371 / 5.642 / 37.807 | 1.223 | 1 | 6,720,080 B |

GPU 有效计时样本均为 **0/600**，`latest_gpu_ms=-1`，因此 GPU frame / vertex invocation / fragment cost / GPU indirect cost / 峰值 VRAM 均为 **未测得**。Unity 文档支持 D3D11 的 FrameTimingManager，但本轮隐藏窗口离屏路径未取得有效 GPU 时间，不能据此说设备不支持或伪造 GPU 毫秒。[Unity 6 帧计时说明](https://docs.unity3d.com/cn/6000.0/Manual/frame-timing-manager.html)

frame_ms 来自主循环 deltaTime，包含异步提交及可能的队列等待，不等于单帧 GPU 完成时间；表中 3 万 P50 小于 1 万不表示扩容更快。所有模式保留 30 Hz 合成快照求值，Empty 是无鱼绘制的夹具基线；并非连夹具都关闭的纯 URP 极限。每档只采样了约 0.3–2.1 秒，不能外推稳态 FPS、热稳定性或微小性能差异。首次加载/预热前已累计丢弃 81–85 Tick，CSV 保留绝对数；3 万档采样期又丢 1 Tick，不能省略。

仅对有上传的提交帧统计（1 万 22 帧，3 万 35 帧）：

| 数量 | CPU 打包 P50 / P95 | CPU 上传 P50 / P95 | 所有显示帧 CPU submit P95 |
|---|---:|---:|---:|
| 10000 | 1.037 / 1.159 ms | 0.104 / 0.193 ms | 0.026 ms |
| 30000 | 3.130 / 3.995 ms | 0.211 / 0.374 ms | 0.037 ms |

打包含 Job 调度与等待，同帧追赶可包含多个提交。多数显示帧没有新 Tick，所以全帧 pack/upload P50 为零，不能将零解释为无成本。当前 3 万档 CPU 打包已经超过规划中“打包+上传+提交 P95 ≤1.5 ms”的拟议目标；本阶段不将该目标标为通过，后续先用更长重复采样定位 Job 等待与历史映射成本，再决定并行化或布局调整。

原始数据：[汇总](Verification/Phase4/Benchmark/summary.json)、[Empty](Verification/Phase4/Benchmark/Empty-10000/frames.csv)、[旧球体](Verification/Phase4/Benchmark/DebugSpheres-10000/frames.csv)、[1 万鱼](Verification/Phase4/Benchmark/GpuFish-10000/frames.csv)、[3 万鱼](Verification/Phase4/Benchmark/GpuFish-30000/frames.csv)。每个目录保留 environment.json 和采样结束截图，例如 [3 万鱼截图](Verification/Phase4/Benchmark/GpuFish-30000/frame.png)。分位数按排序后 nearest-rank（ceil(pN)）计算，summary 保留全帧分布；表中提交帧数据从 `upload_bytes>0` 过滤。

初次隐藏窗口普通路径出现跳过自动出帧、虚低 CPU 循环耗时，因此已经丢弃并重跑上述显式离屏结果，没有将初次数据混入本表。

## 5. 复现与后续验收

构建后在项目目录运行，例如：

```powershell
& ./Builds/Phase4/Phase4.exe -force-d3d11 -screen-fullscreen 0 -screen-width 1920 -screen-height 1080 -rvo-benchmark -rvo-offscreen -rvo-count 30000 -rvo-mode GpuFish -rvo-frames 600 -rvo-output D:/unityhub/tries/3D_RVO/Documentation/RVO/Verification/Phase4/Benchmark/Manual-GpuFish-30000
```

`-rvo-mode` 支持 Empty / DebugSpheres / GpuFish；默认预热 120 帧，采样 600 帧，结束写 CSV、设备 JSON 和截图并退出。关闭 VSync/HUD、解除 targetFrameRate；采样期报告只写预分配内存。`-rvo-offscreen` 将相机自动绘制关闭，每帧显式通过完整 SRP 请求渲染到指定分辨率 RenderTexture，避免隐藏窗口跳过出帧；不包含窗口呈现成本。省略该参数走普通窗口，必须保持可见，不能把隐藏/最小化时的循环耗时当 GPU 性能。元数据记录具体 renderPath。改变 Inspector 的质量参数后需重新保存场景/构建；固定镜头是默认，环绕镜头需显式启用。不要并行运行多个 Player 污染 GPU 测量。

CSV 的 `pack_ms` 为本显示帧内所有提交的打包累计差值；`upload_ms`/`submit_ms` 是 CPU 墙钟，submit 包括命令准备与 API 提交，不是 GPU draw 耗时。`upload_bytes` 是两端点有效字节数；`latest_gpu_ms` 为 FrameTimingManager 延迟结果，以独立 `gpu_timing_timestamp` 关联，-1 为不可用，不能冒充当前 CPU 帧。frame_ms 包含夹具求值、渲染、等待/呈现等端到端成本。Dropped ticks 必须与性能一起报告。

尚待验收：D3D12；GPU 抓帧确认各 pass 与索引寻址；全应用分配与长时资源趋势；阴影/运动矢量按后续质量目标补齐；正式固定轨迹多次采样及 GPU counter（vertex invocation、fragment/overdraw、indirect 成本、显存峰值）。当前没有 2000 顶点 VAT / 3-plane / single billboard 资产，不能提前填它们的性能表，也不能用本期低模结果推导最终海洋组合预算或 3 万真实 ORCA 帧率。
