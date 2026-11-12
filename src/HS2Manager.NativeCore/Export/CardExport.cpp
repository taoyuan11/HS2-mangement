#include "Export/CardExport.h"
#include <windows.h>
#include <algorithm>
#include <map>
#include <sstream>

namespace hs2
{
    namespace
    {
        std::string Lower(std::string text)
        {
            for (auto &c : text)
                if (c >= 'A' && c <= 'Z')
                    c = char(c - 'A' + 'a');
            return text;
        }

        bool ValidLevel(CardExportLevel level)
        {
            return int(level) >= 1 && int(level) <= 4;
        }

        void WriteText(const fs::path &path, const std::string &text)
        {
            ExportPaths::NoLinks(path);
            WriteAtomic(path, {reinterpret_cast<const std::uint8_t *>(text.data()), text.size()});
        }

        std::string IssueLabel(const std::vector<CardExportCard> &cards, const CardExportIssue &issue)
        {
            auto found = std::find_if(cards.begin(), cards.end(),
                                      [&](auto &card)
                                      {
                                          return card.Id == issue.CardId;
                                      });
            if (found == cards.end())
                return issue.Message;
            std::string name = found->Name;
            if (std::count_if(cards.begin(), cards.end(),
                              [&](auto &card)
                              {
                                  return card.Name == found->Name;
                              }) > 1)
                name += " [" + Utf8(Path(found->Source).filename()) + "]";
            return name + "：" + issue.Message;
        }

        std::string Manifest(const CardExportPackage &package, const std::string &directory,
                             const CardExportReport &report, std::string status = {})
        {
            std::set<std::string> ids;
            for (auto &card : package.Cards)
                ids.insert(card.Id);
            std::vector<CardExportIssue> issues;
            for (auto &issue : report.Issues)
                if ((issue.CardId.empty() || ids.contains(issue.CardId)) &&
                    std::find(issues.begin(), issues.end(), issue) == issues.end())
                    issues.push_back(issue);
            auto completed = std::count_if(report.Files.begin(), report.Files.end(),
                                           [&](auto &file)
                                           {
                                               return file.Package == directory;
                                           });
            auto successful = std::count_if(report.Files.begin(), report.Files.end(),
                                            [&](auto &file)
                                            {
                                                return file.Package == directory && file.Success;
                                            });
            if (status.empty())
                status = report.Status == CardExportStatus::Cancelled &&
                                 completed < static_cast<std::ptrdiff_t>(package.Files.size())
                             ? "导出已取消"
                         : !issues.empty() || successful != static_cast<std::ptrdiff_t>(package.Files.size())
                             ? "导出不完整"
                             : "导出完成";
            std::string text =
                status + "\n\n" + report.ScopeDescription() +
                "\n\n将本文件夹内的游戏目录复制到 HS2 根目录。\n已有同名资源请按实际需要处理。\n\n人物卡：\n";
            for (auto &card : package.Cards)
                text += card.Name + " ← " + card.Source + "\n";
            text += "\n文件：\n";
            for (auto &file : package.Files)
            {
                auto result = std::find_if(report.Files.begin(), report.Files.end(),
                                           [&](auto &item)
                                           {
                                               return item.Package == directory &&
                                                      item.File.RelativePath == file.RelativePath &&
                                                      item.File.Source == file.Source;
                                           });
                text += (result == report.Files.end() ? "[未复制] "
                         : result->Success            ? "[成功] "
                                                      : "[失败] ") +
                        file.RelativePath + " ← " + file.Source;
                if (result != report.Files.end() && !result->Success)
                    text += "：" + result->Message;
                text += "\n";
            }
            text += "\n缺失 / 未确认 / 冲突：\n";
            if (issues.empty())
                text += "所选级别范围内无已知缺失。未复制的文件不可视为已完成。\n";
            for (auto &issue : issues)
                text += IssueLabel(package.Cards, issue) + "\n";
            bool heading = false;
            for (auto &warning : report.Warnings)
                if (ids.contains(warning.CardId))
                {
                    if (!heading)
                    {
                        text += "\n缩略图提示（不影响人物外观）：\n";
                        heading = true;
                    }
                    text += IssueLabel(package.Cards, warning) + "\n";
                }
            return text;
        }

