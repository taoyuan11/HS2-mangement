#include "Character/CharacterExtensions.h"
#include <windows.h>
#include <algorithm>
#include <cmath>
#include <iostream>
using namespace hs2;

namespace
{
    using MP = MessagePack;
    int checks = 0;

    void Check(bool value, const char *message)
    {
        if (!value)
            throw std::runtime_error(message);
        checks++;
    }

    MP S(std::string value)
    {
        return MP::Scalar(std::move(value));
    }

    MP I(std::int64_t value)
    {
        return MP::Scalar(value);
    }

    MP F(float value)
    {
        return MP::Scalar(value);
    }

    MP B(bool value)
    {
        return MP::Scalar(value);
    }

    MP V(std::initializer_list<float> values)
    {
        std::vector<MP> items;
        for (auto value : values)
            items.push_back(F(value));
        return MP::Array(std::move(items));
    }

    void Number(Bytes &bytes, std::uint64_t number, int size, bool big = false)
    {
        for (int i = 0; i < size; i++)
            bytes.push_back(static_cast<std::uint8_t>(number >> ((big ? size - i - 1 : i) * 8)));
    }

    void Append(Bytes &output, const Bytes &bytes)
    {
        output.insert(output.end(), bytes.begin(), bytes.end());
    }

    void Text(Bytes &output, const std::string &text)
    {
        std::size_t size = text.size();
        while (size >= 128)
        {
            output.push_back(static_cast<std::uint8_t>((size & 127) | 128));
            size >>= 7;
        }
        output.push_back(static_cast<std::uint8_t>(size));
        output.insert(output.end(), text.begin(), text.end());
    }

    Bytes Parts()
    {
        Bytes output;
        for (int i = 0; i < 3; i++)
        {
            auto map = MP::Map().ToBytes();
            Number(output, map.size(), 4);
            Append(output, map);
        }
        return output;
    }

    CharacterDocument Document(MP extensions)
    {
        auto parameter = MP::Map({{std::string("sex"), I(1)}, {std::string("fullname"), S("Extensions")}}).ToBytes();
        auto coordinate = Parts();
        std::vector<std::pair<std::string, Bytes>> blocks{
            {"Parameter", parameter}, {"Custom", Parts()}, {"Coordinate", coordinate}, {"KKEx", extensions.ToBytes()}};
        Bytes payload;
        std::vector<MP> rows;
        for (auto &[name, bytes] : blocks)
        {
            rows.push_back(MP::Map({{std::string("name"), S(name)},
                                    {std::string("version"), S("1")},
                                    {std::string("pos"), I(payload.size())},
                                    {std::string("size"), I(bytes.size())}}));
            Append(payload, bytes);
        }
        auto table = MP::Map({{std::string("lstInfo"), MP::Array(rows)}}).ToBytes();
        Bytes bytes{137, 80, 78, 71, 13, 10, 26, 10};
        Number(bytes, 13, 4, true);
        bytes.insert(bytes.end(), {'I', 'H', 'D', 'R'});
        bytes.insert(bytes.end(), 13, 0);
        Number(bytes, 0, 4);
        Number(bytes, 0, 4);
        bytes.insert(bytes.end(), {'I', 'E', 'N', 'D'});
        Number(bytes, 0, 4);
        Number(bytes, 100, 4);
        Text(bytes, "【AIS_Chara】");
        Text(bytes, "1.0.0");
        Number(bytes, 0, 4);
        Text(bytes, "user");
        Text(bytes, "data");
        Number(bytes, table.size(), 4);
        Append(bytes, table);
        Number(bytes, payload.size(), 8);
        Append(bytes, payload);
        return CharacterDocument::FromBytes(bytes, L"test.png");
    }

    MP Plugin(int version, MP data)
    {
        return MP::Array({I(version), std::move(data)});
    }

