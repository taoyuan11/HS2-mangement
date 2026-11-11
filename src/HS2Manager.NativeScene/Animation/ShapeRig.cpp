#include "Animation/ShapeRig.h"
#include "Animation/Animation.h"
#include <algorithm>
#include <bit>
#include <cmath>
#include <cstring>
#include <fstream>
#include <map>
#include <sstream>
#include <stdexcept>
#include <variant>

namespace hs2
{
    namespace
    {
        void Require(bool value, const char *error)
        {
            if (!value)
                throw std::runtime_error(error);
        }

        class Cursor
        {
        public:
            std::span<const uint8_t> Data;
            size_t Position = 0;

            explicit Cursor(std::span<const uint8_t> data) : Data(data)
            {
            }

            std::span<const uint8_t> Take(size_t n)
            {
                Require(n <= Data.size() - Position, "Metadata is truncated.");
                auto p = Data.subspan(Position, n);
                Position += n;
                return p;
            }

            template <class T> T Read()
            {
                auto p = Take(sizeof(T));
                T v;
                std::memcpy(&v, p.data(), sizeof v);
                return v;
            }

            uint32_t Index(int size)
            {
                return size == 2 ? Read<uint16_t>() : Read<uint32_t>();
            }

            void Seek(size_t n)
            {
                Require(n <= Data.size(), "Metadata offset exceeds stream.");
                Position = n;
            }

            void Skip(size_t n)
            {
                Take(n);
            }

            void Align()
            {
                Seek((Position + 3) & ~size_t(3));
            }

            std::string CString()
            {
                auto start = Position;
                while (Position < Data.size() && Data[Position])
                    ++Position;
                Require(Position < Data.size(), "Metadata string is not terminated.");
                auto size = Position++ - start;
                return {reinterpret_cast<const char *>(Data.data() + start), size};
            }

            std::string String()
            {
                uint32_t size = 0;
                for (int shift = 0; shift <= 28; shift += 7)
                {
                    auto b = Read<uint8_t>();
                    Require(shift != 28 || (b & 0xf8) == 0, "Invalid shape string length.");
                    size |= (b & 127u) << shift;
                    if (!(b & 128))
                    {
                        Require(size <= 65536, "Shape string exceeds limit.");
                        auto p = Take(size);
                        return {reinterpret_cast<const char *>(p.data()), p.size()};
                    }
                }
                throw std::runtime_error("Invalid shape string length.");
            }
        };

        std::vector<uint8_t> Load(const std::filesystem::path &path)
        {
            std::ifstream input(path, std::ios::binary | std::ios::ate);
            Require(static_cast<bool>(input), "Shape metadata assembly is missing.");
            auto size = input.tellg();
            Require(size > 0 && size <= 256LL * 1024 * 1024, "Shape metadata assembly exceeds limits.");
            std::vector<uint8_t> bytes(static_cast<size_t>(size));
            input.seekg(0);
            input.read(reinterpret_cast<char *>(bytes.data()), bytes.size());
            Require(static_cast<bool>(input), "Shape metadata assembly read failed.");
            return bytes;
        }

        std::vector<std::string> Split(std::string_view text, char delimiter)
        {
            std::vector<std::string> out;
            size_t start = 0;
            for (size_t i = 0; i <= text.size(); ++i)
                if (i == text.size() || text[i] == delimiter)
                {
                    out.emplace_back(text.substr(start, i - start));
                    start = i + 1;
                }
            return out;
        }
    }

    struct NativeShapeRig::Metadata
    {
        struct Section
        {
            uint32_t Address, Size, Offset, RawSize;
        };

        std::vector<uint8_t> Bytes;
        std::vector<Section> Sections;
        std::span<const uint8_t> Tables, Strings, Blobs;
        std::array<uint32_t, 64> Rows{}, Sizes{}, Offsets{};
        int StringSize = 2, BlobSize = 2, GuidSize = 2;

