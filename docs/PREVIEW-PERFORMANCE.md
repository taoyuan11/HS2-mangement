# 模型加载性能验证

> 历史记录：本文的架构、命令、版本、性能和通过数量对应迁移前的 C# 实现及其当时的预览后端。旧源码现存于 `tests/reference/src`。本次 C++ 重写的实际检查见 [C++ 验证结果](CPP-VALIDATION.md)，当前构建与架构见 [C++ 迁移说明](CPP-MIGRATION.md)；本文数字不自动计入新版本验收。

> 本文性能数字记录迁移前的 WPF 宿主，不代表 WinUI 3 的测量结果。当前界面和回归检查已迁移到 WinUI 3，见 [WINUI3-MIGRATION.md](WINUI3-MIGRATION.md)。

2026-11-08，在同一台 Windows 电脑、同一套 `Z:\games\hs2\HS two DX - TSYMQ` NAS 资源上比较。基线为修改前 `9ee317b` 的 Release 管理器及 `safe-diagnostics-30` Player；优化版为本次 Release 管理器及 `loading-performance-release` Player，Unity 2018.4.11f1、Direct3D 11。

## 实测结果

以下为各组耗时的**中位数**，单位秒。首帧终点是 WPF 视口完成对应加载请求的 `OnRender`，包含人物准备、必要的内核启动、Unity 构建和帧传输。

| 场景 | 样本数/版本 | 修改前 | 修改后 | 耗时下降 |
| --- | ---: | ---: | ---: | ---: |
| 已有缓存，重复准备人物定义 | 8 | 2.768 | 0.369 | 86.7% |
| 同会话连续切卡，到首帧 | 20 | 8.509 | 1.720 | 79.8% |
| 关闭预览后重新打开，到首帧 | 4 | 13.126 | 9.280 | 29.3% |
| 全新应用缓存首次打开原版女性，到首帧 | 1 | 12.819 | 9.495 | 25.9% |

重复准备降低 50%、切卡首帧降低 30%、首次打开不明显退化的目标达到；**重新打开降低 30% 的目标未达到**，实测差 0.7 个百分点。比较脚本保留此失败，`release-comparison.json` 的 `passed` 为 `false`，不能将本次结果描述为全部性能验收通过。

首次无缓存的纯准备阶段从 5.213 秒增加到 5.797 秒（增加 11.2%），包含首次索引和元数据写入。并行启动内核后，实际首帧等待仍减少。关闭预览会结束内核，重新打开仍需约 2.8–3.0 秒启动和约 3.8–4.0 秒加载原始资源；这部分不会被元数据缓存消除。

| 人物类型 | 切卡：修改前 → 修改后 | 关闭后重开：修改前 → 修改后 |
| --- | ---: | ---: |
| 原版女性 | 9.173 → 1.600 | 13.540 → 9.289 |
| 原版男性 | 7.996 → 1.302 | 12.858 → 8.839 |
| 重 MOD | 7.681 → 1.986 | 13.394 → 9.270 |
| 大贴图 | 9.164 → 1.792 | 12.854 → 10.001 |

这是一轮有限样本的同机测量，不是跨设备保证。每类切卡 5 次、重开 1 次；20 次切卡的优化后 P95 为 2.484 秒。准备测试和 UI 测试分别使用全新 Data 目录，未清空 Windows/NAS 文件缓存。每组仅第一个原版女性样本完全没有应用缓存，后续首次打开会复用公共数据，因此不将四类 `open-first` 的合并数值当作四次冷启动。

## 样本与证据

| 类型 | 人物卡 |
| --- | --- |
| 原版女性 | `UserData/chara/navi/navi.png` |
| 原版男性 | `UserData/chara/male/HS2_ill_M_000.png` |
| 重 MOD | `UserData/chara/female/HS2ChaF_20250705104015073.png`，本机依赖引用最多的有效卡 |
| 大贴图 | `UserData/chara/female/HS2ChaF_20250524170826719.png`，本机最大的有效卡（约 144.7 MB），用于覆盖大量纹理数据 |

