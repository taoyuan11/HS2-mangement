#include "Assets/AssetReader.h"
#include "ThirdParty/Lzma/LzmaDec.h"
#include <algorithm>
#include <bit>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <limits>
#include <set>
#include <sstream>
#include <stdexcept>

namespace hs2
{
    namespace
    {
        constexpr std::uint64_t MaxResource = 1024ULL * 1024 * 1024;

        void Require(bool value, const char *error)
        {
            if (!value)
                throw std::runtime_error(error);
        }

        class Reader
        {
        public:
            std::span<const std::uint8_t> Data;
            size_t Position = 0;
            bool Big = false;

            explicit Reader(std::span<const std::uint8_t> data, bool big = false) : Data(data), Big(big)
            {
            }

            void Seek(size_t p)
            {
                Require(p <= Data.size(), "Serialized offset exceeds stream.");
                Position = p;
            }

            std::span<const std::uint8_t> Take(size_t n)
            {
                Require(n <= Data.size() - Position, "Serialized data is truncated.");
                auto out = Data.subspan(Position, n);
                Position += n;
                return out;
            }

            void Skip(size_t n)
            {
                Take(n);
            }

            void Align(size_t alignment = 4)
            {
                Require(alignment && !(alignment & (alignment - 1)), "Invalid stream alignment.");
                Seek((Position + alignment - 1) & ~(alignment - 1));
            }

            template <class T> T Read()
            {
                auto bytes = Take(sizeof(T));
                T value;
                std::memcpy(&value, bytes.data(), sizeof(T));
                if (Big && sizeof(T) > 1)
                {
                    auto *p = reinterpret_cast<std::uint8_t *>(&value);
                    std::reverse(p, p + sizeof(T));
                }
                return value;
            }

            size_t Count(size_t max = 1000000)
            {
                auto n = Read<std::int32_t>();
                Require(n >= 0 && static_cast<size_t>(n) <= max, "Serialized count exceeds limit.");
                return static_cast<size_t>(n);
            }

            std::string CString(size_t max = 65536)
            {
                auto begin = Position;
                while (Position < Data.size() && Data[Position])
                {
                    Require(Position - begin < max, "Serialized string exceeds limit.");
                    ++Position;
                }
                Require(Position < Data.size(), "Unterminated serialized string.");
                auto n = Position++ - begin;
                return {reinterpret_cast<const char *>(Data.data() + begin), n};
            }

            std::string String()
            {
                auto size = Count(16 * 1024 * 1024);
                auto b = Take(size);
                return {reinterpret_cast<const char *>(b.data()), b.size()};
            }

            std::string DotNetString()
            {
                std::uint32_t size = 0;
                for (int shift = 0; shift <= 28; shift += 7)
                {
                    auto b = Read<std::uint8_t>();
                    Require(shift != 28 || (b & 0xf8) == 0, "Invalid TPK string length.");
                    size |= (b & 127u) << shift;
                    if (!(b & 128))
                    {
                        Require(size <= 65536, "TPK string exceeds limit.");
                        auto p = Take(size);
                        return {reinterpret_cast<const char *>(p.data()), p.size()};
                    }
                }
                throw std::runtime_error("Invalid TPK string length.");
            }
        };

        std::vector<std::uint8_t> ReadFile(const std::filesystem::path &path)
        {
            std::ifstream input(path, std::ios::binary | std::ios::ate);
            if (!input)
            {
                auto text = path.u8string();
                throw std::runtime_error("Cannot read asset file: " + std::string(text.begin(), text.end()));
            }
            auto size = input.tellg();
            Require(size >= 0 && static_cast<uint64_t>(size) <= MaxResource, "Asset file exceeds limit.");
            std::vector<uint8_t> data(static_cast<size_t>(size));
            input.seekg(0);
            if (!data.empty())
                input.read(reinterpret_cast<char *>(data.data()), data.size());
            Require(static_cast<bool>(input), "Asset file read was incomplete.");
            return data;
        }

        std::string Basename(std::string value)
        {
            std::replace(value.begin(), value.end(), '\\', '/');
            auto i = value.find_last_of('/');
            return i == std::string::npos ? value : value.substr(i + 1);
        }

        bool EqualIgnoreCase(std::string_view a, std::string_view b)
        {
            return a.size() == b.size() && std::equal(a.begin(), a.end(), b.begin(),
                                                      [](unsigned char x, unsigned char y)
                                                      {
                                                          return std::tolower(x) == std::tolower(y);
                                                      });
        }

        std::string Lower(std::string value)
        {
            for (auto &c : value)
                c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
            return value;
        }

        std::uint64_t Version(std::string_view text)
        {
            unsigned major = 0, minor = 0, patch = 0, build = 0;
            char type = '?';
            std::istringstream s{std::string(text)};
            char dot;
            s >> major >> dot >> minor >> dot >> patch >> type >> build;
            unsigned k = type == 'a'   ? 0
                         : type == 'b' ? 1
                         : type == 'c' ? 2
                         : type == 'f' ? 3
                         : type == 'p' ? 4
                         : type == 'x' ? 5
                                       : 255;
            return (uint64_t(major) << 48) | (uint64_t(minor) << 32) | (uint64_t(patch) << 16) | (k << 8) | build;
        }

        std::vector<std::uint8_t> Bytes(const Parameter &p)
        {
            if (!p.Bytes.empty())
                return p.Bytes;
            if (p.Has("Array"))
                return Bytes(p.Get("Array"));
            std::vector<uint8_t> out;
            for (auto &v : p.Children)
                out.push_back(static_cast<uint8_t>(v.Number));
            return out;
        }

