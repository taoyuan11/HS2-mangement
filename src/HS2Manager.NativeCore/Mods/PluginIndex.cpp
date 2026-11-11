#include "Mods/ModIndex.h"
#include "Serialization/CliMetadata.h"
#include <windows.h>
#include <algorithm>
#include <charconv>
#include <map>
#include <set>

namespace hs2
{
    namespace
    {
        std::string Lower(std::string text)
        {
            for (auto &c : text)
                if (c >= 'A' && c <= 'Z')
                    c = char(c - 'A' + 'a');
            return text;
        }

        std::string Version(metadata::Cursor &row)
        {
            auto a = row.Read<std::uint16_t>(), b = row.Read<std::uint16_t>(), c = row.Read<std::uint16_t>(),
                 d = row.Read<std::uint16_t>();
            return std::to_string(a) + "." + std::to_string(b) + "." + std::to_string(c) + "." + std::to_string(d);
        }
    }

    PluginFileInfo PluginFileIndex::Read(const fs::path &path)
    {
        PluginFileInfo result;
        result.Path = Utf8(fs::absolute(path));
        result.Size = fs::file_size(path);
        result.ModifiedUtcTicks = ModifiedTicks(path);
        metadata::Assembly assembly(path);
        if (!assembly.Managed || !assembly.Rows[32])
            return result;
        auto header = assembly.Row(32, 1);
        header.Skip(4);
        result.Version = Version(header);
        header.Skip(4 + assembly.BlobSize);
        result.AssemblyName = assembly.Text(header.Index(assembly.StringSize));
        for (std::uint32_t i = 1; i <= assembly.Rows[35]; i++)
        {
            auto row = assembly.Row(35, i);
            auto version = Version(row);
            row.Skip(4 + assembly.BlobSize);
            result.References.push_back({assembly.Text(row.Index(assembly.StringSize)), version});
        }
        for (std::uint32_t i = 1; i <= assembly.Rows[28]; i++)
        {
            auto row = assembly.Row(28, i);
            row.Skip(2 + assembly.Coded(1, {4, 6}) + assembly.StringSize);
            auto module = row.Index(assembly.IndexSize(26));
            if (module)
            {
                auto reference = assembly.Row(26, module);
                auto name = assembly.Text(reference.Index(assembly.StringSize));
                if (std::find(result.NativeReferences.begin(), result.NativeReferences.end(), name) ==
                    result.NativeReferences.end())
                    result.NativeReferences.push_back(name);
            }
        }

        struct Attribute
        {
            std::uint32_t Constructor, Blob;
        };

        std::map<std::uint32_t, std::vector<Attribute>> attributes;
        for (std::uint32_t i = 1; i <= assembly.Rows[12]; i++)
        {
            auto row = assembly.Row(12, i);
            auto parent = row.Index(
                assembly.Coded(5, {6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43}));
            auto constructor = row.Index(assembly.Coded(3, {6, 10})), blob = row.Index(assembly.BlobSize);
            if ((parent & 31) == 3)
                attributes[parent >> 5].push_back({constructor, blob});
        }
        std::map<std::uint32_t, std::string> constants;
        for (std::uint32_t i = 1; i <= assembly.Rows[11]; i++)
        {
            auto row = assembly.Row(11, i);
            auto type = row.Read<std::uint16_t>();
            auto parent = row.Index(assembly.Coded(2, {4, 8, 23}));
            auto blob = row.Index(assembly.BlobSize);
            if (type != 14 || (parent & 3) != 0)
                continue;
            auto bytes = assembly.Blob(blob);
            if (bytes.size() % 2)
                throw std::runtime_error("Invalid UTF16 CLI field constant");
            std::wstring text(bytes.size() / 2, L'\0');
            std::memcpy(text.data(), bytes.data(), bytes.size());
            constants[parent >> 2] = Utf8(fs::path(text));
        }
        for (std::uint32_t type = 1; type <= assembly.Rows[2]; type++)
        {
            std::vector<PluginDefinition> plugins;
            std::vector<PluginDependency> dependencies;
            std::set<std::uint32_t> visited;
            for (auto current = type; current && visited.insert(current).second; current = assembly.BaseType(current))
            {
                for (auto attribute : attributes[current])
                {
                    try
                    {
                        auto [name, signature] = assembly.AttributeType(attribute.Constructor);
                        if (name != "BepInEx.BepInPlugin" && name != "BepInEx.BepInDependency")
                            continue;
                        metadata::Cursor blob(assembly.Blob(attribute.Blob));
                        metadata::Require(blob.Read<std::uint16_t>() == 1, "Invalid plugin attribute");
                        auto guid = blob.SerializedString();
                        metadata::Require(!guid.empty(), "Plugin attribute has no GUID");
                        if (name == "BepInEx.BepInPlugin")
                        {
                            auto label = blob.SerializedString(), version = blob.SerializedString();
                            plugins.push_back({guid, label.empty() ? guid : label, version, {}, {}});
                        }
                        else
                        {
                            metadata::Cursor sig(signature);
                            auto flags = sig.Read<std::uint8_t>();
                            if (flags & 0x10)
                                sig.Compressed();
                            auto count = sig.Compressed();
                            sig.Read<std::uint8_t>();
                            sig.Read<std::uint8_t>();
                            auto second = count > 1 ? sig.Read<std::uint8_t>() : 0;
                            PluginDependency dependency{guid, second == 14 ? blob.SerializedString() : std::string(),
                                                        false};
                            if (count > 1 && second != 14)
                                dependency.Optional = (blob.Read<std::int32_t>() & 2) != 0;
                            if (std::find(dependencies.begin(), dependencies.end(), dependency) == dependencies.end())
                                dependencies.push_back(dependency);
                        }
                    }
                    catch (const std::exception &error)
                    {
                        result.Error = "无法完整读取插件声明：" + std::string(error.what());
                    }
                }
            }
            for (auto &plugin : plugins)
            {
                plugin.Dependencies = dependencies;
                auto first = assembly.Lists(type).first,
                     last = type < assembly.Rows[2] ? assembly.Lists(type + 1).first : assembly.Rows[4] + 1;
                for (auto field = first; field < last; field++)
                {
                    auto row = assembly.Row(4, field);
                    row.Skip(2);
                    auto name = Lower(assembly.Text(row.Index(assembly.StringSize)));
                    if (name.find("extdata") == std::string::npos && name.find("dataid") == std::string::npos)
                        continue;
                    auto value = constants.find(field);
                    if (value != constants.end() && !value->second.empty())
                        plugin.DataIds.push_back(value->second);
                }
                result.Plugins.push_back(std::move(plugin));
            }
        }
        return result;
    }

