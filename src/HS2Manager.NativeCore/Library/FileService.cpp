#include "Common/Core.h"
#include <windows.h>
#include <algorithm>
#include <array>
#include <set>
#include <system_error>

namespace hs2
{
    namespace
    {
        void WinCheck(BOOL condition, const char *message)
        {
            if (!condition)
                throw std::system_error(GetLastError(), std::system_category(), message);
        }

        struct Handle
        {
            HANDLE Value;

            ~Handle()
            {
                if (Value != INVALID_HANDLE_VALUE)
                    CloseHandle(Value);
            }
        };

        std::int64_t NowTicks()
        {
            FILETIME t;
            GetSystemTimeAsFileTime(&t);
            ULARGE_INTEGER n{};
            n.LowPart = t.dwLowDateTime;
            n.HighPart = t.dwHighDateTime;
            return static_cast<std::int64_t>(n.QuadPart) + 504911232000000000ll;
        }

        bool Present(const fs::path &file)
        {
            bool present = false;
            for (auto &entry : fs::directory_iterator(file.parent_path()))
                if (SamePath(entry.path(), file))
                    present = true;
            return present;
        }

        bool OwnedTrash(const CardRecord &card, const fs::path &source)
        {
            auto folder = source.parent_path();
            return card.Availability == FileAvailability::Quarantined && Utf8(folder.filename()) == card.Id &&
                   _wcsicmp(folder.parent_path().filename().c_str(), L".HS2ManagerTrash") == 0;
        }

        bool DeletedPresent(const FileOperationRecord &op)
        {
            try
            {
                return Present(Path(op.Source));
            }
            catch (const fs::filesystem_error &)
            {
                auto folder = Path(op.Source).parent_path();
                if (op.Phase == "FileDeleted" && op.Card && OwnedTrash(*op.Card, Path(op.Source)))
                {
                    bool exists = false;
                    for (auto &entry : fs::directory_iterator(folder.parent_path()))
                        if (SamePath(entry.path(), folder))
                            exists = true;
                    if (!exists)
                        return false;
                }
                throw;
            }
        }

        bool Matches(const CardRecord &card, const CardDeleteRequest &request)
        {
            return card.Id == request.Id && SamePath(Path(card.Path), Path(request.Path)) &&
                   card.Size == request.Size && card.ModifiedUtcTicks == request.ModifiedUtcTicks;
        }

        void DeleteVerified(const CardDeleteRequest &request, std::stop_token token)
        {
            Handle handle{CreateFileW(Path(request.Path).c_str(), DELETE | FILE_READ_ATTRIBUTES, 0, nullptr,
                                      OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr)};
            WinCheck(handle.Value != INVALID_HANDLE_VALUE, "无法打开人物卡进行删除");
            BY_HANDLE_FILE_INFORMATION info{};
            WinCheck(GetFileInformationByHandle(handle.Value, &info), "无法核对人物卡文件");
            if (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))
                throw std::runtime_error("删除目标必须是普通 PNG 文件，不能是目录或链接");
            auto size = (std::uint64_t(info.nFileSizeHigh) << 32) | info.nFileSizeLow;
            auto time = (std::uint64_t(info.ftLastWriteTime.dwHighDateTime) << 32) | info.ftLastWriteTime.dwLowDateTime;
            if (size != static_cast<std::uint64_t>(request.Size) ||
                time + 504911232000000000ull != static_cast<std::uint64_t>(request.ModifiedUtcTicks))
                throw std::runtime_error("人物卡文件在选中后发生变化，请刷新后重新选择");
            CheckCancelled(token);
            FILE_DISPOSITION_INFO dispose{TRUE};
            WinCheck(SetFileInformationByHandle(handle.Value, FileDispositionInfo, &dispose, sizeof(dispose)),
                     "无法永久删除人物卡");
        }

        void ValidateDeletion(CardRepository &repository, const FileOperationRecord &op)
        {
            if (!op.Card)
                throw std::runtime_error("删除记录缺少人物卡信息");
            auto current = repository.FindById(op.Card->Id);
            if (current && !Matches(*current, {op.Card->Id, op.Source, op.ExpectedSize.value_or(op.Card->Size),
                                               op.ExpectedModifiedUtcTicks.value_or(op.Card->ModifiedUtcTicks)}))
                throw std::runtime_error("人物卡记录已变化，未清理新记录");
        }

