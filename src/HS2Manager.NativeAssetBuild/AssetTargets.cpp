#include "AssetTargets.h"
#include "Editing/CharacterEditComponents.h"
#include "Animation/SimulationMath.h"
#include <algorithm>
#include <cmath>
#include <map>
#include <regex>
#include <set>

namespace hs2::asset
{
    using sim::Require;

    Json CreationTarget(const CharacterDocument &document, const std::string &path)
    {
        Json result{{"target_path", path}, {"schema_version", 3}};
        if (path == "face.moleLayout" || (path.starts_with("face.makeup.paintInfo[") && path.ends_with(".layout")))
        {
            result["kind"] = "layout";
            result["category"] = path == "face.moleLayout" ? 6 : 7;
            result["inputs"] = {"layout"};
            result["components"] = {"width", "height", "x", "y"};
            return result;
        }
        auto binding = CharacterAssetReferences::Describe(document, path);
        if (!binding)
            return nullptr;
        result["category"] = binding->Category;
        if (path == "face.headId")
        {
            result["kind"] = "head";
            result["inputs"] = {"source_head"};
            result["constraints"] = {"same_topology", "bind_pose", "preserve_expressions"};
        }
        else if (binding->Category == 8)
        {
            result["kind"] = "layout";
            result["inputs"] = {"layout_values"};
            result["components"] = {"CenterScale", "AddScale", "CenterX", "CenterY", "MoveX", "MoveY"};
        }
        else if (path.ends_with(".id") &&
                 (path.starts_with("hair.") || path.starts_with("clothes.") || path.starts_with("accessory.")))
        {
            result["kind"] = path.substr(0, path.find('.'));
            result["inputs"] = {"recipe|source_model"};
        }
        else
        {
            result["kind"] = "catalog_texture";
            result["inputs"] = {"textures"};
            Json fields = Json::array();
            if (path == "face.skinId")
                fields = {"MainTex", "NormalMapTex", "OcclusionMapTex"};
            else if (path == "body.skinId")
                fields = {"MainTex"};
            else if (path == "body.detailId")
                fields = {"NormalMapTex", "OcclusionMapTex"};
            else if (binding->Category == 306 || binding->Category == 348)
                fields = {"MainTex"};
            else if (binding->Category == 313 || binding->Category == 316 || binding->Category == 320 ||
                     binding->Category == 322)
                fields = {"AddTex", "GlossTex"};
            else
                fields = {"AddTex"};
            result["required_textures"] = fields;
            result["texture_fields"] = {"MainTex", "AddTex", "GlossTex", "NormalMapTex", "OcclusionMapTex"};
        }
        return result;
    }

    Json CreationTargets(const CharacterDocument &document)
    {
        Json targets = Json::array();
        for (const auto &field : CharacterEditSchema::Build(document))
            if (field.Kind == CharacterFieldKind::Asset || field.Kind == CharacterFieldKind::Preset)
            {
                auto target = CreationTarget(document, field.Path);
                Require(!target.is_null(), ("编辑素材缺少制作映射：" + field.Path).c_str());
                target["enabled"] = TargetEnabled(document, field.Path);
                targets.push_back(std::move(target));
            }
        return targets;
    }

    bool TargetEnabled(const CharacterDocument &document, const std::string &path)
    {
        std::smatch match;
        if (std::regex_match(path, match, std::regex(R"(^(hair|clothes|accessory)\.parts\[(\d+)\]\.(.+)$)")))
        {
            auto root = match[1].str() + ".parts[" + match[2].str() + "]";
            if (match[1] == "accessory")
                return document.Node(root + ".type").Integer() != 350;
            auto slot = std::stoi(match[2]);
            if (match[1] == "clothes" && document.Sex() == 0 && (slot == 2 || slot == 3 || slot == 5 || slot == 6))
                return false;
            // 0 是游戏的空槽/无图案；MOD 的实际槽位通过引用另行识别。
            if (document.Node(root + ".id").Integer() == 0 && !CharacterAssetReferences::Find(document, root + ".id"))
                return false;
        }
        if (path == "face.headId" || path == "face.skinId" || path == "body.skinId" ||
            (path.starts_with("face.pupil[") && path.ends_with("pupilId")))
            return true;
        auto value = document.Node(path);
        if (value.Type == MessagePack::Kind::Array)
        {
            auto owner = path == "face.moleLayout" ? "face.moleId" : path.substr(0, path.size() - 6) + "id";
            return document.Contains(owner) && TargetEnabled(document, owner);
        }
        if (path.ends_with("layoutId"))
            return TargetEnabled(document, path.substr(0, path.size() - 8) + "id");
        return value.Integer() != 0 || CharacterAssetReferences::Find(document, path).has_value();
    }