        Vector2 V2(const Parameter &p)
        {
            return {p.Float("x"), p.Float("y")};
        }

        Vector3 V3(const Parameter &p)
        {
            return {p.Float("x"), p.Float("y"), p.Float("z")};
        }

        Vector4 V4(const Parameter &p, bool color = false)
        {
            return color ? Vector4{p.Float("r"), p.Float("g"), p.Float("b"), p.Float("a")}
                         : Vector4{p.Float("x"), p.Float("y"), p.Float("z"), p.Float("w")};
        }

        // Unity 格式规定的全局字符串表；
        // 偏移量指向以空字符结尾的拼接字节流，而非列表索引。
        constexpr char CommonStrings[] =
            "AABB\0AnimationClip\0AnimationCurve\0AnimationState\0Array\0Base\0BitField\0bitset\0bool\0char\0ColorRGBA"
            "\0Component\0data\0deque\0double\0dynamic_"
            "array\0FastPropertyName\0first\0float\0Font\0GameObject\0Generic "
            "Mono\0GradientNEW\0GUID\0GUIStyle\0int\0list\0long "
            "long\0map\0Matrix4x4f\0MdFour\0MonoBehaviour\0MonoScript\0m_ByteSize\0m_Curve\0m_EditorClassIdentifier\0m_"
            "EditorHideFlags\0m_Enabled\0m_ExtensionPtr\0m_GameObject\0m_Index\0m_IsArray\0m_IsStatic\0m_MetaFlag\0m_"
            "Name\0m_ObjectHideFlags\0m_PrefabInternal\0m_PrefabParentObject\0m_Script\0m_StaticEditorFlags\0m_Type\0m_"
            "Version\0Object\0pair\0PPtr<Component>\0PPtr<GameObject>\0PPtr<Material>\0PPtr<MonoBehaviour>\0PPtr<"
            "MonoScript>\0PPtr<Object>\0PPtr<Prefab>\0PPtr<Sprite>\0PPtr<TextAsset>\0PPtr<Texture>\0PPtr<Texture2D>"
            "\0PPtr<Transform>"
            "\0Prefab\0Quaternionf\0Rectf\0RectInt\0RectOffset\0second\0set\0short\0size\0SInt16\0SInt32\0SInt64\0SInt8"
            "\0staticvector\0string\0TextAsset\0TextMesh\0Texture\0Texture2D\0Transform\0TypelessData\0UInt16\0UInt32\0"
            "UInt64\0UInt8\0unsigned int\0unsigned long long\0unsigned "
            "short\0vector\0Vector2f\0Vector3f\0Vector4f\0m_ScriptingClassIdentifier\0Gradient\0Type*\0int2_"
            "storage\0int3_storage\0BoundsInt\0m_CorrespondingSourceObject\0m_PrefabInstance\0m_"
            "PrefabAsset\0FileSize\0Hash128";

        std::vector<UnityTypeNode> ReadTree(Reader &r, int version)
        {
            auto count = r.Count(100000), stringSize = r.Count(16 * 1024 * 1024);

            struct Raw
            {
                uint32_t type, name;
            };

            std::vector<Raw> raw;
            std::vector<UnityTypeNode> result;
            for (size_t i = 0; i < count; ++i)
            {
                UnityTypeNode n;
                r.Skip(2);
                n.Level = r.Read<uint8_t>();
                r.Skip(1);
                Raw s{r.Read<uint32_t>(), r.Read<uint32_t>()};
                n.Size = r.Read<int32_t>();
                r.Skip(4);
                n.Flags = r.Read<uint32_t>();
                if (version >= 19)
                    r.Skip(8);
                raw.push_back(s);
                result.push_back(n);
            }
            auto strings = r.Take(stringSize);
            auto text = [&](uint32_t offset)
            {
                std::span<const uint8_t> data;
                if (offset & 0x80000000)
                {
                    offset &= 0x7fffffff;
                    data = {reinterpret_cast<const uint8_t *>(CommonStrings), sizeof CommonStrings};
                }
                else
                    data = strings;
                Require(offset < data.size(), "Type tree string offset is invalid.");
                Reader reader(data);
                reader.Seek(offset);
                return reader.CString();
            };
            for (size_t i = 0; i < count; ++i)
            {
                result[i].Type = text(raw[i].type);
                result[i].Name = text(raw[i].name);
                Require(i == 0 ? result[i].Level == 0 : result[i].Level <= result[i - 1].Level + 1,
                        "Invalid type tree nesting.");
            }
            return result;
        }