        size_t Rva(uint32_t address) const
        {
            for (auto s : Sections)
                if (address >= s.Address && uint64_t(address) - s.Address < std::max(s.Size, s.RawSize))
                {
                    auto delta = address - s.Address;
                    Require(delta < s.RawSize && uint64_t(s.Offset) + delta < Bytes.size(),
                            "PE virtual data is not file backed.");
                    return size_t(s.Offset) + delta;
                }
            throw std::runtime_error("PE RVA is outside sections.");
        }

        int IndexSize(int table) const
        {
            return Rows[table] < 65536 ? 2 : 4;
        }

        int Coded(int bits, std::initializer_list<int> tables) const
        {
            uint32_t max = 0;
            for (int table : tables)
                max = std::max(max, Rows[table]);
            return max < (1u << (16 - bits)) ? 2 : 4;
        }

        Cursor Row(int table, uint32_t row) const
        {
            Require(row >= 1 && row <= Rows[table], "CLI table row is invalid.");
            auto offset = Offsets[table] + uint64_t(row - 1) * Sizes[table];
            Require(offset <= Tables.size() && Sizes[table] <= Tables.size() - offset, "CLI table bounds are invalid.");
            return Cursor(Tables.subspan(static_cast<size_t>(offset), Sizes[table]));
        }

        std::string Text(uint32_t index) const
        {
            Require(index < Strings.size(), "CLI string index is invalid.");
            Cursor c(Strings);
            c.Seek(index);
            return c.CString();
        }

        std::span<const uint8_t> Blob(uint32_t index) const
        {
            Require(index < Blobs.size(), "CLI blob index is invalid.");
            Cursor c(Blobs);
            c.Seek(index);
            uint32_t size = c.Read<uint8_t>();
            if (size & 128)
            {
                if (size & 64)
                {
                    Require(!(size & 32), "Invalid compressed CLI integer.");
                    size = ((size & 31) << 24) | (uint32_t(c.Read<uint8_t>()) << 16) |
                           (uint32_t(c.Read<uint8_t>()) << 8) | c.Read<uint8_t>();
                }
                else
                    size = ((size & 63) << 8) | c.Read<uint8_t>();
            }
            return c.Take(size);
        }

