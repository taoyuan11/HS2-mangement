#pragma once
#include <windows.h>
#include <array>
#include <compare>
#include <filesystem>
#include <functional>
#include <optional>
#include <stop_token>
#include <string>
#include <vector>

namespace hs2::update
{
    namespace fs = std::filesystem;
    inline constexpr wchar_t ReleasesPage[] = L"https://github.com/taoyuan11/HS2-mangement/releases";

    struct Handle
    {
        HANDLE Value = INVALID_HANDLE_VALUE;

        explicit Handle(HANDLE value = INVALID_HANDLE_VALUE) : Value(value)
        {
        }

        ~Handle();
        Handle(Handle const &) = delete;
        Handle &operator=(Handle const &) = delete;
        Handle(Handle &&other) noexcept;
        Handle &operator=(Handle &&other) noexcept;
    };

    struct Version
    {
        std::array<unsigned, 3> Parts{};
        static Version Parse(std::string text);
        auto operator<=>(Version const &) const = default;
    };

    struct Release
    {
        std::string Version, Notes, PageUrl, ArchiveName, ArchiveUrl, ManifestUrl, SHA256;
        uint64_t Bytes = 0;
    };

    using Progress = std::function<void(uint64_t, uint64_t)>;

    struct HttpResponse
    {
        unsigned Status = 0;
        std::string Body;
    };

    using Fetch = std::function<HttpResponse(std::string const &, std::stop_token)>;
    std::string Utf8(fs::path const &path);
    fs::path Path(std::string const &text);
    std::string ReadText(fs::path const &path);
    void WriteText(fs::path const &path, std::string const &text);
    std::string HashFile(fs::path const &path, std::stop_token stop = {});
    std::string Id();
    void CheckStop(std::stop_token stop);
    fs::path SafeRelative(std::string name);
    bool Protected(fs::path const &path);
    void CheckOrdinary(fs::path const &path);
    void CheckTreePath(fs::path const &root, fs::path const &relative);
    fs::path CacheRoot(fs::path const &installation);
    Handle InstallationLease(fs::path const &installation, bool exclusive = false);
    bool IsPortable(fs::path const &installation);
    HttpResponse Get(std::string const &url, std::stop_token stop);
    std::optional<Release> Check(std::string const &current, std::stop_token stop = {}, Fetch fetch = Get);
    Release ParseRelease(std::string const &json);
    void ApplyManifest(Release &release, std::string const &json);
    void Download(Release const &release, fs::path const &archive, std::stop_token stop, Progress progress);
    void Extract(Release const &release, fs::path const &archive, fs::path const &destination,
                 std::stop_token stop = {});
    void ValidatePackage(fs::path const &package, std::string const &version, std::stop_token stop = {});
    void Preflight(fs::path const &installation, fs::path const &package, fs::path const &data);
    // 回调只用于故障注入测试；生产调用不传回调。事务在文件移动前先持久化恢复记录。
    fs::path Install(fs::path const &installation, fs::path const &package, fs::path const &data,
                     std::function<void(size_t)> afterMove = {});
    void Recover(fs::path const &journal);
    void Complete(fs::path const &journal);
    fs::path PrepareLaunch(fs::path const &installation, fs::path const &package, fs::path const &data);
    Handle LaunchUpdater(fs::path const &request);
    Handle StartApplication(fs::path const &installation, fs::path const &data, std::wstring const &readyEvent = L"");
}
