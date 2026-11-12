#include "CreationService.h"
#include <windows.h>
#include <algorithm>
#include <sstream>
#include <ranges>

namespace hs2::creation
{
    namespace
    {
        bool Identifier(const std::string &value)
        {
            return value.size() == 32 && value.find_first_not_of("0123456789abcdefABCDEF") == value.npos;
        }

        Json ReadJournal(const fs::path &path)
        {
            return Json::parse(ReadFile(path, 16 * 1024 * 1024));
        }

        void Target(const fs::path &game, const fs::path &path)
        {
            ExportPaths::NoLinks(path);
            if (!Within(path, game / "mods") && !Within(path, game / "UserData/chara"))
                throw std::invalid_argument("创作安装仅允许写入游戏 mods 和 UserData/chara 目录");
        }

        std::shared_ptr<void> InstallLock(const fs::path &game)
        {
            auto path = fs::absolute(game).lexically_normal().wstring();
            CharUpperBuffW(path.data(), static_cast<DWORD>(path.size()));
            auto utf8 = Utf8(fs::path(path));
            auto name = L"Local\\HS2CreationInstall-" + Path(Sha256(Bytes(utf8.begin(), utf8.end()))).wstring();
            auto handle = CreateMutexW(nullptr, FALSE, name.c_str());
            if (!handle)
                throw std::runtime_error("无法取得安装锁");
            auto waited = WaitForSingleObject(handle, 0);
            if (waited != WAIT_OBJECT_0 && waited != WAIT_ABANDONED)
            {
                CloseHandle(handle);
                throw std::runtime_error("另一个窗口正在安装或恢复此游戏的创作文件");
            }
            return std::shared_ptr<void>(handle,
                                         [](void *value)
                                         {
                                             ReleaseMutex(value);
                                             CloseHandle(value);
                                         });
        }

        void MoveNew(const fs::path &source, const fs::path &destination)
        {
            if (!MoveFileExW(NativeFilePath(source).c_str(), NativeFilePath(destination).c_str(),
                             MOVEFILE_WRITE_THROUGH))
                throw std::runtime_error("文件提交失败，目标可能已存在或被占用：" + Utf8(destination));
        }

        void CopyVerified(const fs::path &source, const fs::path &destination, const std::string &expected,
                          std::stop_token token)
        {
            ExportPaths::NoLinks(source);
            ExportPaths::NoLinks(destination);
            auto bytes = ReadFile(source, 1024ull * 1024 * 1024, token);
            if (Sha256(bytes) != expected)
                throw std::runtime_error("源文件在操作期间改变：" + Utf8(source));
            WriteAtomic(destination, bytes);
            if (HashFile(destination, token) != expected)
                throw std::runtime_error("复制校验失败：" + Utf8(destination));
        }

