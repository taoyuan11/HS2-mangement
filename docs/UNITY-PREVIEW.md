# 独立 Unity 预览

目标为本机整合包捏人界面的视觉一致。只有游戏画面对照通过后才可认定达到目标；加载成功和 Shader 球体测试不代表人物还原成功。

## 实现

- .NET 10 管理器负责人物卡、MOD、原始组件和场景数据读取。游戏程序集仅作为 PE/元数据读取，不装载进 CLR。
- 自有 Unity 2018.4.11f1 Windows x64 / Direct3D 11 Player 重建原始 Transform、Renderer 和蒙皮。不会实例化资源上的游戏 MonoBehaviour。
- 使用原始 Shader、材质、纹理合成流程；保留完整纹理尺寸和画质设置。支持 zipmod 内的独立 PNG 纹理、旧 DynamicBone 数据布局与原始动画。
- 读取捏人灯光、画质、环境 SH、DHH 天空与后处理、BetterAA 配置。独立实现眨眼、前向视线、动态链及身体物理。
- 命名管道传递命令，共享内存传递最终 BGRA。暂停、恢复、旋转、平移、缩放、环境恢复及截图均走新内核。
- 每个内核属于管理器创建的 Windows Job。取消、关闭、断连和进程异常均回收所属进程。

## 验收状态

2026-11-08 已完成 81 张人物卡（female 当前层 79 张、原版女样本、原版男样本）的全部对照，共 324 张静态配对图、1,944 张动态配对图。采集失败项已重试完成，当前没有缺失的卡片记录。

| 检查 | 结果 |
| --- | --- |
| 四视角静态数值与人物区域视觉复核 | 15/81 通过 |
| 24 个采样帧的动态数值 | 0/81 通过 |
| 完整视觉一致性验收 | 0/81 通过 |
| 原卡 SHA-256 不变 | 81/81 |
| 独立内核未加载游戏或插件程序集 | 81/81 |

**当前实现未达到完整 1:1 还原。** 原版女样本在最终完整对照中存在头部位置差异，不能沿用早期独立取景测试的通过结论。原版男样本静态通过，但动态眼睑仍有差异。逐卡失败区域见 [验收结果](UNITY-VALIDATION-RESULTS.md)。

完整图像报告在源码工作区 `artifacts/unity-validation/acceptance-28/index.html`，同目录包含 `summary.json`、`summary.csv`、`not-passed.md` 和每卡的四视角、差异图及动态帧记录；报告独立交付，不放入便携应用包。参考记录位于 `artifacts/unity-validation/all-cards-01`，人物定义目录清单为 `artifacts/unity-validation/catalog-11/catalog.json`。

最终 Player 为 `safe-diagnostics-30`。它仅移除会使旧引擎崩溃的可选纹理槽元数据枚举：595 个非诊断方法/字段的 IL 与批量对照构建 28 相同，控制样本的 4 张静态图及 24 张动态图 SHA-256 全部相同，因此保留其余 79 张已完成的对照；053、054 两张卡使用修正版重新采集。两端均保留材质名称、Shader、关键词、属性、队列、最终像素及面部/身体合成贴图，限制与原失败记录一并写入报告。

人物区域使用参考/独立图像与各自空背景图的差异并集。面部区域由参考头部几何在同一镜头下的投影包围范围确定。要求 SSIM ≥ 0.99、平均通道误差 ≤ 2/255，并需检查可见局部错误。动态采用 30Hz、固定随机种子和 360 帧序列；静态结果不能替代动态验收。

游戏参考工具位于 `tests/HS2Manager.GameReference`，仅用于显式开发测试。每张卡使用新游戏进程，避免插件状态跨卡残留。临时采集插件、自动保存配置和捏人设置在脚本结束后清理或恢复，并核对原卡 SHA-256。Studio 始终不用。该工具及游戏依赖不进入生产包。

## 尚未通过的部分

- 动态骨骼、待机姿态修正和完整 IK 的更新结果尚未与游戏一致。当前视线控制只覆盖前向模式，不能视为完整视线控制器已还原。
- 部分 MOD 需要额外的骨骼连接或运行时材质分配，日志会保留具体部件与节点名。已按游戏规则处理缺失发型的原版默认回退，并同时保留缺失与回退诊断；仍须逐卡对照，不能因回退后可加载而计为通过。
- 未知扩展、部分眼部覆盖层以及尚未实现的物理插件效果仍属未完成项。
- 静态参考中仍能看到局部场景表面差异。虽然两边使用相同抗锯齿预热帧数，当前采集没有记录原插件的私有帧相位；发丝抗锯齿差异仍受数值门槛约束，未据此放宽门槛。

每张卡的实际失败区域、动态采样帧、截图和诊断以最终报告为准。通过静态数值不代表动态通过，也不代表局部视觉复核通过。

## 复现

```powershell
.\bootstrap-unity.ps1
# 在 Unity Hub 激活可以构建 2018.4 的本机许可证后：
.\build-unity-preview.ps1 -OutputPath artifacts\unity-preview\my-build
.\.tools\dotnet\dotnet.exe run --project tests\HS2Manager.Checks --disable-build-servers -p:UseSharedCompilation=false -- --prepare-unity-catalog --output artifacts\my-catalog
.\.tools\dotnet\dotnet.exe build tests\HS2Manager.GameReference --disable-build-servers -p:UseSharedCompilation=false '-p:GamePath=Z:\games\hs2\HS two DX - TSYMQ'
.\tests\Invoke-UnityCatalog.ps1 -Catalog artifacts\my-catalog\catalog.json -PlayerDirectory artifacts\unity-preview\my-build -OutputPath artifacts\my-comparison
.\build.ps1 -UnityPlayerDirectory artifacts\unity-preview\my-build
```

每次验证使用新目录。测试会启动并回收游戏本体；日常管理器预览只启动自有内核。发布包包含管理器、Unity Player 和必要运行库，不包含测试图、游戏资源或用户数据。
