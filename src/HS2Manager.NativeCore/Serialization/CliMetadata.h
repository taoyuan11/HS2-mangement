#pragma once
#include "Common/Core.h"
#include <array>
#include <cstring>
#include <set>

namespace hs2::metadata
{
    inline void Require(bool value, const char *error)
    {
        if (!value)
            throw std::runtime_error(error);
    }

    class Cursor
    {
    public:
        std::span<const std::uint8_t> Data;
        std::size_t Position = 0;

        explicit Cursor(std::span<const std::uint8_t> data) : Data(data)
        {
        }

        std::span<const std::uint8_t> Take(std::size_t count)
        {
            Require(Position <= Data.size() && count <= Data.size() - Position, "CLI metadata is truncated");
            auto value = Data.subspan(Position, count);
            Position += count;
            return value;
        }

        template <class T> T Read()
        {
            auto bytes = Take(sizeof(T));
            T result;
            std::memcpy(&result, bytes.data(), sizeof(T));
            return result;
        }

        void Skip(std::size_t count)
        {
            Take(count);
        }

        void Seek(std::size_t offset)
        {
            Require(offset <= Data.size(), "CLI metadata offset is out of range");
            Position = offset;
        }

        std::uint32_t Index(int width)
        {
            return width == 2 ? Read<std::uint16_t>() : Read<std::uint32_t>();
        }

        void Align()
        {
            Seek((Position + 3) & ~std::size_t(3));
        }

        std::string CString()
        {
            auto start = Position;
            while (Position < Data.size() && Data[Position])
                Position++;
            Require(Position < Data.size(), "Unterminated CLI string");
            auto count = Position++ - start;
            return {reinterpret_cast<const char *>(Data.data() + start), count};
        }

        std::uint32_t Compressed()
        {
            std::uint32_t number = Read<std::uint8_t>();
            if (number & 128)
            {
                if (number & 64)
                {
                    Require(!(number & 32), "Invalid CLI compressed integer");
                    number = ((number & 31) << 24) | (std::uint32_t(Read<std::uint8_t>()) << 16) |
                             (std::uint32_t(Read<std::uint8_t>()) << 8) | Read<std::uint8_t>();
                }
                else
                    number = ((number & 63) << 8) | Read<std::uint8_t>();
            }
            return number;
        }

        std::string SerializedString()
        {
            if (Position < Data.size() && Data[Position] == 0xff)
            {
                Position++;
                return {};
            }
            auto length = Compressed();
            Require(length <= 4 * 1024 * 1024, "CLI string exceeds limit");
            auto value = Take(length);
            return {reinterpret_cast<const char *>(value.data()), value.size()};
        }
    };

    // ECMA-335 元数据读取器：只解析数据，不将程序集加载到运行时。
    class Assembly
    {
    public:
        struct Section
        {
            std::uint32_t Address, Size, Offset, RawSize;
        };

        Bytes Data;
        std::vector<Section> Sections;
        std::span<const std::uint8_t> Tables, Strings, Blobs, Resources;
        std::array<std::uint32_t, 64> Rows{}, Sizes{}, Offsets{};
        int StringSize = 2, BlobSize = 2, GuidSize = 2;
        bool Managed = false;

        std::size_t Rva(std::uint32_t address) const
        {
            for (auto section : Sections)
                if (address >= section.Address &&
                    std::uint64_t(address) - section.Address < (std::max)(section.Size, section.RawSize))
                {
                    auto delta = address - section.Address;
                    Require(delta < section.RawSize && std::uint64_t(section.Offset) + delta < Data.size(),
                            "PE RVA is not file backed");
                    return std::size_t(section.Offset) + delta;
                }
            throw std::runtime_error("PE RVA is outside sections");
        }

        int IndexSize(int table) const
        {
            return Rows[table] < 65536 ? 2 : 4;
        }

        int Coded(int bits, std::initializer_list<int> tables) const
        {
            std::uint32_t maximum = 0;
            for (int table : tables)
                maximum = (std::max)(maximum, Rows[table]);
            return maximum < (1u << (16 - bits)) ? 2 : 4;
        }

        Cursor Row(int table, std::uint32_t row) const
        {
            Require(table >= 0 && table < 64 && row >= 1 && row <= Rows[table], "CLI table row is invalid");
            auto offset = Offsets[table] + std::uint64_t(row - 1) * Sizes[table];
            Require(offset <= Tables.size() && Sizes[table] <= Tables.size() - offset, "CLI table bounds are invalid");
            return Cursor(Tables.subspan(static_cast<std::size_t>(offset), Sizes[table]));
        }

        std::string Text(std::uint32_t index) const
        {
            Require(index < Strings.size(), "CLI string index is invalid");
            Cursor cursor(Strings);
            cursor.Seek(index);
            return cursor.CString();
        }

        std::span<const std::uint8_t> Blob(std::uint32_t index) const
        {
            Require(index < Blobs.size(), "CLI blob index is invalid");
            Cursor cursor(Blobs);
            cursor.Seek(index);
            auto count = cursor.Compressed();
            return cursor.Take(count);
        }

