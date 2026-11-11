#include "FrozenDefinition.h"
#include <fstream>
#include <charconv>
#include <iostream>

namespace fixture
{
    using namespace hs2;

    namespace
    {
        const Json Empty = Json::array();

        std::filesystem::path Utf8Path(const std::string &value)
        {
            return std::filesystem::path(std::u8string(reinterpret_cast<const char8_t *>(value.data()), value.size()));
        }

        const Json &Array(const Json &value, const char *key)
        {
            auto it = value.find(key);
            return it != value.end() && it->is_array() ? *it : Empty;
        }

        AssetReference Reference(const Json &j)
        {
            return {Utf8Path(j.value("Bundle", std::string{})), j.value("Asset", std::string{})};
        }

        EmbeddedTextureReference Embedded(const Json &j)
        {
            return {Utf8Path(j.value("Path", std::string{})), j.value("Offset", std::uint64_t{}), j.value("Length", 0)};
        }

        BoneDefinition Bone(const Json &j)
        {
            BoneDefinition b;
            b.Name = j.value("Name", std::string{});
            b.Parent = j.value("Parent", std::string{});
            b.Position = Vector3(j.value("Position", Json{}));
            b.Scale = Vector3(j.value("Scale", Json{}), {1, 1, 1});
            b.Rotation = Quaternion(j.value("Rotation", Json{}));
            return b;
        }

        template <class T> std::vector<T> List(const Json &j, const char *key)
        {
            return Array(j, key).get<std::vector<T>>();
        }

        template <class T, size_t N> void Fixed(std::array<T, N> &result, const Json &j, const char *key)
        {
            auto &a = Array(j, key);
            if (a.empty())
                return;
            if (a.size() != N)
                throw std::runtime_error(std::string("Fixture vector dimensions: ") + key);
            for (size_t i = 0; i < N; ++i)
                result[i] = a[i].get<T>();
        }
    }

    hs2::Vector3 Vector3(const Json &value, hs2::Vector3 fallback)
    {
        if (value.is_array() && value.size() >= 3)
            return {value[0].get<float>(), value[1].get<float>(), value[2].get<float>()};
        if (value.is_object())
            return {value.value("x", fallback.x), value.value("y", fallback.y), value.value("z", fallback.z)};
        return fallback;
    }

    hs2::Quaternion Quaternion(const Json &j)
    {
        if (j.is_array() && j.size() == 4)
            return {j[0].get<float>(), j[1].get<float>(), j[2].get<float>(), j[3].get<float>()};
        if (j.is_object())
            return {j.value("x", 0.f), j.value("y", 0.f), j.value("z", 0.f), j.value("w", 1.f)};
        return {0, 0, 0, 1};
    }

    hs2::Parameter Parameter(const Json &j)
    {
        hs2::Parameter p;
        if (!j.is_object())
            return p;
        p.Name = j.value("Name", std::string{});
        p.Encoding = j.value("Encoding", std::string{});
        p.Text = j.value("Text", std::string{});
        p.Number = j.value("Number", 0.);
        for (const auto &c : Array(j, "Children"))
            p.Children.push_back(Parameter(c));
        return p;
    }