        DWORD CALLBACK CopyProgress(LARGE_INTEGER, LARGE_INTEGER, LARGE_INTEGER, LARGE_INTEGER, DWORD, DWORD, HANDLE,
                                    HANDLE, LPVOID user)
        {
            return static_cast<std::stop_token *>(user)->stop_requested() ? PROGRESS_CANCEL : PROGRESS_CONTINUE;
        }

        void CopyNew(const fs::path &source, const fs::path &destination, std::stop_token token)
        {
            BOOL cancel = FALSE;
            if (!CopyFileExW(source.c_str(), destination.c_str(), CopyProgress, &token, &cancel,
                             COPY_FILE_FAIL_IF_EXISTS))
            {
                CheckCancelled(token);
                WinCheck(FALSE, "复制文件失败");
            }
            Handle handle{
                CreateFileW(destination.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr)};
            WinCheck(handle.Value != INVALID_HANDLE_VALUE, "打开校验副本");
            WinCheck(FlushFileBuffers(handle.Value), "刷新校验副本");
        }

        void RemoveOwnedTemporary(const FileOperationRecord &op, const std::string &path)
        {
            if (path.empty() || op.Destination.empty())
                return;
            auto p = Path(path);
            if (!SamePath(p.parent_path(), Path(op.Destination).parent_path()))
                return;
            auto name = Utf8(p.filename());
            if (name != ".hs2manager-" + op.Id + ".tmp" && name != ".hs2manager-" + op.Id + ".previous")
                return;
            std::error_code error;
            fs::remove(p, error);
        }
    }

    void CardFileService::Save(const FileOperationRecord &op) const
    {
        WriteJson(repository_.DataPath() / L"Operations" / Path(op.Id + ".json"), op);
    }

    FileOperationRecord CardFileService::NewOperation(std::string kind, const fs::path &source,
                                                      const fs::path &destination, std::optional<CardRecord> card)
    {
        if (SamePath(source, destination))
            throw std::invalid_argument("源文件与目标文件相同");
        FileOperationRecord op;
        op.Kind = std::move(kind);
        op.Source = Utf8(fs::absolute(source));
        op.Destination = Utf8(fs::absolute(destination));
        op.Card = std::move(card);
        op.Staging = Utf8(Path(op.Destination).parent_path() / Path(".hs2manager-" + op.Id + ".tmp"));
        Save(op);
        return op;
    }

    fs::path CardFileService::UniqueDestination(const fs::path &directory, const fs::path &fileName)
    {
        auto name = Utf8(fileName);
        if (fileName != fileName.filename() || name.empty() || name == "." || name == ".." ||
            name.find_first_of("<>:\"/\\|?*") != std::string::npos ||
            std::any_of(name.begin(), name.end(),
                        [](unsigned char c)
                        {
                            return c < 32;
                        }))
            throw std::invalid_argument("文件名无效");
        auto folder = fs::absolute(directory);
        auto path = folder / fileName;
        int index = 2;
        while (fs::exists(path))
        {
            path = folder /
                   Path(Utf8(fileName.stem()) + " (" + std::to_string(index++) + ")" + Utf8(fileName.extension()));
        }
        return path;
    }

    void CardFileService::CopyVerified(FileOperationRecord &op, std::stop_token token)
    {
        try
        {
            auto source = Path(op.Source), destination = Path(op.Destination), staging = Path(op.Staging);
            fs::create_directories(destination.parent_path());
            auto size = static_cast<std::int64_t>(fs::file_size(source)), modified = ModifiedTicks(source);
            if ((op.ExpectedSize && *op.ExpectedSize != size) ||
                (op.ExpectedModifiedUtcTicks && *op.ExpectedModifiedUtcTicks != modified))
                throw std::runtime_error("源文件在生成导出计划后发生变化，请重新导出");
            auto hash = HashFile(source, token);
            CopyNew(source, staging, token);
            op.Hash = hash;
            op.Phase = "Copied";
            Save(op);
            if (size != static_cast<std::int64_t>(fs::file_size(source)) || modified != ModifiedTicks(source) ||
                HashFile(source, token) != hash)
                throw std::runtime_error("复制时源文件发生变化，操作已中止");
            if (HashFile(staging, token) != hash)
                throw std::runtime_error("文件校验不一致，原文件已保留");
            CheckCancelled(token);
            WinCheck(MoveFileExW(staging.c_str(), destination.c_str(), MOVEFILE_WRITE_THROUGH), "提交副本");
            op.Phase = "Committed";
            Save(op);
        }
        catch (const std::exception &e)
        {
            op.Error = e.what();
            op.Phase = dynamic_cast<const Cancelled *>(&e) ? "Interrupted" : "NeedsAttention";
            try
            {
                Save(op);
            }
            catch (...)
            {
            }
            RemoveOwnedTemporary(op, op.Staging);
            throw;
        }
    }

    void CardFileService::IndexOperation(FileOperationRecord &op)
    {
        if (op.Kind == "Export" || !op.Card)
            return;
        auto card = repository_.FindById(op.Card->Id).value_or(*op.Card);
        card.Path = op.Destination;
        card.Hash = op.Hash;
        card.Size = fs::file_size(Path(card.Path));
        card.ModifiedUtcTicks = ModifiedTicks(Path(card.Path));
        if ((op.Kind == "Import" || op.Kind == "Move") && !Within(Path(card.Path), Path(card.Root)))
            card.Root = card.Folder();
        if (op.Kind == "Quarantine")
        {
            card.OriginalPath = op.Source;
            card.QuarantinedUtcTicks = NowTicks();
            card.Availability = FileAvailability::Quarantined;
        }
        else
        {
            card.Availability = FileAvailability::Available;
            if (op.Kind == "Restore")
            {
                card.OriginalPath.clear();
                card.QuarantinedUtcTicks = 0;
            }
        }
        auto stale = repository_.FindByPath(Path(card.Path));
        if (stale && stale->Id != card.Id && stale->Availability == FileAvailability::Missing)
            repository_.Remove(stale->Id);
        repository_.Save(card);
    }

    std::vector<FileOperationResult> CardFileService::Import(const std::vector<fs::path> &sources, const fs::path &game,
                                                             std::optional<fs::path> destination, Progress progress,
                                                             std::stop_token token)
    {
        std::vector<FileOperationResult> result;
        std::vector<fs::path> seen;
        CardParser parser;
        for (auto &source : sources)
        {
            CheckCancelled(token);
            if (std::any_of(seen.begin(), seen.end(),
                            [&](auto &p)
                            {
                                return SamePath(p, source);
                            }))
                continue;
            seen.push_back(source);
            if (progress)
                progress({static_cast<int>(seen.size()), static_cast<int>(sources.size()),
                          "导入 " + Utf8(source.filename())});
            try
            {
                auto card = parser.Parse(source, token);
                if (card.Status != CardStatus::Valid)
                    throw std::runtime_error("仅导入可识别的人物卡。" + card.Error);
                if (card.Sex != 0 && card.Sex != 1)
                    throw std::runtime_error("人物性别未知，请先在游戏中重新保存");
                auto folder =
                    destination.value_or(game / L"UserData" / L"chara" / (card.Sex == 0 ? L"male" : L"female"));
                fs::create_directories(folder);
                auto op = NewOperation("Import", source, UniqueDestination(folder, source.filename()), card);
                CopyVerified(op, token);
                card.Path = op.Destination;
                card.Root = Utf8(folder);
                card.Hash = op.Hash;
                card.Size = fs::file_size(Path(card.Path));
                card.ModifiedUtcTicks = ModifiedTicks(Path(card.Path));
                card.CoverFile = Utf8(fs::path(L"Covers") / Path(card.Id + ".png"));
                CardParser::CopyCover(Path(card.Path), card.PngLength, repository_.DataPath() / Path(card.CoverFile),
                                      token);
                repository_.Save(card);
                op.Card = card;
                op.Phase = "Indexed";
                Save(op);
                result.push_back({Utf8(source), op.Destination, true, "已导入"});
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &e)
            {
                result.push_back({Utf8(source), {}, false, e.what()});
            }
        }
        return result;
    }

    std::vector<FileOperationResult> CardFileService::Export(const std::vector<CardRecord> &cards,
                                                             const fs::path &folder, Progress progress,
                                                             std::stop_token token)
    {
        fs::create_directories(folder);
        std::vector<FileOperationResult> result;
        for (std::size_t i = 0; i < cards.size(); i++)
        {
            CheckCancelled(token);
            auto &card = cards[i];
            if (progress)
                progress({static_cast<int>(i + 1), static_cast<int>(cards.size()), "导出 " + card.FileName()});
            try
            {
                auto op =
                    NewOperation("Export", Path(card.Path), UniqueDestination(folder, Path(card.FileName())), card);
                CopyVerified(op, token);
                op.Phase = "Indexed";
                Save(op);
                result.push_back({card.Path, op.Destination, true, "原始文件已完整导出"});
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &e)
            {
                result.push_back({card.Path, {}, false, e.what()});
            }
        }
        return result;
    }

    void CardFileService::ExportFile(const fs::path &source, const fs::path &destination, std::int64_t size,
                                     std::int64_t modified, std::stop_token token)
    {
        auto op = NewOperation("Export", source, destination);
        op.ExpectedSize = size;
        op.ExpectedModifiedUtcTicks = modified;
        Save(op);
        CopyVerified(op, token);
        op.Phase = "Indexed";
        Save(op);
    }

    void CardFileService::MoveCore(const CardRecord &card, const fs::path &destination, std::string kind,
                                   std::stop_token token)
    {
        auto op =
            NewOperation(std::move(kind), Path(card.Path), destination, repository_.FindById(card.Id).value_or(card));
        try
        {
            CheckCancelled(token);
            fs::create_directories(destination.parent_path());
            op.Hash = HashFile(Path(op.Source), token);
            Save(op);
            CheckCancelled(token);
            if (!MoveFileExW(Path(op.Source).c_str(), destination.c_str(), MOVEFILE_WRITE_THROUGH))
            {
                if (GetLastError() != ERROR_NOT_SAME_DEVICE)
                    WinCheck(FALSE, "移动文件");
                CopyVerified(op, token);
                if (HashFile(Path(op.Source), token) != op.Hash)
                    throw std::runtime_error("源文件在移动过程中发生变化，已保留源文件与目标副本");
                CheckCancelled(token);
                WinCheck(DeleteFileW(Path(op.Source).c_str()), "移除已校验源文件");
            }
            op.Phase = "Committed";
            Save(op);
            IndexOperation(op);
            op.Phase = "Indexed";
            Save(op);
        }
        catch (const std::exception &e)
        {
            op.Error = e.what();
            op.Phase = dynamic_cast<const Cancelled *>(&e) ? "Interrupted" : "NeedsAttention";
            try
            {
                Save(op);
            }
            catch (...)
            {
            }
            throw;
        }
    }

    void CardFileService::Move(const CardRecord &card, const fs::path &folder, std::stop_token token)
    {
        MoveCore(card, UniqueDestination(folder, Path(card.FileName())), "Move", token);
    }

    void CardFileService::Rename(const CardRecord &card, std::string name, std::stop_token token)
    {
        auto first = name.find_first_not_of(" \t\r\n"), last = name.find_last_not_of(" \t\r\n");
        if (first == std::string::npos)
            throw std::invalid_argument("文件名无效");
        name = name.substr(first, last - first + 1);
        auto filename = Path(name);
        if (_wcsicmp(filename.extension().c_str(), L".png") != 0)
            filename += L".png";
        if (Utf8(filename) == ".png")
            throw std::invalid_argument("文件名无效");
        auto destination = Path(card.Folder()) / filename;
        if (SamePath(destination, Path(card.Path)))
            return;
        auto validated = UniqueDestination(Path(card.Folder()), filename);
        if (!SamePath(destination, validated))
            throw std::runtime_error("目标文件已存在，请换一个名称");
        MoveCore(card, destination, "Rename", token);
    }

    void CardFileService::Quarantine(const CardRecord &card, const fs::path &game, std::stop_token token)
    {
        if (card.Availability == FileAvailability::Quarantined)
            throw std::runtime_error("该卡片已在隔离区");
        if (Path(card.Id) != Path(card.Id).filename())
            throw std::runtime_error("卡片标识无效");
        auto folder = game / L".HS2ManagerTrash" / Path(card.Id);
        MoveCore(card, UniqueDestination(folder, Path(card.FileName())), "Quarantine", token);
        auto saved = repository_.FindById(card.Id).value();
        WriteJson(folder / L"restore.json", Json{{"CardId", card.Id},
                                                 {"OriginalPath", card.Path},
                                                 {"Path", saved.Path},
                                                 {"Hash", saved.Hash},
                                                 {"QuarantinedUtcTicks", saved.QuarantinedUtcTicks}});
    }

    void CardFileService::Restore(const CardRecord &card, std::stop_token token)
    {
        if (card.Availability != FileAvailability::Quarantined || card.OriginalPath.empty())
            throw std::runtime_error("卡片没有可用的恢复路径");
        auto original = Path(card.OriginalPath);
        MoveCore(card, UniqueDestination(original.parent_path(), original.filename()), "Restore", token);
    }

    std::vector<FileOperationRecord> CardFileService::Operations() const
    {
        auto folder = repository_.DataPath() / L"Operations";
        if (!fs::exists(folder))
            return {};
        std::vector<FileOperationRecord> records;
        for (auto &entry : fs::directory_iterator(folder))
            if (entry.is_regular_file() && entry.path().extension() == L".json")
            {
                try
                {
                    records.push_back(Json::parse(ReadFile(entry.path(), 16 * 1024 * 1024)).get<FileOperationRecord>());
                }
                catch (const Json::exception &)
                {
                }
            }
        std::sort(records.begin(), records.end(),
                  [](auto &a, auto &b)
                  {
                      return a.CreatedUtc > b.CreatedUtc;
                  });
        return records;
    }

    void CardFileService::CleanupDeleted(const FileOperationRecord &op)
    {
        if (!op.Card)
            return;
        auto &card = *op.Card;
        if (Path(card.Id) == Path(card.Id).filename() && !card.CoverFile.empty())
        {
            auto owned = repository_.DataPath() / L"Covers" / Path(card.Id + ".png");
            auto cover = repository_.DataPath() / Path(card.CoverFile);
            auto cards = repository_.All();
            if (SamePath(cover, owned) &&
                std::none_of(cards.begin(), cards.end(),
                             [&](auto &other)
                             {
                                 return !other.CoverFile.empty() &&
                                        SamePath(repository_.DataPath() / Path(other.CoverFile), cover);
                             }))
            {
                std::error_code error;
                fs::remove(cover, error);
                if (error && error != std::errc::no_such_file_or_directory)
                    throw fs::filesystem_error("清理封面", cover, error);
            }
        }
        if (!OwnedTrash(card, Path(op.Source)))
            return;
        auto folder = Path(op.Source).parent_path(), restore = folder / L"restore.json";
        if (fs::exists(restore))
        {
            auto data = Json::parse(ReadFile(restore, 1024 * 1024));
            if (data.value("CardId", "") == card.Id && SamePath(Path(data.value("Path", "")), Path(op.Source)))
                fs::remove(restore);
        }
        if (fs::exists(folder) && fs::is_empty(folder))
            fs::remove(folder);
    }

    std::vector<FileOperationResult> CardFileService::Delete(const std::vector<CardDeleteRequest> &requests,
                                                             Progress progress, std::stop_token token)
    {
        std::vector<FileOperationResult> result;
        std::set<std::string> processed;
        for (auto &request : requests)
        {
            if (token.stop_requested())
                break;
            if (!processed.insert(request.Id).second)
                continue;
            if (progress)
                progress({static_cast<int>(result.size()), static_cast<int>(requests.size()),
                          "永久删除 " + Utf8(Path(request.Path).filename())});
            if (token.stop_requested())
                break;
            std::optional<FileOperationRecord> op;
            bool deleted = false, removed = false;
            try
            {
                auto card = repository_.FindById(request.Id);
                if (!card || !Matches(*card, request) || _wcsicmp(Path(request.Path).extension().c_str(), L".png") != 0)
                    throw std::runtime_error("人物卡记录或路径已变化，请刷新后重新选择");
                bool present = Present(Path(card->Path));
                op.emplace();
                op->Kind = "Delete";
                op->Source = card->Path;
                op->Card = card;
                op->ExpectedSize = request.Size;
                op->ExpectedModifiedUtcTicks = request.ModifiedUtcTicks;
                Save(*op);
                CheckCancelled(token);
                if (present)
                    DeleteVerified(request, token);
                deleted = true;
                op->Phase = "FileDeleted";
                Save(*op);
                if (DeletedPresent(*op))
                    throw std::runtime_error("同路径文件仍存在或已重新创建，已保留");
                ValidateDeletion(repository_, *op);
                repository_.Remove(card->Id);
                removed = true;
                CleanupDeleted(*op);
                op->Phase = "Indexed";
                op->Error.clear();
                Save(*op);
                result.push_back({request.Path, {}, true, present ? "已永久删除" : "文件已缺失，已清理记录"});
            }
            catch (const Cancelled &)
            {
                if (op)
                {
                    op->Phase = "RolledBack";
                    op->Error = "删除前已取消";
                    try
                    {
                        Save(*op);
                    }
                    catch (...)
                    {
                    }
                }
                break;
            }
            catch (const std::exception &e)
            {
                std::string message = deleted ? std::string("源文件已永久删除；") +
                                                    (removed ? "缓存或日志清理待恢复：" : "记录清理待恢复：") + e.what()
                                              : e.what();
                if (op)
                {
                    op->Phase = deleted ? "FileDeleted" : "NeedsAttention";
                    op->Error = message;
                    try
                    {
                        Save(*op);
                    }
                    catch (const std::exception &logError)
                    {
                        message += "；操作日志更新失败：";
                        message += logError.what();
                    }
                }
                result.push_back({request.Path, {}, removed, message});
            }
        }
        return result;
    }

    std::vector<std::string> CardFileService::Recover(std::stop_token token)
    {
        std::vector<std::string> messages;
        auto operations = Operations();
        for (auto &op : operations)
        {
            CheckCancelled(token);
            if (op.Phase == "Indexed" || op.Phase == "RolledBack" || op.Phase == "Superseded")
                continue;
            try
            {
                if (op.Kind == "Delete")
                {
                    if (DeletedPresent(op))
                    {
                        if (op.Phase != "FileDeleted")
                        {
                            op.Phase = "RolledBack";
                            op.Error = "文件仍存在，未自动重试永久删除";
                            Save(op);
                        }
                        messages.push_back("文件仍存在，未自动重试永久删除：" + op.Source);
                        continue;
                    }
                    ValidateDeletion(repository_, op);
                    op.Phase = "FileDeleted";
                    Save(op);
                    repository_.Remove(op.Card->Id);
                    CleanupDeleted(op);
                    op.Phase = "Indexed";
                    op.Error.clear();
                    Save(op);
                    messages.push_back("已完成永久删除后的清理：" + op.Source);
                    continue;
                }
                if (op.Kind.starts_with("Edit"))
                {
                    bool superseded = std::any_of(operations.begin(), operations.end(),
                                                  [&](auto &newer)
                                                  {
                                                      return newer.Card && op.Card && newer.Card->Id == op.Card->Id &&
                                                             newer.CreatedUtc > op.CreatedUtc &&
                                                             newer.Phase == "Indexed" &&
                                                             (newer.Kind == "Delete" ||
                                                              newer.Kind.starts_with("Edit") || newer.Kind == "Move" ||
                                                              newer.Kind == "Rename" || newer.Kind == "Quarantine");
                                                  });
                    if (superseded)
                    {
                        op.Phase = "Superseded";
                        Save(op);
                        continue;
                    }
                    if (op.Phase == "Conflict")
                    {
                        messages.push_back("编辑提交发生冲突，请保留并检查：" + op.SwapBackup);
                        continue;
                    }
                    if (!op.Hash.empty() && fs::exists(Path(op.Destination)) &&
                        HashFile(Path(op.Destination), token) == op.Hash)
                    {
                        if (!op.SwapBackup.empty() && fs::exists(Path(op.SwapBackup)) &&
                            HashFile(Path(op.SwapBackup), token) != op.ExpectedSourceHash)
                        {
                            op.Phase = "Conflict";
                            Save(op);
                            messages.push_back("编辑替换发生冲突，已保留原文件副本：" + op.SwapBackup);
                            continue;
                        }
                        if (!op.Card)
                            throw std::runtime_error("编辑记录缺少人物卡信息");
                        auto parsed = CardParser().Parse(Path(op.Destination));
                        auto card = *op.Card;
                        card.Name = parsed.Name;
                        card.Sex = parsed.Sex;
                        card.Size = parsed.Size;
                        card.ModifiedUtcTicks = parsed.ModifiedUtcTicks;
                        card.PngLength = parsed.PngLength;
                        card.Status = parsed.Status;
                        card.Dependencies = parsed.Dependencies;
                        card.PluginDataIds = parsed.PluginDataIds;
                        card.DependenciesComplete = parsed.DependenciesComplete;
                        card.Hash = op.Hash;
                        CardParser::CopyCover(Path(card.Path), card.PngLength,
                                              repository_.DataPath() / Path(card.CoverFile));
                        repository_.Save(card);
                        op.Phase = "Indexed";
                        Save(op);
                        RemoveOwnedTemporary(op, op.Staging);
                        RemoveOwnedTemporary(op, op.SwapBackup);
                        messages.push_back("已补齐编辑保存记录：" + op.Destination);
                    }
                    else if (fs::exists(Path(op.Source)) && HashFile(Path(op.Source), token) == op.ExpectedSourceHash)
                    {
                        op.Phase = "RolledBack";
                        Save(op);
                        RemoveOwnedTemporary(op, op.Staging);
                        RemoveOwnedTemporary(op, op.SwapBackup);
                        messages.push_back("未提交的编辑已取消：" + op.Source);
                    }
                    else
                        messages.push_back("编辑保存待检查；不会自动覆盖任何文件：" + op.Destination);
                    continue;
                }
                auto deletion = std::find_if(operations.begin(), operations.end(),
                                             [&](auto &d)
                                             {
                                                 return d.Kind == "Delete" && d.Card && op.Card &&
                                                        d.Card->Id == op.Card->Id && d.CreatedUtc >= op.CreatedUtc &&
                                                        d.Phase != "RolledBack";
                                             });
                if (op.Kind != "Export" && deletion != operations.end())
                {
                    if (deletion->Phase == "FileDeleted" || deletion->Phase == "Indexed")
                    {
                        op.Phase = "Superseded";
                        Save(op);
                    }
                    messages.push_back("历史操作由永久删除接管：" + op.Source);
                    continue;
                }
                if (fs::exists(Path(op.Destination)) && !op.Hash.empty() &&
                    HashFile(Path(op.Destination), token) == op.Hash)
                {
                    if (op.Kind == "Import" || op.Kind == "Export" ||
                        (!fs::exists(Path(op.Source)) && fs::exists(Path(op.Source).parent_path())))
                    {
                        IndexOperation(op);
                        op.Phase = "Indexed";
                        Save(op);
                        messages.push_back("已恢复：" + op.Destination);
                    }
                    else
                        messages.push_back("需检查源文件和副本：" + op.Source);
                }
                else if (fs::exists(Path(op.Source)) && !fs::exists(Path(op.Destination)) &&
                         fs::exists(Path(op.Destination).parent_path()))
                {
                    RemoveOwnedTemporary(op, op.Staging);
                    op.Phase = "RolledBack";
                    Save(op);
                    messages.push_back("未完成操作已回滚：" + op.Source);
                }
                else
                    messages.push_back("操作待恢复（文件或目录不可访问）：" + op.Source);
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &e)
            {
                messages.push_back("恢复失败：" + std::string(e.what()));
            }
        }
        return messages;
    }
}
