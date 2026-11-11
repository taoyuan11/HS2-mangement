#include "Character/CharacterPreparation.h"
#include "Character/CharacterExtensions.h"
#include "Character/CharacterBodyCatalog.h"
#include "Environment/EnvironmentReader.h"
#include "Assets/MonoSchema.h"
#include "Character/CharacterShaders.h"
#include "Animation/ShapeRig.h"
#include "Animation/Animation.h"
#include <algorithm>
#include <cmath>
#include <set>
#include <sstream>

namespace hs2
{
    namespace
    {
        void Require(bool value, const char *text)
        {
            if (!value)
                throw std::runtime_error(text);
        }

        bool Present(std::string_view value)
        {
            return !value.empty() && value != "0";
        }

        std::vector<float> Flatten(const Parameter &value)
        {
            std::vector<float> result;
            std::function<void(const Parameter &)> add = [&](auto &p)
            {
                if (p.Children.empty())
                {
                    Require(std::isfinite(p.Number), "Non-finite character parameter.");
                    result.push_back(static_cast<float>(p.Number));
                }
                else
                    for (auto &child : p.Children)
                        add(child);
            };
            add(value);
            return result;
        }

        Vector3 Vector(const Parameter &p)
        {
            auto values = Flatten(p);
            Require(values.size() == 3, "Invalid character vector.");
            return {values[0], values[1], values[2]};
        }

        Quaternion Product(Quaternion a, Quaternion b)
        {
            return {a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y, a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                    a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w, a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z};
        }

        Parameter ListParameters(const NativeAssetEntry &entry)
        {
            Parameter list;
            for (auto &[key, value] : entry.Values)
            {
                Parameter p;
                p.Name = key;
                p.Text = value;
                list.Children.push_back(std::move(p));
            }
            return list;
        }

        void ApplyBoneModifiers(CharacterPart &part, const std::vector<BoneAdjustment> &modifiers)
        {
            std::string root;
            for (auto &b : part.Bones)
                if (b.Parent.empty())
                {
                    root = b.Name;
                    break;
                }
            for (auto &change : modifiers)
            {
                std::string name = change.Name == part.RuntimeName && !root.empty() ? root : change.Name;
                for (auto &bone : part.Bones)
                    if (bone.Name == name)
                    {
                        part.UnmodifiedBones.push_back(bone);
                        bone.Scale = {bone.Scale.x * change.Scale.x, bone.Scale.y * change.Scale.y,
                                      bone.Scale.z * change.Scale.z};
                        bone.Position = {bone.Position.x * change.Length + change.Position.x,
                                         bone.Position.y * change.Length + change.Position.y,
                                         bone.Position.z * change.Length + change.Position.z};
                        bone.Rotation = Product(bone.Rotation, EulerRotation(change.Rotation));
                        break;
                    }
            }
        }
    }

    NativeCharacterService::NativeCharacterService(fs::path game, fs::path data, fs::path tpk)
        : game_(fs::absolute(game)), data_(fs::absolute(data)), catalog_(game_, data_, std::move(tpk)),
          blockCache_(std::make_shared<UnityBlockCache>(data_ / "NativeCpp/blocks-v1"))
    {
    }