        InstallResult RestoreJournal(Json &journal, const fs::path &directory, const fs::path &game,
                                     std::stop_token token)
        {
            InstallResult result{journal.at("id"), "rolled_back"};
            auto save = [&]
            {
                WriteJson(directory / "operation.json", journal);
            };
            for (auto &file : journal.at("files") | std::views::reverse)
            {
                CheckCancelled(token);
                if (file.value("action", "") == "skip" || file.value("phase", "") == "rolled_back")
                    continue;
                auto target = ExportPaths::Destination(game, file.at("relative"));
                Target(game, target);
                auto previous = file.value("previous_hash", ""), hash = file.value("hash", "");
                auto action = file.at("action").get<std::string>();
                auto phase = file.value("phase", "prepared");
                auto temporary =
                    target.parent_path() / Path(".hs2creation-" + result.Id + "-" + file.at("index").dump() + ".tmp");
                auto swap = fs::path(temporary.wstring() + L".previous");
                try
                {
                    auto current = fs::is_regular_file(target) ? HashFile(target, token) : std::string{};
                    if (phase == "prepared")
                    {
                        if (fs::is_regular_file(temporary) && HashFile(temporary) == hash)
                            fs::remove(temporary);
                        file["phase"] = "rolled_back";
                        save();
                        continue;
                    }
                    if (!previous.empty() && current == previous)
                    {
                        file["phase"] = "rolled_back";
                        save();
                    }
                    else if (previous.empty())
                    {
                        if (!current.empty() && current != hash)
                            throw std::runtime_error("安装后文件已被外部修改");
                        if (!current.empty())
                        {
                            // 先移到唯一的同目录撤销文件，再核对，避免按旧摘要直接删除新文件。
                            auto removed = fs::path(temporary.wstring() + L".rollback");
                            MoveNew(target, removed);
                            if (HashFile(removed) != hash)
                            {
                                if (!fs::exists(target))
                                    MoveNew(removed, target);
                                throw std::runtime_error("回退时文件发生变化，已保留");
                            }
                            fs::remove(removed);
                        }
                        file["phase"] = "rolled_back";
                        save();
                    }
                    else
                    {
                        if (!current.empty() && (action == "remove" || current != hash))
                            throw std::runtime_error("安装后文件已被外部修改");
                        auto backup = directory / "Backups" / Path(file.at("index").dump() + ".bin");
                        ExportPaths::NoLinks(backup);
                        CopyVerified(backup, temporary, previous, token);
                        if (current.empty())
                            MoveNew(temporary, target);
                        else
                        {
                            auto rollbackSwap = fs::path(temporary.wstring() + L".undo");
                            if (!ReplaceFileW(NativeFilePath(target).c_str(), NativeFilePath(temporary).c_str(),
                                              NativeFilePath(rollbackSwap).c_str(), REPLACEFILE_IGNORE_MERGE_ERRORS,
                                              nullptr, nullptr))
                                throw std::runtime_error("恢复备份失败，文件可能被占用");
                            if (HashFile(rollbackSwap) != hash)
                            {
                                if (HashFile(target) == previous)
                                    ReplaceFileW(NativeFilePath(target).c_str(), NativeFilePath(rollbackSwap).c_str(),
                                                 NativeFilePath(temporary).c_str(), REPLACEFILE_IGNORE_MERGE_ERRORS,
                                                 nullptr, nullptr);
                                throw std::runtime_error("回退提交时目标改变，已保留外部文件");
                            }
                            fs::remove(rollbackSwap);
                        }
                        file["phase"] = "rolled_back";
                        save();
                    }
                    if (fs::is_regular_file(swap) && HashFile(swap) == previous)
                        fs::remove(swap);
                    if (fs::is_regular_file(temporary) &&
                        (HashFile(temporary) == hash || HashFile(temporary) == previous))
                        fs::remove(temporary);
                    if (target.extension() == ".png")
                        result.Cards.push_back(target);
                }
                catch (const std::exception &e)
                {
                    result.Status = "rollback_conflict";
                    result.Messages.push_back(Utf8(target) + "：" + e.what());
                    file["error"] = e.what();
                    save();
                }
            }
            journal["status"] = result.Status;
            journal["messages"] = result.Messages;
            save();
            return result;
        }
    }

    bool InstallPlan::HasChanges() const
    {
        return std::any_of(Files.begin(), Files.end(),
                           [](const auto &f)
                           {
                               return f.Action != "skip";
                           });
    }

    std::string InstallPlan::Summary() const
    {
        std::ostringstream text;
        for (const auto &f : Files)
            text << (f.Action == "remove"     ? "备份并移除旧版本："
                     : f.Action == "skip"     ? "相同内容，跳过："
                     : f.PreviousHash.empty() ? "新增："
                                              : "备份并更新：")
                 << Utf8(f.Destination) << '\n';
        for (const auto &message : Conflicts)
            text << "冲突：" << message << '\n';
        for (const auto &message : Warnings)
            text << "提示：" << message << '\n';
        return text.str();
    }

