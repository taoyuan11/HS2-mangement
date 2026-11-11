#include "Common/Core.h"
#include <windows.h>
#include <winsqlite/winsqlite3.h>
#include <algorithm>
#include <map>

namespace hs2
{
    namespace
    {
        void CheckSql(int code, sqlite3 *db)
        {
            if (code != SQLITE_OK && code != SQLITE_DONE && code != SQLITE_ROW)
                throw DatabaseError(db ? sqlite3_errmsg(db) : "open failed", db ? sqlite3_extended_errcode(db) : code,
                                    db ? sqlite3_system_errno(db) : 0);
        }

        struct Statement
        {
            sqlite3 *db;
            sqlite3_stmt *handle{};

            Statement(sqlite3 *database, const char *sql) : db(database)
            {
                CheckSql(sqlite3_prepare_v2(db, sql, -1, &handle, nullptr), db);
            }

            ~Statement()
            {
                sqlite3_finalize(handle);
            }

            void Bind(int index, const std::string &text)
            {
                CheckSql(sqlite3_bind_text(handle, index, text.data(), static_cast<int>(text.size()), SQLITE_TRANSIENT),
                         db);
            }

            bool Next()
            {
                int code = sqlite3_step(handle);
                CheckSql(code, db);
                return code == SQLITE_ROW;
            }

            std::string Text(int index)
            {
                auto bytes = sqlite3_column_text(handle, index);
                return bytes ? reinterpret_cast<const char *>(bytes) : "";
            }
        };

        bool Trash(const fs::path &path)
        {
            for (auto &part : path)
                if (_wcsicmp(part.c_str(), L".HS2ManagerTrash") == 0)
                    return true;
            return false;
        }
    }

    struct CardRepository::Impl
    {
        fs::path Path;
        sqlite3 *Database{};
        mutable std::recursive_mutex Gate;

        Impl(fs::path path, bool exclusiveAccess) : Path(fs::absolute(path))
        {
            auto full = Path.wstring();
            bool remote = full.starts_with(L"\\\\?\\UNC\\");
            if (full.starts_with(L"\\\\?\\"))
                full = full.substr(4);
            remote = remote || full.starts_with(L"\\\\") ||
                     GetDriveTypeW(fs::path(full).root_path().c_str()) == DRIVE_REMOTE;
            if (remote)
                throw std::invalid_argument("资料库必须位于本机磁盘，请使用本机 --data 目录");
            fs::create_directories(Path);
            auto file = Utf8(Path / L"library.db");
            int code = sqlite3_open_v2(file.c_str(), &Database,
                                       SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX, nullptr);
            if (code != SQLITE_OK)
            {
                auto message = Database ? sqlite3_errmsg(Database) : "SQLite open failed";
                std::string error(message);
                if (Database)
                    sqlite3_close(Database);
                Database = nullptr;
                throw std::runtime_error(error);
            }
            sqlite3_busy_timeout(Database, 10000);
            try
            {
                // 单实例恢复模式在访问 WAL 前启用独占锁，使用进程内 WAL 索引，
                // 避免再次截断被 Windows 错误报告快照映射的 SHM 文件。
                if (exclusiveAccess)
                    CheckSql(sqlite3_exec(Database, "PRAGMA locking_mode=EXCLUSIVE;", nullptr, nullptr, nullptr),
                             Database);
                CheckSql(
                    sqlite3_exec(Database,
                                 "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS Cards(Id TEXT PRIMARY KEY, Path "
                                 "TEXT NOT NULL COLLATE NOCASE UNIQUE, Json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS "
                                 "Metadata(Key TEXT PRIMARY KEY, Value TEXT NOT NULL); PRAGMA user_version=1;",
                                 nullptr, nullptr, nullptr),
                    Database);
            }
            catch (...)
            {
                sqlite3_close(Database);
                Database = nullptr;
                throw;
            }
        }

        ~Impl()
        {
            if (Database)
                sqlite3_close(Database);
        }
    };

    CardRepository::CardRepository(fs::path path, bool exclusiveAccess)
        : impl_(std::make_unique<Impl>(std::move(path), exclusiveAccess))
    {
    }

    CardRepository::~CardRepository() = default;

    const fs::path &CardRepository::DataPath() const
    {
        return impl_->Path;
    }

    std::vector<CardRecord> CardRepository::All() const
    {
        std::lock_guard guard(impl_->Gate);
        Statement statement(impl_->Database, "SELECT Json FROM Cards");
        std::vector<CardRecord> cards;
        while (statement.Next())
            cards.push_back(Json::parse(statement.Text(0)).get<CardRecord>());
        return cards;
    }

    std::optional<CardRecord> CardRepository::FindById(const std::string &id) const
    {
        std::lock_guard guard(impl_->Gate);
        Statement statement(impl_->Database, "SELECT Json FROM Cards WHERE Id=?1");
        statement.Bind(1, id);
        return statement.Next() ? std::optional(Json::parse(statement.Text(0)).get<CardRecord>()) : std::nullopt;
    }

