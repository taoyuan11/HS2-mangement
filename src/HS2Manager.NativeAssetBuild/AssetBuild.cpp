#include "AssetBundleBuild.h"
#include <map>
#include <sstream>

namespace hs2::asset
{
    void WriteSideloaderList(const MaterialTemplate &source, const Json &spec, const Json &built,
                             const fs::path &output)
    {
        // Preserve slot-specific flags, then replace every resource reference for the generated prefab.
        std::map<std::string, std::string> values(source.Entry.Values.begin(), source.Entry.Values.end());
        values["ID"] = std::to_string(built.at("slot_id").get<int>());
        values["Kind"] = "0";
        values["Name"] = spec.value("name", spec.at("asset_key").get<std::string>());
        values["Possess"] = "1";
        values["MainManifest"] = "abdata";
        values["MainAB"] = built.at("bundle");
        values["MainData"] = values["MainData02"] = built.at("prefab");
        values["Parent"] = spec.value("parent", source.Entry.Get("Parent"));
        // Static accessories use their attachment's local space and have no skin to rebind.
        values["Weights"] = spec.at("kind") == "accessory" ? "0" : "1";
        if (spec.at("kind") == "clothes")
            values["StateType"] = "0";
        for (auto key : {"TexAB", "TexD", "TexC", "ThumbAB", "ThumbTex", "MainTex", "MainTex02", "MainTex03",
                         "ColorMaskTex", "ColorMask02Tex", "ColorMask03Tex", "MaskTex", "MaskTex2"})
            values[key] = "0";
        for (auto key :
             {"OverBodyMask", "OverBraMask", "OverBodyBMask", "OverInnerTBMask", "OverInnerBMask", "OverPanstMask"})
        {
            values[key] = "0";
            values[std::string(key) + "AB"] = "0";
        }
        for (const auto &[key, value] : built.at("masks").items())
        {
            values[key] = value.get<std::string>();
            values[key + "AB"] = built.at("bundle");
        }
        auto csv = [](const std::string &value)
        {
            if (value.find_first_of(",\r\n") != std::string::npos)
                throw std::invalid_argument("HS2 素材清单字段不能包含逗号或换行");
            return value;
        };
        std::string header, row;
        auto append = [&](const std::string &key)
        {
            if (!header.empty())
            {
                header += ',';
                row += ',';
            }
            header += csv(key);
            row += csv(values.at(key));
        };
        append("ID");
        for (const auto &[key, _] : values)
            if (key != "ID")
                append(key);
        auto content =
            std::to_string(source.Entry.Category) + "\r\n0\r\nhs2agent_generated\r\n" + header + "\r\n" + row + "\r\n";
        auto path =
            output / "abdata/list/characustom" / (spec.at("sex").get<int>() == 0 ? "00" : "01") /
            (spec.contains("package_guid") ? "hs2manager_" + std::to_string(built.at("slot_id").get<int>()) + ".csv"
                                           : "hs2agent.csv");
        WriteAtomic(path, Bytes(content.begin(), content.end()));
    }
}
