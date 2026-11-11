#include "Character/CharacterBodyCatalog.h"
#include <set>
#include "Scene/SceneXml.h"
#include "Archive/ZipArchive.h"
#include "Mods/ModIndex.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <fstream>
#include <future>
#include <map>
#include <set>
#include <sstream>
#include <thread>

namespace hs2
{
    namespace
    {
        std::string Trim(std::string value)
        {
            auto a = value.find_first_not_of(" \t\r\n"), b = value.find_last_not_of(" \t\r\n");
            return a == value.npos ? std::string{} : value.substr(a, b - a + 1);
        }

        std::string Lower(std::string value)
        {
            for (auto &c : value)
                if (c >= 'A' && c <= 'Z')
                    c += 32;
            return value;
        }

        std::map<std::string, std::string> Config(const fs::path &path)
        {
            std::map<std::string, std::string> values;
            std::ifstream input(path);
            std::string section;
            for (std::string line; std::getline(input, line);)
            {
                line = Trim(line);
                if (line.empty() || line.starts_with('#'))
                    continue;
                if (line.front() == '[' && line.back() == ']')
                {
                    section = line.substr(1, line.size() - 2);
                    continue;
                }
                auto at = line.find('=');
                if (at != line.npos)
                    values[section + "/" + Trim(line.substr(0, at))] = Trim(line.substr(at + 1));
            }
            return values;
        }

        bool Plugin(const fs::path &game, std::wstring_view name, const PreviewResources &resources)
        {
            if (resources)
                return resources->Plugin(name).has_value();
            auto directory = game / "BepInEx/plugins";
            if (!fs::is_directory(directory))
                return false;
            for (auto &f : fs::recursive_directory_iterator(directory))
                if (f.is_regular_file() && f.path().filename() == name)
                    return true;
            return false;
        }

        // System.Random 的减法随机数算法属于已保存选择结果的兼容约定。
        int RandomChoice(int seed, int count)
        {
            constexpr int big = 2147483647;
            std::array<int, 56> values{};
            int subtraction = seed == INT_MIN ? INT_MAX : std::abs(seed), mj = 161803398 - subtraction;
            values[55] = mj;
            int mk = 1;
            for (int i = 1; i < 55; ++i)
            {
                int index = 21 * i % 55;
                values[index] = mk;
                mk = mj - mk;
                if (mk < 0)
                    mk += big;
                mj = values[index];
            }
            for (int pass = 1; pass < 5; ++pass)
                for (int i = 1; i < 56; ++i)
                {
                    values[i] -= values[1 + (i + 30) % 55];
                    if (values[i] < 0)
                        values[i] += big;
                }
            int result = values[1] - values[22];
            if (result == big)
                --result;
            if (result < 0)
                result += big;
            return static_cast<int>((result * (1.0 / big)) * count);
        }

        Vector3 XmlVector(const SceneXmlNode *value, const char *attribute, float fallback)
        {
            if (!value || !value->Attributes.contains(attribute))
                return {fallback, fallback, fallback};
            std::istringstream input(value->Attribute(attribute));
            std::array<float, 3> out;
            for (int i = 0; i < 3; ++i)
            {
                std::string cell;
                if (!std::getline(input, cell, ','))
                    throw std::runtime_error("Heelz vector is incomplete.");
                out[i] = std::stof(cell);
                if (!std::isfinite(out[i]))
                    throw std::runtime_error("Heelz vector is non-finite.");
            }
            std::string extra;
            if (std::getline(input, extra, ','))
                throw std::runtime_error("Heelz vector is too long.");
            return {out[0], out[1], out[2]};
        }
    }

