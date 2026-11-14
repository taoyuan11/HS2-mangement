#pragma once
#include <filesystem>
#include <stop_token>
#include <vector>

namespace hs2::ui
{
    struct GameSearchLocation
    {
        std::filesystem::path Directory;
        int Depth = 0;
    };

    bool IsGameDirectory(const std::filesystem::path &directory);
    std::vector<GameSearchLocation> GameSearchLocations(const std::filesystem::path &applicationDirectory);
    std::filesystem::path FindGameDirectory(const std::vector<GameSearchLocation> &locations,
                                            std::stop_token stop = {});
}
