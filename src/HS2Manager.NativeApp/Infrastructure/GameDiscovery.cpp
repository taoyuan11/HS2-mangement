#include "Infrastructure/GameDiscovery.h"
#include <windows.h>
#include <algorithm>
#include <chrono>
#include <cwctype>
#include <deque>
#include <map>
#include <string>

namespace hs2::ui
{
    namespace fs = std::filesystem;

    namespace
    {
        std::wstring Lower(std::wstring text)
        {
            std::transform(text.begin(), text.end(), text.begin(), towlower);
            return text;
        }

        std::wstring RegistryText(HKEY key, const wchar_t *name)
        {
            DWORD size = 0;
            if (RegGetValueW(key, nullptr, name, RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ, nullptr, nullptr, &size) !=
                    ERROR_SUCCESS ||
                size > 65536)
                return {};
            std::wstring result(size / sizeof(wchar_t), L'\0');
            if (RegGetValueW(key, nullptr, name, RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ, nullptr, result.data(), &size) !=
                ERROR_SUCCESS)
                return {};
            result.resize(wcslen(result.c_str()));
            return result;
        }

        void InstalledGames(std::vector<GameSearchLocation> &locations)
        {
            for (auto hive : {HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE})
                for (auto view : {KEY_WOW64_32KEY, KEY_WOW64_64KEY})
                {
                    HKEY uninstall{};
                    if (RegOpenKeyExW(hive, L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", 0,
                                      KEY_READ | view, &uninstall) != ERROR_SUCCESS)
                        continue;
                    for (DWORD index = 0; index < 4096; ++index)
                    {
                        wchar_t name[256]{};
                        DWORD length = static_cast<DWORD>(std::size(name));
                        auto status =
                            RegEnumKeyExW(uninstall, index, name, &length, nullptr, nullptr, nullptr, nullptr);
                        if (status == ERROR_NO_MORE_ITEMS)
                            break;
                        if (status != ERROR_SUCCESS)
                            continue;
                        HKEY entry{};
                        if (RegOpenKeyExW(uninstall, name, 0, KEY_READ, &entry) != ERROR_SUCCESS)
                            continue;
                        auto display = Lower(RegistryText(entry, L"DisplayName"));
                        auto path = RegistryText(entry, L"InstallLocation");
                        RegCloseKey(entry);
                        display.erase(std::remove_if(display.begin(), display.end(), iswspace), display.end());
                        if (!path.empty() && (display.find(L"honeyselect2") != display.npos ||
                                              display.find(L"ハニーセレクト2") != display.npos))
                            locations.push_back({path, 0});
                    }
                    RegCloseKey(uninstall);
                }
        }

        bool SkipDirectory(const fs::directory_entry &entry)
        {
            auto attributes = GetFileAttributesW(entry.path().c_str());
            if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY) ||
                (attributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_SYSTEM)))
                return true;
            auto name = Lower(entry.path().filename().wstring());
            // 不跟随目录联接，也不遍历系统目录、人物卡或大型资源树。
            return name == L"windows" || name == L"users" || name == L"programdata" || name == L"appdata" ||
                   name == L"userdata" || name == L"abdata" || name == L"mods" || name == L"bepinex" ||
                   name == L"node_modules" || name.starts_with(L".") || name.starts_with(L"$");
        }
    }

    bool IsGameDirectory(const fs::path &directory)
    {
        if (directory.empty() || !directory.is_absolute())
            return false;
        std::error_code error;
        return fs::is_regular_file(directory / L"HoneySelect2.exe", error) &&
               fs::is_directory(directory / L"HoneySelect2_Data", error) &&
               fs::is_directory(directory / L"abdata", error);
    }

    std::vector<GameSearchLocation> GameSearchLocations(const fs::path &applicationDirectory)
    {
        std::vector<GameSearchLocation> result;
        // 支持管理器放在游戏目录内或旁边；先检查明确位置，再检查磁盘上的浅层目录。
        auto parent = applicationDirectory;
        for (int i = 0; i < 3 && !parent.empty(); ++i)
        {
            result.push_back({parent, 0});
            if (parent == parent.parent_path())
                break;
            parent = parent.parent_path();
        }
        InstalledGames(result);
        result.push_back({applicationDirectory, 2});
        if (applicationDirectory.parent_path() != applicationDirectory.root_path())
            result.push_back({applicationDirectory.parent_path(), 2});
        auto drives = GetLogicalDrives();
        for (int index = 0; index < 26; ++index)
        {
            if (!(drives & (1u << index)))
                continue;
            std::wstring root = L"A:\\";
            root[0] += static_cast<wchar_t>(index);
            auto type = GetDriveTypeW(root.c_str());
            if (type != DRIVE_FIXED && type != DRIVE_REMOVABLE && type != DRIVE_REMOTE)
                continue;
            for (auto folder : {L"Games", L"Game", L"游戏", L"ILLUSION", L"SteamLibrary/steamapps/common",
                                L"Program Files/Steam/steamapps/common", L"Program Files (x86)/Steam/steamapps/common"})
                result.push_back({fs::path(root) / folder, 3});
            result.push_back({root, 2});
        }
        return result;
    }

    fs::path FindGameDirectory(const std::vector<GameSearchLocation> &locations, std::stop_token stop)
    {
        std::deque<GameSearchLocation> pending(locations.begin(), locations.end());
        std::map<std::wstring, int> visited;
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(8);
        size_t entries = 0;
        // 限制枚举量和层数；慢速磁盘上的单次系统调用结束后才能响应取消。
        auto interrupted = [&]
        {
            return stop.stop_requested() || entries >= 12000 || std::chrono::steady_clock::now() >= deadline;
        };
        while (!pending.empty() && !interrupted())
        {
            auto current = std::move(pending.front());
            pending.pop_front();
            if (current.Directory.empty() || !current.Directory.is_absolute())
                continue;
            current.Directory = current.Directory.lexically_normal();
            auto key = Lower(current.Directory.wstring());
            auto found = visited.find(key);
            if (found != visited.end() && found->second >= current.Depth)
                continue;
            visited[key] = current.Depth;
            ++entries;
            if (IsGameDirectory(current.Directory))
                return stop.stop_requested() ? fs::path{} : current.Directory;
            if (current.Depth <= 0)
                continue;
            std::error_code error;
            fs::directory_iterator iterator(current.Directory, fs::directory_options::skip_permission_denied, error);
            std::vector<fs::path> children;
            for (; !error && iterator != fs::directory_iterator() && !interrupted(); iterator.increment(error))
            {
                ++entries;
                if (!SkipDirectory(*iterator))
                    children.push_back(iterator->path());
            }
            std::sort(children.begin(), children.end());
            for (auto const &child : children)
                pending.push_back({child, current.Depth - 1});
        }
        return {};
    }
}