        Parameter ReadValue(Reader &r, const std::vector<UnityTypeNode> &nodes, size_t &cursor, size_t &budget,
                            int depth)
        {
            Require(cursor < nodes.size() && depth <= 128 && budget > 0, "Serialized value tree exceeds limits.");
            --budget;
            const auto &node = nodes[cursor++];
            Parameter p;
            p.Name = node.Name;
            auto type = node.Type;
            auto end = cursor;
            while (end < nodes.size() && nodes[end].Level > node.Level)
                ++end;
            auto signedValue = [&](int64_t v)
            {
                p.Encoding = "int";
                p.Integer = v;
                p.Number = static_cast<double>(v);
            };
            auto unsignedValue = [&](uint64_t v)
            {
                p.Encoding = "uint";
                p.Unsigned = v;
                p.Number = static_cast<double>(v);
            };
            if (type == "string")
            {
                p.Encoding = "string";
                p.Text = r.String();
                r.Align();
                cursor = end;
            }
            else if (type == "TypelessData")
            {
                auto count = r.Count(MaxResource);
                auto b = r.Take(count);
                p.Encoding = "bytes";
                p.Bytes.assign(b.begin(), b.end());
                cursor = end;
            }
            else if (type == "Array")
            {
                Require(cursor + 1 < end, "Invalid array type.");
                auto count = r.Count(16 * 1024 * 1024);
                size_t element = cursor + 1;
                auto &item = nodes[element];
                p.Encoding = "array";
                if ((item.Type == "UInt8" || item.Type == "char" || item.Type == "unsigned char") && item.Size == 1 &&
                    !(item.Flags & 0x4000))
                {
                    auto b = r.Take(count);
                    p.Bytes.assign(b.begin(), b.end());
                    p.Encoding = "bytes";
                }
                else
                {
                    Require(count <= budget, "Serialized array exceeds node budget.");
                    p.Children.reserve(count);
                    for (size_t i = 0; i < count; ++i)
                    {
                        auto next = element;
                        p.Children.push_back(ReadValue(r, nodes, next, budget, depth + 1));
                    }
                }
                cursor = end;
            }
            else if (type == "bool")
            {
                p.Encoding = "bool";
                p.Number = r.Read<uint8_t>() != 0;
            }
            else if (type == "SInt8")
                signedValue(r.Read<int8_t>());
            else if (type == "UInt8" || type == "char" || type == "unsigned char")
                unsignedValue(r.Read<uint8_t>());
            else if (type == "SInt16" || type == "short")
                signedValue(r.Read<int16_t>());
            else if (type == "UInt16" || type == "unsigned short")
                unsignedValue(r.Read<uint16_t>());
            else if (type == "SInt32" || type == "int")
                signedValue(r.Read<int32_t>());
            else if (type == "UInt32" || type == "unsigned int" || type == "Type*")
                unsignedValue(r.Read<uint32_t>());
            else if (type == "SInt64" || type == "long long")
            {
                signedValue(r.Read<int64_t>());
                p.Text = std::to_string(p.Integer);
            }
            else if (type == "UInt64" || type == "unsigned long long" || type == "FileSize")
            {
                unsignedValue(r.Read<uint64_t>());
                p.Text = std::to_string(p.Unsigned);
            }
            else if (type == "float" || type == "double")
            {
                p.Encoding = "float";
                p.Number = type == "float" ? r.Read<float>() : r.Read<double>();
                if (!std::isfinite(p.Number))
                {
                    p.Encoding = "float-special";
                    p.Text = std::isnan(p.Number) ? "NaN" : p.Number < 0 ? "-Infinity" : "Infinity";
                }
            }
            else if (cursor == end)
            {
                Require(node.Size >= 0, "Unknown variable-size serialized field.");
                auto b = r.Take(node.Size);
                p.Encoding = "bytes";
                p.Bytes.assign(b.begin(), b.end());
            }
            else
            {
                p.Encoding = "object";
                while (cursor < end)
                    p.Children.push_back(ReadValue(r, nodes, cursor, budget, depth + 1));
                if (p.Children.size() == 1 && p.Children.front().Name == "Array")
                {
                    auto array = std::move(p.Children.front());
                    p.Children = std::move(array.Children);
                    p.Bytes = std::move(array.Bytes);
                }
            }
            Require(cursor == end, "Type tree scalar has unexpected children.");
            if (node.Flags & 0x4000)
                r.Align();
            return p;
        }
    }

    std::vector<std::uint8_t> DecompressUnityBlock(std::span<const uint8_t> input, size_t outputSize,
                                                   unsigned compression)
    {
        Require(outputSize <= MaxResource, "Decompressed resource exceeds limit.");
        std::vector<uint8_t> output(outputSize);
        if (compression == 0)
        {
            Require(input.size() == outputSize, "Uncompressed block length mismatch.");
            std::copy(input.begin(), input.end(), output.begin());
            return output;
        }
        if (compression == 1)
        {
            Require(input.size() >= 5, "LZMA block is truncated.");
            ISzAlloc allocator{[](ISzAllocPtr, size_t n) -> void *
                               {
                                   return std::malloc(n);
                               },
                               [](ISzAllocPtr, void *p)
                               {
                                   std::free(p);
                               }};
            SizeT out = outputSize, in = input.size() - 5;
            ELzmaStatus status;
            auto result = LzmaDecode(output.data(), &out, input.data() + 5, &in, input.data(), 5, LZMA_FINISH_END,
                                     &status, &allocator);
            Require(result == SZ_OK && out == outputSize, "Invalid LZMA block.");
            return output;
        }
        Require(compression == 2 || compression == 3, "Unknown Unity compression method.");
        size_t source = 0, dest = 0;
        auto length = [&](size_t n)
        {
            if (n == 15)
            {
                uint8_t extra;
                do
                {
                    Require(source < input.size(), "LZ4 length is truncated.");
                    extra = input[source++];
                    Require(n <= outputSize && extra <= outputSize - n, "LZ4 output exceeds advertised size.");
                    n += extra;
                } while (extra == 255);
            }
            return n;
        };
        while (source < input.size())
        {
            uint8_t token = input[source++];
            auto literals = length(token >> 4);
            Require(literals <= input.size() - source && literals <= output.size() - dest,
                    "LZ4 literal range is invalid.");
            std::copy_n(input.data() + source, literals, output.data() + dest);
            source += literals;
            dest += literals;
            if (source == input.size())
                break;
            Require(input.size() - source >= 2, "LZ4 match offset is truncated.");
            size_t offset = input[source] | size_t(input[source + 1]) << 8;
            source += 2;
            Require(offset > 0 && offset <= dest, "LZ4 match offset is invalid.");
            auto match = length(token & 15) + 4;
            Require(match <= output.size() - dest, "LZ4 match exceeds output.");
            for (size_t i = 0; i < match; ++i)
                output[dest + i] = output[dest + i - offset];
            dest += match;
        }
        Require(dest == output.size(), "LZ4 decompressed length mismatch.");
        return output;
    }

