#include "Preview/TemporaryPreviewSession.h"
#include "Archive/PackageArchive.h"
#include "Archive/ZipArchive.h"
#include "Character/CharacterCatalog.h"
#include <windows.h>
#include <algorithm>
#include <fstream>
#include <iostream>

using namespace hs2;

namespace
{
    int checks = 0;

    void Check(bool value, const char *name)
    {
        if (!value)
            throw std::runtime_error(name);
        ++checks;
    }

    template <class F> void Reject(F action, const char *name)
    {
        bool rejected = false;
        try
        {
            action();
        }
        catch (std::exception const &)
        {
            rejected = true;
        }
        Check(rejected, name);
    }

    Bytes Data(std::string value)
    {
        return {value.begin(), value.end()};
    }

    void Append(Bytes &out, const Bytes &value)
    {
        out.insert(out.end(), value.begin(), value.end());
    }

    void Number(Bytes &out, uint64_t value, int bytes)
    {
        for (int i = 0; i < bytes; ++i)
            out.push_back(static_cast<uint8_t>(value >> (i * 8)));
    }

    void Text(Bytes &out, const std::string &value)
    {
        auto size = value.size();
        while (size >= 128)
        {
            out.push_back(static_cast<uint8_t>((size & 127) | 128));
            size >>= 7;
        }
        out.push_back(static_cast<uint8_t>(size));
        Append(out, Data(value));
    }

    MessagePack S(std::string value)
    {
        return MessagePack::Scalar(std::move(value));
    }

    MessagePack I(int64_t value)
    {
        return MessagePack::Scalar(value);
    }

    Bytes Card(Bytes png, std::string name, int sex = 1)
    {
        auto parameter = MessagePack::Map({{std::string("fullname"), S(name)}, {std::string("sex"), I(sex)}}).ToBytes();
        auto entry = MessagePack::Map({{std::string("name"), S("Parameter")},
                                       {std::string("version"), S("1.0.0")},
                                       {std::string("pos"), I(0)},
                                       {std::string("size"), I(parameter.size())}});
        auto table = MessagePack::Map({{std::string("lstInfo"), MessagePack::Array({entry})}}).ToBytes();
        Number(png, 100, 4);
        Text(png, "【AIS_Chara】");
        Text(png, "1.0.0");
        Number(png, 0, 4);
        Text(png, "temporary-preview-fixture");
        Text(png, "fixture");
        Number(png, table.size(), 4);
        Append(png, table);
        Number(png, parameter.size(), 8);
        Append(png, parameter);
        return png;
    }

    Bytes Zip(const std::vector<std::pair<std::string, Bytes>> &entries, bool symlink = false)
    {
        Bytes result, directory;
        for (auto &[name, data] : entries)
        {
            auto offset = result.size();
            auto crc = Crc32(data);
            Number(result, 0x04034b50, 4);
            Number(result, 20, 2);
            Number(result, 0x800, 2);
            Number(result, 0, 2);
            Number(result, 0, 4);
            Number(result, crc, 4);
            Number(result, data.size(), 4);
            Number(result, data.size(), 4);
            Number(result, name.size(), 2);
            Number(result, 0, 2);
            Append(result, Data(name));
            Append(result, data);
            Number(directory, 0x02014b50, 4);
            Number(directory, symlink ? 0x314 : 20, 2);
            Number(directory, 20, 2);
            Number(directory, 0x800, 2);
            Number(directory, 0, 2);
            Number(directory, 0, 4);
            Number(directory, crc, 4);
            Number(directory, data.size(), 4);
            Number(directory, data.size(), 4);
            Number(directory, name.size(), 2);
            Number(directory, 0, 2);
            Number(directory, 0, 2);
            Number(directory, 0, 2);
            Number(directory, 0, 2);
            Number(directory, symlink ? (0120777U << 16) : 0, 4);
            Number(directory, offset, 4);
            Append(directory, Data(name));
        }
        auto offset = result.size();
        Append(result, directory);
        Number(result, 0x06054b50, 4);
        Number(result, 0, 2);
        Number(result, 0, 2);
        Number(result, entries.size(), 2);
        Number(result, entries.size(), 2);
        Number(result, directory.size(), 4);
        Number(result, offset, 4);
        Number(result, 0, 2);
        return result;
    }