    std::shared_ptr<CharacterDefinition> Definition(const std::filesystem::path &file)
    {
        std::ifstream input(file);
        auto j = Json::parse(input);
        auto result = std::make_shared<CharacterDefinition>();
        auto &d = *result;
        d.SchemaVersion = j.value("SchemaVersion", 1);
        d.EditRevision = j.value("EditRevision", std::int64_t{});
        d.Name = j.value("Name", std::string{});
        d.Sex = j.value("Sex", 0);
        d.CardHash = j.value("CardHash", std::string{});
        d.MotionState = j.value("MotionState", std::string{});
        d.BodyVisible = j.value("BodyVisible", true);
        d.EyeOpenMax = j.value("EyeOpenMax", 1.f);
        if (auto it = j.find("Visibility"); it != j.end() && it->is_object())
        {
            d.Visibility.Clothing = it->value("Clothing", 0);
            d.Visibility.Accessories = it->value("Accessories", true);
        }
        for (const auto &p : Array(j, "BundlePaths"))
            d.BundlePaths.push_back(Utf8Path(p.get<std::string>()));
        d.Face = Parameter(j.value("Face", Json{}));
        d.Body = Parameter(j.value("Body", Json{}));
        d.Hair = Parameter(j.value("Hair", Json{}));
        d.Clothes = Parameter(j.value("Clothes", Json{}));
        d.Diagnostics = List<std::string>(j, "Diagnostics");
        for (const auto &p : Array(j, "Parts"))
        {
            auto part = std::make_shared<CharacterPart>();
            part->Role = p.value("Role", std::string{});
            part->Slot = p.value("Slot", 0);
            part->Prefab = Reference(p.at("Prefab"));
            part->Parent = p.value("Parent", std::string{});
            part->RuntimeName = p.value("RuntimeName", std::string{});
            for (const auto &b : Array(p, "Bones"))
                part->Bones.push_back(Bone(b));
            for (const auto &b : Array(p, "UnmodifiedBones"))
                part->UnmodifiedBones.push_back(Bone(b));
            for (const auto &c : Array(p, "Components"))
                part->Components.push_back({c.value("Node", std::string{}), c.value("Type", std::string{}),
                                            Parameter(c.value("Data", Json{}))});
            part->Adjustments = List<float>(p, "Adjustments");
            part->Colors = List<float>(p, "Colors");
            part->HideOptions = List<bool>(p, "HideOptions");
            part->List = Parameter(p.value("List", Json{}));
            part->Parameters = Parameter(p.value("Parameters", Json{}));
            d.Parts.push_back(std::move(part));
        }
        for (const auto &s : Array(j, "Selections"))
            d.Selections.push_back({s.value("Key", std::string{}), Reference(s.at("Reference"))});
        for (const auto &m : Array(j, "MaterialEdits"))
        {
            MaterialEdit edit;
            edit.ObjectType = m.value("ObjectType", 0);
            edit.Slot = m.value("Slot", 0);
            edit.Name = m.value("Name", std::string{});
            edit.Property = m.value("Property", std::string{});
            edit.Kind = m.value("Kind", std::string{});
            edit.Text = m.value("Text", std::string{});
            edit.Number = m.value("Number", 0.f);
            edit.Values = List<float>(m, "Values");
            edit.Offset = List<float>(m, "Offset");
            edit.Scale = List<float>(m, "Scale");
            if (m.contains("Texture") && m["Texture"].is_object())
                edit.Texture = Embedded(m["Texture"]);
            d.MaterialEdits.push_back(std::move(edit));
        }
        for (const auto &b : Array(j, "BoneModifiers"))
        {
            BoneAdjustment bone;
            bone.Name = b.value("Name", std::string{});
            bone.Position = Vector3(b.value("Position", Json{}));
            bone.Rotation = Vector3(b.value("Rotation", Json{}));
            bone.Scale = Vector3(b.value("Scale", Json{}), {1, 1, 1});
            bone.Length = b.value("Length", 1.f);
            d.BoneModifiers.push_back(bone);
        }
        for (const auto &s : Array(j, "Shaders"))
        {
            ShaderDefinition shader;
            shader.Name = s.value("Name", std::string{});
            shader.AutoReplace = s.value("AutoReplace", false);
            shader.RenderQueue = s.value("RenderQueue", -1);
            if (s.contains("Source") && s["Source"].is_object())
                shader.Source = Reference(s["Source"]);
            for (const auto &v : Array(s, "Defaults"))
            {
                ShaderDefault value;
                value.Property = v.value("Property", std::string{});
                value.Type = v.value("Type", std::string{});
                value.Values = List<float>(v, "Values");
                if (v.contains("Texture") && v["Texture"].is_object())
                    value.Texture = Reference(v["Texture"]);
                shader.Defaults.push_back(std::move(value));
            }
            d.Shaders.push_back(std::move(shader));
        }
        for (const auto &o : Array(j, "Overlays"))
        {
            OverlayDefinition overlay;
            overlay.Type = o.value("Type", 0);
            overlay.Name = o.value("Name", std::string{});
            overlay.Replace = o.value("Replace", false);
            if (o.contains("Texture") && o["Texture"].is_object())
                overlay.Texture = Embedded(o["Texture"]);
            d.Overlays.push_back(std::move(overlay));
        }
        if (j.contains("Heels") && j["Heels"].is_object())
        {
            const auto &h = j["Heels"];
            d.Heels.Height = h.value("Height", 0.f);
            for (const auto &b : Array(h, "Bones"))
            {
                HeelBone bone;
                bone.Name = b.value("Name", std::string{});
                bone.Move = Vector3(b.value("Move", Json{}));
                bone.Roll = Vector3(b.value("Roll", Json{}));
                bone.Scale = Vector3(b.value("Scale", Json{}), {1, 1, 1});
                bone.Fixed = b.value("Fixed", false);
                d.Heels.Bones.push_back(bone);
            }
        }
        if (j.contains("ColliderSupport") && j["ColliderSupport"].is_object())
            d.ColliderSupport = ColliderSupportDefinition{j["ColliderSupport"].value("Breast", false),
                                                          j["ColliderSupport"].value("Floor", false)};
        const auto &e = j.at("Environment");
        auto &env = d.Environment;
        env.RenderSettings = Parameter(e.value("RenderSettings", Json{}));
        env.Quality = Parameter(e.value("Quality", Json{}));
        env.Camera = Parameter(e.value("Camera", Json{}));
        env.Physics = Parameter(e.value("Physics", Json{}));
        env.DhhEnabled = e.value("DhhEnabled", false);
        env.SkyboxId = e.value("SkyboxId", 6);
        env.Brightness = e.value("Brightness", 0.f);
        env.LutPath = e.value("LutPath", std::string{});
        env.BetterAA = e.value("BetterAA", false);
        Fixed(env.Background, e, "Background");
        Fixed(env.DhhFloats, e, "DhhFloats");
        Fixed(env.DhhBools, e, "DhhBools");
        Fixed(env.DhhColors, e, "DhhColors");
        Fixed(env.Temporal, e, "Temporal");
        for (const auto &l : Array(e, "Lights"))
        {
            SceneLight light;
            light.Name = l.value("Name", std::string{});
            light.CameraRelative = l.value("CameraRelative", false);
            Fixed(light.Rotation, l, "Rotation");
            light.Parameters = Parameter(l.value("Parameters", Json{}));
            env.Lights.push_back(std::move(light));
        }
        for (const auto &p : Array(e, "Profiles"))
        {
            PostProfile profile;
            profile.Weight = p.value("Weight", 1.f);
            profile.Priority = p.value("Priority", 0.f);
            for (const auto &effect : Array(p, "Effects"))
                profile.Effects.push_back(Parameter(effect));
            env.Profiles.push_back(std::move(profile));
        }
        return result;
    }