    std::vector<BodyCatalogEntry> ResolveCharacterBody(const fs::path &game, const fs::path &data,
                                                       const CharacterDocument &document, const Parameter &uncensor,
                                                       std::stop_token cancellation, PreviewResources resources,
                                                       std::optional<std::span<const ModFileInfo>> selectedMods)
    {
        if (!Plugin(game, L"HS2_UncensorSelector.dll", resources))
            return {};
        std::vector<ModFileInfo> loadedMods;
        if (resources)
        {
            loadedMods = resources->Index.Files;
            // 身体与迁移清单采用后读覆盖，因此从本机、共享到角色目录依次读取。
            std::reverse(loadedMods.begin(), loadedMods.end());
            selectedMods = loadedMods;
        }
        else if (!selectedMods)
        {
            CardRepository repository(data);
            auto stored = repository.LoadMetadata("mods-v1");
            if (!stored)
                throw std::runtime_error("MOD index is required for body selection.");
            loadedMods = NativeModSelection::Select(stored->get<ModIndex>().Files);
            selectedMods = loadedMods;
        }
        const auto mods = *selectedMods;
        std::vector<BodyCatalogEntry> entries{
            {"Body", "Default.Body.Female", "Default Body F", 1, false, {}, "chara/oo_base.unity3d", "p_cf_body_00"},
            {"Penis", "Default.Penis", "Mosaic Penis", -1, false, {}, "chara/oo_base.unity3d", "p_cm_body_00"},
            {"Balls", "Default.Balls", "Mosaic Balls", -1, false, {}, "chara/oo_base.unity3d", "p_cm_body_00"}};
        std::map<std::string, std::array<std::string, 3>> migrations;
        std::string fingerprint;
        for (auto &mod : mods)
            fingerprint +=
                mod.Path + "|" + std::to_string(mod.Size) + "|" + std::to_string(mod.ModifiedUtcTicks) + "\n";
        auto cache = data / "NativeCpp" / "resources" /
                     Path("body-v1-" +
                          Sha256(std::span(reinterpret_cast<const uint8_t *>(fingerprint.data()), fingerprint.size())) +
                          ".json");
        bool cached = false;
        try
        {
            if (fs::is_regular_file(cache))
            {
                auto saved = Json::parse(ReadFile(cache, 32 * 1024 * 1024));
                entries.clear();
                for (auto &row : saved.at("Entries"))
                    entries.push_back({row.at("Kind"), row.at("Guid"), row.at("Name"), row.at("Sex"), row.at("Random"),
                                       Path(row.at("Archive").get<std::string>()), row.at("Bundle"), row.at("Asset")});
                migrations = saved.at("Migrations").get<decltype(migrations)>();
                cached = true;
            }
        }
        catch (const std::exception &)
        {
        }
        if (!cached)
        {
            auto add = [&](BodyCatalogEntry entry)
            {
                auto found = std::find_if(entries.begin(), entries.end(),
                                          [&](auto &e)
                                          {
                                              return e.Kind == entry.Kind && e.Guid == entry.Guid;
                                          });
                if (found == entries.end())
                    entries.push_back(std::move(entry));
                else
                    *found = std::move(entry);
            };
            // 首次读取身体清单需要打开所有 MOD；限量并行读取，仍按原顺序应用覆盖与迁移规则。
            // future 析构会等待尚未结束的读取，取消或异常不能留下后台任务。
            const auto workers = std::clamp(std::thread::hardware_concurrency(), 1u, 4u);
            for (size_t first = 0; first < mods.size(); first += workers)
            {
                CheckCancelled(cancellation);
                auto end = std::min(mods.size(), first + workers);
                std::vector<std::future<SceneXmlNode>> pending;
                for (size_t i = first; i < end; ++i)
                    pending.push_back(std::async(std::launch::async,
                                                 [path = Path(mods[i].Path), cancellation]
                                                 {
                                                     CheckCancelled(cancellation);
                                                     ZipArchive zip(path);
                                                     auto *manifest = zip.Find("manifest.xml");
                                                     if (!manifest || manifest->Size > 2 * 1024 * 1024)
                                                         return SceneXmlNode{};
                                                     auto xml = ReadSceneXml(zip.Read(*manifest, 2 * 1024 * 1024));
                                                     CheckCancelled(cancellation);
                                                     return xml;
                                                 }));
                for (size_t i = first; i < end; ++i)
                {
                    CheckCancelled(cancellation);
                    auto &mod = mods[i];
                    auto xml = pending[i - first].get();
                    auto *body = xml.Child("KK_UncensorSelector");
                    if (!body)
                        continue;
                    for (std::string kind : {"Body", "Penis", "Balls"})
                        for (auto &value : body->Children)
                            if (value.Name == Lower(kind))
                            {
                                auto guid = value.Value("guid");
                                if (guid.empty())
                                    continue;
                                bool isBody = kind == "Body";
                                int sex = isBody ? (Lower(value.Value("sex")) == "female" ? 1 : 0) : -1;
                                auto *source = isBody ? value.Child("oo_base") : &value;
                                auto bundle = source   ? source->Value("file", isBody ? "chara/oo_base.unity3d" : "")
                                              : isBody ? "chara/oo_base.unity3d"
                                                       : "";
                                auto asset =
                                    source   ? source->Value("asset",
                                                             isBody ? (sex == 0 ? "p_cm_body_00" : "p_cf_body_00") : "")
                                    : isBody ? (sex == 0 ? "p_cm_body_00" : "p_cf_body_00")
                                             : "";
                                if (bundle.empty() || asset.empty() || (isBody && bundle == "chara/oo_base.unity3d") ||
                                    (!isBody && value.Value("displayName").empty()))
                                    continue;
                                auto random = Lower(value.Value("allowRandom"));
                                add({kind, guid, value.Value("displayName", guid), sex,
                                     random != "false" && random != "0", Path(mod.Path), bundle, asset});
                            }
                    for (auto &value : body->Children)
                        if (value.Name == "migration")
                        {
                            auto guid = value.Value("guidUncensor");
                            if (!guid.empty())
                                migrations[guid] = {value.Value("guidBody"), value.Value("guidPenis"),
                                                    value.Value("guidBalls")};
                        }
                }
            }
            Json encoded = Json::array();
            for (auto &e : entries)
                encoded.push_back(Json{{"Kind", e.Kind},
                                       {"Guid", e.Guid},
                                       {"Name", e.Name},
                                       {"Sex", e.Sex},
                                       {"Random", e.Random},
                                       {"Archive", Utf8(e.Archive)},
                                       {"Bundle", e.Bundle},
                                       {"Asset", e.Asset}});
            WriteJson(cache, Json{{"Entries", encoded}, {"Migrations", migrations}});
        }
        auto config =
            Config(PreviewResourceFile(game, resources, "BepInEx/config/com.deathweasel.bepinex.uncensorselector.cfg"));
        auto parameter = ParameterOf(MessagePack::Parse(document.BlockBytes("Parameter")));
        float voice = parameter.Float("voiceRate", .5f);
        if (!Plugin(game, L"HS2_SliderUnlocker.dll", resources))
            voice = std::clamp(voice, 0.f, 1.f);
        float pitch = .94f + (1.06f - .94f) * voice;
        int seed = static_cast<int>(parameter.Int("birthDay") + parameter.Int("personality")) +
                   static_cast<int>(pitch * 100.f);
        auto legacy = uncensor.String("UncensorGUID");
        std::vector<BodyCatalogEntry> result;
        int part = 0;
        for (std::string kind : {"Body", "Penis", "Balls"})
        {
            auto guid = migrations.contains(legacy) ? migrations.at(legacy)[part] : uncensor.String(kind + "GUID");
            ++part;
            std::vector<const BodyCatalogEntry *> values;
            for (auto &e : entries)
                if (e.Kind == kind)
                    values.push_back(&e);
            const BodyCatalogEntry *selected = nullptr;
            for (auto *e : values)
                if (e->Guid == guid && (kind != "Body" || e->Sex == document.Sex()))
                {
                    selected = e;
                    break;
                }
            std::string sex = document.Sex() == 0 ? "Male" : "Female";
            if (!selected)
            {
                auto choice = config.contains("Config/Default " + sex + " " + kind)
                                  ? config.at("Config/Default " + sex + " " + kind)
                                  : "Random";
                for (auto *e : values)
                    if ((kind == "Body" ? "[" + std::string(e->Sex == 0 ? "Male" : "Female") + "] " : std::string{}) +
                            e->Name ==
                        choice)
                    {
                        selected = e;
                        break;
                    }
            }
            if (!selected)
            {
                std::set<std::string> excluded;
                auto text = config["Random Excluded/" + sex + " " + kind];
                std::replace(text.begin(), text.end(), ';', ',');
                std::istringstream words(text);
                for (std::string word; std::getline(words, word, ',');)
                    excluded.insert(Trim(word));
                std::vector<const BodyCatalogEntry *> candidates;
                for (auto *e : values)
                    if (e->Random && (kind != "Body" || e->Sex == document.Sex()) && !excluded.contains(e->Guid))
                        candidates.push_back(e);
                if (!candidates.empty())
                    selected = candidates[RandomChoice(seed, static_cast<int>(candidates.size()))];
                else
                    for (auto *e : values)
                        if (e->Guid == "Default." + kind + (kind == "Body" ? "." + sex : std::string{}))
                        {
                            selected = e;
                            break;
                        }
            }
            if (selected)
                result.push_back(*selected);
        }
        return result;
    }