    Bytes Mod(std::string guid, std::string version, std::string label)
    {
        return Zip({{"manifest.xml", Data("<manifest schema-ver=\"1\"><guid>" + guid + "</guid><name>" + label +
                                          "</name><version>" + version + "</version><game>HS2</game></manifest>")},
                    {"abdata/list/characustom/fixture.csv",
                     Data("300,1\nID,Name,MainAB,MainData\n7," + label + ",fixture/hair.unity3d,Hair\n")},
                    {"abdata/fixture/hair.unity3d", Data(label)}});
    }

    void ArchiveChecks(const fs::path &root, const fs::path &decoder, const fs::path &fixtures)
    {
        for (auto name : {L"plain.zip", L"plain.7z", L"plain-rar5.rar"})
        {
            auto storage = TemporaryPreviewStorage::Create(root / "ArchiveData");
            auto output = storage->Root() / "files";
            ExtractPackage(fixtures / name, output, decoder);
            Check(ReadFile(output / "fixture.txt") == Data("temporary preview archive fixture\n"),
                  "plain archive bytes");
        }
        for (auto name : {L"encrypted.zip", L"encrypted.7z", L"encrypted.rar"})
        {
            for (int attempt = 0; attempt < 3; ++attempt)
            {
                auto storage = TemporaryPreviewStorage::Create(root / "ArchiveData");
                auto output = storage->Root() / "files";
                std::optional<std::wstring> password;
                if (attempt)
                    password = attempt == 1                             ? L"wrong"
                               : std::wstring(name) == L"encrypted.rar" ? L"12345678"
                                                                        : L"preview-pass";
                bool required = false;
                try
                {
                    ExtractPackage(fixtures / name, output, decoder, password);
                }
                catch (ArchivePasswordRequired const &error)
                {
                    required = true;
                    Check(error.Retry == (attempt == 1), "password retry reason");
                }
                Check(required == (attempt < 2), "encrypted archive password flow");
                if (!required)
                    Check(fs::is_regular_file(output /
                                              (std::wstring(name) == L"encrypted.rar" ? L"foo.txt" : L"fixture.txt")),
                          "decrypted file exists");
            }
        }
        for (auto name : {L"../escape", L"/absolute", L"C:/absolute", L"a/../escape", L"a//b", L"a/stream:ads",
                          L"CON.txt", L"a./b", L"a /b", L"\\server\\share", L"a/./b", L"COM¹"})
            Reject(
                [&]
                {
                    ValidatePackagePath(name);
                },
                "unsafe archive path rejected");
        for (auto entries :
             {std::vector<std::pair<std::string, Bytes>>{{"valid", Data("ok")}, {"../escape", Data("bad")}},
              {{"same", Data("a")}, {"SAME", Data("b")}},
              {{"parent", Data("a")}, {"parent/child", Data("b")}}})
        {
            auto archive = root / "unsafe.zip";
            WriteAtomic(archive, Zip(entries));
            auto storage = TemporaryPreviewStorage::Create(root / "ArchiveData");
            Reject(
                [&]
                {
                    ExtractPackage(archive, storage->Root() / "files", decoder);
                },
                "unsafe archive preflight");
            Check(fs::is_empty(storage->Root() / "files"), "no writes before archive validation");
        }
        auto link = root / "link.zip";
        WriteAtomic(link, Zip({{"link", Data("../outside")}}, true));
        auto storage = TemporaryPreviewStorage::Create(root / "ArchiveData");
        Reject(
            [&]
            {
                ExtractPackage(link, storage->Root() / "link", decoder);
            },
            "archive symbolic link rejected");
        auto corrupt = Zip({{"file", Data("content")}});
        corrupt[34] ^= 1;
        WriteAtomic(root / "corrupt.zip", corrupt);
        Reject(
            [&]
            {
                ExtractPackage(root / "corrupt.zip", storage->Root() / "corrupt", decoder);
            },
            "CRC verified");
        PackageLimits small;
        small.TotalBytes = 2;
        Reject(
            [&]
            {
                ExtractPackage(fixtures / "plain.zip", storage->Root() / "limited", decoder, {}, {}, {}, small);
            },
            "size budget enforced");
        std::stop_source cancellation;
        cancellation.request_stop();
        Reject(
            [&]
            {
                ExtractPackage(fixtures / "plain.zip", storage->Root() / "cancelled", decoder, {}, {},
                               cancellation.get_token());
            },
            "cancel before archive open");
        Check(!fs::exists(root / "escape"), "archive cannot escape destination");
    }