        explicit Metadata(const std::filesystem::path &path) : Bytes(Load(path))
        {
            Cursor pe(Bytes);
            Require(pe.Read<uint16_t>() == 0x5a4d, "Invalid PE signature.");
            pe.Seek(0x3c);
            auto nt = pe.Read<uint32_t>();
            pe.Seek(nt);
            Require(pe.Read<uint32_t>() == 0x4550, "Invalid NT signature.");
            pe.Skip(2);
            auto sections = pe.Read<uint16_t>();
            pe.Skip(12);
            auto optionalSize = pe.Read<uint16_t>();
            pe.Skip(2);
            auto optional = pe.Position;
            auto magic = pe.Read<uint16_t>();
            Require(magic == 0x10b || magic == 0x20b, "Unsupported PE optional header.");
            auto directory = optional + (magic == 0x10b ? 96 : 112);
            Require(directory + 15 * 8 <= optional + optionalSize, "PE CLI directory is missing.");
            pe.Seek(directory + 14 * 8);
            auto cliRva = pe.Read<uint32_t>();
            auto cliSize = pe.Read<uint32_t>();
            Require(cliRva && cliSize >= 16, "PE CLI metadata is missing.");
            pe.Seek(optional + optionalSize);
            for (unsigned i = 0; i < sections; ++i)
            {
                pe.Skip(8);
                Section s;
                s.Size = pe.Read<uint32_t>();
                s.Address = pe.Read<uint32_t>();
                s.RawSize = pe.Read<uint32_t>();
                s.Offset = pe.Read<uint32_t>();
                pe.Skip(16);
                Sections.push_back(s);
            }
            pe.Seek(Rva(cliRva) + 8);
            auto metadata = Rva(pe.Read<uint32_t>());
            auto metadataSize = pe.Read<uint32_t>();
            Require(metadataSize <= Bytes.size() - metadata, "CLI metadata exceeds PE.");
            Cursor root(std::span(Bytes).subspan(metadata, metadataSize));
            Require(root.Read<uint32_t>() == 0x424a5342, "CLI metadata signature is invalid.");
            root.Skip(8);
            auto versionLength = root.Read<uint32_t>();
            root.Skip(versionLength);
            root.Align();
            root.Skip(2);
            auto streams = root.Read<uint16_t>();
            for (unsigned i = 0; i < streams; ++i)
            {
                auto offset = root.Read<uint32_t>(), size = root.Read<uint32_t>();
                auto name = root.CString();
                root.Align();
                Require(offset <= root.Data.size() && size <= root.Data.size() - offset,
                        "CLI metadata stream exceeds root.");
                auto data = root.Data.subspan(offset, size);
                if (name == "#~" || name == "#-")
                    Tables = data;
                else if (name == "#Strings")
                    Strings = data;
                else if (name == "#Blob")
                    Blobs = data;
            }
            Require(!Tables.empty() && !Strings.empty() && !Blobs.empty(), "CLI metadata streams are missing.");
            Cursor table(Tables);
            table.Skip(6);
            auto heaps = table.Read<uint8_t>();
            table.Skip(1);
            StringSize = (heaps & 1) ? 4 : 2;
            GuidSize = (heaps & 2) ? 4 : 2;
            BlobSize = (heaps & 4) ? 4 : 2;
            auto valid = table.Read<uint64_t>();
            table.Skip(8);
            for (int i = 0; i < 64; ++i)
                if (valid & (1ULL << i))
                {
                    Rows[i] = table.Read<uint32_t>();
                    Require(Rows[i] <= 16000000, "CLI table exceeds row limit.");
                }
            auto I = [&](int t)
            {
                return IndexSize(t);
            };
            int s = StringSize, b = BlobSize, g = GuidSize;
            int type = Coded(2, {2, 1, 27}), resolution = Coded(2, {0, 26, 35, 1}), constant = Coded(2, {4, 8, 23}),
                attribute = Coded(5, {6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43}),
                member = Coded(3, {2, 1, 26, 6, 27}), security = Coded(2, {2, 6, 32}), method = Coded(1, {6, 10}),
                implementation = Coded(2, {38, 35, 39});
            Sizes = {uint32_t(2 + s + g * 3),
                     uint32_t(resolution + s * 2),
                     uint32_t(4 + s * 2 + type + I(4) + I(6)),
                     uint32_t(I(4)),
                     uint32_t(2 + s + b),
                     uint32_t(I(6)),
                     uint32_t(8 + s + b + I(8)),
                     uint32_t(I(8)),
                     uint32_t(4 + s),
                     uint32_t(I(2) + type),
                     uint32_t(member + s + b),
                     uint32_t(2 + constant + b),
                     uint32_t(attribute + Coded(3, {6, 10}) + b),
                     uint32_t(Coded(1, {4, 8}) + b),
                     uint32_t(2 + security + b),
                     uint32_t(6 + I(2)),
                     uint32_t(4 + I(4)),
                     uint32_t(b),
                     uint32_t(I(2) + I(20)),
                     uint32_t(I(20)),
                     uint32_t(2 + s + type),
                     uint32_t(I(2) + I(23)),
                     uint32_t(I(23)),
                     uint32_t(2 + s + b),
                     uint32_t(2 + I(6) + Coded(1, {20, 23})),
                     uint32_t(I(2) + method * 2),
                     uint32_t(s),
                     uint32_t(b),
                     uint32_t(2 + Coded(1, {4, 6}) + s + I(26)),
                     uint32_t(4 + I(4)),
                     8,
                     4,
                     uint32_t(16 + b + s * 2),
                     4,
                     12,
                     uint32_t(12 + b * 2 + s * 2),
                     uint32_t(4 + I(35)),
                     uint32_t(12 + I(35)),
                     uint32_t(4 + s + b),
                     uint32_t(8 + s * 2 + implementation),
                     uint32_t(8 + s + implementation),
                     uint32_t(I(2) * 2),
                     uint32_t(4 + Coded(1, {2, 6}) + s),
                     uint32_t(method + b),
                     uint32_t(I(42) + type)};
            uint64_t offset = table.Position;
            for (int i = 0; i < 64; ++i)
            {
                Require(!Rows[i] || Sizes[i] > 0, "Unsupported CLI metadata table.");
                Require(offset <= UINT32_MAX && uint64_t(Rows[i]) * Sizes[i] <= Tables.size() - offset,
                        "CLI table exceeds metadata stream.");
                Offsets[i] = static_cast<uint32_t>(offset);
                offset += uint64_t(Rows[i]) * Sizes[i];
            }
        }

