# 模型导出验证记录

> 历史记录：本文的架构、命令、版本、性能和通过数量对应迁移前的 C# 实现及其当时的预览后端。旧源码现存于 `tests/reference/src`。本次 C++ 重写的实际检查见 [C++ 验证结果](CPP-VALIDATION.md)，当前构建与架构见 [C++ 迁移说明](CPP-MIGRATION.md)；本文数字不自动计入新版本验收。

## 原生导出与场景验证（2026-11-10）

本轮验证新的三目标导出流程：干净的通用 FBX、可直接打开的 Blender `.blend`、独立 Unreal `.uproject`。下方 2026-11-09 的 FBX / 手动脚本记录保留为历史，不作为新原生流程的验收结论。

实际运行软件为 **Blender 5.3.0 Release Candidate** 和 **Unreal 5.8.4-58902052**；Unity 导出内核为 2018.4.11f1、协议 v4。其他目标版本未经本轮实机验证。

| 样本 | Blender 网格 / 材质 / 打包图像 | Blender 形变通道 | Unreal 材质 / 原生资产 / Morph Targets |
| --- | --- | --- | --- |
| 女性 Fur | 29 / 29 / 148 | 150 | 29 / 182 / 146 |
| 男性 Hero | 19 / 19 / 98 | 155 | 19 / 122 / 151 |
| 女性 MOD / 高跟鞋 | 32 / 32 / 165 | 150 | 32 / 202 / 146 |

Unreal 每个角色合并为一个 Skeletal Mesh；同名形变跨网格合并，两个女性样本的舌头/牙齿默认零位移通道被引擎省略并写入报告。已降低 Unreal MorphThresholdPosition，保留此前会被默认阈值过滤的约 0.1 毫米睫毛、泪线形变；是否可省略依据内核记录的实际位移，不能只按通道名称判断。

| 检查 | 本轮结果 |
| --- | --- |
| 三种实际服务导出、中文输出目录、未保存草稿、隐藏配饰与服装选项 | 通过；原卡 SHA-256 不变 |
| 通用包无转换脚本；原生包无 FBX、脚本及外置贴图中间文件 | 通过 |
| 两张卡批量、同名目录自动编号 | 通过，原目录保留，新目录使用 `(2)` |
| Blender 独立进程回读、打包图像、材质槽、形变名称及默认权重 | 三个代表样本通过 |
| Unreal 独立进程回读、默认关卡、骨架、材质槽、资产依赖及形变清单 | 三个代表样本通过 |
| 搬移到中文目录、暂时隐藏中间文件再回读 | 通过；补充眼睛、挑染与高跟鞋样本亦执行 |
| Blender 参考形态抽样最大位置误差 | 0.000494 毫米，阈值 2 毫米 |
| Unreal 原生网格经引擎导出后回读的最大位置误差 | 0.000871 毫米，阈值 2 毫米 |
| 眨眼、嘴部形变与手臂蒙皮 | Blender 三组通过；Unreal 三组原生资产回读通过 |
| 工具缺失、逐角色失败、请求失败、取消、内核异常退出 | 通过 |
| 转换失败、超时、取消、宿主退出及子进程清理 | 四种进程所有权测试通过 |
| 核心、WinUI 三目标对话框、设置、Unity 源码和应用构建 | 通过；最终构建 0 警告、0 错误 |

Unreal 几何验证调用引擎自身的 SkeletalMeshExporterFBX，将已保存的原生资产写到**测试目录**，再检查参考位置、形变位移和骨骼权重；这些回读 FBX 不属于用户交付包。没有把 Blender 的结果当作 Unreal 的结果。

### 同机位画面对照

三张代表卡分别使用全身正面、面部和侧背面，共九组 Unity / Blender Eevee / Unreal TSR 对照。固定参考姿态、1280×960 画幅、相机、灯光和源调色 LUT；高跟鞋角色的 0.6 源单位（6 厘米）根节点抬升已从相机和灯光中同步消除，避免模型归零后取景错位。