    InstallPlan CreationService::PlanInstall(const CreationState &state, const std::string &projectId,
                                             std::stop_token token)
    {
        if (!Identifier(projectId))
            throw std::invalid_argument("工程编号无效");
        if (!state.LastBuild.is_object() || state.LastBuild.value("status", "") != "complete")
            throw std::runtime_error("请先完成当前工程的构建检查");
        if (state.LastBuild.value("state_token", "") != state.Token)
            throw std::runtime_error("工程在构建后已有修改，请重新构建后安装");
        InstallPlan plan;
        plan.Game = game_;
        plan.ProjectId = projectId;
        plan.Guid = state.LastBuild.at("guid");
        if (!fs::is_directory(game_ / "abdata"))
            throw std::runtime_error("游戏目录离线或无效");
        auto mod = Path(state.LastBuild.value("mod", ""));
        auto card = Path(state.LastBuild.at("card"));
        if (!fs::is_regular_file(card) || HashFile(card, token) != state.LastBuild.at("card_hash").get<std::string>())
            throw std::runtime_error("构建后的角色卡丢失或改变，请重新构建");
        auto history = Installations();
        auto owned = [&](const fs::path &path, const std::string &hash)
        {
            for (const auto &operation : history)
                if (operation.value("project_id", "") == projectId && operation.value("guid", "") == plan.Guid)
                    for (const auto &file : operation.at("files"))
                        if (file.value("relative", "") == Utf8(path.lexically_relative(game_)) &&
                            (file.value("hash", "") == hash || file.value("previous_hash", "") == hash))
                            return true;
            return false;
        };
        auto add = [&](fs::path source, fs::path destination, std::string hash)
        {
            Target(game_, destination);
            InstallFile file{std::move(source), destination, std::move(hash)};
            if (fs::is_regular_file(destination))
            {
                file.PreviousHash = HashFile(destination, token);
                if (file.PreviousHash == file.Hash)
                    file.Action = "skip";
            }
            else if (fs::exists(destination))
                plan.Conflicts.push_back("目标不是文件：" + Utf8(destination));
            plan.Files.push_back(std::move(file));
        };
        if (!mod.empty())
        {
            if (!fs::is_regular_file(mod) || HashFile(mod, token) != state.LastBuild.at("mod_hash").get<std::string>())
                throw std::runtime_error("构建后的 Mod 丢失或改变，请重新构建");
            auto target = game_ / "mods/HS2Manager" / Path(projectId + ".zipmod");
            if (fs::is_regular_file(target))
            {
                auto existing = ModDependencyService::ReadManifest(target);
                if (!existing.Error.empty() || existing.Guid != plan.Guid)
                    plan.Conflicts.push_back("目标位置已有其他来源的 Mod：" + Utf8(target));
                else if (auto hash = HashFile(target, token);
                         hash != state.LastBuild.at("mod_hash").get<std::string>() && !owned(target, hash))
                    plan.Conflicts.push_back("此 Mod 不是本工程已安装的版本，或已被外部修改：" + Utf8(target));
            }
            InvalidateIndex();
            auto resources = Resources(nullptr, {}, token);
            if (!resources->Index.Complete)
                plan.Conflicts.push_back("MOD 清单未完整读取，不能安全判断同 GUID 冲突");
            for (const auto &existing : resources->Index.Files)
                if (existing.Guid == plan.Guid && !SamePath(Path(existing.Path), target))
                {
                    auto path = Path(existing.Path);
                    Target(game_, path);
                    auto hash = HashFile(path, token);
                    if (!owned(path, hash))
                        plan.Conflicts.push_back("同 GUID 的其他来源文件需手动处理：" + Utf8(path));
                    else
                        plan.Files.push_back({{}, path, "", hash, "remove"});
                }
            add(mod, target, state.LastBuild.at("mod_hash"));
        }
        auto folder = game_ / "UserData/chara" / (state.Character->Sex() == 0 ? "male" : "female");
        auto target = folder / Path("HS2Manager-" + projectId + ".png");
        if (fs::is_regular_file(target))
        {
            bool cardOwned = false;
            for (const auto &operation : Installations())
                if (operation.value("project_id", "") == projectId && operation.value("status", "") == "installed")
                    for (const auto &file : operation.at("files"))
                        if (file.value("relative", "") == Utf8(target.lexically_relative(game_)) &&
                            file.value("hash", "") == HashFile(target, token))
                            cardOwned = true;
            if (!cardOwned && HashFile(target, token) != state.LastBuild.at("card_hash").get<std::string>())
                plan.Conflicts.push_back("同名角色卡并非此工程安装的版本，或已被外部修改：" + Utf8(target));
        }
        add(card, target, state.LastBuild.at("card_hash"));
        for (const auto &issue : state.LastBuild.value("dependency_issues", Json::array()))
            plan.Conflicts.push_back(issue.is_string() ? issue.get<std::string>() : issue.dump());
        InvalidateIndex();
        auto currentResources = Resources(&state, mod, token);
        if (!currentResources->Index.Complete)
            plan.Conflicts.push_back("当前资源索引不完整，无法确认安装依赖");
        CardExportDependencies dependencies(game_, currentResources->Index, token, runtime_ / "classdata.tpk");
        auto checked = dependencies.Collect(CardParser().Parse(card, token), CardExportLevel::WithResources);
        plan.Conflicts.insert(plan.Conflicts.end(), checked.Issues.begin(), checked.Issues.end());
        plan.Warnings.insert(plan.Warnings.end(), checked.Warnings.begin(), checked.Warnings.end());
        for (const auto &file : checked.Files)
        {
            auto path = Path(file.Source);
            plan.Dependencies[path] = HashFile(path, token);
        }
        return plan;
    }