        std::string Member(uint32_t token) const
        {
            int table = token >> 24;
            auto row = Row(table, token & 0xffffff);
            if (table == 4)
                row.Skip(2);
            else if (table == 6)
                row.Skip(8);
            else if (table == 10)
                row.Skip(Coded(3, {2, 1, 26, 6, 27}));
            else
                throw std::runtime_error("Unsupported shape metadata member token.");
            return Text(row.Index(StringSize));
        }

        uint32_t FindType(std::string_view name) const
        {
            for (uint32_t i = 1; i <= Rows[2]; ++i)
            {
                auto r = Row(2, i);
                r.Skip(4);
                if (Text(r.Index(StringSize)) == name)
                    return i;
            }
            throw std::runtime_error("Shape formula type is missing: " + std::string(name));
        }

        std::pair<uint32_t, uint32_t> Lists(uint32_t type) const
        {
            auto r = Row(2, type);
            r.Skip(4 + 2 * StringSize + Coded(2, {2, 1, 27}));
            return {r.Index(IndexSize(4)), r.Index(IndexSize(6))};
        }

        std::unordered_map<int, std::string> Enum(uint32_t outer, std::string_view name) const
        {
            uint32_t nested = 0;
            for (uint32_t i = 1; i <= Rows[41]; ++i)
            {
                auto r = Row(41, i);
                auto child = r.Index(IndexSize(2)), parent = r.Index(IndexSize(2));
                if (parent != outer)
                    continue;
                auto t = Row(2, child);
                t.Skip(4);
                if (Text(t.Index(StringSize)) == name)
                {
                    nested = child;
                    break;
                }
            }
            Require(nested != 0, "Shape enum is missing.");
            auto first = Lists(nested).first, last = nested < Rows[2] ? Lists(nested + 1).first : Rows[4] + 1;
            std::unordered_map<int, std::string> output;
            for (uint32_t i = 1; i <= Rows[11]; ++i)
            {
                auto r = Row(11, i);
                auto type = r.Read<uint16_t>();
                auto parent = r.Index(Coded(2, {4, 8, 23}));
                auto blob = r.Index(BlobSize);
                if ((parent & 3) != 0 || (parent >> 2) < first || (parent >> 2) >= last)
                    continue;
                Require(type == 8, "Shape enum constant is not Int32.");
                Cursor value(Blob(blob));
                int id = value.Read<int32_t>();
                auto field = Row(4, parent >> 2);
                field.Skip(2);
                Require(output.emplace(id, Text(field.Index(StringSize))).second, "Duplicate shape enum value.");
            }
            return output;
        }

        std::span<const uint8_t> Update(uint32_t type) const
        {
            auto first = Lists(type).second, last = type < Rows[2] ? Lists(type + 1).second : Rows[6] + 1;
            for (auto i = first; i < last; ++i)
            {
                auto r = Row(6, i);
                auto address = r.Read<uint32_t>();
                r.Skip(4);
                if (Text(r.Index(StringSize)) != "Update")
                    continue;
                Cursor body(Bytes);
                body.Seek(Rva(address));
                auto start = body.Position;
                auto flags = body.Read<uint8_t>();
                if ((flags & 3) == 2)
                    return body.Take(flags >> 2);
                Require((flags & 3) == 3, "Unsupported shape method body format.");
                body.Seek(start);
                auto header = body.Read<uint16_t>();
                Require((header >> 12) >= 3, "Invalid shape method header.");
                body.Skip(2);
                auto size = body.Read<uint32_t>();
                Require(size <= 128 * 1024, "Shape method exceeds size limit.");
                body.Seek(start + (header >> 12) * 4);
                return body.Take(size);
            }
            throw std::runtime_error("Shape update method is missing.");
        }
    };

