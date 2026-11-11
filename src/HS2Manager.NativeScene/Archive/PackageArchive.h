#pragma once
#include <cstdint>
#include <filesystem>
#include <functional>
#include <optional>
#include <stdexcept>
#include <stop_token>
#include <string>

namespace hs2
{
    class ArchivePasswordRequired : public std::runtime_error
    {
    public:
        explicit ArchivePasswordRequired(bool retry)
            : std::runtime_error(retry ? "密码错误或加密内容损坏，请重新输入密码。" : "请输入压缩包密码。"),
              Retry(retry)
        {
        }

        bool Retry;
    };

    struct PackageLimits
    {
        std::uint64_t TotalBytes = 32ULL * 1024 * 1024 * 1024;
        std::uint64_t FileBytes = 8ULL * 1024 * 1024 * 1024;
        std::uint32_t Entries = 100000;
    };

    // 所有输出均由调用方创建流，归档不能创建链接、备用数据流或自行选择文件系统路径。
    std::filesystem::path ValidatePackagePath(std::wstring name);
    void ExtractPackage(const std::filesystem::path &source, const std::filesystem::path &destination,
                        const std::filesystem::path &decoder, const std::optional<std::wstring> &password = {},
                        std::function<void(std::uint64_t, std::uint64_t)> progress = {},
                        std::stop_token cancellation = {}, PackageLimits limits = {});
}
