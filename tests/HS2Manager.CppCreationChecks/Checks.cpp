#include "CreationService.h"
#include "Animation/SimulationMath.h"
#include <windows.h>
#include <objbase.h>
#include <iostream>
using namespace hs2;
using namespace hs2::creation;
#include "../HS2Manager.CppAgentChecks/Fixtures.h"

namespace
{
    int assertions = 0;

    void Check(bool condition, const char *message)
    {
        ++assertions;
        if (!condition)
            throw std::runtime_error(message);
    }

    template <class F> void Throws(F action)
    {
        bool threw = false;
        try
        {
            action();
        }
        catch (const std::exception &)
        {
            threw = true;
        }
        Check(threw, "expected rejection");
    }

    MeshDocument Quad()
    {
        MeshDocument m;
        for (auto p : {Vector3{-1, 0, -1}, {-1, 0, 1}, {1, 0, 1}, {1, 0, -1}})
        {
            MeshVertex v;
            v.Position = p;
            v.Weights = {{"root", .75f}, {"child", .25f}};
            v.Shapes["test"] = {{0, .5f, 0}};
            m.Vertices.emplace(m.NextId++, v);
        }
        m.ShapeWeights["test"] = 100;
        m.AddFace({{1, {0, 0}}, {2, {0, 1}}, {3, {1, 1}}, {4, {1, 0}}});
        m.RebuildEdges();
        m.Validate();
        return m;
    }

    MeshDocument Cube()
    {
        MeshDocument m;
        for (auto p : {Vector3{-1, -1, -1},
                       {1, -1, -1},
                       {1, 1, -1},
                       {-1, 1, -1},
                       {-1, -1, 1},
                       {1, -1, 1},
                       {1, 1, 1},
                       {-1, 1, 1}})
        {
            MeshVertex v;
            v.Position = p;
            v.Weights = {{"root", .7f}, {"child", .3f}};
            v.Shapes["test"] = {{0, .5f, 0}};
            m.Vertices.emplace(m.NextId++, v);
        }
        m.ShapeWeights["test"] = 100;
        for (auto f : {std::array<ElementId, 4>{1, 4, 3, 2},
                       {5, 6, 7, 8},
                       {1, 5, 8, 4},
                       {2, 3, 7, 6},
                       {1, 2, 6, 5},
                       {4, 8, 7, 3}})
            m.AddFace({{f[0], {0, 0}}, {f[1], {1, 0}}, {f[2], {1, 1}}, {f[3], {0, 1}}}, 2);
        m.RebuildEdges();
        return m;
    }

    void Closed(const MeshDocument &mesh)
    {
        std::map<std::pair<ElementId, ElementId>, int> count;
        for (auto &[id, f] : mesh.Faces)
            for (size_t i = 0; i < f.Corners.size(); ++i)
                ++count[std::minmax(f.Corners[i].Vertex, f.Corners[(i + 1) % f.Corners.size()].Vertex)];
        Check(std::all_of(count.begin(), count.end(),
                          [](auto &row)
                          {
                              return row.second == 2;
                          }),
              "closed manifold after topology edit");
        mesh.Triangulate();
    }