另补充隐藏面部配饰后的眼睛近景，以及使用真实头发网格、独立 UV1 遮罩和纹理缩放/偏移的人工挑染夹具。挑染夹具仅修改测试定义，不修改原人物卡。女性样本包含透明头发与半透明服装。

- [女性对照](../artifacts/native-export/review-final/comparison-0.png)、[男性对照](../artifacts/native-export/review-final/comparison-1.png)、[MOD / 高跟鞋对照](../artifacts/native-export/review-final/comparison-2.png)。
- [无配饰眼睛对照](../artifacts/native-export/review-final/comparison-eyes.png)、[UV1 挑染对照](../artifacts/native-export/review-final/comparison-streak.png)。
- [图像指标](../artifacts/native-export/review-final/visual-metrics.json)：记录整图 MAE / RMSE、中央上中下区域均色与误差，以及允许 2 像素偏差的边缘 F1。边缘指标包含内部材质边缘，不等同于 Alpha 轮廓 IoU；背景占比较大，整图 MAE 不能独立证明人物外观一致。
- [Blender 变形检查](../artifacts/native-export/review-final/blender-scene-validation.json)、[Unreal 原生资产回读检查](../artifacts/native-export/review-final/unreal-geometry.json)。

### 可见差异与边界

主体部件、服装和配饰位置保持，眼睛近景未出现白色眼片或眼周灰色面片。两种原生场景仍比源预览更暗，皮肤散射、头发与衣物反光不同；Unreal 皮肤偏暖，头发覆盖和睫毛边缘有更明显的抖动/锯齿。MOD 样本的 Hovl 粒子/中心辉光只保留通用材质近似，出现源预览中不明显的紫色表面；此项没有宣称还原。

Blender Eevee 的 AO、暗角和间接光不与源实现等价，Unreal SkyLight 的漫反射和镜面环境共用源，源 SSR、色差等没有迁移。所有结果均保留这些差异警告和源 Shader 诊断；本轮确认原生交付与可编辑结构，并记录实际外观差异，**不宣称逐像素一致**。

日志：[服务通用](../artifacts/native-export/service-generic.log)、[服务 Blender](../artifacts/native-export/service-blender.log)、[服务 Unreal](../artifacts/native-export/service-unreal.log)、[批量](../artifacts/native-export/batch-final.log)、[转换进程回收](../artifacts/native-export/owned-final.log)、[内核生命周期](../artifacts/native-export/lifecycle-final.log)、[核心](../artifacts/native-export/core-final.log)、[界面](../artifacts/native-export/ui-final.log)、[最终构建](../artifacts/native-export/build-delivery.log)、[Unity 源码](../artifacts/native-export/source-final.log)。

## 历史：FBX 导出验证（2026-11-09）

2026-11-09，本机固定基线的 84 张角色卡均完成实际 FBX 和贴图写出及结构检查，原卡 SHA-256 与基线一致。48 张无缺失诊断，36 张附带资源缺失、未适配效果或无法计算的部件诊断；这些包标为“存在缺失”，不能视为完整外观保证。

## 结构与回读

| 检查 | 结果 |
| --- | --- |
| 基线模型写出、骨架/网格/形变结构及贴图文件检查 | 84/84 完成，失败 0 |
| 原人物卡 SHA-256 与固定基线一致 | 84/84 |
| 无缺失诊断 / 附带缺失诊断 | 48 / 36 |
| 导出网格 / 顶点 / 形变目标总数 | 2,383 / 8,081,693 / 12,610 |
| 独立 Unity 2018.4 回读样本 | 8/8，通过 239 个网格、1,205 个形变目标 |
| 回读参考形态抽样最大位置误差 | 0.0278 毫米，阈值 2 毫米 |
| 形变默认值、眨眼、嘴部及手臂蒙皮 | 通过 |
| 映射表构建有效 Humanoid Avatar | 8/8 |
| 不穿衣物、内着、隐藏配饰及中文输出路径 | 通过 |
| 未保存草稿、输出文件 SHA-256、原卡保持不变 | 通过 |
| 请求失败、取消、内核退出及进程回收 | 通过 |
| 既有预览 IPC、控制、切卡、资源复用及异常恢复 | 通过 |
| 核心、WinUI 界面、Unity 源码编译及便携启动 | 通过，源码编译无警告 |

