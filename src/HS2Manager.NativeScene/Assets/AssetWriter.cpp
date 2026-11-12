#include "Assets/AssetWriter.h"
#include "ThirdParty/Lz4/lz4.h"
#include <algorithm>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>
#include <map>
#include <set>
#include <stdexcept>

namespace hs2
{
    namespace
    {
        constexpr std::size_t Limit = 1024ull * 1024 * 1024;

        void Require(bool condition, const char *message)
        {
            if (!condition)
                throw std::runtime_error(message);
        }

        struct Writer
        {
            std::vector<std::uint8_t> Data;
            bool Big = false;

            void Bytes(std::span<const std::uint8_t> value)
            {
                Require(value.size() <= Limit && Data.size() <= Limit - value.size(),
                        "Serialized output exceeds 1 GiB.");
                Data.insert(Data.end(), value.begin(), value.end());
            }

            template <class T> void Number(T value)
            {
                auto bytes = std::bit_cast<std::array<std::uint8_t, sizeof(T)>>(value);
                if (Big)
                    std::reverse(bytes.begin(), bytes.end());
                Bytes(bytes);
            }

            void CString(std::string_view value)
            {
                Require(value.find('\0') == value.npos && value.size() < 65536, "Invalid serialized identifier.");
                Bytes({reinterpret_cast<const std::uint8_t *>(value.data()), value.size()});
                Number<std::uint8_t>(0);
            }

            void String(std::string_view value)
            {
                Require(value.size() <= 16 * 1024 * 1024, "Serialized string exceeds limit.");
                Number<std::int32_t>(static_cast<std::int32_t>(value.size()));
                Bytes({reinterpret_cast<const std::uint8_t *>(value.data()), value.size()});
                Align();
            }

            void Align(std::size_t alignment = 4)
            {
                while (Data.size() % alignment)
                    Number<std::uint8_t>(0);
            }
        };

        struct Schema
        {
            std::span<const UnityTypeNode> Nodes;
            std::vector<std::size_t> End;

            explicit Schema(std::span<const UnityTypeNode> nodes) : Nodes(nodes), End(nodes.size(), nodes.size())
            {
                Require(!nodes.empty() && nodes.size() <= 100000 && nodes[0].Level == 0, "Invalid output type tree.");
                std::vector<std::size_t> parents;
                for (std::size_t i = 0; i < nodes.size(); ++i)
                {
                    Require(nodes[i].Level <= 128 && (!i || nodes[i].Level <= nodes[i - 1].Level + 1),
                            "Invalid output type tree nesting.");
                    while (!parents.empty() && nodes[parents.back()].Level >= nodes[i].Level)
                    {
                        End[parents.back()] = i;
                        parents.pop_back();
                    }
                    parents.push_back(i);
                }
            }

            Parameter Default(std::size_t i) const
            {
                const auto &node = Nodes[i];
                Parameter p;
                p.Name = node.Name;
                if (node.Type == "string")
                    p.Encoding = "string";
                else if (node.Type == "Array")
                    p.Encoding = "array";
                else if (node.Type == "TypelessData")
                    p.Encoding = "bytes";
                else if (End[i] > i + 1)
                {
                    p.Encoding = "object";
                    if (Nodes[i + 1].Type == "Array" && End[i + 1] == End[i])
                        return p;
                    for (auto j = i + 1; j < End[i]; j = End[j])
                        p.Children.push_back(Default(j));
                }
                else if (node.Type == "float" || node.Type == "double")
                    p.Encoding = "float";
                else if (node.Type == "bool")
                    p.Encoding = "bool";
                else if (node.Type.starts_with("UInt") || node.Type.starts_with("unsigned") || node.Type == "char" ||
                         node.Type == "FileSize" || node.Type == "Type*")
                    p.Encoding = "uint";
                else if (node.Type.starts_with("SInt") || node.Type == "short" || node.Type == "int" ||
                         node.Type == "long long")
                    p.Encoding = "int";
                else
                {
                    Require(node.Size >= 0, "Unknown variable output field.");
                    p.Encoding = "bytes";
                    p.Bytes.resize(node.Size);
                }
                return p;
            }