    void GeometryChecks()
    {
        asset::Geometry reference, authored;
        reference.Bones = {{"root", "", {}, {0, 0, 0, 1}, false}, {"child", "root", {}, {0, 0, 0, 1}, false}};
        authored.Bones = reference.Bones;
        authored.Bones.back().Added = true;
        authored.Meshes.push_back(Quad().Triangulate());
        asset::AdaptGeometry(authored, reference, {{"kind", "hair"}});
        Check(std::find_if(authored.Bones.begin(), authored.Bones.end(),
                           [](const auto &bone)
                           {
                               return bone.Name == "child";
                           })
                  ->Added,
              "rebuild retains authored bone when reference already contains it");
        auto original = Quad();
        MeshSelection all;
        auto faceId = original.Faces.begin()->first;
        Check(original.SelectedVertices({SelectionMode::Face, {faceId}}).size() == 4 &&
                  original.SelectedVertices({SelectionMode::Uv, {faceId}}).size() == 4,
              "face and UV selections resolve their vertices");
        auto edgeId = original.Edges.begin()->second.Id;
        auto edgeVertices = original.SelectedVertices({SelectionMode::Edge, {edgeId}});
        Check(edgeVertices.size() == 2 && original.SelectedVertices({SelectionMode::Vertex, {999999}}).empty(),
              "edge selection resolves two vertices and invalid vertex stays empty");
        auto movedFace = original;
        movedFace.Execute({"transform", {{"position", {0, 1, 0}}}}, {SelectionMode::Face, {faceId}});
        Check(std::all_of(movedFace.Vertices.begin(), movedFace.Vertices.end(),
                          [](const auto &v)
                          {
                              return std::abs(v.second.Position.y - 1) < 1e-6f;
                          }),
              "face transform moves selected vertices");
        auto mesh = original;
        mesh.Execute({"subdivide"}, all);
        Check(mesh.Faces.size() == 4 && mesh.Vertices.size() == 9, "quad subdivision topology");
        for (const auto &[id, v] : mesh.Vertices)
        {
            Check(std::abs(v.Shapes.at("test").Position.y - .5f) < .0001f, "shape interpolation");
            Check(v.Weights.size() == 2, "weight interpolation");
            (void)id;
        }
        auto triangles = mesh.Triangulate();
        Check(triangles.Indices.size() == 24 && triangles.Shapes[0].Positions.size() == triangles.Vertices.size(),
              "triangulation attributes");
        auto encoded = Json::to_cbor(mesh.ToJson());
        Check(MeshDocument::FromJson(Json::from_cbor(encoded)).ToJson() == mesh.ToJson(), "stable topology roundtrip");
        mesh = original;
        mesh.Execute({"extrude", {{"distance", .3}}}, all);
        Check(mesh.Faces.size() == 5 && mesh.Vertices.size() == 8, "region extrusion");
        mesh.Triangulate();
        mesh = original;
        mesh.Execute({"inset", {{"amount", .2}}}, all);
        Check(mesh.Faces.size() == 5 && mesh.Vertices.size() == 8, "face inset");
        mesh.Triangulate();
        mesh = original;
        mesh.Execute({"cut"}, all);
        Check(mesh.Faces.size() == 2, "diagonal cut");
        auto diagonal = mesh.Edges.at(std::pair<ElementId, ElementId>{1, 3}).Id;
        mesh.Execute({"dissolve"}, {SelectionMode::Edge, {diagonal}});
        Check(mesh.Faces.size() == 1 && mesh.Faces.begin()->second.Corners.size() == 4, "dissolve restores polygon");
        mesh = original;
        mesh.Execute({"loop_cut"}, {SelectionMode::Edge, {mesh.Edges.begin()->second.Id}});
        Check(mesh.Faces.size() == 2 && mesh.Vertices.size() == 6, "quad loop cut");
        mesh.Triangulate();
        mesh = original;
        mesh.Execute({"mirror", {{"axis", 0}}}, all);
        Check(mesh.Faces.size() == 2, "mirror duplicate");
        mesh = original;
        mesh.Execute({"transform", {{"position", {1, 2, 3}}, {"scale", {-1, 1, 1}}}}, all);
        Check(std::abs(mesh.Center(all).y - 2) < .0001f && mesh.FaceNormal(mesh.Faces.begin()->first).y > .9f,
              "negative transform winding");
        mesh = original;
        mesh.Execute({"uv_unwrap"}, all);
        mesh.Execute({"uv_pack"}, all);
        for (const auto &[id, f] : mesh.Faces)
            for (const auto &c : f.Corners)
                Check(c.Uv.x >= 0 && c.Uv.x <= 1 && c.Uv.y >= 0 && c.Uv.y <= 1, "UV atlas bounds");
        mesh = original;
        mesh.Execute({"subdivide"}, all);
        mesh.Execute({"sculpt_inflate", {{"center", {0, 0, 0}}, {"radius", 2}, {"strength", .2}}}, all);
        Check(mesh.Center(all).y > 0, "sculpt displaces geometry");
        mesh.Execute({"weight_paint", {{"center", {0, 0, 0}}, {"radius", 10}, {"bone", "child"}, {"strength", .5}}},
                     all);
        for (const auto &[id, v] : mesh.Vertices)
        {
            float sum = 0;
            for (const auto &w : v.Weights)
                sum += w.Weight;
            Check(std::abs(sum - 1) < .0001f, "paint weights normalize");
            (void)id;
        }
        mesh = original;
        auto hit = mesh.Pick({0, 2, 0}, {0, -1, 0});
        Check(hit.has_value() && std::abs(hit->Uv.x - .5f) < .001f, "mesh picking UV barycentrics");
        auto before = mesh.ToJson();
        Throws(
            [&]
            {
                mesh.Execute({"transform", {{"scale", {0, 1, 1}}}}, all);
            });
        Check(mesh.ToJson() == before, "failed command atomicity");
        mesh = Cube();
        mesh.Execute({"bevel", {{"amount", .2}}}, {});
        Check(mesh.Faces.size() == 26 && mesh.Vertices.size() == 24, "all edge bevel creates strips and corner caps");
        Closed(mesh);
        for (auto &[id, v] : mesh.Vertices)
            Check(std::abs(v.Shapes.at("test").Position.y - .5f) < .0001f && v.Weights.size() == 2,
                  "bevel propagates shape and skin");
        mesh = Cube();
        auto edge = mesh.Edges.begin()->second.Id;
        mesh.Execute({"bevel", {{"amount", .2}}}, {SelectionMode::Edge, {edge}});
        Check(mesh.Faces.size() == 7 && mesh.Vertices.size() == 10, "single edge bevel affects adjacent faces");
        Closed(mesh);
        mesh = Cube();
        mesh.Execute({"bevel", {{"amount", .2}}}, {SelectionMode::Vertex, {1}});
        Closed(mesh);
        mesh = Cube();
        auto face = mesh.Faces.begin()->first;
        mesh.Execute({"delete"}, {SelectionMode::Face, {face}});
        mesh.Execute({"fill"}, {});
        Closed(mesh);
        mesh = original;
        auto seam = mesh.Edges.begin()->second.Id;
        mesh.Execute({"uv_seam"}, {SelectionMode::Edge, {seam}});
        mesh.Execute({"split"}, {SelectionMode::Edge, {seam}});
        Check(std::count_if(mesh.Edges.begin(), mesh.Edges.end(),
                            [](auto &e)
                            {
                                return e.second.Seam;
                            }) == 2,
              "edge split retains UV seam");
        mesh = original;
        mesh.Execute({"normal", {{"mode", "flip"}}}, {});
        Check(mesh.FaceNormal(mesh.Faces.begin()->first).y < -.9f, "normal flip reverses winding");
        for (auto op : {"sculpt_move", "sculpt_smooth", "sculpt_flatten", "sculpt_pinch", "mask"})
        {
            mesh = original;
            mesh.Execute({op,
                          {{"center", {-1, .2, -1}},
                           {"normal", {0, 1, 0}},
                           {"delta", {0, 1, 0}},
                           {"radius", 4},
                           {"strength", .3}}},
                         {});
            mesh.Validate();
            Check(mesh.ToJson() != original.ToJson(), "brush edits selected region");
        }
        mesh = original;
        mesh.Execute({"mask", {{"center", {0, 0, 0}}, {"radius", 4}, {"strength", 10}}}, {});
        auto masked = mesh.ToJson();
        mesh.Execute({"sculpt_inflate", {{"center", {0, 0, 0}}, {"radius", 4}, {"strength", 1}}}, {});
        Check(mesh.ToJson() == masked, "full mask protects mesh");
    }