    MP Property(std::string name, MP value)
    {
        return MP::Map({{std::string("ObjectType"), I(4)},
                        {std::string("Slot"), I(0)},
                        {std::string("CoordinateIndex"), I(0)},
                        {std::string("MaterialName"), S("test")},
                        {std::string("Property"), S(name)},
                        {std::string("Value"), std::move(value)}});
    }

    void Set(Parameter &p, const std::string &name, float value)
    {
        Parameter item;
        item.Name = name;
        item.Number = value;
        p.Children.push_back(item);
    }

    void Extensions(const fs::path &root)
    {
        auto bone = MP::Array(
            {S("cf_J_Test"), MP::Array({MP::Array({V({1, 2, 3}), F(.5f), V({.1f, .2f, .3f}), V({5, 6, 7})})})});
        auto texture = MP::Map({{std::string("ObjectType"), I(4)},
                                {std::string("Slot"), I(0)},
                                {std::string("CoordinateIndex"), I(0)},
                                {std::string("MaterialName"), S("test")},
                                {std::string("Property"), S("MainTex")},
                                {std::string("TexID"), I(5)},
                                {std::string("Scale"), V({2, 3})},
                                {std::string("Offset"), V({.1f, .2f})}});
        auto shader = MP::Map({{std::string("ObjectType"), I(4)},
                               {std::string("CoordinateIndex"), I(0)},
                               {std::string("MaterialName"), S("test")},
                               {std::string("ShaderName"), S("Shader/Test")},
                               {std::string("RenderQueue"), I(3100)}});
        Bytes image{0, 1, 2, 3, 4, 5};
        auto pack = [](MP value)
        {
            return MP::Binary(value.ToBytes());
        };
        auto extensions = MP::Map(
            {{std::string("KKABMPlugin.ABMData"),
              Plugin(2, MP::Map({{std::string("boneData"), pack(MP::Array({bone}))}}))},
             {std::string("com.deathweasel.bepinex.materialeditor"),
              Plugin(1,
                     MP::Map({{std::string("MaterialColorPropertyList"),
                               pack(MP::Array({Property("_Color", V({.2f, .4f, .6f, 1}))}))},
                              {std::string("MaterialFloatPropertyList"),
                               pack(MP::Array({Property("_Metallic", S("0.75"))}))},
                              {std::string("MaterialTexturePropertyList"), pack(MP::Array({texture}))},
                              {std::string("TextureDictionary"), pack(MP::Map({{std::int64_t(5), MP::Binary(image)}}))},
                              {std::string("MaterialShaderList"), pack(MP::Array({shader}))}}))},
             {std::string("KSOX"),
              Plugin(2, MP::Map({{std::string("Lookup"),
                                  pack(MP::Map({{std::string("0"), MP::Map({{std::string("1"), I(5)}})}}))},
                                 {std::string("_TextureID_5"), MP::Binary(image)}}))},
             {std::string("KCOX"),
              Plugin(1, MP::Map({{std::string("Overlays"),
                                  pack(MP::Map(
                                      {{std::int64_t(0), MP::Map({{std::string("ct_clothesTop"),
                                                                   MP::Array({MP::Scalar({}), B(true)})}})}}))}}))},
             {std::string("moreAccessories"),
              Plugin(0, MP::Map({{std::string("additionalAccessories"),
                                  S("<accessories><accessory type=\"351\" id=\"7\" color0r=\"0.25\" addMove00x=\"0.5\" "
                                    "parentKey=\"N_Head\" /></accessories>")}}))},
             {std::string("KK_InvisibleBody"), Plugin(0, MP::Map({{std::string("Visible"), B(false)}}))},
             {std::string("com.deathweasel.bepinex.eyecontrol"),
              Plugin(0, MP::Map({{std::string("EyeOpenMax"), F(.8f)}, {std::string("DisableBlinking"), B(true)}}))},
             {std::string("com.deathweasel.bepinex.uncensorselector"),
              Plugin(2, MP::Map({{std::string("BodyGUID"), S("body.test")}, {std::string("DisplayBalls"), B(false)}}))},
             {std::string("orange.spork.additionalaccessorycontrolsplugin"),
              Plugin(0, MP::Map({{std::string("accessoryData"),
                                  pack(MP::Array({MP::Array({B(false), I(20), I(0), I(0), S("Saved Name"), B(true),
                                                             MP::Array({MP::Array({I(20), I(1)})}), S("")})}))}}))}});
        CharacterDefinition definition;
        definition.Sex = 1;
        auto state = ReadCharacterExtensions(Document(extensions), definition, root / L"Data", root / L"Game");
        Check(definition.Diagnostics.empty(), "supported extensions have no diagnostics");
        Check(definition.BoneModifiers.size() == 1 && definition.BoneModifiers[0].Scale.y == 2 &&
                  definition.BoneModifiers[0].Position.z == .3f,
              "ABMX vectors loaded");
        Check(definition.MaterialEdits.size() == 5, "all material property classes loaded");
        Check(definition.MaterialEdits[0].Property == "_Color" && definition.MaterialEdits[1].Number == .75f,
              "material color and float values");
        Check(definition.MaterialEdits[2].Texture && ReadFile(definition.MaterialEdits[2].Texture->Path) == image &&
                  definition.MaterialEdits[2].Scale[1] == 3,
              "embedded material texture and transform");
        Check(definition.MaterialEdits[3].Kind == "queue" && definition.MaterialEdits[3].Number == 3100 &&
                  definition.MaterialEdits[4].Text == "Shader/Test",
              "shader record preserves render queue");
        Check(definition.Overlays.size() == 2 &&
                  definition.Overlays[0].Texture->Path == definition.MaterialEdits[2].Texture->Path &&
                  definition.Overlays[1].Replace && !definition.Overlays[1].Texture,
              "skin overlay cached once and empty clothing replace preserved");
        Check(!definition.BodyVisible && definition.EyeOpenMax == .8f && definition.Face.Float("disableBlinking") == 1,
              "eye and body state loaded");
        Check(state.Accessories.size() == 1 && state.Accessories[0].Slot == 20 &&
                  state.Accessories[0].Category == 351 && state.Accessories[0].Id == 7 &&
                  state.Accessories[0].Parent == "N_Head" && state.Accessories[0].Adjustments[0] == .5f &&
                  state.Accessories[0].Colors[0] == .25f,
              "extended accessory XML parameters");
        Check(state.Uncensor.String("BodyGUID") == "body.test" && state.AccessoryControls.at(20).Name == "Saved Name" &&
                  state.AccessoryControls.at(20).MatchHair,
              "uncensor and saved accessory metadata");
        auto bad = MP::Map(
            {{std::string("moreAccessories"),
              Plugin(0, MP::Map({{std::string("additionalAccessories"),
                                  S("<!DOCTYPE x [<!ENTITY y SYSTEM 'file:///C:/Windows/win.ini'>]><x>&y;</x>")}}))},
             {std::string("KK_InvisibleBody"), Plugin(0, MP::Map({{std::string("Visible"), B(false)}}))}});
        CharacterDefinition damaged;
        auto ignored = ReadCharacterExtensions(Document(bad), damaged, root / L"Data", root / L"Game");
        Check(!damaged.Diagnostics.empty() && ignored.Accessories.empty() && !damaged.BodyVisible,
              "DTD rejected while later extensions preserved");
        std::stop_source stop;
        stop.request_stop();
        bool cancelled = false;
        try
        {
            ReadCharacterExtensions(Document(extensions), definition, root / L"Data", root / L"Game", stop.get_token());
        }
        catch (const Cancelled &)
        {
            cancelled = true;
        }
        Check(cancelled, "extension cancellation");
        // MessagePack-CSharp 的 LZ4 数据使用扩展类型 99，与普通二进制类型不同。
        auto raw = MP::Array({bone}).ToBytes();
        Bytes compressed = I(raw.size()).ToBytes();
        compressed.push_back(0xf0);
        auto remaining = raw.size() - 15;
        while (remaining >= 255)
        {
            compressed.push_back(255);
            remaining -= 255;
        }
        compressed.push_back(static_cast<std::uint8_t>(remaining));
        Append(compressed, raw);
        Bytes encoded{0xc9};
        Number(encoded, compressed.size(), 4, true);
        encoded.push_back(99);
        Append(encoded, compressed);
        auto lz4 = MP::Map({{std::string("KKABMPlugin.ABMData"),
                             Plugin(2, MP::Map({{std::string("boneData"), MP::Binary(encoded)}}))}});
        CharacterDefinition decoded;
        ReadCharacterExtensions(Document(lz4), decoded, root / L"Data", root / L"Game");
        Check(decoded.Diagnostics.empty() && decoded.BoneModifiers.size() == 1 && decoded.BoneModifiers[0].Scale.y == 2,
              "ABMX extension99 LZ4 envelope");
    }

