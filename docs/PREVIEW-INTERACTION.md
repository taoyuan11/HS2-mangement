# 预览交互优化

> 本文性能数字记录迁移前的 WPF 宿主，不代表 WinUI 3 的测量结果。当前界面和回归检查已迁移到 WinUI 3，见 [WINUI3-MIGRATION.md](WINUI3-MIGRATION.md)。

2026-11-08，针对旋转、平移和缩放时的拖影及低帧率，修改 Unity 内核、共享帧传输及 WPF 视口。

## 行为

- 镜头变化时清除时间抗锯齿、反射等效果的历史帧，交互期间使用 SMAA、旁路 CTAA，并暂时关闭运动模糊。停止操作 150 ms 后，用新历史恢复原环境效果。模型、贴图、渲染分辨率和静止时的环境设置不降级；交互时发丝等细边缘可能比时间抗锯齿更明显。
- 内核目标帧率从 30 提高至 60。使用 Unity 2018.4 的异步 GPU 读取，最多三帧在途，赶不上时跳过捕获，只发送最新完成帧。不支持或读取失败时自动切换同步路径。
- 同步路径不再将刚读出的画面用 `Apply` 上传回 GPU；两条路径共用无整帧临时数组的颜色转换。
- WPF 跟随 `CompositionTarget.Rendering` 更新，替代 33 ms 定时器；合并尺寸变化，并复用帧缓冲区。隐藏、卸载或关闭时解除刷新订阅。
- 异步帧带有人物加载编号；旧人物、取消及失败后的帧不发布。调整尺寸或退出前完成并丢弃在途读取，再释放渲染纹理。

异步读取避免了每帧同步等待 GPU，但仍有数帧传输延迟；这与历史图像叠加形成的拖影是两类问题。API 行为见 [Unity 2018.4 AsyncGPUReadback 文档](https://docs.unity.cn/2018.4/Documentation/ScriptReference/Rendering.AsyncGPUReadback.html)。

## 验证方法

本机 AMD Radeon RX 9070 GRE，Direct3D 11。基线是修改前 `cca27ee` 的管理器代码及 `release-b237b9e16a2846718fa75c045734322f` 内核；最终内核为 `artifacts/unity-preview/interaction-optimized`。使用同一人物定义 `artifacts/preview-performance/release/stock-female.json`，实际视口帧为 1266 × 706。

测试分别执行静止、旋转、平移和缩放，每段 4 秒。通过 WPF 合成事件统计视口取得的新帧，并同时记录内核已发布帧的序号差；窗口位于屏幕外，所以这不是显示器扫描输出的测量。性能值是同机有限样本，不保证所有人物或硬件稳定 60 FPS。

原始数据、镜头跳转连续帧和 Unity 日志保存在 `artifacts/interaction-performance`。`before-animated` 和 `optimized-animated` 是开启人物动画的最终比较；暂停人物的捕获用于观察历史帧残留及停止操作后效果恢复。

| 场景（人物动画开启） | 原视口 FPS | 新视口 FPS | 新内核发布 FPS | 原 → 新视口帧间隔 P95 |
| --- | ---: | ---: | ---: | ---: |
| 镜头静止 | 21.5 | 50.5 | 58.5 | 49.7 → 32.2 ms |
| 旋转 | 21.4 | 52.2 | 60.0 | 48.9 → 32.8 ms |
| 平移 | 21.3 | 51.4 | 59.2 | 49.8 → 32.8 ms |
| 缩放 | 21.3 | 52.0 | 60.1 | 49.3 → 32.3 ms |

交互帧率约为原来的 2.4 倍。内核的 60 FPS 目标不等于 WPF 每秒显示 60 个不同帧；合成调度和帧传输仍会跳过部分帧。数据中略高于 60 的值来自 4 秒窗口的边界采样。

## 复现

在项目根目录运行，输出目录必须尚不存在：

```powershell
.\.tools\dotnet\dotnet.exe build tests\HS2Manager.UiChecks -c Release --disable-build-servers -p:UseSharedCompilation=false
.\.tools\dotnet\dotnet.exe tests\HS2Manager.UiChecks\bin\Release\net10.0-windows10.0.19041.0\win-x64\HS2Manager.UiChecks.dll --unity-interaction --animate --player artifacts\unity-preview\interaction-optimized\HS2Manager.UnityPreview.exe --definition artifacts\preview-performance\release\stock-female.json --output artifacts\interaction-performance\new-run

.\.tools\dotnet\dotnet.exe build tests\HS2Manager.Checks -c InteractionChecks --disable-build-servers -p:UseSharedCompilation=false -p:Optimize=true
.\.tools\dotnet\dotnet.exe tests\HS2Manager.Checks\bin\InteractionChecks\net10.0-windows10.0.19041.0\HS2Manager.Checks.dll --unity-session --player artifacts\unity-preview\interaction-optimized\HS2Manager.UnityPreview.exe --definition artifacts\preview-performance\release\stock-female.json
```

诊断时可仅为测试进程设置 `HS2_PREVIEW_SYNC_READBACK=1`，强制验证同步兼容路径，测试结束恢复原环境变量。默认不设置。

通信回归包含帧缓冲区复用与快照隔离、在途读取期间反复调整尺寸（含奇数宽度）、不透明像素、截图与传输帧的颜色及方向、取消、切卡、资源失败、重试和进程回收。UI 回归覆盖隐藏/恢复、加载与启动中关闭、截图及相机操作。

最终版本通过 .NET Release 编译、Unity 2018.4 API 编译及 Player 构建、异步和强制同步两轮通信回归、完整 Unity 预览 UI 回归。通信证据分别位于 `artifacts/checks/unity-session/582b94e81ce143d1a370fc0e16bc40c0` 和 `aa490b746f3347e69d729527233de762`；UI 证据位于 `artifacts/unity-ui-checks/9fbe2ec881a64d00a6a47baba9d16e03`。这些检查均回收其启动的内核进程。

构建脚本已将源码启动使用的 `current-player.txt` 指向新内核；重新运行根目录 `start.cmd` 即可使用。已解压的旧便携包需要更新管理器和 Unity 内核才能包含这些修改。
