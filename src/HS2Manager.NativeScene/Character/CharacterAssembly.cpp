#include "Character/CharacterAssembly.h"
#include "Animation/Animation.h"
#include "Animation/SimulationMath.h"
#include <algorithm>
#include <functional>
#include <set>
#include <stdexcept>

namespace hs2
{
    void ApplyCharacterReferenceHeels(NativeCharacterScene &scene)
    {
        using namespace sim;
        Require(scene.Definition != nullptr, "Character definition is missing.");
        scene.Root->LocalPosition({0, scene.Definition->Heels.Height, 0});
        for (auto &data : scene.Definition->Heels.Bones)
        {
            auto found = scene.Bones.find(data.Name);
            if (found == scene.Bones.end())
                continue;
            auto *bone = found->second;
            bone->LocalPosition(bone->LocalPosition() + data.Move);
            bone->LocalScale(data.Scale);
            if (data.Fixed)
            {
                Require(bone->Parent() != nullptr, "Heel bone has no parent.");
                bone->Rotation(bone->Parent()->Rotation());
            }
            auto pivot = bone->Position();
            for (int axis = 0; axis < 3; ++axis)
            {
                auto basis = axis == 0 ? Vector3{1, 0, 0} : axis == 1 ? Vector3{0, 1, 0} : Vector3{0, 0, 1};
                float degrees = axis == 0 ? data.Roll.x : axis == 1 ? data.Roll.y : data.Roll.z;
                auto direction = bone->TransformDirection(basis);
                bone->Position(pivot + Rotate(AngleAxisDegrees(degrees, direction), bone->Position() - pivot));
                bone->RotateWorld(direction, degrees * .0174532924f);
            }
        }
    }

    namespace
    {
        void Require(bool value, const char *error)
        {
            if (!value)
                throw std::runtime_error(error);
        }

        bool Bound(const CharacterPart &p)
        {
            return p.Role == "body" || p.Role == "head" || p.Role == "supplement" || p.Role == "clothes" ||
                   p.List.String("Weights") == "1" || p.List.String("Weights") == "2" ||
                   (p.Role == "accessory" && p.List.String("Parent") == "null");
        }

        void SetBone(NativeSceneNode &node, const BoneDefinition &b)
        {
            node.LocalPosition(b.Position);
            node.LocalRotation(b.Rotation);
            node.LocalScale(b.Scale);
        }

        const ComponentDefinition *Component(const CharacterPart &p, std::string_view name)
        {
            for (auto &c : p.Components)
                if (c.Type == name)
                    return &c;
            return nullptr;
        }

        float Coordinate(const Parameter &p, int axis)
        {
            const char *keys[]{"x", "y", "z", "w"};
            return p.Has(keys[axis])                            ? p.Float(keys[axis])
                   : axis < static_cast<int>(p.Children.size()) ? static_cast<float>(p.Children[axis].Number)
                                                                : 0;
        }

        Vector3 Vector(std::span<const float> values, size_t offset)
        {
            Require(offset <= values.size() && 3 <= values.size() - offset, "Invalid vector parameter length.");
            return {values[offset], values[offset + 1], values[offset + 2]};
        }

        Parameter ConvertComponent(const Parameter &source, NativeGpuAssetReader &reader, int depth = 0)
        {
            Require(depth <= 32, "Component parameter nesting exceeds limit.");
            // 只复制当前字段，避免每一层都先深拷贝整个子树，再被递归结果覆盖。
            Parameter result;
            result.Name = source.Name;
            result.Encoding = source.Encoding;
            result.Text = source.Text;
            result.Number = source.Number;
            result.Integer = source.Integer;
            result.Unsigned = source.Unsigned;
            result.Bytes = source.Bytes;
            if (source.Has("m_FileID") && source.Has("m_PathID"))
            {
                auto id = source.Int("m_PathID"), file = source.Int("m_FileID");
                if (!id)
                    return result;
                if (file)
                {
                    result.Text = "external:" + std::to_string(file) + ":" + std::to_string(id);
                    return result;
                }
                // 网格、材质和贴图引用只需名称；它们不是挂在 GameObject 上的组件。
                // 直接读取名称前缀，避免为一个引用展开整棵网格形变或贴图参数树。
                auto type = reader.AssetClass(id);
                if (type == 43 || type == 21 || type == 28 || type == 89 || type == 49)
                {
                    result.Text = reader.AssetName(id);
                    return result;
                }
                auto *target = &reader.Metadata(id);
                if (target->Has("m_GameObject"))
                {
                    auto object = target->Get("m_GameObject").Int("m_PathID");
                    if (object)
                        target = &reader.Metadata(object);
                }
                result.Text = target->String("m_Name");
                return result;
            }
            result.Children.reserve(source.Children.size());
            for (auto &child : source.Children)
                result.Children.push_back(ConvertComponent(child, reader, depth + 1));
            return result;
        }
    }

