# 图片人物卡 Agent 验证记录

日期：2026-11-12。已实现 MCP、会话编辑、渲染、新卡保存、覆盖贴图和三类网格制作管线。完整构建、16 项 CTest、发布审计及六个实际游戏样本通过；真实人像的外观拟合仍需调用方 AI 使用实际参考图评估。

## 已执行的检查

| 检查 | 结果与边界 |
| --- | --- |
| 完整构建 | `build-cpp.ps1 -Jobs 4`，C++ 模块、Agent 和 WinUI 构建通过 |
| CTest | 16 / 16 通过；覆盖原有编辑、扩展、渲染、FBX、受管工具进程、启动恢复和 Agent 会话 |
| 新卡数据 | 新姓名、新 PNG 封面、未知人物块与插件扩展保留、覆盖数据往返通过；基准卡原件不变 |
| 会话事务 | 版本冲突、非法字段、部分失败批次、草稿写入失败均不推进已提交版本；历史恢复与重启恢复通过 |
| 预算与失败 | 12 轮修正限制、单素材三次构建限制、缺失工具与失败构建保留草稿、中断任务标记通过 |
| 图片输入 | 本地 PNG、Base64、损坏图片与非法 Base64 通过；PNG/JPEG 共用 WIC 解码入口 |
| MCP stdio | 独立进程完成 initialize、ping、15 个工具发现、异步查询及取消，EOF 后正常退出 |
| 真实基准卡 | 男性原版卡 470 个字段、女性 MOD 卡 619 个字段；修改与恢复通过 |
| 原生 GPU | 正面、侧面、背面、面部和素材缩略图生成通过；已查看全身与面部画面并校正取景 |
| Blender | 用户提供的 Blender 5.3.0 RC（`f78a5e153839`）完成参考 FBX、UV、骨架及 `.blend` 导出；配饰、发型和服装完成独立 FBX 回读，检查有限坐标、UV、世界位置／尺寸和最多四个归一化权重 |
| Unity | Unity 2018.4.11f1 许可激活生效；固定 ModdingTool 工程及七项固定本地依赖完成脚本编译、Prefab、AssetBundle、清单和 `.zipmod` 构建 |
| 新网格 | 三类素材均经 Agent 构建、选择、四视角原生预览、登记到独立素材库及角色包导出；发型／服装另测动态采样、体型扰动，服装另测穿脱状态 |
| 覆盖贴图 | 脸部 KSOX、身体 KSOX、服装 KCOX 各有实际写入、原生预览、另存和导出样本；其它扩展保留 |
| 游戏读卡与装配 | 独立副本执行真实 `HoneySelect2.exe`，使用游戏的 `ChaFileControl`、`ChaControl`、Sideloader 和 Overlay 插件读卡并装配；六个样本完成游戏内检查 |
| 游戏几何与状态 | 配饰、发型和服装按已解析清单确认生成素材来源，检查有效网格、世界尺寸与位置、材质／Shader；发型头部和服装手臂姿态扰动通过，服装全穿／半脱／脱下三种可见性通过 |
| 游戏覆盖图 | 三种覆盖目标均由游戏 Overlay 插件解码为 1024 × 1024 贴图，保存实际 D3D11 游戏渲染图 |
| 发布审计 | 73 个原生 PE 文件检查通过，无 CLR／Unity 引擎导入；构建脚本作为文本资源，技能和配置示例随包提供 |
| 发布复制 | `tests/package/Test-ParallelPackage.ps1` 的文件内容、清单一致性、失败处理及路径边界通过 |
| 脚本与技能 | 本次源码格式、PowerShell 规则、Python 语法及技能 quick_validate 检查通过 |

## 样本与证据

最终 MCP 报告位于 `artifacts/agent-blender/verified-hair-report.json`、`verified-accessory-report.json`、`verified-clothes-report.json` 和 `overlay-{face,body,clothes}-report.json`。游戏报告和渲染图收集到 `artifacts/agent-game`。独立工作目录和角色包保存在本机 `%LOCALAPPDATA%/HS2Manager/AgentValidation/20261112`。这些测试资料不进入发布包。

网格样本是用于验收的球形发型／配饰和简单服装；覆盖图使用项目品牌 PNG。它们证明制作、装配与交付管线可运行，不代表真实人像的相似度或成品美术质量。

游戏副本使用本机 HS2、BepInEx 5.4.17、Sideloader 16.9.1、HS2API 1.28.1 和 Overlay 6.0.5。验收插件仅置于独立副本，发布包不含游戏或测试插件。旧版加载器在启动时可能停住，测试入口使用受管超时与有限重试；失败启动的进程会回收。副本省略了场景资源，标题场景的地图初始化报错与配置管理器缺项单独保留在日志中，人物装配与渲染按具体检查结果判定。

构建修正覆盖了游戏清单的三行元信息、Sideloader 不处理 CSV 引号、资源包路径大小写、FBX 单位／父级变换、有效骨骼索引和逆绑定矩阵。素材指纹包含构建脚本及工具依赖版本，修正后不会沿用旧的生成包。

## 验收入口

- `tests/agent/Test-AgentProtocol.py`：`--render --finalize` 检查已有素材交付；加 `--overlay --overlay-target face/body/clothes` 检查覆盖图；加 `--build-fixture hair/accessory/clothes` 检查新网格。
- `tests/agent/Test-BlenderAssets.py`：独立运行 Blender 与 FBX 回读，包括世界坐标和尺寸比较。
- `tests/agent/Prepare-GameSandbox.py`：准备有标记的独立游戏副本，导入指定测试报告对应的角色包。
- `tests/agent/Test-GameAssets.ps1`：编译验收插件，以 Windows Job 执行游戏，验证本轮报告；`-NoGraphics` 仅验证加载，默认使用 D3D11 并输出画面。

## 适用边界

- 没有执行真实参考人像的识图、拟合和相似度验收；调用方 AI 负责分析图片、制作内容和视觉取舍。
- 半脱状态默认复用完整服装几何。独立半脱造型、复杂长发／裙摆动态、自定义新增骨骼和全部遮罩组合仍需针对具体素材制作并验证。
- 当前游戏样本以男性基准卡为代表；女性基准卡的协议、编辑与数据往返已检查，未逐一覆盖女性新网格的所有组合。
- 游戏检查覆盖读卡、装配、渲染、姿态扰动和穿脱，未覆盖全部游戏场景与素材组合。未来生成卡的 `game_validation` 在未运行针对该卡的游戏检查前仍为 `not_run`；原生预览成功不会自动变成游戏验证通过。