            void Write(Writer &w, std::size_t i, const Parameter &p, std::size_t &budget) const
            {
                Require(budget > 0, "Serialized value exceeds node budget.");
                --budget;
                const auto &node = Nodes[i];
                const auto &type = node.Type;
                if (type == "string")
                    w.String(p.Text);
                else if (type == "TypelessData")
                {
                    Require(p.Bytes.size() <= Limit, "Serialized byte array exceeds limit.");
                    w.Number<std::int32_t>(static_cast<std::int32_t>(p.Bytes.size()));
                    w.Bytes(p.Bytes);
                }
                else if (type == "Array")
                {
                    Require(i + 2 < End[i], "Invalid output array schema.");
                    auto element = i + 2;
                    const auto &item = Nodes[element];
                    bool bytes = (item.Type == "UInt8" || item.Type == "char" || item.Type == "unsigned char") &&
                                 item.Size == 1 && !(item.Flags & 0x4000);
                    auto count = bytes ? p.Bytes.size() : p.Children.size();
                    Require(count <= 16 * 1024 * 1024, "Serialized array exceeds limit.");
                    w.Number<std::int32_t>(static_cast<std::int32_t>(count));
                    if (bytes)
                        w.Bytes(p.Bytes);
                    else
                        for (const auto &child : p.Children)
                            Write(w, element, child, budget);
                }
                else if (type == "bool")
                    w.Number<std::uint8_t>(p.Number != 0);
                else if (type == "SInt8")
                    Integer<std::int8_t>(w, p);
                else if (type == "UInt8" || type == "char" || type == "unsigned char")
                    Integer<std::uint8_t>(w, p);
                else if (type == "SInt16" || type == "short")
                    Integer<std::int16_t>(w, p);
                else if (type == "UInt16" || type == "unsigned short")
                    Integer<std::uint16_t>(w, p);
                else if (type == "SInt32" || type == "int")
                    Integer<std::int32_t>(w, p);
                else if (type == "UInt32" || type == "unsigned int" || type == "Type*")
                    Integer<std::uint32_t>(w, p);
                else if (type == "SInt64" || type == "long long")
                    Integer<std::int64_t>(w, p);
                else if (type == "UInt64" || type == "unsigned long long" || type == "FileSize")
                    Integer<std::uint64_t>(w, p);
                else if (type == "float" || type == "double")
                {
                    auto number = p.Number;
                    if (p.Encoding == "float-special")
                        number = p.Text == "NaN"         ? std::numeric_limits<double>::quiet_NaN()
                                 : p.Text == "-Infinity" ? -std::numeric_limits<double>::infinity()
                                                         : std::numeric_limits<double>::infinity();
                    if (type == "float")
                        w.Number<float>(static_cast<float>(number));
                    else
                        w.Number<double>(number);
                }
                else if (End[i] == i + 1)
                {
                    Require(node.Size >= 0 && p.Bytes.size() == static_cast<std::size_t>(node.Size),
                            "Opaque serialized field has wrong size.");
                    w.Bytes(p.Bytes);
                }
                else if (Nodes[i + 1].Type == "Array" && End[i + 1] == End[i])
                    Write(w, i + 1, p, budget);
                else
                    for (auto j = i + 1; j < End[i]; j = End[j])
                    {
                        Require(p.Has(Nodes[j].Name), "Serialized output field is missing.");
                        Write(w, j, p.Get(Nodes[j].Name), budget);
                    }
                if (node.Flags & 0x4000)
                    w.Align();
            }

            template <class T> static void Integer(Writer &w, const Parameter &p)
            {
                if constexpr (std::is_signed_v<T>)
                {
                    Require(p.Encoding != "uint" ||
                                p.Unsigned <= static_cast<std::uint64_t>(std::numeric_limits<T>::max()),
                            "Serialized signed integer overflow.");
                    auto n = p.Encoding == "uint" ? static_cast<std::int64_t>(p.Unsigned) : p.Integer;
                    Require(n >= std::numeric_limits<T>::min() && n <= std::numeric_limits<T>::max(),
                            "Serialized integer overflow.");
                    w.Number<T>(static_cast<T>(n));
                }
                else
                {
                    Require(p.Encoding != "int" || p.Integer >= 0, "Negative unsigned field.");
                    auto n = p.Encoding == "int" ? static_cast<std::uint64_t>(p.Integer) : p.Unsigned;
                    Require(n <= std::numeric_limits<T>::max(), "Serialized unsigned integer overflow.");
                    w.Number<T>(static_cast<T>(n));
                }
            }
        };

        void Tree(Writer &w, const UnitySerializedType &type, std::uint32_t version)
        {
            Schema check(type.Nodes);
            Writer strings;
            std::map<std::string, std::uint32_t> offsets;
            auto offset = [&](const std::string &s)
            {
                if (auto found = offsets.find(s); found != offsets.end())
                    return found->second;
                auto position = static_cast<std::uint32_t>(strings.Data.size());
                strings.CString(s);
                offsets.emplace(s, position);
                return position;
            };
            for (const auto &node : type.Nodes)
            {
                offset(node.Type);
                offset(node.Name);
            }
            w.Number<std::int32_t>(static_cast<std::int32_t>(type.Nodes.size()));
            w.Number<std::int32_t>(static_cast<std::int32_t>(strings.Data.size()));
            for (const auto &node : type.Nodes)
            {
                w.Number(node.Version);
                w.Number(node.Level);
                w.Number(node.TypeFlags);
                w.Number(offset(node.Type));
                w.Number(offset(node.Name));
                w.Number<std::int32_t>(node.Size);
                w.Number(node.Index);
                w.Number(node.Flags);
                if (version >= 19)
                    w.Number(node.ReferenceTypeHash);
            }
            w.Bytes(strings.Data);
        }