本地原始证据位于工作区 `artifacts/preview-performance`，不包含在便携包内：

- `before/measurements.json`、`release/measurements.json`：逐次耗时、管理器和内核私有内存。
- `release/UiData/Logs/preview-performance.jsonl`：分阶段耗时、缓存/归档计数和实际首帧。
- `release-comparison.json`：本报告使用的最终比较结果；不使用中间版本测量替代最终结果。
- `before-visual`、`release-visual`：相同人物、固定镜头和固定动态帧的图像、骨骼/材质状态及诊断。
- 各视觉目录的 `run.json`：人物卡、人物定义、内核哈希，以及资源路径、长度和修改时间。

## 画面与动态回归

四个人物分别比较正面、侧面、背面、面部等固定视图、材质相关捕获，以及 24 个固定动态时刻。优化版先执行 A→B→A，再捕获 A，覆盖跨卡资源复用。四份人物定义在归一化缓存目录后完全相同；骨骼、渲染器/材质属性、动态姿态及诊断文字均相同。

160 张 PNG 中 155 张像素完全一致，其余 5 张通过既定数值容差：角色区域 SSIM ≥ 0.9999，平均通道误差 ≤ 0.01/255。采用两图角色区域的并集计算，避免背景稀释误差。实测最低 SSIM 为 0.999999801，最大角色区域平均通道误差为 0.00003183/255。

| 有差异的图像 | 变化像素数 | 单通道最大差值（0–255） |
| --- | ---: | ---: |
| 重 MOD，正面 | 50 | 1 |
| 重 MOD，动态帧 210 | 38 | 11 |
| 重 MOD，动态帧 270 | 1 | 1 |
| 重 MOD，动态帧 345 | 2 | 1 |
| 大贴图，正面 | 556 | 5 |

额外复测中，旧版重复捕获、新版不经过 A→B→A 的直接构建均像素一致；上述细微差异出现在资源复用捕获中，不能称为逐像素完全相同。本次没有降低模型细节、纹理尺寸或后处理设置。此回归只证明与修改前独立预览器接近，不代表已经完成与游戏画面的逐卡一致性验收。

## 缓存、内存及失效规则

MOD 清单同时保存归档资源目录和条目长度，人物 CSV 清单和跨包查找目录持久化。20 次热切卡中，MOD 归档打开次数和自动全量扫描次数均为 0；每次保留 19–32 个公共 Unity 资源包。

原始骨架、组件、形变基础数据、依赖和静态场景数据采用独立快照。每次应用人物参数前重新反序列化，避免共享可变对象；人物和环境配置仍按当前文件读取。单次准备最多同时保留 3 个资源读取器，完成或取消后释放。

新增元数据缓存目录为 `Data/UnityPreview/metadata-v1`，使用游戏目录、资源路径、大小、修改时间和版本区分条目。序列化内存总预算 128 MiB，单个 Data 目录磁盘预算 512 MiB，按最近使用淘汰；有校验和、临时写入和取消检查，损坏后重建。预算仅针对新增的序列化元数据，不包含反序列化对象、已提取资源、Unity 原始资源、人物实例、合成纹理或显存。

本次 UI Data 中元数据占 **8.30 MiB**；测试进程同时使用准备和 UI 两个 Data 目录，元数据内存合计 **16.59 MiB**。Unity 资源存储保留下一人物仍需要的资源，卸载旧人物独占资源；源资源发生变化时清空可能仍持有旧依赖引用的 Unity 资源存储。修改后的材质、合成纹理和人物实例另行销毁，不作为跨卡缓存。

| 类型 | 基线内核私有内存峰值 MiB | 优化版峰值 MiB | 优化版首次 → 最后一次同类切卡 MiB |
| --- | ---: | ---: | ---: |
| 原版女性 | 1509 | 1813 | 1809 → 1813 |
| 原版男性 | 1308 | 1714 | 1706 → 1708 |
| 重 MOD | 1780 | 2033 | 2028 → 2031 |
| 大贴图 | 3750 | 4030 | 3926 → 4030 |