    void PaintChecks(const fs::path &root)
    {
        auto paint = PaintDocument::Blank(32, 32);
        std::array points{Vector2{16, 16}};
        paint.Stroke(0, points, {1, 0, 0, 1}, 5, 1, 1);
        Check(paint.Sample({16, 16}).x > .9f && paint.Sample({0, 0}).w == 0, "brush coverage");
        auto before = paint;
        paint.Stroke(0, points, {1, 1, 1, 1}, 5, 1, 1, true);
        Check(paint.Sample({16, 16}).w == 0 && before.Sample({16, 16}).w == 1, "immutable paint history");
        paint.Fill(0, {0, 0}, {0, 1, 0, 1});
        Check(paint.Sample({0, 0}).y == 1, "flood fill");
        paint.AddLayer("top");
        paint.Stroke(1, points, {0, 0, 1, 1}, 5, 1, 1);
        paint.Layers[1].Opacity = .5f;
        auto sample = paint.Sample({16, 16});
        Check(sample.y > .45f && sample.z > .45f, "layer compositing");
        auto restored = PaintDocument::FromJson(Json::from_cbor(Json::to_cbor(paint.ToJson())));
        Check(restored.Composite() == paint.Composite(), "paint roundtrip");
        paint.Save(root / "paint.png");
        Check(PaintDocument::Import(root / "paint.png").Composite() == paint.Composite(), "PNG roundtrip");
        paint.Layers[1].Locked = true;
        Throws(
            [&]
            {
                paint.Stroke(1, points, {1, 1, 1, 1}, 4, 1, 1);
            });
    }

