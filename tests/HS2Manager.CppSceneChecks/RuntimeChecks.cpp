#include "../HS2Manager.CppRenderingChecks/FrozenDefinition.h"
#include "Runtime/CharacterRuntime.h"
#include "Animation/SimulationMath.h"
#include <cmath>
#include <fstream>
#include <iostream>
using namespace hs2;

namespace
{
    using Json = nlohmann::json;

    std::string NodePath(NativeSceneNode &node, NativeSceneNode *root)
    {
        std::string path = node.Name;
        for (auto *parent = node.Parent(); parent && parent != root; parent = parent->Parent())
            path = parent->Name + "/" + path;
        return path;
    }

    Json Snapshot(NativeCharacterScene &scene)
    {
        Json result = Json::object();
        for (auto *node : scene.Root->Traverse())
        {
            auto position = node->LocalPosition(), scale = node->LocalScale();
            auto rotation = node->LocalRotation();
            for (auto value : {position.x, position.y, position.z, scale.x, scale.y, scale.z, rotation.x, rotation.y,
                               rotation.z, rotation.w})
                if (!std::isfinite(value))
                    throw std::runtime_error("Non-finite runtime transform.");
            result[NodePath(*node, scene.Root.get())] = {position.x, position.y, position.z, rotation.x, rotation.y,
                                                         rotation.z, rotation.w, scale.x,    scale.y,    scale.z};
        }
        return result;
    }
} // 命名空间

int wmain(int argc, wchar_t **argv)
{
    try
    {
        if (argc < 5 || argc > 8)
        {
            std::cerr << "Usage: hs2_runtime_checks definition game classdata.tpk "
                         "physics-directory [output.json] [reference.json] [--settled]\n";
            return 2;
        }
        auto definition = fixture::Definition(argv[1]);
        auto database = std::make_shared<UnityClassDatabase>(argv[3]);
        auto assets = std::make_shared<NativeGpuAssetStore>(definition->BundlePaths, argv[2], database);
        auto scene = AssembleCharacter(definition, *assets);
        std::cout << "Assembled runtime fixture nodes=" << scene->Nodes.size()
                  << " renderers=" << scene->Renderers.size() << std::endl;
        NativeCharacterRuntime runtime(scene, assets, argv[4]);
        bool settled = argc == 8 && std::wstring_view(argv[7]) == L"--settled";
        auto settle = [&]
        {
            for (int i = 0; i < 4; ++i)
                runtime.AdvanceFrame(1.f / 30, 0);
            runtime.ResetFixturePhysics();
            for (int i = 0; i < 180; ++i)
                runtime.AdvanceFrame(1.f / 30, 0);
        };
        runtime.Update(0);
        auto initial = Snapshot(*scene);
        if (settled)
            settle();
        else
            runtime.Update(.5f);
        auto half = Snapshot(*scene);
        runtime.Update(settled ? 0 : .5f);
        if (Snapshot(*scene) != half)
            throw std::runtime_error("Repeated time altered runtime pose.");
        if (settled)
        {
            runtime.Reset();
            runtime.AdvanceFrame(0, 0);
            settle();
        }
        else
        {
            runtime.Update(1);
            runtime.Update(.5f);
        }
        auto replay = Snapshot(*scene);
        double error = 0;
        for (auto it = half.begin(); it != half.end(); ++it)
            for (size_t i = 0; i < it.value().size(); ++i)
                error = std::max(error, std::abs(it.value()[i].get<double>() - replay.at(it.key())[i].get<double>()));
        if (error > 1e-5)
            throw std::runtime_error("Rewound runtime replay differs: " + std::to_string(error));
        if (argc >= 6)
        {
            std::ofstream output(argv[5]);
            output << Json{{"Initial", initial}, {"Half", half}, {"ReplayMaxError", error}}.dump();
        }
        if (argc >= 7)
        {
            std::ifstream input(argv[6]);
            auto reference = Json::parse(input);
            size_t compared = 0;
            double maximum = 0;
            for (auto phase : {"Initial", "Half"})
            {
                auto &actual = std::string_view(phase) == "Initial" ? initial : half;
                for (auto it = actual.begin(); it != actual.end(); ++it)
                {
                    if (!reference.at(phase).contains(it.key()))
                        continue;
                    auto expected = reference.at(phase).at(it.key());
                    double dot = 0;
                    for (size_t i = 3; i < 7; ++i)
                        dot += it.value()[i].get<double>() * expected[i].get<double>();
                    if (dot < 0)
                        for (size_t i = 3; i < 7; ++i)
                            expected[i] = -expected[i].get<double>();
                    for (size_t i = 0; i < it.value().size(); ++i)
                        maximum = std::max(maximum, std::abs(it.value()[i].get<double>() - expected[i].get<double>()));
                    ++compared;
                }
            }
            if (compared < 1000 || maximum > 1e-4)
                throw std::runtime_error("Independent runtime reference mismatch: " + std::to_string(maximum));
            std::cout << "Independent runtime reference matched=" << compared << " maxError=" << maximum << "\n";
        }
        for (auto &d : runtime.Diagnostics())
            std::cout << "diagnostic: " << d << "\n";
        std::cout << "Runtime finite transforms, idempotence and replay passed; maxError=" << error << "\n";
        return 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << "\n";
        return 1;
    }
}
