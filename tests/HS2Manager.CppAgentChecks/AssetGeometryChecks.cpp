#include "AssetGeometry.h"
#include "Animation/SimulationMath.h"
#include "Fbx/FbxScene.h"
#include <cmath>
#include <cstring>
#include <iostream>
#include <set>

using namespace hs2;
using namespace hs2::asset;

namespace
{
    void Check(bool condition, const char *message)
    {
        if (!condition)
            throw std::runtime_error(message);
    }

    template <class F> void Reject(F action, const char *message)
    {
        try
        {
            action();
        }
        catch (const std::exception &)
        {
            return;
        }
        throw std::runtime_error(message);
    }

    bool Near(float a, float b)
    {
        return std::abs(a - b) < .0001f;
    }

    Json Triangle()
    {
        return {{"name", "triangle"},
                {"positions", {{0, 0, 0}, {1, 0, 0}, {0, 1, 0}}},
                {"indices", {0, 1, 2}},
                {"uv", {{0, 0}, {1, 0}, {0, 1}}}};
    }

    Geometry Reference()
    {
        auto g = GeometryOf({{"meshes", {Triangle()}}});
        g.Meshes[0].Role = "body";
        g.Bones = {{"cf_J_Root", "", {}}, {"cf_J_Head", "cf_J_Root", {0, 1, 0}}, {"other", "cf_J_Root", {1, 0, 0}}};
        g.Meshes[0].Vertices[0].Weights = {{"cf_J_Root", 1}};
        g.Meshes[0].Vertices[1].Weights = {{"other", 1}};
        g.Meshes[0].Vertices[2].Weights = {{"cf_J_Head", 1}};
        RebuildNormalsTangents(g.Meshes[0]);
        return g;
    }

    void TextFile(const fs::path &path, const std::string &value)
    {
        WriteAtomic(path, Bytes(value.begin(), value.end()));
    }

    Json ReadJson(const fs::path &path)
    {
        return Json::parse(ReadFile(path));
    }

    template <class T> void Append(Bytes &bytes, const T &value)
    {
        const auto *p = reinterpret_cast<const std::uint8_t *>(&value);
        bytes.insert(bytes.end(), p, p + sizeof(T));
    }

    void GltfFiles(const fs::path &root)
    {
        Bytes binary;
        for (float f : {0.f, 0.f, 0.f, 1.f, 0.f, 0.f, 0.f, 1.f, 0.f, 0.f, 0.f, 1.f, 0.f, 0.f, 1.f})
            Append(binary, f);
        Json doc = {{"asset", {{"version", "2.0"}}},
                    {"scene", 0},
                    {"scenes", {{{"nodes", {0}}}}},
                    {"nodes", {{{"mesh", 0}, {"translation", {1, 2, 3}}, {"scale", {-2, 1, 1}}}}},
                    {"meshes", {{{"primitives", {{{"attributes", {{"POSITION", 0}, {"TEXCOORD_0", 1}}}}}}}}},
                    {"buffers", {{{"byteLength", binary.size()}, {"uri", "triangle.bin"}}}},
                    {"bufferViews",
                     {{{"buffer", 0}, {"byteOffset", 0}, {"byteLength", 36}},
                      {{"buffer", 0}, {"byteOffset", 36}, {"byteLength", 24}}}},
                    {"accessors",
                     {{{"bufferView", 0},
                       {"componentType", 5126},
                       {"count", 3},
                       {"type", "VEC3"},
                       {"min", {0, 0, 0}},
                       {"max", {1, 1, 0}}},
                      {{"bufferView", 1}, {"componentType", 5126}, {"count", 3}, {"type", "VEC2"}}}}};
        WriteAtomic(root / "triangle.bin", binary);
        WriteJson(root / "triangle.gltf", doc);
        doc["buffers"][0].erase("uri");
        auto json = doc.dump();
        while (json.size() % 4)
            json += ' ';
        Bytes glb;
        Append(glb, std::uint32_t{0x46546c67});
        Append(glb, std::uint32_t{2});
        Append(glb, static_cast<std::uint32_t>(12 + 8 + json.size() + 8 + binary.size()));
        Append(glb, static_cast<std::uint32_t>(json.size()));
        Append(glb, std::uint32_t{0x4e4f534a});
        glb.insert(glb.end(), json.begin(), json.end());
        Append(glb, static_cast<std::uint32_t>(binary.size()));
        Append(glb, std::uint32_t{0x004e4942});
        glb.insert(glb.end(), binary.begin(), binary.end());
        WriteAtomic(root / "triangle.glb", glb);
    }
}

