#include "Runtime/CharacterRuntime.h"
#include "Animation/Animation.h"
#include "Runtime/ClothRuntime.h"
#include "Runtime/NativeDynamics.h"
#include "Animation/SimulationMath.h"
#include "Animation/PoseControllers.h"
#include <limits>
#include <tuple>

namespace hs2
{
    using namespace sim;

    namespace
    {
        struct LocalPose
        {
            Vector3 Position, Scale;
            Quaternion Rotation;
        };

        LocalPose GetPose(NativeSceneNode &node)
        {
            return {node.LocalPosition(), node.LocalScale(), node.LocalRotation()};
        }

        void SetPose(NativeSceneNode &node, const LocalPose &p)
        {
            node.LocalPosition(p.Position);
            node.LocalScale(p.Scale);
            node.LocalRotation(p.Rotation);
        }

        void SetBone(NativeSceneNode &node, const BoneDefinition &b)
        {
            SetPose(node, {b.Position, b.Scale, b.Rotation});
        }

        GpuAssetPointer Pointer(const std::filesystem::path &bundle, const Parameter &value)
        {
            return {bundle, static_cast<int>(value.Int("m_FileID")), value.Int("m_PathID")};
        }
    }

    struct NativeCharacterRuntime::Impl
    {
        std::shared_ptr<NativeCharacterScene> Scene;
        std::shared_ptr<NativeGpuAssetStore> Assets;
        std::filesystem::path PhysicsDirectory;
        std::unordered_map<NativeSceneNode *, LocalPose> Initial;

        struct SourcePose
        {
            std::vector<std::unique_ptr<NativeSceneNode>> Nodes;
            std::unordered_map<std::string, NativeSceneNode *> ByName;
        };

        std::unordered_map<const CharacterPart *, SourcePose> Sources;
        std::unique_ptr<NativeIdleLimbs> IdleLimbs;
        std::unique_ptr<NativeEyeLook> EyeLook;
        std::unordered_map<NativeSceneNode *, Vector3> ShapedScales;

        struct PartAnimation
        {
            std::unique_ptr<NativeAnimationPlayer> Player;
            float Speed = 1;
            bool Loop = false;
        };

        std::vector<PartAnimation> PartAnimations;
        std::unique_ptr<NativeAnimationPlayer> Idle;
        std::unique_ptr<NativeClothRuntime> Cloth;
        std::vector<std::unique_ptr<NativeBoneChain>> Chains;
        std::vector<NativeBoneChain *> BoundSetup, AccessorySetup;
        std::vector<std::unique_ptr<NativeBodyDynamics>> BodyDynamics;
        std::vector<NativeSceneNode *> ForwardNeck, SupportNodes;
        std::vector<std::string> Diagnostics;
        IndependentBlink Blink{1979};
        bool Initialized = false, PhysicsInitialized = false;
        bool IdlePose = true;
        int Frame = -1;
        float Time = -1;

        Impl(std::shared_ptr<NativeCharacterScene> scene, std::shared_ptr<NativeGpuAssetStore> assets,
             std::filesystem::path directory)
            : Scene(std::move(scene)), Assets(std::move(assets)), PhysicsDirectory(std::move(directory))
        {
            Require(Scene && Scene->Definition && Assets, "Character runtime inputs are missing.");
            for (auto *node : Scene->Root->Traverse())
                Initial[node] = GetPose(*node);
            ReadSources();
            ReadAnimations();
            Cloth = std::make_unique<NativeClothRuntime>(*Scene, PhysicsDirectory);
            InitializeHeels();
        }

