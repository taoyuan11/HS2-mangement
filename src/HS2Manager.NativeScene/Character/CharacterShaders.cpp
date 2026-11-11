#include "Character/CharacterShaders.h"
#include "Scene/SceneXml.h"
#include "Archive/ZipArchive.h"
#include "Serialization/CliMetadata.h"
#include "Mods/ModIndex.h"
#include <fstream>
#include <cmath>
#include <set>
#include <sstream>

namespace hs2
{
    namespace
    {
        std::string Lower(std::string text)
        {
            for (auto &c : text)
                c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
            return text;
        }

        std::string Trim(std::string text)
        {
            auto first = text.find_first_not_of(" \t\r\n"), last = text.find_last_not_of(" \t\r\n");
            return first == text.npos ? std::string{} : text.substr(first, last - first + 1);
        }

        struct Property
        {
            std::string Name, Type, Value, Bundle;
        };

        struct Entry
        {
            fs::path Archive;
            std::string Name, Bundle, Asset;
            int Queue = -1;
            bool Optimization = true;
            std::vector<Property> Properties;
        };

        void Read(const SceneXmlNode &root, const fs::path &archive, std::map<std::string, Entry> &entries)
        {
            for (auto &node : root.Children)
            {
                if (node.Name == "Shader")
                {
                    auto name = node.Attribute("Name");
                    if (name.empty())
                        continue;
                    Entry entry;
                    entry.Name = name;
                    entry.Archive = archive;
                    entry.Bundle = node.Attribute("AssetBundle");
                    entry.Asset = node.Attribute("Asset");
                    try
                    {
                        entry.Queue = std::stoi(node.Attribute("RenderQueue"));
                    }
                    catch (const std::exception &)
                    {
                    }
                    entry.Optimization = Lower(node.Attribute("ShaderOptimization")) != "false";
                    for (auto &property : node.Children)
                        if (property.Name == "Property")
                            entry.Properties.push_back({property.Attribute("Name"), property.Attribute("Type"),
                                                        property.Attribute("DefaultValue"),
                                                        property.Attribute("DefaultValueAssetBundle")});
                    entries[name] = std::move(entry);
                }
                Read(node, archive, entries);
            }
        }