基线检查首先覆盖全部角色，再针对失败样本修复并重跑。修复包含无权重的刚性部件、空 billboard、无效骨骼引用，以及旧资源可选纹理槽指向非贴图对象引发的原生崩溃。最终回读样本包含三张男女编辑卡及五张修复样本；单位缩放骨架和体形烘焙避免了导入器对非均匀缩放/剪切的重新解释。

完整原骨架、额外骨骼和权重保留。Unity 本身无法计算蒙皮的部件会跳过并在报告中标明。例如 `007-6AB05E800FD6` 的一个附加网格缺少骨骼引用，无法得到有效顶点；该角色仍输出其他 38 个网格，并保留具体缺失清单。

`075-943C63A4AAC6` 和 `076-2C83BBDC4238` 另有范围明显大于身体的配饰或发束网格。导出保留这些表面，并提示检查挂点、缩放或专用变形；不能将这两张卡视为可直接使用的完整角色。

## 证据

- [汇总及逐卡数据](../artifacts/model-export/validation-final.json)：合并两组完整基线检查与修复重跑，包含逐卡原始 SHA-256、网格统计、警告和缺失诊断。
- [三个代表角色回读](../artifacts/model-export/normalized-representatives/unity-import-validation.json)、[五个修复样本回读](../artifacts/model-export/final-recovery/unity-import-validation.json)。各角色目录中有正反面检查截图。
- [核心与界面发布检查](../artifacts/model-export/build-release.log)、[Unity 源码检查](../artifacts/model-export/source-checks.log)、[预览回归](../artifacts/model-export/preview-regression.log)、[取消及异常退出](../artifacts/model-export/lifecycle-normalized.log)。
- 最终运行内核：`artifacts/model-export/player-delivery`，协议 v3；运行程序集 SHA-256：`E0A91227DEF6C7FDA98BBDFE8A51F0A6A42D1F3ACCF8352CA2C932BD7B6EF5A8`。

## 验证边界

本机没有 Unity 2022.3、Unity 6 URP 和 Unreal Engine 5.6，尚未执行这些目标环境的实际导入、材质及动画重定向验收。后续 Unreal 5.8 检查范围见本文末尾；Unity 2018.4 回读只证明该环境中的文件结构、参考形态和基本变形，不替代目标引擎验收。

通用 PBR 的光照、透明排序、皮肤散射和头发效果与原游戏不同。检查截图使用基础色材质，仅用于发现缺失、透明遮罩和几何问题，不是画面一致性测试。既有游戏预览的 SSIM 验收不适用于本功能。

构建、操作和复现命令见 [模型导出说明](MODEL-EXPORT.md)。测试资产与游戏缓存不包含在便携包中。

## Blender 材质优化验证（2026-11-09）

使用用户下载的 **Blender 5.3.0 Release Candidate**（构建 `f78a5e153839`）实际导入、渲染和保存。此次针对材质外观验证三个代表样本（女性、男性、女性 MOD），共 **80 个网格 / 80 个材质**，不是此前 84 张结构验收的 Blender 扩展结论。

| 检查 | 结果 |
| --- | --- |
| 新内核实际写出 FBX、贴图、材质清单和 Blender 导入脚本 | 3/3，原人物卡 SHA-256 不变 |
| Unity GPU 烘焙像素检查 | 12/12，通过皮肤/服装 R/B 通道、瞳孔 Alpha、头发渐变、眼周乘色和 AO 等检查 |
| Blender 自动材质导入、全部贴图加载与色彩空间 | 3/3，80 个材质 |
| 法线节点、金属度/粗糙度、自发光、透明阈值和双面设置 | 通过 |
| 角色重复导入、同名材质隔离、已有场景对象和材质保留 | 3/3 |
| Cycles 正面全身及面部渲染 | 3/3，保留直接 FBX 导入与自动材质重建的同灯光对照 |
| `.blend` 贴图打包 | 3/3，示例场景保留完整模型 |
| 重新打开 `.blend`、Eevee 面部渲染 | 3/3，分别确认 145 / 95 / 162 张打包贴图可读取 |
| 核心、WinUI 界面、Unity 源码编译 | 通过；Unity 源码编译 0 警告 / 0 错误 |