    UnityBundle::UnityBundle(std::span<const uint8_t> bytes)
    {
        Reader r(bytes, true);
        Require(r.CString() == "UnityFS", "Only UnityFS bundles are supported.");
        auto version = r.Read<uint32_t>();
        Require(version >= 6 && version <= 8, "Unsupported UnityFS format.");
        r.CString();
        UnityVersion = r.CString();
        auto total = r.Read<uint64_t>();
        auto compressed = r.Read<uint32_t>(), uncompressed = r.Read<uint32_t>(), flags = r.Read<uint32_t>();
        Require(total <= bytes.size() && total >= r.Position, "UnityFS file size is invalid.");
        if (version >= 7)
            r.Align(16);
        auto start = r.Position;
        if (flags & 0x80)
        {
            Require(compressed <= total - start, "UnityFS directory exceeds file.");
            r.Seek(static_cast<size_t>(total - compressed));
        }
        auto header = DecompressUnityBlock(r.Take(compressed), uncompressed, flags & 63);
        if (flags & 0x80)
            r.Seek(start);
        if (flags & 0x200)
            r.Align(16);
        Reader h(header, true);
        h.Skip(16);

        struct Block
        {
            uint32_t unpacked, packed;
            uint16_t flags;
        };

        std::vector<Block> blocks;
        auto count = h.Count(100000);
        uint64_t size = 0;
        for (size_t i = 0; i < count; ++i)
        {
            Block b{h.Read<uint32_t>(), h.Read<uint32_t>(), h.Read<uint16_t>()};
            Require(b.unpacked <= MaxResource - size, "UnityFS payload exceeds limit.");
            size += b.unpacked;
            blocks.push_back(b);
        }
        auto entries = h.Count(100000);
        for (size_t i = 0; i < entries; ++i)
        {
            UnityBundleEntry e;
            e.Offset = h.Read<uint64_t>();
            e.Size = h.Read<uint64_t>();
            e.Flags = h.Read<uint32_t>();
            e.Name = h.CString();
            Require(e.Offset <= size && e.Size <= size - e.Offset, "UnityFS entry exceeds payload.");
            Require(std::none_of(entries_.begin(), entries_.end(),
                                 [&](auto &n)
                                 {
                                     return n.Name == e.Name;
                                 }),
                    "Duplicate UnityFS entry.");
            entries_.push_back(std::move(e));
        }
        Require(h.Position == header.size(), "Unexpected trailing UnityFS directory data.");
        data_.reserve(static_cast<size_t>(size));
        for (auto &b : blocks)
        {
            auto data = DecompressUnityBlock(r.Take(b.packed), b.unpacked, b.flags & 63);
            data_.insert(data_.end(), data.begin(), data.end());
        }
    }

    std::span<const uint8_t> UnityBundle::Entry(size_t index) const
    {
        Require(index < entries_.size(), "UnityFS entry index is invalid.");
        auto &e = entries_[index];
        return std::span(data_).subspan(static_cast<size_t>(e.Offset), static_cast<size_t>(e.Size));
    }

    std::span<const uint8_t> UnityBundle::Entry(std::string_view name) const
    {
        for (size_t i = 0; i < entries_.size(); ++i)
            if (entries_[i].Name == name)
                return Entry(i);
        throw std::runtime_error("UnityFS resource entry is missing: " + std::string(name));
    }

    UnityClassDatabase::UnityClassDatabase(const std::filesystem::path &path)
    {
        auto bytes = ReadFile(path);
        Reader header(bytes);
        auto magic = header.Take(4);
        Require(std::string_view(reinterpret_cast<const char *>(magic.data()), 4) == "TPK*",
                "Invalid class database magic.");
        auto version = header.Read<uint8_t>(), compression = header.Read<uint8_t>(), type = header.Read<uint8_t>();
        Require((version == 1 || version == 2) && type == 0, "Unsupported class database format.");
        header.Skip(5);
        auto packed = header.Read<uint32_t>(), unpacked = header.Read<uint32_t>();
        auto source = header.Take(packed);
        std::vector<uint8_t> data;
        if (compression == 0)
            data.assign(source.begin(), source.end());
        else if (compression == 1)
            data = DecompressUnityBlock(source, unpacked, 2);
        else if (compression == 2)
        {
            Require(source.size() >= 13, "Truncated TPK LZMA stream.");
            std::vector<uint8_t> payload(source.begin(), source.begin() + 5);
            payload.insert(payload.end(), source.begin() + 13, source.end());
            data = DecompressUnityBlock(payload, unpacked, 1);
        }
        else
            throw std::runtime_error("Unsupported TPK compression.");
        Require(data.size() == unpacked, "Class database length mismatch.");
        Reader r(data);
        r.Skip(8);
        auto versions = r.Count(100000);
        r.Skip(versions * 8);
        auto classes = r.Count(100000);
        for (size_t i = 0; i < classes; ++i)
        {
            int id = r.Read<int32_t>();
            auto count = r.Count(100000);
            auto &entries = types_[id];
            for (size_t j = 0; j < count; ++j)
            {
                TypeVersion v;
                v.Version = r.Read<uint64_t>();
                if (r.Read<uint8_t>())
                {
                    r.Skip(4);
                    auto flags = r.Read<uint8_t>();
                    if (flags & 64)
                        r.Skip(2);
                    if (flags & 128)
                        v.Root = r.Read<uint16_t>();
                }
                entries.push_back(v);
            }
        }
        auto common = r.Count(100000);
        if (version == 1)
        {
            r.Skip(common * 9);
            auto indices = r.Count(65536);
            r.Skip(indices * 2);
        }
        else
            for (size_t i = 0; i < common; ++i)
            {
                r.Skip(8);
                auto count = r.Count(65536);
                r.Skip(count * 4);
            }
        auto nodes = r.Count(65536);
        for (size_t i = 0; i < nodes; ++i)
        {
            Node n;
            n.Type = r.Read<uint16_t>();
            n.Name = r.Read<uint16_t>();
            n.Size = r.Read<int32_t>();
            r.Skip(3);
            n.Flags = r.Read<uint32_t>();
            auto children = r.Read<uint16_t>();
            for (unsigned j = 0; j < children; ++j)
                n.Children.push_back(r.Read<uint16_t>());
            nodes_.push_back(std::move(n));
        }
        auto strings = r.Count(65536);
        for (size_t i = 0; i < strings; ++i)
            strings_.push_back(r.DotNetString());
        Require(r.Position == data.size(), "Unexpected class database trailing data.");
    }

