# 通用、Blender 与 Unreal 模型导出

本文说明当前 C++ 版本的模型导出流程。本次重写的实测结果见 [C++ 验证结果](CPP-VALIDATION.md)；[旧原生 GPU 迁移验收](NATIVE-MIGRATION-RESULTS.md) 和 [旧模型导出验证](MODEL-EXPORT-VALIDATION.md) 属于 C# 实现的历史证据。

人物列表的操作栏、右键菜单、3D 预览工具栏及编辑页“更多”均提供“导出 3D 模型…”。选择导出类型、服装和配饰范围，再选择游戏目录之外的输出位置。列表支持单张及批量；编辑页捕获点击时的未保存草稿，不写入原人物卡。

| 类型 | 打开的文件 | 内容 |
| --- | --- | --- |
| 通用 3D 模型（FBX） | `character.fbx` | 骨架、蒙皮、形变、材质槽、UV、法线、切线，配套 PBR 贴图及 `materials.json` |
| Blender 3D 模型 | `character.blend` | 可编辑模型、骨架、表情、材质节点，打包贴图及环境资源，Eevee 场景 |
| Unreal 3D 模型 | `HS2Character.uproject` | Skeletal Mesh、Skeleton、Morph Targets、材质、贴图和默认 Preview 关卡 |

通用包不再附带 Blender 或 Unreal Python 脚本。原生转换由程序调用本机软件完成，用户直接打开结果，无需手动运行脚本。原生交付包不含内部 FBX、Python 和贴图中间文件。

## 软件配置与打开方式

在 **设置 → 模型导出** 中选择 `blender.exe` 或 `UnrealEditor-Cmd.exe`；选择 `UnrealEditor.exe` 时自动使用同目录的命令行程序。自动检测常见安装目录、注册信息和 PATH，Blender 也检测桌面或下载目录中的便携版。导出弹窗允许调整并保存路径。

导出前运行软件能力检查。工具缺失、必要接口不可用或转换失败时明确报错，不将中间 FBX 当作原生结果发布。通用 FBX 无需安装目标软件。实际测试版本见 [C++ 验证结果](CPP-VALIDATION.md)，其他版本不默认视为兼容。

Blender 打开 `character.blend`，使用 Rendered 视图或 F12 查看。模型与预览场景分组，灯光、相机和材质可编辑；贴图及环境资源已打包，环境光探针可重新烘焙。

Unreal 打开项目后默认进入 `/Game/HS2Character/Preview`。角色资产位于 `/Game/HS2Character/Character`，可使用 Asset Actions → Migrate 移入自己的项目。生成项目启用 Python Editor Script Plugin 和 Editor Scripting Utilities，使用传统材质路径、TSR 和固定曝光。相关设置仅写入生成项目；首次打开可能需要编译着色器。

## 场景与画面

预览和编辑页冻结点击时的相机、画幅、背景与灯光调整；列表使用默认游戏环境和自动取景。所有目标沿用同一份草稿、服装和配饰配置。角色使用参考姿态及默认表情，不捕获待机动画、物理或点击瞬间的姿态。

原生场景重建实际预览环境，捕获有效灯光、环境照明、反射及求值后的调色 LUT，并将环境贴图和 LUT 转为可携带资源。纯色背景与环境照明分开处理。原生材质保留皮肤散射、头发渐变、UV1 挑染、各向异性、眼睛高光、眼周乘色、泪线、睫毛和透明遮罩的可编辑实现。

跨引擎 BRDF、散射、环境响应、透明排序与屏幕空间效果仍有差异。Bloom、AO、暗角以目标软件参数近似；无法迁移的折射、MOD Shader、SSR、色差等逐项记录在报告中。原生包附同一原生 GPU 渲染结果生成的 `预览参考.png`，Blender 还输出回读渲染的 `预览.png`。旧预览或 FBX 验收不等于新原生场景的画面一致性验收。

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

通用 FBX 由独立 Autodesk FBX SDK 2020.3.7 适配层直接从共享场景生成。Blender 与 Unreal 的转换、独立重开和事务发布由 C++ 服务管理；没有 CLR、Unity Player、编辑器或 UnityEngine 依赖。转换脚本位于 `src/HS2Manager.NativeExport/Resources/ModelExport`，构建时复制到应用的 `Resources/ModelExport`，由目标软件自己的 Python 接口执行。

C++ 版本已使用同一张真实人物卡执行通用 FBX、Blender 5.3.0 Release Candidate 和 Unreal 5.8.4 的完整服务导出，原生结果由独立进程重新打开，包内文件逐项核对 SHA-256。该卡缺少一项原始配饰资源，报告正确标记为“不完整”。本次样本数量、几何和资产统计见 [C++ 验证结果](CPP-VALIDATION.md) 及 [导出模块记录](../src/HS2Manager.NativeExport/MIGRATION.md)。旧版四组样本、489 张贴图及搬移检查保留在历史验收中，不扩展为本次重写的通过结论；结构验证也不表示跨引擎画面逐像素相同。

```powershell
.\scripts\build\Build-Cpp.ps1 -BuildDirectory C:\Build\HS2Manager
# 不带参数运行合成 GPU、图像及导出选项检查；完整构建也通过 CTest 运行该检查。
C:\Build\HS2Manager\modules\src\HS2Manager.NativeExport\Release\hs2_model_export_checks.exe
# 可选实际服务导出：使用完整运行目录、本机独立测试资料及游戏目录之外的新输出位置。
C:\Build\HS2Manager\modules\src\HS2Manager.NativeExport\Release\hs2_model_export_checks.exe --real '<应用运行目录>' '<游戏目录>' '<本机测试资料目录>' '<人物卡.png>' '<新输出目录>' Blender
```

测试和转换启动的工具通过 Windows Job Object 登记所有权，正常结束、失败、超时、取消和宿主退出均回收本次启动的进程及后代，不终止用户已有程序。