    void CreationService::UpdateInstalledIndex(const Json &journal)
    {
        CardRepository repository(data_);
        for (const auto &file : journal.at("files"))
        {
            auto path = ExportPaths::Destination(game_, file.at("relative"));
            Target(game_, path);
            if (path.extension() != ".png")
                continue;
            if (fs::is_regular_file(path))
                repository.SaveScan(CardParser().Parse(path));
            else if (auto old = repository.FindByPath(path))
                repository.Remove(old->Id);
        }
        ModDependencyService mods(repository);
        auto index = mods.Scan(game_);
        if (!index.Complete)
            throw std::runtime_error("安装索引未能完整更新");
        std::lock_guard lock(indexGate_);
        index_ = std::move(index);
    }

    InstallResult CreationService::Install(const InstallPlan &plan, Progress progress, std::stop_token token)
    {
        if (!SamePath(plan.Game, game_) || !Identifier(plan.Id) || !plan.Conflicts.empty())
            throw std::invalid_argument("安装计划存在冲突或目标游戏已改变");
        auto lock = InstallLock(game_);
        for (const auto &[path, hash] : plan.Dependencies)
            if (!fs::is_regular_file(path) || HashFile(path, token) != hash)
                throw std::runtime_error("安装依赖在检查后改变，请重新检查：" + Utf8(path));
        if (!plan.Guid.empty())
        {
            auto current = ModDependencyService::ScanTransient(game_, nullptr, {}, token);
            if (!current.Complete)
                throw std::runtime_error("安装前资源索引不完整");
            for (const auto &file : current.Files)
                if (file.Guid == plan.Guid && std::none_of(plan.Files.begin(), plan.Files.end(),
                                                           [&](auto &planned)
                                                           {
                                                               return SamePath(planned.Destination, Path(file.Path));
                                                           }))
                    throw std::runtime_error("检查后出现新的同 GUID Mod，请重新检查安装冲突");
        }
        auto directory = data_ / "Creation/Installations" / Path(plan.Id);
        ExportPaths::NoLinks(directory);
        if (fs::exists(directory))
            throw std::runtime_error("此安装计划已经使用，请重新检查");
        fs::create_directories(directory / "Backups");
        Json journal{
            {"version", 1},        {"id", plan.Id},         {"project_id", plan.ProjectId}, {"guid", plan.Guid},
            {"game", Utf8(game_)}, {"status", "preparing"}, {"created_utc", UtcNow()},      {"files", Json::array()}};
        auto save = [&]
        {
            WriteJson(directory / "operation.json", journal);
        };
        save();
        try
        {
            for (std::size_t i = 0; i < plan.Files.size(); ++i)
            {
                CheckCancelled(token);
                const auto &f = plan.Files[i];
                Target(game_, f.Destination);
                auto current = fs::is_regular_file(f.Destination) ? HashFile(f.Destination, token) : std::string{};
                if (current != f.PreviousHash)
                    throw std::runtime_error("安装计划过期，目标文件已经改变");
                auto relative = Utf8(f.Destination.lexically_relative(game_));
                journal["files"].push_back({{"index", i},
                                            {"relative", relative},
                                            {"hash", f.Hash},
                                            {"previous_hash", f.PreviousHash},
                                            {"action", f.Action},
                                            {"phase", "prepared"}});
                save();
                if (f.Action == "skip")
                    continue;
                if (!f.PreviousHash.empty())
                    CopyVerified(f.Destination, directory / "Backups" / Path(std::to_string(i) + ".bin"),
                                 f.PreviousHash, token);
                if (f.Action == "copy")
                {
                    fs::create_directories(f.Destination.parent_path());
                    auto temporary = f.Destination.parent_path() /
                                     Path(".hs2creation-" + plan.Id + "-" + std::to_string(i) + ".tmp");
                    CopyVerified(f.Source, temporary, f.Hash, token);
                }
            }
            journal["status"] = "committing";
            save();
            for (std::size_t i = 0; i < plan.Files.size(); ++i)
            {
                CheckCancelled(token);
                const auto &f = plan.Files[i];
                if (f.Action == "skip")
                    continue;
                if (progress)
                    progress({static_cast<int>(i), static_cast<int>(plan.Files.size()),
                              "安装 " + Utf8(f.Destination.filename())});
                auto current = fs::is_regular_file(f.Destination) ? HashFile(f.Destination, token) : std::string{};
                if (current != f.PreviousHash)
                    throw std::runtime_error("安装期间目标文件发生变化");
                auto temporary =
                    f.Destination.parent_path() / Path(".hs2creation-" + plan.Id + "-" + std::to_string(i) + ".tmp");
                auto swap = fs::path(temporary.wstring() + L".previous");
                journal["files"][i]["phase"] = "commit_pending";
                save();
                if (f.Action == "remove")
                    MoveNew(f.Destination, swap);
                else if (f.PreviousHash.empty())
                    MoveNew(temporary, f.Destination);
                else if (!ReplaceFileW(NativeFilePath(f.Destination).c_str(), NativeFilePath(temporary).c_str(),
                                       NativeFilePath(swap).c_str(), REPLACEFILE_IGNORE_MERGE_ERRORS, nullptr, nullptr))
                    throw std::runtime_error("更新文件失败，文件可能被占用");
                if (!f.PreviousHash.empty() && HashFile(swap) != f.PreviousHash)
                {
                    if (f.Action == "remove" && !fs::exists(f.Destination))
                        MoveNew(swap, f.Destination);
                    else if (fs::is_regular_file(f.Destination) && HashFile(f.Destination) == f.Hash)
                        ReplaceFileW(NativeFilePath(f.Destination).c_str(), NativeFilePath(swap).c_str(),
                                     NativeFilePath(temporary).c_str(), REPLACEFILE_IGNORE_MERGE_ERRORS, nullptr,
                                     nullptr);
                    throw std::runtime_error("提交时原文件改变，已保留外部文件");
                }
                journal["files"][i]["phase"] = "committed";
                save();
                if (fs::exists(swap))
                    fs::remove(swap);
            }
            journal["status"] = "indexing";
            save();
            UpdateInstalledIndex(journal);
            journal["index_phase"] = "committed";
            journal["status"] = "installed";
            save();
            InstallResult result{plan.Id, "installed"};
            for (const auto &f : plan.Files)
                if (f.Destination.extension() == ".png")
                    result.Cards.push_back(f.Destination);
            InvalidateIndex();
            return result;
        }
        catch (const std::exception &e)
        {
            journal["error"] = e.what();
            save();
            auto result = RestoreJournal(journal, directory, game_, {});
            result.Messages.insert(result.Messages.begin(), e.what());
            try
            {
                UpdateInstalledIndex(journal);
                journal["index_phase"] = "restored";
                save();
            }
            catch (const std::exception &indexError)
            {
                journal["index_phase"] = "pending";
                journal["index_error"] = indexError.what();
                save();
                result.Messages.push_back(indexError.what());
            }
            InvalidateIndex();
            return result;
        }
    }

