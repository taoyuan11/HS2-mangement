#include "Character/CharacterCatalog.h"
#include "Archive/ZipArchive.h"
#include <algorithm>
#include <chrono>
#include <iostream>
#include <thread>

using namespace hs2;

namespace
{
    void Check(bool value, const char *message)
    {
        if (!value)
            throw std::runtime_error(message);
    }

    // 构造独立的未压缩 ZIP，不依赖游戏安装或外部压缩程序。
    Bytes Zip(const std::string &name, const std::string &text)
    {
        Bytes out;
        auto number = [&](uint64_t value, int bytes)
        {
            for (int i = 0; i < bytes; ++i)
                out.push_back(static_cast<uint8_t>(value >> (8 * i)));
        };
        auto append = [&](const std::string &s)
        {
            out.insert(out.end(), s.begin(), s.end());
        };
        auto crc = Crc32({reinterpret_cast<const uint8_t *>(text.data()), text.size()});
        number(0x04034b50, 4);
        number(20, 2);
        number(0x800, 2);
        number(0, 2);
        number(0, 4);
        number(crc, 4);
        number(text.size(), 4);
        number(text.size(), 4);
        number(name.size(), 2);
        number(0, 2);
        append(name);
        append(text);
        auto offset = out.size();
        number(0x02014b50, 4);
        number(20, 2);
        number(20, 2);
        number(0x800, 2);
        number(0, 2);
        number(0, 4);
        number(crc, 4);
        number(text.size(), 4);
        number(text.size(), 4);
        number(name.size(), 2);
        number(0, 2);
        number(0, 2);
        number(0, 2);
        number(0, 2);
        number(0, 4);
        number(0, 4);
        append(name);
        auto size = out.size() - offset;
        number(0x06054b50, 4);
        number(0, 2);
        number(0, 2);
        number(1, 2);
        number(1, 2);
        number(size, 4);
        number(offset, 4);
        number(0, 2);
        return out;
    }

    ModFileInfo MakeMod(const fs::path &root, int id, bool list = true, std::string label = "Hair")
    {
        auto name = list ? "ABDATA\\List\\CharaCustom\\test.CSV" : "abdata/item/test.unity3d";
        auto path = root / "mods" / Path(std::to_string(id) + ".zipmod");
        auto csv = "300,1\nID,Name,MainAB,MainData\n7," + label + ",fixture/hair.unity3d,Hair\n";
        WriteAtomic(path, Zip(name, list ? csv : "bundle"));
        ModFileInfo mod;
        mod.Path = Utf8(path);
        mod.Guid = "fixture." + std::to_string(id);
        mod.Name = label;
        mod.Size = fs::file_size(path);
        mod.ModifiedUtcTicks = ModifiedTicks(path);
        mod.ArchiveEntries = std::vector<ModArchiveEntry>{{name, static_cast<int64_t>(csv.size())}};
        return mod;
    }

    std::string Digest(const CharacterCatalog &catalog)
    {
        std::vector<std::string> rows;
        for (auto &e : catalog.Entries())
            rows.push_back(Json::array({e.Category, e.Id, e.Guid, e.Source, Utf8(e.Archive), e.Values}).dump());
        std::sort(rows.begin(), rows.end());
        auto text = Json(rows).dump();
        return Sha256({reinterpret_cast<const uint8_t *>(text.data()), text.size()});
    }

    void Benchmark(const fs::path &game, const fs::path &data, const fs::path &tpk)
    {
        fs::create_directories(data);
        auto resources = std::make_shared<PreviewResourceContext>();
        resources->Game = game;
        resources->Data = data;
        auto index = data / "benchmark-index.json";
        if (fs::exists(index))
            resources->Index = Json::parse(ReadFile(index)).get<ModIndex>();
        else
        {
            resources->Index = ModDependencyService::ScanTransient(game, nullptr,
                                                                   [](auto &p)
                                                                   {
                                                                       if (p.Completed % 1000 == 0)
                                                                           std::cout << p.Message << " " << p.Completed
                                                                                     << "/" << p.Total << std::endl;
                                                                   });
            resources->Index.Files = NativeModSelection::Select(resources->Index.Files);
            WriteJson(index, resources->Index);
        }
        for (int run = 0; run < 2; ++run)
        {
            CharacterCatalog catalog(game, data, tpk, resources);
            auto start = std::chrono::steady_clock::now(), modStart = start;
            bool mods = false;
            catalog.Load(
                [&](auto &p)
                {
                    if (!mods && p.Message == "读取 MOD 素材清单")
                    {
                        mods = true;
                        modStart = std::chrono::steady_clock::now();
                    }
                });
            auto end = std::chrono::steady_clock::now();
            std::cout << "run=" << run << " mods=" << resources->Index.Files.size()
                      << " entries=" << catalog.Entries().size() << " warnings=" << catalog.Warnings().size()
                      << " total_ms=" << std::chrono::duration<double, std::milli>(end - start).count()
                      << " mod_ms=" << std::chrono::duration<double, std::milli>(end - modStart).count()
                      << " digest=" << Digest(catalog) << std::endl;
        }
    }