    HeelDefinition ReadCharacterHeels(const NativeAssetEntry *shoe, std::vector<std::string> &diagnostics)
    {
        HeelDefinition result;
        if (!shoe || shoe->Archive.empty())
            return result;
        try
        {
            ZipArchive zip(shoe->Archive);
            auto *manifest = zip.Find("manifest.xml");
            if (!manifest)
                return result;
            auto root = ReadSceneXml(zip.Read(*manifest, 4 * 1024 * 1024));
            auto *data = root.Child("AI_HeelsData");
            if (!data)
                return result;
            for (auto &heel : data->Children)
                if (heel.Name == "heel" && heel.Attribute("id") == std::to_string(shoe->Id))
                {
                    result.Height = XmlVector(heel.Child("root"), "vec", 0).y;
                    for (auto &[key, name] : std::vector<std::pair<std::string, std::string>>{
                             {"foot01", "cf_J_Foot01"}, {"foot02", "cf_J_Foot02"}, {"toes01", "cf_J_Toes01"}})
                    {
                        auto *value = heel.Child(key);
                        if (!value)
                            continue;
                        auto *roll = value->Child("roll");
                        auto rotation = XmlVector(roll, "vec", 0);
                        if (roll && roll->Attributes.contains("min") && roll->Attributes.contains("max"))
                        {
                            auto lo = XmlVector(roll, "min", 0), hi = XmlVector(roll, "max", 0);
                            rotation = {std::clamp(rotation.x, lo.x, hi.x), std::clamp(rotation.y, lo.y, hi.y),
                                        std::clamp(rotation.z, lo.z, hi.z)};
                        }
                        for (std::string side : {"_L", "_R"})
                            result.Bones.push_back({name + side, XmlVector(value->Child("move"), "vec", 0), rotation,
                                                    XmlVector(value->Child("scale"), "vec", 1),
                                                    Lower(value->Attribute("fixed")) == "true"});
                    }
                    break;
                }
        }
        catch (const std::exception &e)
        {
            diagnostics.push_back("Heelz 鞋履参数读取失败：" + std::string(e.what()));
        }
        return result;
    }

    std::optional<ColliderSupportDefinition> ReadCharacterColliders(const fs::path &game, PreviewResources resources)
    {
        if (!Plugin(game, L"HS2_Colliders.dll", resources))
            return {};
        ColliderSupportDefinition result{true, true};
        auto config =
            Config(PreviewResourceFile(game, resources, "BepInEx/config/com.deathweasel.bepinex.colliders.cfg"));
        for (auto &[key, value] : config)
        {
            if (key.ends_with("/Breast Colliders"))
                result.Breast = Lower(value) == "true";
            else if (key.ends_with("/Floor Collider"))
                result.Floor = Lower(value) == "true";
        }
        return result;
    }
}
