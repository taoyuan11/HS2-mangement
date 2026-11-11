#pragma once
#include <filesystem>
#include <span>
#include <cstdint>

namespace hs2
{
    void SavePng(const std::filesystem::path &path, int width, int height, std::span<const std::uint8_t> bgra);
    void WriteRadiance(const std::filesystem::path &path, int width, int height, std::span<const float> rgba);
    void WriteOpenExr(const std::filesystem::path &path, int width, int height, std::span<const float> rgba);
}
