#include "Update.h"
#include "AppVersion.h"
#include "Archive/ZipArchive.h"
#include "third_party/json.hpp"
#include <bcrypt.h>
#include <shlobj.h>
#include <algorithm>
#include <charconv>
#include <fstream>
#include <iomanip>
#include <set>
#include <sstream>
#include <stdexcept>
#include <utility>

namespace hs2::update
{
    using Json = nlohmann::json;

    Handle::~Handle()
    {
        if (Value && Value != INVALID_HANDLE_VALUE)
            CloseHandle(Value);
    }

    Handle::Handle(Handle &&other) noexcept : Value(std::exchange(other.Value, INVALID_HANDLE_VALUE))
    {
    }

    Handle &Handle::operator=(Handle &&other) noexcept
    {
        if (this != &other)
        {
            if (Value && Value != INVALID_HANDLE_VALUE)
                CloseHandle(Value);
            Value = std::exchange(other.Value, INVALID_HANDLE_VALUE);
        }
        return *this;
    }

    namespace
    {
        std::string Lower(std::string value)
        {
            std::transform(value.begin(), value.end(), value.begin(),
                           [](unsigned char c)
                           {
                               return char(std::tolower(c));
                           });
            return value;
        }

        std::string Hex(std::span<unsigned char const> bytes)
        {
            std::ostringstream out;
            for (auto b : bytes)
                out << std::hex << std::setfill('0') << std::setw(2) << unsigned(b);
            return out.str();
        }

        std::string HashBytes(std::span<unsigned char const> bytes)
        {
            unsigned char digest[32]{};
            if (BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, const_cast<PUCHAR>(bytes.data()),
                           static_cast<ULONG>(bytes.size()), digest, sizeof(digest)) < 0)
                throw std::runtime_error("SHA-256 计算失败。");
            return Hex(digest);
        }

