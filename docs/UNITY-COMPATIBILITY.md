# Unity 预览兼容性说明修复

> 历史记录：本文的架构、命令、版本、性能和通过数量对应迁移前的 C# 实现及其当时的预览后端。旧源码现存于 `tests/reference/src`。本次 C++ 重写的实际检查见 [C++ 验证结果](CPP-VALIDATION.md)，当前构建与架构见 [C++ 迁移说明](CPP-MIGRATION.md)；本文数字不自动计入新版本验收。

2026-11-08。检查范围为 `Z:\games\hs2\HS two DX - TSYMQ\UserData\chara\female` 当前层全部 79 张 PNG 人物卡，不递归。

## 修复内容

- BonesFramework 清单中的扩展根骨骼可能互相嵌套。现在只移接最外层分支，保留其子骨骼层级，修复 QG03_00、QG05_00、joint2 等节点被重复移接和误报缺少连接点的问题。
- Unity 资源导出名按虚拟路径处理，避免未使用的导出名含 Windows 非法路径字符时中断整件服装加载。常规导出名查找失败后，按类型缓存资源内部名称别名，修复部分头发和服装贴图无法找到的问题。
- 按人物当前的发型 Shader 模式选择 TexD 或 TexC，仅为头发和 `typeHair` 配饰读取这组贴图。普通配饰清单里的无效发型贴图字段不再触发资源缺失提示。
- 服装只合成具有对应 Renderer 的材质组；未穿着服装的花纹不再解析。清单中未使用的第二、第三组占位贴图不会误报缺失。
- MaterialEditor 保存的属性在当前 Shader 上不存在时，按插件行为跳过。Shader 加载失败、内嵌贴图损坏等实际失败仍保留诊断。
- 空材质在最终显隐和扩展参数应用后检查，只报告当前可见网格的空材质。未显示的备用网格不再误报需要运行时材质分配。
- 已成功处理的 CAB 名称冲突不再计入兼容性问题。通用的画面及动态验收限制保留在预览下方固定说明中，不重复列入每张人物卡的诊断。
- 修复 Heelz 诊断在人物定义的诊断列表快照之后产生、导致提示丢失的问题。

## 检查方式

使用当前 `UnityCharacterService` 重新生成全部人物定义，再通过与管理器相同的命名管道、共享内存链路逐张加载实际 Unity Player。每张验证完成帧、有效包围盒和原卡 SHA-256，保存逐卡诊断与预览 PNG。检查结束释放会话，并验证本次内核进程已经退出。

原始检查记录位于源码工作区 `artifacts/compatibility-audit/runtime-before`，修复后记录位于 `artifacts/compatibility-audit/runtime-final`。目录中的 `results.json`、`report.md` 和 PNG 为开发检查产物，不进入发布包。

| 检查 | 结果 |
| --- | --- |
| 当前层人物卡 | 79/79 |
| 实际 Unity 加载与完成帧 | 79/79，0 失败 |
| 原卡 SHA-256 不变 | 79/79 |
| 逐卡诊断总条数 | 348 → 90（修复前包含 79 条重复的通用验收限制） |
| 无逐卡提示 / 有逐卡提示 | 33 / 46 |
| 核心检查、18 项布局检查、Unity 视口及切卡/重试/关闭清理检查 | 全部通过 |

79 张预览 PNG 及四张缩略总览均已检查。保留的诊断包括真实缺失的 MOD/贴图、原始 MOD 引用丢失、部分资源自身缺少扩展骨骼连接点，以及未安装或尚未实现效果的插件。

真实缺失 MOD、丢失原始来源的 `99999999` 引用、实际缺失贴图及未实现的物理插件效果仍保留说明。缺失的资源不会被任意其它资源替换。本次检查覆盖加载、首帧和诊断，不替代完整的游戏画面及动态对照；此前完整视觉验收记录见 [Unity 预览说明](UNITY-PREVIEW.md)。

## 复现

```powershell
.\build-unity-preview.ps1 -OutputPath artifacts\unity-preview\compatibility-check
.\.tools\dotnet\dotnet.exe run --disable-build-servers --project tests\HS2Manager.Checks -c CompatibilityCheck -p:UseSharedCompilation=false -- --prepare-unity-catalog --female-only --output artifacts\compatibility-check\definitions
.\.tools\dotnet\dotnet.exe run --disable-build-servers --project tests\HS2Manager.Checks -c CompatibilityCheck -p:UseSharedCompilation=false -- --audit-unity-compatibility --catalog artifacts\compatibility-check\definitions\catalog.json --player artifacts\unity-preview\compatibility-check\HS2Manager.UnityPreview.exe --output artifacts\compatibility-check\runtime
```

每次运行使用新目录。省略 `--female-only` 时，人物定义准备工具会额外包含原版女性和男性控制样本。
