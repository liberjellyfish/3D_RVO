# 文档清理记录

整理日期：2026-10-07。范围为项目维护的 Markdown 文档；Unity 缓存、第三方包、代码、场景、生成资产、测试数据及复现脚本均未在本次清理中修改。

## 清理依据

先对照当前工作区的启动器、模块工厂、World/存储、三维寻路/避障、快照桥接、GPU/URP 渲染、最新礁石资产、测试与脚本，再判断文档用途。旧日期本身不是删除理由：有独立设计取舍、真实测试/性能/失败记录、截图和复现价值的资料仍然保留。

原 ARCHITECTURE 同时包含“Full3D 已实现”和“完整 XYZ 求解尚未实现”，原 README/ROADMAP 串联了多个阶段的旧默认值，初版计划还把已经实现的功能写成下一批任务。因此保留原有架构入口并重写当前行为，将历史结果与当前说明分开。

## 删除的 8 份 Markdown

下表文件名用于记录，不再提供指向已删除文件的链接。

| 删除文件 | 理由 | 内容去向 |
| --- | --- | --- |
| `ROADMAP.md` | 主要为早期里程碑和完成清单，阶段状态反复叠加，当前索引已替代 | README 的历史报告索引；ARCHITECTURE 的已实现范围/边界 |
| `PHASE3_PLAN.md` | XYZ 装配、体素、球体 ORCA、粗图/地标/Jobs 已实现，原分批将来时容易误导 | ARCHITECTURE 三维主线；两份三维验证报告保留具体性能与取舍 |
| `PHASE4_GPU_VISUALIZATION_PLAN.md` | 初版表示/GPU/海洋规划与当前网格、Buffer 布局、RG 提交方式有差异，实施与结果已有独立记录 | ARCHITECTURE GPU/海洋章节；P4 基础/续阶段验证与实验说明保留 |
| `PHASE4_PRESENTATION.md` | 早期 GPU 接入提案，重复快照边界与实施顺序，主要转引其他旧计划 | ARCHITECTURE 快照、渲染与依赖章节 |
| `OCEANLIVE_RENDER_REARCHITECTURE_REVIEW.md` | 修改前审查/重构规划；背景箱、鱼深度、光程、PBR、motion 后续已有实现与独立证据 | ARCHITECTURE 当前水下结构；P0/P1/P2 实施记录及原始证据保留 |
| `PLAN_REEF_ROUTES48.md` | 临时执行清单，已由结果报告覆盖，且明确“不扩展 2048”与当前状态不符 | 48 障碍/水纹验证报告；ARCHITECTURE 当前边界 |
| `PLAN_REEF_NETWORK2048.md` | 多通路布局与验证待办已落地，重复 Builder 和现有 2048 报告 | ARCHITECTURE 当前 96 m/18 区/24 开口布局；2048 验证报告 |
| `VERIFICATION_P2_OPTIMIZATION.md` | 仅为补齐缺失链接的短索引，没有独立测量分析 | 五条原始证据链接全部合并到 VALIDATION |

## 重写与保留

- [ARCHITECTURE.md](ARCHITECTURE.md)：当前完整说明，共 15 个主题，附源码与场景链接、流程图、关键配置、所有权和后续阅读切口。
- [README.md](README.md)：当前运行与文档索引；项目根新增简短 README 作为入口。
- [VALIDATION.md](VALIDATION.md)：当前测试/复现用法、历史索引和准确指标口径。
- 其余 16 份专题设计/实施/验证记录保留，包括二维设计与交通、三维优化与收尾、GPU/材质/光学、礁石迭代与 2048 正式结果。历史页面增加阅读定位，指向当前架构；指向删除页面的链接全部更新。
- `Documentation/RVO/Verification` 与根 `Verification` 的 XML/CSV/JSON/图片及失败报告全部保留。本次未重新测量，未改写历史数字。

## 历史文档的使用方式

历史页中的 RenderMeshIndirect、旧内箱背景、旧 224N buffer 布局、128³/64 m/1024 默认值，是各次运行当时的状态。当前主场景使用显式 Render Graph 间接几何提交、PreviousDisplay、192³/96 m/2048 配置，先读 ARCHITECTURE。

`RunReefRoutes48.ps1` 的 Profile 现在调用最新 Builder 与最大数量档，不能直接复原旧 20/48 障碍对照。保留脚本的测试/采集价值，在验证指南和对应历史报告中说明了这一限制。

本次检查包括文档相对链接、显式目录锚点、源码/场景/菜单/参数交叉核对、删除文件引用检查、`git diff --check` 和非文档文件指纹比对；本次没有启动 Unity 或运行性能测试。
