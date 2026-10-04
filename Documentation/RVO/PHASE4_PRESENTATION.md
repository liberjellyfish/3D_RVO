# GPU 鱼群与海洋（实施入口与早期记录）

**2026-10-03：P4.0–P4.2 基础实现已交付。** 运行入口、数据布局、测试与 Player 基线见 [基础验证记录](VERIFICATION_P4_FOUNDATION.md)。终态设计以 [Phase 4 GPU 可视化详细规划](PHASE4_GPU_VISUALIZATION_PLAN.md) 为准，涵盖多表示 LOD、VAT/Bone、稳定三维 frame、焦散分析和海洋合成。

已新增 `Phase4_RenderOnly.unity` 与 `Phase4_Live.unity`；`AgentSnapshot → FishPoseBuffer → GraphicsBuffer → Compute Culling/LOD → RenderMeshIndirect` 已运行。当前使用 200/128/72/32 顶点程序化鱼验证四档，不表示 VAT、Bone、impostor 或 Ocean 已完成。仿真程序集不引用 Rendering，旧球体调试场景继续保留。

以下第 1–5 节保留早期设计背景，其中拟议接口、顺序和将来时描述不作为当前实施清单；“先 VAT”已经由多表示实测路线替代。后续从 P4.3 开始，外部抓帧、D3D12 和正式重复基准仍待完成，不能由当前短基线推定通过。

2026-10-03 续阶段：同源动画对照、解析远景卡片、共享焦散和水下 Render Graph 合成已接入独立 Ocean 场景，见 [后续实施与范围](PHASE4_CONTINUATION.md)。上方基础阶段描述为历史状态；详细验收与未完成门槛见新记录。

## 1. 最终演示目标与边界

目标是“大规模 3D 鱼群在复杂海洋环境中自主寻路 + ORCA 群体避障”。导航与运动由 CPU/Jobs 核心负责，表现层读取完成的状态。海洋画面、鱼形网格、动画、相机和光照都不成为寻路依赖。

先完成 [Phase 3](PHASE3_PLAN.md) 的真实 XYZ 静动态导航。开发 GPU 表现时保持一套可关闭渲染的核心场景，分别记录仿真、上传、GPU 与端到端开销。渲染剔除/LOD 不移除物理 agent，也不能解决 CPU 窄口排队问题。

## 2. 现在约定、之后实施的接口

保留 `ISimulationPresenter` 的只读和提交后调用原则；以下为未来适配数据，不向 AgentStorage 增加 GraphicsBuffer，不提前修改现有接口。

| 数据边界 | 拟议字段 / 约束 | 生命周期 |
| --- | --- | --- |
| 核心 → 表现快照 | Tick、SimulationTime、Count、稳定 ID、float3 位置/速度、半径；目标/求解状态按需附加 | 仅在 World Ready 后读取；异步使用前复制到表现层自有缓冲 |
| 表现实例状态 | 位置、朝向 quaternion、缩放、颜色/种类、动画相位/速度、LOD 分组 | 朝向由速度推导，近零速保留上次朝向；稳定 ID 关联外观，不把数组槽位当永久身份 |
| 表现 → GPU | 显式布局的实例数组、可见实例索引、绘制参数 | renderer 自己创建/扩容/释放，字段对齐与 stride 在实现时固定；与 CPU 核心内存解耦 |
| 调试数据 | 选中 agent、路径、邻居、约束、扫掠/恢复状态 | 独立小容量通道，关闭时不上传全体约束 |
| 地图显示 | 不可变体积版本、障碍外表面/分块边界 | 版本变化时重建，不逐帧转换全部体素 |

若以后做插值，只在前后两次已完成状态之间生成显示姿态，不写回位置/速度、不外推物理结果。异步 GPU 读取必须保证上传缓冲在 GPU 用完前不被复用；Reset/释放同样尊重该生命周期。Phase 3 不为这些行为加入每 Tick GPU 同步。

## 3. GPU 呈现实施顺序

```mermaid
flowchart LR
    A[AgentStorage 已提交状态] --> B[只读表现适配与复制]
    B --> C[GraphicsBuffer]
    C --> D[可见实例索引与 LOD 分组]
    D --> E[RenderMeshIndirect]
    E --> F[VAT 鱼体动画]
    F --> G[URP 海洋集成]
```

1. **简单实例网格。** 先上传球体/低模鱼，确认坐标、朝向、缩放、ID 和 Reset 正确，再采用间接绘制。暂不加 VAT 或海洋，以便定位问题。
2. **剔除与 LOD。** 按可见性生成实例索引，各 mesh/material/LOD 使用独立分组与绘制命令；验证阴影等额外 pass 的成本。Unity 的 `RenderMeshIndirect` 接收间接参数，但 `worldBounds` 是整批裁剪/排序边界，不能把它当作自动逐实例 GPU 剔除；绘制参数使用 `GraphicsBuffer.IndirectDrawIndexedArgs` 的平台布局。[Unity 6 API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Graphics.RenderMeshIndirect.html)
3. **VAT 鱼群。** 增加动画资产、每实例相位和由速度驱动的播放速率。鱼形轮廓与球形碰撞代理的差异必须可见；采用能包住需要保护部位的半径，或明确纯视觉尾鳍可能相交。弯曲/摇摆不得改变导航位置。
4. **URP 海洋。** 加入海面、水下雾、色调、深度和必要的光照效果；先确认低成本基础组合，再加反射、折射、泡沫等选项。效果质量可缩放，导航能在关闭海洋时运行。

GPU buffer 的创建、上传与显式释放依据 [Unity GraphicsBuffer](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/GraphicsBuffer.html)；目标平台能力和缺少 compute 支持时的轻量回退在实施时确定，不把高端渲染要求施加给算法测试。

2026-10-04：用户明确要求外表材质呈现参考图的明暗纹理；当前实现与截图以 [表面修正](PHASE4_SURFACE_CORRECTION.md) 为准，早期将颜色主要交给雾的选择已撤回。

## 4. 用户提供的海洋参考

保留 [Shadertoy MdlXz8](https://www.shadertoy.com/view/MdlXz8) 作为视觉研究入口。早期网页抓取失败；用户现已提供完整源码，具体运算、数值稳定性、空间接缝与时间周期分析见 [详细规划第8节](PHASE4_GPU_VISUALIZATION_PLAN.md#8-用户提供焦散源码的具体分析)。作者许可与实际GPU性能仍未核验，不把“高性能”写成已测结论。

提供的源码是无纹理输入的二维程序化焦散图案，不是射线步进或网格海面；实施前核验许可，并隔离比较直接计算、低清共享生成与短循环烘焙变体。最终画面以鱼群观察和性能预算为准。

海面几何与仿真边界分开。若要让鱼随浪、海流或流体运动，属于动力学/导航模型扩展，需要重新定义 preferred、可行速度和安全检查，不作为渲染附带功能实现。

## 5. 完成等级与报告

Phase 4 开工前记录 Phase 3 的功能完成情况及未解决的压力档问题。Phase 4 分别给出纯仿真、简单网格、GPU 剔除/LOD、VAT、海洋各档结果，区分 CPU Tick、上传耗时、GPU frame、显存与整体 FPS。没有测量之前不承诺大规模数量或帧率。

稀疏体素、分层路径图、大世界流式加载归导航扩展，是否实施由体积内存/搜索成本决定。它们可以与后续表现开发独立排期，不因开始做海洋就自动成为必做项。
