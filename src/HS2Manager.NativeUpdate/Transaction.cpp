#include "Update.h"
#include "third_party/json.hpp"
#include <algorithm>
#include <map>
#include <set>
#include <stdexcept>

namespace hs2::update
{
    using Json = nlohmann::json;

    namespace
    {
        std::string Key(fs::path const &path)
        {
            auto name = path.wstring();
            CharUpperBuffW(name.data(), static_cast<DWORD>(name.size()));
            return Utf8(fs::path(name));
        }

        fs::path ResolvedDirectory(fs::path const &path)
        {
            Handle directory(CreateFileW(path.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                         nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr));
            std::wstring resolved(32768, L'\0');
            auto size = GetFinalPathNameByHandleW(directory.Value, resolved.data(), static_cast<DWORD>(resolved.size()),
                                                  FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
            if (!size || size >= resolved.size())
                throw std::runtime_error("无法确认安装目录与数据目录的实际位置。");
            resolved.resize(size);
            return fs::path(resolved);
        }

        struct File
        {
            fs::path Relative;
            std::string Hash;
            uint64_t Bytes = 0;
        };

        std::map<std::string, File> Files(fs::path const &root)
        {
            auto entries = Json::parse(ReadText(root / L"package-files.json"));
            if (!entries.is_array() || entries.size() > 10000)
                throw std::runtime_error("安装文件清单无效。");
            std::map<std::string, File> files;
            for (auto const &entry : entries)
            {
                auto relative = SafeRelative(entry.at("Path").get<std::string>());
                if (Protected(relative))
                    continue;
                if (Key(relative) == "PACKAGE-FILES.JSON")
                    throw std::runtime_error("文件清单不能包含自身。");
                auto hash = entry.at("SHA256").get<std::string>();
                std::transform(hash.begin(), hash.end(), hash.begin(),
                               [](unsigned char c)
                               {
                                   return char(std::tolower(c));
                               });
                if (hash.size() != 64 || !std::all_of(hash.begin(), hash.end(),
                                                      [](unsigned char c)
                                                      {
                                                          return std::isxdigit(c);
                                                      }))
                    throw std::runtime_error("安装文件清单校验值无效。");
                if (!files.emplace(Key(relative), File{relative, hash, entry.at("Bytes").get<uint64_t>()}).second)
                    throw std::runtime_error("安装文件清单路径重复。");
            }
            files.emplace("PACKAGE-FILES.JSON", File{L"package-files.json", HashFile(root / L"package-files.json"),
                                                     fs::file_size(root / L"package-files.json")});
            return files;
        }

        Json Plan(fs::path const &installation, fs::path const &package, fs::path const &data)
        {
            if (!IsPortable(installation))
                throw std::runtime_error("源码构建目录不支持自动替换，请下载便携版。");
            CheckTreePath(installation, {});
            auto version = Json::parse(ReadText(package / L"package-info.json")).at("version").get<std::string>();
            auto previous = Json::parse(ReadText(installation / L"package-info.json")).at("version").get<std::string>();
            if (Version::Parse(version) <= Version::Parse(previous))
                throw std::runtime_error("只允许安装较新的正式版本。");
            ValidatePackage(package, version);
            auto old = Files(installation), next = Files(package);
            std::set<std::string> names;
            for (auto const &[key, entry] : old)
                names.insert(key);
            for (auto const &[key, entry] : next)
                names.insert(key);
            Json entries = Json::array();
            uint64_t bytes = 0;
            auto dataPath = ResolvedDirectory(data);
            auto installationPath = ResolvedDirectory(installation);
            for (auto const &key : names)
            {
                bool hasOld = old.contains(key), hasNew = next.contains(key);
                auto relative = hasOld ? old.at(key).Relative : next.at(key).Relative;
                // Windows 不区分大小写；新旧清单必须使用同一规范路径。
                auto target = fs::absolute(installation / relative).lexically_normal();
                auto resolvedTarget = installationPath / relative;
                if (Key(resolvedTarget) == Key(dataPath) || Key(resolvedTarget).starts_with(Key(dataPath) + "/"))
                    throw std::runtime_error("更新文件与自定义数据目录重叠。");
                CheckTreePath(installation, relative);
                if (hasOld)
                {
                    if (!fs::is_regular_file(target) || HashFile(target) != old.at(key).Hash)
                        throw std::runtime_error("程序文件已被修改或缺失，请先恢复后重试：" + Utf8(relative));
                    // 预检独占打开，不删除文件，也不强制结束占用者。
                    Handle probe(CreateFileW(target.c_str(), GENERIC_READ | DELETE, FILE_SHARE_READ | FILE_SHARE_DELETE,
                                             nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
                    if (probe.Value == INVALID_HANDLE_VALUE)
                        throw std::runtime_error("程序文件被占用或没有替换权限：" + Utf8(relative));
                }
                else if (fs::exists(target))
                    throw std::runtime_error("新版本文件与用户文件冲突：" + Utf8(relative));
                if (hasNew)
                    bytes += next.at(key).Bytes;
                entries.push_back({{"path", Utf8(relative)},
                                   {"source", hasNew ? Utf8(next.at(key).Relative) : ""},
                                   {"old", hasOld ? old.at(key).Hash : ""},
                                   {"new", hasNew ? next.at(key).Hash : ""}});
            }
            if (fs::space(installation).available < bytes + 16 * 1024 * 1024)
                throw std::runtime_error("安装磁盘空间不足。");
            return {{"installation", Utf8(fs::absolute(installation).lexically_normal())},
                    {"data", Utf8(dataPath)},
                    {"version", version},
                    {"state", "prepared"},
                    {"entries", entries}};
        }

        void Move(fs::path const &from, fs::path const &to)
        {
            fs::create_directories(to.parent_path());
            if (!MoveFileExW(from.c_str(), to.c_str(), MOVEFILE_WRITE_THROUGH))
                throw std::runtime_error("更新文件移动失败（Windows " + std::to_string(GetLastError()) + "）：" +
                                         Utf8(from));
        }

        Json ReadJournal(fs::path const &path)
        {
            auto j = Json::parse(ReadText(path));
            auto installation = Path(j.at("installation"));
            if (fs::absolute(path).lexically_normal() != CacheRoot(installation) / L"transaction.json")
                throw std::runtime_error("更新恢复记录位置不符。");
            auto stage = Path(j.at("stage"));
            if (stage.parent_path() != installation || !Utf8(stage.filename()).starts_with(".hs2-update-"))
                throw std::runtime_error("更新恢复目录越界。");
            CheckTreePath(stage, {});
            for (auto const &entry : j.at("entries"))
            {
                auto relative = SafeRelative(entry.at("path"));
                if (Protected(relative))
                    throw std::runtime_error("恢复清单包含用户数据。");
                CheckTreePath(installation, relative);
                CheckTreePath(stage / L"backup", relative);
            }
            return j;
        }

        void RemoveStage(fs::path const &stage)
        {
            CheckTreePath(stage, {});
            if (fs::exists(stage))
            {
                for (auto const &entry : fs::recursive_directory_iterator(stage))
                    CheckOrdinary(entry.path());
                fs::remove_all(stage);
            }
        }

        std::wstring Quote(std::wstring const &value)
        {
            std::wstring out = L"\"";
            size_t slashes = 0;
            for (auto c : value)
            {
                if (c == L'\\')
                {
                    ++slashes;
                    continue;
                }
                out.append(slashes * (c == L'"' ? 2 : 1), L'\\');
                slashes = 0;
                if (c == L'"')
                    out += L'\\';
                out += c;
            }
            out.append(slashes * 2, L'\\');
            return out + L'"';
        }

        Handle Start(fs::path const &exe, std::wstring command, fs::path const &cwd)
        {
            STARTUPINFOW startup{sizeof(startup)};
            PROCESS_INFORMATION process{};
            if (!CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr,
                                cwd.c_str(), &startup, &process))
                throw std::runtime_error("无法启动程序（Windows " + std::to_string(GetLastError()) + "）。");
            CloseHandle(process.hThread);
            return Handle(process.hProcess);
        }
    }

    void Preflight(fs::path const &installation, fs::path const &package, fs::path const &data)
    {
        // 主程序仍在运行时只校验内容；删除权限与占用由退出后的独占安装阶段复检。
        if (!IsPortable(installation))
            throw std::runtime_error("开发构建只能检查更新，请下载并使用便携包。");
        auto info = Json::parse(ReadText(package / L"package-info.json"));
        ValidatePackage(package, info.at("version").get<std::string>());
        CheckTreePath(installation, {});
        if (data.empty())
            throw std::runtime_error("更新需要明确的数据目录。");
        if (fs::exists(installation / L".hs2-update-pending"))
            throw std::runtime_error("请先在原电脑上恢复该安装未完成的更新。");
        auto pending = CacheRoot(installation) / L"transaction.json";
        if (fs::exists(pending))
        {
            auto j = ReadJournal(pending);
            if (j.at("state").get<std::string>() == "applying")
                throw std::runtime_error("请先恢复上次中断的更新。");
            if (j.at("state").get<std::string>() == "installed")
                throw std::runtime_error("上次更新备份尚未确认，请先完成恢复或清理。");
        }
    }

    fs::path Install(fs::path const &installation, fs::path const &package, fs::path const &data,
                     std::function<void(size_t)> afterMove)
    {
        Preflight(installation, package, data);
        auto j = Plan(installation, package, data);
        auto root = Path(j.at("installation"));
        auto stage = root / Path(".hs2-update-" + Id());
        auto journal = CacheRoot(root) / L"transaction.json";
        j["stage"] = Utf8(stage);
        try
        {
            fs::create_directory(stage);
            for (auto const &entry : j.at("entries"))
            {
                if (entry.at("new").get<std::string>() == "")
                    continue;
                auto relative = SafeRelative(entry.at("path"));
                auto source = SafeRelative(entry.at("source"));
                auto file = stage / L"new" / relative;
                fs::create_directories(file.parent_path());
                fs::copy_file(package / source, file);
                if (HashFile(file) != entry.at("new").get<std::string>())
                    throw std::runtime_error("安装盘暂存文件校验失败。");
            }
            j["state"] = "applying";
            WriteText(journal, j.dump(2));
            WriteText(root / L".hs2-update-pending", Utf8(journal));
            size_t moved = 0;
            for (auto const &entry : j.at("entries"))
            {
                auto relative = SafeRelative(entry.at("path"));
                CheckTreePath(root, relative);
                if (entry.at("old").get<std::string>() != "")
                {
                    if (HashFile(root / relative) != entry.at("old").get<std::string>())
                        throw std::runtime_error("安装文件在更新期间发生变化。");
                    Move(root / relative, stage / L"backup" / relative);
                    if (afterMove)
                        afterMove(++moved);
                }
                if (entry.at("new").get<std::string>() != "")
                {
                    Move(stage / L"new" / relative, root / relative);
                    if (afterMove)
                        afterMove(++moved);
                }
            }
            j["state"] = "installed";
            WriteText(journal, j.dump(2));
            fs::remove(root / L".hs2-update-pending");
            return journal;
        }
        catch (...)
        {
            auto failure = std::current_exception();
            if (fs::exists(journal))
            {
                auto saved = Json::parse(ReadText(journal));
                if (saved.value("stage", "") == Utf8(stage))
                {
                    if (saved.value("state", "") == "applying")
                        Recover(journal);
                    // 已安装但清理失败时保留备份，供明确恢复或确认完成。
                }
                else
                    RemoveStage(stage);
            }
            else
                RemoveStage(stage);
            std::rethrow_exception(failure);
        }
    }

    void Recover(fs::path const &journal)
    {
        auto j = ReadJournal(journal);
        if (j.at("state").get<std::string>() == "rolled-back" || j.at("state").get<std::string>() == "complete")
            return;
        auto root = Path(j.at("installation")), stage = Path(j.at("stage"));
        // 回滚也可中断，依据备份存在性与内容哈希重入，绝不删除未知文件。
        j["state"] = "applying";
        WriteText(journal, j.dump(2));
        auto entries = j.at("entries");
        for (auto it = entries.rbegin(); it != entries.rend(); ++it)
        {
            auto relative = SafeRelative(it->at("path"));
            auto target = root / relative, backup = stage / L"backup" / relative;
            bool hadOld = it->at("old").get<std::string>() != "";
            if (fs::exists(backup))
            {
                if (HashFile(backup) != it->at("old").get<std::string>())
                    throw std::runtime_error("更新备份损坏，已保留恢复目录。");
                if (fs::exists(target))
                {
                    if (HashFile(target) != it->at("new").get<std::string>())
                        throw std::runtime_error("目标文件已变化，回滚保留现场。");
                    fs::remove(target);
                }
                Move(backup, target);
            }
            else if (!hadOld && fs::exists(target))
            {
                if (HashFile(target) != it->at("new").get<std::string>())
                    throw std::runtime_error("新增文件已变化，回滚保留现场。");
                fs::remove(target);
            }
            else if (hadOld && (!fs::exists(target) || HashFile(target) != it->at("old").get<std::string>()))
                throw std::runtime_error("原始程序文件无法恢复，请保留恢复目录。");
        }
        j["state"] = "rolled-back";
        WriteText(journal, j.dump(2));
        fs::remove(root / L".hs2-update-pending");
        RemoveStage(stage);
    }

    void Complete(fs::path const &journal)
    {
        auto j = ReadJournal(journal);
        if (j.at("state").get<std::string>() != "installed" && j.at("state").get<std::string>() != "complete")
            throw std::runtime_error("安装尚未完成。");
        j["state"] = "complete";
        WriteText(journal, j.dump(2));
        fs::remove(Path(j.at("installation")) / L".hs2-update-pending");
        RemoveStage(Path(j.at("stage")));
        std::error_code ignored;
        fs::remove(journal.parent_path() / L"last-error.txt", ignored);
    }

    fs::path PrepareLaunch(fs::path const &installation, fs::path const &package, fs::path const &data)
    {
        Preflight(installation, package, data);
        auto root = CacheRoot(installation);
        auto helper = root / L"HS2Updater.exe";
        fs::copy_file(installation / L"HS2Updater.exe", helper, fs::copy_options::overwrite_existing);
        if (HashFile(helper) != HashFile(installation / L"HS2Updater.exe"))
            throw std::runtime_error("更新器复制校验失败。");
        // 每次交接使用独立请求，避免同一安装的其他窗口覆盖父进程和待安装包。
        auto request = root / Path("request-" + Id() + ".json");
        FILETIME creation{}, exit{}, kernel{}, user{};
        if (!GetProcessTimes(GetCurrentProcess(), &creation, &exit, &kernel, &user))
            throw std::runtime_error("无法识别当前进程。");
        WriteText(request, Json{{"installation", Utf8(fs::absolute(installation))},
                                {"package", Utf8(fs::absolute(package))},
                                {"data", Utf8(fs::absolute(data))},
                                {"parent", GetCurrentProcessId()},
                                {"created", (uint64_t(creation.dwHighDateTime) << 32) | creation.dwLowDateTime}}
                               .dump(2));
        return request;
    }

    Handle LaunchUpdater(fs::path const &request)
    {
        auto exe = request.parent_path() / L"HS2Updater.exe";
        auto eventName = L"Local\\HS2Manager.UpdateHandoff." + Path(Id()).wstring();
        Handle ready(CreateEventW(nullptr, TRUE, FALSE, eventName.c_str()));
        auto process = Start(
            exe, Quote(exe.wstring()) + L" --install " + Quote(request.wstring()) + L" --handoff " + Quote(eventName),
            request.parent_path());
        HANDLE handles[]{ready.Value, process.Value};
        if (WaitForMultipleObjects(2, handles, FALSE, 10000) != WAIT_OBJECT_0)
        {
            // 只终止本次交接创建且尚未获得安装授权的助手，防止迟到的交接误触发更新。
            TerminateProcess(process.Value, ERROR_CANCELLED);
            WaitForSingleObject(process.Value, 5000);
            throw std::runtime_error("更新器未准备就绪，程序继续运行。");
        }
        return process;
    }

    Handle StartApplication(fs::path const &installation, fs::path const &data, std::wstring const &readyEvent)
    {
        auto exe = installation / L"HS2Manager.exe";
        auto args = Quote(exe.wstring()) + L" --data " + Quote(data.wstring());
        if (!readyEvent.empty())
            args += L" --update-ready " + Quote(readyEvent);
        return Start(exe, args, installation);
    }
}