    std::vector<ComponentDefinition> ReadCharacterComponents(NativeGpuAssetReader &reader, const GpuPrefabData &prefab,
                                                             std::vector<std::string> &diagnostics)
    {
        std::vector<ComponentDefinition> result;
        diagnostics.insert(diagnostics.end(), prefab.Diagnostics.begin(), prefab.Diagnostics.end());
        std::unordered_map<int64_t, std::string> names;
        for (auto &node : prefab.Nodes)
            names[node.Id] = node.Name;
        for (auto &component : prefab.Components)
        {
            try
            {
                std::string type;
                if (component.ClassId == 114)
                {
                    auto &script = component.Data.Get("m_Script");
                    Require(script.Int("m_FileID") == 0, "External component script is unsupported.");
                    type = reader.Metadata(script.Int("m_PathID")).String("m_ClassName");
                }
                else if (component.ClassId == 183)
                    type = "Cloth";
                else if (component.ClassId == 137 || component.ClassId == 23)
                    type = "NativeRenderer";
                if (!type.empty())
                    result.push_back({names.at(component.NodeId), type, ConvertComponent(component.Data, reader)});
            }
            catch (const std::exception &e)
            {
                diagnostics.push_back(names.at(component.NodeId) + " component: " + e.what());
            }
        }
        return result;
    }

