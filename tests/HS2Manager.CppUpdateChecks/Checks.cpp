#include "Update.h"
#include "third_party/json.hpp"
#include "Archive/ZipArchive.h"
#include <fstream>
#include <iostream>
#include <stdexcept>

using namespace hs2::update;
using Json = nlohmann::json;

namespace
{
    void Require(bool value, char const *message)
    {
        if (!value)
            throw std::runtime_error(message);
    }

    template <class F> void Reject(F action)
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
        Require(rejected, "Expected rejection");
    }

    void Package(fs::path const &root, std::string version, bool newer)
    {
        fs::create_directories(root);
        for (auto name : {L"HS2Manager.exe", L"HS2Agent.exe", L"HS2Updater.exe", L"native/runtime.dll"})
            WriteText(root / name, version + " test program");
        WriteText(root / (newer ? L"new-resource.txt" : L"old-resource.txt"), version);
        WriteText(root / L"Data/README.txt", "sample only");
        WriteText(root / L"package-info.json",
                  Json{{"schemaVersion", 1}, {"version", version}, {"platform", "win-x64"}}.dump());
        Json manifest = Json::array();
        for (auto const &file : fs::recursive_directory_iterator(root))
            if (file.is_regular_file())
                manifest.push_back({{"Path", Utf8(file.path().lexically_relative(root))},
                                    {"Bytes", file.file_size()},
                                    {"SHA256", HashFile(file.path())}});
        WriteText(root / L"package-files.json", manifest.dump());
    }

    Json ReleaseJson()
    {
        auto base = "https://github.com/taoyuan11/HS2-mangement/releases/download/v1.10.0/";
        return {
            {"tag_name", "v1.10.0"},
            {"draft", false},
            {"prerelease", false},
            {"body", "说明"},
            {"assets",
             Json::array({{{"name", "HS2Manager-1.10.0-cpp-win-x64.zip"},
                           {"size", 4},
                           {"browser_download_url", std::string(base) + "HS2Manager-1.10.0-cpp-win-x64.zip"}},
                          {{"name", "update.json"}, {"browser_download_url", std::string(base) + "update.json"}}})}};
    }

    void Zip(fs::path const &package, fs::path const &archive, std::string badName = {})
    {
        std::vector<uint8_t> bytes, central;
        auto put = [](auto &out, uint32_t n, int length)
        {
            for (int i = 0; i < length; ++i)
                out.push_back(uint8_t(n >> (8 * i)));
        };
        unsigned count = 0;
        for (auto const &file : fs::recursive_directory_iterator(package))
        {
            if (!file.is_regular_file())
                continue;
            auto name = badName.empty()
                            ? "HS2Manager-1.10.0-cpp-win-x64/" + Utf8(file.path().lexically_relative(package))
                            : badName;
            auto data = ReadText(file.path());
            auto crc = hs2::Crc32({reinterpret_cast<uint8_t const *>(data.data()), data.size()});
            auto offset = uint32_t(bytes.size());
            put(bytes, 0x04034b50, 4);
            put(bytes, 20, 2);
            put(bytes, 0x800, 2);
            put(bytes, 0, 2);
            put(bytes, 0, 4);
            put(bytes, crc, 4);
            put(bytes, uint32_t(data.size()), 4);
            put(bytes, uint32_t(data.size()), 4);
            put(bytes, uint32_t(name.size()), 2);
            put(bytes, 0, 2);
            bytes.insert(bytes.end(), name.begin(), name.end());
            bytes.insert(bytes.end(), data.begin(), data.end());
            put(central, 0x02014b50, 4);
            put(central, 20, 2);
            put(central, 20, 2);
            put(central, 0x800, 2);
            put(central, 0, 2);
            put(central, 0, 4);
            put(central, crc, 4);
            put(central, uint32_t(data.size()), 4);
            put(central, uint32_t(data.size()), 4);
            put(central, uint32_t(name.size()), 2);
            put(central, 0, 2);
            put(central, 0, 2);
            put(central, 0, 2);
            put(central, 0, 2);
            put(central, 0, 4);
            put(central, offset, 4);
            central.insert(central.end(), name.begin(), name.end());
            ++count;
            if (!badName.empty())
                break;
        }
        auto offset = uint32_t(bytes.size());
        bytes.insert(bytes.end(), central.begin(), central.end());
        put(bytes, 0x06054b50, 4);
        put(bytes, 0, 2);
        put(bytes, 0, 2);
        put(bytes, count, 2);
        put(bytes, count, 2);
        put(bytes, uint32_t(central.size()), 4);
        put(bytes, offset, 4);
        put(bytes, 0, 2);
        std::ofstream out(archive, std::ios::binary);
        out.write(reinterpret_cast<char const *>(bytes.data()), bytes.size());
    }
}

