#include "CreationService.h"
#include "Animation/SimulationMath.h"
#include <objbase.h>
#include <iostream>

using namespace hs2;
using namespace hs2::creation;

namespace
{
    void Require(bool value, const char *message)
    {
        if (!value)
            throw std::runtime_error(message);
    }

    CreationTexture Texture(std::string name, Vector4 color)
    {
        CreationTexture texture;
        texture.Name = std::move(name);
        texture.Image = PaintDocument::Blank(64, 64);
        texture.Image.Fill(0, {0, 0}, color);
        return texture;
    }

    void AddGeometry(CreationAsset &item, asset::Geometry geometry)
    {
        item.Bones = std::move(geometry.Bones);
        for (auto &mesh : geometry.Meshes)
        {
            CreationObject object;
            object.Mesh = MeshDocument::FromMesh(mesh);
            item.Objects.push_back(std::move(object));
        }
    }

    void Populate(CreationService &service, const std::shared_ptr<CreationProject> &project)
    {
        project->Edit("设置配饰分类",
                      [](auto &state)
                      {
                          state.Character = state.Character->WithValue("accessory.parts[0].type", std::int64_t(351));
                      });
        auto reference = service.Reference(project->State());
        auto anchor = std::find_if(reference.Bones.begin(), reference.Bones.end(),
                                   [](auto &b)
                                   {
                                       return b.Name == "N_Head";
                                   });
        Require(anchor != reference.Bones.end(), "reference attachment missing");
        auto head = std::find_if(reference.Bones.begin(), reference.Bones.end(),
                                 [](auto &b)
                                 {
                                     return b.Name == "cf_J_Head";
                                 });
        if (head == reference.Bones.end())
            head = std::find_if(reference.Bones.begin(), reference.Bones.end(),
                                [](auto &b)
                                {
                                    return b.Name == "cm_J_Head";
                                });
        Require(head != reference.Bones.end(), "reference head missing");

        auto accessory = service.NewAsset(project->State(), "accessory", "accessory.parts[0].id", "手工配饰", "351::0");
        auto geometry = asset::BuildRecipe({{"operations", Json::array({{{"op", "primitive"},
                                                                         {"primitive", "sphere"},
                                                                         {"name", "creation_accessory"},
                                                                         {"radius", .6},
                                                                         {"rings", 8},
                                                                         {"segments", 12},
                                                                         {"position", {1.2, 1.3, 0}}}})}},
                                           reference);
        for (auto &mesh : geometry.Meshes)
            asset::TransformMesh(mesh, asset::BoneWorld(*anchor));
        AddGeometry(accessory, std::move(geometry));
        accessory.Textures.push_back(Texture("配饰底色", {.2f, .7f, .9f, 1}));
        project->Edit("添加配饰",
                      [&](auto &state)
                      {
                          state.Assets.push_back(accessory);
                      });

        auto hair = service.NewAsset(project->State(), "hair", "hair.parts[0].id", "曲线动态发束", "300::1");
        Json recipe{
            {"bones",
             Json::array(
                 {{{"name", "creation_strand_root"}, {"parent", head->Name}, {"position", {-.6, 2, .7}}},
                  {{"name", "creation_strand_mid"}, {"parent", "creation_strand_root"}, {"position", {-.45, -1, .2}}},
                  {{"name", "creation_strand_tip"},
                   {"parent", "creation_strand_mid"},
                   {"position", {-.1, -1.2, -.1}}}})},
            {"operations",
             Json::array({{{"op", "curve"},
                           {"name", "creation_hair"},
                           {"mode", "ribbon"},
                           {"anchor", head->Name},
                           {"points", {{-.6, 2, .7}, {-1.05, 1, .9}, {-1.15, -.2, .8}}},
                           {"bones", {"creation_strand_root", "creation_strand_mid", "creation_strand_tip"}},
                           {"width", .6}}})}};
        AddGeometry(hair, asset::BuildRecipe(recipe, reference));
        hair.DynamicRoots = {"creation_strand_root"};
        hair.DynamicSettings = {
            {"creation_strand_root",
             {{"damping", .12}, {"stiffness", .15}, {"elasticity", .18}, {"inertia", .1}, {"radius", .04}}}};
        hair.Objects.front().Generator = recipe.at("operations").at(0);
        auto meshText = hair.Objects.front().Mesh.ToJson().dump();
        hair.Objects.front().GeneratorMeshHash = Sha256(Bytes(meshText.begin(), meshText.end()));
        project->Edit("添加动态发型",
                      [&](auto &state)
                      {
                          state.Assets.push_back(hair);
                      });

        auto clothes = service.NewAsset(project->State(), "clothes", "clothes.parts[0].id", "独立半脱服装",
                                        project->State().Character->Sex() == 0 ? "140::1" : "240::1");
        auto body = asset::BuildRecipe({{"operations", Json::array({{{"op", "body_surface"},
                                                                     {"name", "creation_clothes"},
                                                                     {"minimum", {-20, 0, -20}},
                                                                     {"maximum", {20, 14.8, 20}},
                                                                     {"offset", .05}}})}},
                                       reference);
        AddGeometry(clothes, std::move(body));
        auto full = clothes.Objects;
        for (auto object : full)
        {
            object.Id = NewId();
            object.HalfState = true;
            object.Mesh.Execute({"transform", {{"position", {0, -.5, 0}}}}, {});
            clothes.Objects.push_back(std::move(object));
        }
        clothes.IndependentHalfState = true;
        auto mask = Texture("身体遮罩", {1, 1, 1, 1});
        mask.Kind = "mask";
        mask.Property = "body";
        clothes.Textures.push_back(std::move(mask));
        project->Edit("添加服装与覆盖贴图",
                      [&](auto &state)
                      {
                          state.Assets.push_back(clothes);
                          for (auto target : {"face", "body", "clothes"})
                          {
                              auto overlay = Texture(std::string("覆盖 · ") + target, {0, 0, 0, 0});
                              overlay.Kind = "overlay";
                              overlay.Target = target;
                              std::array points{Vector2{32, 32}};
                              overlay.Image.Stroke(0, points, {.1f, .8f, .3f, .65f}, 8, 1, .5f);
                              state.Overlays.push_back(std::move(overlay));
                          }
                      });
    }
}