    std::shared_ptr<NativeCharacterScene> AssembleCharacter(std::shared_ptr<CharacterDefinition> definition,
                                                            NativeGpuAssetStore &assets, std::stop_token cancellation)
    {
        Require(definition != nullptr, "Character definition is missing.");
        auto scene = std::make_shared<NativeCharacterScene>();
        scene->Definition = definition;
        scene->Name = definition->Name;
        scene->Sex = definition->Sex;
        scene->Diagnostics = definition->Diagnostics;
        for (auto &part : definition->Parts)
        {
            if (cancellation.stop_requested())
                throw std::runtime_error("Character assembly cancelled.");
            auto preparedPrefab = assets.Prefab(part->Prefab.Bundle, part->Prefab.Asset);
            const auto &prefab = *preparedPrefab;
            std::unordered_map<int64_t, NativeSceneNode *> map;
            map.reserve(prefab.Nodes.size());
            auto runtime = std::make_shared<NativeScenePart>();
            runtime->Definition = part;
            runtime->Nodes.reserve(prefab.Nodes.size());
            runtime->ByName.reserve(prefab.Nodes.size());
            runtime->Components = prefab.Components;
            runtime->SourceNodes = prefab.Nodes;
            auto bundleUtf8 = part->Prefab.Bundle.u8string();
            std::string sourcePrefix(bundleUtf8.begin(), bundleUtf8.end());
            for (auto &n : prefab.Nodes)
            {
                auto node = std::make_unique<NativeSceneNode>(n.Name, sourcePrefix + ":" + std::to_string(n.Id));
                node->LocalPosition(n.Position);
                node->LocalRotation(n.Rotation);
                node->LocalScale(n.Scale);
                node->Active = n.Active;
                node->Layer = n.Layer;
                Require(map.emplace(n.Id, node.get()).second, "Duplicate prefab node ID.");
                runtime->Nodes.push_back(node.get());
                runtime->ByName.try_emplace(n.Name, node.get());
                scene->Nodes.push_back(std::move(node));
            }
            for (auto &n : prefab.Nodes)
                if (n.Id != prefab.RootId)
                {
                    auto parent = map.find(n.ParentId);
                    Require(parent != map.end(), "Prefab parent is missing.");
                    map.at(n.Id)->SetParent(parent->second);
                }
            runtime->Root = map.at(prefab.RootId);
            if (!part->Parent.empty() && part->Parent != "null" && !scene->Bones.contains(part->Parent))
                throw std::runtime_error("Missing attachment node: " + part->Parent);
            runtime->Root->SetParent(scene->Bones.contains(part->Parent) ? scene->Bones.at(part->Parent)
                                                                         : scene->Root.get());
            for (auto &bone : part->Bones)
                if (auto found = runtime->ByName.find(bone.Name); found != runtime->ByName.end())
                    SetBone(*found->second, bone);
            if (part->Role == "rig" || part->Role == "headRig")
                for (auto &[name, node] : runtime->ByName)
                    scene->Bones.try_emplace(name, node);
            bool bind = Bound(*part);
            std::set<std::string> skinnedAccessories, additionalRoots;
            for (auto &c : part->Components)
            {
                if (c.Type == "SkinnedAccessory" && c.Data.Float("m_Enabled", 1) != 0)
                    for (auto &p : c.Data.Get("meshRenderers").Children)
                        skinnedAccessories.insert(p.Text);
                if (c.Type == "BonesFramework" && bind)
                    for (auto &p : c.Data.Children)
                        if (!p.Text.empty())
                            additionalRoots.insert(p.Text);
            }
            for (auto *branch : runtime->Nodes)
                if (additionalRoots.contains(branch->Name))
                {
                    bool nested = false;
                    for (auto *p = branch->Parent(); p && p != runtime->Root; p = p->Parent())
                        if (additionalRoots.contains(p->Name))
                        {
                            nested = true;
                            break;
                        }
                    if (nested)
                        continue;
                    auto *p = branch->Parent();
                    auto destination = p ? scene->Bones.find(p->Name) : scene->Bones.end();
                    if (!p || destination == scene->Bones.end() || destination->second == branch)
                    {
                        scene->Diagnostics.push_back(
                            "Missing authored additional-bone attachment: " + part->Prefab.Asset + "/" + branch->Name);
                        continue;
                    }
                    branch->SetParent(destination->second);
                    if (part->Role == "supplement")
                        for (auto *node : branch->Traverse())
                            scene->Bones[node->Name] = node;
                }
            for (auto &c : part->Components)
                if (c.Type == "BoneImplantProcess")
                {
                    auto branch = runtime->ByName.find(c.Data.String("trfSrc")),
                         destination = scene->Bones.find(c.Data.String("trfDst"));
                    if (branch == runtime->ByName.end() || destination == scene->Bones.end() ||
                        branch->second == destination->second)
                    {
                        scene->Diagnostics.push_back(
                            "Missing authored implanted-bone attachment: " + part->Prefab.Asset + "/" + c.Node);
                        continue;
                    }
                    branch->second->SetParent(destination->second);
                }
            auto bone = [&](const GpuAssetPointer &p, bool shared) -> NativeSceneNode *
            {
                Require(p.PathId != 0 && p.FileId == 0 && map.contains(p.PathId),
                        "External, null, or missing skin bone.");
                auto *node = map.at(p.PathId);
                auto common = scene->Bones.find(node->Name);
                return shared && common != scene->Bones.end() ? common->second : node;
            };
            auto bones = [&](const GpuRendererData &r, const GpuMeshData &mesh, bool shared)
            {
                std::vector<NativeSceneNode *> result(r.Bones.size());
                if (!result.empty() && std::all_of(r.Bones.begin(), r.Bones.end(),
                                                   [](auto &p)
                                                   {
                                                       return p.PathId == 0;
                                                   }))
                {
                    scene->Diagnostics.push_back("Source skinner has no bone transforms: " + part->Prefab.Asset);
                    return result;
                }
                for (size_t i = 0; i < r.Bones.size(); ++i)
                {
                    if (r.Bones[i].PathId)
                    {
                        result[i] = bone(r.Bones[i], shared);
                        continue;
                    }
                    for (int v = 0; v < mesh.VertexCount; ++v)
                        for (int influence = 0; influence < 4; ++influence)
                            Require(mesh.Read(12, v, influence) <= 0 ||
                                        static_cast<int>(mesh.Read(13, v, influence)) != i,
                                    "Skinned renderer has a weighted null bone.");
                }
                return result;
            };
            for (auto &source : prefab.Renderers)
            {
                if (cancellation.stop_requested())
                    throw std::runtime_error("Character assembly cancelled.");
                auto *node = map.at(source.NodeId);
                const auto &name = node->Name;
                if (part->Role == "rig" || part->Role == "headRig")
                    continue;
                if (part->Role == "supplement")
                {
                    if (part->Slot == 0 && source.Skinned && name.starts_with("o_body_"))
                        for (auto &existing : scene->Renderers)
                            if (existing->Role == "body" && existing->Node->Name == name)
                            {
                                existing->Mesh = assets.Mesh(source.Mesh);
                                existing->Bones = bones(source, *existing->Mesh, true);
                                existing->BlendWeights.assign(existing->Mesh->BlendShapes.size(), 0);
                                std::copy_n(source.BlendWeights.begin(),
                                            std::min(source.BlendWeights.size(), existing->BlendWeights.size()),
                                            existing->BlendWeights.begin());
                                break;
                            }
                    continue;
                }
                std::string lower = name;
                std::transform(lower.begin(), lower.end(), lower.begin(),
                               [](unsigned char c)
                               {
                                   return char(std::tolower(c));
                               });
                if ((part->Role == "body" && !name.starts_with("o_body_")) || lower.starts_with("o_hit_") ||
                    name.find("silhouette") != name.npos)
                    continue;
                bool shared = part->Role != "accessory" || bind || skinnedAccessories.contains(name);
                auto r = std::make_shared<NativeSceneRenderer>();
                r->Node = node;
                r->Part = part;
                r->Role = part->Role;
                r->Slot = part->Slot;
                r->Source = source;
                r->Mesh = assets.Mesh(source.Mesh);
                for (auto &p : source.Materials)
                    r->Materials.push_back(p.PathId ? std::make_shared<GpuMaterialData>(*assets.Material(p)) : nullptr);
                if (part->Role == "body" || (part->Role == "head" && name == "o_head"))
                {
                    std::string key =
                        part->Role == "body"
                            ? "template." + std::string(definition->Sex == 0 ? "cm" : "cf") + "_m_skin_body_00"
                            : "head.MatData";
                    auto selection = std::find_if(definition->Selections.begin(), definition->Selections.end(),
                                                  [&](auto &s)
                                                  {
                                                      return s.Key == key;
                                                  });
                    Require(selection != definition->Selections.end() && !r->Materials.empty(),
                            "Skin material selection is missing.");
                    r->Materials[0] = std::make_shared<GpuMaterialData>(*assets.Material(
                        assets.Reader(selection->Reference.Bundle)->Find(selection->Reference.Asset, 21)));
                }
                r->Bones = source.Skinned ? bones(source, *r->Mesh, shared) : std::vector<NativeSceneNode *>{};
                r->BlendWeights.assign(r->Mesh->BlendShapes.size(), 0);
                std::copy_n(source.BlendWeights.begin(), std::min(source.BlendWeights.size(), r->BlendWeights.size()),
                            r->BlendWeights.begin());
                r->RootBone = source.Skinned && source.RootBone.PathId ? bone(source.RootBone, shared) : nullptr;
                if (source.Skinned && bind && part->Role != "head" && scene->Bones.contains("cf_J_Hips") &&
                    std::none_of(prefab.Components.begin(), prefab.Components.end(),
                                 [&](auto &c)
                                 {
                                     return c.NodeId == source.NodeId && c.ClassId == 183;
                                 }))
                    r->RootBone = scene->Bones.at("cf_J_Hips");
                if (source.Skinned && skinnedAccessories.contains(name))
                {
                    auto rig = std::find_if(scene->Parts.begin(), scene->Parts.end(),
                                            [](auto &p)
                                            {
                                                return p->Definition->Role == "rig";
                                            });
                    Require(rig != scene->Parts.end(), "Accessory root rig is missing.");
                    r->RootBone = (*rig)->Root;
                }
                if (source.Skinned && skinnedAccessories.contains(name))
                    r->FixedLocalBounds = NativeRendererBounds{{0, 10, 0}, {10, 10, 10}};
                else if (source.Skinned && bind)
                    r->FixedLocalBounds = NativeRendererBounds{{0, -2, 0}, {10, 10, 10}};
                r->Enabled = source.Enabled;
                r->ShadowCasting = static_cast<int>(source.Settings.Int("m_CastShadows", 1));
                r->ReceiveShadows = source.Settings.Int("m_ReceiveShadows", 1) != 0;
                scene->Renderers.push_back(r);
            }
            for (auto &c : part->Components)
            {
                for (std::string key : {"objTopDef", "objBotDef", "objTopHalf", "objBotHalf"})
                    for (auto *n : runtime->Nodes)
                        if (n->Name == c.Data.String(key))
                            n->Active = key.ends_with("Def");
                for (size_t i = 0; i < part->HideOptions.size(); ++i)
                    for (auto &p : c.Data.Get("objOpt0" + std::to_string(i + 1)).Children)
                        for (auto *n : runtime->Nodes)
                            if (n->Name == p.Text)
                                n->Active = !part->HideOptions[i];
            }
            if (part->Role == "accessory")
            {
                if (auto *c = Component(*part, "CmpAccessory"); c && part->Adjustments.size() == 18)
                    for (int i = 0; i < 2; ++i)
                        if (auto found = runtime->ByName.find(c->Data.String("trfMove0" + std::to_string(i + 1)));
                            found != runtime->ByName.end())
                        {
                            auto position = Vector(part->Adjustments, i * 9);
                            found->second->LocalPosition({position.x * .1f, position.y * .1f, position.z * .1f});
                            found->second->LocalRotation(EulerRotation(Vector(part->Adjustments, i * 9 + 3)));
                            found->second->LocalScale(Vector(part->Adjustments, i * 9 + 6));
                        }
                if (part->Parameters.Has("show") && part->Parameters.Float("show") == 0)
                    runtime->Root->Active = false;
            }
            if (part->Role == "hair")
                if (auto *c = Component(*part, "CmpHair"))
                {
                    auto &hair = definition->Hair.Get("parts").Children;
                    if (part->Slot >= 0 && static_cast<size_t>(part->Slot) < hair.size())
                    {
                        auto &parameters = hair[part->Slot].Get("dictBundle");
                        auto &configs = c->Data.Get("boneInfo").Children;
                        for (size_t i = 0; i < configs.size(); ++i)
                        {
                            auto key = std::to_string(i);
                            if (!parameters.Has(key))
                                continue;
                            auto &p = parameters.Get(key);
                            auto &config = configs[i];
                            auto found = runtime->ByName.find(config.String("trfCorrect"));
                            if (found == runtime->ByName.end())
                                continue;
                            auto interpolate = [&](const char *lo, const char *hi, const char *key)
                            {
                                Vector3 out;
                                float *xyz = &out.x;
                                for (int axis = 0; axis < 3; ++axis)
                                {
                                    float t = Coordinate(p.Get(key), axis);
                                    if (definition->Hair.Float("unlockedSliders") == 0)
                                        t = std::clamp(t, 0.f, 1.f);
                                    float a = Coordinate(config.Get(lo), axis), b = Coordinate(config.Get(hi), axis);
                                    xyz[axis] = a + (b - a) * t;
                                }
                                return out;
                            };
                            found->second->LocalPosition(interpolate("posMin", "posMax", "moveRate"));
                            found->second->LocalRotation(EulerRotation(interpolate("rotMin", "rotMax", "rotRate")));
                        }
                    }
                }
            scene->Parts.push_back(runtime);
        }
        // ABMX 绑定后修改第一个同名活动节点；
        // 排除蒙皮部件下的骨架副本，避免同名节点重复应用编辑。
        std::set<NativeSceneNode *> excluded;
        std::unordered_map<NativeSceneNode *, std::string> aliases;
        for (auto &p : scene->Parts)
        {
            if (!p->Definition->RuntimeName.empty())
                aliases[p->Root] = p->Definition->RuntimeName;
            bool skinned = Component(*p->Definition, "SkinnedAccessory") != nullptr;
            if (!Bound(*p->Definition) && !skinned)
                continue;
            for (auto *node : p->Nodes)
                for (auto *a = node; a && a != scene->Root.get(); a = a->Parent())
                    if (scene->Bones.contains(a->Name) && scene->Bones.at(a->Name) != a)
                    {
                        excluded.insert(node);
                        break;
                    }
        }
        std::unordered_map<std::string, NativeSceneNode *> first;
        for (auto *node : scene->Root->Traverse())
            if (!excluded.contains(node))
                first.try_emplace(aliases.contains(node) ? aliases[node] : node->Name, node);
        for (auto &p : scene->Parts)
            for (auto &b : p->Definition->UnmodifiedBones)
                if (auto found = p->ByName.find(b.Name); found != p->ByName.end())
                {
                    auto *node = found->second;
                    auto name = aliases.contains(node) ? aliases[node] : b.Name;
                    if (!first.contains(name) || first.at(name) != node)
                        SetBone(*node, b);
                    else
                        scene->ScopedBaselines[node] = b;
                }
        for (auto &adjustment : definition->BoneModifiers)
            if (auto found = first.find(adjustment.Name);
                found != first.end() && scene->ScopedBaselines.contains(found->second))
                scene->ScopedAdjustments[found->second] = adjustment;
        PoseCharacterFace(*scene, 1);
        ApplyCharacterVisibility(*scene);
        return scene;
    }

