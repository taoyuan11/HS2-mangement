# 独立 Unity 预览

目标为本机整合包捏人界面的视觉一致。只有游戏画面对照通过后才可认定达到目标；加载成功和 Shader 球体测试不代表人物还原成功。

## 实现

- .NET 10 管理器负责人物卡、MOD、原始组件和场景数据读取。游戏程序集仅作为 PE/元数据读取，不装载进 CLR。
- 自有 Unity 2018.4.11f1 Windows x64 / Direct3D 11 Player 重建原始 Transform、Renderer 和蒙皮。不会实例化资源上的游戏 MonoBehaviour。
- 使用原始 Shader、材质、纹理合成流程；保留完整纹理尺寸和画质设置。支持 zipmod 内的独立 PNG 纹理、旧 DynamicBone 数据布局与原始动画。
- 读取捏人灯光、画质、环境 SH、DHH 天空与后处理、BetterAA 配置。独立实现眨眼、前向视线、动态链及身体物理。
- 命名管道传递命令，共享内存传递最终 BGRA。暂停、恢复、旋转、平移、缩放、环境恢复及截图均走新内核。
- 一次预览复用一个内核进程，切卡仅取消旧加载、释放旧角色并加载最新选择；卡片加载失败后重试仍复用健康内核。启动任务不随单张卡片取消，快速切换不会重复启动。
- 每个内核属于管理器创建的 Windows Job。关闭预览、退出管理器、断连和进程异常均回收所属进程；内核失效后的重试会创建新进程。

## 验收状态

2026-11-09 已完成当前构建的 84 张人物卡验收：female 当前层全部 79 张、原版男女各 1 张、编辑保存副本 3 张，共 336 张静态配对图和 2,016 张动态配对图。所有卡片均完成逐张局部视觉复核，复核记录绑定四视角、24 帧及复核图的 SHA-256。此前仅验证加载和首帧的记录见 [兼容性说明修复](UNITY-COMPATIBILITY.md)。

| 检查 | 结果 |
| --- | --- |
| 四视角静态数值与人物区域视觉复核 | 84/84 通过 |
| 24 个采样帧的动态数值与视觉复核 | 84/84 通过 |
| 完整视觉一致性验收 | 84/84 通过 |
| 原卡 SHA-256 不变 | 84/84 |
| 独立内核未加载游戏或插件程序集 | 84/84 |
| 编辑副本在游戏中的参数与资源选择 | 3/3 通过 |

本次结论限于固定的本机整合包、配置和人物卡，按规定阈值通过，仍允许阈值内的像素差异。完整图像报告在源码工作区 [artifacts/editor-baseline/compare-v20/index.html](../artifacts/editor-baseline/compare-v20/index.html)，同目录包含 `summary.json`、`summary.csv`、`not-passed.md`、构建/完整性记录、每卡四视角、差异图和动态采样图；报告独立交付，不放入便携应用包。游戏参考位于 `artifacts/editor-baseline/full-v10`，最终清单为 `artifacts/editor-baseline/catalog-v15/uniform-final.json`。详细数字与基线指纹见 [验收结果](UNITY-VALIDATION-RESULTS.md)。

所有 84 张独立截图、实时编辑和 UI 验证统一使用 `artifacts/unity-preview/editor-player-95`。内核 `HS2Manager.UnityPreview.dll` SHA-256 为 `2F9CE7C5D995D022CC5A62DBC351AA2BD66B5AA154347A1C77931E7C8C5CBAB4`，发布包携带同一 Player。修复包含部件作用域骨骼绑定、额外身体资源、材质实例隔离及覆盖贴图、待机与 IK 更新顺序、眨眼、动态链初始化/碰撞/重启和衣物动画时钟。

人物区域使用参考/独立图像与各自空背景图的差异并集。面部区域由参考头部几何在同一镜头下的投影包围范围确定。要求 SSIM ≥ 0.99、平均通道误差 ≤ 2/255，并需检查可见局部错误。动态采用 30Hz、固定随机种子和 360 帧序列；静态结果不能替代动态验收。

游戏参考工具位于 `tests/HS2Manager.GameReference`，仅用于显式开发测试。每张卡使用新游戏进程，避免插件状态跨卡残留。临时采集插件、自动保存配置和捏人设置在脚本结束后清理或恢复，并核对原卡 SHA-256。Studio 始终不用。该工具及游戏依赖不进入生产包。

## 验证边界

- 验收使用捏人待机、前向视线和本机已保存的插件效果；不扩展为任意姿势、玩法状态或其他视线模式的验证。
- 素材目录与编辑器覆盖原版功能；插件专属编辑面板、性别转换和扩展配饰槽专属编辑不在范围内。未知插件数据保留，未经验证的新插件组合仍须重新对照。
- 两端固定随机种子、动画材质时间、抗锯齿相位、物理重置和眨眼时钟；保持本机启用的渲染效果及原数值门槛。采集时的确定性设置不改变日常实时播放。
- 旧引擎不安全的可选纹理槽元数据枚举仍不执行；最终像素、材质/Shader/关键词、合成贴图、骨架及动态记录保留。

更换卡片、MOD、插件或画质配置后，需按新基线重新验证。缺失、失败或未复核的记录始终不计通过。

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