        std::vector<std::uint8_t> Compress(std::span<const std::uint8_t> bytes)
        {
            Require(bytes.size() <= LZ4_MAX_INPUT_SIZE, "LZ4 input exceeds limit.");
            std::vector<std::uint8_t> output(LZ4_compressBound(static_cast<int>(bytes.size())));
            int count = LZ4_compress_default(reinterpret_cast<const char *>(bytes.data()),
                                             reinterpret_cast<char *>(output.data()), static_cast<int>(bytes.size()),
                                             static_cast<int>(output.size()));
            Require(count > 0, "LZ4 compression failed.");
            output.resize(count);
            return output;
        }
    }

    std::vector<std::uint8_t> WriteUnityValue(std::span<const UnityTypeNode> schema, const Parameter &value,
                                              bool bigEndian)
    {
        Writer w;
        w.Big = bigEndian;
        std::size_t budget = 4000000;
        Schema(schema).Write(w, 0, value, budget);
        return std::move(w.Data);
    }

    Parameter DefaultUnityValue(std::span<const UnityTypeNode> schema)
    {
        return Schema(schema).Default(0);
    }

    Parameter DefaultUnityElement(std::span<const UnityTypeNode> nodes, std::span<const std::string_view> path)
    {
        Schema schema(nodes);
        std::size_t parent = 0;
        for (auto name : path)
        {
            std::size_t child = parent + 1;
            for (; child < schema.End[parent]; child = schema.End[child])
                if (nodes[child].Name == name)
                    break;
            Require(child < schema.End[parent], "Array field is absent from type tree.");
            parent = child;
        }
        if (nodes[parent].Type != "Array")
        {
            Require(parent + 1 < schema.End[parent] && nodes[parent + 1].Type == "Array",
                    "Output field is not an array.");
            ++parent;
        }
        Require(parent + 2 < schema.End[parent], "Invalid array element type.");
        return schema.Default(parent + 2);
    }

    void VisitUnityPointers(Parameter &value, const std::function<void(std::int32_t &, std::int64_t &)> &visitor)
    {
        if (value.Has("m_FileID") && value.Has("m_PathID"))
        {
            auto file = static_cast<std::int32_t>(value.Int("m_FileID"));
            auto path = value.Int("m_PathID");
            visitor(file, path);
            for (auto &child : value.Children)
            {
                child.Encoding = "int";
                child.Integer = child.Name == "m_FileID" ? file : path;
                child.Number = static_cast<double>(child.Integer);
            }
            return;
        }
        for (auto &child : value.Children)
            VisitUnityPointers(child, visitor);
    }

