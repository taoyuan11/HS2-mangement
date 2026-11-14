#include "Infrastructure/GameDiscovery.h"
#include "Common/Core.h"
#include <iostream>

using namespace hs2;
using namespace hs2::ui;

namespace
{
    void Check(bool condition, const char *message)
    {
        if (!condition)
            throw std::runtime_error(message);
    }

    void Game(const fs::path &directory)
    {
        fs::create_directories(directory / L"HoneySelect2_Data");
        fs::create_directories(directory / L"abdata");
        WriteAtomic(directory / L"HoneySelect2.exe", Bytes{0});
    }
}

int main()
{
    auto root = fs::temp_directory_path() / L"HS2GameDiscoveryChecks" / Path(NewId());
    int result = 0;
    try
    {
        auto game = root / L"游戏合集" / L"非默认安装目录";
        Game(game);
        Check(IsGameDirectory(game), "valid game without UserData was rejected");
        Check(!IsGameDirectory({}) && !IsGameDirectory(L"."), "empty or relative path accepted");
        Check(!IsGameDirectory(game / L"abdata"), "resource subdirectory accepted as game root");
        auto incomplete = root / L"incomplete";
        fs::create_directories(incomplete / L"HoneySelect2.exe");
        fs::create_directories(incomplete / L"HoneySelect2_Data");
        fs::create_directories(incomplete / L"abdata");
        Check(!IsGameDirectory(incomplete), "executable directory accepted as an executable file");
        WriteAtomic(root / L"only-exe" / L"HoneySelect2.exe", Bytes{0});
        Check(!IsGameDirectory(root / L"only-exe"), "game without resources accepted");
        Check(FindGameDirectory({{incomplete, 0}, {root / L"missing", 2}}).empty(),
              "missing or incomplete games did not request manual selection");
        Check(SamePath(FindGameDirectory({{root / L"missing", 3}, {root, 2}}), game),
              "non-default Unicode game directory was not discovered");
        Check(FindGameDirectory({{root, 1}}).empty(), "search exceeded its depth limit");
        Check(SamePath(FindGameDirectory({{root, 0}, {root, 2}}), game), "shallow duplicate prevented deeper search");
        auto other = root / L"other-install";
        Game(other);
        Check(SamePath(FindGameDirectory({{game, 0}, {other, 0}}), game), "candidate priority changed");
        std::stop_source stop;
        stop.request_stop();
        Check(FindGameDirectory({{game, 0}}, stop.get_token()).empty(), "cancelled search returned a game");
        auto ignored = root / L"excluded" / L"mods" / L"copy";
        Game(ignored);
        Check(FindGameDirectory({{root / L"excluded", 3}}).empty(), "search traversed game resource trees");
        AppSettings settings;
        settings.GamePath = Utf8(game);
        settings.Theme = "Dark";
        settings.ExtraRoots = {Utf8(root / L"cards")};
        settings.Save(root / L"Data");
        auto restored = AppSettings::Load(root / L"Data");
        Check(restored.GamePath == settings.GamePath && restored.Theme == "Dark" &&
                  restored.ExtraRoots == settings.ExtraRoots,
              "detected game path or existing preferences were not persisted");
        std::cout << "PASS game discovery: custom paths, validation, missing games, depth, cancellation, persistence\n";
    }
    catch (std::exception const &error)
    {
        std::cerr << error.what() << '\n';
        result = 1;
    }
    std::error_code error;
    fs::remove_all(root, error);
    return result;
}