    std::optional<CardRecord> CardRepository::FindByPath(const fs::path &path) const
    {
        std::lock_guard guard(impl_->Gate);
        Statement statement(impl_->Database, "SELECT Json FROM Cards WHERE Path=?1");
        statement.Bind(1, Utf8(fs::absolute(path)));
        return statement.Next() ? std::optional(Json::parse(statement.Text(0)).get<CardRecord>()) : std::nullopt;
    }

    void CardRepository::Save(const CardRecord &card)
    {
        std::lock_guard guard(impl_->Gate);
        Statement statement(impl_->Database, "INSERT INTO Cards(Id,Path,Json) VALUES(?1,?2,?3) ON CONFLICT(Id) DO "
                                             "UPDATE SET Path=excluded.Path,Json=excluded.Json");
        statement.Bind(1, card.Id);
        statement.Bind(2, card.Path);
        statement.Bind(3, Json(card).dump());
        statement.Next();
    }

    CardRecord CardRepository::SaveScan(CardRecord card)
    {
        std::lock_guard guard(impl_->Gate);
        if (auto old = FindByPath(Path(card.Path)))
        {
            card.Id = old->Id;
            card.Favorite = old->Favorite;
            card.Tags = old->Tags;
            card.Collection = old->Collection;
            card.Notes = old->Notes;
            if (card.Size == old->Size && card.ModifiedUtcTicks == old->ModifiedUtcTicks)
                card.Hash = old->Hash;
        }
        Save(card);
        return card;
    }

    void CardRepository::UpdateMetadata(const std::vector<std::string> &ids,
                                        const std::function<void(CardRecord &)> &update)
    {
        std::lock_guard guard(impl_->Gate);
        for (auto &id : ids)
            if (auto card = FindById(id))
            {
                update(*card);
                Save(*card);
            }
    }

    void CardRepository::Remove(const std::string &id)
    {
        std::lock_guard guard(impl_->Gate);
        Statement statement(impl_->Database, "DELETE FROM Cards WHERE Id=?1");
        statement.Bind(1, id);
        statement.Next();
    }

    void CardRepository::StoreMetadata(const std::string &key, const Json &value)
    {
        std::lock_guard guard(impl_->Gate);
        Statement statement(
            impl_->Database,
            "INSERT INTO Metadata(Key,Value) VALUES(?1,?2) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value");
        statement.Bind(1, key);
        statement.Bind(2, value.dump());
        statement.Next();
    }

    std::optional<Json> CardRepository::LoadMetadata(const std::string &key) const
    {
        std::lock_guard guard(impl_->Gate);
        Statement statement(impl_->Database, "SELECT Value FROM Metadata WHERE Key=?1");
        statement.Bind(1, key);
        return statement.Next() ? std::optional(Json::parse(statement.Text(0))) : std::nullopt;
    }

    void CardRepository::Backup(const fs::path &target) const
    {
        std::lock_guard guard(impl_->Gate);
        if (SamePath(target, impl_->Path / L"library.db"))
            throw std::invalid_argument("备份不能覆盖源数据库");
        sqlite3 *destination{};
        CheckSql(sqlite3_open(Utf8(target).c_str(), &destination), destination);
        try
        {
            auto backup = sqlite3_backup_init(destination, "main", impl_->Database, "main");
            if (!backup)
                throw std::runtime_error(sqlite3_errmsg(destination));
            int code = sqlite3_backup_step(backup, -1);
            int finish = sqlite3_backup_finish(backup);
            CheckSql(code, destination);
            CheckSql(finish, destination);
            sqlite3_close(destination);
        }
        catch (...)
        {
            sqlite3_close(destination);
            throw;
        }
    }

    std::vector<fs::path> CardLibraryService::EnumerateCards(const fs::path &root, bool recursive,
                                                             std::stop_token token)
    {
        std::vector<fs::path> result, pending{fs::absolute(root)};
        while (!pending.empty())
        {
            CheckCancelled(token);
            auto folder = pending.back();
            pending.pop_back();
            if (Trash(folder))
                continue;
            for (auto &item : fs::directory_iterator(folder))
            {
                CheckCancelled(token);
                auto attributes = GetFileAttributesW(item.path().c_str());
                if (attributes == INVALID_FILE_ATTRIBUTES)
                    throw std::system_error(GetLastError(), std::system_category(), "读取目录条目");
                if (attributes & FILE_ATTRIBUTE_DIRECTORY)
                {
                    if (recursive && !(attributes & FILE_ATTRIBUTE_REPARSE_POINT))
                        pending.push_back(item.path());
                }
                else if (_wcsicmp(item.path().extension().c_str(), L".png") == 0)
                    result.push_back(item.path());
            }
        }
        return result;
    }

