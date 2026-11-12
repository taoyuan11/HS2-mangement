#pragma once
#include <windows.h>
#include <filesystem>

namespace hs2::ui
{
    // 必须先于 COM、XAML 和显卡驱动初始化，仅影响当前进程。
    void InitializeProcessLifetime();
    void SetProcessErrorDirectory(std::filesystem::path const &directory);
    // 只在用户已决定退出或后台任务正在取消时计时；取消关闭时必须撤销。
    void BeginProcessShutdown(DWORD timeoutMilliseconds = 30000) noexcept;
    void CancelProcessShutdown() noexcept;
    // 业务对象和线程清理完成后调用，避免再执行第三方 DLL 的进程卸载回调。
    [[noreturn]] void FinishProcess(DWORD exitCode) noexcept;
} // 命名空间 hs2::ui