        std::map<std::string, Entry> Catalog(const fs::path &data, const std::vector<fs::path> &plugins,
                                             std::stop_token cancellation, const PreviewResources &resources)
        {
            std::vector<ModFileInfo> mods;
            if (resources)
            {
                mods = resources->Index.Files;
                // Shader 清单按名称覆盖，低优先级先读，让包内清单最后生效。
                std::reverse(mods.begin(), mods.end());
            }
            else
            {
                CardRepository repository(data);
                auto index = repository.LoadMetadata("mods-v1");
                if (!index)
                    throw std::runtime_error("MOD index is required for shader manifests.");
                mods = NativeModSelection::Select(index->get<ModIndex>().Files);
            }
            std::string stamp;
            for (auto &mod : mods)
                stamp += mod.Path + "|" + std::to_string(mod.Size) + "|" + std::to_string(mod.ModifiedUtcTicks) + "\n";
            for (auto &plugin : plugins)
                stamp += Utf8(plugin) + "|" + std::to_string(fs::file_size(plugin)) + "|" +
                         std::to_string(ModifiedTicks(plugin)) + "\n";
            auto cache =
                data / "NativeCpp" / "resources" /
                Path("shaders-v1-" + Sha256(std::span(reinterpret_cast<const uint8_t *>(stamp.data()), stamp.size())) +
                     ".json");
            std::map<std::string, Entry> result;
            try
            {
                if (fs::is_regular_file(cache))
                {
                    auto encoded = Json::parse(ReadFile(cache, 32 * 1024 * 1024));
                    for (auto &row : encoded)
                    {
                        Entry entry;
                        entry.Name = row.at("Name");
                        entry.Archive = Path(row.at("Archive").get<std::string>());
                        entry.Bundle = row.at("Bundle");
                        entry.Asset = row.at("Asset");
                        entry.Queue = row.at("Queue");
                        entry.Optimization = row.at("Optimization");
                        for (auto &p : row.at("Properties"))
                            entry.Properties.push_back({p.at("Name"), p.at("Type"), p.at("Value"), p.at("Bundle")});
                        result[entry.Name] = std::move(entry);
                    }
                    return result;
                }
            }
            catch (const std::exception &)
            {
                result.clear();
            }
            for (auto &plugin : plugins)
            {
                CheckCancelled(cancellation);
                metadata::Assembly assembly(plugin);
                auto resource = assembly.EmbeddedResource("KK_Plugins.Resources.default.xml");
                if (!resource.empty())
                    Read(ReadSceneXml(resource), plugin, result);
            }
            for (auto &mod : mods)
            {
                CheckCancelled(cancellation);
                if (mod.ExportMetadata && mod.ExportMetadata->Shaders.empty())
                    continue;
                try
                {
                    ZipArchive zip(Path(mod.Path));
                    auto *manifest = zip.Find("manifest.xml");
                    if (!manifest || manifest->Size > 2 * 1024 * 1024)
                        continue;
                    auto root = ReadSceneXml(zip.Read(*manifest, 2 * 1024 * 1024));
                    auto *editor = root.Child("MaterialEditor");
                    if (!editor)
                        editor = root.Child("HS2_MaterialEditor");
                    if (editor)
                        Read(*editor, Path(mod.Path), result);
                }
                catch (const Cancelled &)
                {
                    throw;
                }
                catch (const std::exception &)
                {
                }
            }
            Json encoded = Json::array();
            for (auto &[name, entry] : result)
            {
                Json properties = Json::array();
                for (auto &p : entry.Properties)
                    properties.push_back(
                        {{"Name", p.Name}, {"Type", p.Type}, {"Value", p.Value}, {"Bundle", p.Bundle}});
                encoded.push_back({{"Name", entry.Name},
                                   {"Archive", Utf8(entry.Archive)},
                                   {"Bundle", entry.Bundle},
                                   {"Asset", entry.Asset},
                                   {"Queue", entry.Queue},
                                   {"Optimization", entry.Optimization},
                                   {"Properties", properties}});
            }
            WriteJson(cache, encoded);
            return result;
        }
    }

