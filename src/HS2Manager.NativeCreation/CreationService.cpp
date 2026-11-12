#include "CreationService.h"
#include "Assets/AssetReader.h"
#include "Animation/SimulationMath.h"
#include "Character/CharacterBodyCatalog.h"
#include <algorithm>

namespace hs2::creation
{
    void CreationService::RequireCurrentBuild(const CreationState &state) const
    {
        if (state.Assets.empty() && state.Overlays.empty())
            return;
        if (!state.LastBuild.is_object() || state.LastBuild.value("status", "") != "complete" ||
            state.LastBuild.value("state_token", "") != state.Token)
            throw std::runtime_error("素材或贴图已有修改，请先构建当前版本再导出");
        for (auto key : {"card", "mod"})
        {
            auto path = Path(state.LastBuild.value(key, ""));
            if (path.empty() && std::string(key) == "mod" && state.Assets.empty())
                continue;
            if (!fs::is_regular_file(path) || HashFile(path) != state.LastBuild.value(std::string(key) + "_hash", ""))
                throw std::runtime_error("构建成品丢失或改变，请重新构建");
        }
    }

    CreationService::CreationService(fs::path game, fs::path data, fs::path runtime)
        : game_(fs::absolute(std::move(game))), data_(fs::absolute(std::move(data))),
          runtime_(fs::absolute(std::move(runtime)))
    {
        CardExportService::ValidateOutput(game_, data_);
        ExportPaths::NoLinks(data_);
    }

    CharacterDocument CreationService::DefaultCharacter(int sex, std::stop_token token) const
    {
        if (sex != 0 && sex != 1)
            throw std::invalid_argument("请选择男性或女性");
        auto bundle =
            game_ / (sex == 0 ? "abdata/custom/00/presets_m_00.unity3d" : "abdata/custom/00/presets_f_00.unity3d");
        if (!fs::is_regular_file(bundle))
            throw std::runtime_error("原版默认角色资源不存在或游戏目录离线：" + Utf8(bundle));
        UnityClassDatabase database(runtime_ / "classdata.tpk");
        NativeGpuAssetReader reader(bundle, &database, {}, token);
        auto bytes = reader.ReadText(sex == 0 ? "ill_Default_Male" : "ill_Default_Female");
        auto output = data_ / "Creation/Defaults" / (sex == 0 ? "male.png" : "female.png");
        try
        {
            auto document = CharacterDocument::FromBytes(std::move(bytes), output);
            if (document.Sex() != sex)
                throw std::runtime_error("原版默认角色的性别不匹配");
            return document;
        }
        catch (const std::exception &e)
        {
            throw std::runtime_error("无法读取此游戏版本的默认角色，可选择已有卡创建：" + std::string(e.what()));
        }
    }

    std::shared_ptr<CreationProject> CreationService::Create(std::string name, std::string kind, int sex,
                                                             std::optional<fs::path> source, std::stop_token token)
    {
        auto document = source ? CharacterDocument::Open(*source, token) : DefaultCharacter(sex, token);
        document = ResolveBodySelection(document, Resources(nullptr, {}, token), token);
        return CreationProject::Create(ProjectsRoot(), std::move(name), std::move(kind), std::move(document));
    }

    CharacterDocument CreationService::ResolveBodySelection(const CharacterDocument &document,
                                                            PreviewResources resources, std::stop_token token)
    {
        const std::string id = "com.deathweasel.bepinex.uncensorselector";
        auto extensions = document.Extensions().value_or(MessagePack::Map());
        auto existing = extensions.Find(id);
        auto data = existing ? existing->Require(std::int64_t(1)) : MessagePack::Map();
        if (existing &&
            (existing->Require(std::int64_t(0)).Integer() < 1 || existing->Require(std::int64_t(0)).Integer() > 2))
            throw std::runtime_error("身体选择插件数据版本不支持");
        auto selection =
            ResolveCharacterBody(game_, data_ / "Creation/Cache", document, ParameterOf(data), token, resources);
        if (selection.empty())
            return document;
        for (const auto &part : selection)
            data = data.Set(part.Kind + "GUID", MessagePack::Scalar(part.Guid), true);
        // Pin the actual locally selected bodies; a random/default plugin setting must
        // not silently change the skeleton when this card moves to another installation.
        data = data.Set(std::string("UncensorGUID"), MessagePack::Scalar(std::string()), true);
        extensions = extensions.Set(id, MessagePack::Array({MessagePack::Scalar(std::int64_t(2)), data}), true);
        return document.WithExtensions(extensions);
    }