        explicit Assembly(const fs::path &path) : Data(ReadFile(path, 256ull * 1024 * 1024))
        {
            Cursor pe(Data);
            Require(pe.Read<std::uint16_t>() == 0x5a4d, "Invalid DOS signature");
            pe.Seek(0x3c);
            auto nt = pe.Read<std::uint32_t>();
            pe.Seek(nt);
            Require(pe.Read<std::uint32_t>() == 0x4550, "Invalid PE signature");
            pe.Skip(2);
            auto sections = pe.Read<std::uint16_t>();
            pe.Skip(12);
            auto optionalSize = pe.Read<std::uint16_t>();
            pe.Skip(2);
            auto optional = pe.Position;
            auto magic = pe.Read<std::uint16_t>();
            Require(magic == 0x10b || magic == 0x20b, "Invalid PE optional header");
            auto directory = optional + (magic == 0x10b ? 96 : 112);
            if (directory + 15 * 8 > optional + optionalSize)
                return;
            pe.Seek(directory + 14 * 8);
            auto cliRva = pe.Read<std::uint32_t>(), cliSize = pe.Read<std::uint32_t>();
            if (!cliRva || cliSize < 16)
                return;
            pe.Seek(optional + optionalSize);
            for (unsigned i = 0; i < sections; i++)
            {
                pe.Skip(8);
                Section section;
                section.Size = pe.Read<std::uint32_t>();
                section.Address = pe.Read<std::uint32_t>();
                section.RawSize = pe.Read<std::uint32_t>();
                section.Offset = pe.Read<std::uint32_t>();
                pe.Skip(16);
                Sections.push_back(section);
            }
            auto cli = Rva(cliRva);
            pe.Seek(cli + 8);
            auto rootOffset = Rva(pe.Read<std::uint32_t>()),
                 metadataSize = static_cast<std::size_t>(pe.Read<std::uint32_t>());
            Require(metadataSize <= Data.size() - rootOffset, "CLI metadata exceeds PE");
            if (cliSize >= 32)
            {
                pe.Seek(cli + 24);
                auto resourceRva = pe.Read<std::uint32_t>(), resourceSize = pe.Read<std::uint32_t>();
                if (resourceRva && resourceSize)
                {
                    auto offset = Rva(resourceRva);
                    Require(resourceSize <= Data.size() - offset, "CLI resources exceed PE");
                    Resources = std::span(Data).subspan(offset, resourceSize);
                }
            }
            Cursor root(std::span(Data).subspan(rootOffset, metadataSize));
            Require(root.Read<std::uint32_t>() == 0x424a5342, "Invalid CLI metadata signature");
            root.Skip(8);
            auto versionLength = root.Read<std::uint32_t>();
            root.Skip(versionLength);
            root.Align();
            root.Skip(2);
            auto streams = root.Read<std::uint16_t>();
            for (unsigned i = 0; i < streams; i++)
            {
                auto offset = root.Read<std::uint32_t>(), size = root.Read<std::uint32_t>();
                auto name = root.CString();
                root.Align();
                Require(offset <= root.Data.size() && size <= root.Data.size() - offset, "CLI stream exceeds root");
                auto bytes = root.Data.subspan(offset, size);
                if (name == "#~" || name == "#-")
                    Tables = bytes;
                else if (name == "#Strings")
                    Strings = bytes;
                else if (name == "#Blob")
                    Blobs = bytes;
            }
            Require(!Tables.empty() && !Strings.empty() && !Blobs.empty(), "CLI metadata streams missing");
            Cursor table(Tables);
            table.Skip(6);
            auto heaps = table.Read<std::uint8_t>();
            table.Skip(1);
            StringSize = heaps & 1 ? 4 : 2;
            GuidSize = heaps & 2 ? 4 : 2;
            BlobSize = heaps & 4 ? 4 : 2;
            auto valid = table.Read<std::uint64_t>();
            table.Skip(8);
            for (int i = 0; i < 64; i++)
                if (valid & (1ull << i))
                {
                    Rows[i] = table.Read<std::uint32_t>();
                    Require(Rows[i] <= 16000000, "CLI table exceeds row limit");
                }
            auto I = [&](int t)
            {
                return IndexSize(t);
            };
            int s = StringSize, b = BlobSize, g = GuidSize, type = Coded(2, {2, 1, 27}),
                resolution = Coded(2, {0, 26, 35, 1}), constant = Coded(2, {4, 8, 23}),
                attribute = Coded(5, {6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43}),
                member = Coded(3, {2, 1, 26, 6, 27}), security = Coded(2, {2, 6, 32}), method = Coded(1, {6, 10}),
                implementation = Coded(2, {38, 35, 39});
            Sizes = {std::uint32_t(2 + s + g * 3),
                     std::uint32_t(resolution + s * 2),
                     std::uint32_t(4 + s * 2 + type + I(4) + I(6)),
                     std::uint32_t(I(4)),
                     std::uint32_t(2 + s + b),
                     std::uint32_t(I(6)),
                     std::uint32_t(8 + s + b + I(8)),
                     std::uint32_t(I(8)),
                     std::uint32_t(4 + s),
                     std::uint32_t(I(2) + type),
                     std::uint32_t(member + s + b),
                     std::uint32_t(2 + constant + b),
                     std::uint32_t(attribute + Coded(3, {6, 10}) + b),
                     std::uint32_t(Coded(1, {4, 8}) + b),
                     std::uint32_t(2 + security + b),
                     std::uint32_t(6 + I(2)),
                     std::uint32_t(4 + I(4)),
                     std::uint32_t(b),
                     std::uint32_t(I(2) + I(20)),
                     std::uint32_t(I(20)),
                     std::uint32_t(2 + s + type),
                     std::uint32_t(I(2) + I(23)),
                     std::uint32_t(I(23)),
                     std::uint32_t(2 + s + b),
                     std::uint32_t(2 + I(6) + Coded(1, {20, 23})),
                     std::uint32_t(I(2) + method * 2),
                     std::uint32_t(s),
                     std::uint32_t(b),
                     std::uint32_t(2 + Coded(1, {4, 6}) + s + I(26)),
                     std::uint32_t(4 + I(4)),
                     8,
                     4,
                     std::uint32_t(16 + b + s * 2),
                     4,
                     12,
                     std::uint32_t(12 + b * 2 + s * 2),
                     std::uint32_t(4 + I(35)),
                     std::uint32_t(12 + I(35)),
                     std::uint32_t(4 + s + b),
                     std::uint32_t(8 + s * 2 + implementation),
                     std::uint32_t(8 + s + implementation),
                     std::uint32_t(I(2) * 2),
                     std::uint32_t(4 + Coded(1, {2, 6}) + s),
                     std::uint32_t(method + b),
                     std::uint32_t(I(42) + type)};
            std::uint64_t offset = table.Position;
            for (int i = 0; i < 64; i++)
            {
                Require(!Rows[i] || Sizes[i] > 0, "Unsupported CLI metadata table");
                Require(offset <= Tables.size() && offset <= UINT32_MAX &&
                            std::uint64_t(Rows[i]) * Sizes[i] <= Tables.size() - offset,
                        "CLI table exceeds metadata stream");
                Offsets[i] = static_cast<std::uint32_t>(offset);
                offset += std::uint64_t(Rows[i]) * Sizes[i];
            }
            Managed = true;
        }