        std::string RequiredUrl(Json const &asset, std::string const &version, std::string const &name)
        {
            std::string expected =
                "https://github.com/taoyuan11/HS2-mangement/releases/download/v" + version + "/" + name;
            if (asset.at("browser_download_url").get<std::string>() != expected)
                throw std::runtime_error("更新附件地址与仓库或版本不符。");
            return expected;
        }
    }

    std::string Utf8(fs::path const &path)
    {
        auto text = path.generic_u8string();
        return {reinterpret_cast<char const *>(text.data()), text.size()};
    }

    fs::path Path(std::string const &text)
    {
        return fs::path(std::u8string(text.begin(), text.end()));
    }

    std::string Id()
    {
        unsigned char bytes[16]{};
        if (BCryptGenRandom(nullptr, bytes, sizeof(bytes), BCRYPT_USE_SYSTEM_PREFERRED_RNG) < 0)
            throw std::runtime_error("无法生成更新会话编号。");
        return Hex(bytes);
    }

    void CheckStop(std::stop_token stop)
    {
        if (stop.stop_requested())
            throw std::runtime_error("已取消更新操作。");
    }

    std::string ReadText(fs::path const &path)
    {
        if (fs::file_size(path) > 16 * 1024 * 1024)
            throw std::runtime_error("更新清单过大。");
        std::ifstream in(path, std::ios::binary);
        if (!in)
            throw std::runtime_error("无法读取更新清单。");
        return {std::istreambuf_iterator<char>(in), {}};
    }

    void WriteText(fs::path const &path, std::string const &text)
    {
        fs::create_directories(path.parent_path());
        auto temporary = path;
        temporary += L".tmp";
        Handle file(
            CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
        DWORD written{};
        if (file.Value == INVALID_HANDLE_VALUE ||
            !WriteFile(file.Value, text.data(), static_cast<DWORD>(text.size()), &written, nullptr) ||
            written != text.size() || !FlushFileBuffers(file.Value))
            throw std::runtime_error("无法保存更新恢复记录。");
        file = Handle();
        if (!MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
            throw std::runtime_error("无法提交更新恢复记录。");
    }

    std::string HashFile(fs::path const &path, std::stop_token stop)
    {
        BCRYPT_HASH_HANDLE hash{};
        if (BCryptCreateHash(BCRYPT_SHA256_ALG_HANDLE, &hash, nullptr, 0, nullptr, 0, 0) < 0)
            throw std::runtime_error("无法建立 SHA-256 校验。");

        struct Guard
        {
            BCRYPT_HASH_HANDLE H;

            ~Guard()
            {
                BCryptDestroyHash(H);
            }
        } guard{hash};

        std::ifstream in(path, std::ios::binary);
        if (!in)
            throw std::runtime_error("无法读取待校验文件：" + Utf8(path));
        std::array<char, 65536> buffer{};
        while (in)
        {
            CheckStop(stop);
            in.read(buffer.data(), buffer.size());
            if (in.gcount() &&
                BCryptHashData(hash, reinterpret_cast<PUCHAR>(buffer.data()), static_cast<ULONG>(in.gcount()), 0) < 0)
                throw std::runtime_error("文件校验失败。");
        }
        if (!in.eof())
            throw std::runtime_error("文件读取失败。");
        unsigned char digest[32]{};
        if (BCryptFinishHash(hash, digest, sizeof(digest), 0) < 0)
            throw std::runtime_error("文件校验失败。");
        return Hex(digest);
    }

    Version Version::Parse(std::string text)
    {
        Version value;
        for (size_t i = 0; i < 3; ++i)
        {
            auto end = text.find('.');
            auto part = text.substr(0, end);
            unsigned n{};
            auto parsed = std::from_chars(part.data(), part.data() + part.size(), n);
            if (part.empty() || (part.size() > 1 && part[0] == '0') || parsed.ec != std::errc() ||
                parsed.ptr != part.data() + part.size() || n > 65535 || (i < 2) != (end != std::string::npos))
                throw std::runtime_error("无效的正式版本号。");
            value.Parts[i] = n;
            if (end != std::string::npos)
                text.erase(0, end + 1);
        }
        return value;
    }

    fs::path SafeRelative(std::string name)
    {
        std::replace(name.begin(), name.end(), '\\', '/');
        if (name.empty() || name.front() == '/' || name.back() == '/' ||
            name.find_first_of(":*?\"<>|\0", 0, 8) != std::string::npos)
            throw std::runtime_error("更新包包含不安全路径。");
        size_t start = 0;
        while (start < name.size())
        {
            auto end = name.find('/', start);
            auto part = name.substr(start, end == std::string::npos ? end : end - start);
            auto base = Lower(part.substr(0, part.find('.')));
            if (part.empty() || part == "." || part == ".." || part.back() == '.' || part.back() == ' ' ||
                std::any_of(part.begin(), part.end(),
                            [](unsigned char c)
                            {
                                return c < 32;
                            }) ||
                base == "con" || base == "prn" || base == "aux" || base == "nul" ||
                (base.size() == 4 && (base.starts_with("com") || base.starts_with("lpt")) && base[3] >= '0' &&
                 base[3] <= '9'))
                throw std::runtime_error("更新包包含不安全路径。");
            if (end == std::string::npos)
                break;
            start = end + 1;
        }
        return Path(name);
    }

    bool Protected(fs::path const &path)
    {
        auto first = Lower(Utf8(*path.begin()));
        auto name = Lower(Utf8(path.filename()));
        return first == "data" || first.starts_with("data.") || first.starts_with(".hs2-update-") ||
               first == ".hs2-install.lock" || first == "logs" || first == "covers" || first == "cardbackups" ||
               first == "operations" || first == "nativecpp" || first == "userdata" || first == "mods" ||
               first == "abdata" || first == "bepinex" || name == "settings.json" || name.starts_with("library.db") ||
               name == "agent.json";
    }

    void CheckOrdinary(fs::path const &path)
    {
        auto attrs = GetFileAttributesW(path.c_str());
        if (attrs == INVALID_FILE_ATTRIBUTES)
        {
            auto error = GetLastError();
            if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND)
                throw std::runtime_error("无法访问更新路径：" + Utf8(path));
        }
        else if (attrs & FILE_ATTRIBUTE_REPARSE_POINT)
            throw std::runtime_error("更新路径不能包含链接：" + Utf8(path));
    }

    void CheckTreePath(fs::path const &root, fs::path const &relative)
    {
        auto current = root.root_path();
        for (auto const &part : root.relative_path())
        {
            current /= part;
            CheckOrdinary(current);
        }
        for (auto const &part : relative)
        {
            current /= part;
            CheckOrdinary(current);
        }
    }

    fs::path CacheRoot(fs::path const &installation)
    {
        Handle dir(CreateFileW(installation.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                               OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr));
        std::wstring identity(32768, L'\0');
        DWORD size = GetFinalPathNameByHandleW(dir.Value, identity.data(), static_cast<DWORD>(identity.size()),
                                               FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        if (!size || size >= identity.size())
            throw std::runtime_error("无法识别程序安装路径。");
        identity.resize(size);
        CharUpperBuffW(identity.data(), size);
        auto key =
            HashBytes({reinterpret_cast<unsigned char const *>(identity.data()), identity.size() * sizeof(wchar_t)});
        PWSTR local{};
        if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local)))
            throw std::runtime_error("无法找到本机更新缓存目录。");
        fs::path root = fs::path(local) / L"HS2Manager/Updates" / Path(key.substr(0, 24));
        CoTaskMemFree(local);
        fs::create_directories(root);
        CheckTreePath(root, {});
        return root;
    }

    Handle InstallationLease(fs::path const &installation, bool exclusive)
    {
        auto root = CacheRoot(installation);
        auto lock = IsPortable(installation) ? installation / L".hs2-install.lock" : root / L"installation.lock";
        CheckTreePath(lock.parent_path(), lock.filename());
        // 预先创建后以只读共享方式持有，GUI 与 MCP 可同时读取，更新器必须独占。
        {
            Handle create(CreateFileW(lock.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_ALWAYS,
                                      FILE_ATTRIBUTE_NORMAL, nullptr));
        }
        Handle result(CreateFileW(lock.c_str(), GENERIC_READ, exclusive ? 0 : FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                                  FILE_ATTRIBUTE_NORMAL, nullptr));
        if (result.Value == INVALID_HANDLE_VALUE && !exclusive && !fs::exists(lock))
        {
            // 只读介质无法创建协作锁时持有程序映像，安装阶段的删除权限检查仍会拒绝替换。
            result = Handle(CreateFileW((installation / L"HS2Manager.exe").c_str(), GENERIC_READ, FILE_SHARE_READ,
                                        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
        }
        if (result.Value == INVALID_HANDLE_VALUE)
            throw std::runtime_error("该安装正在使用或更新，请关闭此安装的其他程序和 MCP 连接后重试。");
        if (!exclusive && fs::exists(installation / L".hs2-update-pending"))
            throw std::runtime_error("该安装有未完成的更新，请在发起更新的电脑上运行缓存中的更新器恢复。记录：" +
                                     ReadText(installation / L".hs2-update-pending"));
        if (!exclusive && fs::exists(root / L"transaction.json"))
        {
            auto journal = Json::parse(ReadText(root / L"transaction.json"));
            if (journal.value("state", "") == "applying")
                throw std::runtime_error("上次更新中断，请运行缓存中的 HS2Updater.exe --recover \"" +
                                         Utf8(root / L"transaction.json") + "\"。");
        }
        return result;
    }

    bool IsPortable(fs::path const &installation)
    {
        return fs::is_regular_file(installation / L"package-info.json") &&
               fs::is_regular_file(installation / L"package-files.json");
    }

    Release ParseRelease(std::string const &json)
    {
        auto j = Json::parse(json);
        if (j.value("draft", false) || j.value("prerelease", false))
            throw std::runtime_error("该发布不是正式版。");
        auto tag = j.at("tag_name").get<std::string>();
        if (!tag.starts_with('v'))
            throw std::runtime_error("发布版本标签格式不符。");
        Release result;
        result.Version = tag.substr(1);
        Version::Parse(result.Version);
        result.Notes = j.value("body", Json()).is_string() ? j.at("body").get<std::string>() : "";
        result.PageUrl = "https://github.com/taoyuan11/HS2-mangement/releases/tag/" + tag;
        result.ArchiveName = "HS2Manager-" + result.Version + "-cpp-win-x64.zip";
        for (auto const &asset : j.at("assets"))
        {
            auto name = asset.at("name").get<std::string>();
            if (name == result.ArchiveName)
            {
                if (!result.ArchiveUrl.empty())
                    throw std::runtime_error("发布附件重复。");
                result.ArchiveUrl = RequiredUrl(asset, result.Version, name);
                result.Bytes = asset.at("size").get<uint64_t>();
            }
            if (name == "update.json")
            {
                if (!result.ManifestUrl.empty())
                    throw std::runtime_error("更新清单重复。");
                result.ManifestUrl = RequiredUrl(asset, result.Version, name);
            }
        }
        if (result.ArchiveUrl.empty() || result.ManifestUrl.empty())
            throw std::runtime_error("此版本缺少完整更新附件，请打开发布页下载。");
        return result;
    }

    void ApplyManifest(Release &release, std::string const &json)
    {
        auto j = Json::parse(json);
        auto hash = Lower(j.at("sha256").get<std::string>());
        if (j.at("schemaVersion") != 1 || j.at("version").get<std::string>() != release.Version ||
            j.at("platform").get<std::string>() != "win-x64" ||
            j.at("archive").get<std::string>() != release.ArchiveName || j.at("bytes") != release.Bytes ||
            release.Bytes == 0 || release.Bytes > 2ULL * 1024 * 1024 * 1024 || hash.size() != 64 ||
            !std::all_of(hash.begin(), hash.end(),
                         [](unsigned char c)
                         {
                             return std::isxdigit(c) != 0;
                         }))
            throw std::runtime_error("更新清单与发布附件不一致。");
        release.SHA256 = hash;
    }

    std::optional<Release> Check(std::string const &current, std::stop_token stop, Fetch fetch)
    {
        auto response = fetch("https://api.github.com/repos/taoyuan11/HS2-mangement/releases/latest", stop);
        if (response.Status == 404)
            throw std::runtime_error("GitHub 尚未发布正式版本。");
        if (response.Status == 403 || response.Status == 429)
            throw std::runtime_error("GitHub 请求受限，请稍后重试。");
        if (response.Status != 200)
            throw std::runtime_error("无法读取 GitHub 发布信息（HTTP " + std::to_string(response.Status) + "）。");
        auto j = Json::parse(response.Body);
        if (j.value("draft", false) || j.value("prerelease", false))
            return {};
        auto tag = j.at("tag_name").get<std::string>();
        if (!tag.starts_with('v') || Version::Parse(tag.substr(1)) <= Version::Parse(current))
            return {};
        auto release = ParseRelease(response.Body);
        auto manifest = fetch(release.ManifestUrl, stop);
        if (manifest.Status != 200)
            throw std::runtime_error("无法下载更新清单。");
        ApplyManifest(release, manifest.Body);
        return release;
    }

    void ValidatePackage(fs::path const &package, std::string const &version, std::stop_token stop)
    {
        auto info = Json::parse(ReadText(package / L"package-info.json"));
        if (info.at("version").get<std::string>() != version || info.at("platform").get<std::string>() != "win-x64" ||
            info.at("schemaVersion") != 1)
            throw std::runtime_error("解压后的程序版本不符。");
        std::set<std::string> names;
        auto manifest = Json::parse(ReadText(package / L"package-files.json"));
        if (!manifest.is_array() || manifest.size() > 10000)
            throw std::runtime_error("包内文件清单无效。");
        for (auto const &entry : manifest)
        {
            CheckStop(stop);
            auto relative = SafeRelative(entry.at("Path").get<std::string>());
            auto key = Lower(Utf8(relative));
            if (!names.insert(key).second)
                throw std::runtime_error("包内文件路径重复。");
            CheckTreePath(package, relative);
            if (!fs::is_regular_file(package / relative) ||
                fs::file_size(package / relative) != entry.at("Bytes").get<uint64_t>() ||
                HashFile(package / relative, stop) != Lower(entry.at("SHA256").get<std::string>()))
                throw std::runtime_error("包内文件校验失败：" + Utf8(relative));
        }
        for (auto name : {"hs2manager.exe", "hs2agent.exe", "hs2updater.exe", "package-info.json"})
            if (!names.contains(name))
                throw std::runtime_error("更新包缺少必要文件。");
        names.insert("package-files.json");
        for (auto const &entry : fs::recursive_directory_iterator(package))
        {
            CheckOrdinary(entry.path());
            if (entry.is_regular_file() && !names.contains(Lower(Utf8(entry.path().lexically_relative(package)))))
                throw std::runtime_error("更新包包含未登记文件。");
        }
    }

    void Extract(Release const &release, fs::path const &archive, fs::path const &destination, std::stop_token stop)
    {
        if (fs::file_size(archive) != release.Bytes || HashFile(archive, stop) != release.SHA256)
            throw std::runtime_error("下载文件大小或 SHA-256 校验失败。");
        if (fs::exists(destination))
            throw std::runtime_error("更新解压目录已存在。");
        fs::create_directories(destination);
        try
        {
            hs2::ZipArchive zip(archive);
            auto prefix = "HS2Manager-" + release.Version + "-cpp-win-x64/";
            std::set<std::string> names;
            uint64_t total = 0;
            if (zip.Entries().size() > 10000)
                throw std::runtime_error("更新包条目过多。");
            for (auto const &entry : zip.Entries())
            {
                CheckStop(stop);
                if (entry.Link)
                    throw std::runtime_error("更新包不能包含链接条目。");
                if (!entry.Name.starts_with(prefix))
                    throw std::runtime_error("更新包根目录不符。");
                auto name = entry.Name.substr(prefix.size());
                if (entry.Directory && name.empty())
                    continue;
                if (entry.Directory && name.ends_with('/'))
                    name.pop_back();
                auto relative = SafeRelative(name);
                if (!names.insert(Lower(Utf8(relative))).second)
                    throw std::runtime_error("归档路径重复。");
                if (entry.Size > 512ULL * 1024 * 1024 || (total += entry.Size) > 4ULL * 1024 * 1024 * 1024)
                    throw std::runtime_error("更新包解压大小超限。");
                if (entry.Directory)
                {
                    fs::create_directories(destination / relative);
                    continue;
                }
                auto bytes = zip.Read(entry);
                fs::create_directories((destination / relative).parent_path());
                std::ofstream out(destination / relative, std::ios::binary);
                out.write(reinterpret_cast<char const *>(bytes.data()), bytes.size());
                out.close();
                if (!out)
                    throw std::runtime_error("更新文件写入失败。");
            }
            ValidatePackage(destination, release.Version, stop);
        }
        catch (...)
        {
            std::error_code ec;
            fs::remove_all(destination, ec);
            throw;
        }
    }
}