    PreviewResources CreationService::Resources(const CreationState *state, const fs::path &candidate,
                                                std::stop_token token)
    {
        auto result = std::make_shared<PreviewResourceContext>();
        result->Game = game_;
        result->Data = data_ / "Creation/Cache";
        result->CatalogCache = data_;
        {
            std::lock_guard lock(indexGate_);
            if (!index_)
                index_ = ModDependencyService::ScanTransient(game_, nullptr, {}, token);
            result->Index = *index_;
        }
        auto package = candidate;
        if (package.empty() && state && state->LastBuild.is_object())
            package = Path(state->LastBuild.value("mod", ""));
        if (!package.empty() && fs::is_regular_file(package))
        {
            auto mod = ModDependencyService::ReadManifest(package);
            if (!mod.Error.empty())
                throw std::runtime_error(mod.Error);
            std::erase_if(result->Index.Files,
                          [&](const auto &file)
                          {
                              return file.Guid == mod.Guid;
                          });
            result->Index.Files.push_back(mod);
            result->Priorities[package] = 10;
            result->Package = package.parent_path();
        }
        return result;
    }

    void CreationService::InvalidateIndex()
    {
        std::lock_guard lock(indexGate_);
        index_.reset();
    }

    asset::Geometry CreationService::Reference(const CreationState &state, std::stop_token token)
    {
        CardRecord card;
        card.Path = Utf8(state.Character->SourcePath());
        card.Sex = state.Character->Sex();
        card.Name = state.Name;
        NativeCharacterService service(game_, data_ / "Creation/Cache", runtime_ / "classdata.tpk",
                                       Resources(&state, {}, token));
        auto prepared = service.Prepare(card, &*state.Character, {0, false}, {}, token);
        return asset::CaptureReference(prepared, token);
    }

    Json CreationService::Templates(const CreationState &state, const std::string &target, std::stop_token token)
    {
        auto binding = CharacterAssetReferences::Describe(*state.Character, target);
        if (!binding)
            throw std::invalid_argument("目标位置不支持素材");
        CharacterCatalog catalog(game_, data_ / "Creation/Cache", runtime_ / "classdata.tpk",
                                 Resources(&state, {}, token));
        catalog.Load({}, token);
        auto choices = asset::TemplateChoices(catalog, binding->Category, 5000);
        std::stable_sort(choices.begin(), choices.end(),
                         [](const auto &a, const auto &b)
                         {
                             return (a.at("asset_id").template get<std::string>().find("::") != std::string::npos) >
                                    (b.at("asset_id").template get<std::string>().find("::") != std::string::npos);
                         });
        return choices;
    }

    Json CreationService::MaterialProperties(const CreationState &state, const CreationAsset &item,
                                             std::stop_token token)
    {
        CharacterCatalog catalog(game_, data_ / "Creation/Cache", runtime_ / "classdata.tpk",
                                 Resources(&state, {}, token));
        catalog.Load({}, token);
        auto source = asset::ResolveTemplate(
            catalog, *state.Character,
            {{"kind", item.Kind}, {"target_path", item.TargetPath}, {"template_asset_id", item.TemplateId}}, game_,
            token);
        Json result = Json::array();
        int slot = 0;
        for (const auto &renderer : source.Data->Renderers)
            for (const auto &pointer : renderer.Materials)
                if (pointer.PathId)
                {
                    auto material = source.Assets->Material(pointer);
                    Json values = Json::array();
                    for (const auto &[key, value] : material->Floats)
                        values.push_back({{"property", key}, {"kind", "number"}, {"value", value}});
                    for (const auto &[key, value] : material->Colors)
                        values.push_back({{"property", key}, {"kind", "color"}, {"value", asset::VectorJson(value)}});
                    for (const auto &[key, value] : material->Textures)
                    {
                        values.push_back({{"property", key}, {"kind", "texture"}});
                        (void)value;
                    }
                    result.push_back({{"slot", slot++}, {"name", material->Name}, {"values", values}});
                }
        return result;
    }

    CreationAsset CreationService::NewAsset(const CreationState &state, std::string kind, std::string target,
                                            std::string name, std::string templateId, std::stop_token token)
    {
        CheckCancelled(token);
        auto binding = CharacterAssetReferences::Describe(*state.Character, target);
        if (!binding || (kind != "accessory" && kind != "hair" && kind != "clothes"))
            throw std::invalid_argument("素材类别或目标位置无效");
        if (templateId.empty())
            throw std::invalid_argument("请选择兼容的游戏材质与组件模板");
        CreationAsset asset;
        asset.Kind = std::move(kind);
        asset.Name = std::move(name);
        asset.TargetPath = std::move(target);
        asset.TemplateId = std::move(templateId);
        CharacterCatalog catalog(game_, data_ / "Creation/Cache", runtime_ / "classdata.tpk",
                                 Resources(&state, {}, token));
        catalog.Load({}, token);
        auto source = asset::ResolveTemplate(
            catalog, *state.Character,
            {{"target_path", asset.TargetPath}, {"kind", asset.Kind}, {"template_asset_id", asset.TemplateId}}, game_,
            token);
        auto values = Json(source.Entry.Values).dump();
        asset.TemplateSource = {{"bundle", Utf8(source.Prefab.Bundle)},
                                {"asset", source.Prefab.Asset},
                                {"bundle_hash", HashFile(source.Prefab.Bundle, token)},
                                {"list_hash", Sha256(Bytes(values.begin(), values.end()))}};
        for (const auto &item : state.Assets)
            asset.Slot = std::max(asset.Slot, item.Slot + 1);
        return asset;
    }