    InstallResult CreationService::Rollback(const std::string &id, std::stop_token token)
    {
        if (!Identifier(id))
            throw std::invalid_argument("安装操作编号无效");
        auto lock = InstallLock(game_);
        auto directory = data_ / "Creation/Installations" / Path(id);
        ExportPaths::NoLinks(directory);
        auto journal = ReadJournal(directory / "operation.json");
        if (journal.at("id") != id || !SamePath(Path(journal.at("game")), game_))
            throw std::runtime_error("安装日志不属于当前游戏");
        auto result = RestoreJournal(journal, directory, game_, token);
        try
        {
            UpdateInstalledIndex(journal);
            journal["index_phase"] = "restored";
        }
        catch (const std::exception &e)
        {
            journal["index_phase"] = "pending";
            journal["index_error"] = e.what();
            result.Messages.push_back(e.what());
        }
        WriteJson(directory / "operation.json", journal);
        InvalidateIndex();
        return result;
    }

    std::vector<Json> CreationService::Installations() const
    {
        std::vector<Json> result;
        auto root = data_ / "Creation/Installations";
        if (!fs::is_directory(root))
            return result;
        for (const auto &entry : fs::directory_iterator(root))
            if (entry.is_directory() && Identifier(Utf8(entry.path().filename())) &&
                fs::is_regular_file(entry.path() / "operation.json"))
            {
                ExportPaths::NoLinks(entry.path());
                auto journal = ReadJournal(entry.path() / "operation.json");
                if (SamePath(Path(journal.at("game")), game_))
                    result.push_back(std::move(journal));
            }
        std::sort(result.begin(), result.end(),
                  [](const auto &a, const auto &b)
                  {
                      return a.at("created_utc") > b.at("created_utc");
                  });
        return result;
    }

    std::vector<InstallResult> CreationService::RecoverInstallations()
    {
        std::vector<InstallResult> results;
        for (const auto &journal : Installations())
            if (auto status = journal.value("status", "");
                status == "preparing" || status == "committing" || status == "indexing")
                results.push_back(Rollback(journal.at("id")));
            else if (journal.value("index_phase", "") == "pending")
            {
                auto lock = InstallLock(game_);
                auto recovered = journal;
                UpdateInstalledIndex(recovered);
                recovered["index_phase"] = "restored";
                WriteJson(data_ / "Creation/Installations" / Path(journal.at("id")) / "operation.json", recovered);
            }
        return results;
    }
}
