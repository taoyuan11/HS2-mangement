#include "Infrastructure/DataLocation.h"
#include <iostream>
#include <windows.h>
using namespace hs2;
using namespace hs2::ui;

int wmain(int argc, wchar_t **argv)
{
    auto root = fs::temp_directory_path() / L"HS2NativeStartupTests" / Path(NewId());
    fs::path networkRoot;
    int result = 0;
    try
    {
        auto source = root / L"source", destination = root / L"destination";
        fs::create_directories(source);
        CardRepository repository(source);
        CardRecord card;
        card.Id = NewId();
        card.Path = Utf8(source / L"sample.png");
        card.Name = "WAL事务";
        repository.Save(card);
        auto id = NewId();
        auto backup = source / L"CardBackups" / Path(card.Id) / Path(id + ".png");
        WriteAtomic(backup, Bytes{1, 2, 3});
        WriteJson(source / L"Operations" / Path(id + ".json"), Json{{"Id", id},
                                                                    {"Card", Json{{"Id", card.Id}}},
                                                                    {"BackupPath", Utf8(backup)},
                                                                    {"ExpectedSourceHash", HashFile(backup)},
                                                                    {"UnknownField", 42}});
        WriteJson(source / L"settings.json", Json{{"Theme", "Light"}});
        WriteAtomic(source / L"NativePreview-v1" / L"skip", Bytes{4});
        CopyLibrary(source, destination);
        CardRepository copied(destination);
        if (copied.All().size() != 1 || copied.All()[0].Name != card.Name)
            throw std::runtime_error("WAL backup lost transaction");
        auto bytes = ReadFile(destination / L"Operations" / Path(id + ".json"));
        auto journal = Json::parse(bytes);
        if (journal["BackupPath"] != Utf8(destination / L"CardBackups" / Path(card.Id) / Path(id + ".png")) ||
            journal["UnknownField"] != 42)
            throw std::runtime_error("journal rebasing lost data");
        if (fs::exists(destination / L"NativePreview-v1"))
            throw std::runtime_error("disposable cache copied");
        auto local = ResolveLibraryLocation(root, {}, root / L"local");
        if (!SamePath(local.Directory, root / L"Data"))
            throw std::runtime_error("portable data changed");
        auto explicitData = ResolveLibraryLocation(root, {L"--data", (root / L"explicit").wstring()}, root / L"local");
        if (!SamePath(explicitData.Directory, root / L"explicit"))
            throw std::runtime_error("explicit data changed");
        // C++ 源码构建必须保留历史 C# 源码资料库标识。
        auto sourceRoot = root / L"checkout", nativeProject = sourceRoot / L"src/HS2Manager.NativeApp";
        fs::create_directories(nativeProject);
        WriteAtomic(nativeProject / L"HS2Manager.NativeApp.vcxproj", Bytes{1});
        auto historical = sourceRoot / L"src/HS2Manager.App/bin/Release/net10.0-windows10.0.19041.0/win-x64";
        auto envLength = GetEnvironmentVariableW(L"HS2MANAGER_SOURCE_OUTPUT", nullptr, 0);
        std::wstring oldEnv(envLength, L'\0');
        if (envLength)
            oldEnv.resize(GetEnvironmentVariableW(L"HS2MANAGER_SOURCE_OUTPUT", oldEnv.data(), envLength));
        SetEnvironmentVariableW(L"HS2MANAGER_SOURCE_OUTPUT", historical.c_str());
        auto sourceLocation = ResolveLibraryLocation(root / L"native-bin", {}, root / L"local");
        auto explicitSource =
            ResolveLibraryLocation(root / L"native-bin", {L"--data", (root / L"override").wstring()}, root / L"local");
        SetEnvironmentVariableW(L"HS2MANAGER_SOURCE_OUTPUT", envLength ? oldEnv.c_str() : nullptr);
        if (!SamePath(sourceLocation.Directory, historical / L"Data") ||
            !SamePath(explicitSource.Directory, root / L"override"))
            throw std::runtime_error("native source build changed library identity or explicit override");
        // 可选的 NAS 测试根目录仅包含本测试可清理的临时检出。
        if (argc == 2)
        {
            auto networkParent = fs::absolute(argv[1]).lexically_normal();
            if (!IsNetworkPath(networkParent))
                throw std::runtime_error("network startup test requires a NAS test directory");
            networkRoot = networkParent / Path("cpp-startup-" + NewId());
            auto native = networkRoot / L"src/HS2Manager.NativeApp";
            auto legacy = networkRoot / L"src/HS2Manager.App/bin/Release/net10.0-windows10.0.19041.0/win-x64";
            fs::create_directories(native);
            WriteAtomic(native / L"HS2Manager.NativeApp.vcxproj", Bytes{1});
            auto absent = ResolveLibraryLocation(legacy, {}, root / L"nas-local");
            if (!SamePath(absent.MigrationSource, legacy / L"Data") || IsNetworkPath(absent.Directory) ||
                absent.Directory.parent_path().parent_path().filename() != L"Source")
                throw std::runtime_error("archived NAS source changed library identity");
            WriteJson(legacy / L"Data/settings.json", Json{{"Theme", "Dark"}, {"Test", "NAS migration"}});
            auto present = ResolveLibraryLocation(legacy, {}, root / L"nas-local");
            if (!SamePath(absent.Directory, present.Directory))
                throw std::runtime_error("restoring historical NAS data changed the destination identity");
            PrepareLibrary(present);
            if (ReadFile(present.Directory / L"settings.json") != ReadFile(legacy / L"Data/settings.json"))
                throw std::runtime_error("historical NAS source settings were not preserved");
            std::cout << "PASS absent managed NAS project, stable source identity "
                         "and local settings migration\n";
        }
        else if (argc != 1)
            throw std::runtime_error("Usage: hs2_startup_checks [network-test-directory]");
        bool rejected = false;
        try
        {
            ResolveLibraryLocation(root, {L"--data"}, root);
        }
        catch (...)
        {
            rejected = true;
        }
        if (!rejected)
            throw std::runtime_error("invalid args accepted");
        rejected = false;
        try
        {
            ResolveLibraryLocation(root, {L"--data", L"\\\\server\\share\\Data"}, root);
        }
        catch (...)
        {
            rejected = true;
        }
        if (!rejected)
            throw std::runtime_error("network database accepted");
        auto corrupt = root / L"corrupt";
        fs::create_directories(corrupt);
        WriteAtomic(corrupt / L"library.db", Bytes{1, 2, 3});
        rejected = false;
        try
        {
            CopyLibrary(corrupt, root / L"failed");
        }
        catch (...)
        {
            rejected = true;
        }
        if (!rejected || fs::exists(root / L"failed"))
            throw std::runtime_error("partial migration published");
        std::cout << "PASS WAL migration, journal rebasing, cache exclusion, "
                     "argument and publication safety\n";
    }
    catch (std::exception const &e)
    {
        std::cerr << e.what() << '\n';
        result = 1;
    }
    std::error_code error;
    fs::remove_all(root, error);
    if (!networkRoot.empty())
        fs::remove_all(networkRoot, error);
    return result;
}