    Json FaceHeadBinding(const CharacterDocument &document)
    {
        auto reference = CharacterAssetReferences::Find(document, "face.headId");
        return {{"guid", reference ? reference->Guid : ""},
                {"slot", reference ? reference->Slot : document.Node("face.headId").Integer()}};
    }

    NativeAssetEntry CatalogTemplate(CharacterCatalog &catalog, const CharacterDocument &document, const Json &target)
    {
        auto category = target.at("category").get<int>();
        auto selected = CharacterAssetReferences::Describe(document, target.at("target_path"))
                            ? catalog.Selected(document, target.at("target_path"))
                            : std::nullopt;
        if (selected && selected->Category == category)
            return *selected;
        for (const auto &entry : catalog.Entries())
            if (entry.Category == category)
                return entry;
        throw std::runtime_error("ASSET_TEMPLATE_REQUIRED：本机缺少类别 " + std::to_string(category) + " 的清单结构");
    }

    Json BuildCatalogAsset(const NativeAssetEntry &source, const Json &spec, const fs::path &output,
                           std::stop_token token)
    {
        auto values = spec.at("layout_values");
        for (auto name : {"CenterScale", "AddScale", "CenterX", "CenterY", "MoveX", "MoveY"})
            Require(values.contains(name) && values[name].is_number() && std::isfinite(values[name].get<double>()),
                    ("布局缺少有限数值：" + std::string(name)).c_str());
        Require(values.size() == 6 && values["CenterScale"].get<double>() > 0 &&
                    values["AddScale"].get<double>() >= 0 &&
                    values["AddScale"].get<double>() < values["CenterScale"].get<double>() &&
                    values["MoveX"].get<double>() >= 0 && values["MoveY"].get<double>() >= 0,
                "身体彩绘布局缩放或移动范围无效");
        auto text = spec.dump() + std::string(BuilderVersion) + Json(source.Values).dump();
        auto hash = Sha256(Bytes(text.begin(), text.end()));
        Json built{{"hash", hash},
                   {"guid", "hs2agent." + hash},
                   {"slot_id", 100000 + static_cast<int>(std::stoul(hash.substr(0, 6), nullptr, 16))},
                   {"builder_version", BuilderVersion},
                   {"additional_bones", Json::array()},
                   {"layout_values", values},
                   {"game_validation", "not_run"}};
        CheckCancelled(token);
        WriteCatalogList(source, spec, built, output);
        return built;
    }

    void WriteCatalogList(const NativeAssetEntry &source, const Json &spec, const Json &built, const fs::path &output)
    {
        std::map<std::string, std::string> values(source.Values.begin(), source.Values.end());
        values["ID"] = std::to_string(built.at("slot_id").get<int>());
        values["Name"] = spec.value("name", spec.at("asset_key").get<std::string>());
        values["Kind"] = "0";
        values["Possess"] = "1";
        if (spec.at("kind") == "layout")
            for (const auto &[key, value] : built.at("layout_values").items())
                values[key] = value.dump();
        else if (spec.at("kind") == "catalog_texture")
        {
            if (spec.contains("head_binding"))
                values["HeadID"] = std::to_string(spec.at("head_binding").at("slot").get<int>());
            for (auto &[key, value] : values)
                if (key.ends_with("Tex") || key.ends_with("AB") || key.ends_with("Data") || key.ends_with("Manifest"))
                    value = "0";
            values["MainManifest"] = "abdata";
            values["MainAB"] = built.at("bundle");
            values["MainTexAB"] = built.at("bundle");
            for (const auto &[key, value] : built.at("textures").items())
                values[key] = value.get<std::string>();
        }
        else
        {
            values["MainManifest"] = "abdata";
            values["MainAB"] = built.at("bundle");
            values["MainData"] = built.at("prefab");
            values["MainData02"] = built.at("prefab");
            values["ShapeAnime"] = built.at("shape_anime");
            values["MatData"] = built.at("mat_data");
            values["ThumbAB"] = values["ThumbTex"] = "0";
        }
        std::string header = "ID", row = values.at("ID");
        for (const auto &[key, value] : values)
            if (key != "ID")
            {
                Require(key.find_first_of(",\r\n") == key.npos && value.find_first_of(",\r\n") == value.npos,
                        "HS2 清单字段不能包含逗号或换行");
                header += "," + key;
                row += "," + value;
            }
        auto text =
            std::to_string(source.Category) + "\r\n0\r\nhs2agent_generated\r\n" + header + "\r\n" + row + "\r\n";
        WriteAtomic(output / "abdata/list/characustom" / (spec.at("sex").get<int>() == 0 ? "00" : "01") /
                        "hs2agent.csv",
                    Bytes(text.begin(), text.end()));
    }
}