int wmain(int argc, wchar_t **argv)
{
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    int status = 0;
    try
    {
        if (argc != 5 && argc != 6)
            throw std::invalid_argument("usage: creation_acceptance game data runtime output");
        fs::path output(argv[4]);
        fs::create_directories(output);
        if (argc == 6 && std::wstring_view(argv[5]) == L"--recheck-dependencies")
        {
            CreationService service(argv[1], argv[2], argv[3]);
            auto report = Json::parse(ReadFile(output / "acceptance.json"));
            for (auto &entry : report.at("cases"))
            {
                auto project = CreationProject::Open(Path(entry.at("project").get<std::string>()));
                auto package = service.ExportPackage(project->State(), output / "VerifiedPackages");
                Require(package.Status == CardExportStatus::Complete, "refreshed dependencies incomplete");
                entry["packages"] = package.OutputFolders;
            }
            WriteJson(output / "acceptance.json", report);
            std::cout << "dependency export recheck passed" << std::endl;
            CoUninitialize();
            return 0;
        }
        Require(!fs::exists(output / "acceptance.json"), "use a new acceptance output directory");
        CreationService service(argv[1], argv[2], argv[3]);
        Json reports = Json::array();
        auto progress = [](const WorkProgress &info)
        {
            std::cout << info.Message << std::endl;
        };
        for (int sex : {1, 0})
        {
            auto project = service.Create(sex ? "创作验收女性" : "创作验收男性", "mod", sex);
            Populate(service, project);
            project->Save();
            auto directory = project->Directory();
            auto guid = project->Guid();
            auto token = project->State().Token;
            project.reset();
            project = CreationProject::Open(directory);
            Require(project->State().Token == token && project->State().Assets.size() == 3,
                    "saved assets did not reopen");
            auto build = service.Build({project->Snapshot(), directory, guid, project->Revision()}, progress);
            Require(build.Report.at("assets").size() == 3, "multiasset package incomplete");
            project->AcceptBuild(build.Report, *build.Character, build.Revision);
            project->Save();
            auto originalMod = service.ExportMod(project->State(), output / "Mods");
            auto card = service.ExportCard(project->State(), output / "Cards");
            auto duplicateCard = service.ExportCard(project->State(), output / "Cards");
            Require(card.Path != duplicateCard.Path, "same-name export overwrote card");
            auto package = service.ExportPackage(project->State(), output / "Packages");
            Require(package.Status == CardExportStatus::Complete, "package export incomplete");

            auto stale = project->Snapshot();
            auto staleRevision = project->Revision();
            project->Edit("修改配饰",
                          [](auto &state)
                          {
                              state.Assets.front().Objects.front().Mesh.Execute({"transform", {{"scale", {1.1, 1, 1}}}},
                                                                                {});
                          });
            bool rejected = false;
            try
            {
                project->AcceptBuild(build.Report, *build.Character, staleRevision);
            }
            catch (...)
            {
                rejected = true;
            }
            Require(rejected, "stale build overwrote edits");
            rejected = false;
            try
            {
                service.ExportMod(project->State(), output / "Stale");
            }
            catch (...)
            {
                rejected = true;
            }
            Require(rejected, "stale mod export accepted");
            auto update = service.Build({project->Snapshot(), directory, guid, project->Revision()}, progress);
            Require(update.Report.at("guid") == guid && update.Report.at("mod_hash") != build.Report.at("mod_hash"),
                    "stable GUID update failed");
            for (size_t i = 0; i < 3; ++i)
            {
                Require(update.Report.at("assets").at(i).at("slot_id") == build.Report.at("assets").at(i).at("slot_id"),
                        "asset slot changed");
                Require(update.Report.at("assets").at(i).at("additional_bones") ==
                            build.Report.at("assets").at(i).at("additional_bones"),
                        "rebuild lost authored bones");
            }
            project->AcceptBuild(update.Report, *update.Character, update.Revision);
            project->Save();
            auto finalPackage = service.ExportPackage(project->State(), output / "UpdatedPackages");
            Require(finalPackage.Status == CardExportStatus::Complete, "updated package incomplete");
            std::stop_source cancel;
            cancel.request_stop();
            rejected = false;
            try
            {
                service.Build({project->Snapshot(), directory, guid, project->Revision()}, {}, cancel.get_token());
            }
            catch (...)
            {
                rejected = true;
            }
            Require(rejected && project->State().LastBuild.at("mod_hash") == update.Report.at("mod_hash"),
                    "cancelled build lost valid output");
            reports.push_back({{"sex", sex},
                               {"project", Utf8(directory)},
                               {"guid", guid},
                               {"first", build.Report},
                               {"updated", update.Report},
                               {"packages", finalPackage.OutputFolders},
                               {"original_mod", Utf8(originalMod)},
                               {"original_card", card.Path},
                               {"native_validation", "passed"},
                               {"game_validation", "not_run"}});
            WriteJson(output / "acceptance-progress.json", reports);
        }
        WriteJson(output / "acceptance.json", {{"passed", true}, {"cases", reports}});
        std::cout << "creation native acceptance passed" << std::endl;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << std::endl;
        status = 1;
    }
    CoUninitialize();
    return status;
}
