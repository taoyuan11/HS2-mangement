#include "Infrastructure/ProcessLifetime.h"
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdio>
#include <stdexcept>
#include <system_error>
#include <werapi.h>

namespace hs2::ui
{
    namespace
    {
        PTP_TIMER shutdownTimer = nullptr;
        std::atomic<bool> shutdownArmed = false;
        std::array<wchar_t, 32768> errorPath{};

        void LogFailure(char const *reason, DWORD code, void const *address = nullptr) noexcept
        {
            if (!errorPath[0])
                return;
            auto file =
                CreateFileW(errorPath.data(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                            nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE)
                return;
            SYSTEMTIME utc{};
            GetSystemTime(&utc);
            char line[256];
            auto length = sprintf_s(line, "%04u-%02u-%02uT%02u:%02u:%02uZ %s PID=%lu code=0x%08lx address=%p\n",
                                    utc.wYear, utc.wMonth, utc.wDay, utc.wHour, utc.wMinute, utc.wSecond, reason,
                                    GetCurrentProcessId(), code, address);
            DWORD written{};
            if (length > 0)
                WriteFile(file, line, static_cast<DWORD>(length), &written, nullptr);
            CloseHandle(file);
        }

        void CALLBACK ShutdownExpired(PTP_CALLBACK_INSTANCE, void *, PTP_TIMER) noexcept
        {
            if (shutdownArmed.exchange(false))
            {
                LogFailure("Shutdown timeout", WAIT_TIMEOUT);
                FinishProcess(WAIT_TIMEOUT);
            }
        }

        LONG WINAPI FatalException(EXCEPTION_POINTERS *exception) noexcept
        {
            auto record = exception ? exception->ExceptionRecord : nullptr;
            auto code = record ? record->ExceptionCode : static_cast<DWORD>(E_FAIL);
            LogFailure("Unhandled native exception", code, record ? record->ExceptionAddress : nullptr);
            // 致命异常后不能再运行可能已损坏的 C++/COM 析构，也不复制进程用于崩溃报告。
            FinishProcess(code);
        }
    } // 命名空间

    void InitializeProcessLifetime()
    {
        DWORD flags{};
        auto result = WerGetFlags(GetCurrentProcess(), &flags);
        // 尚未创建 WER 配置的全新进程返回 ERROR_NOT_FOUND，此时从默认值开始。
        if (SUCCEEDED(result) || result == HRESULT_FROM_WIN32(ERROR_NOT_FOUND))
            result = WerSetFlags(flags | WER_FAULT_REPORTING_DISABLE_SNAPSHOT_CRASH |
                                 WER_FAULT_REPORTING_DISABLE_SNAPSHOT_HANG | WER_FAULT_REPORTING_NO_UI);
        if (FAILED(result))
            throw std::system_error(static_cast<int>(result), std::system_category(),
                                    "Configure process error reporting");
        // 保留给 fast-fail 等绕过异常过滤器的错误报告，但不让 WER 创建持有 DLL/数据库映射的克隆。
        SetUnhandledExceptionFilter(FatalException);
        shutdownTimer = CreateThreadpoolTimer(ShutdownExpired, nullptr, nullptr);
        if (!shutdownTimer)
            throw std::system_error(static_cast<int>(GetLastError()), std::system_category(),
                                    "Create shutdown deadline");
        // 定时器由当前进程持有，正常完成时由 FinishProcess 一并回收，不启动辅助进程。
    }

    void SetProcessErrorDirectory(std::filesystem::path const &directory)
    {
        auto path = (directory / L"native-error.log").wstring();
        if (path.size() >= errorPath.size())
            throw std::length_error("Process error log path is too long.");
        wcscpy_s(errorPath.data(), errorPath.size(), path.c_str());
    }

    void BeginProcessShutdown(DWORD timeoutMilliseconds) noexcept
    {
        if (!shutdownTimer || shutdownArmed.exchange(true))
            return;
        LARGE_INTEGER relative{};
        relative.QuadPart = -static_cast<LONGLONG>(std::max<DWORD>(1, timeoutMilliseconds)) * 10000;
        FILETIME due{relative.LowPart, static_cast<DWORD>(relative.HighPart)};
        SetThreadpoolTimer(shutdownTimer, &due, 0, 0);
    }

    void CancelProcessShutdown() noexcept
    {
        shutdownArmed = false;
        if (shutdownTimer)
        {
            SetThreadpoolTimer(shutdownTimer, nullptr, 0, 0);
            WaitForThreadpoolTimerCallbacks(shutdownTimer, TRUE);
        }
    }

    [[noreturn]] void FinishProcess(DWORD exitCode) noexcept
    {
        // 所有资料写入和自有线程的等待都必须在调用之前完成。
        // return/ExitProcess 会先终止驱动工作线程、再进入 DLL_PROCESS_DETACH，可能死锁或崩溃。
        TerminateProcess(GetCurrentProcess(), exitCode);
        ExitProcess(exitCode);
    }
} // 命名空间 hs2::ui