    NativeShapeRig::NativeShapeRig(const std::filesystem::path &game, bool unlocked)
        : metadata_(std::make_shared<Metadata>(game / "HoneySelect2_Data/Managed/Assembly-CSharp.dll")),
          unlocked_(unlocked)
    {
    }

    NativeShapeRig::Keys NativeShapeRig::ReadKeys(std::span<const uint8_t> bytes)
    {
        Cursor r(bytes);
        auto count = r.Read<int32_t>();
        Require(count >= 1 && count <= 4096, "Shape data count exceeds limit.");
        Keys result;
        for (int i = 0; i < count; ++i)
        {
            auto name = r.String();
            auto frames = r.Read<int32_t>();
            Require(name.size() <= 256 && frames >= 1 && frames <= 1024 &&
                        uint64_t(frames) * 40 <= bytes.size() - r.Position,
                    "Shape keyframes exceed data bounds.");
            std::vector<std::array<float, 9>> keys;
            for (int j = 0; j < frames; ++j)
            {
                r.Skip(4);
                std::array<float, 9> key;
                for (auto &v : key)
                {
                    v = r.Read<float>();
                    Require(std::isfinite(v), "Non-finite shape keyframe.");
                }
                keys.push_back(key);
            }
            Require(result.emplace(name, std::move(keys)).second, "Duplicate shape source name.");
        }
        return result;
    }

    float NativeShapeRig::Interpolate(std::span<const std::array<float, 9>> frames, std::string_view name, float value,
                                      int component, bool unlocked)
    {
        Require(!frames.empty() && component >= 0 && component < 9 && std::isfinite(value),
                "Invalid shape interpolation input.");
        float rate = std::clamp(value, 0.f, 1.f) * (frames.size() - 1);
        size_t a = static_cast<size_t>(rate), b = std::min(a + 1, frames.size() - 1);
        float delta = frames[b][component] - frames[a][component];
        bool rotation = component >= 3 && component < 6;
        if (rotation)
            delta = std::fmod(std::fmod(delta, 360.f) + 540, 360.f) - 180;
        float result = frames[a][component] + delta * (rate - a);
        if (!unlocked || (value >= 0 && value <= 1) || frames.size() < 2)
            return result;
        if (rotation && (name.find("cf_s_Mune") != name.npos || name.find("cf_s_Mouth") != name.npos ||
                         name.find("cf_s_LegLow") != name.npos || name.find("cf_s_MayuTip") != name.npos ||
                         (name.find("thigh") != name.npos && name.find("01") != name.npos) ||
                         (name.starts_with("cf_a_bust") && name.ends_with("_size"))))
            return result;
        float span = frames.back()[component] - frames[0][component];
        if (rotation)
        {
            bool increasing = frames[1][component] - frames[0][component] >= 0;
            if (span > 0 && !increasing)
                span -= 360;
            else if (span < 0 && increasing)
                span += 360;
        }
        float extended = frames[0][component] + span * value;
        Require(std::isfinite(extended), "Extended shape interpolation overflows.");
        return extended;
    }

