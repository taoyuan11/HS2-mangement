# WinUI 3 迁移

> 本文保留历史验证记录，其中根目录 `build*.ps1` 命令及参数为当时入口。当前构建使用 `scripts/build/Build-Cpp.ps1`，打包使用 `scripts/package/Publish-Cpp.ps1`；现行用法见 [开发说明](DEVELOPMENT.md)。

> 历史记录：本文的架构、命令、版本、性能和通过数量对应迁移前的 C# 实现及其当时的预览后端。旧源码和专用脚本已移除，历史命令需检出相应 Git 版本后运行。本次 C++ 重写的实际检查见 [C++ 验证结果](CPP-VALIDATION.md)，当前构建与架构见 [C++ 迁移说明](CPP-MIGRATION.md)；本文数字不自动计入新版本验收。

2026-11-08，桌面程序由 WPF 迁移到 WinUI 3，版本 `1.2.0-preview.2`。

## 架构与功能

- `HS2Manager.App` 使用 .NET 10、Windows App SDK `1.8.260921001`，目标 Windows 10 2004 / Windows 11 x64。主窗口为 `Microsoft.UI.Xaml.Window`，列表使用虚拟化 `GridView` / `ItemsWrapGrid`，文件夹使用 WinUI `TreeView`。
- 设置、输入和确认使用 `ContentDialog`，菜单使用 `MenuFlyout`，文件选择器通过窗口 HWND 初始化。搜索、筛选、批量选择、标签、收藏、备注、虚拟分类、导入/导出、隔离/恢复和快捷键继续使用原有业务逻辑。
- Unity 预览仍为原有独立进程。共享 BGRA 帧通过 WinUI `WriteableBitmap` 显示，跟随合成事件刷新并按实际 DPI 请求分辨率；会话切换、隐藏和卸载时停止订阅。相机数学改用 `System.Numerics`，截图使用 Windows 图像编码器。
- Core 的内嵌贴图解码和环境 PNG 编码改用 `Windows.Graphics.Imaging`，生产程序集不再引用 `System.Windows` 或 `Microsoft.WindowsDesktop.App.WPF`。
- 旧软件渲染器移至 `tests/HS2Manager.LegacyRendering`，保留裁切、透明度、纹理和光照回归。核心测试保留独立 WPF 编码器生成的测试图片，用于交叉验证新解码器。两者均不会进入发布包。
- 标题栏关闭先取消后台操作、关闭所属 Unity 进程、解除帧刷新，再释放窗口。浅色、深色和系统主题通过 WinUI `RequestedTheme` 与系统颜色通知更新。

## 启动与数据

直接运行根目录 `start.cmd`。无需 Visual Studio；首次构建需联网还原 NuGet。构建工具和 Windows App SDK 由固定版本的 NuGet 包提供。

本机磁盘上的默认数据仍在程序旁的 `Data`。源码输出现在位于 `bin/<配置>/net10.0-windows10.0.19041.0/win-x64`；若此处没有 `Data`，会查找同一配置下旧 `net10.0-windows/Data`（包括旧 RID 子目录），继续使用已有数据库与设置。已有新目录优先，显式 `--data` 优先级最高。便携安装不执行源码目录回退。

源码位于 NAS / 网络映射盘时，默认改用 `%LOCALAPPDATA%\HS2Manager\Source\<项目与配置标识>\Data`，避免 SQLite 数据库落在网络盘而导致启动被拒绝。首次迁移通过 SQLite 备份复制数据库（包含已提交的 WAL 数据），同时复制设置、封面、操作记录与日志，保留原目录；预览缓存重新生成。复制完成后一次性启用本机目录，失败可重试，已有本机目录不覆盖。显式 `--data` 仍须指向本机磁盘。

`start.cmd` 通过 `scripts/Start-Source.ps1` 启动。网络盘源码的编译产物也放入该本机标识目录下的 `Build`，避免 NAS 上已执行过的 EXE 无法覆盖。启动器保留原源码目录信息，因此仍能迁移旧数据、定位最近构建的 Unity Player；本机源码保留原来的构建路径。

发布采用未打包的自包含目录，同时携带 .NET 和 Windows App SDK 运行时。无需 MSIX、管理员权限或预先安装 Windows App Runtime。发布流程依据 [Microsoft 的未打包 WinUI 3 部署说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)。

## 验证

```powershell
.\.tools\dotnet\dotnet.exe build HS2Manager.slnx --disable-build-servers -p:UseSharedCompilation=false
.\.tools\dotnet\dotnet.exe run --project tests/HS2Manager.Checks --disable-build-servers -p:UseSharedCompilation=false -- --core
.\.tools\dotnet\dotnet.exe run --project tests/HS2Manager.UiChecks --disable-build-servers -p:UseSharedCompilation=false
.\.tools\dotnet\dotnet.exe run --project tests/HS2Manager.UiChecks --disable-build-servers -p:UseSharedCompilation=false -- --unity-preview
.\.tools\dotnet\dotnet.exe run --project tests/HS2Manager.LegacyRendering --disable-build-servers -p:UseSharedCompilation=false
# 使用已构建的 Unity Player，运行检查并生成便携包：
.\build.ps1 -UnityPlayerDirectory 'artifacts\unity-preview\material-quality'
# 单独检查发布产物，自动关闭所启动的进程：
.\tests\Invoke-WinUiSmoke.ps1 -Executable '<发布目录>\HS2Manager.exe'
```

本机验证包括：

- 解决方案构建零警告、零错误；31 项核心检查及纹理、扫描设置、缓存回归通过。
- 真实 WinUI 窗口的浅色/深色/系统主题 × 1440×900、1280×760、1100×700 布局通过，80 张测试卡不会全部实例化；未检测到 XAML 绑定错误。
- WinUI 缩略图解码、文件夹树、搜索和选择绑定、刷新后的选择恢复、标签备注持久化、输入对话框、设置验证与保存、旧数据目录发现和显式覆盖通过。
- 资源库/预览 × 空闲/后台操作中四种原生标题栏关闭场景通过，重复关闭不会跳过清理。
- 使用本机真实男女卡和独立 Unity Player，验证镜头、面部取景、截图、灯光/背景、播放状态、启动中换卡、连续和快速换卡、离线重试、崩溃重启、加载/启动中关闭及进程回收。
- 历史软件渲染器的独立回归通过。
- 自包含发布产物直接启动、独立测试数据库创建、原生窗口关闭通过；发布目标显式携带生成的 XBF 和 PRI，避免仅构建目录可运行而发布目录缺少界面资源。

布局截图位于 `artifacts/winui-checks/<本次检查 ID>`，真实预览截图位于 `artifacts/unity-ui-checks/<本次检查 ID>`。窗口尺寸测试不等同于实机切换所有 DPI；本机 Windows 11 验证不能替代 Windows 10 实机兼容性测试。既有预览还原度限制仍然适用，历史 WPF 性能数字不作为 WinUI 3 的性能承诺。
