#include "Export/CardExport.h"
#include "Serialization/CliMetadata.h"
#include "Assets/AssetReader.h"
#include "Archive/ZipArchive.h"
#include <windows.h>
#include <shlwapi.h>
#include <xmllite.h>
#include <wrl/client.h>
#include <algorithm>
#include <charconv>
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

        std::string Normalize(std::string text)
        {
            std::replace(text.begin(), text.end(), '\\', '/');
            return text;
        }

        std::string Trim(std::string text)
        {
            auto first = text.find_first_not_of(" \t\r\n"), last = text.find_last_not_of(" \t\r\n");
            return first == std::string::npos ? std::string() : text.substr(first, last - first + 1);
        }

        std::optional<int> Integer(const std::string &text)
        {
            int value{};
            auto [end, error] = std::from_chars(text.data(), text.data() + text.size(), value);
            return error == std::errc() && end == text.data() + text.size() ? std::optional(value) : std::nullopt;
        }

        bool KnownExtension(const std::string &id)
        {
            static const std::set<std::string> values{"com.bepis.sideloader.universalautoresolver",
                                                      "EC.Core.Sideloader.UniversalAutoResolver",
                                                      "moreAccessories",
                                                      "KKABMPlugin.ABMData",
                                                      "com.deathweasel.bepinex.materialeditor",
                                                      "KSOX",
                                                      "KCOX",
                                                      "com.deathweasel.bepinex.uncensorselector",
                                                      "KK_InvisibleBody",
                                                      "TestCharaCustomFunctionController",
                                                      "com.fairbair.hs2_boobsettings",
                                                      "com.deathweasel.bepinex.eyecontrol",
                                                      "KK_PregnancyPlus",
                                                      "mikke.pushUpAI",
                                                      "orange.spork.additionalaccessorycontrolsplugin",
                                                      "orange.spork.advikplugin",
                                                      "orange.spork.outfitpainter",
                                                      "marco.authordata",
                                                      "orange.spork.earwiggle"};
            return values.contains(id);
        }

        bool SupportedVersion(const std::string &id, std::int64_t version)
        {
            if (id == "com.bepis.sideloader.universalautoresolver" || id == "EC.Core.Sideloader.UniversalAutoResolver")
                return true;
            if (id == "KSOX")
                return version >= 0 && version <= 2;
            if (id == "com.deathweasel.bepinex.uncensorselector")
                return version == 1 || version == 2;
            if (id == "KCOX" || id == "KK_PregnancyPlus" || id == "marco.authordata")
                return version == 1;
            if (id == "com.deathweasel.bepinex.materialeditor")
                return version == 0 || version == 1;
            if (id == "KKABMPlugin.ABMData")
                return version == 2;
            if (id == "mikke.pushUpAI")
                return version == 3;
            return KnownExtension(id) && version == 0;
        }

        std::optional<MessagePack> ReadExtensions(const fs::path &path, std::stop_token token)
        {
            auto data = ReadFile(path, 512ull * 1024 * 1024, token);
            metadata::Cursor reader(data);
            reader.Position = CardParser::FindPngEnd(data);
            reader.Skip(4);
            auto text = [&]
            {
                std::size_t length = 0;
                for (int shift = 0;; shift += 7)
                {
                    if (shift > 28)
                        throw std::runtime_error("人物头文本越界");
                    auto b = reader.Read<std::uint8_t>();
                    length |= std::size_t(b & 127) << shift;
                    if (!(b & 128))
                        break;
                }
                if (length > 65536)
                    throw std::runtime_error("人物头文本越界");
                reader.Skip(length);
            };
            text();
            text();
            reader.Skip(4);
            text();
            text();
            auto tableLength = reader.Read<std::int32_t>();
            if (tableLength < 1 || tableLength > 1024 * 1024)
                throw std::runtime_error("人物块目录无效");
            auto table = MessagePack::Parse(reader.Take(tableLength));
            auto length = reader.Read<std::uint64_t>();
            auto start = reader.Position;
            if (length > data.size() - start)
                throw std::runtime_error("人物块长度越界");
            auto &blocks = table.Require(std::string("lstInfo"));
            for (auto &block : blocks.Children)
                if (block.Require(std::string("name")).Text() == "KKEx")
                {
                    auto offset = block.Require(std::string("pos")).Integer(),
                         size = block.Require(std::string("size")).Integer();
                    if (offset < 0 || size < 0 || static_cast<std::uint64_t>(offset) > length ||
                        static_cast<std::uint64_t>(size) > length - offset)
                        throw std::runtime_error("扩展块越界");
                    return MessagePack::Parse(std::span(data).subspan(start + static_cast<std::size_t>(offset),
                                                                      static_cast<std::size_t>(size)));
                }
            return {};
        }

        bool IsInactive(const std::string &id, std::int64_t version, const MessagePack &data)
        {
            if (data.Type != MessagePack::Kind::Map)
                throw std::runtime_error("扩展参数不是字典");
            if (id == "KK_PregnancyPlus" && version == 1)
            {
                for (std::size_t i = 0; i < data.Children.size(); i += 2)
                {
                    auto key = data.Children[i].Text();
                    auto &value = data.Children[i + 1];
                    if (key != "pluginVersion" && key != "clothingOffsetVersion")
                        return false;
                    if (key == "pluginVersion" && !std::holds_alternative<std::monostate>(value.ScalarValue) &&
                        !std::holds_alternative<std::string>(value.ScalarValue))
                        return false;
                    if (key == "clothingOffsetVersion" && !std::holds_alternative<std::monostate>(value.ScalarValue) &&
                        !std::holds_alternative<std::int64_t>(value.ScalarValue))
                        return false;
                }
                return true;
            }
            if (id != "com.fairbair.hs2_boobsettings" || version != 0)
                return false;
            const std::set<std::string> flags{"overridePhysics", "overrideGravity", "butt_overridePhysics",
                                              "butt_overrideGravity"},
                parameters{"damping", "elasticity", "stiffness", "inert", "gravityX", "gravityY", "gravityZ"};
            for (auto &flag : flags)
            {
                auto value = data.Find(flag);
                if (!value || value->ScalarValue != MessagePack::Value(false))
                    return false;
            }
            for (std::size_t i = 0; i < data.Children.size(); i += 2)
            {
                auto key = data.Children[i].Text();
                if (!flags.contains(key) && !parameters.contains(key) &&
                    !(key.starts_with("butt_") && parameters.contains(key.substr(5))))
                    return false;
            }
            return true;
        }

        struct Resources
        {
            std::set<std::string> Paths, Shaders, InactivePlugins, BodyGuids;
            std::vector<std::string> Issues;
        };

        Resources ReadResources(const CardRecord &card, CardExportLevel level, std::stop_token token)
        {
            Resources result;
            bool full = level == CardExportLevel::WithResources;
            try
            {
                auto extensions = ReadExtensions(Path(card.Path), token);
                if (!extensions)
                    return result;
                if (extensions->Type != MessagePack::Kind::Map)
                    throw std::runtime_error("扩展数据格式无效");
                for (std::size_t i = 0; i < extensions->Children.size(); i += 2)
                {
                    CheckCancelled(token);
                    auto id = extensions->Children[i].Text();
                    bool shaderProvider = id == "com.deathweasel.bepinex.materialeditor";
                    if (!full && !shaderProvider &&
                        (level < CardExportLevel::WithPlugins ||
                         (id != "KK_PregnancyPlus" && id != "com.fairbair.hs2_boobsettings")))
                        continue;
                    try
                    {
                        auto &plugin = extensions->Children[i + 1];
                        auto version = plugin.Require(std::int64_t(0)).Integer();
                        auto &data = plugin.Require(std::int64_t(1));
                        if (data.Type != MessagePack::Kind::Map)
                            throw std::runtime_error("扩展参数格式不支持");
                        if (IsInactive(id, version, data))
                        {
                            result.InactivePlugins.insert(id);
                            continue;
                        }
                        if (full && !SupportedVersion(id, version))
                            result.Issues.push_back("扩展 " + id + " 的数据版本 " + std::to_string(version) +
                                                    " 尚未验证，配套资源可能不完整");
                        std::function<void(const MessagePack &, const std::string &)> visit =
                            [&](const MessagePack &node, const std::string &key)
                        {
                            CheckCancelled(token);
                            if (node.Type == MessagePack::Kind::Map)
                            {
                                for (std::size_t j = 0; j < node.Children.size(); j += 2)
                                {
                                    auto name = std::get_if<std::string>(&node.Children[j].ScalarValue);
                                    visit(node.Children[j + 1], name ? *name : std::string());
                                }
                            }
                            else if (node.Type == MessagePack::Kind::Array)
                            {
                                for (auto &child : node.Children)
                                    visit(child, key);
                            }
                            else if (auto value = std::get_if<std::string>(&node.ScalarValue))
                            {
                                if (id == "com.deathweasel.bepinex.uncensorselector" &&
                                    (key == "BodyGUID" || key == "PenisGUID" || key == "BallsGUID") &&
                                    !value->empty() && !value->starts_with("Default."))
                                    result.BodyGuids.insert(*value);
                                if (id == "com.deathweasel.bepinex.uncensorselector" && key == "UncensorGUID" &&
                                    !value->empty())
                                    result.Issues.push_back("旧版身体引用需要先在创作工程中解析：" + *value);
                                if (shaderProvider && key == "ShaderName" && !value->empty())
                                    result.Shaders.insert(*value);
                                if (!full)
                                    return;
                                auto path = Normalize(*value);
                                if (Lower(path).starts_with("userdata/") || Path(*value).has_root_path())
                                    result.Paths.insert(*value);
                                else if ((Lower(key).find("path") != std::string::npos ||
                                          Lower(key).ends_with("file") || Lower(key).ends_with("filename")) &&
                                         !value->empty())
                                    result.Issues.push_back("无法确定外部资源的相对基准：" + id + "/" + key + " = " +
                                                            *value);
                            }
                        };
                        for (std::size_t j = 0; j < data.Children.size(); j += 2)
                        {
                            auto key = data.Children[j].Text();
                            if (!full && !(shaderProvider && key == "MaterialShaderList"))
                                continue;
                            auto &value = data.Children[j + 1];
                            bool packed = (shaderProvider &&
                                           (key == "MaterialShaderList" || key == "MaterialTexturePropertyList")) ||
                                          (id == "KCOX" && key == "Overlays") || (id == "KSOX" && key == "Lookup");
                            if (packed)
                            {
                                if (value.Type == MessagePack::Kind::Binary)
                                    visit(MessagePack::Parse(value.Payload()), key);
                                else if (!std::holds_alternative<std::monostate>(value.ScalarValue))
                                    throw std::runtime_error("扩展字段不是二进制参数");
                            }
                            else
                                visit(value, key);
                        }
                    }
                    catch (const Cancelled &)
                    {
                        throw;
                    }
                    catch (const std::exception &error)
                    {
                        if (full || shaderProvider)
                            result.Issues.push_back("扩展资源读取不完整：" + id + "：" + error.what());
                    }
                }
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &error)
            {
                result.Issues.push_back("外部资源检查不完整：" + std::string(error.what()));
            }
            return result;
        }

        std::vector<std::vector<std::string>> Csv(std::span<const std::uint8_t> bytes)
        {
            std::string text(reinterpret_cast<const char *>(bytes.data()), bytes.size());
            if (text.starts_with("\xef\xbb\xbf"))
                text.erase(0, 3);
            std::vector<std::vector<std::string>> rows;
            std::vector<std::string> row;
            std::string field;
            bool quote = false, closed = false;
            auto finishField = [&]
            {
                row.push_back(Trim(field));
                field.clear();
                closed = false;
                if (row.size() > 4096)
                    throw std::runtime_error("MOD 清单列数超限");
            };
            auto finishRow = [&]
            {
                finishField();
                if (row.size() > 1 || !row.front().empty())
                    rows.push_back(std::move(row));
                row.clear();
                if (rows.size() > 100010)
                    throw std::runtime_error("MOD 清单行数超限");
            };
            for (std::size_t i = 0; i < text.size(); i++)
            {
                char c = text[i];
                if (quote)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.size() && text[i + 1] == '"')
                        {
                            field += '"';
                            i++;
                        }
                        else
                        {
                            quote = false;
                            closed = true;
                        }
                    }
                    else
                        field += c;
                }
                else if (c == '"' && Trim(field).empty() && !closed)
                {
                    field.clear();
                    quote = true;
                }
                else if (c == ',')
                    finishField();
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.size() && text[i + 1] == '\n')
                        i++;
                    finishRow();
                }
                else if (closed && c != ' ' && c != '\t')
                    throw std::runtime_error("MOD CSV 引号后含无效字符");
                else
                    field += c;
            }
            if (quote)
                throw std::runtime_error("MOD CSV 引号未结束");
            if (!field.empty() || !row.empty() || closed)
                finishRow();
            return rows;
        }

        bool ResourceFile(const fs::path &path)
        {
            auto name = Lower(Utf8(path.filename()));
            if (name.find("config") != std::string::npos || name.find("settings") != std::string::npos)
                return false;
            static const std::set<std::string> extensions{".xml", ".json", ".png",     ".jpg",         ".jpeg",
                                                          ".dds", ".tga",  ".unity3d", ".assetbundle", ".bytes",
                                                          ".dat", ".csv",  ".shader",  ".bin"};
            return extensions.contains(Lower(Utf8(path.extension())));
        }

        std::set<std::string> ShaderNames(const Bytes &bytes)
        {
            if (bytes.empty())
                return {};
            if (bytes.size() > 2 * 1024 * 1024)
                throw std::runtime_error("Shader XML 过大");
            using Microsoft::WRL::ComPtr;
            ComPtr<IStream> input;
            input.Attach(SHCreateMemStream(bytes.data(), static_cast<UINT>(bytes.size())));
            ComPtr<IXmlReader> reader;
            if (!input || FAILED(CreateXmlReader(__uuidof(IXmlReader), reinterpret_cast<void **>(reader.GetAddressOf()),
                                                 nullptr)))
                throw std::runtime_error("Shader XML 初始化失败");
            reader->SetProperty(XmlReaderProperty_DtdProcessing, DtdProcessing_Prohibit);
            reader->SetInput(input.Get());
            std::set<std::string> names;
            XmlNodeType type;
            HRESULT code;
            while ((code = reader->Read(&type)) == S_OK)
                if (type == XmlNodeType_Element)
                {
                    const wchar_t *name{};
                    UINT size{};
                    if (FAILED(reader->GetLocalName(&name, &size)))
                        throw std::runtime_error("Shader XML 无效");
                    if (std::wstring_view(name, size) != L"Shader")
                        continue;
                    if (reader->MoveToAttributeByName(L"Name", nullptr) == S_OK)
                    {
                        const wchar_t *value{};
                        UINT count{};
                        if (SUCCEEDED(reader->GetValue(&value, &count)))
                            names.insert(Utf8(fs::path(std::wstring(value, count))));
                        reader->MoveToElement();
                    }
                }
            if (FAILED(code))
                throw std::runtime_error("Shader XML 无效");
            return names;
        }
    }

    struct CardExportDependencies::Impl
    {
        struct ListEntry
        {
            int Category, Slot;
            std::map<std::string, std::string> Fields;
        };

        struct Plugin
        {
            const PluginFileInfo *File;
            const PluginDefinition *Definition;
        };

        struct Resolution
        {
            bool Found = false;
            std::vector<const ModFileInfo *> Mods;
            std::optional<ModReference> Reference;
        };

        fs::path Game;
        ModIndex Index;
        std::stop_token Cancellation;
        std::optional<fs::path> ClassDatabase;
        std::vector<ModFileInfo> Mods;
        std::map<std::string, const ModFileInfo *> ByGuid, Bundles;
        std::map<std::string, std::pair<const ModFileInfo *, const ExportShader *>> Shaders;
        std::map<std::string, std::vector<Plugin>> Plugins;
        std::map<std::string, std::vector<ListEntry>> Lists;
        std::optional<std::set<std::string>> Stock, Embedded;
        std::vector<std::string> StockWarnings;

        Impl(fs::path game, ModIndex index, std::stop_token token, std::optional<fs::path> database)
            : Game(std::move(game)), Index(std::move(index)), Cancellation(token), ClassDatabase(std::move(database))
        {
            Mods = NativeModSelection::Select(Index.Files);
            std::sort(Mods.begin(), Mods.end(),
                      [](auto &a, auto &b)
                      {
                          return Lower(a.Path) < Lower(b.Path);
                      });
            for (auto &mod : Mods)
            {
                ByGuid[Lower(mod.Guid)] = &mod;
                if (mod.ArchiveEntries)
                    for (auto &entry : *mod.ArchiveEntries)
                        Bundles[Lower(Normalize(entry.Name))] = &mod;
                if (mod.ExportMetadata)
                    for (auto &shader : mod.ExportMetadata->Shaders)
                        Shaders[shader.Name] = {&mod, &shader};
            }
            for (auto &file : Index.PluginFiles)
                for (auto &plugin : file.Plugins)
                    Plugins[Lower(plugin.Guid)].push_back({&file, &plugin});
            for (auto &[guid, candidates] : Plugins)
                std::sort(candidates.begin(), candidates.end(),
                          [](auto &a, auto &b)
                          {
                              int compare =
                                  PluginFileIndex::CompareVersions(a.Definition->Version, b.Definition->Version);
                              return compare ? compare > 0 : Lower(a.File->Path) < Lower(b.File->Path);
                          });
        }

        Resolution Resolve(ModReference reference, std::set<std::string> visited)
        {
            CheckCancelled(Cancellation);
            auto key =
                Lower(reference.Guid) + ":" + std::to_string(reference.Category) + ":" + std::to_string(reference.Slot);
            if (visited.size() > 32 || !visited.insert(key).second)
                return {};
            if (Index.MigrationEnabled)
            {
                std::vector<std::pair<const ModFileInfo *, const ModMigration *>> rules;
                for (auto &mod : Mods)
                    for (auto &rule : mod.Migrations)
                        if (Lower(rule.OldGuid) == Lower(reference.Guid))
                            rules.emplace_back(&mod, &rule);
                std::stable_sort(rules.begin(), rules.end(),
                                 [](auto &a, auto &b)
                                 {
                                     return Lower(a.second->Kind).ends_with("all") <
                                            Lower(b.second->Kind).ends_with("all");
                                 });
                for (auto &[owner, rule] : rules)
                {
                    bool all = Lower(rule->Kind).ends_with("all");
                    if (!all && (rule->OldSlot != reference.Slot ||
                                 NativeModCategories::Parse(rule->Category) != reference.Category))
                        continue;
                    if (rule->Kind == "Strip" || rule->Kind == "StripAll")
                        return {true, {owner}, {}};
                    if (rule->Kind != "Migrate" && rule->Kind != "MigrateAll")
                        continue;
                    auto child = Resolve(
                        {rule->NewGuid, all ? reference.Slot : rule->NewSlot, reference.Category, reference.Property},
                        visited);
                    if (child.Found)
                    {
                        if (std::find(child.Mods.begin(), child.Mods.end(), owner) == child.Mods.end())
                            child.Mods.insert(child.Mods.begin(), owner);
                        return child;
                    }
                }
            }
            auto found = ByGuid.find(Lower(reference.Guid));
            return found == ByGuid.end() ? Resolution() : Resolution{true, {found->second}, reference};
        }

        const std::vector<ListEntry> &ReadLists(const ModFileInfo &mod)
        {
            auto cached = Lists.find(mod.Path);
            if (cached != Lists.end())
                return cached->second;
            std::vector<ListEntry> entries;
            ZipArchive archive(Path(mod.Path));
            for (auto &entry : archive.Entries())
            {
                auto name = Lower(Normalize(entry.Name));
                if (!name.starts_with("abdata/list/characustom/") || !name.ends_with(".csv"))
                    continue;
                CheckCancelled(Cancellation);
                auto rows = Csv(archive.Read(entry, 8 * 1024 * 1024));
                if (rows.empty() || rows[0].empty())
                    throw std::runtime_error("MOD 清单类别无效");
                auto category = Integer(rows[0][0]);
                if (!category)
                    throw std::runtime_error("MOD 清单类别无效");
                std::size_t header = 1;
                while (header < rows.size() && header <= 8 && (rows[header].empty() || rows[header][0] != "ID"))
                    header++;
                if (header > 8 || header == rows.size())
                    throw std::runtime_error("MOD 清单缺少 ID 表头");
                for (std::size_t i = header + 1; i < rows.size(); i++)
                {
                    CheckCancelled(Cancellation);
                    if (rows[i].empty())
                        continue;
                    auto slot = Integer(rows[i][0]);
                    if (!slot)
                        continue;
                    ListEntry item{*category, *slot, {}};
                    for (std::size_t column = 0; column < (std::min)(rows[header].size(), rows[i].size()); column++)
                        if (!rows[header][column].empty())
                            item.Fields[rows[header][column]] = rows[i][column];
                    entries.push_back(std::move(item));
                }
            }
            return Lists.emplace(mod.Path, std::move(entries)).first->second;
        }

        bool BaseAssembly(const std::string &name) const
        {
            static const std::set<std::string> known{"mscorlib",
                                                     "netstandard",
                                                     "System",
                                                     "Microsoft.CSharp",
                                                     "BepInEx",
                                                     "0Harmony",
                                                     "Assembly-CSharp",
                                                     "Assembly-CSharp-firstpass",
                                                     "System.Core",
                                                     "System.Xml",
                                                     "System.Xml.Linq",
                                                     "System.Drawing",
                                                     "System.Data",
                                                     "System.Windows.Forms",
                                                     "System.Configuration",
                                                     "System.Numerics",
                                                     "System.Net.Http",
                                                     "System.Runtime.Serialization",
                                                     "System.Security",
                                                     "System.Transactions"};
            return known.contains(name) || name.starts_with("UnityEngine") ||
                   fs::exists(Game / L"BepInEx" / L"core" / Path(name + ".dll")) ||
                   fs::exists(Game / L"HoneySelect2_Data" / L"Managed" / Path(name + ".dll"));
        }

        const std::set<std::string> &StockBundles()
        {
            if (Stock)
                return *Stock;
            Stock.emplace();
            auto root = Game / L"abdata";
            if (!fs::exists(root))
                return *Stock;
            std::unique_ptr<UnityClassDatabase> database;
            try
            {
                auto path = ClassDatabase;
                if (!path)
                {
                    std::wstring module(32768, L'\0');
                    auto count = GetModuleFileNameW(nullptr, module.data(), static_cast<DWORD>(module.size()));
                    module.resize(count);
                    path = fs::path(module).parent_path() / L"Resources" / L"classdata.tpk";
                }
                database = std::make_unique<UnityClassDatabase>(*path);
            }
            catch (const std::exception &error)
            {
                StockWarnings.push_back("原版资源类型数据库无法读取：" + std::string(error.what()));
                return *Stock;
            }
            for (auto &file : fs::directory_iterator(root))
            {
                CheckCancelled(Cancellation);
                if (!file.is_regular_file())
                    continue;
                auto name = Utf8(file.path().filename());
                if (name != "abdata" && !(name.starts_with("add") && name.size() > 3 &&
                                          std::all_of(name.begin() + 3, name.end(),
                                                      [](char c)
                                                      {
                                                          return c >= '0' && c <= '9';
                                                      })))
                    continue;
                try
                {
                    NativeGpuAssetReader reader(file.path(), database.get());
                    for (auto &asset : reader.AssetIndex())
                        if (asset.ClassId == 290)
                            for (auto &entry : reader.Metadata(asset.PathId).Get("AssetBundleNames").Array())
                                Stock->insert(Lower("abdata/" + Normalize(entry.Get("second").Text)));
                }
                catch (const std::exception &error)
                {
                    StockWarnings.push_back("原版资源清单无法读取，散装资源来源需确认：" + name + "：" + error.what());
                }
            }
            return *Stock;
        }

        const std::set<std::string> &EmbeddedShaders()
        {
            if (Embedded)
                return *Embedded;
            Embedded.emplace();
            for (auto &file : Index.PluginFiles)
                if (std::any_of(file.Plugins.begin(), file.Plugins.end(),
                                [](auto &plugin)
                                {
                                    return plugin.Guid == "com.deathweasel.bepinex.materialeditor";
                                }))
                {
                    CheckCancelled(Cancellation);
                    try
                    {
                        metadata::Assembly assembly(Path(file.Path));
                        auto names = ShaderNames(assembly.EmbeddedResource("KK_Plugins.Resources.default.xml"));
                        Embedded->insert(names.begin(), names.end());
                    }
                    catch (const std::exception &)
                    {
                    }
                }
            return *Embedded;
        }
    };

    CardExportDependencies::CardExportDependencies(fs::path game, ModIndex index, std::stop_token token,
                                                   std::optional<fs::path> database)
        : impl_(std::make_unique<Impl>(std::move(game), std::move(index), token, std::move(database)))
    {
    }

    CardExportDependencies::~CardExportDependencies() = default;

    CollectedDependencies CardExportDependencies::Collect(const CardRecord &card, CardExportLevel level)
    {
        auto &state = *impl_;
        bool pluginsIncluded = level >= CardExportLevel::WithPlugins,
             resourcesIncluded = level == CardExportLevel::WithResources;
        CollectedDependencies result;
        std::set<std::string> issues, warnings, visitedMods, visitedAssemblies, visitedPlugins;
        auto resources = ReadResources(card, level, state.Cancellation);
        auto add = [&](const fs::path &source, const std::string &relative, CardExportFileKind kind)
        {
            ExportCandidate candidate{Utf8(source), relative, kind};
            if (std::find(result.Files.begin(), result.Files.end(), candidate) == result.Files.end())
                result.Files.push_back(std::move(candidate));
        };
        std::function<void(const ModFileInfo &)> addMod;
        std::function<void(const std::string &, const std::string &, bool)> addPlugin;
        std::function<void(const PluginFileInfo &)> addAssembly;
        auto addCompanions = [&](const PluginFileInfo &assembly)
        {
            auto folder = Path(assembly.Path).parent_path();
            auto stem = Path(assembly.Path).stem();
            try
            {
                for (auto &item : fs::directory_iterator(folder))
                    if (item.is_regular_file() && Lower(Utf8(item.path().stem())) == Lower(Utf8(stem)) &&
                        ResourceFile(item.path()))
                        add(item.path(), Utf8(fs::relative(item.path(), state.Game)), CardExportFileKind::Resource);
                std::set<fs::path> roots;
                auto named = folder / stem;
                if (fs::exists(named) && fs::is_directory(named))
                    roots.insert(named);
                if (!SamePath(folder, state.Game / L"BepInEx" / L"plugins") &&
                    std::all_of(state.Index.PluginFiles.begin(), state.Index.PluginFiles.end(),
                                [&](auto &other)
                                {
                                    return other.Plugins.empty() || !Within(Path(other.Path), folder) ||
                                           SamePath(Path(other.Path), Path(assembly.Path));
                                }))
                    roots.insert(folder);
                for (auto &root : roots)
                    for (auto iterator = fs::recursive_directory_iterator(root);
                         iterator != fs::recursive_directory_iterator(); ++iterator)
                    {
                        CheckCancelled(state.Cancellation);
                        auto attributes = GetFileAttributesW(iterator->path().c_str());
                        if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT))
                        {
                            if (iterator->is_directory())
                                iterator.disable_recursion_pending();
                            continue;
                        }
                        if (iterator->is_regular_file() && ResourceFile(iterator->path()))
                            add(iterator->path(), Utf8(fs::relative(iterator->path(), state.Game)),
                                CardExportFileKind::Resource);
                    }
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &error)
            {
                issues.insert("插件配套资源无法完整读取：" + Utf8(stem) + "：" + error.what());
            }
        };
        addMod = [&](const ModFileInfo &mod)
        {
            CheckCancelled(state.Cancellation);
            if (!visitedMods.insert(Lower(mod.Path)).second)
                return;
            auto source = Path(mod.Path);
            if (!fs::exists(source) || static_cast<std::int64_t>(fs::file_size(source)) != mod.Size ||
                ModifiedTicks(source) != mod.ModifiedUtcTicks)
            {
                issues.insert("MOD 在索引后发生变化或已缺失，请重新导出：" + mod.Path);
                return;
            }
            auto relative = Within(source, state.Game / L"mods") ? fs::relative(source, state.Game)
                                                                 : fs::path(L"mods") / L"外部MOD" / source.filename();
            add(source, Utf8(relative), CardExportFileKind::Mod);
            if (mod.ExportMetadata && mod.ExportMetadata->BonesFramework)
                addPlugin("com.joan6694.illusionplugins.bonesframework", {}, false);
        };
        addPlugin = [&](const std::string &guid, const std::string &minimum, bool optional)
        {
            if (!pluginsIncluded)
                return;
            CheckCancelled(state.Cancellation);
            auto found = state.Plugins.find(Lower(guid));
            if (found == state.Plugins.end())
            {
                if (!optional)
                    issues.insert("缺少插件或无法映射扩展标识：" + guid);
                return;
            }
            auto &selected = found->second.front();
            if (!minimum.empty() && PluginFileIndex::CompareVersions(selected.Definition->Version, minimum) < 0)
                issues.insert("插件版本不足：" + guid + "，需要 " + minimum + "，找到 " + selected.Definition->Version);
            if (std::any_of(found->second.begin(), found->second.end(),
                            [&](auto &candidate)
                            {
                                return candidate.File->Path != selected.File->Path &&
                                       candidate.Definition->Version == selected.Definition->Version;
                            }))
                issues.insert("同一插件 GUID 存在相同版本的多个文件，已按路径确定一个来源，需确认：" + guid);
            if (!visitedPlugins.insert(Lower(guid)).second)
                return;
            addAssembly(*selected.File);
        };
        addAssembly = [&](const PluginFileInfo &assembly)
        {
            CheckCancelled(state.Cancellation);
            if (!visitedAssemblies.insert(Lower(assembly.Path)).second)
                return;
            auto source = Path(assembly.Path);
            if (!fs::exists(source) || static_cast<std::int64_t>(fs::file_size(source)) != assembly.Size ||
                ModifiedTicks(source) != assembly.ModifiedUtcTicks)
            {
                issues.insert("插件在索引后发生变化或已缺失，请重新导出：" + assembly.Path);
                return;
            }
            if (!Within(source, state.Game / L"BepInEx" / L"plugins"))
            {
                issues.insert("插件不在游戏 plugins 目录内：" + assembly.Path);
                return;
            }
            add(source, Utf8(fs::relative(source, state.Game)), CardExportFileKind::Plugin);
            if (!assembly.Error.empty())
                issues.insert(assembly.Path + "：" + assembly.Error);
            for (auto &definition : assembly.Plugins)
                for (auto &dependency : definition.Dependencies)
                    // Soft dependencies are load-order hints for optional integrations. A card that
                    // needs that plugin adds it through its own extension/assembly dependencies.
                    if (!dependency.Optional)
                        addPlugin(dependency.Guid, dependency.MinimumVersion, false);
            for (auto &reference : assembly.References)
            {
                std::vector<const PluginFileInfo *> candidates;
                for (auto &file : state.Index.PluginFiles)
                    if (Lower(file.AssemblyName) == Lower(reference.Name) &&
                        std::all_of(file.Plugins.begin(), file.Plugins.end(),
                                    [&](auto &plugin)
                                    {
                                        auto found = state.Plugins.find(Lower(plugin.Guid));
                                        return found != state.Plugins.end() &&
                                               SamePath(Path(found->second.front().File->Path), Path(file.Path));
                                    }))
                        candidates.push_back(&file);
                std::sort(candidates.begin(), candidates.end(),
                          [&](auto a, auto b)
                          {
                              if ((a->Version == reference.Version) != (b->Version == reference.Version))
                                  return a->Version == reference.Version;
                              auto compare = PluginFileIndex::CompareVersions(a->Version, b->Version);
                              return compare ? compare > 0 : Lower(a->Path) < Lower(b->Path);
                          });
                if (!candidates.empty())
                {
                    if (PluginFileIndex::CompareVersions(candidates.front()->Version, reference.Version) < 0)
                        issues.insert("依赖库版本不足：" + reference.Name + " " + reference.Version);
                    addAssembly(*candidates.front());
                }
                else if (assembly.AssemblyName == "ConfigurationManager" && assembly.Version == "16.4.0.0" &&
                         reference.Name == "BepInEx.BepIn4Patcher")
                    // This version retains SavedKeyboardShortcut for BepInEx 4 callers.
                    // LegacySettingSearcher probes Type.GetType(..., false) before using it;
                    // BepInEx 5 and the character plugins do not require that compatibility API.
                    warnings.insert("Configuration Manager 16.4 的可选 BepInEx 4 兼容组件未安装；使用 BepInEx 5 功能");
                else if (!state.BaseAssembly(reference.Name))
                    issues.insert("缺少依赖库：" + reference.Name + "（" + Utf8(source.filename()) + "）");
            }
            static const std::set<std::string> systemLibraries{
                "kernel32", "user32", "gdi32", "shell32", "shlwapi", "advapi32", "ole32",   "oleaut32", "comdlg32",
                "comctl32", "winmm",  "ntdll", "dwmapi",  "uxtheme", "msvcrt",   "version", "psapi",    "opengl32"};
            for (auto &native : assembly.NativeReferences)
            {
                if (systemLibraries.contains(Lower(Utf8(Path(native).stem()))))
                    continue;
                auto fileName = Lower(native).ends_with(".dll") ? native : native + ".dll";
                if (Path(fileName) != Path(fileName).filename())
                {
                    issues.insert("原生依赖库路径需确认：" + native);
                    continue;
                }
                std::vector<const PluginFileInfo *> matches;
                for (auto &file : state.Index.PluginFiles)
                    if (Lower(Utf8(Path(file.Path).filename())) == Lower(fileName))
                        matches.push_back(&file);
                std::sort(matches.begin(), matches.end(),
                          [&](auto a, auto b)
                          {
                              bool localA = SamePath(Path(a->Path).parent_path(), source.parent_path()),
                                   localB = SamePath(Path(b->Path).parent_path(), source.parent_path());
                              return localA != localB ? localA : Lower(a->Path) < Lower(b->Path);
                          });
                if (!matches.empty())
                    add(Path(matches.front()->Path), Utf8(fs::relative(Path(matches.front()->Path), state.Game)),
                        CardExportFileKind::Plugin);
                else if (!fs::exists(state.Game / L"HoneySelect2_Data" / L"Plugins" / Path(fileName)) &&
                         !fs::exists(state.Game / Path(fileName)))
                    issues.insert("缺少或无法确认原生依赖库：" + native + "（" + Utf8(source.filename()) + "）");
            }
            if (resourcesIncluded)
                addCompanions(assembly);
        };
        std::set<std::string> checkedScripts;
        std::unique_ptr<UnityClassDatabase> scriptDatabase;
        auto checkScripts = [&](const ModFileInfo *owner, const fs::path &physical, const std::string &relative)
        {
            if (!pluginsIncluded ||
                !checkedScripts.insert((owner ? owner->Path : Utf8(physical)) + ":" + relative).second)
                return;
            try
            {
                if (!scriptDatabase)
                {
                    auto database = state.ClassDatabase;
                    if (!database)
                    {
                        std::wstring executable(32768, L'\0');
                        executable.resize(
                            GetModuleFileNameW(nullptr, executable.data(), static_cast<DWORD>(executable.size())));
                        database = fs::path(executable).parent_path() / L"Resources/classdata.tpk";
                    }
                    scriptDatabase = std::make_unique<UnityClassDatabase>(*database);
                }
                Bytes bytes;
                if (owner)
                {
                    ZipArchive archive(Path(owner->Path));
                    auto entry = archive.Find(relative);
                    if (!entry)
                        throw std::runtime_error("资源包条目不存在");
                    bytes = archive.Read(*entry, 512ull * 1024 * 1024);
                }
                else
                    bytes = ReadFile(physical, 512ull * 1024 * 1024, state.Cancellation);
                UnityBundle bundle(bytes, {}, state.Cancellation);
                if (bundle.Entries().empty())
                    throw std::runtime_error("资源包为空");
                auto &entry = bundle.Entries().front();
                UnitySerializedFile file(bundle.ReadEntry(entry.Name, 0, entry.Size), scriptDatabase.get());
                for (auto &asset : file.Assets())
                    if (asset.ClassId == 115)
                    {
                        CheckCancelled(state.Cancellation);
                        auto script = file.Read(asset.PathId);
                        auto name = script.Get("m_AssemblyName").Text;
                        if (Lower(name).ends_with(".dll"))
                            name.resize(name.size() - 4);
                        if (name.empty() || state.BaseAssembly(name))
                            continue;
                        const PluginFileInfo *selected = nullptr;
                        for (auto &candidate : state.Index.PluginFiles)
                            if (Lower(candidate.AssemblyName) == Lower(name))
                                if (!selected ||
                                    PluginFileIndex::CompareVersions(candidate.Version, selected->Version) > 0)
                                    selected = &candidate;
                        if (selected)
                            addAssembly(*selected);
                        else
                            issues.insert("资源组件依赖库缺失：" + name + "（" + relative + "）");
                    }
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &error)
            {
                issues.insert("无法验证资源组件依赖：" + relative + "：" + error.what());
            }
        };
        auto addBundle = [&](const ModFileInfo &owner, const std::string &relative,
                             const std::vector<std::string> &assets, bool texture, bool thumbnail)
        {
            if (Trim(relative).empty() || relative == "0" || relative.starts_with("Resources."))
                return;
            auto normalized = Normalize(relative);
            if (!Lower(normalized).starts_with("abdata/"))
                normalized = "abdata/" + normalized;
            try
            {
                auto physical = ExportPaths::Destination(state.Game, normalized);
                auto inOwner = [&](const std::string &name)
                {
                    return owner.ArchiveEntries &&
                           std::any_of(owner.ArchiveEntries->begin(), owner.ArchiveEntries->end(),
                                       [&](auto &entry)
                                       {
                                           return Lower(Normalize(entry.Name)) == Lower(name);
                                       });
                };
                if (texture && !assets.empty())
                {
                    bool allFound = true;
                    for (auto &asset : assets)
                    {
                        auto png = normalized.substr(0, normalized.size() - Utf8(Path(normalized).extension()).size()) +
                                   "/" + asset + ".png";
                        auto loose = ExportPaths::Destination(state.Game, png);
                        if (inOwner(png))
                            continue;
                        if (fs::exists(loose))
                        {
                            if (resourcesIncluded)
                                add(loose, png, CardExportFileKind::Resource);
                        }
                        else if (auto provider = state.Bundles.find(Lower(png)); provider != state.Bundles.end())
                            addMod(*provider->second);
                        else
                            allFound = false;
                    }
                    if (allFound)
                        return;
                }
                if (inOwner(normalized))
                {
                    if (!texture && !thumbnail)
                        checkScripts(&owner, {}, normalized);
                    return;
                }
                if (fs::exists(physical))
                {
                    if (resourcesIncluded && !state.StockBundles().contains(Lower(normalized)))
                        add(physical, normalized, CardExportFileKind::Resource);
                    if (!texture && !thumbnail)
                        checkScripts(nullptr, physical, normalized);
                    return;
                }
                auto provider = state.Bundles.find(Lower(normalized));
                if (provider != state.Bundles.end())
                {
                    addMod(*provider->second);
                    if (!texture && !thumbnail)
                        checkScripts(provider->second, {}, normalized);
                }
                else if (resourcesIncluded)
                {
                    if (thumbnail)
                        warnings.insert("菜单缩略图缺失，不影响人物外观：" + normalized + "（" + owner.Guid + "）");
                    else
                        issues.insert("资源包缺失：" + normalized + "（" + owner.Guid + "）");
                }
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &error)
            {
                if (resourcesIncluded)
                    issues.insert("资源包路径无效：" + relative + "：" + error.what());
            }
        };
        auto checkList = [&](const ModFileInfo &mod, const ModReference &reference)
        {
            try
            {
                auto &entries = state.ReadLists(mod);
                auto entry = std::find_if(entries.rbegin(), entries.rend(),
                                          [&](auto &e)
                                          {
                                              return e.Category == reference.Category && e.Slot == reference.Slot;
                                          });
                if (entry == entries.rend())
                {
                    issues.insert("MOD 清单项缺失：" + reference.Guid + "（类别 " + std::to_string(reference.Category) +
                                  " / Slot " + std::to_string(reference.Slot) + "）");
                    return;
                }
                auto field = [&](const std::string &name, std::string fallback = {})
                {
                    auto found = entry->Fields.find(name);
                    return found == entry->Fields.end() ? fallback : found->second;
                };
                for (auto &[column, bundle] : entry->Fields)
                    if (column.ends_with("AB") && Lower(bundle).ends_with(".unity3d"))
                    {
                        bool thumbnail = column == "ThumbAB", texture = column != "MainAB";
                        std::vector<std::string> assets;
                        if (column == "MainAB")
                        {
                            texture = (field("MainData").empty() || field("MainData") == "0") &&
                                      (field("MainData02").empty() || field("MainData02") == "0");
                            if (texture)
                                for (auto name : {"MainTex", "AddTex", "GlossTex", "NormalMapTex", "OcclusionMapTex"})
                                    if (field(std::string(name) + "AB", "0").empty() ||
                                        field(std::string(name) + "AB", "0") == "0")
                                        assets.push_back(field(name));
                        }
                        else
                            assets.push_back(field(thumbnail ? "ThumbTex" : column.substr(0, column.size() - 2)));
                        std::erase_if(assets,
                                      [](auto &a)
                                      {
                                          return a.empty() || a == "0";
                                      });
                        addBundle(mod, bundle, assets, texture, thumbnail);
                    }
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &error)
            {
                issues.insert("无法完整检查 MOD 的资源引用：" + Utf8(Path(mod.Path).filename()) + "：" + error.what());
            }
        };
        if (!state.Index.Complete)
            issues.insert("MOD 索引扫描不完整；未找到的资源仍需确认");
        if (pluginsIncluded)
            for (auto &warning : state.Index.Warnings)
                if (warning.starts_with("插件"))
                    issues.insert(warning);
        for (auto &reference : card.Dependencies)
        {
            auto resolution = state.Resolve(reference, {});
            for (auto mod : resolution.Mods)
                addMod(*mod);
            if (!resolution.Found)
                issues.insert("缺失或无法确认 MOD：" + reference.Guid + "（类别 " + std::to_string(reference.Category) +
                              " / Slot " + std::to_string(reference.Slot) + "）");
            else if (resolution.Reference)
                checkList(*state.ByGuid.at(Lower(resolution.Reference->Guid)), *resolution.Reference);
        }
        if (!card.Dependencies.empty())
            addPlugin("com.bepis.bepinex.sideloader", {}, false);
        if (resourcesIncluded)
            for (const auto &guid : resources.BodyGuids)
            {
                const ModFileInfo *found = nullptr;
                for (const auto &mod : state.Mods)
                    if (mod.ExportMetadata &&
                        std::find(mod.ExportMetadata->BodyGuids.begin(), mod.ExportMetadata->BodyGuids.end(), guid) !=
                            mod.ExportMetadata->BodyGuids.end())
                        found = &mod;
                if (found)
                {
                    addMod(*found);
                    addPlugin("com.bepis.bepinex.sideloader", {}, false);
                    if (found->ExportMetadata->BodyBundles.contains(guid))
                        for (const auto &bundle : found->ExportMetadata->BodyBundles.at(guid))
                            addBundle(*found, bundle, {}, false, false);
                }
                else
                    issues.insert("身体素材依赖缺失：" + guid);
            }
        if (!card.PluginDataIds.empty())
            addPlugin("com.bepis.bepinex.extendedsave", {}, false);
        if (pluginsIncluded)
            for (auto &id : card.PluginDataIds)
            {
                if (resources.InactivePlugins.contains(id))
                    continue;
                auto guid = PluginFileIndex::PluginGuid(id);
                if (guid == id && !state.Plugins.contains(Lower(guid)))
                {
                    std::map<std::string, std::string> aliases;
                    for (auto &file : state.Index.PluginFiles)
                        for (auto &plugin : file.Plugins)
                            if (std::find(plugin.DataIds.begin(), plugin.DataIds.end(), id) != plugin.DataIds.end())
                                aliases[Lower(plugin.Guid)] = plugin.Guid;
                    if (aliases.size() == 1)
                        guid = aliases.begin()->second;
                }
                addPlugin(guid, {}, false);
                if (resourcesIncluded && !KnownExtension(id))
                    issues.insert("未确认扩展的外部资源需求：" + id);
            }
        issues.insert(resources.Issues.begin(), resources.Issues.end());
        if (resourcesIncluded)
            for (auto &path : resources.Paths)
            {
                try
                {
                    auto supplied = Path(path);
                    bool absolute = supplied.has_root_path();
                    auto source = absolute ? fs::absolute(supplied) : ExportPaths::Destination(state.Game, path);
                    if (!Within(source, state.Game / L"UserData"))
                    {
                        issues.insert("无法移植的外部资源路径：" + path);
                        continue;
                    }
                    if (absolute)
                        issues.insert("卡片保存了绝对资源路径，复制到其它游戏位置后可能需重新指定：" + path);
                    add(source, Utf8(fs::relative(source, state.Game)), CardExportFileKind::Resource);
                }
                catch (const std::exception &error)
                {
                    issues.insert("外部资源路径无法确认：" + path + "：" + error.what());
                }
            }
        for (auto &shader : resources.Shaders)
        {
            auto found = state.Shaders.find(shader);
            if (found != state.Shaders.end())
            {
                addMod(*found->second.first);
                for (auto &bundle : found->second.second->Bundles)
                    addBundle(*found->second.first, bundle, {}, false, false);
            }
            else if (resourcesIncluded && !state.EmbeddedShaders().contains(shader))
                issues.insert("未确认自定义 Shader 的资源来源：" + shader);
        }
        if (resourcesIncluded)
            issues.insert(state.StockWarnings.begin(), state.StockWarnings.end());
        result.Issues.assign(issues.begin(), issues.end());
        result.Warnings.assign(warnings.begin(), warnings.end());
        return result;
    }
}
