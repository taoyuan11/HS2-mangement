# NAS 迁移与验证

项目可以保留在映射盘或 UNC 共享目录中。源码入口为根目录 `start.cmd`；发布入口为完整解压后的 `HS2Manager.exe`。

## 数据与构建位置

| 内容 | NAS 项目运行时的位置 |
| --- | --- |
| 源码、游戏资源、原始人物卡 | 原有 NAS 目录 |
| 源码运行的数据库、封面、编辑备份与日志 | `%LOCALAPPDATA%\HS2Manager\Source\<标识>\Data` |
| NAS 发布包的数据库与资料 | `%LOCALAPPDATA%\HS2Manager\Portable\<标识>\Data` |
| `start.cmd` / `build.ps1` 的 .NET 中间文件 | 对应 Source 标识下的 `Build` |
| 发布暂存 | `%LOCALAPPDATA%\HS2Manager\Publish` |
| Unity 构建项目、Library、Temp | `%LOCALAPPDATA%\HS2Manager\UnityBuild` |
| 默认核心/UI 检查资料 | `%LOCALAPPDATA%\HS2Manager\Tests\artifacts` |
| 成功的 Unity Player、便携目录与 ZIP | 项目的 `artifacts` |

同一台电脑上，映射盘与其 UNC 路径复用同一资料库。已有旧版 Source 标识目录中的资料继续使用，不会被新资料覆盖。`--data` 可显式选择本机资料目录。实际使用的位置见“设置与连接 → 数据与维护”。

首次迁移用 SQLite 备份接口读取已提交的 WAL 内容，并复制设置、封面、操作记录、日志和 `CardBackups`。编辑备份校验后更新操作记录中的备份位置。复制全部完成后才启用本机目录，失败时清除本次临时副本；NAS 原件保留。预览缓存重新建立。

NAS 程序与本机资料库需要分别备份。更换电脑、改变项目的真实共享路径时，不会自动同步收藏、备注或已迁移的本机数据库；需要保留原本机 Data，必要时通过 `--data` 显式使用它。不要让多台机器共同写入 NAS 上的 SQLite 数据库。

## 2026-11-09 验证

在 `Z:\project\HS2-mangement`（映射到 `\\nas\nas`）完成：

- Z 盘和 UNC 路径的 `start.cmd` 启动、主窗口显示及正常关闭。
- 源码/便携包本地目录选择、映射路径别名、WAL 与编辑备份迁移、迁移失败重试、旧 Unity 绝对路径重定位。
- NAS 测试卡的扫描、覆盖保存、另存、迁移后恢复备份、重命名、本机与 UNC 之间移动、隔离/恢复、导出和永久删除。仅操作生成的独立测试卡。
- 核心检查与全部 WinUI 默认检查。
- Unity 2018.4.11f1 在本机镜像项目中完成构建，Player 复制回 NAS；实际人物加载、IPC、取消、资源替换、异常重试和进程回收通过。
- `build.ps1` 本机发布暂存、跨卷复制回 NAS、ZIP/SHA-256、解压后启动通过。
- NAS 便携包不传 `--data`，通过映射盘和 UNC 启动均使用同一本机数据库，原 NAS 设置保持不变。

复查启动：

```powershell
pwsh -NoProfile -File .\tests\Test-SourceLaunch.ps1
pwsh -NoProfile -File .\tests\Test-SourceLaunch.ps1 -Unc
pwsh -NoProfile -File .\tests\Test-NasPortable.ps1 -Package '<完整便携包目录>'
```

复查迁移及 NAS 文件操作（.NET SDK 路径可换为系统 `dotnet`）：

```powershell
$env:MSBUILDDISABLENODEREUSE = '1'
$build = Join-Path $env:LOCALAPPDATA 'HS2Manager\Tests\NasBuild'
.\.tools\dotnet\dotnet.exe run --project tests/HS2Manager.Checks -c NasValidation --artifacts-path $build --disable-build-servers -p:UseSharedCompilation=false -- --data-location
.\.tools\dotnet\dotnet.exe run --project tests/HS2Manager.Checks -c NasValidation --artifacts-path $build --disable-build-servers -p:UseSharedCompilation=false -- --nas-files --workspace (Get-Location).Path
```

测试进程均在完成后退出；失败路径也会回收测试启动的应用和 Unity 子进程。
