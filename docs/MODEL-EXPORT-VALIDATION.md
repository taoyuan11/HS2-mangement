# FBX 导出验证记录

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

本机没有 Unity 2022.3、Unity 6 URP 和 Unreal Engine 5.6，尚未执行这些目标环境的实际导入、材质及动画重定向验收。Unity 2018.4 回读只证明该环境中的文件结构、参考形态和基本变形，不替代目标引擎验收。

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
