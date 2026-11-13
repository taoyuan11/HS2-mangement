# 发布与程序更新

## 发布正式版

版本号以根目录 `VERSION` 为准，格式为 `主.次.修订`，每段不超过 65535。CMake 配置时生成程序资源和应用清单；界面、EXE 属性与 ZIP 文件名保持一致。不要手动修改生成目录中的版本文件。

将版本修改和代码一起提交到 `main`，确认构建通过后推送对应标签：

```powershell
git tag v1.5.0
git push origin v1.5.0
```

GitHub Actions 在 Windows 2022 托管 runner 中还原固定版本 SDK，执行完整 Release 构建、CTest、包依赖审计和发布脚本测试。PR、普通 main 提交及手动工作流只产生构建附件；只有版本标签触发正式发布。无需游戏目录或个人电脑在线；真实游戏画面与 GUI 冒烟仍在本机验证。

FBX 2020.3.7 安装包从 Autodesk 官方地址下载并检查 SHA-256，使用 runner 自带的 7-Zip 提取 SDK 文件，沿用项目附带的许可。PhysX 3.4.2 固定到源码提交 `5e42a5f112351a223c19c17bb331e6c55037b8eb`，检查 Git 对象完整性。7-Zip 源码与 NuGet 依赖继续使用现有校验值。缓存只保存 SDK 输入和 NuGet 包，不缓存应用构建结果。

发布使用仓库自动提供的 `GITHUB_TOKEN`，仅发布任务有 `contents: write` 权限。先创建草稿并上传 ZIP、`.zip.sha256` 和 `update.json`，全部校验成功后再公开。发布任务排队串行执行，仅较新版本设为 Latest。失败不会公开不完整附件；相同发布任务重试复用已校验附件。已公开版本禁止改写：如果重新构建产生不同字节，必须提升版本发布。

`update.json` 的协议为：`schemaVersion: 1`、`version`、`platform: win-x64`、`archive`、`bytes`、`sha256`。它与 ZIP 校验文件位于 `artifacts/release`。包内 `package-info.json` 标识版本与平台，`package-files.json` 列出文件路径、大小和 SHA-256。发布包只包含程序与空白数据说明，不包含本机数据库或游戏资源。

## 在程序中更新

打开“设置与连接 → 程序更新”。默认启动后检查最新正式版，每 24 小时最多一次；开关点击设置的“保存”后生效。手动检查不受间隔限制。检查使用公开 GitHub API，无需 Token；访问失败、限流及附件缺失会显示具体状态，程序仍可使用。

发现新版本只显示提示。点击“下载更新”才下载完整 ZIP，校验和解压通过后显示“重启并安装”。检查与下载均可取消。点击安装视为确认关闭程序；如有未保存编辑，仍按原有保存／放弃／取消流程处理。正在执行其他任务时须先完成或取消该任务。关闭同一安装的其他程序实例及 MCP 连接后再安装。

安装助手 `HS2Updater.exe` 使用静态运行库，运行副本保存在 `%LOCALAPPDATA%/HS2Manager/Updates/<安装标识>`。主程序退出后助手独占安装锁，复检清单、文件冲突、空间和权限，将新文件暂存到安装目录下 `.hs2-update-<标识>` 再替换。NAS 程序同样先下载到本机，再在 NAS 安装目录准备替换。

只替换清单管理的程序文件，并删除清单中已废弃的程序文件。`Data`、`Data.*` 恢复备份、自定义数据目录、`agent.json` 和其他非包管理文件保留。若包管理的文件被手动修改或新增路径与用户文件冲突，会停止安装并报告；不会擅自覆盖这些修改。源码构建目录支持检查版本，但没有正式包清单，不能自动安装。

旧版没有更新入口，第一次需手动下载带更新功能的完整便携包。保留原 Data 和其他个人文件，关闭程序后替换程序文件。

## 失败与恢复

下载或校验失败不会修改现有安装。文件替换前写入 `transaction.json`，替换失败自动恢复旧文件；文件被外部修改或备份不可读时，保留现场并说明原因。运行日志和错误记录位于该安装的本机更新缓存，暂存及旧文件备份位于安装盘。

断电或强制结束助手后，关闭该安装的所有实例，按错误信息中的实际路径运行缓存中的独立助手：

```powershell
& '<更新缓存目录>\HS2Updater.exe' --recover '<更新缓存目录>\transaction.json'
```

恢复操作可重复执行，只按日志及哈希恢复程序文件，不恢复或降级数据库。成功安装会按原数据位置启动新程序，窗口初始化成功后清理旧程序备份。新版启动失败或 60 秒内未确认启动时保留备份；确认新版正常且已关闭后，可运行 `--complete` 替代 `--recover` 清理备份。升级后的数据库若有格式变化，恢复旧程序前须确认旧程序兼容性。

可取消的检查／下载工作线程随主程序退出回收；安装助手完成、失败或等待原程序退出超过 45 秒后结束，不安装服务或计划任务。成功安装后删除本次下载目录；缓存中的助手、事务结果和诊断记录保留供恢复与排查。

## 验证入口

```powershell
python -m unittest discover -s tests/release -p 'test_*.py'
.\build-cpp.ps1 -Jobs 4
.\tests\package\Test-ParallelPackage.ps1
# 真实更新助手、进程交接、启动确认和数据保留，均在隔离安装副本中执行。
.\tests\package\Test-UpdaterProcess.ps1 -Package '.\artifacts\release\HS2Manager-1.5.0-cpp-win-x64' -Checker '<构建目录>\modules\src\HS2Manager.NativeUpdate\Release\hs2_update_checks.exe'
# GUI 冒烟使用隔离程序与数据副本，并在结束时回收所有测试进程。
.\tests\ui\Invoke-CppWinUiSmoke.ps1 -Executable '<构建目录>\bin\Release\HS2Manager.exe' -FixtureTool '<构建目录>\modules\Release\hs2_ui_fixtures.exe'
```

CTest 的 `application_update` 覆盖版本、发布信息、包完整性、路径、安装锁、冲突、文件占用、故障回滚及数据保留，并在多个替换步骤强制结束测试子进程后验证中断恢复。发布脚本使用模拟 GitHub API 验证草稿、上传失败、重复运行与 Latest 策略，不会创建测试 Release。