    ScanReport CardLibraryService::Scan(const std::vector<fs::path> &roots, Progress progress, std::stop_token token,
                                        bool recursive)
    {
        std::lock_guard gate(scanGate_);
        ScanReport report;
        std::vector<fs::path> handled, scanned;
        CardParser parser;
        for (auto &raw : roots)
        {
            CheckCancelled(token);
            auto root = fs::absolute(raw).lexically_normal();
            if (std::any_of(scanned.begin(), scanned.end(),
                            [&](auto &p)
                            {
                                return SamePath(p, root);
                            }))
                continue;
            scanned.push_back(root);
            auto cards = repository_.All();
            std::vector<CardRecord> previous;
            for (auto &card : cards)
                if (!card.Root.empty() && SamePath(Path(card.Root), root) &&
                    card.Availability != FileAvailability::Quarantined &&
                    (recursive || SamePath(Path(card.Folder()), root)))
                    previous.push_back(card);
            std::vector<fs::path> files;
            try
            {
                if (progress)
                    progress({report.Candidates, 0, "正在读取目录：" + Utf8(root)});
                files = EnumerateCards(root, recursive, token);
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &e)
            {
                report.Warnings.push_back(Utf8(root) + "：" + e.what());
                for (auto &old : previous)
                {
                    old.Availability = FileAvailability::Offline;
                    repository_.Save(old);
                }
                continue;
            }
            for (std::size_t i = 0; i < files.size(); i++)
            {
                CheckCancelled(token);
                auto path = files[i];
                if (std::any_of(handled.begin(), handled.end(),
                                [&](auto &p)
                                {
                                    return SamePath(p, path);
                                }))
                    continue;
                handled.push_back(path);
                report.Candidates++;
                if (progress)
                    progress(
                        {static_cast<int>(i + 1), static_cast<int>(files.size()), "正在索引 " + Utf8(path.filename())});
                auto old = repository_.FindByPath(path);
                try
                {
                    if (old && static_cast<std::uint64_t>(old->Size) == fs::file_size(path) &&
                        old->ModifiedUtcTicks == ModifiedTicks(path) && old->Status != CardStatus::Unreadable &&
                        !old->CoverFile.empty() && fs::exists(repository_.DataPath() / Path(old->CoverFile)))
                    {
                        old->Availability = FileAvailability::Available;
                        old->Root = Utf8(root);
                        repository_.Save(*old);
                        report.Unchanged++;
                        if (old->Status == CardStatus::Valid)
                            report.Valid++;
                        else
                            report.Invalid++;
                        continue;
                    }
                    auto parsed = parser.Parse(path, token);
                    parsed.Root = Utf8(root);
                    if (old)
                        parsed.Id = old->Id;
                    if (parsed.PngLength > 0)
                    {
                        parsed.CoverFile = Utf8(fs::path(L"Covers") / Path(parsed.Id + ".png"));
                        CardParser::CopyCover(path, parsed.PngLength, repository_.DataPath() / Path(parsed.CoverFile),
                                              token);
                    }
                    repository_.SaveScan(parsed);
                    if (parsed.Status == CardStatus::Valid)
                        report.Valid++;
                    else
                        report.Invalid++;
                }
                catch (const Cancelled &)
                {
                    throw;
                }
                catch (const std::exception &e)
                {
                    report.Invalid++;
                    report.Warnings.push_back(Utf8(path) + "：" + e.what());
                    if (old)
                    {
                        old->Availability = FileAvailability::Offline;
                        old->Error = e.what();
                        repository_.Save(*old);
                    }
                }
            }
            if (fs::exists(root))
                for (auto &old : previous)
                    if (std::none_of(files.begin(), files.end(),
                                     [&](auto &path)
                                     {
                                         return SamePath(path, Path(old.Path));
                                     }))
                    {
                        old.Availability = FileAvailability::Missing;
                        repository_.Save(old);
                    }
        }
        return report;
    }

    void CardLibraryService::ComputeHashes(Progress progress, std::stop_token token)
    {
        auto cards = repository_.All();
        std::map<std::int64_t, int> sizes;
        for (auto &card : cards)
            if (card.Availability == FileAvailability::Available)
                sizes[card.Size]++;
        std::vector<CardRecord> candidates;
        for (auto &card : cards)
            if (card.Availability == FileAvailability::Available && sizes[card.Size] > 1)
                candidates.push_back(card);
        for (std::size_t i = 0; i < candidates.size(); i++)
        {
            CheckCancelled(token);
            auto &card = candidates[i];
            if (progress)
                progress(
                    {static_cast<int>(i + 1), static_cast<int>(candidates.size()), "校验完整文件：" + card.FileName()});
            auto path = Path(card.Path);
            auto size = fs::file_size(path), ticks = static_cast<std::uint64_t>(ModifiedTicks(path));
            auto hash = HashFile(path, token);
            if (size != fs::file_size(path) || ticks != static_cast<std::uint64_t>(ModifiedTicks(path)))
                continue;
            repository_.UpdateMetadata({card.Id},
                                       [&](CardRecord &current)
                                       {
                                           if (static_cast<std::uint64_t>(current.Size) == size &&
                                               static_cast<std::uint64_t>(current.ModifiedUtcTicks) == ticks)
                                               current.Hash = hash;
                                       });
        }
    }
}
