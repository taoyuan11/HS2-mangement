# 通用、Blender 与 Unreal 模型导出

人物列表的操作栏、右键菜单、3D 预览工具栏及编辑页“更多”均提供“导出 3D 模型…”。选择导出类型、服装和配饰范围，再选择游戏目录之外的输出位置。列表支持单张及批量；编辑页捕获点击时的未保存草稿，不写入原人物卡。

| 类型 | 打开的文件 | 内容 |
| --- | --- | --- |
| 通用 3D 模型（FBX） | `character.fbx` | 骨架、蒙皮、形变、材质槽、UV、法线、切线，配套 PBR 贴图及 `materials.json` |
| Blender 3D 模型 | `character.blend` | 可编辑模型、骨架、表情、材质节点，打包贴图及环境资源，Eevee 场景 |
| Unreal 3D 模型 | `HS2Character.uproject` | Skeletal Mesh、Skeleton、Morph Targets、材质、贴图和默认 Preview 关卡 |

通用包不再附带 Blender 或 Unreal Python 脚本。原生转换由程序调用本机软件完成，用户直接打开结果，无需手动运行脚本。原生交付包不含内部 FBX、Python 和贴图中间文件。

## 软件配置与打开方式

在 **设置 → 模型导出** 中选择 `blender.exe` 或 `UnrealEditor-Cmd.exe`；选择 `UnrealEditor.exe` 时自动使用同目录的命令行程序。自动检测常见安装目录、注册信息和 PATH，Blender 也检测桌面或下载目录中的便携版。导出弹窗允许调整并保存路径。

导出前运行软件能力检查。工具缺失、必要接口不可用或转换失败时明确报错，不将中间 FBX 当作原生结果发布。通用 FBX 无需安装目标软件。实际测试版本见[验证记录](MODEL-EXPORT-VALIDATION.md)，其他版本不默认视为兼容。

Blender 打开 `character.blend`，使用 Rendered 视图或 F12 查看。模型与预览场景分组，灯光、相机和材质可编辑；贴图及环境资源已打包，环境光探针可重新烘焙。

Unreal 打开项目后默认进入 `/Game/HS2Character/Preview`。角色资产位于 `/Game/HS2Character/Character`，可使用 Asset Actions → Migrate 移入自己的项目。生成项目启用 Python Editor Script Plugin 和 Editor Scripting Utilities，使用传统材质路径、TSR 和固定曝光。相关设置仅写入生成项目；首次打开可能需要编译着色器。

## 场景与画面

预览和编辑页冻结点击时的相机、画幅、背景与灯光调整；列表使用默认游戏环境和自动取景。所有目标沿用同一份草稿、服装和配饰配置。角色使用参考姿态及默认表情，不捕获待机动画、物理或点击瞬间的姿态。

独立内核重建实际预览环境，捕获有效灯光、环境照明、反射及求值后的调色 LUT，并将环境贴图和 LUT 转为可携带资源。纯色背景与环境照明分开处理。原生材质保留皮肤散射、头发渐变、UV1 挑染、各向异性、眼睛高光、眼周乘色、泪线、睫毛和透明遮罩的可编辑实现。

跨引擎 BRDF、散射、环境响应、透明排序与屏幕空间效果仍有差异。Bloom、AO、暗角以目标软件参数近似；无法迁移的折射、MOD Shader、SSR、色差等逐项记录在报告中。原生包附 Unity 的 `预览参考.png`，Blender 还输出回读渲染的 `预览.png`。旧预览或 FBX 验收不等于新原生场景的画面一致性验收。

## 骨架、尺寸与表情

保留原骨架、扩展骨骼和权重。体形、ABMX 与非均匀缩放烘焙到参考网格和关节位置，并重建绑定矩阵。刚性配饰采用单骨蒙皮。空占位对象忽略，无法计算的部件明确列为缺失。

中间 FBX 使用右手 Y-up、厘米，HS2 源单位按 10 厘米换算。目标软件自动转换单位，不需额外缩放。未自动改绑为 Unreal Mannequin 等标准骨架，附带的 `骨骼映射.json` 可用于 Unity Humanoid 或 Unreal IK Rig / Retargeter。

默认表情烘焙到基础网格，导出形变默认权重全部为 0，避免重复叠加。需要使用游戏原始控制值时，先设置 `HS2_ResetExpressions`（存在时），再设置原始通道。Unity 使用 0–100，Blender 与 Unreal 使用对应的 0–1 数值。`model-result.json` 保存原始及导出默认值和参考形态抽样。

## 通用 FBX 手动材质配置

- BaseColor、Emission 使用 sRGB；Normal、Metallic、Roughness、Occlusion、HairStreakMask 使用线性数据。
- 法线为 OpenGL +Y；Unreal 翻转绿色通道，Unity 设置为 Normal Map。
- Unity URP 将 Roughness 取反为 Smoothness，并按金属贴图 R/A 通道打包。
- AlphaMode 为 Mask 时使用裁剪，为 Blend 时使用半透明；按 AlphaCutoff 和 DoubleSided 设置阈值及双面。
- 挑染使用 HairStreakUv 指定的 UV 通道、独立遮罩、强度和颜色。仅导入 FBX 不会自动构建专用材质。

## 发布、报告与清理

每张卡独立成包，同名目录自动编号。转换、结构和依赖检查、独立进程回读完成后才发布。取消清理尚未发布的角色，保留此前完成的角色；逐角色失败继续后续卡片。

各包附骨骼映射、说明、`model-result.json`、`导出报告.txt` 和 `files.json` SHA-256 清单；原生结果另记录目标软件实际版本、入口及回读结果。完成与“存在缺失”分开统计，具体内容见报告。

中间资源及脚本位于应用数据目录的任务工作区，结束时清理。Unreal 本地派生数据缓存可复用，交付包不携带缓存。转换进程及子进程使用 Windows Job 管理，在完成、失败、超时、取消和应用退出时回收。默认各阶段超时 900 秒。

## 构建与验证

内核协议升级为 v4，应用与 Player 必须一起更新。`build-unity-preview.ps1` 获取并校验 Autodesk FBX SDK 4.2.0；运行时无需 Unity 编辑器。

```powershell
.\build-unity-preview.ps1
.tools\dotnet\dotnet.exe build tests\HS2Manager.UnitySourceChecks -c Release --disable-build-servers
.tools\dotnet\dotnet.exe run --project tests\HS2Manager.Checks -c Release -- --model-export --player '<Player.exe>' --card '<人物卡.png>' --target Blender --tool '<blender.exe>' --draft --output '<输出目录>'
.tools\dotnet\dotnet.exe run --project tests\HS2Manager.Checks -c Release -- --native-model-export --player '<Player.exe>' --definition '<角色定义.json>' --target Unreal --tool '<UnrealEditor-Cmd.exe>' --output '<新检查目录>'
.tools\dotnet\dotnet.exe run --project tests\HS2Manager.Checks -c Release -- --model-export-lifecycle --player '<Player.exe>' --definition '<角色定义.json>' --output '<新检查目录>'
```

`--model-export` 支持 GenericFbx、Blender、Unreal，覆盖服务及原卡校验。`--native-model-export` 从定义生成原生文件，搬移到中文路径并使内部源目录不可访问，再次独立回读；`--view` 可提供 ModelExportView JSON 以生成同机位对照。旧 Test-BlenderExport / Test-UnrealExport 脚本用于内部材质构建器回归，不代表新的原生发布流程。

结构检查沿用最大 2 毫米的参考形态抽样阈值。实际版本、样本、截图及未覆盖范围记录在[验证记录](MODEL-EXPORT-VALIDATION.md)。
