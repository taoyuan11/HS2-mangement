#pragma once
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace hs2 {
struct ZipEntry {
    std::string Name;
    std::uint64_t CompressedSize=0,Size=0,LocalOffset=0;
    std::uint32_t Crc32=0;
    std::uint16_t Method=0,Flags=0;
    bool Directory=false;
};
// Read-only ZIP/ZIP64 reader. It never extracts archive-controlled paths.
// Every Read verifies the central/local metadata and uncompressed CRC32.
class ZipArchive {
public:
    explicit ZipArchive(const std::filesystem::path& path);
    const std::vector<ZipEntry>& Entries() const {return entries_;}
    const ZipEntry* Find(std::string_view name) const;
    std::vector<std::uint8_t> Read(const ZipEntry& entry,std::uint64_t maximum=512ULL*1024*1024) const;
    std::vector<std::uint8_t> Read(std::string_view name,std::uint64_t maximum=512ULL*1024*1024) const;
private:
    std::filesystem::path path_;
    std::uint64_t size_=0;
    std::filesystem::file_time_type modified_;
    std::shared_ptr<std::ifstream> input_;
    std::shared_ptr<std::mutex> inputGate_;
    std::vector<ZipEntry> entries_;
    std::vector<std::uint8_t> ReadAt(std::uint64_t offset,std::uint64_t size) const;
};
std::uint32_t Crc32(std::span<const std::uint8_t> bytes);
}
