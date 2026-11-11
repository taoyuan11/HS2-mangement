#pragma once
#include "../HS2Manager.NativeCore/Core.h"
#include <chrono>

namespace hs2 {
// The process is created inside a kill-on-close job before its first instruction.
// Descendants are reaped on success, timeout, cancellation, and exception.
int RunOwnedTool(const fs::path& executable,const std::vector<std::wstring>& arguments,
    const fs::path& directory,std::chrono::milliseconds timeout,const fs::path& log,
    std::stop_token cancellation={});
}