int wmain(int argc, wchar_t **argv)
{
    if (argc == 2 && std::wstring(argv[1]) == L"--network")
    {
        try
        {
            auto result = Get("https://api.github.com/repos/taoyuan11/HS2-mangement/releases/latest", {});
            Require(result.Status == 200 || result.Status == 404 || result.Status == 403 || result.Status == 429,
                    "Live GitHub status");
            std::cout << "PASS live GitHub HTTPS: " << result.Status << '\n';
            std::stop_source stop;
            stop.request_stop();
            Reject(
                [&]
                {
                    Get("https://api.github.com/repos/taoyuan11/HS2-mangement/releases/latest", stop.get_token());
                });
            std::cout << "PASS cancelled HTTPS request\n";
            return 0;
        }
        catch (std::exception const &e)
        {
            std::cerr << e.what() << '\n';
            return 1;
        }
    }
    if (argc == 5 && std::wstring(argv[1]) == L"--handoff")
    {
        try
        {
            fs::path installation(argv[2]), package(argv[3]), data(argv[4]);
            auto lease = InstallationLease(installation);
            auto request = PrepareLaunch(installation, package, data);
            auto helper = LaunchUpdater(request);
            std::cout << "HELPER:" << GetProcessId(helper.Value) << "\nCACHE:" << Utf8(CacheRoot(installation)) << '\n';
            return 0;
        }
        catch (std::exception const &e)
        {
            std::cerr << e.what() << '\n';
            return 1;
        }
    }
    if (argc == 6 && std::wstring(argv[1]) == L"--crash-install")
    {
        auto lease = InstallationLease(argv[2], true);
        auto failAt = std::stoul(argv[5]);
        Install(argv[2], argv[3], argv[4],
                [failAt](size_t moved)
                {
                    if (moved == failAt)
                        TerminateProcess(GetCurrentProcess(), 77);
                });
        return 1;
    }
    auto root = fs::temp_directory_path() / Path("hs2-update-check-" + Id());
    fs::path cache;
    try
    {
        Require(Version::Parse("1.10.0") > Version::Parse("1.9.0"), "Numeric version comparison");
        for (auto text : {"1.2", "1.2.3.4", "01.2.3", "1.-1.3", "1.2.65536", "1.2.3-rc1"})
            Reject(
                [&]
                {
                    Version::Parse(text);
                });
        for (auto name : {"../bad", "C:/bad", "a/../bad", "a//b", "a/CON.txt", "a./b", "a/b ", "a:stream",
                          "\\server\\share", "Data/.."})
            Reject(
                [&]
                {
                    SafeRelative(name);
                });
        Require(Protected(SafeRelative("Data/a")) && Protected(SafeRelative("Data.startup-backup-x/a")),
                "Protected data");
        auto release = ParseRelease(ReleaseJson().dump());
        auto manifest = Json{{"schemaVersion", 1},
                             {"version", "1.10.0"},
                             {"platform", "win-x64"},
                             {"archive", release.ArchiveName},
                             {"bytes", 4},
                             {"sha256", std::string(64, 'a')}};
        ApplyManifest(release, manifest.dump());
        auto fetch = [&](std::string const &url, std::stop_token)
        {
            return HttpResponse{200, url.ends_with("latest") ? ReleaseJson().dump() : manifest.dump()};
        };
        Require(Check("1.9.0", {}, fetch).has_value(), "New release");
        Require(!Check("1.10.0", {}, fetch), "No downgrade");
        for (unsigned status : {404, 403, 429, 500})
            Reject(
                [&]
                {
                    Check("1.0.0", {},
                          [status](auto const &, auto)
                          {
                              return HttpResponse{status, ""};
                          });
                });
        Reject(
            [&]
            {
                Check("1.0.0", {},
                      [](auto const &, auto) -> HttpResponse
                      {
                          throw std::runtime_error("offline");
                      });
            });
        auto prerelease = ReleaseJson();
        prerelease["prerelease"] = true;
        Require(!Check("1.0.0", {},
                       [&](auto const &, auto)
                       {
                           return HttpResponse{200, prerelease.dump()};
                       }),
                "Ignore prerelease");
        auto missing = ReleaseJson();
        missing["assets"] = Json::array();
        Reject(
            [&]
            {
                ParseRelease(missing.dump());
            });
        manifest["sha256"] = "bad";
        Reject(
            [&]
            {
                ApplyManifest(release, manifest.dump());
            });
        auto install = root / L"中文 installation", package = root / L"new", data = install / L"Data";
        Package(install, "1.9.0", false);
        Package(package, "1.10.0", true);
        cache = CacheRoot(install);
        WriteText(data / L"library.db", "database");
        WriteText(data / L"settings.json", "settings");
        WriteText(install / L"Data.startup-backup-1/keep", "backup");
        WriteText(install / L"agent.json", "user config");
        ValidatePackage(package, "1.10.0");
        auto firstRequest = PrepareLaunch(install, package, data);
        auto firstContents = ReadText(firstRequest);
        auto secondRequest = PrepareLaunch(install, package, data / L"another-library");
        Require(firstRequest != secondRequest && ReadText(firstRequest) == firstContents,
                "Concurrent windows must not overwrite update handoff requests");
        auto oldHash = HashFile(install / L"HS2Manager.exe");
        Reject(
            [&]
            {
                Install(install, package, install / L"NATIVE");
            });
        Require(HashFile(install / L"HS2Manager.exe") == oldHash,
                "Custom data overlap must leave installation unchanged");
        {
            auto lease = InstallationLease(install);
            auto second = InstallationLease(install);
            Reject(
                [&]
                {
                    auto writer = InstallationLease(install, true);
                });
        }
        {
            auto lease = InstallationLease(install, true);
            Reject(
                [&]
                {
                    auto reader = InstallationLease(install);
                });
        }
        std::stop_source cancelled;
        cancelled.request_stop();
        Reject(
            [&]
            {
                HashFile(package / L"HS2Manager.exe", cancelled.get_token());
            });
        auto archive = root / L"update.zip";
        Zip(package, archive);
        release.Bytes = fs::file_size(archive);
        release.SHA256 = HashFile(archive);
        Extract(release, archive, root / L"expanded");
        Require(HashFile(root / L"expanded/HS2Manager.exe") == HashFile(package / L"HS2Manager.exe"),
                "Extracted package");
        auto wrong = release;
        wrong.SHA256 = std::string(64, '0');
        Reject(
            [&]
            {
                Extract(wrong, archive, root / L"wrong");
            });
        Zip(package, root / L"bad.zip", "HS2Manager-1.10.0-cpp-win-x64/../escape");
        wrong.Bytes = fs::file_size(root / L"bad.zip");
        wrong.SHA256 = HashFile(root / L"bad.zip");
        Reject(
            [&]
            {
                Extract(wrong, root / L"bad.zip", root / L"unsafe");
            });
        WriteText(install / L"new-resource.txt", "custom conflict");
        Reject(
            [&]
            {
                Install(install, package, data);
            });
        fs::remove(install / L"new-resource.txt");
        {
            Handle locked(CreateFileW((install / L"HS2Manager.exe").c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING,
                                      FILE_ATTRIBUTE_NORMAL, nullptr));
            Reject(
                [&]
                {
                    Install(install, package, data);
                });
        }
        Require(HashFile(install / L"HS2Manager.exe") == oldHash, "Locked installation unchanged");
        for (size_t failAt : {1U, 2U, 5U, 9U})
        {
            Reject(
                [&]
                {
                    Install(install, package, data,
                            [failAt](size_t moved)
                            {
                                if (moved == failAt)
                                    throw std::runtime_error("injected failure");
                            });
                });
            Require(HashFile(install / L"HS2Manager.exe") == oldHash && fs::exists(install / L"old-resource.txt"),
                    "Rollback restored old package");
            Recover(cache / L"transaction.json");
        }
        for (size_t failAt : {1U, 2U, 5U})
        {
            wchar_t executable[32768]{};
            GetModuleFileNameW(nullptr, executable, 32768);
            auto command = std::wstring(L"\"") + executable + L"\" --crash-install \"" + install.wstring() + L"\" \"" +
                           package.wstring() + L"\" \"" + data.wstring() + L"\" " + std::to_wstring(failAt);
            STARTUPINFOW startup{sizeof(startup)};
            PROCESS_INFORMATION child{};
            Require(CreateProcessW(executable, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr,
                                   nullptr, &startup, &child),
                    "Start interrupted install fixture");
            Handle process(child.hProcess), thread(child.hThread);
            if (WaitForSingleObject(process.Value, 10000) != WAIT_OBJECT_0)
            {
                TerminateProcess(process.Value, 1);
                WaitForSingleObject(process.Value, 5000);
                throw std::runtime_error("Interrupted installer fixture timed out");
            }
            DWORD code{};
            GetExitCodeProcess(process.Value, &code);
            Require(code == 77, "Installer interrupted at requested move");
            Reject(
                [&]
                {
                    auto lease = InstallationLease(install);
                });
            auto lease = InstallationLease(install, true);
            Recover(cache / L"transaction.json");
            Require(HashFile(install / L"HS2Manager.exe") == oldHash, "Restart recovery restored old package");
        }
        auto journal = Install(install, package, data);
        Require(!fs::exists(install / L"old-resource.txt") && fs::exists(install / L"new-resource.txt"),
                "Obsolete managed file removed");
        Recover(journal);
        Require(HashFile(install / L"HS2Manager.exe") == oldHash, "Explicit recovery");
        journal = Install(install, package, data);
        Complete(journal);
        Require(ReadText(data / L"library.db") == "database" && ReadText(data / L"settings.json") == "settings" &&
                    ReadText(install / L"Data.startup-backup-1/keep") == "backup" &&
                    ReadText(install / L"agent.json") == "user config",
                "User files preserved");
        Require(Json::parse(ReadText(journal)).at("state") == "complete", "Completed journal");
        fs::remove_all(cache);
        fs::remove_all(root);
        std::cout
            << "PASS: update metadata, paths, package verification, locks, installation, rollback and preserved data\n";
        return 0;
    }
    catch (std::exception const &error)
    {
        std::cerr << error.what() << "\nFixture: " << Utf8(root) << '\n';
        std::error_code ignored;
        if (!cache.empty())
            fs::remove_all(cache, ignored);
        fs::remove_all(root, ignored);
        return 1;
    }
}