    void ReadCharacterShaders(const fs::path &game, const fs::path &data, CharacterCatalog &catalog,
                              NativeGpuAssetStore &assets, CharacterDefinition &definition,
                              std::stop_token cancellation, PreviewResources resources)
    {
        std::vector<fs::path> plugins;
        auto pluginDirectory = game / "BepInEx/plugins";
        if (resources)
        {
            for (auto &plugin : resources->Index.PluginFiles)
                if (Lower(Utf8(Path(plugin.Path).filename())).find("materialeditor") != std::string::npos)
                    plugins.push_back(Path(plugin.Path));
            std::reverse(plugins.begin(), plugins.end());
        }
        else if (fs::is_directory(pluginDirectory))
            for (auto &file : fs::recursive_directory_iterator(pluginDirectory))
            {
                CheckCancelled(cancellation);
                auto name = Lower(Utf8(file.path().filename()));
                if (file.is_regular_file() && name.ends_with(".dll") &&
                    name.find("materialeditor") != std::string::npos)
                    plugins.push_back(file.path());
            }
        if (!resources)
            std::sort(plugins.begin(), plugins.end(),
                      [](auto &a, auto &b)
                      {
                          return Lower(Utf8(a)) < Lower(Utf8(b));
                      });
        bool optimization = !plugins.empty();
        std::ifstream config(
            PreviewResourceFile(game, resources, "BepInEx/config/com.deathweasel.bepinex.materialeditor.cfg"));
        for (std::string line; std::getline(config, line);)
        {
            auto at = line.find('=');
            if (at != line.npos && Trim(line.substr(0, at)) == "Shader Optimization")
            {
                auto value = Lower(Trim(line.substr(at + 1)));
                if (value == "true" || value == "false")
                    optimization = value == "true";
            }
        }
        std::set<std::string> wanted, automatic;
        for (auto &edit : definition.MaterialEdits)
            if (edit.Kind == "shader" && !edit.Text.empty())
                wanted.insert(edit.Text);
        if (optimization)
            for (auto &path : assets.Paths())
            {
                CheckCancelled(cancellation);
                auto reader = assets.Reader(path);
                assets.IndexShaderNames(reader);
                for (auto &object : reader->AssetIndex())
                    if (object.ClassId == 48)
                    {
                        // 自动替换只需要名称，完整着色器参数和程序在实际使用时再解析。
                        auto name = reader->AssetName(object.PathId);
                        if (!name.empty())
                            automatic.insert(std::move(name));
                    }
            }
        if (wanted.empty() && automatic.empty())
            return;
        auto entries = Catalog(data, plugins, cancellation, resources);
        auto names = wanted;
        names.insert(automatic.begin(), automatic.end());
        auto reference = [&](const std::string &bundle, const std::string &asset, const fs::path &source)
        {
            CheckCancelled(cancellation);
            if (!bundle.starts_with("Resources."))
            {
                auto archive = Lower(Utf8(source.extension())) == ".dll" ? fs::path{} : source;
                auto value = catalog.Asset(bundle, asset, archive);
                for (auto &dependency : catalog.Dependencies(bundle, archive))
                    assets.AddPath(dependency);
                assets.AddPath(value.Bundle);
                return value;
            }
            metadata::Assembly assembly(source);
            auto bytes = assembly.EmbeddedResource("KK_Plugins." + bundle);
            const uint8_t magic[]{'U', 'n', 'i', 't', 'y', 'F', 'S', 0};
            if (bytes.size() < 48 || bytes.size() > 128 * 1024 * 1024 ||
                !std::equal(std::begin(magic), std::end(magic), bytes.begin()))
                throw std::runtime_error("Invalid embedded MaterialEditor shader bundle: " + bundle);
            auto hash = Sha256(bytes);
            auto path = data / "NativeCpp" / "resources" / "shader-data" / Path(hash + ".unity3d");
            if (!fs::is_regular_file(path) || HashFile(path, cancellation) != hash)
                WriteAtomic(path, bytes);
            assets.AddPath(path);
            return AssetReference{path, asset};
        };
        for (auto &name : names)
        {
            CheckCancelled(cancellation);
            auto found = entries.find(name);
            if (found == entries.end())
                continue;
            auto &entry = found->second;
            bool replace = automatic.contains(name) && entry.Optimization;
            if (!wanted.contains(name) && !replace)
                continue;
            try
            {
                ShaderDefinition shader;
                shader.Name = name;
                shader.AutoReplace = replace;
                shader.RenderQueue = entry.Queue;
                if (!entry.Bundle.empty())
                    shader.Source = reference(entry.Bundle, entry.Asset.empty() ? name : entry.Asset, entry.Archive);
                for (auto &property : entry.Properties)
                {
                    if (property.Value.empty())
                        continue;
                    ShaderDefault value;
                    auto start = property.Name.find_first_not_of('_');
                    value.Property = "_" + (start == std::string::npos ? std::string{} : property.Name.substr(start));
                    value.Type = property.Type;
                    if (value.Type == "Texture")
                    {
                        if (property.Bundle.empty())
                            continue;
                        value.Texture = reference(property.Bundle, property.Value, entry.Archive);
                    }
                    else
                    {
                        std::istringstream input(property.Value);
                        for (std::string text; std::getline(input, text, ',');)
                        {
                            size_t consumed = 0;
                            float number = std::stof(text, &consumed);
                            if (!std::isfinite(number) || !Trim(text.substr(consumed)).empty())
                                throw std::runtime_error("Invalid MaterialEditor shader default.");
                            value.Values.push_back(number);
                        }
                    }
                    shader.Defaults.push_back(std::move(value));
                }
                definition.Shaders.push_back(std::move(shader));
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &e)
            {
                definition.Diagnostics.push_back("MaterialEditor Shader " + name + "：" + e.what());
            }
        }
    }
}
