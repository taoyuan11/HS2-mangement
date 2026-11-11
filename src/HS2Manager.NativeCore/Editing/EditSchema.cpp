#include "Common/Core.h"
#include <algorithm>
#include <array>
#include <charconv>
#include <fstream>
#include <sstream>

namespace hs2
{
    const std::vector<std::pair<std::string, std::string>> &CharacterEditSchema::Categories()
    {
        static const std::vector<std::pair<std::string, std::string>> values{
            {"face", "脸部"},   {"body", "身体"},    {"hair", "发型"},
            {"makeup", "妆容"}, {"clothes", "服装"}, {"accessory", "配饰"}};
        return values;
    }

    const std::vector<std::string> &CharacterEditSchema::HairNames()
    {
        static const std::vector<std::string> values{"后发", "前发", "侧发", "附加发型"};
        return values;
    }

    const std::vector<std::string> &CharacterEditSchema::ClothesNames()
    {
        static const std::vector<std::string> values{"上衣", "下装", "内衣", "内裤", "手套", "连裤袜", "袜子", "鞋子"};
        return values;
    }

    const std::vector<CharacterChoice> &CharacterEditSchema::AccessoryTypes()
    {
        static const auto values = []()
        {
            std::vector<CharacterChoice> result{{std::int64_t(350), "无"}};
            int category = 351;
            for (auto label : {"头部", "耳部", "眼镜", "面部", "颈部", "肩部", "胸部", "腰部", "背部", "手臂", "手部",
                               "腿部", "胯部"})
                result.push_back({std::int64_t(category++), label});
            return result;
        }();
        return values;
    }

    const std::vector<CharacterChoice> &CharacterEditSchema::AccessoryParents()
    {
        static const auto values = []()
        {
            std::vector<CharacterChoice> result{{std::string(), "素材默认挂点"}};
            std::vector<std::pair<std::string, std::string>> plain{
                {"N_Hair_pony", "马尾"}, {"N_Head_top", "头顶"}, {"N_Hitai", "额头"},   {"N_Head", "头部"},
                {"N_Face", "面部"},      {"N_Megane", "眼镜"},   {"N_Nose", "鼻子"},    {"N_Mouth", "嘴部"},
                {"N_Neck", "颈部"},      {"N_Chest_f", "胸前"},  {"N_Chest", "胸部"},   {"N_Back", "背部"},
                {"N_Waist", "腰部"},     {"N_Waist_f", "腰前"},  {"N_Waist_b", "腰后"}, {"N_Dan", "生殖器"},
                {"N_Kokan", "胯部"},     {"N_Ana", "肛门"}};
            for (auto &[key, label] : plain)
                result.push_back({key, label});
            std::vector<std::pair<std::string, std::string>> paired{
                {"N_Hair_twin", "双马尾"}, {"N_Hair_pin", "发夹"}, {"N_Earring", "耳环"},  {"N_Tikubi", "乳头"},
                {"N_Back", "背部"},        {"N_Waist", "腰侧"},    {"N_Leg", "大腿"},      {"N_Knee", "膝盖"},
                {"N_Ankle", "脚踝"},       {"N_Foot", "脚"},       {"N_Shoulder", "肩膀"}, {"N_Elbo", "肘部"},
                {"N_Arm", "手臂"},         {"N_Wrist", "手腕"},    {"N_Hand", "手"},       {"N_Index", "食指"},
                {"N_Middle", "中指"},      {"N_Ring", "无名指"}};
            for (auto &[key, label] : paired)
            {
                result.push_back({key + "_L", "左" + label});
                result.push_back({key + "_R", "右" + label});
            }
            return result;
        }();
        return values;
    }

    int CharacterEditSchema::AutoClothingState(const std::string &category, const std::string &group)
    {
        if (category == "body")
            return 2;
        auto name = group.substr(0, group.find(" · "));
        auto &names = ClothesNames();
        auto slot = std::find(names.begin(), names.end(), name) - names.begin();
        return category == "clothes" && slot >= 2 && slot <= 6 ? 1 : 0;
    }

    std::vector<float> CharacterEditSchema::ColorValue(const CharacterDocument &doc, const std::string &path)
    {
        std::vector<float> result;
        for (auto &child : doc.Node(path).Children)
            result.push_back(std::visit(
                [](auto &value) -> float
                {
                    using T = std::decay_t<decltype(value)>;
                    if constexpr (std::is_arithmetic_v<T>)
                        return static_cast<float>(value);
                    else
                        throw std::runtime_error("颜色分量不是数值");
                },
                child.ScalarValue));
        return result;
    }