    void PushUp()
    {
        CharacterDefinition definition;
        definition.Sex = 1;
        Set(definition.Body, "bustSoftness", .8f);
        Parameter shape;
        shape.Name = "shapeValueBody";
        for (int i = 0; i < 33; i++)
            Set(shape, std::to_string(i), .5f);
        definition.Body.Children.push_back(shape);
        auto rig = std::make_shared<CharacterPart>();
        rig->Role = "rig";
        rig->Bones = {{"cf_J_Mune00_t_L", {}, {}, {1, 1, 1}, {0, 0, 0, 1}},
                      {"cf_J_Mune01_s_L", {}, {}, {1, 1, 1}, {0, 0, 0, 1}}};
        rig->UnmodifiedBones = rig->Bones;
        definition.Parts.push_back(rig);
        auto accessory = std::make_shared<CharacterPart>();
        accessory->Role = "accessory";
        accessory->Parent = "N_Tikubi_L";
        definition.Parts.push_back(accessory);
        CharacterExtensionState state;
        Parameter bra;
        bra.Name = "Bra";
        Set(bra, "ENABLE_PUSHUP", 1);
        Set(bra, "LIFT", .8f);
        Set(bra, "FIRMNESS", .9f);
        Set(bra, "HIDE_ACCESSORIES", 1);
        Set(bra, "SQUEEZE", .8f);
        state.PushUp.Children.push_back(bra);
        ApplyCharacterPushUp(definition, state, {}, true, false);
        Check(std::abs(rig->Bones[0].Position.y - .09f) < 1e-7f &&
                  rig->UnmodifiedBones[0].Position.y == rig->Bones[0].Position.y,
              "pushup adjusts both modified and original bone bases");
        Check(rig->Bones[1].Scale.x > 1 && definition.Parts.size() == 1, "pushup squeeze and accessory visibility");
        Check(std::abs(definition.Body.Float("effectiveBustSoftness") - .1f) < 1e-7f, "pushup firmness");
    }
}

int wmain(int argc, wchar_t **argv)
{
    fs::path root;
    try
    {
        wchar_t temp[MAX_PATH];
        GetTempPathW(MAX_PATH, temp);
        root = fs::path(temp) / Path("HS2Manager-CppExtensions-" + NewId());
        fs::create_directories(root);
        Extensions(root);
        PushUp();
        if (argc > 1)
        {
            auto document = CharacterDocument::Open(argv[1]);
            CharacterDefinition definition;
            definition.Sex = document.Sex();
            ReadCharacterExtensions(document, definition, root / L"Data", root / L"Game");
            Check(!definition.BoneModifiers.empty(), "real card ABMX modifiers missing");
            for (auto &warning : definition.Diagnostics)
                Check(warning.find("KKABMPlugin.ABMData") == std::string::npos, "real card ABMX diagnostic");
            std::cout << "Real card ABMX bones=" << definition.BoneModifiers.size() << "\n";
        }
        fs::remove_all(root);
        std::cout << "PASS " << checks << " native extension checks\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << "FAIL " << checks << ": " << error.what() << "\n" << Utf8(root) << "\n";
        return 1;
    }
}
