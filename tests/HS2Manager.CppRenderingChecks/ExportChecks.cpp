#include "Fbx/FbxScene.h"
#include <chrono>
#include <iostream>
#include <stdexcept>

int wmain(int argc, wchar_t **argv)
{
    std::filesystem::path output;
    try
    {
        if (argc != 2)
            throw std::runtime_error("Pass absolute path to HS2Manager.FbxBridge.dll.");
        hs2::FbxScene scene(std::filesystem::path(std::filesystem::path(argv[1]).generic_wstring()));
        int root = scene.Bone("Root", -1, {0, 0, 0}, {0, 0, 0, 1});
        int material = scene.Material("Material", "", "", "");
        const std::vector<float> positions{0, 0, 0, 1, 0, 0, 0, 1, 0}, normals{0, 0, 1, 0, 0, 1, 0, 0, 1},
            uv{0, 0, 1, 0, 0, 1};
        int mesh = scene.Mesh("Mesh", positions, normals);
        scene.Uv(mesh, 0, uv);
        const std::vector<int> indices{0, 1, 2};
        const std::vector<float> weights{1, 1, 1};
        scene.Triangles(mesh, material, indices, false);
        scene.Skin(mesh, root, indices, weights);
        auto shape = positions;
        shape[7] = 1.25f;
        scene.Shape(mesh, "Expression", 100, shape, normals);
        bool rejected = false;
        try
        {
            scene.Uv(mesh, 0, {uv.data(), 3});
        }
        catch (const std::invalid_argument &)
        {
            rejected = true;
        }
        if (!rejected)
            throw std::runtime_error("Truncated UV span accepted.");
        output =
            std::filesystem::temp_directory_path() /
            ("hs2-cpp-export-" + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()) + ".fbx");
        auto report = scene.WriteAndVerify(output);
        if (report.Meshes != 1 || report.Vertices != 3 || report.Polygons != 1 || report.Bones != 1 ||
            report.Clusters != 1 || report.Channels != 1 || report.ShapeFrames != 1 || report.Materials != 1)
            throw std::runtime_error("FBX scene counts differ.");
        rejected = false;
        try
        {
            scene.WriteAndVerify(output);
        }
        catch (const std::runtime_error &)
        {
            rejected = true;
        }
        if (!rejected)
            throw std::runtime_error("Existing FBX output overwritten.");
        std::filesystem::remove(output);
        std::cout << "PASS: native FBX create/write/reopen, UV/skin/blend shapes and overwrite protection\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        if (!output.empty())
        {
            std::error_code ignored;
            std::filesystem::remove(output, ignored);
        }
        std::cerr << error.what() << '\n';
        return 1;
    }
}