    PreparedCharacter NativeCharacterService::Prepare(const CardRecord &card, const CharacterDocument *draft,
                                                      CharacterPreviewVisibility visibility, Progress progress,
                                                      std::stop_token cancellation)
    {
        CheckCancelled(cancellation);
        auto document = draft ? *draft : CharacterDocument::Open(Path(card.Path), cancellation);
        catalog_.Load(progress, cancellation);
        auto definition = std::make_shared<CharacterDefinition>();
        definition->Name = card.Name;
        definition->Sex = document.Sex();
        definition->CardHash = document.SourceHash();
        definition->Visibility = visibility;
        definition->Face = ParameterOf(document.Node("face"));
        definition->Body = ParameterOf(document.Node("body"));
        definition->Hair = ParameterOf(document.Node("hair"));
        definition->Clothes = ParameterOf(document.Node("clothes"));
        definition->Diagnostics = catalog_.Warnings();
        auto extensions = ReadCharacterExtensions(document, *definition, data_, game_, cancellation);
        bool unlocked = false;
        auto pluginRoot = game_ / "BepInEx/plugins";
        if (fs::is_directory(pluginRoot))
            for (auto &file : fs::recursive_directory_iterator(pluginRoot))
                if (file.path().filename() == L"HS2_SliderUnlocker.dll")
                {
                    unlocked = true;
                    break;
                }
        Parameter unlockedFlag;
        unlockedFlag.Name = "unlockedSliders";
        unlockedFlag.Number = unlocked ? 1 : 0;
        definition->Hair.Children.push_back(unlockedFlag);
        NativeShapeRig shapes(game_, unlocked);
        auto assets = std::make_shared<NativeGpuAssetStore>(std::vector<fs::path>{}, game_, catalog_.Database(),
                                                            cancellation, blockCache_);
        std::set<fs::path> paths;
        auto schemas = std::make_shared<NativeMonoSchema>(game_ / "HoneySelect2_Data/Managed", catalog_.Database());
        assets->SetScriptSchema(
            [weak = std::weak_ptr<NativeGpuAssetStore>(assets), schemas](const GpuAssetPointer &pointer,
                                                                         std::string_view version)
            {
                auto store = weak.lock();
                Require(bool(store), "Character asset store has expired.");
                auto script = store->Metadata(pointer);
                return schemas->Type(script.String("m_AssemblyName"), script.String("m_Namespace"),
                                     script.String("m_ClassName"), version);
            });
        auto reference = [&](const std::string &bundle, const std::string &name, const fs::path &archive = fs::path{})
        {
            CheckCancelled(cancellation);
            auto ref = catalog_.Asset(bundle, name, archive);
            for (auto &dependency : catalog_.Dependencies(bundle, archive))
            {
                assets->AddPath(dependency);
                paths.insert(dependency);
            }
            assets->AddPath(ref.Bundle);
            paths.insert(ref.Bundle);
            return ref;
        };
        auto text = [&](const std::string &bundle, const std::string &name, const fs::path &archive = fs::path{})
        {
            auto ref = reference(bundle, name, archive);
            return assets->Reader(ref.Bundle)->ReadText(name);
        };
        auto read = [&](const std::string &bundle, const std::string &name, const fs::path &archive,
                        const std::string &role, int slot)
        {
            if (progress)
                progress({0, 0, "读取原始模型结构：" + name});
            auto ref = reference(bundle, name, archive);
            auto reader = assets->Reader(ref.Bundle);
            auto prefab = assets->Prefab(ref.Bundle, name);
            const auto &raw = *prefab;
            auto part = std::make_shared<CharacterPart>();
            part->Role = role;
            part->Slot = slot;
            part->Prefab = ref;
            std::unordered_map<int64_t, std::string> names;
            for (auto &node : raw.Nodes)
                names[node.Id] = node.Name;
            for (auto &node : raw.Nodes)
                part->Bones.push_back({node.Name, names.contains(node.ParentId) ? names.at(node.ParentId) : "",
                                       node.Position, node.Scale, node.Rotation});
            part->Components = ReadCharacterComponents(*reader, raw, definition->Diagnostics);
            if (reader->TryFind("additional_bones", 49))
            {
                auto bytes = reader->ReadText("additional_bones");
                std::istringstream lines(std::string(bytes.begin(), bytes.end()));
                for (std::string line; std::getline(lines, line);)
                {
                    std::istringstream columns(line);
                    std::string key;
                    if (!std::getline(columns, key, '\t') || key != name)
                        continue;
                    Parameter data;
                    for (std::string bone; std::getline(columns, bone, '\t');)
                        if (!bone.empty())
                        {
                            if (bone.back() == '\r')
                                bone.pop_back();
                            Parameter p;
                            p.Text = bone;
                            data.Children.push_back(std::move(p));
                        }
                    part->Components.push_back({name, "BonesFramework", std::move(data)});
                    break;
                }
            }
            definition->Parts.push_back(part);
            return part;
        };
        auto applyShape = [&](CharacterPart &part, const char *recipe, std::span<const uint8_t> animation,
                              std::span<const uint8_t> categories, const Parameter &values)
        {
            std::unordered_map<std::string, BonePose> bones;
            for (auto &b : part.Bones)
                bones.try_emplace(b.Name, BonePose{b.Position, b.Scale, b.Rotation});
            auto flattened = Flatten(values);
            shapes.Apply(bones, recipe, animation,
                         std::string_view(reinterpret_cast<const char *>(categories.data()), categories.size()),
                         flattened, cancellation);
            for (auto &b : part.Bones)
            {
                auto &pose = bones.at(b.Name);
                b.Position = pose.Position;
                b.Scale = pose.Scale;
                b.Rotation = pose.Rotation;
            }
        };
        auto rig = read("chara/oo_base.unity3d", "p_cf_anim", {}, "rig", -1);
        auto bodyAnimation = text("list/customshape.unity3d", "cf_anmShapeBody"),
             bodyCategories = text("list/customshape.unity3d", "cf_custombody");
        applyShape(*rig, "ShapeBodyInfoFemale", bodyAnimation, bodyCategories, definition->Body.Get("shapeValueBody"));
        ApplyBoneModifiers(*rig, definition->BoneModifiers);
        read("chara/oo_base.unity3d", definition->Sex == 0 ? "p_cm_body_00" : "p_cf_body_00", {}, "body", 0);
        for (auto &supplement : ResolveCharacterBody(game_, data_, document, extensions.Uncensor, cancellation))
        {
            auto part = read(supplement.Bundle, supplement.Asset, supplement.Archive, "supplement",
                             supplement.Kind == "Body"    ? 0
                             : supplement.Kind == "Penis" ? 1
                                                          : 2);
            Parameter kind;
            kind.Name = "Kind";
            kind.Text = supplement.Kind;
            Parameter guid;
            guid.Name = "GUID";
            guid.Text = supplement.Guid;
            part->List.Children = {kind, guid};
            ApplyBoneModifiers(*part, definition->BoneModifiers);
        }
        auto select = [&](std::string key, const std::string &bundle, const std::string &name,
                          const fs::path &archive = fs::path{})
        {
            if (!Present(bundle) || !Present(name))
                return;
            try
            {
                definition->Selections.push_back({key, reference(bundle, name, archive)});
            }
            catch (const std::exception &e)
            {
                CheckCancelled(cancellation);
                definition->Diagnostics.push_back(key + ": " + e.what());
            }
        };
        std::unordered_map<int, NativeAssetEntry> worn;
        auto add = [&](const std::string &path, const std::string &role, int slot, const Parameter &parameters,
                       const ExtraCharacterAccessory *extra = nullptr)
        {
            CheckCancelled(cancellation);
            auto partCount = definition->Parts.size();
            try
            {
                std::optional<NativeAssetEntry> chosen;
                if (extra)
                {
                    if (extra->Reference)
                        chosen = catalog_.Resolve(*extra->Reference);
                    else
                        for (auto &candidate : catalog_.Entries())
                            if (candidate.Category == extra->Category && candidate.Id == extra->Id &&
                                candidate.Guid.empty())
                            {
                                chosen = candidate;
                                break;
                            }
                }
                else
                    chosen = catalog_.Selected(document, path);
                if (!chosen && role == "accessory" && parameters.Int("id") == 99999999 &&
                    extensions.AccessoryControls.contains(slot))
                {
                    auto saved = extensions.AccessoryControls.at(slot).Name;
                    std::optional<NativeAssetEntry> match;
                    bool ambiguous = false;
                    for (auto &candidate : catalog_.Entries())
                        if (candidate.Category == parameters.Int("type") && candidate.Get("Name") == saved)
                        {
                            if (match)
                            {
                                ambiguous = true;
                                break;
                            }
                            match = candidate;
                        }
                    if (!ambiguous)
                        chosen = match;
                }
                if (!chosen && role == "hair")
                {
                    int fallback = slot == 1 ? (definition->Sex == 0 ? 2 : 1) : 0;
                    for (auto &entry : catalog_.Entries())
                        if (entry.Category == 300 + slot && entry.Id == fallback && entry.Guid.empty())
                        {
                            chosen = entry;
                            for (auto &field : definition->Hair.Children)
                                if (field.Name == "parts" && slot >= 0 &&
                                    static_cast<size_t>(slot) < field.Children.size())
                                    for (auto &saved : field.Children[slot].Children)
                                        if (saved.Name == "dictBundle")
                                            saved.Children.clear();
                            definition->Diagnostics.push_back("发型资源缺失，使用游戏默认发型：" + path);
                            break;
                        }
                }
                if (!chosen)
                {
                    definition->Diagnostics.push_back("素材清单项不存在：" + path);
                    return;
                }
                auto &entry = *chosen;
                if (role == "clothes" && ((slot == 1 && worn.contains(0) && worn.at(0).Get("Coordinate") == "1") ||
                                          (slot == 2 && worn.contains(0) && worn.at(0).Get("NotBra") == "1") ||
                                          (slot == 3 && worn.contains(2) && worn.at(2).Get("Coordinate") == "1")))
                    return;
                auto name = definition->Sex == 0 && Present(entry.MaleAsset) ? entry.MaleAsset : entry.Asset;
                if (!Present(name) || name == "p_dummy")
                    return;
                auto part = read(entry.Bundle, name, entry.Archive, role, slot);
                part->Parent = role == "head"                            ? "cf_J_Head_s"
                               : role == "hair"                          ? "N_hair_Root"
                               : Present(parameters.String("parentKey")) ? parameters.String("parentKey")
                                                                         : entry.Get("Parent");
                part->Parameters = parameters;
                part->List = ListParameters(entry);
                if (role == "hair")
                {
                    static const char *roots[]{"ct_hairB", "ct_hairF", "ct_hairS", "ct_hairO"};
                    part->RuntimeName = roots[slot];
                    part->Colors = Flatten(parameters.Get("baseColor"));
                }
                else if (role == "clothes")
                {
                    static const char *roots[]{"ct_clothesTop", "ct_clothesBot", "ct_inner_t", "ct_inner_b",
                                               "ct_gloves",     "ct_panst",      "ct_socks",   "ct_shoes"};
                    part->RuntimeName = roots[slot];
                    for (auto &info : parameters.Get("colorInfo").Children)
                    {
                        auto color = Flatten(info.Get("baseColor"));
                        part->Colors.insert(part->Colors.end(), color.begin(), color.end());
                    }
                    for (auto &v : parameters.Get("hideOpt").Children)
                        part->HideOptions.push_back(v.Number != 0);
                }
                else if (role == "accessory")
                {
                    part->RuntimeName =
                        "ca_slot" + (slot < 10 ? std::string("0") : std::string{}) + std::to_string(slot);
                    for (auto &info : parameters.Get("colorInfo").Children)
                    {
                        auto color = Flatten(info.Get("color"));
                        part->Colors.insert(part->Colors.end(), color.begin(), color.end());
                    }
                    auto &move = parameters.Get("addMove");
                    part->Adjustments = Flatten(
                        move.Children.size() == 3 && !move.Children[2].Children.empty() ? move.Children[2] : move);
                }
                if (role == "hair" && fs::is_regular_file(game_ / "BepInEx/plugins/HairMeshforAll.dll"))
                    for (auto &component : part->Components)
                        if (component.Type == "CmpHair" && !component.Data.Get("rendHair").Children.empty())
                            for (auto &field : component.Data.Children)
                                if (field.Name == "useMesh")
                                    field.Number = 1;
                if (role == "head")
                {
                    auto head = read("chara/oo_base.unity3d", "p_cf_head_bone", {}, "headRig", -1);
                    definition->Parts.pop_back();
                    auto before = std::find(definition->Parts.begin(), definition->Parts.end(), part);
                    definition->Parts.insert(before, head);
                    head->Parent = "cf_J_Head_s";
                    part->Parent = "p_cf_head_bone";
                    for (auto &source : part->Bones)
                        for (auto &target : head->Bones)
                            if (source.Name == target.Name)
                            {
                                target.Position = source.Position;
                                target.Rotation = source.Rotation;
                                target.Scale = source.Scale;
                                break;
                            }
                    auto animation = text(entry.Bundle, entry.Get("ShapeAnime"), entry.Archive),
                         categories =
                             text("list/customshape.unity3d", definition->Sex == 0 ? "cm_customhead" : "cf_customhead");
                    applyShape(*head, "ShapeHeadInfoFemale", animation, categories,
                               definition->Face.Get("shapeValueFace"));
                    ApplyBoneModifiers(*head, definition->BoneModifiers);
                    select("head.MatData", entry.Bundle, entry.Get("MatData"), entry.Archive);
                }
                if (extra)
                {
                    part->Adjustments = extra->Adjustments;
                    part->Colors = extra->Colors;
                    part->Parent = extra->Parent.empty() ? part->Parent : extra->Parent;
                }
                ApplyBoneModifiers(*part, definition->BoneModifiers);
                bool hairTexture =
                    role == "hair" || std::any_of(part->Components.begin(), part->Components.end(),
                                                  [](auto &c)
                                                  {
                                                      return c.Type == "CmpAccessory" && c.Data.Float("typeHair") != 0;
                                                  });
                if (hairTexture)
                {
                    std::string key = definition->Hair.Int("shaderType") == 0 ? "TexD" : "TexC";
                    select(role + "." + std::to_string(slot) + "." + key, entry.Get("TexAB"), entry.Get(key),
                           entry.Archive);
                }
                if (role == "clothes")
                {
                    worn[slot] = entry;
                    for (std::string key : {"MainTex", "MainTex02", "MainTex03", "ColorMaskTex", "ColorMask02Tex",
                                            "ColorMask03Tex", "MaskTex", "MaskTex2"})
                        select("clothes." + std::to_string(slot) + "." + key, entry.Bundle, entry.Get(key),
                               entry.Archive);
                    for (std::string key : {"OverBodyMask", "OverBraMask", "OverBodyBMask", "OverInnerTBMask",
                                            "OverInnerBMask", "OverPanstMask"})
                        select("clothes." + std::to_string(slot) + "." + key, entry.Get(key + "AB"), entry.Get(key),
                               entry.Archive);
                }
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &e)
            {
                CheckCancelled(cancellation);
                definition->Parts.resize(partCount);
                definition->Diagnostics.push_back(path + ": " + e.what());
            }
        };
        add("face.headId", "head", -1, definition->Face);
        auto &hair = definition->Hair.Get("parts").Children;
        for (size_t i = 0; i < std::min<size_t>(4, hair.size()); ++i)
            add("hair.parts[" + std::to_string(i) + "].id", "hair", static_cast<int>(i), hair[i]);
        auto &clothes = definition->Clothes.Get("parts").Children;
        for (size_t i = 0; i < std::min<size_t>(8, clothes.size()); ++i)
            if (definition->Sex != 0 || (i != 2 && i != 3 && i != 5 && i != 6))
                add("clothes.parts[" + std::to_string(i) + "].id", "clothes", static_cast<int>(i), clothes[i]);
        auto accessories = ParameterOf(document.Node("accessory"));
        auto &accessoryParts = accessories.Get("parts").Children;
        for (size_t i = 0; i < std::min<size_t>(256, accessoryParts.size()); ++i)
            if (accessoryParts[i].Int("type") >= 351 && accessoryParts[i].Int("type") <= 399)
                add("accessory.parts[" + std::to_string(i) + "].id", "accessory", static_cast<int>(i),
                    accessoryParts[i]);
        for (auto &extra : extensions.Accessories)
            add("accessory.parts[" + std::to_string(extra.Slot) + "].id", "accessory", extra.Slot, extra.Parameters,
                &extra);
        ApplyCharacterPushUp(*definition, extensions, bodyAnimation, worn.contains(2) && visibility.ClothesVisible(2),
                             worn.contains(0) && visibility.ClothesVisible(0));
        auto selectCategory = [&](const std::string &key, const std::string &path)
        {
            if (!document.Contains(path))
                return;
            try
            {
                auto entry = catalog_.Selected(document, path);
                if (!entry)
                {
                    definition->Diagnostics.push_back("素材清单项不存在：" + path);
                    return;
                }
                if (entry->Category == 348 && entry->Id == 0)
                    return;
                for (std::string property : {"MainTex", "AddTex", "GlossTex", "NormalMapTex", "OcclusionMapTex"})
                    select(key + "." + property,
                           property == "MainTex" && Present(entry->Get("MainTexAB")) ? entry->Get("MainTexAB")
                                                                                     : entry->Bundle,
                           entry->Get(property), entry->Archive);
            }
            catch (const std::exception &e)
            {
                CheckCancelled(cancellation);
                definition->Diagnostics.push_back(path + ": " + e.what());
            }
        };
        for (std::string key : {"skin", "detail", "sunburn", "nip", "underhair"})
            selectCategory("body." + key, "body." + key + "Id");
        for (std::string key : {"skin", "detail", "eyebrow", "eyelashes", "mole"})
            selectCategory("face." + key, "face." + key + "Id");
        for (std::string key : {"eyeshadow", "cheek", "lip"})
            selectCategory("face." + key, "face.makeup." + key + "Id");
        for (int eye = 0; eye < 2; ++eye)
        {
            auto index = std::to_string(eye);
            selectCategory("eye." + index, "face.pupil[" + index + "].pupilId");
            selectCategory("pupil." + index, "face.pupil[" + index + "].blackId");
        }
        selectCategory("highlight", "face.hlId");
        for (int i = 0; i < 2; ++i)
        {
            auto index = std::to_string(i);
            selectCategory("face.paint." + index, "face.makeup.paintInfo[" + index + "].id");
            selectCategory("body.paint." + index, "body.paintInfo[" + index + "].id");
        }
        for (size_t slot = 0; slot < clothes.size(); ++slot)
            if (worn.contains(static_cast<int>(slot)))
                for (size_t color = 0; color < clothes[slot].Get("colorInfo").Children.size(); ++color)
                    selectCategory("clothes." + std::to_string(slot) + ".pattern." + std::to_string(color),
                                   "clothes.parts[" + std::to_string(slot) + "].colorInfo[" + std::to_string(color) +
                                       "].pattern");
        for (std::string name :
             {"create_skin_body", "create_skin detail_body", "create_skin_face", "create_skin detail_face",
              "create_clothes", "create_clothes detail", "cf_m_skin_body_00", "cm_m_skin_body_00"})
            select("template." + name, "chara/mm_base.unity3d", name);
        for (std::string name : {"black2048", "black4096"})
            select("template." + name, "chara/etc.unity3d", name);
        select("template.hair", "chara/hair_shader_mat.unity3d",
               definition->Hair.Int("shaderType") == 0 ? "hair_dithering" : "hair_cutout");
        int count = 0;
        for (auto &entry : catalog_.StockEntries())
            if (entry.Category == (definition->Sex == 0 ? 500 : 501) && ++count == 2)
            {
                select("motion.controller", entry.Bundle, entry.Get("MainData"), entry.Archive);
                definition->MotionState = entry.Get("Clip");
                break;
            }
        definition->Heels = visibility.ClothesVisible(7)
                                ? ReadCharacterHeels(worn.contains(7) ? &worn.at(7) : nullptr, definition->Diagnostics)
                                : HeelDefinition{};
        definition->ColliderSupport = ReadCharacterColliders(game_);
        if (!definition->Overlays.empty() ||
            std::any_of(definition->MaterialEdits.begin(), definition->MaterialEdits.end(),
                        [](auto &e)
                        {
                            return e.Kind == "shader";
                        }))
            for (auto &bundle : ExtractUnityBundles(game_ / "BepInEx/plugins/HS2_OverlayMods.dll",
                                                    data_ / "NativeCpp/resources/shader-data", cancellation))
            {
                assets->AddPath(bundle);
                auto reader = assets->Reader(bundle);
                if (reader->TryFind("composite", 48))
                    definition->Selections.push_back({"overlay.composite", {bundle, "composite"}});
                if (reader->TryFind("Eye", 48))
                    definition->Selections.push_back({"overlay.eye", {bundle, "Eye"}});
            }
        if (progress)
            progress({0, 0, "读取角色光照与后处理设置"});
        definition->Environment =
            ReadCharacterEnvironment(game_, data_ / "NativeCpp" / "resources", *assets, definition->Selections,
                                     definition->Diagnostics, cancellation);
        if (progress)
            progress({0, 0, "读取材质编辑器着色器配置"});
        ReadCharacterShaders(game_, data_, catalog_, *assets, *definition, cancellation);
        for (auto &parameter : definition->Body.Children)
            if (parameter.Name == "paintInfo")
                for (auto &paint : parameter.Children)
                {
                    const NativeAssetEntry *layout = nullptr;
                    for (auto &entry : catalog_.StockEntries())
                        if (entry.Category == 8 && entry.Id == paint.Int("layoutId"))
                            layout = &entry;
                    if (layout)
                    {
                        Parameter resolved;
                        resolved.Name = "resolvedLayout";
                        for (auto &[key, cell] : layout->Values)
                            try
                            {
                                size_t consumed = 0;
                                double value = std::stod(cell, &consumed);
                                if (consumed != cell.size() || !std::isfinite(value))
                                    continue;
                                Parameter field;
                                field.Name = key;
                                field.Number = value;
                                resolved.Children.push_back(std::move(field));
                            }
                            catch (const std::exception &)
                            {
                            }
                        paint.Children.push_back(std::move(resolved));
                    }
                }
        definition->BundlePaths = assets->Paths();
        if (progress)
            progress({0, 0, "组装角色网格与骨骼"});
        auto scene = AssembleCharacter(definition, *assets, cancellation);
        CheckCancelled(cancellation);
        Require(!scene->Renderers.empty(), "No character renderers were assembled.");
        return {scene, assets};
    }
}