    void SessionChecks(const fs::path &root, const fs::path &decoder, const fs::path &cover, const fs::path &tpk)
    {
        auto png = ReadFile(cover);
        png.resize(CardParser::FindPngEnd(png));
        auto game = root / "Game", data = root / "Data";
        fs::create_directories(game / "abdata/list/characustom");
        fs::create_directories(game / "BepInEx/plugins");
        WriteAtomic(game / "mods/local.zipmod", Mod("same.guid", "99", "Installed"));
        WriteAtomic(game / "mods/fallback.zipmod", Mod("fallback.guid", "1", "Fallback"));
        WriteAtomic(game / "UserData/fallback.txt", Data("installed fallback"));
        CardRepository repository(data);
        auto base = ModDependencyService(repository).Scan(game);
        auto before = repository.LoadMetadata("mods-v1")->dump();
        auto source = root / L"角色卡.png";
        WriteAtomic(source, Card(png, "独立角色"));
        auto sourceHash = HashFile(source), modHash = HashFile(game / "mods/local.zipmod");
        auto single = TemporaryPreviewSession::Open(source, game, data, decoder, base);
        Check(single->Cards.size() == 1 && single->Cards.front().Card.Name == "独立角色", "single PNG preview");
        Check(HashFile(Path(single->Cards[0].Card.Path)) == sourceHash, "full character snapshot preserves bytes");
        auto singleRoot = single->Storage->Root();
        auto lease = single->Cards[0].Resources;
        TemporaryPreviewStorage::CleanAbandoned(data);
        Check(fs::exists(singleRoot), "active preview survives cleanup");
        single.reset();
        Check(fs::exists(singleRoot), "resource context retains directory lease");
        lease.reset();
        Check(!fs::exists(singleRoot), "last lease cleans directory");

        auto package = root / L"多角色资源.zip";
        WriteAtomic(package,
                    Zip({{"包装/角色A/UserData/chara/female/a.png", Card(png, "角色A")},
                         {"包装/角色B/UserData/chara/female/b.png", Card(png, "角色B")},
                         {"包装/角色B/UserData/chara/male/c.png", Card(png, "角色C", 0)},
                         {"包装/角色A/mods/one.zipmod", Mod("same.guid", "1", "A")},
                         {"包装/角色B/mods/two.zipmod", Mod("same.guid", "2", "B")},
                         {"包装/mods/shared.zipmod", Mod("shared.guid", "1", "Shared")},
                         {"包装/abdata/fixture/hair.unity3d", Data("shared loose must not override character MOD")},
                         {"包装/角色A/UserData/texture.png", png},
                         {"包装/角色B/UserData/texture.png", png},
                         {"包装/角色A/abdata/fixture/loose.unity3d", Data("loose A")},
                         {"包装/角色B/abdata/fixture/loose.unity3d", Data("loose B")},
                         {"包装/角色A/abdata/fixture/texture/image.png", png},
                         {"包装/BepInEx/plugins/bad.dll", Data("invalid plugin metadata")},
                         {"包装/普通图片.png", png},
                         {"包装/损坏.png", Data("bad")}}));
        auto packageHash = HashFile(package);
        auto session = TemporaryPreviewSession::Open(package, game, data, decoder, base);
        auto sessionRoot = session->Storage->Root();
        Check(session->Cards.size() == 3, "multiple valid cards and ignored images");
        Check(session->Cards[0].Card.Name == "角色A" && session->Cards[2].Card.Name == "角色C",
              "relative-path card order");
        Check(!session->Warnings.empty(), "invalid resources reported without losing valid cards");
        for (size_t i = 0; i < session->Cards.size(); ++i)
        {
            auto &entry = session->Cards[i];
            auto resources = entry.Resources;
            auto find = [&](std::string guid) -> const ModFileInfo &
            {
                auto found = std::find_if(resources->Index.Files.begin(), resources->Index.Files.end(),
                                          [&](auto &mod)
                                          {
                                              return mod.Guid == guid;
                                          });
                if (found == resources->Index.Files.end())
                    throw std::runtime_error("expected mod missing");
                return *found;
            };
            Check(find("same.guid").Name == (i == 0 ? "A" : "B"),
                  "per-character package GUID overrides newer installed mod");
            Check(find("shared.guid").Name == "Shared" && find("fallback.guid").Name == "Fallback",
                  "shared then installed fallback");
            Check(ReadFile(resources->File("abdata/fixture/loose.unity3d")) == Data(i == 0 ? "loose A" : "loose B"),
                  "per-character loose resource scope");
            Check(resources->File("UserData/fallback.txt") == game / "UserData/fallback.txt",
                  "installed loose fallback");
            Check(resources->File("USERDATA\\texture.PNG") != game / "USERDATA\\texture.PNG",
                  "case and slash normalization");
            Reject(
                [&]
                {
                    resources->File("../escape");
                },
                "resource reference traversal rejected");
            CharacterCatalog catalog(game, resources->Data, tpk, resources);
            catalog.Load();
            for (auto &warning : catalog.Warnings())
                std::cerr << "Catalog: " << warning << '\n';
            auto selected = catalog.Resolve({"same.guid", 7, 300, "hair.parts[0].id"});
            Check(selected.Get("Name") == (i == 0 ? "A" : "B"), "catalog consumes scoped transient index");
            Check(ReadFile(catalog.Bundle("fixture/hair.unity3d", selected.Archive)) == Data(i == 0 ? "A" : "B"),
                  "catalog bundle comes from selected package");
            if (i == 0)
                Check(HashFile(catalog.Asset("fixture/texture.unity3d", "image").Bundle) == Sha256(png),
                      "loose PNG resource provider");
            Check(!fs::exists(resources->Data / "library.db"), "transient catalog does not open a database");
        }
        Check(repository.All().empty() && repository.LoadMetadata("mods-v1")->dump() == before,
              "no library or MOD index mutation");
        Check(HashFile(source) == sourceHash && HashFile(package) == packageHash &&
                  HashFile(game / "mods/local.zipmod") == modHash,
              "source card archive and installed resource unchanged");
        session.reset();
        Check(!fs::exists(sessionRoot), "multi-card session cleaned");
        auto image = root / "image.png";
        WriteAtomic(image, png);
        Reject(
            [&]
            {
                TemporaryPreviewSession::Open(image, game, data, decoder);
            },
            "ordinary PNG rejected");
        Check(fs::is_empty(data / "TemporaryPreview"), "failed preparation cleans session");
        std::stop_source cancellation;
        Reject(
            [&]
            {
                TemporaryPreviewSession::Open(
                    package, game, data, decoder, base, {},
                    [&](auto &progress)
                    {
                        if (progress.Message == "索引包内预览资源")
                            cancellation.request_stop();
                    },
                    cancellation.get_token());
            },
            "cancel during indexing");
        Check(fs::is_empty(data / "TemporaryPreview"), "cancel cleans extracted files");
        auto orphan = data / "TemporaryPreview" / Path(NewId());
        WriteAtomic(orphan / "session.lock", Data(std::string("HS2Manager temporary preview v1\n") + '\0'));
        WriteAtomic(orphan / "files/partial", Data("partial"));
        auto foreign = data / "TemporaryPreview" / Path(NewId());
        WriteAtomic(foreign / "session.lock", Data("another application's file"));
        TemporaryPreviewStorage::CleanAbandoned(data);
        Check(!fs::exists(orphan) && fs::exists(foreign), "startup only cleans stale owned sessions");
    }
}

int wmain(int argc, wchar_t **argv)
{
    auto root = fs::temp_directory_path() / Path("hs2-temporary-preview-checks-" + NewId());
    int result = 0;
    try
    {
        if (argc != 5)
            throw std::runtime_error("Usage: temporary_preview_checks decoder fixtures cover classdata.tpk");
        fs::create_directory(root);
        ArchiveChecks(root, argv[1], argv[2]);
        SessionChecks(root, argv[1], argv[3], argv[4]);
        std::cout << "PASS temporary preview: " << checks << " checks\n";
    }
    catch (std::exception const &e)
    {
        std::cerr << "FAIL after " << checks << " checks: " << e.what() << '\n';
        result = 1;
    }
    // 只清理本测试创建的唯一目录，不影响用户资料。
    if (root.parent_path() == fs::temp_directory_path() &&
        root.filename().wstring().starts_with(L"hs2-temporary-preview-checks-"))
    {
        std::error_code error;
        fs::remove_all(root, error);
        if (error)
        {
            std::cerr << "Test cleanup failed: " << error.message() << '\n';
            result = 1;
        }
    }
    return result;
}