资源复用增加约 253–405 MiB 私有内存；20 次切卡内同类样本最后一次占用均低于首次数值 × 1.05 + 64 MiB 的阈值，未见持续随次数攀升。该检查只能说明这轮测试的趋势，不是无限期运行无泄漏的证明。关闭预览释放资源并终止所属内核进程。

已索引归档的大小或修改时间变化会重建受影响条目。相同索引版本的同一缺失项合并自动刷新；不同缺失引用仍可触发发现新归档。手动“更新 MOD 索引”发布新版本并清除缺失查询结果。安装或删除 MOD 后仍建议更新索引。缓存命中也检查源文件，资源离线不能依靠元数据缓存伪装为可用。

日志 `Data/Logs/preview-performance.jsonl` 记录 `prepare`、`startup`、`kernel`、`firstFrame`。准备阶段有 `card`、`stockIndex`、`structure`、`shape`、`modCatalog`、`environment` 等子项及缓存计数。阶段存在嵌套，不能简单相加；约 4 MiB 轮转并保留上一份日志。

## 功能检查与复现

检查覆盖缓存损坏、大小/时间变更、源资源删除、可变数据隔离、并发共享、LRU 淘汰、取消不写入半成品、旧 MOD 索引升级、重复缺失查询及当前环境配置。Unity 通信检查覆盖原生资源读取中取消、人物构建中取消、公共包复用、同 CAB 名称的路径替换、修改时间变化后重载、资源缺失/损坏、重试、崩溃与进程回收。WPF 检查覆盖启动中切卡、快速切卡、离线路径恢复、重试和加载/启动中关闭。离线场景使用不可用路径模拟，没有断开用户实际 NAS。

在项目根目录使用 PowerShell，替换路径并为每轮测试指定尚不存在的输出目录：

```powershell
.\.tools\dotnet\dotnet.exe build tests\HS2Manager.UiChecks -c Release --disable-build-servers -p:UseSharedCompilation=false
.\.tools\dotnet\dotnet.exe tests\HS2Manager.UiChecks\bin\Release\net10.0-windows10.0.19041.0\win-x64\HS2Manager.UiChecks.dll --unity-performance --samples artifacts\preview-performance\before\samples.json --output artifacts\preview-performance\new-run --player artifacts\unity-preview\loading-performance-release\HS2Manager.UnityPreview.exe

# 为四类人物分别执行；PrimeDefinition 使用另一人物的定义，内部执行 A→B→A。
.\tests\Invoke-UnityCharacter.ps1 -PlayerDirectory artifacts\unity-preview\loading-performance-release -Definition artifacts\preview-performance\release\stock-female.json -PrimeDefinition artifacts\preview-performance\release\stock-male.json -OutputPath artifacts\preview-performance\new-visual\stock-female

# Python 环境需要 numpy、Pillow、scikit-image；本机位于 .tools/image-compare。
.\.tools\image-compare\Scripts\python.exe tests\Compare-PreviewPerformance.py --before artifacts\preview-performance\before --after artifacts\preview-performance\release --reference artifacts\preview-performance\before-visual --actual artifacts\preview-performance\release-visual --output artifacts\preview-performance\release-comparison.json

.\build.ps1 -UnityPlayerDirectory artifacts\unity-preview\loading-performance-release -PreviewTest
.\tests\Test-SourceLaunch.ps1
.\tests\Test-PortableRelease.ps1 -Package '<artifacts/release 下的发布目录>' -Definition artifacts\preview-performance\release\stock-female.json
```

最终比较命令当前预期退出码为 1，唯一失败项为重开耗时下降未达到 30%；不应修改阈值掩盖这一结果。构建和运行检查使用本任务拥有的进程，正常结束及异常清理均回收进程树，不停止用户原有进程。