    void InstallChecks(const fs::path &root)
    {
        auto game = root / "Game", data = root / "InstallData";
        fs::create_directories(game / "abdata");
        fs::create_directories(game / "mods");
        fs::create_directories(game / "BepInEx/plugins");
        CreationService service(game, data, root);
        asset::Geometry body;
        body.Bones = {{"root", "", {}, {0, 0, 0, 1}, false}, {"child", "root", {}, {0, 0, 0, 1}, false}};
        body.Meshes.push_back(Quad().Triangulate());
        body.Meshes.back().Role = "body";
        CreationAsset clothing;
        clothing.Kind = "clothes";
        CreationObject full;
        full.Mesh = Quad();
        for (auto &[id, vertex] : full.Mesh.Vertices)
            vertex.Weights.clear();
        clothing.Objects.push_back(full);
        auto half = full;
        half.Id = NewId();
        half.HalfState = true;
        clothing.Objects.push_back(half);
        service.TransferWeights(clothing, body);
        Check(clothing.Objects[0].Mesh.Name == clothing.Objects[1].Mesh.Name &&
                  clothing.Objects[0].Mesh.Vertices.at(1).Weights.size() == 2 &&
                  clothing.Objects[1].Mesh.Vertices.at(1).Weights.size() == 2 &&
                  clothing.Objects[1].Mesh.Faces.begin()->second.Corners[1].Uv.y == 1 &&
                  clothing.Objects[1].Mesh.Vertices.at(1).Shapes.contains("test"),
              "weight transfer accepts same-name full and half meshes while preserving editing data");
        auto source = root / "install-source.png";
        auto clean = CharacterDocument::FromBytes(Fixture(), source).WithExtensions({});
        WriteAtomic(source, clean.ToBytes());
        CreationState state;
        state.Character = CharacterDocument::Open(source);
        auto id = NewId();
        state.LastBuild = {{"status", "complete"},
                           {"guid", "hs2manager." + id},
                           {"card", Utf8(source)},
                           {"card_hash", HashFile(source)},
                           {"mod", ""},
                           {"state_token", state.Token}};
        auto plan = service.PlanInstall(state, id);
        if (!plan.Conflicts.empty())
            std::cerr << plan.Summary();
        Check(plan.Conflicts.empty() && plan.HasChanges(), "new install plan");
        auto result = service.Install(plan);
        Check(result.Status == "installed" && result.Cards.size() == 1, "install committed");
        auto target = result.Cards.front();
        Check(HashFile(target) == HashFile(source), "installed bytes verified");
        auto same = service.PlanInstall(state, id);
        Check(!same.HasChanges(), "same content skipped");
        auto old = ReadFile(target);
        WriteAtomic(source, state.Character->WithName("updated").ToBytes());
        state.LastBuild["card_hash"] = HashFile(source);
        auto update = service.PlanInstall(state, id);
        auto changed = service.Install(update);
        Check(changed.Status == "installed" && HashFile(target) == HashFile(source), "same project update");
        Check(service.Rollback(changed.Id).Status == "rolled_back" && ReadFile(target) == old,
              "update rollback restores backup");
        WriteAtomic(target, Bytes{1, 2, 3});
        auto conflict = service.Rollback(result.Id);
        Check(conflict.Status == "rollback_conflict" && ReadFile(target) == Bytes({1, 2, 3}),
              "external modifications preserved");
        WriteAtomic(target, old);
        Check(service.Rollback(result.Id).Status == "rolled_back" && !fs::exists(target),
              "new install rollback removes only owned file");
        state.Token = NewId();
        Throws(
            [&]
            {
                service.PlanInstall(state, id);
            });
        state.LastBuild["state_token"] = state.Token;
        auto cancelPlan = service.PlanInstall(state, id);
        std::stop_source cancel;
        cancel.request_stop();
        Throws(
            [&]
            {
                service.Install(cancelPlan, {}, cancel.get_token());
            });
        Check(!fs::exists(target), "cancelled install does not write target");
        auto stale = service.PlanInstall(state, id);
        WriteAtomic(target, Bytes{4, 5, 6});
        Check(service.Install(stale).Status == "rolled_back" && ReadFile(target) == Bytes({4, 5, 6}),
              "stale installation plan cannot overwrite");
    }