面部检查临时隐藏配饰，以暴露被头盔、眼镜遮挡的眼睛；全身图和保存的 `.blend` 保留配饰。截图使用固定的中性环境和三盏面积灯，不代表原游戏的 DHH 环境。目视复核确认眼周灰色面片/不透明白色眼片得到修正，头发的透明覆盖和随视角变化的光泽可用；不以截图推定游戏画面 1:1 一致。

本次修正了原烘焙误读的通道与 UV，而不只是补接贴图。Blender 脚本为皮肤、头发、眼球、眼周阴影及泪线分别建立节点；原版头发保留视角相关光泽参数。第二套 UV 挑染、复杂 MOD Shader、粒子加法效果和原游戏后处理仍需额外适配。Blender 4.x 的兼容分支尚未在本机实际运行。

证据：

- [12 项 GPU 烘焙结果](../artifacts/model-export/bake-validation-v1/material-bake-validation.json)。
- [女性样本](../artifacts/model-export/blender-validation/final/edited-0/validation.json)、[男性样本](../artifacts/model-export/blender-validation/final/edited-1/validation.json)、[MOD 样本](../artifacts/model-export/blender-validation/final/edited-2/validation.json)。各目录内有 `before-front.png`、`after-front.png`、`before-face.png`、`after-face.png` 和打包贴图的 `character.blend`。
- 同目录的 `packed-scene-validation.json` 和 `face-eevee.png` 记录重新打开打包场景后的 Eevee 检查，入口为 `tests/Test-BlenderPackedScene.py`。
- [三张重新导出记录](../artifacts/model-export/blender-export-final.log)、[Unity 源码编译](../artifacts/model-export/blender-source-checks.log)、[核心检查](../artifacts/model-export/blender-core-checks.log)、[界面检查](../artifacts/model-export/blender-ui-checks.log)。
- 最终内核位于 `artifacts/model-export/player-blender-delivery`。导入脚本随角色包交付，不要求安装 Blender 插件。

## Blender 文本编辑器路径修复（2026-11-09）

此前材质测试直接调用 `import_character`，没有覆盖用户在文本编辑器按 Alt+P 的入口。在 Blender 5.3 的未保存场景中，实际复现 `__file__` 为 `\\Blender导入.py`，而 `Text.filepath` 为脚本真实绝对路径；旧入口因优先使用前者而找不到同目录的 FBX。

入口现优先使用活动文本的 `filepath`，通过 `bpy.path.abspath` 解析已保存场景中的 `//` 相对路径。未关联磁盘文件的文本提供中文操作提示，缺失文件显示完整查找路径，保留命令行入口与文件夹越界检查。

`tests/Test-BlenderTextImport.py` 清除测试命令行的角色参数后，调用与 Alt+P 相同的 `bpy.ops.text.run_script()`，实际载入下载目录中的中文角色包：30 个网格、30 个材质、152 张贴图。未保存场景、已保存场景的相对路径、内部文本提示、命令行参数及路径边界共 8 项检查通过。本次仅验证路径入口和导入，不增加材质渲染结论。

证据：[原错误复现](../artifacts/model-export/blender-path-fix/reproduction.json)、[修复后检查](../artifacts/model-export/blender-path-fix/verified/validation.json)。源码和该现有角色包的脚本已更新；此前发布的 ZIP 不包含此路径修复。

## Unreal 自动材质导入优化（2026-11-09）

