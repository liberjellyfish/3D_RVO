# 礁石海洋表现优化（2026-10-05）

入口仍为 `Assets/RVO/Demo/OceanReef/OceanReefLive.unity`，菜单 `Tools/RVO/Open Reef Presentation`。默认 1024 条真实导航鱼、ORCA、SMAA。原 Phase4 对照场景不重建。

## 水纹与材质

用户给出的 David Hoskins / joltz0r 五次迭代 turbulence 已在 `CausticPattern.hlsl` 中实现。此前礁石场景的全局强度仅 0.55，实际画面几乎看不出纹理。现在将同一公式生成的共享焦散设为 512² / 30 Hz、全局强度 3.2、世界尺度 0.085（约 11.8 米一个周期）；岩石与海床材质新增 `Water turbulence on lit surface` 参数，默认增益 1.8。

水纹进入 URP PBR 的主光照明，受法线、太阳路径和阴影影响；鱼身也接收同一水纹。原示例的蓝绿色平底色不直接覆盖鱼身和石材颜色，保留材质层次。观察方向的水下雾仍只合成一次。沿用共享纹理、mip 和帧间插值，不按每条鱼重复运行五轮三角函数。原作者署名保留；没有声称这次已测得 GPU 性能收益。

## 地图

从 8 个闭合礁石/海床网格增加到 20 个。新增两侧错位高柱、低礁脊、侧拱和底部碎礁。保留中央主拱门及两端出生带，路径可选择穿过拱洞、左右绕礁或从上方通过。

网格、调试代理、`ReefNavigation.bytes` 和 `ReefVolume.asset` 一并重建，仍使用 128³、0.5 米体素。所有新增大礁石参与导航，不只是装饰。使用网格世界包围盒的保守代理，不能视为精确凹网格碰撞。

## 鱼群

- 大小在原个体大小的 0.7–1.3 倍范围均匀随机。真实半径为 0.455–0.845；渲染尺寸、包围球、LOD 与避障跟随半径变化。
- 最大导航速度在 0.75–1.25 倍范围均匀随机，即 3.75–6.25 单位/秒。路径跟随、ORCA 与交通恢复使用每条鱼的真实速度上限；摆尾频率继续响应实际运动速度。
- 大小/速度使用场景 seed 和个体 ID 的独立随机流，不消耗出生采样随机流，重置和数量档位前缀保持一致。
- 出生与目标点按两条鱼各自半径之和加间距检查。静态导航按最大半径 0.845 加安全余量 0.08 烘焙，防止大鱼沿小鱼通道穿模；代价是小鱼也使用保守净空。
- 金黄、青绿、蓝、珊瑚红、紫、黄绿六种体侧条纹，配合轻微体色差异。条纹颜色由 stable ID 选取，各级网格共享条纹遮罩，鳍和眼不被条纹染色；无需逐鱼材质或额外绘制批次。

大小范围比 0.5–1.5 收紧，以减少极小鱼辨识度损失和大鱼挤占拱洞；速度范围更集中以控制拥堵。参数在 `ReefNavigation.asset > Scenario > VolumeSizeVariation / VolumeSpeedVariation`；0 表示不随机，旧配置默认 0。更改大小范围必须重新烘焙，速度范围无需重烘焙。生成场景的持久默认值应同步修改 `OceanReefBuilder.BakeProfile()`；使用 `Tools/RVO/Build Ocean Reef Presentation` 重建礁石场景。

## 验证

验证结果和实际画面存于 `Verification/ReefEnhancement`。本轮检查编译、随机范围和可重复性、1024 个出生/目标点间距、真实速度上限、最大净空与失效烘焙拒绝、绕礁路线、四级鱼网格，以及实际 D3D11 运行画面。未进行长时间吞吐或 GPU 性能矩阵测试。

| 检查 | 结果 |
| --- | --- |
| 全部 EditMode | [121/121 通过](Verification/ReefEnhancement/EditMode.xml)，包括 4 项新增检查 |
| 全部 PlayMode，D3D11 | [14/14 通过](Verification/ReefEnhancement/PlayMode.xml) |
| 实际 Live 场景 | 1024 条鱼，1280×720，SMAA；默认、跟拍、全景三个镜头持续出帧 |

实际截图：[修改前](Verification/ReefEnhancement/Before/live-01.png)、[最终默认镜头](Verification/ReefEnhancement/ArchFinal/live-01.png)、[条纹与大小近景](Verification/ReefEnhancement/Near/live-00.png)、[礁石全景](Verification/ReefEnhancement/Overview/live-01.png)。各目录 `capture.txt` 记录 frame / Tick / 相机 / API，截图时间不同，不作为严格同步的性能对照。

复核中修复了隐藏编辑器下截图工具可能不推进玩家帧的问题，显式请求 PlayerLoop 更新。旧 `DisplayHistoryTracksRenderedFramesAndResetsAtGenerationOrCut` 测试没有真正渲染相机，却期待显示历史有效；按现有渲染规则补齐真实相机请求，并增加漏帧后历史失效断言。未放宽断言或修改生产历史逻辑来迁就测试。