    std::vector<std::uint8_t> WriteUnitySerializedFile(const UnitySerializedDocument &d)
    {
        Require(d.FormatVersion >= 17 && d.FormatVersion <= 19 && d.UnityVersion.starts_with("2018."),
                "Native writing supports Unity 2018 serialized formats 17-19.");
        Require(d.Types.size() <= 100000 && d.Objects.size() <= 1000000 && d.Externals.size() <= 100000 &&
                    d.ScriptReferences.size() <= 1000000,
                "Serialized table exceeds limit.");
        Writer metadata, payload;
        metadata.Big = d.BigEndian;
        metadata.CString(d.UnityVersion);
        metadata.Number(d.Platform);
        metadata.Number<std::uint8_t>(d.HasTypeTree);
        metadata.Number<std::int32_t>(static_cast<std::int32_t>(d.Types.size()));
        for (const auto &type : d.Types)
        {
            metadata.Number<std::int32_t>(type.ClassId);
            metadata.Number<std::uint8_t>(type.Stripped);
            metadata.Number(type.ScriptTypeIndex);
            if (type.ClassId == 114)
                metadata.Bytes(type.ScriptId);
            metadata.Bytes(type.TypeHash);
            if (d.HasTypeTree)
                Tree(metadata, type, d.FormatVersion);
        }
        metadata.Number<std::int32_t>(static_cast<std::int32_t>(d.Objects.size()));
        std::set<std::int64_t> ids;
        for (const auto &object : d.Objects)
        {
            Require(object.PathId != 0 && ids.insert(object.PathId).second, "Duplicate or zero output object ID.");
            Require(object.TypeIndex >= 0 && static_cast<std::size_t>(object.TypeIndex) < d.Types.size(),
                    "Invalid output object type index.");
            payload.Align(8);
            metadata.Align();
            metadata.Number(object.PathId);
            metadata.Number<std::uint32_t>(static_cast<std::uint32_t>(payload.Data.size()));
            metadata.Number<std::uint32_t>(static_cast<std::uint32_t>(object.Data.size()));
            metadata.Number(object.TypeIndex);
            payload.Bytes(object.Data);
        }
        metadata.Number<std::int32_t>(static_cast<std::int32_t>(d.ScriptReferences.size()));
        for (auto [file, path] : d.ScriptReferences)
        {
            metadata.Number(file);
            metadata.Align();
            metadata.Number(path);
        }
        metadata.Number<std::int32_t>(static_cast<std::int32_t>(d.Externals.size()));
        for (const auto &external : d.Externals)
        {
            metadata.CString(external.Name);
            metadata.Bytes(external.Guid);
            metadata.Number(external.Type);
            metadata.CString(external.Path);
        }
        metadata.CString(d.UserInformation);
        auto offset = (20 + metadata.Data.size() + 15) & ~std::size_t(15);
        Writer output;
        output.Big = true;
        output.Number<std::uint32_t>(static_cast<std::uint32_t>(metadata.Data.size()));
        output.Number<std::uint32_t>(static_cast<std::uint32_t>(offset + payload.Data.size()));
        output.Number(d.FormatVersion);
        output.Number<std::uint32_t>(static_cast<std::uint32_t>(offset));
        output.Number<std::uint8_t>(d.BigEndian);
        output.Number<std::uint8_t>(0);
        output.Number<std::uint16_t>(0);
        output.Bytes(metadata.Data);
        output.Align(16);
        output.Bytes(payload.Data);
        return std::move(output.Data);
    }

    std::vector<std::uint8_t> WriteUnityBundle(std::string_view version, std::span<const UnityBundleOutput> entries,
                                               std::stop_token token)
    {
        Require(!entries.empty() && entries.size() <= 100000 && version.starts_with("2018."), "Invalid bundle output.");
        Writer payload, packed, directory;
        directory.Big = true;
        directory.Bytes(std::array<std::uint8_t, 16>{});
        std::set<std::string> names;
        for (const auto &entry : entries)
        {
            Require(!entry.Name.empty() && names.insert(entry.Name).second, "Duplicate or empty bundle entry.");
            payload.Bytes(entry.Data);
        }
        constexpr std::size_t blockSize = 128 * 1024;
        directory.Number<std::int32_t>(static_cast<std::int32_t>((payload.Data.size() + blockSize - 1) / blockSize));
        for (std::size_t at = 0; at < payload.Data.size(); at += blockSize)
        {
            if (token.stop_requested())
                throw std::runtime_error("Bundle writing cancelled.");
            auto size = std::min(blockSize, payload.Data.size() - at);
            auto source = std::span(payload.Data).subspan(at, size);
            auto compressed = Compress(source);
            bool useCompressed = compressed.size() < size;
            directory.Number<std::uint32_t>(static_cast<std::uint32_t>(size));
            directory.Number<std::uint32_t>(static_cast<std::uint32_t>(useCompressed ? compressed.size() : size));
            directory.Number<std::uint16_t>(useCompressed ? 2 : 0);
            packed.Bytes(useCompressed ? std::span<const std::uint8_t>(compressed) : source);
        }
        directory.Number<std::int32_t>(static_cast<std::int32_t>(entries.size()));
        std::uint64_t offset = 0;
        for (const auto &entry : entries)
        {
            directory.Number(offset);
            directory.Number<std::uint64_t>(entry.Data.size());
            directory.Number(entry.Flags);
            directory.CString(entry.Name);
            offset += entry.Data.size();
        }
        auto info = Compress(directory.Data);
        Writer output;
        output.Big = true;
        output.CString("UnityFS");
        output.Number<std::uint32_t>(6);
        output.CString("5.x.x");
        output.CString(version);
        auto size = output.Data.size() + 20 + info.size() + packed.Data.size();
        output.Number<std::uint64_t>(size);
        output.Number<std::uint32_t>(static_cast<std::uint32_t>(info.size()));
        output.Number<std::uint32_t>(static_cast<std::uint32_t>(directory.Data.size()));
        output.Number<std::uint32_t>(0x40 | 2);
        output.Bytes(info);
        output.Bytes(packed.Data);
        return std::move(output.Data);
    }
}