        std::string TypeName(int table, std::uint32_t row) const
        {
            if (table != 1 && table != 2)
                return {};
            auto value = Row(table, row);
            value.Skip(table == 1 ? Coded(2, {0, 26, 35, 1}) : 4);
            auto name = Text(value.Index(StringSize)), space = Text(value.Index(StringSize));
            return space.empty() ? name : space + "." + name;
        }

        std::uint32_t BaseType(std::uint32_t type) const
        {
            auto value = Row(2, type);
            value.Skip(4 + StringSize * 2);
            auto token = value.Index(Coded(2, {2, 1, 27}));
            return (token & 3) == 0 ? token >> 2 : 0;
        }

        std::pair<std::uint32_t, std::uint32_t> Lists(std::uint32_t type) const
        {
            auto value = Row(2, type);
            value.Skip(4 + StringSize * 2 + Coded(2, {2, 1, 27}));
            return {value.Index(IndexSize(4)), value.Index(IndexSize(6))};
        }

        std::pair<std::string, std::span<const std::uint8_t>> AttributeType(std::uint32_t constructor) const
        {
            int tag = constructor & 7;
            auto index = constructor >> 3;
            if (tag == 3)
            {
                auto member = Row(10, index);
                auto parent = member.Index(Coded(3, {2, 1, 26, 6, 27}));
                member.Skip(StringSize);
                auto signature = Blob(member.Index(BlobSize));
                int parentTag = parent & 7;
                return {TypeName(parentTag == 0 ? 2 : parentTag == 1 ? 1 : -1, parent >> 3), signature};
            }
            if (tag == 2)
            {
                auto method = Row(6, index);
                method.Skip(8 + StringSize);
                auto signature = Blob(method.Index(BlobSize));
                for (std::uint32_t type = 1; type <= Rows[2]; type++)
                {
                    auto first = Lists(type).second, last = type < Rows[2] ? Lists(type + 1).second : Rows[6] + 1;
                    if (index >= first && index < last)
                        return {TypeName(2, type), signature};
                }
            }
            return {};
        }

        Bytes EmbeddedResource(const std::string &name) const
        {
            if (Resources.empty())
                return {};
            for (std::uint32_t i = 1; i <= Rows[40]; i++)
            {
                auto row = Row(40, i);
                auto offset = row.Read<std::uint32_t>();
                row.Skip(4);
                auto resourceName = Text(row.Index(StringSize));
                auto implementation = row.Index(Coded(2, {38, 35, 39}));
                if (implementation || resourceName != name)
                    continue;
                Cursor resource(Resources);
                resource.Seek(offset);
                auto length = resource.Read<std::uint32_t>();
                auto value = resource.Take(length);
                return {value.begin(), value.end()};
            }
            return {};
        }
    };
}