    std::vector<UnityTypeNode> UnityClassDatabase::Type(int classId, std::string_view version) const
    {
        auto found = types_.find(classId);
        Require(found != types_.end(), "Class database has no requested class.");
        auto value = Version(version);
        uint16_t root = 65535;
        for (auto &v : found->second)
        {
            if (v.Version > value)
                break;
            root = v.Root;
        }
        Require(root != 65535, "Class database has no matching release schema.");
        std::vector<UnityTypeNode> result;
        std::set<uint16_t> ancestors;
        std::function<void(uint16_t, int)> append = [&](uint16_t id, int depth)
        {
            Require(id < nodes_.size() && depth <= 128 && result.size() < 100000 && ancestors.insert(id).second,
                    "Invalid class database tree.");
            auto &n = nodes_[id];
            Require(n.Type < strings_.size() && n.Name < strings_.size(), "Invalid class database string index.");
            result.push_back({strings_[n.Type], strings_[n.Name], n.Size, n.Flags, static_cast<uint8_t>(depth)});
            for (auto child : n.Children)
                append(child, depth + 1);
            ancestors.erase(id);
        };
        append(root, 0);
        return result;
    }

    UnitySerializedFile::UnitySerializedFile(std::span<const uint8_t> bytes, const UnityClassDatabase *database)
        : bytes_(bytes.begin(), bytes.end())
    {
        Reader r(bytes_, true);
        auto metadataSize = r.Read<uint32_t>();
        uint64_t fileSize = r.Read<uint32_t>();
        auto version = r.Read<uint32_t>();
        uint64_t dataOffset = r.Read<uint32_t>();
        Require(version >= 17 && version <= 22, "Unsupported serialized file version.");
        bigEndian_ = r.Read<uint8_t>() != 0;
        r.Skip(3);
        if (version >= 22)
        {
            metadataSize = r.Read<uint32_t>();
            fileSize = r.Read<uint64_t>();
            dataOffset = r.Read<uint64_t>();
            r.Skip(8);
        }
        Require(fileSize <= bytes.size() && dataOffset <= fileSize && metadataSize <= fileSize,
                "Invalid serialized file bounds.");
        r.Big = bigEndian_;
        UnityVersion = r.CString();
        Require(UnityVersion.starts_with("2018."), "Only Unity 2018 assets are supported.");
        r.Skip(4);
        bool hasTree = r.Read<uint8_t>() != 0;
        auto count = r.Count(100000);
        for (size_t i = 0; i < count; ++i)
        {
            Type t;
            t.ClassId = r.Read<int32_t>();
            r.Skip(1);
            auto script = r.Read<int16_t>();
            if (t.ClassId == 114)
                r.Skip(16);
            r.Skip(16);
            if (hasTree)
            {
                t.Nodes = ReadTree(r, version);
                if (version >= 21)
                {
                    auto deps = r.Count(100000);
                    r.Skip(deps * 4);
                }
            }
            else
            {
                Require(database != nullptr, "Serialized file has no type tree; provide classdata.tpk.");
                t.Nodes = database->Type(t.ClassId, UnityVersion);
            }
            types_.push_back(std::move(t));
            (void)script;
        }
        auto objects = r.Count(1000000);
        for (size_t i = 0; i < objects; ++i)
        {
            r.Align();
            UnityAssetInfo o;
            o.PathId = r.Read<int64_t>();
            o.Offset = version >= 22 ? r.Read<uint64_t>() : r.Read<uint32_t>();
            Require(o.Offset <= fileSize - dataOffset, "Serialized object offset is invalid.");
            o.Offset += dataOffset;
            o.Size = r.Read<uint32_t>();
            o.TypeIndex = r.Read<int32_t>();
            Require(o.TypeIndex >= 0 && static_cast<size_t>(o.TypeIndex) < types_.size() &&
                        o.Size <= fileSize - o.Offset,
                    "Serialized object bounds are invalid.");
            o.ClassId = types_[o.TypeIndex].ClassId;
            Require(index_.emplace(o.PathId, assets_.size()).second, "Duplicate serialized object ID.");
            assets_.push_back(o);
        }
        auto scripts = r.Count(1000000);
        for (size_t i = 0; i < scripts; ++i)
        {
            r.Skip(4);
            r.Align();
            r.Skip(8);
        }
        auto externals = r.Count(100000);
        for (size_t i = 0; i < externals; ++i)
        {
            r.CString();
            r.Skip(20);
            ExternalNames.push_back(Basename(r.CString()));
        }
        Require(version < 20, "Reference type metadata requires a newer Unity format.");
        r.CString();
        Require(r.Position <= dataOffset, "Serialized metadata overlaps object data.");
    }

