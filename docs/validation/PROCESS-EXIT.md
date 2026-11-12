# 进程退出与目录占用检查（2026-11-11）

本轮检查发现桌面 `HS2Manager` 已被部分删除，只剩 EXE、WinUI／VC++ DLL 和原生依赖。系统仍列出 PID 7884（父 PID 7068），线程数 0、句柄数 0、无窗口，查询进程句柄返回 Win32 5。20:52 的应用错误事件记录原进程 PID 7068 在 `amdxx64.dll` 发生 `0xc0000005`，随后在 COM／XAML 报告 `0x80010105`、`0xc000027b`。残留进程的父子关系和无线程状态符合 WER 快照特征；事件日志不能证明驱动内部访问冲突的具体原因。

此次修复包括：

- 在 COM／XAML 初始化前设置本进程 WER 的 `DISABLE_SNAPSHOT_CRASH` 与 `DISABLE_SNAPSHOT_HANG`，覆盖快速失败等绕过异常过滤器的崩溃。
- 原生未处理异常及 XAML 致命异常先记录诊断，再结束当前进程，避免继续运行受损状态的析构代码。
- 关闭时先取消任务，异步停止预览并解除交换链，等待自有线程，主动关闭 SQLite 和系统主题通知；补上 `Window.Closed` 的直接销毁路径。
- 清理完成后结束当前进程，避免返回入口触发第三方 DLL 的进程卸载回调。Microsoft 文档说明，`ExitProcess` 会先停止其他线程，之后进入 DLL 卸载；卸载代码若等待被停止线程持有的锁，就可能死锁。
- 确认退出／后台任务取消最长等待 30 秒。重复关闭不延长期限；保存／放弃／取消对话框等待用户期间暂停计时，取消或保存失败后继续使用时撤销期限。

参考：[Microsoft 的进程退出说明](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-exitprocess)、[WER 设置 API](https://learn.microsoft.com/en-us/windows/win32/api/werapi/nf-werapi-wersetflags)、[微软 SDK 的崩溃快照标志](https://microsoft.github.io/windows-docs-rs/doc/windows/Win32/System/ErrorReporting/constant.WER_FAULT_REPORTING_DISABLE_SNAPSHOT_CRASH.html)。

Release x64 构建及原生依赖检查通过，CTest 15 / 15 通过。新增 `process_exit_lifetime` 检查卸载阻塞、退出超时、撤销超时、原生异常、快速失败、强制结束父进程后回收导出子进程。检查在每次结束后枚举进程，最后完整删除 EXE／DLL 所在目录。

扩展 WinUI 检查完成七次独立进程测试：空资料库关闭、强制结束、强制结束后重启、首次扫描中关闭、首次扫描完成、已有 80 张人物卡资料库两次重启。正常关闭均为退出码 0，强制结束为 -1；退出后无本次进程或其克隆，最后成功删除完整 WinUI／VC++ 运行库副本。SQLite 人物记录验证通过。

真实 GPU 界面复测使用“王者荣耀 海月预言先知.zip”和独立资料库，确认 Direct3D 11 画面后分别点击右上角 X、按 Alt+F4。两次均退出码 0、无测试进程或克隆、临时目录清空、无新增 Windows 应用崩溃事件。独立运行库测试副本的后续删除被命令自动审核拒绝，副本保留；全部测试程序已停止。

旧版本已经留下的 PID 7884 不属于本次启动的进程，本轮未终止它。新版进程策略不能追溯释放系统已持有的旧快照；旧占用需由 Windows 释放，必要时重启后清理。

最终版本已发布至 `C:\Users\sky\Desktop\HS2Manager`，EXE 的 SHA-256 为 `5E5D546306FE829EC8D16FBA88805001E76EDB2CB28413F1977AC8D3BB2F6D88`，与最终构建一致。原有占用文件隔离在桌面 `HS2Manager-backup-56fb863ec22d4ffbb875a62f443243f7`，已登记下次登录清理。最终版本再次通过七项 WinUI 检查、运行库副本完整删除和 15 / 15 CTest；本次启动的构建、测试及 GUI 进程均已结束，仅有先前的 PID 7884 保留。