    int CharacterEditSchema::ExtraAccessoryCount(const CharacterDocument &doc)
    {
        return (std::max)(0, static_cast<int>(doc.Node("accessory.parts").Count()) - 20);
    }

    CharacterSliderLimits CharacterSliderLimits::Read(const fs::path &game)
    {
        auto plugins = game / L"BepInEx" / L"plugins";
        bool found = false;
        if (!fs::exists(plugins))
            return {};
        for (auto &file : fs::recursive_directory_iterator(plugins, fs::directory_options::none))
            if (file.is_regular_file() && file.path().filename() == L"HS2_SliderUnlocker.dll")
            {
                found = true;
                break;
            }
        if (!found)
            return {};
        double minimum = -1, maximum = 2;
        std::ifstream config(game / L"BepInEx" / L"config" / L"com.bepis.bepinex.sliderunlocker.cfg");
        std::string line;
        while (std::getline(config, line))
        {
            auto separator = line.find('=');
            if (separator == std::string::npos)
                continue;
            auto key = line.substr(0, separator), text = line.substr(separator + 1);
            auto trim = [](std::string s)
            {
                auto start = s.find_first_not_of(" \r\n\t");
                return start == std::string::npos ? std::string()
                                                  : s.substr(start, s.find_last_not_of(" \r\n\t") - start + 1);
            };
            key = trim(key);
            text = trim(text);
            double value{};
            auto [end, error] = std::from_chars(text.data(), text.data() + text.size(), value);
            if (error != std::errc() || end != text.data() + text.size())
                continue;
            if (key == "Minimum slider value")
                minimum = std::clamp(value, -500., 0.) / 100;
            if (key == "Maximum slider value")
                maximum = std::clamp(value, 100., 500.) / 100;
        }
        return {minimum, maximum, (std::min)(minimum, -5.), (std::max)(maximum, 5.)};
    }