    const UnityAssetInfo &UnitySerializedFile::Asset(int64_t id) const
    {
        auto i = index_.find(id);
        if (i == index_.end())
            throw std::runtime_error("Serialized object is missing.");
        return assets_[i->second];
    }

    std::span<const uint8_t> UnitySerializedFile::Bytes(int64_t id) const
    {
        auto &a = Asset(id);
        return std::span(bytes_).subspan(static_cast<size_t>(a.Offset), a.Size);
    }

    Parameter UnitySerializedFile::Read(int64_t id, bool allowTrailing) const
    {
        auto &a = Asset(id);
        auto &type = types_[a.TypeIndex];
        Require(!type.Nodes.empty(), "Object type tree is empty.");
        Reader r(Bytes(id), bigEndian_);
        size_t cursor = 0, budget = 4000000;
        auto result = ReadValue(r, type.Nodes, cursor, budget, 0);
        Require(cursor == type.Nodes.size(), "Object type tree was not fully consumed.");
        Require(allowTrailing || r.Position == r.Data.size(), "Object payload does not match its type tree.");
        return result;
    }

    Parameter UnitySerializedFile::ReadScript(int64_t id, const std::vector<UnityTypeNode> &schema) const
    {
        Reader r(Bytes(id), bigEndian_);
        size_t cursor = 0, budget = 4000000;
        auto value = ReadValue(r, schema, cursor, budget, 0);
        Require(cursor == schema.size() && r.Position == r.Data.size(),
                "Mono payload does not match its serialized field schema.");
        return value;
    }