    std::vector<PluginFileInfo> PluginFileIndex::Scan(const fs::path &folder, std::vector<std::string> &warnings,
                                                      std::stop_token token)
    {
        std::vector<PluginFileInfo> result;
        std::vector<fs::path> pending{folder};
        try
        {
            while (!pending.empty())
            {
                CheckCancelled(token);
                auto directory = pending.back();
                pending.pop_back();
                for (auto &entry : fs::directory_iterator(directory))
                {
                    CheckCancelled(token);
                    auto attributes = GetFileAttributesW(entry.path().c_str());
                    if (attributes == INVALID_FILE_ATTRIBUTES)
                        throw std::system_error(GetLastError(), std::system_category(), "插件目录");
                    if (attributes & FILE_ATTRIBUTE_REPARSE_POINT)
                        continue;
                    if (attributes & FILE_ATTRIBUTE_DIRECTORY)
                    {
                        pending.push_back(entry.path());
                        continue;
                    }
                    if (Lower(Utf8(entry.path().extension())) != ".dll")
                        continue;
                    try
                    {
                        auto file = Read(entry.path());
                        if (!file.Error.empty())
                            warnings.push_back("插件元数据：" + file.Path + "：" + file.Error);
                        result.push_back(std::move(file));
                    }
                    catch (const std::exception &error)
                    {
                        warnings.push_back("插件扫描：" + Utf8(entry.path()) + "：" + error.what());
                    }
                }
            }
        }
        catch (const Cancelled &)
        {
            throw;
        }
        catch (const std::exception &error)
        {
            warnings.push_back("插件目录：" + Utf8(folder) + "：" + error.what());
        }
        std::sort(result.begin(), result.end(),
                  [](auto &a, auto &b)
                  {
                      return Lower(a.Path) < Lower(b.Path);
                  });
        return result;
    }

    std::string PluginFileIndex::PluginGuid(const std::string &id)
    {
        if (id == "com.bepis.sideloader.universalautoresolver" || id == "EC.Core.Sideloader.UniversalAutoResolver")
            return "com.bepis.bepinex.sideloader";
        if (id == "KKABMPlugin.ABMData")
            return "KKABMX.Core";
        if (id == "moreAccessories")
            return "com.joan6694.illusionplugins.moreaccessories";
        if (id == "KK_InvisibleBody")
            return "com.deathweasel.bepinex.invisiblebody";
        if (id == "marco.authordata" || id == "TestCharaCustomFunctionController")
            return "marco.kkapi";
        return id;
    }

    int PluginFileIndex::CompareVersions(const std::string &first, const std::string &second)
    {
        auto parse = [](const std::string &text)
        {
            std::array<int, 4> parts{0, 0, -1, -1};
            std::size_t at = 0;
            int count = 0;
            while (at <= text.size() && count < 4)
            {
                auto end = text.find('.', at);
                if (end == std::string::npos)
                    end = text.size();
                int number = 0;
                auto [stop, error] = std::from_chars(text.data() + at, text.data() + end, number);
                if (error != std::errc() || stop != text.data() + end || number < 0)
                    return std::array<int, 4>{0, 0, -1, -1};
                parts[count++] = number;
                if (end == text.size())
                {
                    at = end + 1;
                    break;
                }
                at = end + 1;
            }
            return count < 2 || at <= text.size() ? std::array<int, 4>{0, 0, -1, -1} : parts;
        };
        auto a = parse(first), b = parse(second);
        return a == b ? 0 : a < b ? -1 : 1;
    }
}