    void CreationService::TransferWeights(CreationAsset &asset, const asset::Geometry &reference)
    {
        asset::Geometry geometry;
        geometry.Bones = asset.Bones;
        for (const auto &object : asset.Objects)
        {
            auto mesh = object.Mesh.Triangulate();
            mesh.Name = object.Id + "_" + mesh.Name;
            for (auto &v : mesh.Vertices)
                v.Weights.clear();
            geometry.Meshes.push_back(std::move(mesh));
        }
        asset::AdaptGeometry(geometry, reference, {{"kind", "clothes"}, {"transfer_weights", true}});
        for (std::size_t i = 0; i < asset.Objects.size(); ++i)
        {
            // 三角化按面角输出；把结果映射回稳定顶点，而不重建拓扑和 UV 接缝。
            auto &object = asset.Objects[i];
            std::size_t corner = 0;
            for (const auto &[id, face] : object.Mesh.Faces)
            {
                for (const auto &c : face.Corners)
                    object.Mesh.Vertices.at(c.Vertex).Weights = geometry.Meshes.at(i).Vertices.at(corner++).Weights;
                (void)id;
            }
            object.Mesh.NormalizeWeights(asset.LockedBones);
        }
    }

    CharacterDocument CreationService::Cover(const CreationState &state, const fs::path &output, std::stop_token token)
    {
        RequireCurrentBuild(state);
        auto document = *state.Character;
        if (state.CoverSettings.value("mode", "render") == "import")
            return document;
        Json args = state.CoverSettings;
        args["views"] = Json::array({args.value("view", "front")});
        args["width"] = 504;
        args["height"] = 704;
        auto render = RenderCharacter(game_, data_ / "Creation/Cache", runtime_, document, args, output,
                                      Resources(&state, {}, token), token);
        auto png = ReadFile(Path(render.at("images").at(0).at("path")), 32 * 1024 * 1024, token);
        return document.WithCover(std::move(png));
    }

    CardRecord CreationService::ExportCard(const CreationState &state, const fs::path &output, std::stop_token token)
    {
        RequireCurrentBuild(state);
        CardExportService::ValidateOutput(game_, output);
        auto document = Cover(state, data_ / "Creation/Renders" / Path(NewId()), token);
        CardRepository repository(data_ / "Creation/OutputIndex");
        auto target = CardFileService::UniqueDestination(output, Path(ExportPaths::SafeName(state.Name) + ".png"));
        return CardEditService(repository).SaveNew(document, target, token).Card;
    }

    CardExportReport CreationService::ExportPackage(const CreationState &state, const fs::path &output,
                                                    std::stop_token token)
    {
        RequireCurrentBuild(state);
        CardExportService::ValidateOutput(game_, output);
        CardRepository repository(data_ / "Creation/OutputIndex");
        auto document = Cover(state, data_ / "Creation/Renders" / Path(NewId()), token);
        auto temporary = data_ / "Creation/Exports" / Path(NewId()) / "character.png";
        auto card = CardEditService(repository).SaveNew(document, temporary, token).Card;
        CardExportService exporter(repository, runtime_ / "classdata.tpk");
        auto resources = Resources(&state, {}, token);
        auto plan = exporter.BuildPlan({card}, game_, output, {CardExportMode::PerCard, CardExportLevel::WithResources},
                                       {}, token, &resources->Index);
        return exporter.Export(plan, {}, token);
    }

    fs::path CreationService::ExportMod(const CreationState &state, const fs::path &output, std::stop_token token)
    {
        if (state.Assets.empty())
            throw std::runtime_error("当前工程没有可导出的 Mod 素材");
        RequireCurrentBuild(state);
        CardExportService::ValidateOutput(game_, output);
        auto source = Path(state.LastBuild.at("mod").get<std::string>());
        auto target = CardFileService::UniqueDestination(output, source.filename());
        WriteAtomic(target, ReadFile(source, 2ull * 1024 * 1024 * 1024, token));
        if (HashFile(target, token) != state.LastBuild.at("mod_hash").get<std::string>())
            throw std::runtime_error("Mod 导出校验失败");
        return target;
    }
}
