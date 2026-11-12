#include "CreationService.h"
#include "Assets/AssetReader.h"
#include <algorithm>

namespace hs2::creation
{
    BuildResult CreationService::Build(const BuildRequest &request, Progress progress, std::stop_token token)
    {
        if (!request.State)
            throw std::invalid_argument("构建缺少工程快照");
        CreationProject::Validate(*request.State);
        const auto &state = *request.State;
        auto work = fs::path(NativeFilePath(request.ProjectDirectory / "Builds" / Path(NewId())));
        ExportPaths::NoLinks(work);
        fs::create_directories(work);
        Json job{
            {"status", "running"}, {"revision", request.Revision}, {"token", state.Token}, {"started_utc", UtcNow()}};
        WriteJson(work / "job.json", job);
        try
        {
            BuildResult result;
            result.Directory = work;
            result.Revision = request.Revision;
            result.Token = state.Token;
            auto document = *state.Character;
            auto resources = Resources(&state, {}, token);
            document = ResolveBodySelection(document, resources, token);
            CardRecord card;
            card.Path = Utf8(document.SourcePath());
            card.Name = state.Name;
            card.Sex = document.Sex();
            NativeCharacterService service(game_, data_ / "Creation/Cache", runtime_ / "classdata.tpk", resources);
            if (progress)
                progress({0, static_cast<int>(state.Assets.size()) + 2, "准备当前体型与骨架"});
            std::optional<asset::Geometry> reference;
            if (!state.Assets.empty())
            {
                auto prepared = service.Prepare(card, &document, {2, false}, {}, token);
                reference = asset::CaptureReference(prepared, token);
            }
            Json assets = Json::array();
            bool additionalBones = false;
            for (std::size_t i = 0; i < state.Assets.size(); ++i)
            {
                CheckCancelled(token);
                const auto &item = state.Assets[i];
                if (progress)
                    progress({static_cast<int>(i), static_cast<int>(state.Assets.size()) + 2, "构建 " + item.Name});
                auto binding = CharacterAssetReferences::Describe(document, item.TargetPath);
                if (!binding)
                    throw std::runtime_error("素材目标槽位不存在：" + item.TargetPath);
                if ((item.Kind == "hair" && !item.TargetPath.starts_with("hair.")) ||
                    (item.Kind == "clothes" && !item.TargetPath.starts_with("clothes.")) ||
                    (item.Kind == "accessory" && !item.TargetPath.starts_with("accessory.")))
                    throw std::runtime_error("素材种类与目标位置不匹配");
                Json spec{{"schema_version", asset::SchemaVersion},
                          {"kind", item.Kind},
                          {"asset_key", item.Id},
                          {"name", item.Name},
                          {"target_path", item.TargetPath},
                          {"template_asset_id", item.TemplateId},
                          {"parent", item.Parent},
                          {"category", binding->Category},
                          {"sex", document.Sex()},
                          {"package_guid", request.Guid},
                          {"slot_id", item.Slot},
                          {"dynamic_roots", item.DynamicRoots},
                          {"dynamic_settings", item.DynamicSettings},
                          {"material_values", item.MaterialValues},
                          {"independent_half", item.IndependentHalfState},
                          {"textures", Json::array()},
                          {"masks", Json::array()}};
                asset::Geometry geometry;
                geometry.Bones = item.Bones;
                for (const auto &object : item.Objects)
                {
                    if (object.Hidden)
                        continue;
                    if (object.HalfState && !item.IndependentHalfState)
                        continue;
                    auto mesh = object.Mesh.Triangulate();
                    mesh.Name = object.Id + "_" + mesh.Name;
                    // 编辑空间是人物根坐标；游戏配饰网格使用挂点局部坐标。
                    if (item.Kind == "accessory")
                    {
                        auto parent = std::find_if(reference->Bones.begin(), reference->Bones.end(),
                                                   [&](const auto &bone)
                                                   {
                                                       return bone.Name == item.Parent;
                                                   });
                        if (parent == reference->Bones.end())
                            throw std::runtime_error("配饰挂点不存在：" + item.Parent);
                        asset::TransformMesh(mesh, InverseMatrix(asset::BoneWorld(*parent)));
                    }
                    if (object.HalfState)
                        mesh.Role = "clothes_half";
                    geometry.Meshes.push_back(std::move(mesh));
                }
                if (geometry.Meshes.empty())
                    throw std::runtime_error(item.Name + " 没有可构建的网格");
                if (std::none_of(item.Objects.begin(), item.Objects.end(),
                                 [](const auto &o)
                                 {
                                     return !o.Hidden && !o.HalfState;
                                 }))
                    throw std::runtime_error(item.Name + " 缺少完整穿着网格");
                auto inputs = work / "Inputs" / Path(item.Id);
                fs::create_directories(inputs);
                for (const auto &texture : item.Textures)
                {
                    auto path = inputs / Path(texture.Id + ".png");
                    texture.Image.Save(path);
                    if (texture.Kind == "mask")
                    {
                        auto field = texture.Property == "body"    ? "OverBodyMask"
                                     : texture.Property == "bra"   ? "OverBraMask"
                                     : texture.Property == "inner" ? "OverInnerBMask"
                                                                   : texture.Property;
                        spec["masks"].push_back({{"field", field}, {"path", Utf8(path)}});
                    }
                    else
                        spec["textures"].push_back({{"property", texture.Property},
                                                    {"path", Utf8(path)},
                                                    {"material_slot", texture.MaterialSlot},
                                                    {"srgb", texture.Image.Srgb}});
                }
                asset::AdaptGeometry(geometry, *reference, spec, token);
                auto source = asset::ResolveTemplate(service.Catalog(), document, spec, game_, token);
                if (!item.TemplateSource.empty())
                {
                    auto values = Json(source.Entry.Values).dump();
                    if (HashFile(source.Prefab.Bundle, token) != item.TemplateSource.value("bundle_hash", "") ||
                        Sha256(Bytes(values.begin(), values.end())) != item.TemplateSource.value("list_hash", ""))
                        throw std::runtime_error(item.Name + " 的游戏模板已改变，请重新选择并确认模板后构建");
                }
                auto built = asset::BuildGameBundle(geometry, source, spec, game_, work / "Package", token);
                asset::WriteSideloaderList(source, spec, built, work / "Package");
                additionalBones |= !built.at("additional_bones").empty();
                built["asset_id"] = item.Id;
                built["target"] = item.TargetPath;
                built["geometry"] = asset::GeometryReport(geometry);
                built["weighted_bones"] = Json::array();
                for (const auto &mesh : geometry.Meshes)
                {
                    std::set<std::string> names;
                    for (const auto &vertex : mesh.Vertices)
                        for (const auto &weight : vertex.Weights)
                            if (weight.Weight > 0)
                                names.insert(weight.Bone);
                    built["weighted_bones"].push_back({{"mesh", mesh.Name}, {"bones", names}});
                }
                auto templateText = Json(source.Entry.Values).dump();
                built["template_hash"] = Sha256(Bytes(templateText.begin(), templateText.end()));
                assets.push_back(std::move(built));
                WriteJson(inputs / "spec.json", spec);
            }
            for (const auto &overlay : state.Overlays)
            {
                CheckCancelled(token);
                auto image = work / "Overlays" / Path(overlay.Id + ".png");
                fs::create_directories(image.parent_path());
                overlay.Image.Save(image);
                document = ApplyOverlay(
                    document,
                    {{"target", overlay.Target}, {"clothing_name", overlay.ClothingName}, {"replace", overlay.Replace}},
                    ReadFile(image, 32 * 1024 * 1024));
            }
            if (!state.Assets.empty())
            {
                auto manifest = "<?xml version=\"1.0\" encoding=\"utf-8\"?><manifest schema-ver=\"1\"><guid>" +
                                XmlText(request.Guid) + "</guid><name>" + XmlText(state.Name) + "</name><version>1." +
                                std::to_string(request.Revision) +
                                "</version><author>HS2Manager</author><game>HoneySelect2</game>" +
                                (additionalBones ? "<BonesFramework/>" : "") + "</manifest>";
                WriteAtomic(work / "Package/manifest.xml", Bytes(manifest.begin(), manifest.end()));
                result.Mod = work / Path(request.Guid + ".zipmod");
                WriteAssetZip(work / "Package", result.Mod, token);
                resources = Resources(&state, result.Mod, token);
                CharacterCatalog catalog(game_, data_ / "Creation/Cache", runtime_ / "classdata.tpk", resources);
                catalog.Load({}, token);
                for (const auto &item : state.Assets)
                {
                    auto binding = CharacterAssetReferences::Describe(document, item.TargetPath);
                    auto entry = catalog.Resolve({request.Guid, item.Slot, binding->Category, binding->Property});
                    document = catalog.Select(document, item.TargetPath, entry);
                }
            }
            if (progress)
                progress({static_cast<int>(state.Assets.size()), static_cast<int>(state.Assets.size()) + 2,
                          "回读资源并渲染检查"});
            auto render =
                RenderCharacter(game_, data_ / "Creation/Cache", runtime_, document,
                                {{"views", {"front", "side", "back", "face"}}}, work / "Previews", resources, token);
            if (!render.at("diagnostics").empty())
                throw std::runtime_error("原生试渲染存在未解决的问题：" + render.at("diagnostics").dump());
            Json checks = Json::array({render});
            bool fitted = std::any_of(state.Assets.begin(), state.Assets.end(),
                                      [](const auto &a)
                                      {
                                          return a.Kind != "accessory";
                                      });
            if (fitted)
            {
                auto motion =
                    RenderCharacter(game_, data_ / "Creation/Cache", runtime_, document,
                                    {{"views", {"front"}}, {"sample_time", .5}}, work / "Motion", resources, token);
                if (!motion.at("diagnostics").empty())
                    throw std::runtime_error("动态采样未通过");
                checks.push_back(motion);
                for (const auto &field : CharacterEditSchema::Build(document))
                    if (field.Path.starts_with("body.shapeValueBody["))
                    {
                        for (float value : {.2f, .8f})
                        {
                            auto changed = document.WithValue(field.Path, value);
                            auto frame = RenderCharacter(
                                game_, data_ / "Creation/Cache", runtime_, changed, {{"views", {"front"}}},
                                work / Path(value < .5f ? "BodySmall" : "BodyLarge"), resources, token);
                            if (!frame.at("diagnostics").empty())
                                throw std::runtime_error("体型适配采样未通过");
                            checks.push_back(frame);
                        }
                        break;
                    }
            }
            if (std::any_of(state.Assets.begin(), state.Assets.end(),
                            [](const auto &a)
                            {
                                return a.Kind == "clothes";
                            }))
                for (int clothing : {1, 2})
                {
                    auto frame = RenderCharacter(game_, data_ / "Creation/Cache", runtime_, document,
                                                 {{"views", {"front"}}, {"clothing_state", clothing}},
                                                 work / Path("Clothing" + std::to_string(clothing)), resources, token);
                    if (!frame.at("diagnostics").empty())
                        throw std::runtime_error("服装状态采样未通过");
                    checks.push_back(frame);
                }
            if (state.CoverSettings.value("mode", "render") == "render")
            {
                auto args = state.CoverSettings;
                args["views"] = Json::array({args.value("view", "front")});
                args["width"] = 504;
                args["height"] = 704;
                auto cover = RenderCharacter(game_, data_ / "Creation/Cache", runtime_, document, args, work / "Cover",
                                             resources, token);
                document = document.WithCover(ReadFile(Path(cover.at("images")[0].at("path")), 32 * 1024 * 1024));
            }
            result.Card = work / "character.png";
            WriteAtomic(result.Card, document.ToBytes());
            auto parsed = CardParser().Parse(result.Card, token);
            if (parsed.Status != CardStatus::Valid)
                throw std::runtime_error("构建后的角色卡回读失败");
            CardExportDependencies collector(game_, resources->Index, token, runtime_ / "classdata.tpk");
            auto dependencies = collector.Collect(parsed, CardExportLevel::WithResources);
            result.Report = {{"status", "complete"},
                             {"revision", request.Revision},
                             {"token", state.Token},
                             {"guid", request.Guid},
                             {"mod", Utf8(result.Mod)},
                             {"card", Utf8(result.Card)},
                             {"mod_hash", result.Mod.empty() ? "" : HashFile(result.Mod, token)},
                             {"card_hash", parsed.Hash.empty() ? HashFile(result.Card, token) : parsed.Hash},
                             {"assets", assets},
                             {"checks", checks},
                             {"dependency_issues", dependencies.Issues},
                             {"warnings", dependencies.Warnings},
                             {"game_validation", "not_run"}};
            if (!dependencies.Issues.empty())
            {
                result.Report["status"] = "needs_dependencies";
                WriteJson(work / "build-result.json", result.Report);
                throw std::runtime_error("构建文件已保留，但依赖检查未通过：" + Json(dependencies.Issues).dump());
            }
            CheckCancelled(token);
            WriteJson(work / "build-result.json", result.Report);
            result.Character = document;
            job["status"] = "complete";
            job["result"] = result.Report;
            WriteJson(work / "job.json", job);
            if (progress)
                progress({static_cast<int>(state.Assets.size()) + 2, static_cast<int>(state.Assets.size()) + 2,
                          "构建与回读完成"});
            return result;
        }
        catch (const std::exception &e)
        {
            job["status"] = token.stop_requested() ? "cancelled" : "failed";
            job["message"] = e.what();
            try
            {
                WriteJson(work / "job.json", job);
            }
            catch (...)
            {
            }
            throw;
        }
    }
}