    void BodyDependencyChecks(const fs::path &root)
    {
        auto game = root / "BodyGame";
        auto package = root / "BodyPackage";
        fs::create_directories(game / "mods");
        fs::create_directories(package);
        std::string xml =
            "<manifest "
            "schema-ver=\"1\"><guid>body.mod</guid><game>HS2</game><KK_UncensorSelector><body><guid>body.fixture</"
            "guid></body></KK_UncensorSelector></manifest>";
        WriteAtomic(package / "manifest.xml", Bytes(xml.begin(), xml.end()));
        auto modPath = game / "mods/body.zipmod";
        WriteAssetZip(package, modPath);
        auto doc = CharacterDocument::FromBytes(Fixture(), root / "body-card.png");
        auto data = MessagePack::Map({{std::string("BodyGUID"), MessagePack::Scalar(std::string("body.fixture"))}});
        doc =
            doc.WithExtensions(MessagePack::Map({{std::string("com.deathweasel.bepinex.uncensorselector"),
                                                  MessagePack::Array({MessagePack::Scalar(std::int64_t(2)), data})}}));
        WriteAtomic(root / "body-card.png", doc.ToBytes());
        auto card = CardParser().Parse(root / "body-card.png");
        ModIndex index;
        index.Complete = true;
        index.Files.push_back(ModDependencyService::ReadManifest(modPath));
        auto found = CardExportDependencies(game, index).Collect(card, CardExportLevel::WithResources);
        Check(std::any_of(found.Files.begin(), found.Files.end(),
                          [&](auto &file)
                          {
                              return Path(file.Source) == modPath;
                          }),
              "explicit body selection exports body package");
        index.Files.clear();
        auto missing = CardExportDependencies(game, index).Collect(card, CardExportLevel::WithResources);
        Check(std::any_of(missing.Issues.begin(), missing.Issues.end(),
                          [](auto &issue)
                          {
                              return issue.find("body.fixture") != std::string::npos;
                          }),
              "missing body dependency reported");
    }