    void Regression(const fs::path &root, const fs::path &tpk)
    {
        auto resources = std::make_shared<PreviewResourceContext>();
        resources->Game = root / "game";
        resources->Data = root / "data";
        fs::create_directories(resources->Game / "abdata/list/characustom");
        for (int i = 0; i < 40; ++i)
            resources->Index.Files.push_back(MakeMod(resources->Game, i, i % 2 == 0));
        CharacterCatalog cold(resources->Game, resources->Data, tpk, resources);
        auto caller = std::this_thread::get_id();
        int completed = -1;
        cold.Load(
            [&](auto &p)
            {
                Check(std::this_thread::get_id() == caller, "Progress must run on the calling thread.");
                Check(p.Completed >= completed && p.Completed <= p.Total, "Progress went backwards or exceeded total.");
                completed = p.Completed;
            });
        Check(cold.Entries().size() == 20 && cold.Warnings().empty(), "Cold catalog entries mismatch.");
        Check(completed == 40, "Catalog progress did not finish.");
        std::vector<fs::path> cacheFiles;
        auto cacheRoot = resources->Data / "NativeCpp/resources";
        for (auto &file : fs::directory_iterator(cacheRoot))
            cacheFiles.push_back(file.path());
        Check(cacheFiles.size() == 1 && cacheFiles[0].extension() == ".msgpack",
              "Expected one consolidated MOD cache.");
        auto cacheTime = fs::last_write_time(cacheFiles[0]);
        CharacterCatalog warm(resources->Game, resources->Data, tpk, resources);
        warm.Load();
        Check(Digest(cold) == Digest(warm), "Warm catalog differs from cold catalog.");
        Check(fs::last_write_time(cacheFiles[0]) == cacheTime, "Unchanged cache was rewritten.");
        Check(ReadFile(warm.Bundle("item/test.unity3d")) == Bytes({'b', 'u', 'n', 'd', 'l', 'e'}),
              "Skipping non-character MODs lost bundle providers.");

        // 临时目录更换后仍命中安装资源缓存，即使此时源包不可打开。
        for (auto &mod : resources->Index.Files)
            fs::rename(Path(mod.Path), Path(mod.Path + ".offline"));
        resources->CatalogCache = cacheRoot;
        resources->Data = root / "session-one";
        CharacterCatalog session(resources->Game, resources->Data, tpk, resources);
        session.Load();
        Check(Digest(session) == Digest(cold) && session.Warnings().empty(),
              "Transient catalog did not reuse installed lists.");
        resources->Data = root / "session-two";
        CharacterCatalog sessionTwo(resources->Game, resources->Data, tpk, resources);
        sessionTwo.Load();
        Check(Digest(sessionTwo) == Digest(cold), "Separate preview sessions did not share installed lists.");
        for (auto &mod : resources->Index.Files)
            fs::rename(Path(mod.Path + ".offline"), Path(mod.Path));

        // 单个包更新只使其自身失效，来源名称不应固定在缓存里。
        resources->Index.Files[0] = MakeMod(resources->Game, 0, true, "Updated hair");
        resources->Index.Files[2].Name = "Renamed source";
        resources->Index.Files[2].Guid = "renamed.guid";
        for (size_t i = 1; i < resources->Index.Files.size(); ++i)
        {
            auto &mod = resources->Index.Files[i];
            fs::rename(Path(mod.Path), Path(mod.Path + ".offline"));
        }
        CharacterCatalog changed(resources->Game, resources->Data, tpk, resources);
        changed.Load();
        Check(changed.Warnings().empty() && changed.Entries().size() == 20,
              "Unchanged MODs were re-read after an update.");
        Check(changed.Resolve({"fixture.0", 7, 300}).Get("Name") == "Updated hair", "Changed MOD reused stale rows.");
        Check(changed.Resolve({"renamed.guid", 7, 300}).Source == "Renamed source",
              "Cached source metadata was stale.");
        for (size_t i = 1; i < resources->Index.Files.size(); ++i)
        {
            auto &mod = resources->Index.Files[i];
            fs::rename(Path(mod.Path + ".offline"), Path(mod.Path));
        }
        auto previous = resources->Index.Files[0];
        auto previousTime = fs::last_write_time(Path(previous.Path));
        auto revised = MakeMod(resources->Game, 0, true, "Revised hair");
        fs::last_write_time(Path(revised.Path), previousTime + std::chrono::seconds(2));
        revised.ModifiedUtcTicks = ModifiedTicks(Path(revised.Path));
        Check(revised.Size == previous.Size, "Timestamp fixture changed file size.");
        resources->Index.Files[0] = revised;
        CharacterCatalog timestamp(resources->Game, resources->Data, tpk, resources);
        timestamp.Load();
        Check(timestamp.Resolve({"fixture.0", 7, 300}).Get("Name") == "Revised hair",
              "Timestamp-only update reused stale rows.");

        auto added = MakeMod(resources->Game, 40, true, "Added");
        added.ArchiveEntries.reset();
        resources->Index.Files.push_back(added);
        resources->Index.Files.erase(resources->Index.Files.begin() + 4);
        CharacterCatalog selection(resources->Game, resources->Data, tpk, resources);
        selection.Load();
        Check(selection.Entries().size() == 20 && selection.Resolve({"fixture.40", 7, 300}).Get("Name") == "Added",
              "Added/removed MODs or missing archive index handled incorrectly.");

        WriteAtomic(cacheFiles[0], Bytes{1, 2, 3});
        CharacterCatalog repaired(resources->Game, resources->Data, tpk, resources);
        repaired.Load();
        Check(Digest(repaired) == Digest(selection) && repaired.Warnings().empty(), "Corrupt cache did not rebuild.");

        // 包内覆盖不能污染安装资源缓存；同键的最后一个来源继续胜出。
        auto overlay = MakeMod(root / "package", 50, true, "Package override");
        overlay.Guid = "fixture.0";
        resources->Index.Files.push_back(overlay);
        CharacterCatalog scoped(resources->Game, resources->Data, tpk, resources);
        scoped.Load();
        Check(scoped.Resolve({"fixture.0", 7, 300}).Get("Name") == "Package override",
              "Parallel reads changed precedence.");
        resources->Index.Files.pop_back();
        CharacterCatalog unscoped(resources->Game, resources->Data, tpk, resources);
        unscoped.Load();
        Check(Digest(unscoped) == Digest(selection), "Package cache leaked into installed resources.");

        auto broken = MakeMod(resources->Game, 52, true, "\"unterminated");
        resources->Index.Files.push_back(broken);
        CharacterCatalog partial(resources->Game, resources->Data, tpk, resources);
        partial.Load();
        Check(partial.Entries().size() == 20 && partial.Warnings().size() == 1,
              "Bad MOD hid valid entries or lost its warning.");
        resources->Index.Files.pop_back();

        // 缓存无法写入时清单仍然可用；取消后同一对象可以安全重试。
        auto blocked = root / "cache-is-a-file";
        WriteAtomic(blocked, Bytes{0});
        resources->CatalogCache = blocked;
        resources->Data = root / "uncached-session";
        CharacterCatalog uncached(resources->Game, resources->Data, tpk, resources);
        std::stop_source stop;
        bool cancelled = false;
        try
        {
            int calls = 0;
            uncached.Load(
                [&](auto &)
                {
                    if (++calls == 2)
                        stop.request_stop();
                },
                stop.get_token());
        }
        catch (const Cancelled &)
        {
            cancelled = true;
        }
        Check(cancelled, "MOD loading ignored cancellation.");
        uncached.Load();
        Check(Digest(uncached) == Digest(selection) && uncached.Warnings().empty(),
              "Retry or unwritable cache lost valid rows.");

        CharacterCatalog callbackFailure(resources->Game, root / "callback-session", tpk, resources);
        bool failed = false;
        try
        {
            int calls = 0;
            callbackFailure.Load(
                [&](auto &)
                {
                    if (++calls == 2)
                        throw std::runtime_error("callback failure");
                });
        }
        catch (const std::runtime_error &)
        {
            failed = true;
        }
        Check(failed, "Progress callback failure was swallowed.");
        callbackFailure.Load();
        Check(Digest(callbackFailure) == Digest(selection), "Callback failure left unsafe background work.");
    }
}

int wmain(int argc, wchar_t **argv)
{
    fs::path root;
    try
    {
        if (argc == 5)
            Benchmark(argv[2], argv[3], argv[4]);
        else
        {
            Check(argc == 2, "Usage: hs2_catalog_checks classdata.tpk | --benchmark game data classdata.tpk");
            root = fs::temp_directory_path() / Path("hs2-catalog-" + NewId());
            Regression(root, argv[1]);
            fs::remove_all(root);
            std::cout << "Catalog checks passed.\n";
        }
        return 0;
    }
    catch (const std::exception &e)
    {
        if (!root.empty())
        {
            std::error_code error;
            fs::remove_all(root, error);
        }
        std::cerr << e.what() << '\n';
        return 1;
    }
}
