# 预览材质与环境检查

> 历史记录：本文的架构、命令、版本、性能和通过数量对应迁移前的 C# 实现及其当时的预览后端。旧源码和专用脚本已移除，历史命令需检出相应 Git 版本后运行。本次 C++ 重写的实际检查见 [C++ 验证结果](CPP-VALIDATION.md)，当前构建与架构见 [C++ 迁移说明](CPP-MIGRATION.md)；本文数字不自动计入新版本验收。

2026-11-08，针对“殿堂-烈翎冰鸾”预览服装偏白、游戏中反光更强的截图进行检查。

## 结论

本地存在两张同名、外形相近但服装材质不同的卡。分别使用原始卡片重新准备定义并通过当前独立内核采集，与已有同卡游戏参考比较：柔白版在游戏中也呈柔白，高反光版在独立预览中也能呈现银色服装的凹凸和镜面反光。截图中的显著材质差异与这两个版本的差异一致。

| 文件 | SHA-256 前缀 | 保存的材质修改数 | 外观 |
| --- | --- | ---: | --- |
| `HS2ChaF_20250820095557637.png` | `A130A3C9E7D3` | 22 | 柔白服装 |
| `HS2ChaF_20250820100501303.png` | `82893C1D774B` | 75 | 更强的法线细节及金属反光 |

这些记录包括不同的服装资源、细节法线强度及光泽参数。本次未修改原卡或统一提高材质金属度。两张原卡的完整 SHA-256 均与原采集记录一致。

## 修正

- 原界面调整灯光时会同时应用背景下拉框的纯色值，导致游戏背景被切走；现在灯光与背景选择独立，默认显示“游戏背景”。纯色背景继续使用原来的天空和环境反射资源。
- 灯光滑条从绝对强度改为相对原始主灯强度的倍率。1 倍准确对应游戏环境，多次调整基于原值计算，避免强度累乘。
- 恢复环境使用环境创建完成时的实际背景、清屏方式和灯光强度，并清除后处理历史，避免旧画面的残留。恢复或切卡同步复位界面控件。
- 环境命令携带当前加载编号，内核忽略旧人物的环境命令。
- 预览标题和人物列表增加文件名，悬停显示完整路径，帮助区分同名卡。

## 验证

- .NET Release 构建、Unity 2018.4 API 编译和独立 Player 构建通过。源码启动标记指向 `artifacts/unity-preview/material-quality`。
- 两张卡各采集四视角和动态帧，原始图像及对比图保存在 `artifacts/material-quality`。`card-material-comparison.png` 使用相同裁切展示游戏参考与本次预览，未调整颜色或锐度。
- 修改前的当前内核执行“高反光 → 柔白 → 高反光”资源复用检查，最终四视角与单独加载高反光版的 PNG SHA-256 完全一致，未发现材质串卡。证据为 `reuse-comparison.json`。
- 新增内核环境检查验证背景、主灯倍率、反射及环境光保留、重复调整不累乘和精确恢复，两张卡均通过。结果在 `final-metallic/environment-controls.txt` 和 `final-soft/environment-controls.txt`。
- 18 个主题/缩放布局检查通过。真实 Unity UI 回归覆盖灯光/背景绑定、恢复、切卡时复位、文件身份、截图、快速切卡、失败重试及进程退出，记录在 `artifacts/unity-ui-checks/2dda399d2ab14e93b50543eef90fdac9`。

这不是完整 1:1 视觉验收。沿用已有游戏参考与人物区域差异并集，四视角 SSIM 约 0.970–0.986，仍未达到项目的 0.99 门槛；脚下场景表面、局部轮廓与动态差异继续按既有验收记录保留。本次没有重新启动游戏采集参考。数值记录为 `static-comparison.json`。

复现单卡及环境检查（输出目录必须尚不存在）：

```powershell
.\tests\Invoke-UnityCharacter.ps1 -PlayerDirectory artifacts\unity-preview\material-quality -Definition artifacts\material-quality\metallic.json -Fixture artifacts\unity-validation\all-cards-01\041-82893C1D774B\reference\fixture.json -OutputPath "$PWD\artifacts\material-quality\new-run" -CheckEnvironmentControls
```

重新运行根目录 `start.cmd` 使用源码更新。已解压的旧便携包需要同时更新管理器与 Unity Player。本次所有构建和测试进程均已结束；未停止已有用户进程。

## 殷紫萍丝袜横条纹修复

2026-11-08，原卡 `殷紫萍.png`（SHA-256 `B68363232A80BEBA1C92E16A5D1B4D692597A3F5101F1FA6F503FA9B43A66CD7`）在预览中显示横条纹，游戏中为连续的浅色丝袜。

原因是同一 GUID `com.boy2239socks and panst` 在本机有 6 个历史包，均未填写版本号。游戏 Sideloader 选择修改时间最新的 `mods/[Boy2239]Socks and panst.zipmod`；旧预览按版本号及路径排序，选中了 `mods/殷紫萍/[Boy2239] Socks and panst.zipmod`。两者包含不同网格、材质和组件设置，不能通过修改颜色或透明度修复。

现在先按游戏规则为每个 GUID 选择一个有效包：全部填写版本号时按分段版本比较，同版本优先较长路径；任一包缺少版本号时，整组按文件修改时间选择。服装清单、资源目录、独立 PNG、Shader 清单、名称恢复及依赖迁移均使用这组有效包，旧包不会补入已被移除的资源。资源目录缓存键已更新，已有缓存无需手动清除。

验证记录在 `artifacts/stocking-fix`：

- 使用原卡重新采集游戏捏人参考，修复前后独立预览均使用参考的相机与采样配置。`stocking-comparison.png` 是同位置裁切的修复前、修复后和游戏画面；未改变颜色或锐度。
- 修复后横条纹消失，`o_panst_a` 启用、`o_panst_b` 停用，材质 `cf_m_panst_01`、Shader `AIT/Clothes Alpha True`、队列 2620 均与游戏一致。提取的丝袜资源包与游戏所选归档内原始资源包 SHA-256 完全相同（`95C83CB8CAF3481EAFCD2C13B95AF482CA3A0E7BEEFDFF4A965A85C5A5D7E7A0`）。
- 新增重复 GUID 回归覆盖缺失版本号、数字和后缀版本、同版本路径规则，以及淘汰包中的服装、资源、PNG 和迁移不得重新生效。Debug/Release 核心检查与 Release 解决方案构建通过，构建零警告、零错误。
- 原角色卡 SHA-256 不变。游戏参考脚本已移除临时采集插件、恢复捏人及自动保存设置，所有本次启动的进程均已结束。

本次修复的是丝袜资源选择。完整人物静态与动态数值验收仍未通过；同镜头四视角人物区域 SSIM 为 0.935–0.971，报告在 `comparison/report.json`。本次仅修改管理器 Core，无需重新构建 Unity Player；重新运行根目录 `start.cmd` 后重新打开角色预览即可使用修复。