    void ProjectChecks(const fs::path &root)
    {
        auto source = root / "source.png";
        WriteAtomic(source, Fixture());
        auto document = CharacterDocument::Open(source);
        auto renamed = document.WithName("创作测试");
        auto decoded = CharacterDocument::FromBytes(renamed.ToBytes(), root / "new.png");
        Check(decoded.Node("parameter.fullname").Text() == "创作测试", "parameter editing roundtrip");
        Check(decoded.Node("face.unknown").ContentEquals(document.Node("face.unknown")), "unknown preserved");
        Throws(
            [&]
            {
                document.WithValue("parameter.sex", std::int64_t(0));
            });
        CardRepository repository(root / "Library");
        auto saved = CardEditService(repository).SaveNew(renamed, root / "new.png");
        Check(saved.Card.Name == "创作测试" && repository.FindById(saved.Card.Id).has_value(),
              "new card save and index");
        auto project = CreationProject::Create(root / "Projects", "测试工程", "character", document);
        auto path = project->Directory();
        Throws(
            [&]
            {
                CreationProject::Open(path);
            });
        project->BeginGesture();
        for (int i = 0; i < 3; ++i)
            project->Edit("修改",
                          [&](auto &state)
                          {
                              state.Character = state.Character->WithValue("face.shapeValueFace[0]", .3f + .1f * i);
                          });
        project->EndGesture();
        Check(project->IsDirty(), "dirty project");
        project->Undo();
        Check(!project->IsDirty(), "gesture undo returns saved state");
        project->Redo();
        project->Save(true);
        auto token = project->State().Token;
        project.reset();
        project = CreationProject::Open(path, true);
        Check(project->State().Token == token && project->IsDirty(), "recovery snapshot distinct from saved");
        project->Save();
        project.reset();
        project = CreationProject::Open(path);
        Check(project->State().Token == token && !project->IsDirty(), "saved project reopen");
        auto revision = project->Revision();
        Throws(
            [&]
            {
                project->Edit("无效",
                              [](auto &state)
                              {
                                  state.Name.clear();
                              });
            });
        Check(project->Revision() == revision, "invalid edit does not commit");
        auto delayed = project->PrepareSave(project->Snapshot(), project->Revision());
        project->Edit("新修改",
                      [](auto &s)
                      {
                          s.Name = "新修订";
                      });
        project->CommitSave(delayed, true);
        auto manifest = Json::parse(ReadFile(path / "project.json"));
        Check(!manifest.contains("recovery"), "outdated background snapshot not committed");
        project->Save();
        project->CommitSave(delayed, true);
        Check(!Json::parse(ReadFile(path / "project.json")).contains("recovery"),
              "recovery cannot displace manual save");
        auto committedManifest = ReadFile(path / "project.json");
        project->Edit("未保存修改",
                      [](auto &s)
                      {
                          s.Name = "写入失败验收";
                      });
        auto held = CreateFileW((path / "project.json").c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                                FILE_ATTRIBUTE_NORMAL, nullptr);
        Check(held != INVALID_HANDLE_VALUE, "hold manifest for disk write failure test");
        Throws(
            [&]
            {
                project->Save();
            });
        CloseHandle(held);
        Check(ReadFile(path / "project.json") == committedManifest && project->IsDirty(),
              "failed atomic commit preserves saved project");
        project->Undo();
        auto imports = root / "Imports";
        fs::create_directories(imports);
        WriteJson(imports / "model.gltf", {{"asset", {{"version", "2.0"}}},
                                           {"buffers", {{{"uri", "geometry%20data.bin"}, {"byteLength", 3}}}},
                                           {"images", {{{"uri", "texture.png"}}}}});
        WriteAtomic(imports / "geometry data.bin", Bytes{1, 2, 3});
        WriteAtomic(imports / "texture.png", referencePng);
        auto copied = project->ImportSource(imports / "model.gltf");
        Check(ReadFile(copied.parent_path() / "geometry data.bin") == Bytes({1, 2, 3}) &&
                  HashFile(copied.parent_path() / "texture.png") == HashFile(imports / "texture.png"),
              "GLTF resources copied");
        WriteJson(imports / "escape.gltf", {{"images", {{{"uri", "../source.png"}}}}});
        Throws(
            [&]
            {
                project->ImportSource(imports / "escape.gltf");
            });
        WriteJson(imports / "missing.gltf", {{"buffers", {{{"uri", "missing.bin"}}}}});
        Throws(
            [&]
            {
                project->ImportSource(imports / "missing.gltf");
            });
        auto longFile =
            root / Path(std::string(90, 'a')) / Path(std::string(90, 'b')) / Path(std::string(90, 'c')) / "image.png";
        WriteAtomic(longFile, referencePng);
        Check(ReadFile(longFile) == referencePng && HashFile(longFile) == Sha256(referencePng),
              "long path atomic file roundtrip");
        Check(PaintDocument::Import(longFile).Width > 0, "long path image decode");
        auto snapshotPath = path / "Snapshots" /
                            Path(Json::parse(ReadFile(path / "project.json")).at("saved").get<std::string>()) /
                            "document.cbor";
        auto savedBytes = ReadFile(snapshotPath);
        project.reset();
        auto duplicate = CreationProject::Duplicate(root / "Projects", path, "副本");
        Check(duplicate->Directory() != path && duplicate->State().Name == "副本", "project duplication");
        Check(fs::is_regular_file(duplicate->Directory() / fs::relative(copied, path)),
              "duplicated project retains imported sources");
        WriteAtomic(snapshotPath, Bytes{1, 2, 3});
        Throws(
            [&]
            {
                CreationProject::Open(path);
            });
        WriteAtomic(snapshotPath, savedBytes);
        Check(CreationProject::Open(path)->State().Name == "新修订", "committed snapshot survives failed reopen");
        Check(ReadFile(source) == Fixture(), "source card unchanged");
    }
}

