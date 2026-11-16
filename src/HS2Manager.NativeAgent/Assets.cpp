#include "Agent.h"
#include "SharedAssets.h"
#include "Model/ModelExport.h"
#include "AssetBundleBuild.h"
#include "AssetTargets.h"
#include "Export/CardExport.h"
#include "Archive/ZipArchive.h"
#include <fstream>
#include <algorithm>
#include <cmath>

namespace hs2::agent
{
    CharacterDocument ApplyOverlay(CharacterDocument document, const Json &spec, Bytes png)
    {
        return creation::ApplyOverlay(std::move(document), spec, std::move(png));
    }

    Json Service::PrepareAsset(Json &session, const Json &args, std::stop_token token)
    {
        auto document = Document(session);
        if (args.contains("target_path"))
        {
            auto target = asset::CreationTarget(document, args.at("target_path"));
            if (target.is_null())
                throw std::invalid_argument("目标字段没有素材制作接口");
            if (target.at("kind") == "head")
            {
                CharacterCatalog catalog(game_, data_ / "Cache", runtime_ / "classdata.tpk", Resources(session, token));
                catalog.Load({}, token);
                auto spec = target;
                auto source = asset::ResolveTemplate(catalog, document, spec, game_, token);
                auto output = data_ / "Reference" / Path(NewId()) / "head-bind-pose.json";
                WriteJson(output, asset::HeadReference(source, token));
                return {{"schema_version", 3},
                        {"reference_revision", session.at("revision")},
                        {"creation", target},
                        {"source_head", Utf8(output)},
                        {"template_asset_id", asset::AssetId(source.Entry)}};
            }
            if (target.at("kind") == "layout")
                return {{"schema_version", 3}, {"reference_revision", session.at("revision")}, {"creation", target}};
        }
        auto card = CardParser().Parse(document.SourcePath(), token);
        // 参考 FBX 与结构化场景均由内置原生模块导出。
        auto output = data_ / "Reference" / Path(NewId());
        ModelExportOptions options;
        options.Clothing = 2;
        options.Accessories = false;
        auto report =
            ModelExportService(runtime_).Export({{card, document, session.at("revision")}}, game_, data_ / "Cache",
                                                output, options, {}, token, Resources(session, token));
        if (report.Items.empty() || report.Items.front().Status != ModelExportStatus::Complete)
            throw std::runtime_error("建模参考导出失败：" + report.Summary() +
                                     (report.Items.empty() ? std::string{} : "；" + report.Items.front().Message));
        auto model = Path(report.Items.front().Directory) / "character.fbx";
        NativeCharacterService service(game_, data_ / "Cache", runtime_ / "classdata.tpk", Resources(session, token));
        auto prepared = service.Prepare(card, &document, {2, false}, {}, token);
        auto reference = asset::CaptureReference(prepared, token);
        auto result = asset::WriteReference(reference, output, token);
        result["reference_model"] = Utf8(model);
        result["reference_revision"] = session.at("revision");
        result["creation_targets"] = asset::CreationTargets(document);
        if (args.contains("target_path"))
            result["creation"] = asset::CreationTarget(document, args.at("target_path"));
        CheckCancelled(token);
        (void)args;
        session["reference_assets"] = result;
        WriteJson(SessionPath(session.at("session_id")) / "session.json", session);
        return result;
    }