        void ReadSources()
        {
            for (auto &part : Scene->Parts)
            {
                auto &pose = Sources[part->Definition.get()];
                std::unordered_map<int64_t, NativeSceneNode *> ids;
                for (auto &source : part->SourceNodes)
                {
                    auto node = std::make_unique<NativeSceneNode>(source.Name, "rest:" + std::to_string(source.Id));
                    node->LocalPosition(source.Position);
                    node->LocalRotation(source.Rotation);
                    node->LocalScale(source.Scale);
                    node->Active = source.Active;
                    ids[source.Id] = node.get();
                    pose.ByName.try_emplace(source.Name, node.get());
                    pose.Nodes.push_back(std::move(node));
                }
                for (auto &source : part->SourceNodes)
                    if (ids.contains(source.ParentId))
                        ids.at(source.Id)->SetParent(ids.at(source.ParentId));
            }
        }

        void ReadAnimations()
        {
            auto &definition = *Scene->Definition;
            auto motion = std::find_if(definition.Selections.begin(), definition.Selections.end(),
                                       [](auto &s)
                                       {
                                           return s.Key == "motion.controller";
                                       });
            if (motion != definition.Selections.end())
            {
                Idle = MotionPlayer({motion->Reference.Bundle, definition.MotionState});
            }
            for (auto &part : Scene->Parts)
                if (part->Definition->Role != "rig" && part->Definition->Role != "headRig")
                    for (auto &component : part->Components)
                        if ((component.ClassId == 95 || component.ClassId == 111) &&
                            component.Data.Float("m_Enabled", 1) != 0)
                        {
                            auto pointer =
                                Pointer(part->Definition->Prefab.Bundle,
                                        component.Data.Get(component.ClassId == 95 ? "m_Controller" : "m_Animation"));
                            if (!pointer.PathId)
                                continue;
                            pointer = Assets->Resolve(pointer);
                            if (component.ClassId == 95)
                            {
                                auto controller = Assets->Metadata(pointer);
                                auto &clips = controller.Get("m_AnimationClips").Children;
                                Require(!clips.empty(), "Animator controller requires its override/state evaluator.");
                                pointer = Assets->Resolve(Pointer(pointer.Bundle, clips.front()));
                            }
                            auto data = Assets->Metadata(pointer);
                            auto clip = std::make_shared<NativeAnimationClip>(data);
                            auto source = std::find_if(part->SourceNodes.begin(), part->SourceNodes.end(),
                                                       [&](auto &n)
                                                       {
                                                           return n.Id == component.NodeId;
                                                       });
                            Require(source != part->SourceNodes.end() && part->ByName.contains(source->Name),
                                    "Part animation target is missing.");
                            std::vector<NativeSceneRenderer *> renderers;
                            for (auto &r : Scene->Renderers)
                                if (r->Part == part->Definition)
                                    renderers.push_back(r.get());
                            PartAnimation entry;
                            entry.Player = std::make_unique<NativeAnimationPlayer>(clip, *part->ByName.at(source->Name),
                                                                                   part->Nodes, renderers);
                            entry.Speed = component.Data.Float("m_Speed", 1);
                            entry.Loop =
                                data.Get("m_MuscleClip").Float("m_LoopTime") != 0 || data.Int("m_WrapMode") == 2;
                            PartAnimations.push_back(std::move(entry));
                        }
        }

        void InitializeHeels()
        {
            Scene->Root->LocalPosition({0, Scene->Definition->Heels.Height, 0});
            ApplyHeels();
        }

        std::unique_ptr<NativeAnimationPlayer> MotionPlayer(const AssetReference &reference)
        {
            if (reference.Asset.empty())
                return {};
            auto reader = Assets->Reader(reference.Bundle);
            auto clip =
                std::make_shared<NativeAnimationClip>(reader->Metadata(reader->Find(reference.Asset, 74).PathId));
            auto rig = std::find_if(Scene->Parts.begin(), Scene->Parts.end(),
                                    [](auto &part)
                                    {
                                        return part->Definition->Role == "rig";
                                    });
            Require(rig != Scene->Parts.end(), "Character rig is missing.");
            return std::make_unique<NativeAnimationPlayer>(clip, *(*rig)->Root, (*rig)->Nodes);
        }

