#include "CreationService.h"
#include "Runtime/CharacterRuntime.h"
#include "Animation/SimulationMath.h"

namespace hs2::creation
{
    using namespace sim;

    FitPreview CreationService::PreviewFit(const CreationState &state, float time, std::string bodyPath, float value,
                                           std::stop_token token)
    {
        if (!std::isfinite(time) || time < 0 || time > 5 || !std::isfinite(value) || value < 0 || value > 1)
            throw std::invalid_argument("姿态时间或体型参数超出范围");
        auto baseline = Reference(state, token);
        auto document = *state.Character;
        if (!bodyPath.empty())
        {
            auto fields = CharacterEditSchema::Build(document);
            if (!bodyPath.starts_with("body.shapeValueBody[") || std::none_of(fields.begin(), fields.end(),
                                                                              [&](const auto &f)
                                                                              {
                                                                                  return f.Path == bodyPath;
                                                                              }))
                throw std::invalid_argument("请选择原生体型参数");
            document = document.WithValue(bodyPath, value);
        }
        CardRecord card;
        card.Path = Utf8(document.SourcePath());
        card.Sex = document.Sex();
        card.Name = state.Name;
        NativeCharacterService service(game_, data_ / "Creation/Cache", runtime_ / "classdata.tpk",
                                       Resources(&state, {}, token));
        auto prepared = service.Prepare(card, &document, {0, false}, {}, token);
        NativeCharacterRuntime runtime(prepared.Scene, prepared.Assets, runtime_ / "native/win-x64");
        runtime.Update(0);
        for (float sample = 1.f / 30; sample <= time; sample += 1.f / 30)
        {
            CheckCancelled(token);
            runtime.Update(sample);
        }
        auto reference = std::make_shared<asset::Geometry>(asset::CaptureReference(prepared, token));
        std::map<std::string, Matrix4x4> oldBones, newBones;
        for (auto &bone : baseline.Bones)
            oldBones[bone.Name] = asset::BoneWorld(bone);
        for (auto &bone : reference->Bones)
            newBones[bone.Name] = asset::BoneWorld(bone);
        auto display = std::make_shared<CreationState>(state);
        display->Character = document;
        for (auto &item : display->Assets)
        {
            // 新骨骼保留绑定姿态相对父骨的变换，随后跟随本次采样的父骨。
            std::map<std::string, Matrix4x4> posed = newBones, rest = oldBones;
            std::set<std::string> visiting;
            std::function<void(const std::string &)> resolve = [&](const std::string &name)
            {
                if (posed.contains(name))
                    return;
                if (!visiting.insert(name).second)
                    throw std::runtime_error("适配检查骨骼循环");
                auto bone = std::find_if(item.Bones.begin(), item.Bones.end(),
                                         [&](const auto &b)
                                         {
                                             return b.Name == name;
                                         });
                if (bone == item.Bones.end())
                    throw std::runtime_error("适配检查缺少骨骼：" + name);
                resolve(bone->Parent);
                rest[name] = asset::BoneWorld(*bone);
                posed[name] = MultiplyMatrix(MultiplyMatrix(rest.at(name), InverseMatrix(rest.at(bone->Parent))),
                                             posed.at(bone->Parent));
                visiting.erase(name);
            };
            if (item.Kind == "clothes")
            {
                bool missing = false;
                for (const auto &object : item.Objects)
                    for (auto &[id, vertex] : object.Mesh.Vertices)
                        missing |= vertex.Weights.empty();
                if (missing)
                    TransferWeights(item, baseline);
            }
            for (auto &object : item.Objects)
            {
                for (auto &[id, vertex] : object.Mesh.Vertices)
                {
                    CheckCancelled(token);
                    auto weights = vertex.Weights;
                    if (item.Kind == "accessory")
                        weights = {{item.Parent, 1}};
                    if (weights.empty())
                        weights = {{oldBones.contains("cf_J_Head") ? "cf_J_Head" : "cm_J_Head", 1}};
                    Vector3 position{};
                    float total = 0;
                    for (const auto &weight : weights)
                        if (weight.Weight > 0)
                        {
                            resolve(weight.Bone);
                            auto delta = MultiplyMatrix(InverseMatrix(rest.at(weight.Bone)), posed.at(weight.Bone));
                            position += TransformPoint(vertex.Position, delta) * weight.Weight;
                            total += weight.Weight;
                        }
                    if (total <= 0)
                        throw std::runtime_error("适配检查遇到零权重顶点");
                    vertex.Position = position / total;
                    // 预览使用重算法线，不改变工程中存储的法线与形变通道。
                }
                for (auto &[id, face] : object.Mesh.Faces)
                    for (auto &corner : face.Corners)
                        corner.Normal = {};
            }
        }
        return {display, reference};
    }
}