int wmain(int argc, wchar_t **argv)
{
    auto root = fs::temp_directory_path() / Path("hs2-creation-checks-" + NewId());
    fs::create_directories(root);
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    int result = 0;
    try
    {
        if (argc < 2)
            throw std::invalid_argument("PNG fixture required");
        referencePng = ReadFile(argv[1]);
        GeometryChecks();
        PaintChecks(root);
        ProjectChecks(root);
        InstallChecks(root);
        BodyDependencyChecks(root);
        if (argc >= 4)
        {
            CreationService service(argv[2], root / "RealData", argv[3]);
            auto choices = InformationChoices(argv[2], fs::path(argv[3]) / "classdata.tpk");
            Check(!choices.at("personality").empty() && !choices.at("trait").empty(), "real information choices");
            std::cout << "information choices: " << choices.at("personality").size() << " personalities\n";
            for (int sex : {0, 1})
            {
                auto doc = service.DefaultCharacter(sex);
                Check(doc.Sex() == sex, "real default sex");
                WriteAtomic(root / (sex == 0 ? "default-male.png" : "default-female.png"), doc.ToBytes());
            }
        }
        std::cout << "creation checks: " << assertions << " passed\n";
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << "\n";
        result = 1;
    }
    CoUninitialize();
    if (result == 0)
        fs::remove_all(fs::path(NativeFilePath(root)));
    else
        std::cerr << "artifacts: " << Utf8(root) << "\n";
    return result;
}
