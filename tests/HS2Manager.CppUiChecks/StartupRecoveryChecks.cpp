#include "Infrastructure/DataLocation.h"
#include <windows.h>
#include <winsqlite/winsqlite3.h>
#include <iostream>

using namespace hs2;
using namespace hs2::ui;

namespace
{
    struct Database
    {
        sqlite3 *Value{};

        ~Database()
        {
            if (Value)
                sqlite3_close(Value);
        }
    };

    struct Mapping
    {
        HANDLE File = INVALID_HANDLE_VALUE, Section{};
        void *View{};

        explicit Mapping(fs::path const &path)
        {
            File = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                               OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (File != INVALID_HANDLE_VALUE)
                Section = CreateFileMappingW(File, nullptr, PAGE_READWRITE, 0, 0, nullptr);
            if (Section)
                View = MapViewOfFile(Section, FILE_MAP_WRITE, 0, 0, 0);
        }

        ~Mapping()
        {
            if (View)
                UnmapViewOfFile(View);
            if (Section)
                CloseHandle(Section);
            if (File != INVALID_HANDLE_VALUE)
                CloseHandle(File);
        }
    };

    void Check(bool condition, char const *message)
    {
        if (!condition)
            throw std::runtime_error(message);
    }

    void Recover(fs::path const &root)
    {
        auto data = root / L"Data";
        Database connection;
        CardRecord card;
        card.Path = Utf8(root / L"card.png");
        card.Name = "崩溃前尚未 checkpoint 的人物卡";
        card.Favorite = true;
        card.Notes = "保留收藏、标签及备注";
        card.Tags = {"回归检查"};
        card.Collection = "自定义分类";
        {
            CardRepository repository(data);
            Check(sqlite3_open(Utf8(data / L"library.db").c_str(), &connection.Value) == SQLITE_OK, "open WAL keeper");
            Check(sqlite3_db_config(connection.Value, SQLITE_DBCONFIG_NO_CKPT_ON_CLOSE, 1, nullptr) == SQLITE_OK,
                  "preserve committed WAL after close");
            repository.Save(card);
            repository.StoreMetadata("custom", Json{{"value", 42}});
            Check(sqlite3_exec(connection.Value, "SELECT count(*) FROM Cards;", nullptr, nullptr, nullptr) == SQLITE_OK,
                  "join the live WAL before closing the writer");
        }
        WriteJson(data / L"settings.json", Json{{"Theme", "Dark"}});
        WriteAtomic(data / L"Covers" / L"test.png", Bytes{1, 2, 3});
        WriteAtomic(data / L"custom" / L"keep.bin", Bytes{4, 5, 6});
        Mapping pinned(data / L"library.db-shm");
        Check(pinned.View != nullptr, "pin SQLite SHM like a Windows crash snapshot");
        Check(sqlite3_close(connection.Value) == SQLITE_OK, "close WAL keeper");
        connection.Value = nullptr;
        auto originalDb = HashFile(data / L"library.db");
        auto originalWal = HashFile(data / L"library.db-wal");
        bool reproduced = false;
        try
        {
            CardRepository blocked(data);
        }
        catch (DatabaseError const &error)
        {
            reproduced = error.ExtendedCode == SQLITE_IOERR_TRUNCATE && error.SystemCode == ERROR_USER_MAPPED_FILE;
        }
        Check(reproduced, "reproduce SQLite 1546 / Win32 1224 before recovery");

        auto trailing = fs::path(data.wstring() + L"\\");
        Check(LibraryMutexName(data) == LibraryMutexName(trailing), "one library lock regardless of trailing slash");
        auto recovered = OpenLibrary(trailing);
        auto all = recovered->All();
        Check(all.size() == 1 && all[0].Name == card.Name && all[0].Favorite && all[0].Notes == card.Notes &&
                  all[0].Tags == card.Tags && all[0].Collection == card.Collection,
              "recovery must retain the uncheckpointed card and metadata");
        Check(recovered->LoadMetadata("custom") == Json{{"value", 42}}, "retain custom SQLite metadata");
        Check(ReadFile(data / L"custom" / L"keep.bin") == Bytes({4, 5, 6}), "retain unknown user files");
        Check(ReadFile(data / L"Covers" / L"test.png") == Bytes({1, 2, 3}), "retain covers");
        Check(Json::parse(ReadFile(data / L"settings.json"))["Theme"] == "Dark", "retain settings");
        int backups = 0;
        for (auto const &entry : fs::directory_iterator(root))
            if (entry.path().filename().wstring().starts_with(L"Data.startup-backup-"))
            {
                ++backups;
                Check(HashFile(entry.path() / L"library.db") == originalDb &&
                          HashFile(entry.path() / L"library.db-wal") == originalWal &&
                          fs::exists(entry.path() / L"library.db-shm"),
                      "retain the complete original DB/WAL/SHM without changes");
            }
        Check(backups == 1, "retain one original data directory");
        all[0].Notes += "；恢复后可继续保存";
        recovered->Save(all[0]);
        recovered.reset();
        auto restarted = OpenLibrary(data)->All();
        Check(restarted.size() == 1 && restarted[0].Notes == all[0].Notes,
              "restart and retain new writes while the old SHM remains mapped");
        std::cout << "PASS mapped SHM recovery, committed WAL, metadata, original backup, repeat startup\n";
    }
}

int main()
{
    auto root = fs::temp_directory_path() / L"HS2StartupRecoveryTests" / Path(NewId());
    int result = 0;
    try
    {
        Recover(root);
        auto corrupt = root / L"corrupt";
        fs::create_directories(corrupt);
        WriteAtomic(corrupt / L"library.db", Bytes{1, 2, 3});
        bool rejected = false;
        try
        {
            OpenLibrary(corrupt);
        }
        catch (std::exception const &)
        {
            rejected = true;
        }
        Check(rejected && ReadFile(corrupt / L"library.db") == Bytes({1, 2, 3}),
              "do not silently reset a corrupt library");
        std::cout << "PASS corrupt database is preserved and reported\n";
    }
    catch (std::exception const &error)
    {
        std::cerr << error.what() << '\n';
        result = 1;
    }
    std::error_code ignored;
    fs::remove_all(root, ignored);
    return result;
}
