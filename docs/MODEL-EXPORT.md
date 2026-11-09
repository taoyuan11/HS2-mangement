# FBX 可动画角色导出

在人物列表的底部操作栏、右键菜单、3D 预览工具栏或编辑页“更多”中选择“导出 3D 模型…”。列表支持单张和批量；编辑页在点击时取得不可变草稿，包含未保存修改，不写入原人物卡。

选择完整着装、内着或不穿衣物，以及是否包含配饰，然后选择游戏目录之外的输出位置。每张卡独立成包，同名目录加序号；导出在临时目录完成并校验后发布。取消删除本张尚未发布的临时输出，保留已经完成的角色；单张失败继续后续卡片。

## 输出内容

| 文件 | 内容 |
| --- | --- |
| `character.fbx` | 二进制 FBX，原骨架、扩展骨骼、网格、蒙皮、表情、材质槽、UV、法线、切线、顶点色 |
| `Textures/` | PNG 格式的 Base Color/Alpha、Normal、Metallic、Roughness、Occlusion，以及存在时的 Emission |
| `materials.json` | 材质槽与贴图对应关系、颜色空间、透明模式、双面及裁剪阈值 |
| `骨骼映射.json` | 唯一导出骨骼名、原 Transform 路径、Unity Humanoid 和 Unreal 人形骨骼对应关系 |
| `导入说明.txt` | Unity URP / Unreal 的材质接线、人形动画配置、法线方向及尺寸说明 |
| `Blender导入.py` | Blender 4+ 导入入口，按清单自动重建材质并放入独立角色集合 |
| `model-result.json` | 网格和骨骼数量、形变默认值、参考形态抽样及诊断 |
| `files.json`、`导出报告.txt` | 文件 SHA-256 清单和角色处理结果；输出目录另有批次总报告 |

结果区分完成、存在缺失、失败和取消。已知的通用材质近似显示为提示；角色加载阶段的缺失部件、未实现扩展等诊断列为“存在缺失”。用户可保留该包并按报告检查。角色包不包含游戏或插件程序集、原 AssetBundle、待机动画和物理组件。

原资源的骨骼引用不完整时，能够计算参考外形的网格仍可保留，并将缺失引用挂到有效根骨骼；对应动画需要人工检查。Unity 本身无法计算蒙皮的部件会跳过并明确列出，整个角色标为存在缺失。没有顶点的 LOD / billboard 占位对象不产生表面，直接忽略。

## 骨架、尺寸和表情

导出在角色组装完成、待机与物理模拟开始前取得参考姿态。体形、ABMX 和附加部件的变形通过 Unity 原生蒙皮计算转换到网格，权重保留，并为导出骨架重建绑定矩阵。没有权重的部件以单骨蒙皮附着，避免刚性配饰在动画中脱离。骨骼按 Transform 身份关联，同名节点有独立导出名称。

导出关节保留参考位置与旋转，使用单位缩放；体形的非均匀缩放已经烘焙到网格及关节位置，避免引擎重新解释继承缩放和剪切而使网格偏移。可选贴图槽会核对实际对象类型，旧资源中指向非贴图对象的无效引用使用默认值。

FBX 为右手 Y-up、厘米，源 HS2 单位按 10 厘米换算。使用引擎的单位转换，导入缩放保持 1。未将骨架改绑为 Unreal Mannequin 等标准骨架，需要按附带映射设置人形重定向。

部分引擎不保留 FBX 的形变默认权重。为保持首次导入的外观，保存的默认表情烘焙到基础网格，导出的形变默认值全部为 0。若需使用原始表情控制值，将 `HS2_ResetExpressions`（存在时）设为 100，再设置原始通道；`SourceDefaultWeights` 记录原始值，`DefaultWeights` 记录导出值。Unity 使用 0–100，Unreal Morph Target 使用对应的 0–1 数值。

## 通用材质与引擎设置

支持 Blender 的自动材质导入，以及 Unity 2022.3 / Unity 6 URP 和 Unreal Engine 5 的手动材质配置。不自动安装工具或生成 Humanoid、IK Rig。

### Blender

1. 建议在空场景打开 **Scripting** 工作区，在文本编辑器中 **Open** 导出文件夹内的 `Blender导入.py`，然后 **Run Script / Alt+P**。无需先手动导入 FBX。
2. 脚本读取旁边的 `character.fbx` 和 `materials.json`，创建独立角色集合，自动设置贴图、颜色空间、法线、透明和双面材质。重复运行会新增角色，已有角色的同名材质不会被覆盖。
3. 使用 **Material Preview** 或 **Rendered** 查看；选中角色后按小键盘 `.` 聚焦。Solid 模式不能用来判断最终材质。脚本保留场景灯光、相机和色彩管理；灯光与曝光需按自己的场景调整。
4. 另存 `.blend`；需要独立携带时，使用 **File → External Data → Pack Resources** 打包贴图。