    Json Service::BuildAsset(Json &session, const Json &args, std::stop_token token)
    {
        auto spec = args.at("spec");
        auto kind = spec.at("kind").get<std::string>();
        if (kind != "texture" && kind != "accessory" && kind != "hair" && kind != "clothes" &&
            kind != "catalog_texture" && kind != "layout" && kind != "head")
            throw std::invalid_argument("未知素材类型");
        auto key = spec.at("asset_key").get<std::string>();
        if (key.empty() || key.size() > 128)
            throw std::invalid_argument("asset_key 无效");
        auto attempts = session.value("build_attempts", Json::object());
        auto count = attempts.value(key, std::int64_t{0});
        attempts[key] = count + 1;
        session["build_attempts"] = attempts;
        WriteJson(SessionPath(session.at("session_id")) / "session.json", session);
        auto document = Document(session);
        if (kind == "texture")
        {
            auto source = Path(spec.at("source_image"));
            ValidateImage(source);
            auto bytes = ReadFile(source, 32 * 1024 * 1024, token);
            if (CardParser::FindPngEnd(bytes) != bytes.size())
                throw std::invalid_argument("覆盖贴图必须为无尾部数据的 PNG");
            auto hash = Sha256(bytes);
            auto next = ApplyOverlay(document, spec, bytes);
            auto trial = session;
            auto render = Render(trial, next, {{"views", {"front", "face"}}, {"probe", true}}, token);
            if (!render.at("diagnostics").empty())
                return {{"status", "needs_work"}, {"render", render}};
            CheckCancelled(token);
            WriteAtomic(data_ / "Assets" / Path(hash + ".png"), bytes);
            Json record{{"kind", kind},
                        {"hash", hash},
                        {"asset_key", key},
                        {"spec", spec},
                        {"path", Utf8(data_ / "Assets" / Path(hash + ".png"))},
                        {"mime_type", "image/png"}};
            WriteJson(data_ / "Assets" / Path(hash + ".json"), record);
            session["assets"].push_back(record);
            SaveRevision(session, next, args);
            return {{"status", "applied"}, {"session", session}, {"render", render}};
        }
        for (auto legacyKey : {"source_script", "template_material", "shader", "unity_project", "modding_project"})
            if (spec.contains(legacyKey))
                throw std::invalid_argument("ASSET_SPEC_MIGRATION：请使用 schema_version:2、recipe/source_model 和 "
                                            "template_asset_id；不再执行 Blender Python 或加载 Unity 工程材质");
        auto version = spec.value("schema_version", 0);
        if (version != 3 && (version != 2 || kind == "head" || kind == "catalog_texture" || kind == "layout"))
            throw std::invalid_argument("ASSET_SPEC_VERSION：新制作类型需要 schema_version:3；旧网格描述支持版本 2");
        auto target = spec.at("target_path").get<std::string>();
        auto fields = CharacterEditSchema::Build(document, CharacterSliderLimits::Read(game_));
        if (std::none_of(fields.begin(), fields.end(),
                         [&](const auto &field)
                         {
                             return field.Path == target && (field.Kind == CharacterFieldKind::Asset ||
                                                             field.Kind == CharacterFieldKind::Preset);
                         }))
            throw std::invalid_argument("目标字段不在当前编辑 Schema 中");
        auto creation = asset::CreationTarget(document, target);
        if (creation.is_null() || creation.at("kind") != kind)
            throw std::invalid_argument("素材类型和目标字段不匹配");
        auto binding = CharacterAssetReferences::Describe(document, target);
        if (kind == "layout" && !binding)
        {
            const auto &layout = spec.at("layout");
            if (!layout.is_array() || layout.size() != 4)
                throw std::invalid_argument("layout 需要四个 0–1 数值：宽度、高度、横向位置、纵向位置");
            auto next = document;
            for (int i = 0; i < 4; ++i)
            {
                if (!layout[i].is_number() || !std::isfinite(layout[i].get<double>()) || layout[i].get<double>() < 0 ||
                    layout[i].get<double>() > 1)
                    throw std::invalid_argument("layout 数值超出 0–1");
                next = next.WithValue(target + "[" + std::to_string(i) + "]", layout[i].get<float>());
            }
            auto trial = session;
            auto render = Render(trial, next, {{"probe", true}}, token);
            if (!render.at("diagnostics").empty())
                return {{"status", "needs_work"}, {"render", render}};
            Json record{
                {"kind", kind}, {"asset_key", key}, {"target_path", target}, {"layout", layout}, {"spec", spec}};
            session["assets"].push_back(record);
            CheckCancelled(token);
            SaveRevision(session, next, args);
            return {{"status", "applied"}, {"session", session}, {"asset", record}, {"render", render}};
        }
        if (!binding)
            throw std::invalid_argument("目标槽位不能选择素材");
        spec["category"] = binding->Category;
        spec["sex"] = document.Sex();
        if (target == "face.skinId")
            spec["head_binding"] = asset::FaceHeadBinding(document);
        auto work = data_ / "Builds" / Path(NewId());
        fs::create_directories(work);
        if (kind == "head" && !spec.contains("template_asset_id"))
            spec["template_asset_id"] = ReadJson(Path(spec.at("source_head"))).at("template_asset_id");
        auto buildContext = session;
        if (spec.contains("template_asset_id"))
        {
            // 持续修正可能使用已被后续版本替换的本会话技术模板。
            buildContext["resource_guids"] = Json::array();
            for (const auto &record : session.value("assets", Json::array()))
                if (record.contains("guid") && record.contains("spec"))
                {
                    const auto &previous = record.at("spec");
                    if (previous.contains("category") && previous.contains("slot_id") &&
                        std::to_string(previous.at("category").get<int>()) + ":" +
                                record.at("guid").get<std::string>() + ":" +
                                std::to_string(previous.at("slot_id").get<int>()) ==
                            spec.at("template_asset_id").get<std::string>())
                        buildContext["resource_guids"].push_back(record.at("guid"));
                }
        }
        Json built, geometry = Json::object();
        if (kind == "catalog_texture" || kind == "layout" || kind == "head")
        {
            CharacterCatalog catalog(game_, data_ / "Cache", runtime_ / "classdata.tpk",
                                     Resources(buildContext, token));
            catalog.Load({}, token);
            auto entry = asset::CatalogTemplate(catalog, document, creation);
            if (kind == "head")
            {
                auto source = asset::ResolveTemplate(catalog, document, spec, game_, token);
                entry = source.Entry;
                built = asset::BuildHeadBundle(source, spec, game_, work / "Package", token);
            }
            else if (kind == "catalog_texture")
            {
                for (const auto &required : creation.at("required_textures"))
                    if (std::none_of(spec.at("textures").begin(), spec.at("textures").end(),
                                     [&](const auto &t)
                                     {
                                         return t.at("field") == required;
                                     }))
                        throw std::invalid_argument("缺少新贴图通道：" + required.get<std::string>());
                built = asset::BuildTextureBundle(catalog, spec, game_, work / "Package", token);
            }
            else
                built = asset::BuildCatalogAsset(entry, spec, work / "Package", token);
            asset::WriteCatalogList(entry, spec, built, work / "Package");
        }
        else
        {
            // 每次重取当前身体，不能把改体型前的参考用于新蒙皮。
            auto card = CardParser().Parse(document.SourcePath(), token);
            NativeCharacterService service(game_, data_ / "Cache", runtime_ / "classdata.tpk",
                                           Resources(buildContext, token));
            auto prepared = service.Prepare(card, &document, {2, false}, {}, token);
            auto reference = asset::CaptureReference(prepared, token);
            auto source = asset::ResolveTemplate(service.Catalog(), document, spec, game_, token);
            if (spec.contains("source_model") == spec.contains("recipe"))
                throw std::invalid_argument("必须提供 recipe 或 source_model，且只能选择一种");
            auto mesh = spec.contains("source_model")
                            ? asset::ImportModel(Path(spec.at("source_model")),
                                                 runtime_ / "native/win-x64/HS2Manager.FbxBridge.dll", token)
                            : asset::BuildRecipe(spec.at("recipe"), reference, token);
            asset::AdaptGeometry(mesh, reference, spec, token);
            geometry = asset::GeometryReport(mesh);
            built = asset::BuildGameBundle(mesh, source, spec, game_, work / "Package", token);
            asset::WriteSideloaderList(source, spec, built, work / "Package");
        }
        auto hash = built.at("hash").get<std::string>();
        spec["guid"] = built.at("guid");
        spec["slot_id"] = built.at("slot_id");
        WriteJson(work / "spec.json", spec);
        WriteJson(work / "build-result.json", built);
        auto package = work / "Package";
        std::string manifest = "<?xml version=\"1.0\" encoding=\"utf-8\"?><manifest schema-ver=\"1\"><guid>" +
                               spec.at("guid").get<std::string>() + "</guid><name>" +
                               creation::XmlText(spec.value("name", key)) +
                               "</name><version>1.0</version><author>HS2Agent</author><game>HoneySelect2</game>" +
                               (built.at("additional_bones").empty() ? std::string{} : "<BonesFramework/>");
        if (spec.contains("head_binding") && !spec.at("head_binding").at("guid").get<std::string>().empty())
            manifest += "<faceSkinInfo skinID=\"" + std::to_string(spec.at("slot_id").get<int>()) + "\" headID=\"" +
                        std::to_string(spec.at("head_binding").at("slot").get<int>()) + "\" headGUID=\"" +
                        creation::XmlText(spec.at("head_binding").at("guid").get<std::string>()) + "\"/>";
        manifest += "</manifest>";
        WriteAtomic(package / "manifest.xml", Bytes(manifest.begin(), manifest.end()));
        auto candidate = work / Path(hash + ".zipmod");
        creation::WriteAssetZip(package, candidate, token);
        auto trial = session;
        trial["candidate_mod"] = Utf8(candidate);
        CharacterCatalog catalog(game_, data_ / "Cache", runtime_ / "classdata.tpk", Resources(trial, token));
        catalog.Load({}, token);
        auto entry = catalog.Resolve({spec["guid"], spec["slot_id"], spec["category"], binding->Property});
        auto next = catalog.Select(document, target, entry);
        auto render = Render(trial, next, {{"views", {"front", "side", "back", "face"}}, {"probe", true}}, token);
        if (!render.at("diagnostics").empty())
            return {{"status", "needs_work"}, {"render", render}, {"work_directory", Utf8(work)}};
        Json checks = Json::array();
        if (kind == "hair" || kind == "clothes")
        {
            auto motion =
                Render(trial, next, {{"views", {"front", "side"}}, {"sample_time", .5}, {"probe", true}}, token);
            if (!motion.at("diagnostics").empty())
                throw std::runtime_error("素材动态采样验证失败");
            checks.push_back({{"kind", "motion"}, {"render", motion}});
            auto bodyFields = CharacterEditSchema::Build(next, CharacterSliderLimits::Read(game_));
            auto body = std::find_if(bodyFields.begin(), bodyFields.end(),
                                     [](const auto &field)
                                     {
                                         return field.Category == "body" && field.Kind == CharacterFieldKind::Number &&
                                                field.Path.starts_with("body.shapeValueBody[");
                                     });
            if (body == bodyFields.end())
                throw std::runtime_error("找不到体型适配测试字段");
            auto value = ScalarJson(next.Node(body->Path).ScalarValue).get<double>();
            for (int sign : {-1, 1})
            {
                double changed =
                    std::clamp(value + sign * (body->Maximum - body->Minimum) * .1, body->Minimum, body->Maximum);
                auto variation = CharacterEditRules::SetValue(next, body->Path, changed);
                auto frame = Render(trial, variation, {{"views", {"front"}}, {"probe", true}}, token);
                if (!frame.at("diagnostics").empty())
                    throw std::runtime_error("素材体型适配验证失败");
                checks.push_back(
                    {{"kind", "body_variation"}, {"path", body->Path}, {"value", changed}, {"render", frame}});
            }
        }
        if (kind == "clothes")
            for (int state : {1, 2})
            {
                auto check =
                    Render(trial, next, {{"views", {"front"}}, {"clothing_state", state}, {"probe", true}}, token);
                if (!check.at("diagnostics").empty())
                    throw std::runtime_error("服装穿脱状态验证失败");
                checks.push_back({{"kind", "clothing_state"}, {"state", state}, {"render", check}});
            }
        CheckCancelled(token);
        auto installed = data_ / "Assets" / candidate.filename();
        if (!fs::exists(installed))
            WriteAtomic(installed, ReadFile(candidate, 1024ull * 1024 * 1024, token));
        Json record{{"kind", kind}, {"asset_key", key},        {"target_path", target},       {"guid", spec["guid"]},
                    {"hash", hash}, {"path", Utf8(installed)}, {"geometry", geometry},        {"build", built},
                    {"spec", spec}, {"checks", checks},        {"game_validation", "not_run"}};
        WriteJson(data_ / "Assets" / Path(hash + ".json"), record);
        session["assets"].push_back(record);
        SaveRevision(session, next, args);
        return {{"status", "applied"}, {"session", session}, {"asset", record}, {"render", render}};
    }
}