    std::optional<std::string> UnitySerializedFile::NamedPrefix(int64_t id) const
    {
        auto &type = types_[Asset(id).TypeIndex];
        if (type.Nodes.size() < 2 || type.Nodes[1].Name != "m_Name")
            return {};
        Reader r(Bytes(id), bigEndian_);
        auto length = r.Count(65536);
        auto bytes = r.Take(length);
        return std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size());
    }

    NativeGpuAssetReader::NativeGpuAssetReader(const std::filesystem::path &path, const UnityClassDatabase *database)
        : Path(std::filesystem::absolute(path))
    {
        auto bytes = ReadFile(Path);
        if (bytes.size() >= 8 && std::memcmp(bytes.data(), "UnityFS", 7) == 0)
        {
            bundle_ = std::make_unique<UnityBundle>(bytes);
            Require(!bundle_->Entries().empty(), "Empty UnityFS bundle.");
            file_ = std::make_unique<UnitySerializedFile>(bundle_->Entry(0), database);
            CabName = bundle_->Entries()[0].Name;
        }
        else
        {
            file_ = std::make_unique<UnitySerializedFile>(bytes, database);
            auto u = Path.filename().u8string();
            CabName.assign(reinterpret_cast<const char *>(u.data()), u.size());
        }
        ExternalNames = file_->ExternalNames;
    }

    const std::string &NativeGpuAssetReader::UnityVersion() const
    {
        return file_->UnityVersion;
    }

    Parameter NativeGpuAssetReader::MetadataHeader(int64_t id) const
    {
        return file_->Read(id, true);
    }

    const Parameter &NativeGpuAssetReader::ReadScript(int64_t id, const std::vector<UnityTypeNode> &schema)
    {
        return fields_.insert_or_assign(id, file_->ReadScript(id, schema)).first->second;
    }

    const Parameter &NativeGpuAssetReader::Metadata(int64_t id)
    {
        auto f = fields_.find(id);
        if (f != fields_.end())
            return f->second;
        try
        {
            return fields_.emplace(id, file_->Read(id)).first->second;
        }
        catch (const std::exception &)
        {
            if (AssetClass(id) != 114 || !ScriptSchema)
                throw;
            auto header = MetadataHeader(id);
            return ReadScript(id, ScriptSchema(Pointer(header.Get("m_Script")), file_->UnityVersion));
        }
    }

    const std::vector<UnityAssetInfo> &NativeGpuAssetReader::AssetIndex() const
    {
        return file_->Assets();
    }

    std::optional<int> UnitySerializedFile::AssetClass(int64_t id) const
    {
        auto found = index_.find(id);
        return found == index_.end() ? std::optional<int>{} : assets_[found->second].ClassId;
    }

    std::optional<int> NativeGpuAssetReader::AssetClass(int64_t id) const
    {
        return file_->AssetClass(id);
    }

    std::vector<std::string> NativeGpuAssetReader::ExportNames()
    {
        std::vector<std::string> result;
        for (auto &a : file_->Assets())
            if (a.ClassId == 142)
                for (auto &p : Metadata(a.PathId).Get("m_Container").Array())
                    result.push_back(p.Get("first").Text);
        return result;
    }

    GpuAssetPointer NativeGpuAssetReader::Pointer(const Parameter &p) const
    {
        return {Path, static_cast<int>(p.Int("m_FileID")), p.Int("m_PathID")};
    }

    std::optional<GpuAssetPointer> NativeGpuAssetReader::TryFind(std::string_view name, int classId, int textureSize)
    {
        for (auto &a : file_->Assets())
            if (a.ClassId == 142)
            {
                for (auto &p : Metadata(a.PathId).Get("m_Container").Array())
                {
                    auto asset = Pointer(p.Get("second").Get("asset"));
                    if (asset.FileId == 0 && AssetClass(asset.PathId) == classId &&
                        (EqualIgnoreCase(p.Get("first").Text, name) ||
                         EqualIgnoreCase(Basename(p.Get("first").Text), name) ||
                         EqualIgnoreCase(
                             Basename(p.Get("first").Text).substr(0, Basename(p.Get("first").Text).find_last_of('.')),
                             name)))
                    {
                        if (!textureSize || (Metadata(asset.PathId).Int("m_Width") == textureSize &&
                                             Metadata(asset.PathId).Int("m_Height") == textureSize))
                            return asset;
                    }
                }
            }
        auto names = names_.find(classId);
        if (names == names_.end())
        {
            std::unordered_map<std::string, std::vector<int64_t>> index;
            for (auto &a : file_->Assets())
                if (a.ClassId == classId)
                {
                    auto prefix = file_->NamedPrefix(a.PathId);
                    auto assetName = prefix ? *prefix : Metadata(a.PathId).String("m_Name");
                    if (classId == 48 && assetName.empty())
                        assetName = Metadata(a.PathId).Get("m_ParsedForm").String("m_Name");
                    index[Lower(std::move(assetName))].push_back(a.PathId);
                }
            names = names_.emplace(classId, std::move(index)).first;
        }
        if (auto found = names->second.find(Lower(std::string(name))); found != names->second.end())
            for (auto id : found->second)
                if (!textureSize ||
                    (Metadata(id).Int("m_Width") == textureSize && Metadata(id).Int("m_Height") == textureSize))
                    return GpuAssetPointer{Path, 0, id};
        return {};
    }

    GpuAssetPointer NativeGpuAssetReader::Find(std::string_view name, int classId, int textureSize)
    {
        auto p = TryFind(name, classId, textureSize);
        if (!p)
            throw std::runtime_error("Resource is missing: " + std::to_string(classId) + "/" + std::string(name));
        return *p;
    }

    std::vector<uint8_t> NativeGpuAssetReader::ReadText(std::string_view name)
    {
        auto id = Find(name, 49).PathId;
        Reader r(file_->Bytes(id));
        r.String();
        r.Align();
        auto length = r.Count(32 * 1024 * 1024);
        auto b = r.Take(length);
        return {b.begin(), b.end()};
    }

    std::vector<uint8_t> NativeGpuAssetReader::ResourceStream(std::string_view name, uint64_t offset,
                                                              uint64_t size) const
    {
        Require(size <= 512ULL * 1024 * 1024, "Resource stream exceeds limit.");
        auto entry = Basename(std::string(name));
        std::vector<uint8_t> file;
        std::span<const uint8_t> bytes;
        if (bundle_)
            bytes = bundle_->Entry(entry);
        else
        {
            Require(entry != "." && entry != ".." && entry.find(':') == std::string::npos,
                    "Invalid resource stream path.");
            file = ReadFile(Path.parent_path() / std::filesystem::path(std::u8string(
                                                     reinterpret_cast<const char8_t *>(entry.data()), entry.size())));
            bytes = file;
        }
        Require(offset <= bytes.size() && size <= bytes.size() - offset, "Resource stream is truncated.");
        auto b = bytes.subspan(static_cast<size_t>(offset), static_cast<size_t>(size));
        return {b.begin(), b.end()};
    }

    GpuMeshData NativeGpuAssetReader::Mesh(int64_t id)
    {
        Require(AssetClass(id) == 43, "Asset is not a mesh.");
        auto &p = Metadata(id);
        auto bytes = Bytes(p.Get("m_VertexData").Get("m_DataSize"));
        auto &stream = p.Get("m_StreamData");
        if (bytes.empty() && stream.Int("size") > 0)
            bytes = ResourceStream(stream.String("path"), stream.Int("offset"), stream.Int("size"));
        return GpuMeshData::FromMetadata(p, std::move(bytes));
    }

    GpuTextureData NativeGpuAssetReader::Texture(int64_t id)
    {
        Require(AssetClass(id) == 28 || AssetClass(id) == 89, "Asset is not a texture.");
        auto &p = Metadata(id);
        GpuTextureData t;
        t.Name = p.String("m_Name");
        t.Width = static_cast<int>(p.Int("m_Width"));
        t.Height = static_cast<int>(p.Int("m_Height"));
        Require(t.Width >= 1 && t.Width <= 16384 && t.Height >= 1 && t.Height <= 16384 &&
                    uint64_t(t.Width) * t.Height <= 256 * 1024 * 1024,
                "Invalid texture dimensions.");
        t.Format = static_cast<int>(p.Int("m_TextureFormat"));
        t.MipCount = static_cast<int>(p.Int("m_MipCount", 1));
        t.ImageCount = static_cast<int>(p.Int("m_ImageCount", 1));
        t.Dimension = AssetClass(id) == 89 ? 4 : static_cast<int>(p.Int("m_TextureDimension", 2));
        t.Srgb = p.Int("m_ColorSpace", 1) == 1;
        auto &settings = p.Get("m_TextureSettings");
        t.Filter = static_cast<int>(settings.Int("m_FilterMode", 1));
        t.Anisotropy = static_cast<int>(settings.Int("m_Aniso", 1));
        t.MipBias = settings.Float("m_MipBias");
        t.WrapU = static_cast<int>(settings.Int("m_WrapU"));
        t.WrapV = static_cast<int>(settings.Int("m_WrapV"));
        t.WrapW = static_cast<int>(settings.Int("m_WrapW"));
        t.Bytes = Bytes(p.Get("image data"));
        auto &stream = p.Get("m_StreamData");
        if (t.Bytes.empty() && stream.Int("size") > 0)
            t.Bytes = ResourceStream(stream.String("path"), stream.Int("offset"), stream.Int("size"));
        return t;
    }

    GpuMaterialData NativeGpuAssetReader::Material(int64_t id)
    {
        Require(AssetClass(id) == 21, "Asset is not a material.");
        auto &p = Metadata(id);
        GpuMaterialData m;
        m.Name = p.String("m_Name");
        m.Shader = Pointer(p.Get("m_Shader"));
        m.Queue = static_cast<int>(p.Int("m_CustomRenderQueue", -1));
        std::istringstream words(p.String("m_ShaderKeywords"));
        for (std::string word; words >> word;)
            m.Keywords.push_back(std::move(word));
        auto &saved = p.Get("m_SavedProperties");
        for (auto &v : saved.Get("m_Floats").Array())
            m.Floats[v.Get("first").Text] = static_cast<float>(v.Get("second").Number);
        for (auto &v : saved.Get("m_Colors").Array())
            m.Colors[v.Get("first").Text] = V4(v.Get("second"), true);
        for (auto &v : saved.Get("m_TexEnvs").Array())
        {
            auto &data = v.Get("second");
            m.Textures[v.Get("first").Text] = {Pointer(data.Get("m_Texture")), V2(data.Get("m_Scale")),
                                               V2(data.Get("m_Offset"))};
        }
        for (auto &v : p.Get("stringTagMap").Array())
            m.OverrideTags[v.Get("first").Text] = v.Get("second").Text;
        return m;
    }

    GpuPrefabData NativeGpuAssetReader::Prefab(std::string_view name)
    {
        auto object = Find(name, 1);
        GpuPrefabData prefab;
        std::set<int64_t> visited;
        std::function<void(int64_t, int)> walk = [&](int64_t id, int depth)
        {
            Require(depth <= 128 && visited.size() < 20000 && visited.insert(id).second, "Invalid prefab hierarchy.");
            auto &obj = Metadata(id);
            std::vector<int64_t> components;
            for (auto &p : obj.Get("m_Component").Array())
                components.push_back(Pointer(p.Get("component")).PathId);
            auto transform = std::find_if(components.begin(), components.end(),
                                          [&](auto c)
                                          {
                                              return AssetClass(c) == 4;
                                          });
            Require(transform != components.end(), "Prefab transform is missing.");
            auto &tr = Metadata(*transform);
            GpuNodeData n;
            n.Id = *transform;
            n.ParentId = Pointer(tr.Get("m_Father")).PathId;
            n.Name = obj.String("m_Name");
            n.Active = obj.Int("m_IsActive") != 0;
            n.Layer = static_cast<int>(obj.Int("m_Layer"));
            n.Position = V3(tr.Get("m_LocalPosition"));
            n.Scale = V3(tr.Get("m_LocalScale"));
            n.Rotation = V4(tr.Get("m_LocalRotation"));
            for (auto &p : tr.Get("m_Children").Array())
                n.Children.push_back(Pointer(p).PathId);
            prefab.Nodes.push_back(n);
            for (auto component : components)
            {
                int type = AssetClass(component).value_or(0);
                if (type != 4 && type != 33)
                {
                    try
                    {
                        prefab.Components.push_back({n.Id, component, type, Metadata(component)});
                    }
                    catch (const std::exception &e)
                    {
                        if (type != 114)
                            throw;
                        prefab.Diagnostics.push_back(n.Name + " optional component #" + std::to_string(component) +
                                                     ": " + e.what());
                    }
                }
                if (type != 23 && type != 137)
                    continue;
                auto &p = Metadata(component);
                GpuRendererData r;
                r.NodeId = n.Id;
                r.Enabled = p.Int("m_Enabled") != 0;
                r.Skinned = type == 137;
                auto filter = std::find_if(components.begin(), components.end(),
                                           [&](auto c)
                                           {
                                               return AssetClass(c) == 33;
                                           });
                if (!r.Skinned && filter == components.end())
                    continue;
                r.Mesh = Pointer(r.Skinned ? p.Get("m_Mesh") : Metadata(*filter).Get("m_Mesh"));
                if (!r.Mesh.PathId)
                    continue;
                for (auto &v : p.Get("m_Materials").Array())
                    r.Materials.push_back(Pointer(v));
                if (r.Skinned)
                {
                    for (auto &v : p.Get("m_Bones").Array())
                        r.Bones.push_back(Pointer(v));
                    for (auto &v : p.Get("m_BlendShapeWeights").Array())
                        r.BlendWeights.push_back(static_cast<float>(v.Number));
                    r.RootBone = Pointer(p.Get("m_RootBone"));
                }
                r.Settings = p;
                prefab.Renderers.push_back(std::move(r));
            }
            for (auto child : n.Children)
                walk(Pointer(Metadata(child).Get("m_GameObject")).PathId, depth + 1);
        };
        walk(object.PathId, 0);
        Require(!prefab.Nodes.empty(), "Empty prefab.");
        prefab.RootId = prefab.Nodes[0].Id;
        return prefab;
    }
}
