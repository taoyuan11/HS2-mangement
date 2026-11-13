#include "Archive/ZipArchive.h"
#include "ThirdParty/Miniz/miniz_tinfl.h"
#include <Windows.h>
#include <algorithm>
#include <array>
#include <cstring>
#include <fstream>
#include <stdexcept>

namespace hs2
{
    namespace
    {
        void Require(bool value, const char *error)
        {
            if (!value)
                throw std::runtime_error(error);
        }

        template <class T> T Read(std::span<const uint8_t> bytes, size_t offset)
        {
            Require(offset <= bytes.size() && sizeof(T) <= bytes.size() - offset, "Truncated ZIP metadata.");
            T v;
            std::memcpy(&v, bytes.data() + offset, sizeof v);
            return v;
        }

        std::string Normalize(std::string text)
        {
            std::replace(text.begin(), text.end(), '\\', '/');
            return text;
        }

        std::string DecodeName(std::span<const uint8_t> data, bool utf8)
        {
            std::string text(reinterpret_cast<const char *>(data.data()), data.size());
            if (utf8)
                return Normalize(text);
            int count = MultiByteToWideChar(437, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
            Require(count > 0 || text.empty(), "Invalid ZIP filename.");
            std::wstring wide(count, L'\0');
            MultiByteToWideChar(437, 0, text.data(), static_cast<int>(text.size()), wide.data(), count);
            count = WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()), nullptr, 0, nullptr,
                                        nullptr);
            text.resize(count);
            WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()), text.data(), count, nullptr,
                                nullptr);
            return Normalize(text);
        }

        bool SameName(std::string_view a, std::string_view b)
        {
            return a.size() == b.size() && std::equal(a.begin(), a.end(), b.begin(),
                                                      [](unsigned char a, unsigned char b)
                                                      {
                                                          return std::tolower(a) == std::tolower(b);
                                                      });
        }
    }

    uint32_t Crc32(std::span<const uint8_t> bytes)
    {
        static constexpr auto table = []()
        {
            std::array<uint32_t, 256> result{};
            for (uint32_t i = 0; i < 256; ++i)
            {
                uint32_t c = i;
                for (int bit = 0; bit < 8; ++bit)
                    c = (c >> 1) ^ ((c & 1) ? 0xedb88320u : 0);
                result[i] = c;
            }
            return result;
        }();
        uint32_t crc = UINT32_MAX;
        for (auto value : bytes)
            crc = (crc >> 8) ^ table[(crc ^ value) & 255];
        return ~crc;
    }

    std::vector<uint8_t> ZipArchive::ReadAt(uint64_t offset, uint64_t size) const
    {
        Require(offset <= size_ && size <= size_ - offset && size <= 1024ULL * 1024 * 1024,
                "ZIP read exceeds file bounds.");
        std::lock_guard lock(*inputGate_);
        auto &input = *input_;
        input.clear();
        input.seekg(static_cast<std::streamoff>(offset));
        std::vector<uint8_t> bytes(static_cast<size_t>(size));
        if (size)
            input.read(reinterpret_cast<char *>(bytes.data()), static_cast<std::streamsize>(size));
        Require(static_cast<bool>(input), "Archive changed or read was incomplete.");
        return bytes;
    }

    ZipArchive::ZipArchive(const std::filesystem::path &path)
        : path_(std::filesystem::absolute(path)), size_(std::filesystem::file_size(path_)),
          modified_(std::filesystem::last_write_time(path_)),
          input_(std::make_shared<std::ifstream>(path_, std::ios::binary)), inputGate_(std::make_shared<std::mutex>())
    {
        Require(static_cast<bool>(*input_), "Cannot open archive.");
        auto tail = ReadAt(size_ - std::min<uint64_t>(size_, 65557), std::min<uint64_t>(size_, 65557));
        Require(tail.size() >= 22, "ZIP end record is missing.");
        size_t end = tail.size();
        for (size_t p = tail.size() - 22;; --p)
        {
            if (::hs2::Read<uint32_t>(tail, p) == 0x06054b50 &&
                p + 22 + ::hs2::Read<uint16_t>(tail, p + 20) == tail.size())
            {
                end = p;
                break;
            }
            if (p == 0)
                break;
        }
        Require(end != tail.size(), "ZIP end record is invalid.");
        Require(::hs2::Read<uint16_t>(tail, end + 4) == 0 && ::hs2::Read<uint16_t>(tail, end + 6) == 0,
                "Split ZIP archives are not supported.");
        uint64_t count = ::hs2::Read<uint16_t>(tail, end + 10), centralSize = ::hs2::Read<uint32_t>(tail, end + 12),
                 centralOffset = ::hs2::Read<uint32_t>(tail, end + 16);
        auto absoluteEnd = size_ - tail.size() + end;
        if (count == 65535 || centralSize == UINT32_MAX || centralOffset == UINT32_MAX)
        {
            Require(absoluteEnd >= 20, "ZIP64 locator is missing.");
            auto locator = ReadAt(absoluteEnd - 20, 20);
            Require(::hs2::Read<uint32_t>(locator, 0) == 0x07064b50 && ::hs2::Read<uint32_t>(locator, 4) == 0 &&
                        ::hs2::Read<uint32_t>(locator, 16) == 1,
                    "Invalid ZIP64 locator.");
            auto position = ::hs2::Read<uint64_t>(locator, 8);
            auto z64 = ReadAt(position, 56);
            Require(::hs2::Read<uint32_t>(z64, 0) == 0x06064b50 && ::hs2::Read<uint64_t>(z64, 4) >= 44 &&
                        ::hs2::Read<uint32_t>(z64, 16) == 0 && ::hs2::Read<uint32_t>(z64, 20) == 0,
                    "Invalid ZIP64 directory.");
            count = ::hs2::Read<uint64_t>(z64, 32);
            centralSize = ::hs2::Read<uint64_t>(z64, 40);
            centralOffset = ::hs2::Read<uint64_t>(z64, 48);
        }
        Require(count <= 1000000 && centralSize <= 128ULL * 1024 * 1024 && centralOffset <= absoluteEnd &&
                    centralSize <= absoluteEnd - centralOffset,
                "ZIP directory exceeds limit.");
        auto central = ReadAt(centralOffset, centralSize);
        size_t cursor = 0;
        entries_.reserve(static_cast<size_t>(count));
        for (uint64_t i = 0; i < count; ++i)
        {
            auto p = std::span(central).subspan(cursor);
            Require(p.size() >= 46 && ::hs2::Read<uint32_t>(p, 0) == 0x02014b50, "Invalid ZIP central entry.");
            ZipEntry e;
            auto creator = ::hs2::Read<uint16_t>(p, 4) >> 8;
            auto attributes = ::hs2::Read<uint32_t>(p, 38);
            e.Link = (creator == 3 && ((attributes >> 16) & 0170000) == 0120000) ||
                     (creator == 0 && (attributes & FILE_ATTRIBUTE_REPARSE_POINT));
            e.Flags = ::hs2::Read<uint16_t>(p, 8);
            e.Method = ::hs2::Read<uint16_t>(p, 10);
            e.Crc32 = ::hs2::Read<uint32_t>(p, 16);
            e.CompressedSize = ::hs2::Read<uint32_t>(p, 20);
            e.Size = ::hs2::Read<uint32_t>(p, 24);
            auto nameSize = ::hs2::Read<uint16_t>(p, 28), extraSize = ::hs2::Read<uint16_t>(p, 30),
                 commentSize = ::hs2::Read<uint16_t>(p, 32);
            e.LocalOffset = ::hs2::Read<uint32_t>(p, 42);
            Require(::hs2::Read<uint16_t>(p, 34) == 0, "Split ZIP entry is not supported.");
            Require(46ULL + nameSize + extraSize + commentSize <= p.size(), "Truncated ZIP central entry.");
            e.Name = DecodeName(p.subspan(46, nameSize), (e.Flags & 2048) != 0);
            Require(e.Name.find('\0') == std::string::npos, "Invalid ZIP name.");
            e.Directory = !e.Name.empty() && e.Name.back() == '/';
            auto extra = p.subspan(46 + nameSize, extraSize);
            size_t at = 0;
            while (at < extra.size())
            {
                auto tag = ::hs2::Read<uint16_t>(extra, at), length = ::hs2::Read<uint16_t>(extra, at + 2);
                at += 4;
                Require(length <= extra.size() - at, "Truncated ZIP extra field.");
                if (tag == 1)
                {
                    auto data = extra.subspan(at, length);
                    size_t q = 0;
                    auto next = [&]()
                    {
                        auto value = ::hs2::Read<uint64_t>(data, q);
                        q += 8;
                        return value;
                    };
                    if (e.Size == UINT32_MAX)
                        e.Size = next();
                    if (e.CompressedSize == UINT32_MAX)
                        e.CompressedSize = next();
                    if (e.LocalOffset == UINT32_MAX)
                        e.LocalOffset = next();
                }
                at += length;
            }
            Require(e.LocalOffset < centralOffset, "ZIP local entry offset is invalid.");
            entries_.push_back(std::move(e));
            cursor += 46 + nameSize + extraSize + commentSize;
        }
        Require(cursor == central.size(), "Unexpected ZIP directory data.");
    }

    const ZipEntry *ZipArchive::Find(std::string_view name) const
    {
        auto normalized = Normalize(std::string(name));
        const ZipEntry *match = nullptr;
        for (auto &e : entries_)
            if (SameName(e.Name, normalized))
            {
                Require(!match, "Ambiguous ZIP entry name.");
                match = &e;
            }
        return match;
    }

    std::vector<uint8_t> ZipArchive::Read(std::string_view name, uint64_t maximum) const
    {
        auto *entry = Find(name);
        Require(entry != nullptr, "ZIP entry is missing.");
        return Read(*entry, maximum);
    }

    std::vector<uint8_t> ZipArchive::Read(const ZipEntry &e, uint64_t maximum) const
    {
        Require(std::filesystem::file_size(path_) == size_ && std::filesystem::last_write_time(path_) == modified_,
                "Archive changed during read.");
        Require(e.Size <= maximum && e.CompressedSize <= 1024ULL * 1024 * 1024, "ZIP entry exceeds allowed size.");
        Require(!(e.Flags & 1), "Encrypted ZIP entries are unsupported.");
        Require(e.Method == 0 || e.Method == 8, "Unsupported ZIP compression method.");
        auto local = ReadAt(e.LocalOffset, 30);
        Require(::hs2::Read<uint32_t>(local, 0) == 0x04034b50 && ::hs2::Read<uint16_t>(local, 6) == e.Flags &&
                    ::hs2::Read<uint16_t>(local, 8) == e.Method,
                "ZIP local entry disagrees with directory.");
        auto nameSize = ::hs2::Read<uint16_t>(local, 26), extraSize = ::hs2::Read<uint16_t>(local, 28);
        auto name = ReadAt(e.LocalOffset + 30, nameSize);
        Require(DecodeName(name, (e.Flags & 2048) != 0) == e.Name, "ZIP local filename disagrees with directory.");
        auto source = ReadAt(e.LocalOffset + 30 + nameSize + extraSize, e.CompressedSize);
        std::vector<uint8_t> output;
        if (e.Method == 0)
        {
            Require(e.Size == e.CompressedSize, "Stored ZIP size mismatch.");
            output = std::move(source);
        }
        else
        {
            output.resize(static_cast<size_t>(e.Size));
            auto n = tinfl_decompress_mem_to_mem(output.data(), output.size(), source.data(), source.size(), 0);
            Require(n != TINFL_DECOMPRESS_MEM_TO_MEM_FAILED && n == e.Size, "ZIP inflate failed.");
        }
        Require(Crc32(output) == e.Crc32, "ZIP entry CRC mismatch.");
        return output;
    }
}
