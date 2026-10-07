# Phase 4 续阶段验证记录

> 历史专题记录：下文的默认参数、实现状态和测试数字对应文中日期及当时版本。当前系统结构与运行配置见 [ARCHITECTURE.md](ARCHITECTURE.md)，最新主场景证据见 [2048 多通路报告](VERIFICATION_REEF_NETWORK2048.md)。

日期：2026-10-03。实施范围、运行方式和明确延后项见 [PHASE4_CONTINUATION.md](PHASE4_CONTINUATION.md)。测试与性能口径分别记录，不把功能成功解释为最终 GPU 预算通过。

**历史口径：本页记录 2026-10-03 初版材质/强雾的测试和短测。** 2026-10-04 已根据用户参考改为明确的动态表面材质，新增静态阴影并减弱雾；当前外观与新验证见 [表面表现修正](PHASE4_SURFACE_CORRECTION.md)。下表不能作为新画面的性能结论。

## 自动验证

| 检查 | 结果 | 证据 |
|---|---|---|
| 全部 EditMode | 117/117 通过 | [EditMode.xml](Verification/Phase4Continuation/EditMode.xml) |
| 全部 PlayMode，真实 D3D11 | 9/9 通过 | [PlayMode.xml](Verification/Phase4Continuation/PlayMode.xml) |
| 全部 PlayMode，真实 D3D12（初版） | 9/9 通过 | [PlayMode-D3D12.xml](Verification/Phase4Continuation/PlayMode-D3D12.xml) |
| Windows x64 非 Development Ocean Player | 构建成功；18 次启动、渲染、写出并退出成功 | 本地 `Logs/Phase4-Ocean-Build.log`、下方逐次证据 |

新增 EditMode 在完整波周期检查 2056 顶点网格的变形包围球，数值差分检查法线所用导数，并验证共享纹理维度/mip/CPU 副本释放。

GPU 检查覆盖三种近景模式的真实像素与图像差异、三种卡片的可见轮廓、交叉面端视回退和滞回、前方不透明黑墙对鱼的遮挡、Render Graph 水体出图、相机水内/水外/射线不交水体的 Beer 数值及 100 次环境释放重建。原有 3 万索引唯一性/保守剔除、100 次鱼 Buffer 重建、Live 开关不改变 60 Tick 仿真状态也继续通过。

生命周期测试无意外错误日志，但不能代替外部 VRAM allocated/resident 长期趋势。单鱼图像差异是功能对照，不是多方向轮廓误差达标报告。

验证中修复了 HLSL 保留字作为变量名的编译错误，并消除了 Compute 分支内联的未初始化警告。水体数值检查曾将黑墙放在鱼体后面，导致像素包含鱼的表面颜色；调整接收墙后断言通过，未通过扩大容差掩盖问题。

## 焦散数值

128² RFloat 设备回读，仅用于测试；正常运行使用 256²/512² R16F，不回读。

| 对照 | 最大绝对强度误差 |
|---|---:|
| 原除法式 vs 稳定代数式，t=7.25 秒 | 5.96046448×10⁻⁷ |
| 12 秒整数谐波艺术变体，首尾 | 1.54733658×10⁻⁴ |

[原始检查值](Verification/Phase4Continuation/caustic-error.txt)、[强度截图](Verification/Phase4Continuation/caustic.png)。这仅覆盖所测分辨率/时刻，不意味着所有时钟、mip 接缝、短循环画质均已验收。原运动不是 12 秒循环，背景使用的边缘修补也是单独艺术近似。

## 合成 Player 重复短测

RTX 4060 Laptop GPU（7957 MB 报告显存）、i7-12650H、D3D11、1920×1080、PC_RPAsset；固定相机、自动 LOD、默认程序化动画，LOD0 为 2056 顶点、其它为 128/72/32。每档 3 次，预热 300 帧、采样 1800 帧；共享焦散档位 256²/30 Hz。

本次是确定性合成回放，每显示帧推进 1/60 秒夹具时钟，固定相同采样阶段。每次实际只采样约 2.2–5.8 秒；它是重复短测，不是热稳定性能冻结。回放不丢 Tick 是时钟定义的结果，不证明 ORCA 能实时运行。

| 数量 | 海洋 | CPU 主循环 frame_ms P95，3 次范围 | 每次采样实际秒数 |
|---:|---|---:|---:|
| 10000 | 关 | 2.19–2.30 | 2.16–2.18 |
| 10000 | 开 | 2.31–2.42 | 2.43–2.44 |
| 20000 | 关 | 3.53–3.58 | 3.85–3.86 |
| 20000 | 开 | 3.69–3.82 | 4.11–4.18 |
| 30000 | 关 | 5.50–5.78 | 5.53–5.74 |
| 30000 | 开 | 5.65–5.85 | 5.76–5.78 |

**全部 18 次有效 GPU 时间戳为零。** `latest_gpu_ms=-1`，汇总 `gpu_p95=null`。以上 CPU 循环分位数不等于 GPU 帧时间，也不能把开关差值解释成 Ocean GPU pass 成本。离屏路径不包含窗口呈现，不能外推实际可见窗口 FPS。

[完整汇总](Verification/Phase4Continuation/Benchmark/summary.json)；每个 case/run 子目录含 `frames.csv`、`environment.json`、1080p 截图，未跟踪的 `player.log` 留在本地。例如 [1 万环境配置](Verification/Phase4Continuation/Benchmark/ocean-10000-d3d11-run1/environment.json)、[3 万原始 CSV](Verification/Phase4Continuation/Benchmark/ocean-30000-d3d11-run1/frames.csv)、[3 万截图](Verification/Phase4Continuation/Benchmark/ocean-30000-d3d11-run1/frame.png)。数据采集后仅新增 Live HUD 关闭和结束取消订阅，不改变上述合成测量路径。

[RunPhase4Benchmarks.ps1](RunPhase4Benchmarks.ps1) 可复跑矩阵，并包括 VAT/Bone/卡片/直接焦散/512² 的可选 case。脚本按 GPU timestamp 去重，已有证据目录不静默覆盖；未跑的 case 不填入性能表。

## 退出门槛与下一步

| 阶段 | 本次状态 | 剩余工作 |
|---|---|---|
| P4.3 | 同源动画实验链路可用 | 精制 DCC 资产、完整动画 Pass/阴影策略与有效 GPU 对照；目前默认 procedural |
| P4.4 | 解析卡片实验、端视回退可用 | 多视图 atlas、全方向/动态轮廓误差、等画质收益；默认卡片关闭 |
| P4.5 | 共享焦散、内壁、一次 Beer 雾可用 | 接缝/mip/长时钟的更广图像扫描、离线短 loop、分效果 GPU 时间 |
| P4.6 | 相机局部 RG 合成与显式深度策略可用 | 近鱼 shadow proxy、有限粒子、独立质量资产按测量收益再加；多相机/XR 不支持 |
| P4.7 | 双入口与可复现采样工具、重复短测 | 有效 GPU 抓帧/统计、长稳态矩阵和质量验收后再冻结 |

没有宣称完整 P4.3–P4.7 退出条件全部完成，也没有宣称 3 万真实导航/ORCA 达标。先取得有效 GPU 证据，再决定是否承担卡片 atlas、阴影与粒子的复杂度；不为填满清单增加未经证明的默认成本。