使用本机 **Unreal Engine 5.8.4**（`58902052`），在独立临时项目中实际运行随包交付的 `Unreal导入.py`。重新导出女性、男性及女性 MOD 三个代表样本，共 **80 个源网格 / 80 个材质**；原卡 SHA-256 不变。此结果不扩展为 84 张卡的 Unreal 画面一致性验收。

| 检查 | 结果 |
| --- | --- |
| 新内核生成 FBX、PBR 贴图和 Unreal / Blender 导入脚本 | 3/3；随包脚本 SHA-256 与源码相同 |
| UE 骨骼网格导入、材质槽匹配及保存 | 3/3，3 个 Skeletal Mesh、80 个材质槽完整保留 |
| BaseColor/Emission sRGB、线性数据贴图、法线压缩和绿通道翻转 | 80 个材质全部通过 |
| 皮肤散射、眼球不透明、眼周 Unlit/Modulate、头发双面抖动遮罩 | 分类设置检查通过 |
| UE Morph Targets 导入 | 144 / 148 / 144；数量低于源通道总数，动画重定向不在本轮验收范围 |
| 重复导入保留已有材质及用户编辑标记 | 通过；新建独立目录 |
| 角色包路径边界、材质编号及缺失贴图检查 | 通过 |
| Unity GPU 烘焙 | 13/13，通过新增的无主法线头发细节检查 |
| 修复后 Unity 独立回读几何及表情 | 3/3，80 个网格、455 个形变目标；抽样最大位置误差 0.0006 毫米 |
| 主程序及 Unity 源码编译、核心回归、便携启动/关闭 | 通过；主程序及 Unity 源码编译 0 警告 / 0 错误 |

最主要的问题是旧 FBX 的多 UV 网格没有显式将材质索引挂到第 0 层。UE 只从该层读取材质，三个样本因此分别丢失 5 / 4 / 2 个材质槽，头部和部分头发错误套用身体材质。现在显式绑定到第 0 层，29 / 19 / 32 个材质槽全部保留，并在导入时检查完整性。重新计算切线或关闭法线只能掩盖该问题，不能修复材质对应关系。

另修复头发细节法线在缺少主法线时退化为单像素的问题。自动材质导入保留原发色，按原 Shader 的 N·V 关系近似头发光泽；头发使用切线空间 V 方向各向异性和写入深度的抖动遮罩，避免通用半透明发片的排序穿透。眼球保留烘焙虹膜和镜面反光，眼周乘色贴图不再作为普通受光表面。眼周 Modulate 使用非方向体积透明模式，避免 UE 为不支持的动态透明光照回退到默认材质。

测试项目使用传统材质路径（`r.Substrate=0`），没有修改用户项目设置；Substrate 项目尚未验证。Unity 2022.3 / Unity 6、UE 5.6、IK 重定向、原游戏后处理及复杂 MOD Shader 的画面一致性仍未验证。MOD 样本保留原有“配饰 3 资源不存在”诊断。

证据：

- [三个样本实际导出](../artifacts/model-export/unreal-packages-summary.json)、[UE 自动导入结果](../artifacts/model-export/unreal-optimized-validation/validation.json)。
- [GPU 烘焙检查](../artifacts/model-export/unreal-bake-v2/material-bake-validation.json)、[修复后 Unity 独立回读](../artifacts/model-export/unreal-unity-import-validation.json)。
- [UE 同灯光导入前后渲染记录](../artifacts/model-export/unreal-optimized-validation/render-validation.json)：三个角色的正面和面部截图；面部对照临时隐藏配饰以检查眼睛，全身保留配饰。不代表原游戏环境下的 1:1 还原。
- 最终导出内核为 `artifacts/model-export/player-unreal-layerfix`，程序集 SHA-256 为 `5AE7976C51D5BAC3A037C3D09BE0A70814EDA29D21A8BD029C541E90A5D7049F`。
- 可运行便携目录为 `artifacts/release/HS2Manager-unreal-optimized-win-x64`，内含相同内核，需用此版本重新导出角色包。