int main(int argc, char **argv)
{
    auto root = fs::temp_directory_path() / Path("hs2-native-assets-" + NewId());
    int exit = 0;
    try
    {
        fs::create_directories(root);
        auto reference = Reference();
        auto hair = GeometryOf({{"meshes", {Triangle()}}});
        AdaptGeometry(hair, reference, {{"kind", "hair"}});
        for (auto &v : hair.Meshes[0].Vertices)
            Check(v.Weights.size() == 1 && v.Weights[0].Bone == "cf_J_Head" && Near(v.Weights[0].Weight, 1),
                  "head binding");
        auto clothes = GeometryOf({{"meshes", {Triangle()}}});
        clothes.Meshes[0].Vertices[0].Position = {.25f, .25f, .2f};
        AdaptGeometry(clothes, reference, {{"kind", "clothes"}});
        auto weights = clothes.Meshes[0].Vertices[0].Weights;
        Check(weights.size() == 3, "barycentric transfer count");
        for (auto &w : weights)
            Check(Near(w.Weight, w.Bone == "cf_J_Root" ? .5f : .25f), "barycentric transfer values");
        auto mirrored =
            BuildRecipe({{"meshes", {Triangle()}},
                         {"operations",
                          {{{"op", "mirror"}, {"mesh", "triangle"}, {"name", "reflection"}, {"axis", "X"}},
                           {{"op", "merge"}, {"name", "combined"}, {"meshes", {"triangle", "reflection"}}}}}},
                        reference);
        AdaptGeometry(mirrored, reference, {{"kind", "accessory"}});
        auto &mesh = mirrored.Meshes[0];
        Check(mesh.Vertices.size() == 6 && mesh.Indices == std::vector<std::uint32_t>{0, 1, 2, 3, 5, 4},
              "mirror winding and merge");
        Check(Near(mesh.Vertices[4].Position.x, -1), "mirror positions");
        auto bad = hair;
        bad.Meshes[0].HasUv = false;
        Reject(
            [&]
            {
                AdaptGeometry(bad, reference, {{"kind", "hair"}});
            },
            "missing UV accepted");
        bad = hair;
        bad.Meshes[0].Indices[0] = 100;
        Reject(
            [&]
            {
                AdaptGeometry(bad, reference, {{"kind", "hair"}});
            },
            "bad index accepted");
        bad = hair;
        bad.Meshes[0].Vertices[0].Weights = {{"missing", 1}};
        Reject(
            [&]
            {
                AdaptGeometry(bad, reference, {{"kind", "hair"}});
            },
            "unknown bone accepted");
        Reject(
            [&]
            {
                AdaptGeometry(hair, reference, {{"kind", "hair"}, {"max_triangles", 0}});
            },
            "budget accepted");
        auto direct = Triangle();
        direct["weights"] = Json::array();
        for (int i = 0; i < 3; ++i)
            direct["weights"].push_back({{{"bone", "extra"}, {"weight", 2}}, {{"bone", "cf_J_Head"}, {"weight", 2}}});
        auto added = BuildRecipe(
            {{"meshes", {direct}}, {"bones", {{{"name", "extra"}, {"parent", "cf_J_Head"}, {"position", {0, .2, 0}}}}}},
            reference);
        AdaptGeometry(added, reference, {{"kind", "hair"}, {"dynamic_roots", {"extra"}}});
        Check(Near(added.Meshes[0].Vertices[0].Weights[0].Weight, .5f), "weight normalization");
        auto extra = std::find_if(added.Bones.begin(), added.Bones.end(),
                                  [](auto &b)
                                  {
                                      return b.Name == "extra";
                                  });
        Check(extra != added.Bones.end() && extra->Added && Near(extra->Position.y, 1.2f), "new bone transform");
        auto report = WriteReference(reference, root / "reference");
        Check(report["images"].size() == 1 && fs::file_size(Path(report["images"][0]["path"])) > 100,
              "UV image output");
        auto decoded = GeometryOf(ReadJson(root / "reference/reference-scene.json"));
        Check(GeometryJson(decoded) == GeometryJson(reference), "reference JSON roundtrip");
        std::stop_source cancelled;
        cancelled.request_stop();
        Reject(
            [&]
            {
                BuildRecipe({{"operations", {{{"op", "primitive"}, {"primitive", "box"}, {"name", "cancel"}}}}},
                            reference, cancelled.get_token());
            },
            "recipe ignored cancellation");
        GltfFiles(root);
        auto skinnedGltf = ReadJson(root / "triangle.gltf");
        auto skinnedBytes = ReadFile(root / "triangle.bin");
        skinnedBytes.insert(skinnedBytes.end(), 12, 0);
        for (int vertex = 0; vertex < 3; ++vertex)
            for (float weight : {1.f, 0.f, 0.f, 0.f})
                Append(skinnedBytes, weight);
        WriteAtomic(root / "skin.bin", skinnedBytes);
        skinnedGltf["buffers"][0] = {{"byteLength", skinnedBytes.size()}, {"uri", "skin.bin"}};
        skinnedGltf["bufferViews"].push_back({{"buffer", 0}, {"byteOffset", 60}, {"byteLength", 12}});
        skinnedGltf["bufferViews"].push_back({{"buffer", 0}, {"byteOffset", 72}, {"byteLength", 48}});
        skinnedGltf["accessors"].push_back(
            {{"bufferView", 2}, {"componentType", 5121}, {"count", 3}, {"type", "VEC4"}});
        skinnedGltf["accessors"].push_back(
            {{"bufferView", 3}, {"componentType", 5126}, {"count", 3}, {"type", "VEC4"}});
        skinnedGltf["meshes"][0]["primitives"][0]["attributes"]["JOINTS_0"] = 2;
        skinnedGltf["meshes"][0]["primitives"][0]["attributes"]["WEIGHTS_0"] = 3;
        skinnedGltf["nodes"][0]["skin"] = 0;
        skinnedGltf["nodes"].push_back(
            {{"name", "cf_J_Head"}, {"scale", {2, 3, 4}}, {"translation", {.1, .2, .3}}, {"children", {2}}});
        skinnedGltf["nodes"].push_back({{"name", "extra"}, {"translation", {0, .5, 0}}});
        skinnedGltf["scenes"][0]["nodes"].push_back(1);
        skinnedGltf["skins"] = {{{"joints", {2}}}};
        WriteJson(root / "skin.gltf", skinnedGltf);
        auto skin = ImportModel(root / "skin.gltf", {});
        auto importedExtra = std::find_if(skin.Bones.begin(), skin.Bones.end(),
                                          [](const auto &bone)
                                          {
                                              return bone.Name == "extra";
                                          });
        Check(importedExtra != skin.Bones.end() && importedExtra->HasMatrix && Near(importedExtra->Matrix._11, 2) &&
                  Near(importedExtra->Matrix._22, 3) && Near(importedExtra->Matrix._33, 4) &&
                  Near(importedExtra->Matrix._41, -1) && Near(importedExtra->Matrix._42, 17) &&
                  Near(importedExtra->Matrix._43, 3),
              "glTF skin preserves scaled hierarchical bind transform and unit conversion");
        Check(skin.Meshes[0].Vertices[0].Weights[0].Bone == "extra", "glTF joint indices resolve to names");
        for (auto format : {"triangle.gltf", "triangle.glb"})
        {
            auto imported = ImportModel(root / format, {});
            AdaptGeometry(imported, reference, {{"kind", "accessory"}});
            auto &m = imported.Meshes[0];
            Check(Near(m.Vertices[0].Position.x, -10) && Near(m.Vertices[0].Position.y, 20) &&
                      Near(m.Vertices[0].Position.z, 30),
                  "glTF coordinates");
            Check(Near(m.Vertices[1].Position.x, 10) && m.Indices == std::vector<std::uint32_t>{0, 1, 2},
                  "glTF mirrored transform");
        }
        auto escaped = ReadJson(root / "triangle.gltf");
        escaped["buffers"][0]["uri"] = "../outside.bin";
        WriteJson(root / "escape.gltf", escaped);
        Reject(
            [&]
            {
                ImportModel(root / "escape.gltf", {});
            },
            "external glTF path escape accepted");
        if (argc == 2)
        {
            auto bridge = fs::absolute(Path(argv[1]));
            TextFile(root / "triangle.obj", "v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nf 1/1 2/2 3/3\n");
            auto obj = ImportModel(root / "triangle.obj", bridge);
            Check(Near(obj.Meshes[0].Vertices[1].Position.x, -10), "OBJ meters to HS2 units");
            FbxScene fbx(bridge);
            int material = fbx.Material("test", "", "", "");
            std::vector<float> p{0, 0, 0, 1, 0, 0, 0, 1, 0}, n{0, 0, 1, 0, 0, 1, 0, 0, 1}, uv{0, 0, 1, 0, 0, 1};
            int id = fbx.Mesh("triangle", p, n);
            fbx.Uv(id, 0, uv);
            fbx.Triangles(id, material, std::array{0, 1, 2}, false);
            int head = fbx.Bone("cf_J_Head", -1, {0, 1, 0}, {0, 0, 0, 1});
            fbx.Skin(id, head, std::array{0, 1, 2}, std::array{1.f, 1.f, 1.f});
            auto shape = p;
            shape[2] = .1f;
            fbx.Shape(id, "morph", 100, shape, n);
            fbx.WriteAndVerify(root / "triangle.fbx");
            auto imported = ImportModel(root / "triangle.fbx", bridge);
            Check(imported.Meshes.size() == 1 && imported.Meshes[0].Shapes.size() == 1, "FBX geometry and morph");
            Check(Near(imported.Meshes[0].Shapes[0].Positions[0].z, .1f), "FBX morph delta");
            float maximum = 0;
            for (auto &v : imported.Meshes[0].Vertices)
                maximum = std::max(maximum, v.Position.x);
            Check(Near(maximum, 1), "FBX unit roundtrip");
            AdaptGeometry(imported, reference, {{"kind", "hair"}});
            Check(imported.Meshes[0].Vertices[0].Weights[0].Bone == "cf_J_Head", "FBX skin");
        }
        std::cout << "PASS native recipes, coordinates, reference, UV, skinning, imports and cancellation\n";
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << '\n';
        exit = 1;
    }
    std::error_code ignored;
    if (root.parent_path() == fs::temp_directory_path() && Utf8(root.filename()).starts_with("hs2-native-assets-"))
        fs::remove_all(root, ignored);
    return exit;
}