        void ApplyHeels()
        {
            for (auto &data : Scene->Definition->Heels.Bones)
            {
                auto found = Scene->Bones.find(data.Name);
                if (found == Scene->Bones.end())
                    continue;
                auto *bone = found->second;
                bone->LocalPosition(bone->LocalPosition() + data.Move);
                bone->LocalScale(data.Scale);
                if (data.Fixed && bone->Parent())
                    bone->Rotation(bone->Parent()->Rotation());
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

        void Scoped(bool apply)
        {
            for (auto &[target, edit] : Scene->ScopedAdjustments)
            {
                auto &baseline = Scene->ScopedBaselines.at(target);
                if (!apply)
                {
                    if (edit.Length != 1 || LengthSquared(edit.Position) != 0)
                        target->LocalPosition(baseline.Position);
                    if (LengthSquared(edit.Rotation) != 0)
                        target->LocalRotation(baseline.Rotation);
                    continue;
                }
                if (edit.Scale.x != 1 || edit.Scale.y != 1 || edit.Scale.z != 1)
                    target->LocalScale(Product(baseline.Scale, edit.Scale));
                if (LengthSquared(edit.Rotation) != 0)
                    target->LocalRotation(Mul(baseline.Rotation, EulerRotation(edit.Rotation)));
                if (edit.Length != 1 && Length(baseline.Position) > 0)
                {
                    auto direction = target->LocalPosition();
                    if (edit.Length < .1f || LengthSquared(direction) == 0)
                        direction = baseline.Position;
                    target->LocalPosition(Unit(direction) * Length(baseline.Position) * edit.Length + edit.Position);
                }
                else if (LengthSquared(edit.Position) != 0)
                    target->LocalPosition(baseline.Position + edit.Position);
            }
        }

        void Capture(std::unordered_map<NativeSceneNode *, Matrix4x4> &matrices,
                     std::unordered_map<NativeSceneNode *, Quaternion> &rotations,
                     std::unordered_map<NativeSceneNode *, LocalPose> &locals)
        {
            std::unordered_map<NativeSceneNode *, LocalPose> saved;
            for (auto *node : Scene->Root->Traverse())
                saved[node] = GetPose(*node);
            for (auto &part : Scene->Parts)
            {
                for (auto &bone : part->Definition->UnmodifiedBones)
                    if (part->ByName.contains(bone.Name))
                        SetBone(*part->ByName.at(bone.Name), bone);
                auto root = std::find_if(part->SourceNodes.begin(), part->SourceNodes.end(),
                                         [](auto &n)
                                         {
                                             return !n.ParentId;
                                         });
                if (root == part->SourceNodes.end())
                    root = part->SourceNodes.begin();
                if (root != part->SourceNodes.end())
                    SetPose(*part->Root, {root->Position, root->Scale, root->Rotation});
            }
            for (auto *node : Scene->Root->Traverse())
            {
                matrices[node] = node->WorldMatrix();
                rotations[node] = node->Rotation();
                locals[node] = GetPose(*node);
            }
            for (auto &[node, pose] : saved)
                SetPose(*node, pose);
        }

        void AddSupport(std::vector<NativeDynamicCollider> &colliders, const std::string &name, float radius,
                        float height, Vector3 center)
        {
            if (!Scene->Bones.contains(name))
                return;
            auto *parent = Scene->Bones.at(name);
            auto all = Scene->Root->Traverse();
            auto first = std::find_if(all.begin(), all.end(),
                                      [&](auto *n)
                                      {
                                          return n->Name == name;
                                      });
            if (first == all.end() || *first != parent)
                return;
            NativeSceneNode *node = nullptr;
            for (auto *existing : SupportNodes)
                if (existing->Name == "HS2_Colliders_" + name)
                    node = existing;
            if (!node)
            {
                auto added = std::make_unique<NativeSceneNode>("HS2_Colliders_" + name, "support:" + name);
                node = added.get();
                node->SetParent(parent);
                SupportNodes.push_back(node);
                Scene->Nodes.push_back(std::move(added));
            }
            colliders.push_back({node, center, radius, height, 0, 0});
        }

        void InitializePhysics()
        {
            std::unordered_map<NativeSceneNode *, Matrix4x4> matrices;
            std::unordered_map<NativeSceneNode *, Quaternion> rotations;
            std::unordered_map<NativeSceneNode *, LocalPose> locals;
            Capture(matrices, rotations, locals);
            std::vector<NativeDynamicCollider> body, original;
            for (auto &part : Scene->Parts)
            {
                auto &def = *part->Definition;
                auto &nodes = part->ByName;
                if (def.Role == "rig")
                    for (auto &bone : def.Bones)
                    {
                        auto source = std::find_if(part->SourceNodes.begin(), part->SourceNodes.end(),
                                                   [&](auto &n)
                                                   {
                                                       return n.Name == bone.Name;
                                                   });
                        auto scale = nodes.contains(bone.Name) ? nodes.at(bone.Name)->LocalScale() : bone.Scale;
                        if (source != part->SourceNodes.end() && Scene->Bones.contains(bone.Name) &&
                            LengthSquared(scale - source->Scale) > 1e-12f)
                            ShapedScales[Scene->Bones.at(bone.Name)] = scale;
                    }
                for (auto &c : def.Components)
                {
                    if (IdlePose && c.Type == "FullBodyBipedIK" && def.Role == "rig" &&
                        c.Data.Float("m_Enabled", 1) != 0)
                        IdleLimbs = std::make_unique<NativeIdleLimbs>(c.Data, Scene->Bones, Sources.at(&def).ByName,
                                                                      Diagnostics);
                    if (c.Type == "EyeLookCalc")
                        EyeLook = std::make_unique<NativeEyeLook>(c.Data, nodes, Sources.at(&def).ByName);
                    if (IdlePose && c.Type == "NeckLookCalcVer2")
                        for (auto &item : c.Data.Get("aBones").Children)
                            if (nodes.contains(item.String("neckBone")))
                                ForwardNeck.push_back(nodes.at(item.String("neckBone")));
                    if (c.Type != "DynamicBoneCollider" || c.Data.Float("m_Enabled", 1) == 0 || !nodes.contains(c.Node))
                        continue;
                    auto *node = nodes.at(c.Node);
                    if (def.Role == "supplement" && Descendant(node, part->Root))
                        continue;
                    NativeDynamicCollider collider{node,
                                                   Vector(c.Data.Get("m_Center")),
                                                   c.Data.Float("m_Radius"),
                                                   c.Data.Float("m_Height"),
                                                   static_cast<int>(c.Data.Int("m_Direction")),
                                                   static_cast<int>(c.Data.Int("m_Bound"))};
                    body.push_back(collider);
                    if (def.Role != "supplement")
                        original.push_back(collider);
                }
            }
            std::vector<NativeDynamicCollider> extra;
            if (Scene->Definition->ColliderSupport)
            {
                AddSupport(extra, "cf_J_Root", 100, 0, {0, -100.01f, 0});
                for (std::string side : {"L", "R"})
                {
                    AddSupport(extra, "cf_J_Hand_s_" + side, .2f, .75f, {side == "L" ? -.3f : .3f, -.05f, 0});
                    AddSupport(extra, "cf_J_ArmLow02_s_" + side, .25f, 2.5f, {});
                    AddSupport(extra, "cf_J_ArmUp02_s_" + side, .25f, 2.5f, {});
                }
            }
            auto all = body;
            all.insert(all.end(), extra.begin(), extra.end());
            std::unordered_map<NativeSceneNode *, size_t> order;
            auto hierarchy = Scene->Root->Traverse();
            for (size_t i = 0; i < hierarchy.size(); ++i)
                order[hierarchy[i]] = i;
            auto sort = [&](auto &items)
            {
                std::sort(items.begin(), items.end(),
                          [&](auto &a, auto &b)
                          {
                              return order.at(a.Node) < order.at(b.Node);
                          });
            };
            auto clothing = all;
            sort(clothing);
            sort(original);
            for (auto &part : Scene->Parts)
            {
                auto &def = *part->Definition;
                auto &nodes = part->ByName;
                std::vector<NativeSceneNode *> roots, initializedRoots;
                for (auto &c : def.Components)
                    if (c.Type == "DynamicBone" && c.Data.Float("m_Enabled", 1) != 0 &&
                        nodes.contains(c.Data.String("m_Root")))
                        roots.push_back(nodes.at(c.Data.String("m_Root")));
                bool overlapping =
                    def.Role == "hair" && std::any_of(roots.begin(), roots.end(),
                                                      [&](auto *t)
                                                      {
                                                          return std::any_of(roots.begin(), roots.end(),
                                                                             [&](auto *p)
                                                                             {
                                                                                 return p != t && Descendant(t, p);
                                                                             });
                                                      });
                std::unordered_map<NativeSceneNode *, LocalPose> startup;
                if (overlapping)
                    for (auto *n : Scene->Root->Traverse())
                    {
                        startup[n] = GetPose(*n);
                        if (locals.contains(n))
                            SetPose(*n, locals.at(n));
                    }
                if (Scene->Sex != 0 && def.Role != "supplement")
                    for (auto &c : def.Components)
                        if (c.Type == "DynamicBone_Ver02" && c.Data.Float("m_Enabled", 1) != 0)
                            BodyDynamics.push_back(std::make_unique<NativeBodyDynamics>(
                                c.Data, Scene->Definition->Body, nodes, all, Scene->Definition->ColliderSupport));
                for (auto &c : def.Components)
                    if (c.Type == "DynamicBone" && c.Data.Float("m_Enabled", 1) != 0)
                    {
                        auto rootName = c.Data.String("m_Root");
                        if (rootName.empty())
                            continue;
                        if (!nodes.contains(rootName) || !nodes.contains(c.Node))
                        {
                            Diagnostics.push_back("动态骨骼缺少节点：" + c.Node);
                            continue;
                        }
                        auto *root = nodes.at(rootName);
                        auto *host = nodes.at(c.Node);
                        if (def.Role == "supplement" && Descendant(host, part->Root))
                            continue;
                        if (def.Role == "accessory" && def.Parameters.Float("noShake") != 0)
                            continue;
                        bool disabled = false;
                        if (def.Role == "hair")
                            for (auto &hair : def.Components)
                                if (hair.Type == "CmpHair")
                                {
                                    auto &groups = hair.Data.Get("boneInfo").Children;
                                    for (size_t i = 0; i < groups.size(); ++i)
                                        if (nodes.contains(groups[i].String("trfCorrect")) &&
                                            Descendant(root, nodes.at(groups[i].String("trfCorrect"))) &&
                                            At(Scene->Definition->Hair.Get("parts"), def.Slot)
                                                    .Get("dictBundle")
                                                    .Get(std::to_string(i))
                                                    .Float("noShake") != 0)
                                            disabled = true;
                                }
                        if (disabled)
                            continue;
                        if (def.Role == "clothes" && def.Slot < 2 && Scene->Bones.contains(root->Name))
                            root = Scene->Bones.at(root->Name);
                        std::optional<std::vector<NativeSceneRenderer *>> visibility;
                        auto owner = std::find_if(def.Components.begin(), def.Components.end(),
                                                  [](auto &v)
                                                  {
                                                      return v.Type == "CmpClothes" || v.Type == "CmpAccessory" ||
                                                             v.Type == "CmpHair";
                                                  });
                        bool controls = owner != def.Components.end() && owner->Type != "CmpHair";
                        if (owner != def.Components.end() && owner->Type == "CmpHair")
                            for (auto &group : owner->Data.Get("boneInfo").Children)
                                for (auto &v : group.Get("dynamicBone").Children)
                                    if (v.Text == c.Node)
                                        controls = true;
                        if (controls && def.Role != "supplement")
                        {
                            visibility.emplace();
                            std::set<std::string> names;
                            for (auto &v : owner->Data.Get("rendCheckVisible").Children)
                                names.insert(v.Text);
                            for (auto &r : Scene->Renderers)
                                if (r->Part == part->Definition && names.contains(r->Node->Name))
                                    visibility->push_back(r.get());
                        }
                        auto colliders = def.Role == "clothes" ? clothing : original;
                        if (def.Role == "supplement")
                        {
                            colliders.clear();
                            for (auto &v : c.Data.Get("m_Colliders").Children)
                            {
                                auto found = std::find_if(all.begin(), all.end(),
                                                          [&](auto &x)
                                                          {
                                                              return x.Node->Name == v.Text;
                                                          });
                                if (found != all.end())
                                    colliders.push_back(*found);
                            }
                        }
                        bool follows = overlapping && std::any_of(initializedRoots.begin(), initializedRoots.end(),
                                                                  [&](auto *t)
                                                                  {
                                                                      return root != t && Descendant(root, t);
                                                                  });
                        auto chain = std::make_unique<NativeBoneChain>(*host, *root, c.Data, colliders, visibility,
                                                                       follows ? nullptr : &matrices,
                                                                       follows ? nullptr : &rotations);
                        initializedRoots.push_back(root);
                        if (def.Role == "clothes" && std::any_of(def.Components.begin(), def.Components.end(),
                                                                 [](auto &v)
                                                                 {
                                                                     return v.Type == "BonesFramework";
                                                                 }))
                            BoundSetup.push_back(chain.get());
                        if (def.Role == "accessory")
                            AccessorySetup.push_back(chain.get());
                        if (overlapping && std::any_of(roots.begin(), roots.end(),
                                                       [&](auto *t)
                                                       {
                                                           return t != root && Descendant(t, root);
                                                       }))
                            chain->Step();
                        Chains.push_back(std::move(chain));
                    }
                for (auto &[n, pose] : startup)
                    SetPose(*n, pose);
            }
        }

        void Step(float delta, float time)
        {
            if (!Initialized)
            {
                InitializePhysics();
                Initialized = true;
            }
            for (auto &chain : Chains)
                chain->Restore();
            for (auto &chain : BodyDynamics)
                chain->Restore();
            std::unordered_map<NativeSceneNode *, Vector3> heelPositions;
            for (auto &data : Scene->Definition->Heels.Bones)
                if (Scene->Bones.contains(data.Name))
                {
                    auto *bone = Scene->Bones.at(data.Name);
                    heelPositions[bone] = bone->LocalPosition();
                }
            if (IdleLimbs)
                IdleLimbs->RestoreInputPose();
            Scoped(false);
            for (auto &entry : PartAnimations)
                entry.Player->Sample(time * entry.Speed, entry.Loop);
            if (Idle)
            {
                float clipTime = std::fmod(time, std::max(.01f, Idle->Duration()));
                clipTime = static_cast<float>((static_cast<double>(clipTime) / Idle->Duration()) * Idle->Duration());
                Idle->Sample(clipTime, false);
            }
            for (auto &[node, scale] : ShapedScales)
                node->LocalScale(scale);
            for (auto &[node, position] : heelPositions)
                node->LocalPosition(position);
            Scoped(true);
            if (delta > 0)
                for (auto &chain : Chains)
                    chain->UpdateVisibility();
            for (auto *chain : BoundSetup)
                chain->RefreshBoundOrientation();
            BoundSetup.clear();
            if (IdleLimbs)
                IdleLimbs->Step();
            ApplyHeels();
            for (auto *neck : ForwardNeck)
                neck->LocalRotation({0, 0, 0, 1});
            if (EyeLook)
                EyeLook->Step(delta);
            for (auto *chain : AccessorySetup)
                chain->RefreshGravityOrientation();
            AccessorySetup.clear();
            if (!PhysicsInitialized)
            {
                for (auto &chain : Chains)
                    chain->Reset();
                for (auto &chain : BodyDynamics)
                    chain->Reset();
                PhysicsInitialized = true;
            }
            if (delta > 0)
            {
                for (auto &chain : BodyDynamics)
                    chain->Step(delta);
                for (auto &chain : Chains)
                    chain->Step();
            }
            PoseCharacterFace(*Scene, Scene->Definition->Face.Float("disableBlinking") != 0 ? 1 : Blink.Evaluate(time),
                              true, EyeLook ? EyeLook->HorizontalRate() : 0, EyeLook ? EyeLook->VerticalRate() : 0,
                              bool(EyeLook));
            if (delta > 0)
                Cloth->Step(delta);
        }

        void Reset()
        {
            for (auto &[node, pose] : Initial)
                SetPose(*node, pose);
            for (auto &r : Scene->Renderers)
            {
                r->SimulatedVertices.reset();
                r->LastRenderedVisibility.reset();
            }
            Chains.clear();
            BodyDynamics.clear();
            BoundSetup.clear();
            AccessorySetup.clear();
            ShapedScales.clear();
            ForwardNeck.clear();
            IdleLimbs.reset();
            EyeLook.reset();
            Cloth = std::make_unique<NativeClothRuntime>(*Scene, PhysicsDirectory);
            Blink = IndependentBlink(1979);
            Initialized = false;
            PhysicsInitialized = false;
            Frame = -1;
            Time = -1;
            InitializeHeels();
        }

        void Update(float time)
        {
            Require(std::isfinite(time) && time >= 0 && time < 360000, "Invalid character playback time.");
            if (time == Time)
                return;
            if (time < Time)
                Reset();
            int target = static_cast<int>(std::floor(time * 30));
            if (Frame < 0)
            {
                Step(0, 0);
                Frame = 0;
            }
            while (Frame < target)
            {
                ++Frame;
                Step(FixedTime(1), FixedTime(Frame));
            }
            Time = time;
        }

        void AdvanceFrame(float delta, float time)
        {
            Require(std::isfinite(delta) && delta >= 0 && delta <= .333334f && std::isfinite(time) && time >= 0 &&
                        time < 360000,
                    "Invalid character frame time.");
            Step(delta, time);
            Frame = static_cast<int>(std::floor(time * 30));
            Time = time;
        }

        void RefreshRestPose(const NativeCharacterScene &next)
        {
            std::unordered_map<NativeSceneNode *, NativeSceneNode *> nodes;
            nodes[next.Root.get()] = Scene->Root.get();
            Initial.at(Scene->Root.get()) = GetPose(*next.Root);
            for (auto const &part : next.Parts)
            {
                auto old = std::find_if(Scene->Parts.begin(), Scene->Parts.end(),
                                        [&](auto const &old)
                                        {
                                            return old->Definition->Role == part->Definition->Role &&
                                                   old->Definition->Slot == part->Definition->Slot;
                                        });
                Require(old != Scene->Parts.end() && (*old)->Nodes.size() == part->Nodes.size(),
                        "Character topology changed during a pose update.");
                for (size_t i = 0; i < part->Nodes.size(); ++i)
                {
                    auto node = (*old)->Nodes[i];
                    nodes[part->Nodes[i]] = node;
                    Initial.at(node) = GetPose(*part->Nodes[i]);
                }
            }
            Scene->ScopedBaselines.clear();
            Scene->ScopedAdjustments.clear();
            for (auto const &[node, baseline] : next.ScopedBaselines)
                Scene->ScopedBaselines.emplace(nodes.at(node), baseline);
            for (auto const &[node, adjustment] : next.ScopedAdjustments)
                Scene->ScopedAdjustments.emplace(nodes.at(node), adjustment);
        }

        void Visibility(CharacterPreviewVisibility value)
        {
            auto &definition = *Scene->Definition;
            if (definition.Visibility == value)
                return;
            bool reshape = definition.Visibility.Clothing != value.Clothing && !definition.ClothingStates.empty();
            if (reshape)
            {
                auto const &state = definition.ClothingStates.at(value.Clothing);
                definition.Body = state.Body;
                definition.Heels = state.Heels;
                for (auto &part : Scene->Parts)
                    if (part->Definition->Role == "rig")
                    {
                        part->Definition->Bones = state.Bones;
                        part->Definition->UnmodifiedBones = state.UnmodifiedBones;
                        for (auto const &bone : state.Bones)
                            if (auto found = part->ByName.find(bone.Name); found != part->ByName.end())
                                Initial.at(found->second) = {bone.Position, bone.Scale, bone.Rotation};
                        for (auto const &bone : state.UnmodifiedBones)
                            if (auto found = part->ByName.find(bone.Name);
                                found != part->ByName.end() && Scene->ScopedBaselines.contains(found->second))
                                Scene->ScopedBaselines.at(found->second) = bone;
                    }
            }
            definition.Visibility = value;
            ApplyCharacterVisibility(*Scene);
            if (reshape)
            {
                // 沿用已有资源与播放时间，只重置受塑形和鞋跟影响的运行姿态。
                float time = std::max(0.f, Time);
                Reset();
                AdvanceFrame(0, time);
            }
        }

        void ResetFixturePhysics()
        {
            for (auto &chain : Chains)
                chain->Restore();
            for (auto &chain : BodyDynamics)
                chain->Restore();
            std::unordered_map<NativeSceneNode *, LocalPose> saved;
            for (auto &[node, baseline] : Scene->ScopedBaselines)
            {
                saved[node] = GetPose(*node);
                SetBone(*node, baseline);
            }
            try
            {
                for (auto &chain : Chains)
                    chain->Reset();
                for (auto &chain : BodyDynamics)
                    chain->Reset();
                Cloth->Reset();
            }
            catch (...)
            {
                for (auto &[node, pose] : saved)
                    SetPose(*node, pose);
                throw;
            }
            for (auto &[node, pose] : saved)
                SetPose(*node, pose);
        }
    };

    NativeCharacterRuntime::NativeCharacterRuntime(std::shared_ptr<NativeCharacterScene> scene,
                                                   std::shared_ptr<NativeGpuAssetStore> assets,
                                                   std::filesystem::path directory)
        : impl_(std::make_unique<Impl>(std::move(scene), std::move(assets), std::move(directory)))
    {
    }

    NativeCharacterRuntime::~NativeCharacterRuntime() = default;

    void NativeCharacterRuntime::Visibility(CharacterPreviewVisibility value)
    {
        impl_->Visibility(value);
    }

    void NativeCharacterRuntime::RefreshRestPose(const NativeCharacterScene &next)
    {
        impl_->RefreshRestPose(next);
    }

    void NativeCharacterRuntime::Update(float time)
    {
        impl_->Update(time);
    }

    void NativeCharacterRuntime::AdvanceFrame(float delta, float time)
    {
        impl_->AdvanceFrame(delta, time);
    }

    void NativeCharacterRuntime::ResetFixturePhysics()
    {
        impl_->ResetFixturePhysics();
    }

    void NativeCharacterRuntime::Reset()
    {
        impl_->Reset();
    }

    void NativeCharacterRuntime::Motion(const AssetReference &clip)
    {
        // 先完成读取和绑定；无效动作不会破坏当前播放状态。
        auto player = impl_->MotionPlayer(clip);
        impl_->Reset();
        // 原待机的固定 IK 目标和朝前颈部控制会覆盖其它动作的四肢与头部。
        auto const &definition = *impl_->Scene->Definition;
        impl_->IdlePose = std::any_of(definition.Selections.begin(), definition.Selections.end(),
                                      [&](auto const &selection)
                                      {
                                          return selection.Key == "motion.controller" &&
                                                 selection.Reference.Bundle == clip.Bundle &&
                                                 definition.MotionState == clip.Asset;
                                      });
        impl_->Idle = std::move(player);
        impl_->Update(0);
    }

    const std::vector<std::string> &NativeCharacterRuntime::Diagnostics() const
    {
        return impl_->Diagnostics;
    }
}