    void Pose(NativeCharacterScene &scene, const std::filesystem::path &file)
    {
        std::ifstream input(file);
        auto j = Json::parse(input);
        std::unordered_map<std::string, std::vector<NativeSceneNode *>> paths;
        std::unordered_map<std::string, size_t> occurrences;
        for (auto *node : scene.Root->Traverse())
        {
            std::string path = node->Name;
            for (auto *p = node->Parent(); p && p != scene.Root.get(); p = p->Parent())
                path = p->Name + "/" + path;
            paths[path].push_back(node);
        }
        size_t matched = 0;
        for (const auto &b : Array(j, "Bones"))
        {
            auto it = paths.find(b.value("Path", std::string{}));
            if (it == paths.end())
                continue;
            auto &occurrence = occurrences[it->first];
            if (occurrence >= it->second.size())
                continue;
            auto &node = *it->second[occurrence++];
            node.LocalPosition(Vector3(b.at("Position")));
            node.LocalRotation(Quaternion(b.at("Rotation")));
            node.LocalScale(Vector3(b.at("Scale"), {1, 1, 1}));
            ++matched;
        }
        for (const auto &face : Array(j, "Faces"))
            for (auto &renderer : scene.Renderers)
                if (renderer->Node->Name == face.value("Name", std::string{}))
                {
                    auto weights = List<float>(face, "BlendWeights");
                    if (weights.size() == renderer->BlendWeights.size())
                        renderer->BlendWeights = std::move(weights);
                }
        std::cout << "Frozen pose bones applied=" << matched << "/" << Array(j, "Bones").size() << std::endl;
        if (matched < 100)
            throw std::runtime_error("Frozen pose did not match character hierarchy.");
    }
}
