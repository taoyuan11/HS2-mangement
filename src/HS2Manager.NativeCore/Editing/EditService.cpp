#include "Common/Core.h"
#include <windows.h>
#include <algorithm>
#include <system_error>

namespace hs2
{
    namespace
    {
        void WinCheck(BOOL ok, const char *message)
        {
            if (!ok)
                throw std::system_error(GetLastError(), std::system_category(), message);
        }

        void WriteNew(const fs::path &path, std::span<const std::uint8_t> bytes, std::stop_token token)
        {
            HANDLE handle =
                CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
            WinCheck(handle != INVALID_HANDLE_VALUE, "创建编辑临时文件");
            try
            {
                while (!bytes.empty())
                {
                    CheckCancelled(token);
                    DWORD count = 0;
                    WinCheck(WriteFile(handle, bytes.data(),
                                       static_cast<DWORD>((std::min)(bytes.size(), std::size_t(131072))), &count,
                                       nullptr),
                             "写入编辑文件");
                    if (!count)
                        throw std::runtime_error("编辑文件写入截断");
                    bytes = bytes.subspan(count);
                }
                WinCheck(FlushFileBuffers(handle), "刷新编辑文件");
                CloseHandle(handle);
            }
            catch (...)
            {
                CloseHandle(handle);
                throw;
            }
        }

        void Cleanup(const FileOperationRecord &op)
        {
            for (auto &text : {op.Staging, op.SwapBackup})
            {
                if (text.empty())
                    continue;
                auto path = Path(text);
                if (!SamePath(path.parent_path(), Path(op.Destination).parent_path()))
                    continue;
                auto name = Utf8(path.filename());
                if (name != ".hs2manager-" + op.Id + ".tmp" && name != ".hs2manager-" + op.Id + ".previous")
                    continue;
                std::error_code error;
                fs::remove(path, error);
            }
        }
    }

    CardEditSaveResult CardEditService::Save(const CharacterDocument &document, const CardRecord &source,
                                             bool overwrite, std::optional<fs::path> destination, std::stop_token token)
    {
        if (!SamePath(Path(source.Path), document.SourcePath()))
            throw std::invalid_argument("当前卡片和编辑草稿不匹配");
        auto target = overwrite ? Path(source.Path)
                                : destination.value_or(CardFileService::UniqueDestination(
                                      Path(source.Folder()), Path(Utf8(Path(source.FileName()).stem()) + "-编辑.png")));
        if (!overwrite && SamePath(target, document.SourcePath()))
            throw std::invalid_argument("另存目标不能是原卡；请使用覆盖原卡操作");
        return SaveCore(document, source, target, overwrite ? "EditOverwrite" : "EditSaveAs", document.SourceHash(),
                        token);
    }

    CardEditSaveResult CardEditService::RestoreBackup(const FileOperationRecord &operation, std::stop_token token)
    {
        if (!operation.Kind.starts_with("Edit") || operation.BackupPath.empty() || !operation.Card ||
            !SamePath(Path(operation.BackupPath),
                      repository_.DataPath() / L"CardBackups" / Path(operation.Card->Id) / Path(operation.Id + ".png")))
            throw std::invalid_argument("此操作没有可恢复的编辑备份");
        auto current = repository_.FindByPath(Path(operation.Destination));
        if (!current)
            throw std::runtime_error("目标人物卡不在资源库中");
        if (current->Availability != FileAvailability::Available)
            throw std::runtime_error("请先恢复人物卡的位置和可访问状态");
        auto backup = CharacterDocument::Open(Path(operation.BackupPath), token);
        if (backup.SourceHash() != operation.ExpectedSourceHash)
            throw std::runtime_error("备份文件校验失败");
        return SaveCore(backup, *current, Path(current->Path), "EditRestore", HashFile(Path(current->Path), token),
                        token);
    }

    CardEditSaveResult CardEditService::SaveNew(const CharacterDocument &document, const fs::path &destination,
                                                std::stop_token token)
    {
        CardRecord source;
        source.Path = Utf8(document.SourcePath());
        source.Root = Utf8(fs::absolute(destination).parent_path());
        source.Sex = document.Sex();
        // 新建卡没有需要覆盖的磁盘源文件，沿用另存的提交和索引恢复事务。
        return SaveCore(document, source, destination, "EditSaveAs", document.SourceHash(), token);
    }

