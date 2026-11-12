#include "CreationService.h"
#include "Assets/MonoSchema.h"
#include <algorithm>

namespace hs2::creation
{
    std::map<std::string, std::vector<CharacterChoice>> InformationChoices(const fs::path &game, const fs::path &tpk,
                                                                           std::stop_token token)
    {
        auto database = std::make_shared<UnityClassDatabase>(tpk);
        NativeMonoSchema schemas(game / "HoneySelect2_Data/Managed", database);
        std::vector<fs::path> paths;
        for (auto relative : {"abdata/etcetra/list/config", "abdata/etcetra/list/parametername"})
        {
            auto directory = game / relative;
            if (!fs::is_directory(directory))
                continue;
            for (auto &entry : fs::recursive_directory_iterator(directory))
                if (entry.is_regular_file() && entry.path().extension() == L".unity3d")
                    paths.push_back(entry.path());
        }
        std::sort(paths.begin(), paths.end());
        NativeGpuAssetStore store(paths, game, database, token);
        std::map<int, std::pair<int, std::string>> voices;
        std::map<int, Parameter> parameters;
        for (auto &path : paths)
        {
            CheckCancelled(token);
            auto reader = store.Reader(path);
            for (auto &entry : reader->AssetIndex())
                if (entry.ClassId == 114)
                {
                    auto header = reader->MetadataHeader(entry.PathId);
                    if (!header.Has("m_Script"))
                        continue;
                    auto pointer = header.Get("m_Script");
                    auto script =
                        store.Metadata({path, static_cast<int>(pointer.Int("m_FileID")), pointer.Int("m_PathID")});
                    auto name = script.String("m_ClassName");
                    if (name != "VoiceInfo" && name != "ParameterNameInfo")
                        continue;
                    auto schema = schemas.Type(script.String("m_AssemblyName"), script.String("m_Namespace"), name,
                                               reader->UnityVersion());
                    const auto &data = reader->ReadScript(entry.PathId, schema);
                    for (auto &row : data.Get("param").Array())
                        if (name == "VoiceInfo")
                        {
                            int id = static_cast<int>(row.Int("No", -1));
                            if (id < 0)
                                continue;
                            auto label = row.String("ZhCN");
                            if (label.empty())
                                label = row.String("Personality");
                            if (!label.empty())
                                voices[id] = {static_cast<int>(row.Int("Sort")), label};
                        }
                        else
                            parameters[static_cast<int>(row.Int("id"))] = row;
                }
        }
        std::map<std::string, std::vector<CharacterChoice>> result;
        std::vector<std::pair<int, std::pair<int, std::string>>> ordered(voices.begin(), voices.end());
        std::stable_sort(ordered.begin(), ordered.end(),
                         [](auto &a, auto &b)
                         {
                             return a.second.first < b.second.first;
                         });
        for (auto &[id, value] : ordered)
            result["personality"].push_back({std::int64_t(id), value.second});
        for (auto key : {"trait", "mind", "hattribute", "state"})
        {
            int index = 0;
            for (auto &[id, row] : parameters)
            {
                auto labels = row.Get(key).Array();
                std::string label = labels.size() > 2 ? labels[2].Text : labels.empty() ? "" : labels[0].Text;
                if (label.empty() && !labels.empty())
                    label = labels[0].Text;
                if (!label.empty())
                    result[key == std::string_view("hattribute") ? "hAttribute" : key].push_back(
                        {std::int64_t(index++), label});
                (void)id;
            }
        }
        if (result["personality"].empty())
            throw std::runtime_error("游戏未提供可识别的原版性格清单；现有值保留");
        return result;
    }
}