    void PoseCharacterFace(NativeCharacterScene &scene, float eyes, bool dynamic, float horizontal, float vertical,
                           bool gaze)
    {
        Require(scene.Definition != nullptr, "Character definition is missing.");
        for (auto &p : scene.Parts)
            if (p->Definition->Role == "head")
                if (auto *c = Component(*p->Definition, "FaceBlendShape"))
                    for (std::string controller : {"EyesCtrl", "EyebrowCtrl", "MouthCtrl"})
                    {
                        float maximum = scene.Definition->EyeOpenMax;
                        if (dynamic && gaze && controller == "EyesCtrl" && !c->Data.String("EyeLookController").empty())
                        {
                            float up = c->Data.Float("EyeLookUpCorrect", .1f),
                                  down = c->Data.Float("EyeLookDownCorrect", .3f),
                                  side = c->Data.Float("EyeLookSideCorrect", .1f);
                            float correction =
                                (vertical > 0 ? up * std::sqrt(vertical) : -down * std::sqrt(-vertical)) -
                                side * std::sqrt(std::abs(horizontal));
                            maximum =
                                std::min(1 - up, maximum) + std::clamp(correction, -std::max(down, side), up) * maximum;
                        }
                        float amount = controller == "EyesCtrl"      ? std::clamp(eyes * maximum, 0.f, 1.f)
                                       : controller == "EyebrowCtrl" ? (dynamic ? eyes : 1.f)
                                                                     : 0.f;
                        int percent = static_cast<int>(amount * 100);
                        for (auto &target : c->Data.Get(controller).Get("FBSTarget").Children)
                        {
                            auto r = std::find_if(scene.Renderers.begin(), scene.Renderers.end(),
                                                  [&](auto &r)
                                                  {
                                                      return r->Part == p->Definition && r->Source.Skinned &&
                                                             r->Node->Name == target.String("ObjTarget");
                                                  });
                            if (r == scene.Renderers.end())
                                continue;
                            auto &patterns = target.Get("PtnSet").Children;
                            if (patterns.empty())
                                continue;
                            int close = static_cast<int>(patterns[0].Int("Close", -1)),
                                open = static_cast<int>(patterns[0].Int("Open", -1));
                            auto &weights = (*r)->BlendWeights;
                            if (close >= 0 && static_cast<size_t>(close) < weights.size())
                                weights[close] = float(100 - percent);
                            if (open >= 0 && static_cast<size_t>(open) < weights.size())
                                weights[open] = float(open == close ? 100 : percent);
                        }
                    }
    }

    void ApplyCharacterVisibility(NativeCharacterScene &scene)
    {
        Require(scene.Definition != nullptr, "Character definition is missing.");
        for (auto &p : scene.Parts)
        {
            auto &role = p->Definition->Role;
            if (role == "body" && !scene.Definition->BodyVisible)
                for (auto &r : scene.Renderers)
                    if (r->Part == p->Definition)
                        r->Enabled = false;
            if (role != "clothes" && role != "accessory")
                continue;
            bool visible = role == "clothes" ? scene.Definition->Visibility.ClothesVisible(p->Definition->Slot)
                                             : scene.Definition->Visibility.Accessories &&
                                                   (!p->Definition->Parameters.Has("show") ||
                                                    p->Definition->Parameters.Float("show") != 0);
            p->Root->Active = visible;
            for (auto &r : scene.Renderers)
                if (r->Part == p->Definition)
                    r->Enabled = visible && r->Source.Enabled;
        }
    }
}