    CardEditSaveResult CardEditService::SaveCore(const CharacterDocument &document, const CardRecord &source,
                                                 const fs::path &destination, std::string kind,
                                                 const std::string &expectedHash, std::stop_token token)
    {
        std::lock_guard guard(gate_);
        CheckCancelled(token);
        auto target = fs::absolute(destination);
        if (_wcsicmp(target.extension().c_str(), L".png") != 0)
            throw std::invalid_argument("人物卡必须保存为 PNG 文件");
        bool overwrite = kind != "EditSaveAs";
        if (!overwrite && fs::exists(target))
            throw std::runtime_error("另存目标已存在，请选择新的文件名");
        if (overwrite && source.Availability != FileAvailability::Available)
            throw std::runtime_error("当前人物卡不可覆盖保存，请另存草稿");
        auto metadata = repository_.FindById(source.Id).value_or(source);
        if (!overwrite)
            metadata.Id = NewId();
        metadata.Path = Utf8(target);
        metadata.Availability = FileAvailability::Available;
        metadata.OriginalPath.clear();
        metadata.QuarantinedUtcTicks = 0;
        if (!Within(target, Path(metadata.Root)))
            metadata.Root = Utf8(target.parent_path());
        metadata.CoverFile = Utf8(fs::path(L"Covers") / Path(metadata.Id + ".png"));
        FileOperationRecord op;
        op.Kind = std::move(kind);
        op.Source = source.Path;
        op.Destination = Utf8(target);
        op.ExpectedSourceHash = expectedHash;
        op.Card = metadata;
        op.Staging = Utf8(target.parent_path() / Path(".hs2manager-" + op.Id + ".tmp"));
        if (overwrite)
        {
            op.BackupPath = Utf8(repository_.DataPath() / L"CardBackups" / Path(source.Id) / Path(op.Id + ".png"));
            op.SwapBackup = Utf8(target.parent_path() / Path(".hs2manager-" + op.Id + ".previous"));
        }
        auto save = [&]
        {
            WriteJson(repository_.DataPath() / L"Operations" / Path(op.Id + ".json"), op);
        };
        save();
        bool committed = false;
        try
        {
            fs::create_directories(target.parent_path());
            if (overwrite && HashFile(Path(source.Path), token) != expectedHash)
                throw std::runtime_error("原卡已被外部修改，当前草稿已保留");
            auto encoded = document.ToBytes();
            WriteNew(Path(op.Staging), encoded, token);
            auto written = CharacterDocument::Open(Path(op.Staging), token);
            if (!std::equal(document.Cover().begin(), document.Cover().end(), written.Cover().begin(),
                            written.Cover().end()))
                throw std::runtime_error("保存后的封面与原卡不一致");
            for (auto &section : document.SectionNames())
                if (!document.Node(section).ContentEquals(written.Node(section)))
                    throw std::runtime_error("保存后的外观参数校验失败");
            if (document.Extensions() &&
                (!written.Extensions() || !document.Extensions()->ContentEquals(*written.Extensions())))
                throw std::runtime_error("保存后的扩展数据校验失败");
            op.Hash = written.SourceHash();
            if (overwrite)
            {
                fs::create_directories(Path(op.BackupPath).parent_path());
                auto original = ReadFile(Path(source.Path), 512ull * 1024 * 1024, token);
                if (Sha256(original) != expectedHash)
                    throw std::runtime_error("原卡在保存期间发生变化，未覆盖");
                WriteNew(Path(op.BackupPath), original, token);
                if (HashFile(Path(op.BackupPath), token) != expectedHash)
                    throw std::runtime_error("原卡备份校验失败，未覆盖原卡");
            }
            op.Phase = "Prepared";
            save();
            CheckCancelled(token);
            if (overwrite)
            {
                if (HashFile(Path(source.Path), token) != expectedHash)
                    throw std::runtime_error("原卡在保存期间发生变化，未覆盖");
                WinCheck(ReplaceFileW(target.c_str(), Path(op.Staging).c_str(), Path(op.SwapBackup).c_str(),
                                      REPLACEFILE_IGNORE_MERGE_ERRORS, nullptr, nullptr),
                         "替换人物卡");
                committed = true;
                if (HashFile(Path(op.SwapBackup)) != expectedHash)
                {
                    op.Phase = "Conflict";
                    save();
                    if (HashFile(target) == op.Hash)
                    {
                        WinCheck(ReplaceFileW(target.c_str(), Path(op.SwapBackup).c_str(), Path(op.Staging).c_str(),
                                              REPLACEFILE_IGNORE_MERGE_ERRORS, nullptr, nullptr),
                                 "恢复并发替换的原卡");
                        committed = false;
                    }
                    throw std::runtime_error("原卡在提交时被外部替换；外部文件已保留，请检查操作日志");
                }
            }
            else
            {
                WinCheck(MoveFileExW(Path(op.Staging).c_str(), target.c_str(), MOVEFILE_WRITE_THROUGH), "提交新人物卡");
                committed = true;
            }
            op.Phase = "Committed";
            save();
            auto parsed = CardParser().Parse(target);
            if (parsed.Status != CardStatus::Valid)
                throw std::runtime_error("人物卡已写入，索引更新失败");
            parsed.Id = metadata.Id;
            parsed.Favorite = metadata.Favorite;
            parsed.Tags = metadata.Tags;
            parsed.Collection = metadata.Collection;
            parsed.Notes = metadata.Notes;
            parsed.Root = metadata.Root;
            parsed.Hash = op.Hash;
            parsed.CoverFile = metadata.CoverFile;
            CardParser::CopyCover(target, parsed.PngLength, repository_.DataPath() / Path(parsed.CoverFile));
            auto stale = repository_.FindByPath(target);
            if (stale && stale->Id != parsed.Id)
                throw std::runtime_error("保存位置已被其他资源库记录使用，请检查操作日志");
            repository_.Save(parsed);
            op.Card = parsed;
            op.Phase = "Indexed";
            save();
            Cleanup(op);
            return {parsed, CharacterDocument::Open(target), op.Id, op.BackupPath};
        }
        catch (const std::exception &e)
        {
            op.Error = e.what();
            if (op.Phase != "Conflict")
                op.Phase = committed ? "Committed" : "RolledBack";
            try
            {
                save();
            }
            catch (...)
            {
            }
            if (!committed && op.Phase != "Conflict")
                Cleanup(op);
            throw;
        }
    }
}