    std::vector<CharacterField> CharacterEditSchema::Build(const CharacterDocument &doc, CharacterSliderLimits limits)
    {
        std::vector<CharacterField> fields;
        std::string category = "face", group = "整体";
        auto count = [&](const std::string &path)
        {
            return doc.Contains(path) ? static_cast<int>(doc.Node(path).Count()) : 0;
        };
        auto add = [&](const std::string &path, const std::string &label, CharacterFieldKind kind, double minimum = 0,
                       double maximum = 1, double scale = 100, std::vector<CharacterChoice> choices = {})
        {
            if (doc.Contains(path))
                fields.push_back({path, category, group, label, kind, minimum, maximum, scale, std::move(choices)});
        };
        auto number = [&](const std::string &path, const std::string &label, bool extended = false)
        {
            add(path, label, CharacterFieldKind::Number, extended ? limits.Minimum : 0, extended ? limits.Maximum : 1);
        };
        auto color = [&](const std::string &path, const std::string &label)
        {
            if (count(path) == 3 || count(path) == 4)
                add(path, label, CharacterFieldKind::Color);
        };
        auto toggle = [&](const std::string &path, const std::string &label)
        {
            add(path, label, CharacterFieldKind::Toggle);
        };
        auto asset = [&](const std::string &path, const std::string &label)
        {
            add(path, label, CharacterFieldKind::Asset);
        };
        auto choice =
            [&](const std::string &path, const std::string &label, const std::vector<CharacterChoice> &choices)
        {
            add(path, label, CharacterFieldKind::Choice, 0, 1, 100, choices);
        };
        auto layout = [&](const std::string &path, const std::string &label, const std::vector<std::string> &components,
                          bool extended = false)
        {
            for (int i = 0; i < (std::min)(static_cast<int>(components.size()), count(path)); i++)
                number(path + "[" + std::to_string(i) + "]", label + " " + components[i], extended);
        };
        auto paint = [&](const std::string &path, const std::string &label)
        {
            for (int i = 0; i < count(path); i++)
            {
                group = label + " " + std::to_string(i + 1);
                auto p = path + "[" + std::to_string(i) + "]";
                asset(p + ".id", "图案");
                color(p + ".color", "颜色");
                number(p + ".glossPower", "光泽");
                number(p + ".metallicPower", "金属感");
                if (path.starts_with("body."))
                    asset(p + ".layoutId", "位置预设");
                if (path.starts_with("face."))
                    add(p + ".layout", "位置预设", CharacterFieldKind::Preset);
                layout(p + ".layout", "图案", {"宽度", "高度", "横向位置", "纵向位置"});
                number(p + ".rotation", "旋转");
            }
        };
        const std::vector<std::pair<std::string, std::vector<std::string>>> faceShapes{
            {"整体", {"脸部宽度", "上半脸前后", "上半脸高度", "下半脸前后", "下半脸宽度"}},
            {"下颌",
             {"下颌宽度", "下颌高度", "下颌前后", "下颌角度", "下颌底部高度", "下巴尖宽度", "下巴尖高度",
              "下巴尖前后"}},
            {"脸颊", {"下脸颊高度", "下脸颊前后", "下脸颊宽度", "上脸颊高度", "上脸颊前后", "上脸颊宽度"}},
            {"眼形",
             {"眼睛高度", "眼睛间距", "眼睛前后", "眼睛宽度", "眼睛高度比例", "眼睛倾斜", "眼睛前后角度", "内眼角横向",
              "外眼角横向", "内眼角高度", "外眼角高度", "眼睑形状一", "眼睑形状二"}},
            {"鼻子",
             {"鼻子高度", "鼻子前后", "鼻子角度", "鼻子宽度", "鼻梁高度", "鼻梁宽度", "鼻梁形状", "鼻翼宽度",
              "鼻翼高度", "鼻翼前后", "鼻翼上下角度", "鼻翼左右角度", "鼻尖高度", "鼻尖角度", "鼻尖大小"}},
            {"嘴形", {"嘴部高度", "嘴部宽度", "嘴部厚度", "嘴部前后", "上唇形状", "下唇形状", "嘴角形状"}},
            {"耳朵", {"耳朵大小", "耳朵前后角度", "耳朵倾斜", "上耳形状", "耳垂形状"}}};
        int faceIndex = 0;
        for (auto &[label, labels] : faceShapes)
        {
            group = label;
            for (auto &text : labels)
                number("face.shapeValueFace[" + std::to_string(faceIndex++) + "]", text, true);
        }
        group = "脸型与皮肤";
        asset("face.headId", "头部模型");
        asset("face.skinId", "面部皮肤");
        asset("face.detailId", "皮肤细节");
        number("face.detailPower", "细节强度");
        group = "眉毛";
        asset("face.eyebrowId", "眉毛样式");
        color("face.eyebrowColor", "眉毛颜色");
        layout("face.eyebrowLayout", "眉毛", {"横向位置", "纵向位置", "宽度", "高度"});
        number("face.eyebrowTilt", "眉毛角度");
        group = "睫毛";
        asset("face.eyelashesId", "睫毛样式");
        color("face.eyelashesColor", "睫毛颜色");
        group = "眼睛";
        toggle("face.pupilSameSetting", "左右眼同步");
        number("face.pupilY", "瞳孔高度");
        number("face.whiteShadowScale", "眼白阴影");
        for (int eye = 0; eye < count("face.pupil"); eye++)
        {
            group = eye == 0 ? "左眼" : "右眼";
            auto path = "face.pupil[" + std::to_string(eye) + "]";
            color(path + ".whiteColor", "眼白颜色");
            asset(path + ".pupilId", "虹膜样式");
            color(path + ".pupilColor", "虹膜颜色");
            number(path + ".pupilW", "虹膜宽度");
            number(path + ".pupilH", "虹膜高度");
            number(path + ".pupilEmission", "虹膜发光");
            asset(path + ".blackId", "瞳孔样式");
            color(path + ".blackColor", "瞳孔颜色");
            number(path + ".blackW", "瞳孔宽度");
            number(path + ".blackH", "瞳孔高度");
        }
        group = "眼部高光";
        asset("face.hlId", "高光样式");
        color("face.hlColor", "高光颜色");
        layout("face.hlLayout", "高光", {"宽度", "高度", "横向位置", "纵向位置"});
        number("face.hlTilt", "高光角度");
        group = "痣";
        asset("face.moleId", "痣的样式");
        color("face.moleColor", "痣的颜色");
        add("face.moleLayout", "位置预设", CharacterFieldKind::Preset);
        layout("face.moleLayout", "痣", {"宽度", "高度", "横向位置", "纵向位置"});
        if (doc.Sex() == 0)
        {
            group = "胡须";
            asset("face.beardId", "胡须样式");
            color("face.beardColor", "胡须颜色");
        }
        category = "body";

        struct Shape
        {
            std::vector<int> Indices;
            std::string Group;
            std::vector<std::string> Labels;
        };

        std::vector<Shape> bodyShapes{
            {{0, 9}, "整体", {"身高", "头部大小"}},
            {{1, 2, 3, 4, 5, 6, 7, 8, 32},
             "胸部",
             {"胸部大小", "胸部高度", "胸部上下角度", "胸部间距", "胸部左右角度", "胸部形状", "乳晕隆起", "乳头大小",
              "乳头突出"}},
            {{10, 11, 12, 13, 14, 15, 16, 17},
             "上半身",
             {"颈部宽度", "颈部厚度", "肩部宽度", "肩部厚度", "上躯干宽度", "上躯干厚度", "下躯干宽度", "下躯干厚度"}},
            {{18, 19, 20, 21, 22, 23, 24},
             "腰臀",
             {"腰部高度", "上腰宽度", "上腰厚度", "下腰宽度", "下腰厚度", "臀部大小", "臀部角度"}},
            {{29, 30, 31}, "手臂", {"肩膀", "上臂粗细", "前臂粗细"}},
            {{25, 26, 27, 28}, "腿部", {"大腿根部", "大腿下部", "小腿粗细", "脚踝粗细"}}};
        for (auto &shape : bodyShapes)
        {
            if (shape.Group == "胸部" && doc.Sex() == 0)
                continue;
            group = shape.Group;
            for (std::size_t i = 0; i < shape.Indices.size(); i++)
                number("body.shapeValueBody[" + std::to_string(shape.Indices[i]) + "]", shape.Labels[i], true);
        }
        if (doc.Sex() == 1)
        {
            group = "胸部";
            number("body.bustSoftness", "胸部柔软度");
            number("body.bustWeight", "胸部重量");
        }
        group = "皮肤";
        asset("body.skinId", "身体皮肤");
        color("body.skinColor", "肤色");
        asset("body.detailId", "皮肤细节");
        number("body.detailPower", "细节强度");
        number("body.skinGlossPower", "皮肤光泽");
        number("body.skinMetallicPower", "皮肤金属感");
        group = "晒痕";
        asset("body.sunburnId", "晒痕样式");
        color("body.sunburnColor", "晒痕颜色");
        group = "乳头与乳晕";
        asset("body.nipId", "乳头样式");
        color("body.nipColor", "乳头颜色");
        number("body.nipGlossPower", "乳头光泽");
        number("body.areolaSize", "乳晕大小");
        group = "体毛";
        asset("body.underhairId", "体毛样式");
        color("body.underhairColor", "体毛颜色");
        group = "指甲";
        color("body.nailColor", "指甲颜色");
        number("body.nailGlossPower", "指甲光泽");
        paint("body.paintInfo", "身体彩绘");
        category = "makeup";
        group = "眼影";
        asset("face.makeup.eyeshadowId", "眼影样式");
        color("face.makeup.eyeshadowColor", "眼影颜色");
        number("face.makeup.eyeshadowGloss", "眼影光泽");
        group = "腮红";
        asset("face.makeup.cheekId", "腮红样式");
        color("face.makeup.cheekColor", "腮红颜色");
        number("face.makeup.cheekGloss", "腮红光泽");
        group = "唇妆";
        asset("face.makeup.lipId", "唇妆样式");
        color("face.makeup.lipColor", "唇妆颜色");
        number("face.makeup.lipGloss", "唇妆光泽");
        paint("face.makeup.paintInfo", "面部彩绘");
        category = "hair";
        group = "统一设置";
        toggle("hair.sameSetting", "四组发色同步");
        toggle("hair.autoSetting", "自动生成渐变与高光色");
        toggle("hair.ctrlTogether", "同组发束同步调整");
        choice("hair.shaderType", "头发渲染方式", {{std::int64_t(0), "柔和透明"}, {std::int64_t(1), "透明裁切"}});
        for (int i = 0; i < (std::min)(4, count("hair.parts")); i++)
        {
            group = HairNames()[i];
            auto part = "hair.parts[" + std::to_string(i) + "]";
            asset(part + ".id", "发型");
            color(part + ".baseColor", "基础颜色");
            color(part + ".topColor", "发根颜色");
            color(part + ".underColor", "发梢颜色");
            color(part + ".specular", "高光颜色");
            number(part + ".metallic", "金属感");
            number(part + ".smoothness", "光滑度");
            asset(part + ".meshType", "挑染图案");
            color(part + ".meshColor", "挑染颜色");
            layout(part + ".meshLayout", "挑染", {"宽度", "高度", "横向位置", "纵向位置"});
            for (int c = 0; c < count(part + ".acsColorInfo"); c++)
                color(part + ".acsColorInfo[" + std::to_string(c) + "].color", "发饰颜色 " + std::to_string(c + 1));
            if (doc.Contains(part + ".dictBundle"))
            {
                auto &map = doc.Node(part + ".dictBundle");
                for (std::size_t item = 0; item < map.Children.size(); item += 2)
                {
                    auto key = map.Children[item].Integer();
                    group = HairNames()[i] + " · 发束 " + std::to_string(key + 1);
                    auto bundle = part + ".dictBundle{" + std::to_string(key) + "}";
                    layout(bundle + ".moveRate", "位置", {"X", "Y", "Z"}, true);
                    layout(bundle + ".rotRate", "角度", {"X", "Y", "Z"}, true);
                    toggle(bundle + ".noShake", "固定发束");
                }
            }
        }
        category = "clothes";
        for (int slot = 0; slot < (std::min)(8, count("clothes.parts")); slot++)
        {
            if (doc.Sex() == 0 && (slot == 2 || slot == 3 || slot == 5 || slot == 6))
                continue;
            group = ClothesNames()[slot];
            auto part = "clothes.parts[" + std::to_string(slot) + "]";
            asset(part + ".id", "服装样式");
            number(part + ".breakRate", "破损程度");
            for (int option = 0; option < count(part + ".hideOpt"); option++)
                toggle(part + ".hideOpt[" + std::to_string(option) + "]", "隐藏可选部件 " + std::to_string(option + 1));
            for (int c = 0; c < count(part + ".colorInfo"); c++)
            {
                group = ClothesNames()[slot] + " · 颜色 " + std::to_string(c + 1);
                auto p = part + ".colorInfo[" + std::to_string(c) + "]";
                color(p + ".baseColor", "基础颜色");
                asset(p + ".pattern", "花纹图案");
                color(p + ".patternColor", "花纹颜色");
                layout(p + ".layout", "花纹", {"宽度", "高度", "横向位置", "纵向位置"});
                number(p + ".rotation", "花纹角度");
                number(p + ".glossPower", "光泽");
                number(p + ".metallicPower", "金属感");
            }
        }
        category = "accessory";
        for (int slot = 0; slot < (std::min)(20, count("accessory.parts")); slot++)
        {
            group = "配饰 " + std::to_string(slot + 1);
            auto part = "accessory.parts[" + std::to_string(slot) + "]";
            choice(part + ".type", "配饰类别", AccessoryTypes());
            auto type = doc.Node(part + ".type").Integer();
            if (type <= 350 || type > 363)
                continue;
            asset(part + ".id", "配饰样式");
            choice(part + ".parentKey", "挂点", AccessoryParents());
            choice(part + ".hideCategory", "服装联动",
                   {{std::int64_t(0), "不联动"},
                    {std::int64_t(1), "上衣"},
                    {std::int64_t(2), "下装"},
                    {std::int64_t(3), "内衣"},
                    {std::int64_t(4), "内裤"},
                    {std::int64_t(5), "手套"},
                    {std::int64_t(6), "连裤袜"},
                    {std::int64_t(7), "袜子"},
                    {std::int64_t(8), "鞋子"}});
            choice(part + ".hideTiming", "隐藏时机", {{std::int64_t(0), "完全脱下"}, {std::int64_t(1), "半脱或脱下"}});
            toggle(part + ".noShake", "固定配饰");
            toggle(part + ".partsOfHead", "作为头部部件");
            for (int c = 0; c < count(part + ".colorInfo"); c++)
            {
                auto p = part + ".colorInfo[" + std::to_string(c) + "]";
                auto n = std::to_string(c + 1);
                color(p + ".color", "颜色 " + n);
                number(p + ".glossPower", "光泽 " + n);
                number(p + ".metallicPower", "金属感 " + n);
                number(p + ".smoothnessPower", "光滑度 " + n);
            }
            if (doc.Contains(part + ".addMove"))
            {
                auto &matrix = doc.Node(part + ".addMove");
                auto moves =
                    part + (matrix.Count() == 3 && matrix.Children[2].Type == MessagePack::Kind::Array ? ".addMove[2]"
                                                                                                       : ".addMove");
                for (int vector = 0; vector < count(moves); vector++)
                {
                    int operation = vector % 3;
                    group = "配饰 " + std::to_string(slot + 1) + " · 调整 " + std::to_string(vector / 3 + 1);
                    for (int axis = 0; axis < 3; axis++)
                        add(moves + "[" + std::to_string(vector) + "][" + std::to_string(axis) + "]",
                            std::string(operation == 0   ? "位置"
                                        : operation == 1 ? "旋转"
                                                         : "缩放") +
                                " " + "XYZ"[axis],
                            CharacterFieldKind::Number,
                            operation == 0   ? -100
                            : operation == 1 ? -360
                                             : .01,
                            operation == 0   ? 100
                            : operation == 1 ? 360
                                             : 10,
                            1);
                }
            }
        }
        return fields;
    }
}