    void NativeShapeRig::Apply(std::unordered_map<std::string, BonePose> &bones, std::string_view recipe,
                               std::span<const uint8_t> animation, std::string_view categories,
                               std::span<const float> values, std::stop_token cancellation) const
    {
        auto type = metadata_->FindType(recipe);
        auto sources = metadata_->Enum(type, "SrcName"), destinations = metadata_->Enum(type, "DstName");
        auto keys = ReadKeys(animation);
        std::unordered_map<std::string, int> sourceIds;
        std::unordered_map<int, std::array<float, 9>> parameters;
        for (auto &[id, name] : sources)
        {
            sourceIds[name] = id;
            parameters[id] = {0, 0, 0, 0, 0, 0, 1, 1, 1};
        }
        for (auto &row : Split(categories, '\n'))
        {
            if (cancellation.stop_requested())
                throw std::runtime_error("Shape preparation cancelled.");
            auto cells = Split(row, '\t');
            if (cells.size() < 11)
                continue;
            size_t parsed = 0;
            int index;
            try
            {
                index = std::stoi(cells[0], &parsed);
            }
            catch (...)
            {
                continue;
            }
            if (parsed != cells[0].size() || index < 0 || static_cast<size_t>(index) >= values.size())
                continue;
            auto source = sourceIds.find(cells[1]);
            auto frames = keys.find(cells[1]);
            if (source == sourceIds.end() || frames == keys.end())
                continue;
            for (int i = 0; i < 9; ++i)
                if (cells[i + 2] != "0")
                    parameters[source->second][i] = Interpolate(frames->second, cells[1], values[index], i, unlocked_);
        }
        std::unordered_map<int, BonePose *> targets;
        std::map<int, BonePose> unused;
        for (auto &[id, name] : destinations)
        {
            auto p = bones.find(name);
            targets[id] = p == bones.end() ? &unused[id] : &p->second;
        }
        enum class Tag
        {
            Self,
            Sources,
            Targets
        };

        struct Shape
        {
            int Id;
        };

        struct Vector
        {
            int Id, Offset;
        };

        using Value = std::variant<float, Tag, Shape, Vector, BonePose *>;
        std::vector<Value> stack;
        std::array<Value, 256> locals{};
        Cursor il(metadata_->Update(type));
        int steps = 0;
        auto pop = [&]()
        {
            Require(!stack.empty(), "Shape operand stack underflow.");
            auto value = stack.back();
            stack.pop_back();
            return value;
        };
        auto number = [&]()
        {
            auto value = pop();
            Require(std::holds_alternative<float>(value), "Shape operand is not numeric.");
            return std::get<float>(value);
        };
        auto push = [&](float n)
        {
            Require(std::isfinite(n), "Shape formula produced non-finite value.");
            stack.emplace_back(n);
        };
        auto branch = [&](int offset)
        {
            auto next = int64_t(il.Position) + offset;
            Require(next >= 0 && uint64_t(next) <= il.Data.size(), "Shape branch exceeds method.");
            il.Seek(static_cast<size_t>(next));
        };
        while (il.Position < il.Data.size())
        {
            Require(++steps <= 100000 && stack.size() < 1024, "Shape evaluation exceeds limits.");
            if ((steps & 1023) == 0 && cancellation.stop_requested())
                throw std::runtime_error("Shape preparation cancelled.");
            int op = il.Read<uint8_t>();
            if (op == 0xfe)
                op = 0xfe00 | il.Read<uint8_t>();
            if (op == 0x2a)
                return;
            if (op == 0)
                continue;
            if (op == 2)
            {
                stack.emplace_back(Tag::Self);
                continue;
            }
            if (op >= 0x15 && op <= 0x1e)
            {
                push(float(op - 0x16));
                continue;
            }
            if (op == 0x1f)
            {
                push(il.Read<int8_t>());
                continue;
            }
            if (op == 0x20)
            {
                push(static_cast<float>(il.Read<int32_t>()));
                continue;
            }
            if (op == 0x22)
            {
                push(il.Read<float>());
                continue;
            }
            if ((op >= 6 && op <= 9) || op == 0x11 || op == 0x12 || op == 0xfe0c || op == 0xfe0d)
            {
                int index = op >= 6 && op <= 9 ? op - 6 : op < 0xfe00 ? il.Read<uint8_t>() : il.Read<uint16_t>();
                Require(index < 256, "Shape local index exceeds limit.");
                stack.push_back(locals[index]);
                continue;
            }
            if ((op >= 0xa && op <= 0xd) || op == 0x13 || op == 0xfe0e)
            {
                int index = op >= 0xa && op <= 0xd ? op - 0xa : op == 0x13 ? il.Read<uint8_t>() : il.Read<uint16_t>();
                Require(index < 256, "Shape local index exceeds limit.");
                locals[index] = pop();
                continue;
            }
            if (op == 0x25)
            {
                Require(!stack.empty(), "Shape dup stack underflow.");
                stack.push_back(stack.back());
                continue;
            }
            if (op == 0x26)
            {
                pop();
                continue;
            }
            if (op == 0x58 || op == 0x59 || op == 0x5a || op == 0x5b || op == 0x5f)
            {
                float b = number(), a = number();
                push(op == 0x58   ? a + b
                     : op == 0x59 ? a - b
                     : op == 0x5a ? a * b
                     : op == 0x5b ? a / b
                                  : float(int(a) & int(b)));
                continue;
            }
            if (op == 0x65)
            {
                push(-number());
                continue;
            }
            if (op == 0x2c || op == 0x2d || op == 0x39 || op == 0x3a)
            {
                int offset = op <= 0x2d ? il.Read<int8_t>() : il.Read<int32_t>();
                bool condition = number() != 0;
                if (condition == (op == 0x2d || op == 0x3a))
                    branch(offset);
                continue;
            }
            if (op == 0x7b || op == 0x7c)
            {
                auto member = metadata_->Member(il.Read<uint32_t>());
                auto instance = pop();
                if (member == "dictSrc" && std::get<Tag>(instance) == Tag::Self)
                    stack.emplace_back(Tag::Sources);
                else if (member == "dictDst" && std::get<Tag>(instance) == Tag::Self)
                    stack.emplace_back(Tag::Targets);
                else if (member == "updateMask")
                    push(31);
                else if (member == "trfBone")
                    stack.push_back(instance);
                else if (member == "vctPos" || member == "vctRot" || member == "vctScl")
                    stack.emplace_back(Vector{std::get<Shape>(instance).Id, member == "vctPos"   ? 0
                                                                            : member == "vctRot" ? 3
                                                                                                 : 6});
                else if (member == "x" || member == "y" || member == "z")
                {
                    auto v = std::get<Vector>(instance);
                    push(parameters.at(v.Id)[v.Offset + (member == "x" ? 0 : member == "y" ? 1 : 2)]);
                }
                else
                    throw std::runtime_error("Unsupported shape field: " + member);
                continue;
            }
            if (op == 0x28 || op == 0x6f)
            {
                auto member = metadata_->Member(il.Read<uint32_t>());
                if (member == "get_InitEnd")
                {
                    Require(std::get<Tag>(pop()) == Tag::Self, "Invalid shape getter receiver.");
                    push(1);
                    continue;
                }
                if (member == "get_Count")
                {
                    pop();
                    push(static_cast<float>(parameters.size()));
                    continue;
                }
                if (member == "get_Item")
                {
                    auto id = static_cast<int>(number());
                    auto dictionary = std::get<Tag>(pop());
                    if (dictionary == Tag::Sources)
                    {
                        Require(parameters.contains(id), "Shape source key is missing.");
                        stack.emplace_back(Shape{id});
                    }
                    else
                    {
                        Require(dictionary == Tag::Targets && targets.contains(id),
                                "Shape destination key is missing.");
                        stack.emplace_back(targets.at(id));
                    }
                    continue;
                }
                if (member == "SetLocalPosition" || member == "SetLocalRotation" || member == "SetLocalScale")
                {
                    float z = number(), y = number(), x = number();
                    auto *target = std::get<BonePose *>(pop());
                    if (member == "SetLocalRotation")
                        target->Rotation = EulerRotation({x, y, z});
                    else if (member == "SetLocalScale")
                        target->Scale = {x, y, z};
                    else
                        target->Position = {x, y, z};
                    continue;
                }
                if (member == "SetLocalPositionX" || member == "SetLocalPositionY" || member == "SetLocalPositionZ")
                {
                    float value = number();
                    auto *target = std::get<BonePose *>(pop());
                    (member.back() == 'X'   ? target->Position.x
                     : member.back() == 'Y' ? target->Position.y
                                            : target->Position.z) = value;
                    continue;
                }
                throw std::runtime_error("Unsupported shape operation: " + member);
            }
            throw std::runtime_error("Unsupported numeric shape instruction: " + std::to_string(op));
        }
        throw std::runtime_error("Shape formula ended without return.");
    }
}
