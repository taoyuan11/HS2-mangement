#pragma once
#include "Common/Core.h"
#include <chrono>

namespace hs2
{
    // 进程执行首条指令前即加入关闭时终止的作业；
    // 成功、超时、取消或异常时均回收全部子进程。
    int RunOwnedTool(const fs::path &executable, const std::vector<std::wstring> &arguments, const fs::path &directory,
                     std::chrono::milliseconds timeout, const fs::path &log, std::stop_token cancellation = {});
}