中文界面可将鼠标放在 3D 视图内按 `Shift+F11` 进入文本编辑器，选择 **文本 → 打开** 载入脚本，再将鼠标放在代码区域按 `Alt+P`。脚本使用文本编辑器记录的真实文件路径定位角色包；Blender 的 `__file__` 可能只是内部文本名称。更新磁盘上的脚本后，应重新打开文件，避免继续运行内存中的旧版本。缺失文件的报错会显示实际查找的完整路径。

支持 Blender 4.0 及以上 API；实际验证版本和范围见 [验证记录](MODEL-EXPORT-VALIDATION.md)。命令行入口：

```powershell
blender --background --python '角色文件夹/Blender导入.py' -- '角色文件夹'
```

材质保留独立且可编辑的节点：皮肤采用少量散射，头发采用 UV 切线各向异性，眼球采用清漆层；眼周乘色阴影使用透明着色器，避免变成受光的灰色面片。原头发的抖动覆盖率转为连续透明度；Eevee 与 Cycles 的透明排序和阴影仍可能不同。`HS2 AO influence` 节点控制 AO 对基础色的额外影响，默认 0.35，设为 0 可关闭；这是目标软件中的近似参数。

新版烘焙按 AIT 通道解释皮肤和服装的金属度/光泽，按原遮罩合成头发渐变，并修正眼球的虹膜/瞳孔 UV、瞳孔 Alpha 和颜色 Alpha。已有旧包可使用新导入脚本补齐接线，但贴图中的旧烘焙误差需要重新导出才能修正。

### Unity / Unreal

- Base Color 和 Emission 使用 sRGB；法线、金属度、粗糙度、AO 使用线性数据。
- 法线是 OpenGL +Y。Unity 设为 Normal Map；Unreal 开启 Flip Green Channel。
- Unity URP 需要将 Roughness 取反为 Smoothness，再按金属贴图的 R/A 通道打包；Unreal 直接连接独立灰度贴图。
- Mask 模式开启 Alpha Clipping / Masked，Blend 模式使用 Transparent / Translucent；按 `DoubleSided` 设置双面。
- 皮肤、妆容、服装图案和覆盖层复用已经合成的贴图；眼睛和头发转换为通用基础色。原游戏合成贴图的 Alpha 可能保存其他数据，导出时按材质类型转换并应用遮罩。

Blender 的皮肤散射、眼球清漆和头发各向异性属于通用近似；Unity / Unreal 需要自行设置对应效果。游戏专用的视角相关高光、眼球视差折射、第二套 UV 头发挑染、MatCap、描边和后处理未完整迁移。未知 Shader 使用近似并列入报告，不承诺还原原游戏的全部视觉效果。

## 构建与验证

`build-unity-preview.ps1` 自动获取并校验官方 `com.autodesk.fbx` 4.2.0，启用运行时绑定。为兼容 Unity 2018，嵌入包排除其可选测试目录，运行库未修改。SDK 和第三方许可随便携包分发，使用导出时不需要 Unity 编辑器或 Blender。

```powershell
.\build-unity-preview.ps1
.tools\dotnet\dotnet.exe build tests\HS2Manager.UnitySourceChecks -c Release --disable-build-servers
.tools\dotnet\dotnet.exe run --project tests\HS2Manager.Checks -c Release -- --model-export --player '<Player.exe>' --catalog '<人物卡目录清单.json>' --count 3 --draft --output '<输出目录>'
.\scripts\Test-ModelExport.ps1 -OutputDirectory '<输出目录>' -EditorPath '<Unity.exe>'
.\scripts\Test-ModelMaterialBake.ps1 -OutputDirectory '<新的烘焙检查目录>'
.\scripts\Test-BlenderExport.ps1 -PackageDirectory '<角色文件夹>' -OutputDirectory '<新的检查目录>' -BlenderPath '<blender.exe>'
.tools\dotnet\dotnet.exe run --project tests\HS2Manager.Checks -c Release -- --model-export-lifecycle --player '<Player.exe>' --definition '<角色定义.json>' --output '<检查目录>'
```

固定基线结构检查入口为 `--model-export-catalog`，接受 `--catalog`、`--player`、`--output`。此检查逐卡实际写出 FBX 和贴图，核对结构、贴图文件与原卡 SHA-256，保留统计及诊断后清理大体积资产；需要保留完整包时使用 `--model-export`。

回读检查使用独立 Unity 项目，仅载入导出包，检查蒙皮参考形态、形变数量与默认值、眨眼/嘴部变形以及手臂运动。引擎可能在 UV 和切线接缝处分裂或重排顶点，因此按几何位置匹配抽样，允许最大 2 毫米误差。另输出基础色材质的正反面截图用于检查外观；截图不代表完整 URP 或 Unreal 材质验收。

当前机器只有 Unity 2018.4。Unity 2022.3、Unity 6 URP 与 Unreal 5.6 的实际导入验收尚未执行；旧版 Unity 回读结果不能替代这些目标环境的验收。既有 84 张人物卡的游戏预览一致性结论属于原预览功能，不能用于声称 FBX 或通用材质具有相同画面。

本次 84 张结构检查及 8 张独立回读的结果见 [导出验证记录](MODEL-EXPORT-VALIDATION.md)。