        fs::path ReserveDirectory(const fs::path &parent, const std::string &name)
        {
            auto staging = parent / Path(".hs2manager-export-" + NewId());
            fs::create_directory(staging);
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    auto target = parent / Path(ExportPaths::UniqueDirectoryName(parent, name, {}));
                    if (MoveFileExW(staging.c_str(), target.c_str(), MOVEFILE_WRITE_THROUGH))
                        return target;
                    auto error = GetLastError();
                    if (!fs::exists(target))
                        throw std::system_error(error, std::system_category(), "分配导出目录");
                }
                throw std::runtime_error("无法分配独立导出目录");
            }
            catch (...)
            {
                std::error_code error;
                fs::remove(staging, error);
                throw;
            }
        }
    }

    std::string ExportLevelLabel(CardExportLevel level)
    {
        switch (level)
        {
        case CardExportLevel::CardOnly:
            return "一级：仅原始 PNG";
        case CardExportLevel::WithMods:
            return "二级：包含 MOD";
        case CardExportLevel::WithPlugins:
            return "三级：包含插件";
        case CardExportLevel::WithResources:
            return "四级：包含配套资源";
        default:
            throw std::invalid_argument("导出级别无效");
        }
    }

    std::string ExportLevelContents(CardExportLevel level)
    {
        switch (level)
        {
        case CardExportLevel::CardOnly:
            return "完整原始人物卡，保留嵌入的人物及扩展数据。";
        case CardExportLevel::WithMods:
            return "原始人物卡和关联 MOD，包括迁移映射包及可确认的资源提供包。";
        case CardExportLevel::WithPlugins:
            return "原始人物卡、关联 MOD、依赖插件及递归依赖 DLL。";
        case CardExportLevel::WithResources:
            return "原始人物卡、关联 MOD、依赖插件，以及可识别的外部贴图、独立资源和插件配套文件。";
        default:
            throw std::invalid_argument("导出级别无效");
        }
    }

    std::string ExportLevelScope(CardExportLevel level)
    {
        switch (level)
        {
        case CardExportLevel::CardOnly:
            return "未包含 MOD、插件及配套资源，需要目标游戏已有这些依赖。";
        case CardExportLevel::WithMods:
            return "未包含插件及配套资源，需要目标游戏已有这些依赖。";
        case CardExportLevel::WithPlugins:
            return "未包含外部贴图、独立资源及插件配套文件，需要目标游戏已有这些资源。";
        case CardExportLevel::WithResources:
            return "不包含插件配置、基础加载器或游戏本体；目标游戏需已安装 BepInEx，插件沿用目标游戏设置。";
        default:
            throw std::invalid_argument("导出级别无效");
        }
    }

    std::string CardExportReport::ScopeDescription() const
    {
        return ExportLevelLabel(Level) + "\n" + ExportLevelContents(Level) + "\n" + ExportLevelScope(Level) +
               "\n导出完成表示所选级别范围内的文件已完成复制和校验。";
    }

    std::string CardExportReport::StatusText() const
    {
        return Status == CardExportStatus::Complete    ? "导出完成"
               : Status == CardExportStatus::Cancelled ? "导出已取消"
                                                       : "导出不完整";
    }

    int CardExportReport::ExportedCards() const
    {
        return static_cast<int>(std::count_if(Files.begin(), Files.end(),
                                              [](auto &file)
                                              {
                                                  return file.Success && file.File.Kind == CardExportFileKind::Card;
                                              }));
    }

    std::string CardExportReport::Summary() const
    {
        auto result = StatusText() + "（" + ExportLevelLabel(Level) + "）：" + std::to_string(ExportedCards()) +
                      " 张人物卡，" +
                      std::to_string(std::count_if(Files.begin(), Files.end(),
                                                   [](auto &file)
                                                   {
                                                       return file.Success;
                                                   })) +
                      " 个文件";
        if (!Issues.empty())
            result += "，" + std::to_string(Issues.size()) + " 项问题（详见导出清单）";
        if (!Warnings.empty())
            result += "，" + std::to_string(Warnings.size()) + " 项缩略图提示（不影响人物外观）";
        return result;
    }

    fs::path ExportPaths::Relative(const std::string &value)
    {
        auto text = value;
        std::replace(text.begin(), text.end(), '/', '\\');
        auto path = Path(text);
        if (text.empty() || path.is_absolute() || path.has_root_path())
            throw std::invalid_argument("导出相对路径无效");
        std::size_t start = 0;
        for (std::size_t i = 0; i <= text.size(); i++)
            if (i == text.size() || text[i] == '\\')
            {
                auto part = text.substr(start, i - start);
                if (part.empty() || part == "." || part == ".." || part.back() == '.' || part.back() == ' ' ||
                    part.find_first_of("<>:\"/\\|?*") != std::string::npos ||
                    std::any_of(part.begin(), part.end(),
                                [](unsigned char c)
                                {
                                    return c < 32;
                                }))
                    throw std::invalid_argument("导出路径包含无效或越界的目录");
                start = i + 1;
            }
        return path;
    }

    fs::path ExportPaths::Destination(const fs::path &root, const std::string &relative)
    {
        auto target = fs::absolute(root / Relative(relative));
        if (!Within(target, root))
            throw std::invalid_argument("导出路径越界");
        return target;
    }

    void ExportPaths::NoLinks(const fs::path &path)
    {
        for (auto current = fs::absolute(path); !current.empty(); current = current.parent_path())
        {
            auto attributes = GetFileAttributesW(current.c_str());
            if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT))
                throw std::runtime_error("导出不跟随符号链接或目录联接：" + Utf8(current));
            if (current == current.parent_path())
                break;
        }
    }

    std::string ExportPaths::SafeName(const std::string &name)
    {
        auto clean = Path(name).wstring();
        for (auto &c : clean)
            if (c < 32 || std::wstring_view(L"<>:\"/\\|?*").find(c) != std::wstring_view::npos)
                c = L'_';
        auto first = clean.find_first_not_of(L" \t\r\n");
        if (first != std::wstring::npos)
            clean = clean.substr(first);
        else
            clean.clear();
        while (!clean.empty() && (clean.back() == L'.' || clean.back() == L' '))
            clean.pop_back();
        if (clean.size() > 80)
            clean.resize(80);
        while (!clean.empty() &&
               (clean.back() == L'.' || clean.back() == L' ' || (clean.back() >= 0xd800 && clean.back() <= 0xdbff)))
            clean.pop_back();
        if (clean.empty())
            clean = L"未命名角色";
        auto stem = clean.substr(0, clean.find(L'.'));
        for (auto &c : stem)
            if (c >= L'a' && c <= L'z')
                c -= 32;
        if (stem == L"CON" || stem == L"PRN" || stem == L"AUX" || stem == L"NUL" || stem == L"CLOCK$" ||
            (stem.size() == 4 && (stem.starts_with(L"COM") || stem.starts_with(L"LPT")) &&
             std::wstring_view(L"123456789¹²³").find(stem[3]) != std::wstring_view::npos))
            clean = L"_" + clean;
        return Utf8(fs::path(clean));
    }

    std::string ExportPaths::UniqueDirectoryName(const fs::path &parent, const std::string &name,
                                                 const std::set<std::string> &reserved)
    {
        auto candidate = name;
        int index = 2;
        while (reserved.contains(Lower(candidate)) || fs::exists(parent / Path(candidate)))
            candidate = name + " (" + std::to_string(index++) + ")";
        return candidate;
    }

    void CardExportService::ValidateOutput(const fs::path &game, const fs::path &output)
    {
        if (Within(output, game) || SamePath(output, game))
            throw std::invalid_argument("请选择游戏目录之外的导出位置，以免修改或重复扫描原游戏资源");
        ExportPaths::NoLinks(output);
    }

    CardExportPlan CardExportService::BuildPlan(const std::vector<CardRecord> &cards, const fs::path &gamePath,
                                                const fs::path &outputPath, CardExportOptions options,
                                                Progress progress, std::stop_token token)
    {
        return BuildPlan(cards, gamePath, outputPath, options, progress, token, nullptr);
    }

    CardExportPlan CardExportService::BuildPlan(const std::vector<CardRecord> &cards, const fs::path &gamePath,
                                                const fs::path &outputPath, CardExportOptions options,
                                                Progress progress, std::stop_token token, const ModIndex *resourceIndex)
    {
        if (!ValidLevel(options.Level) ||
            (options.Mode != CardExportMode::Merged && options.Mode != CardExportMode::PerCard))
            throw std::invalid_argument("导出选项无效");
        auto game = fs::absolute(gamePath), output = fs::absolute(outputPath);
        ValidateOutput(game, output);
        std::vector<CardRecord> selected;
        for (auto &card : cards)
            if (std::none_of(selected.begin(), selected.end(),
                             [&](auto &other)
                             {
                                 return SamePath(Path(card.Path), Path(other.Path));
                             }))
                selected.push_back(card);
        if (selected.empty())
            throw std::invalid_argument("请先选择人物卡");
        CheckCancelled(token);
        CardExportPlan plan;
        plan.GamePath = Utf8(game);
        plan.OutputDirectory = Utf8(output);
        plan.Mode = options.Mode;
        plan.Level = options.Level;
        plan.RequestedCards = static_cast<int>(selected.size());
        std::unique_ptr<CardExportDependencies> dependencies;
        if (options.Level >= CardExportLevel::WithMods)
        {
            if (progress)
                progress({0, 0, "检查导出依赖…"});
            auto index = resourceIndex ? *resourceIndex : ModDependencyService(repository_).Scan(game, progress, token);
            dependencies = std::make_unique<CardExportDependencies>(game, std::move(index), token, classDatabase_);
        }
        std::set<std::string> names;
        std::map<std::string, std::string> hashes;
        auto hash = [&](const std::string &source)
        {
            auto key = Lower(source);
            auto found = hashes.find(key);
            if (found != hashes.end())
                return found->second;
            return hashes[key] = HashFile(Path(source), token);
        };
        auto addFile = [&](CardExportPackage &package, const std::string &cardId, const ExportCandidate &candidate)
        {
            CheckCancelled(token);
            try
            {
                auto relative = Utf8(ExportPaths::Relative(candidate.RelativePath));
                auto source = fs::absolute(Path(candidate.Source));
                auto size = static_cast<std::int64_t>(fs::file_size(source));
                auto modified = ModifiedTicks(source);
                ExportPaths::NoLinks(source);
                if (candidate.Kind == CardExportFileKind::Card)
                {
                    auto original = Path(relative);
                    int suffix = 2;
                    while (std::any_of(package.Files.begin(), package.Files.end(),
                                       [&](auto &file)
                                       {
                                           return Lower(file.RelativePath) == Lower(relative);
                                       }))
                        relative =
                            Utf8(original.parent_path() / Path(Utf8(original.stem()) + " (" + std::to_string(suffix++) +
                                                               ")" + Utf8(original.extension())));
                }
                auto key = Lower(relative);
                if (package.Conflicts.contains(key))
                {
                    package.Issues.push_back({cardId, "目标路径存在内容冲突，已跳过：" + relative});
                    return;
                }
                auto previous = std::find_if(package.Files.begin(), package.Files.end(),
                                             [&](auto &file)
                                             {
                                                 return Lower(file.RelativePath) == key;
                                             });
                if (previous != package.Files.end())
                {
                    if (SamePath(Path(previous->Source), source) ||
                        (previous->Size == size && hash(previous->Source) == hash(Utf8(source))))
                    {
                        if (std::find(previous->CardIds.begin(), previous->CardIds.end(), cardId) ==
                            previous->CardIds.end())
                            previous->CardIds.push_back(cardId);
                        return;
                    }
                    auto ids = previous->CardIds;
                    ids.push_back(cardId);
                    package.Files.erase(previous);
                    package.Conflicts.insert(key);
                    for (auto &id : ids)
                        package.Issues.push_back({id, "目标路径存在内容冲突，所有冲突文件均已跳过：" + relative});
                    return;
                }
                package.Files.push_back({Utf8(source), relative, candidate.Kind, size, modified, {cardId}});
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &error)
            {
                package.Issues.push_back({cardId, candidate.RelativePath + "：" + error.what()});
            }
        };
        for (std::size_t i = 0; i < selected.size(); i++)
        {
            CheckCancelled(token);
            auto &source = selected[i];
            if (progress)
                progress({static_cast<int>(i), static_cast<int>(selected.size()), "收集资源 " + source.FileName()});
            auto card = CardParser().Parse(Path(source.Path), token);
            card.Id = source.Id;
            if (card.Status != CardStatus::Valid || (card.Sex != 0 && card.Sex != 1))
            {
                plan.Issues.push_back({card.Id, source.FileName() + "：跳过，" +
                                                    (card.Error.empty() ? "无法识别人物卡或性别" : card.Error)});
                continue;
            }
            if (options.Mode == CardExportMode::PerCard || plan.Packages.empty())
            {
                auto base = options.Mode == CardExportMode::Merged ? "角色卡合并导出"
                            : card.Name.empty()                    ? Utf8(Path(card.Path).stem())
                                                                   : card.Name;
                auto name = ExportPaths::UniqueDirectoryName(output, ExportPaths::SafeName(base), names);
                names.insert(Lower(name));
                CardExportPackage package;
                package.FolderName = name;
                plan.Packages.push_back(std::move(package));
            }
            auto &package = options.Mode == CardExportMode::Merged ? plan.Packages.front() : plan.Packages.back();
            package.Cards.push_back({card.Id, card.Name, card.Path});
            if (options.Level >= CardExportLevel::WithMods && !card.DependenciesComplete)
                package.Issues.push_back({card.Id, card.Error});
            addFile(
                package, card.Id,
                {card.Path,
                 Utf8(fs::path(L"UserData") / L"chara" / (card.Sex == 0 ? L"male" : L"female") / Path(card.FileName())),
                 CardExportFileKind::Card});
            try
            {
                if (dependencies)
                {
                    auto collected = dependencies->Collect(card, options.Level);
                    for (auto &issue : collected.Issues)
                        package.Issues.push_back({card.Id, issue});
                    for (auto &warning : collected.Warnings)
                        package.Warnings.push_back({card.Id, warning});
                    for (auto &file : collected.Files)
                        addFile(package, card.Id, file);
                }
                if (static_cast<std::int64_t>(fs::file_size(Path(card.Path))) != card.Size ||
                    ModifiedTicks(Path(card.Path)) != card.ModifiedUtcTicks)
                {
                    std::erase_if(package.Files,
                                  [&](auto &file)
                                  {
                                      return file.Kind == CardExportFileKind::Card &&
                                             std::find(file.CardIds.begin(), file.CardIds.end(), card.Id) !=
                                                 file.CardIds.end();
                                  });
                    package.Issues.push_back({card.Id, "卡片在依赖分析期间发生变化，未导出该卡片，请重新导出"});
                }
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &error)
            {
                package.Issues.push_back({card.Id, "依赖分析不完整：" + std::string(error.what())});
            }
        }
        if (progress)
            progress({static_cast<int>(selected.size()), static_cast<int>(selected.size()), "导出计划已就绪"});
        return plan;
    }

    CardExportReport CardExportService::Export(const CardExportPlan &plan, Progress progress, std::stop_token token)
    {
        if (!ValidLevel(plan.Level))
            throw std::invalid_argument("导出级别无效");
        ValidateOutput(Path(plan.GamePath), Path(plan.OutputDirectory));
        CardExportReport report;
        report.Level = plan.Level;
        report.Issues = plan.Issues;
        int total = 0, completed = 0;
        for (auto &package : plan.Packages)
        {
            report.Issues.insert(report.Issues.end(), package.Issues.begin(), package.Issues.end());
            report.Warnings.insert(report.Warnings.end(), package.Warnings.begin(), package.Warnings.end());
            total += static_cast<int>(package.Files.size());
        }
        std::map<std::size_t, std::string> directories;
        CardFileService service(repository_);
        try
        {
            CheckCancelled(token);
            auto output = Path(plan.OutputDirectory);
            fs::create_directories(output);
            auto timestamp = UtcNow();
            std::replace(timestamp.begin(), timestamp.end(), ':', '-');
            report.ReportPath =
                Utf8(CardFileService::UniqueDestination(output, Path("导出报告-" + timestamp + ".txt")));
            for (std::size_t i = 0; i < plan.Packages.size(); i++)
            {
                CheckCancelled(token);
                auto &package = plan.Packages[i];
                fs::path directory;
                try
                {
                    directory = ReserveDirectory(output, package.FolderName);
                    directories[i] = Utf8(directory);
                    report.OutputFolders.push_back(Utf8(directory));
                    WriteText(directory / L"导出清单.txt",
                              Manifest(package, Utf8(directory), report, "尚未完成（如操作中断，请重新导出）"));
                }
                catch (const std::exception &error)
                {
                    for (auto &card : package.Cards)
                        report.Issues.push_back({card.Id, "无法创建导出目录：" + std::string(error.what())});
                    continue;
                }
                for (auto &file : package.Files)
                {
                    CheckCancelled(token);
                    if (progress)
                        progress({completed, total,
                                  "导出 " + std::to_string(completed + 1) + "/" + std::to_string(total) + "：" +
                                      file.RelativePath});
                    try
                    {
                        auto target = ExportPaths::Destination(directory, file.RelativePath);
                        ExportPaths::NoLinks(target);
                        ExportPaths::NoLinks(Path(file.Source));
                        service.ExportFile(Path(file.Source), target, file.Size, file.ModifiedUtcTicks, token);
                        report.Files.push_back({Utf8(directory), file, true, "SHA-256 校验通过"});
                    }
                    catch (const Cancelled &)
                    {
                        throw;
                    }
                    catch (const std::exception &error)
                    {
                        report.Files.push_back({Utf8(directory), file, false, error.what()});
                        for (auto &id : file.CardIds)
                            report.Issues.push_back({id, file.RelativePath + "：" + error.what()});
                    }
                    completed++;
                }
            }
            report.Status = !report.Issues.empty() || report.ExportedCards() != plan.RequestedCards
                                ? CardExportStatus::Incomplete
                                : CardExportStatus::Complete;
        }
        catch (const Cancelled &)
        {
            report.Status = CardExportStatus::Cancelled;
        }
        catch (const std::exception &error)
        {
            report.Issues.push_back({{}, "导出未完成：" + std::string(error.what())});
            report.Status = CardExportStatus::Incomplete;
        }
        for (auto &[index, directory] : directories)
        {
            try
            {
                WriteText(Path(directory) / L"导出清单.txt", Manifest(plan.Packages[index], directory, report));
            }
            catch (const std::exception &error)
            {
                report.Issues.push_back({{}, "无法写入导出清单：" + std::string(error.what())});
            }
        }
        if (report.Status == CardExportStatus::Complete && !report.Issues.empty())
            report.Status = CardExportStatus::Incomplete;
        if (!report.ReportPath.empty())
        {
            try
            {
                std::string text = report.Summary() + " / 选中 " + std::to_string(plan.RequestedCards) + " 张\n\n" +
                                   report.ScopeDescription() + "\n\n";
                for (auto &folder : report.OutputFolders)
                    text += folder + "\n";
                std::vector<CardExportCard> cards;
                for (auto &package : plan.Packages)
                    cards.insert(cards.end(), package.Cards.begin(), package.Cards.end());
                text += "\n缺失 / 未确认 / 冲突：\n";
                for (auto &issue : report.Issues)
                    text += IssueLabel(cards, issue) + "\n";
                text += "\n缩略图提示（不影响人物外观）：\n";
                for (auto &warning : report.Warnings)
                    text += IssueLabel(cards, warning) + "\n";
                WriteText(Path(report.ReportPath), text);
            }
            catch (const std::exception &error)
            {
                report.Issues.push_back({{}, "无法写入总报告：" + std::string(error.what())});
                report.ReportPath.clear();
                if (report.Status != CardExportStatus::Cancelled)
                    report.Status = CardExportStatus::Incomplete;
            }
        }
        if (progress)
            progress({completed, total, report.Summary()});
        return report;
    }
}
