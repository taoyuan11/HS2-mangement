#include "Common/Core.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <map>
#include <regex>

namespace hs2
{
    namespace
    {
        const std::array<std::string, 2> Resolvers{"com.bepis.sideloader.universalautoresolver",
                                                   "EC.Core.Sideloader.UniversalAutoResolver"};
        const std::array<std::string, 4> Hair{"HairBack", "HairFront", "HairSide", "HairOption"};
        const std::array<std::string, 8> Clothes{"ClothesTop",    "ClothesBot",       "ClothesBra",   "ClothesShorts",
                                                 "ClothesGloves", "ClothesPantyhose", "ClothesSocks", "ClothesShoes"};
        using ReferenceEntry = std::pair<MessagePack, MessagePack>;

        std::vector<ReferenceEntry> ReadReferences(const MessagePack *plugin)
        {
            std::vector<ReferenceEntry> result;
            if (!plugin)
                return result;
            auto info = plugin->Require(std::int64_t(1)).Find(std::string("info"));
            if (!info)
                return result;
            if (info->Type != MessagePack::Kind::Array)
                throw std::runtime_error("Sideloader 引用格式不支持编辑");
            for (auto &node : info->Children)
            {
                if (node.Type != MessagePack::Kind::Binary)
                    throw std::runtime_error("Sideloader 引用不是二进制参数");
                result.emplace_back(node, MessagePack::Parse(node.Payload()));
            }
            return result;
        }

        std::string Property(const MessagePack &info)
        {
            return info.Require(std::string("Property")).Text();
        }

        bool Boolean(const CharacterDocument &doc, const std::string &path)
        {
            if (!doc.Contains(path))
                return false;
            auto value = std::get_if<bool>(&doc.Node(path).ScalarValue);
            return value && *value;
        }

        float Numeric(const MessagePack::Value &value)
        {
            return std::visit(
                [](auto &v) -> float
                {
                    using T = std::decay_t<decltype(v)>;
                    if constexpr (std::is_arithmetic_v<T>)
                        return static_cast<float>(v);
                    else
                        throw std::invalid_argument("预期数值");
                },
                value);
        }

        CharacterDocument SetColor(const CharacterDocument &document, const std::string &path,
                                   const std::vector<float> &color)
        {
            if (color.size() < 3 || color.size() > 4 ||
                std::any_of(color.begin(), color.end(),
                            [](float c)
                            {
                                return !std::isfinite(c) || c < 0 || c > 1;
                            }))
                throw std::invalid_argument("颜色分量必须介于 0 和 1");
            auto node = document.Node(path);
            for (std::size_t i = 0; i < (std::min)(node.Count(), color.size()); i++)
                node = node.Set(static_cast<std::int64_t>(i),
                                node.Require(static_cast<std::int64_t>(i)).WithScalar(color[i]));
            return document.WithNode(path, node);
        }

        std::array<float, 3> Hsv(const std::vector<float> &c)
        {
            float maximum = (std::max)({c[0], c[1], c[2]}), minimum = (std::min)({c[0], c[1], c[2]}),
                  delta = maximum - minimum;
            float h = delta == 0        ? 0
                      : maximum == c[0] ? (c[1] - c[2]) / delta
                      : maximum == c[1] ? 2 + (c[2] - c[0]) / delta
                                        : 4 + (c[0] - c[1]) / delta;
            return {std::fmod(h / 6.f + 1.f, 1.f), maximum == 0 ? 0 : delta / maximum, maximum};
        }

        std::vector<float> Rgb(float h, float s, float v)
        {
            float n = h * 6;
            int segment = static_cast<int>(n);
            float f = n - segment, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
            switch (segment % 6)
            {
            case 0:
                return {v, t, p, 1};
            case 1:
                return {q, v, p, 1};
            case 2:
                return {p, v, t, 1};
            case 3:
                return {p, q, v, 1};
            case 4:
                return {t, p, v, 1};
            default:
                return {v, p, q, 1};
            }
        }
    }

    std::optional<CharacterAssetBinding> CharacterAssetReferences::Describe(const CharacterDocument &document,
                                                                            const std::string &path)
    {
        int accessory = 350;
        if (std::regex_match(path, std::regex(R"(^accessory\.parts\[\d+\]\.id$)")))
            accessory = static_cast<int>(document.Node(path.substr(0, path.size() - 2) + "type").Integer());
        return DescribePath(document.Sex(), path, accessory);
    }

    std::optional<CharacterAssetBinding> CharacterAssetReferences::DescribePath(int sexValue, std::string path,
                                                                                int accessory)
    {
        if (path.starts_with("outfit."))
            path = path.substr(7);
        int sex = sexValue == 0 ? 100 : 200;
        std::smatch match;
        if (std::regex_match(path, match, std::regex(R"(^face\.pupil\[(\d+)\]\.(pupilId|blackId)$)")))
            return CharacterAssetBinding{match[2] == "pupilId" ? 317 : 318,
                                         "ChaFileFace." + std::string(match[2] == "pupilId" ? "Eye" : "EyeBlack") +
                                             std::to_string(std::stoi(match[1]) + 1)};
        if (std::regex_match(path, match, std::regex(R"(^(face\.makeup|body)\.paintInfo\[(\d+)\]\.(id|layoutId)$)")))
        {
            bool face = match[1] != "body", layout = match[3] == "layoutId";
            return CharacterAssetBinding{layout ? (face ? 7 : 8) : 313,
                                         std::string(face ? "MakeupInfo." : "ChaFileBody.") +
                                             (layout ? "PaintLayoutID" : "PaintID") +
                                             std::to_string(std::stoi(match[2]) + 1)};
        }
        if (std::regex_match(path, match, std::regex(R"(^(hair|clothes|accessory)\.parts\[(\d+)\]\.(.+)$)")))
        {
            int slot = std::stoi(match[2]);
            std::string field = match[3];
            if (match[1] == "hair" && slot < 4 && (field == "id" || field == "meshType"))
                return CharacterAssetBinding{field == "id" ? 300 + slot : 306,
                                             "ChaFileHair." + Hair[slot] + (field == "id" ? "" : "Mesh")};
            if (match[1] == "clothes" && slot < 8)
            {
                if (field == "id")
                    return CharacterAssetBinding{
                        ((slot == 2 || slot == 3 || slot == 5 || slot == 6) ? 200 : sex) + 40 + slot,
                        "outfit.ChaFileClothes." + Clothes[slot] +
                            ((sexValue == 0 && (slot == 0 || slot == 1 || slot == 4 || slot == 7)) ? "M" : "")};
                std::smatch pattern;
                if (std::regex_match(field, pattern, std::regex(R"(^colorInfo\[(\d+)\]\.pattern$)")))
                    return CharacterAssetBinding{348, "outfit.ChaFileClothes." + Clothes[slot] + "Pattern" +
                                                          pattern[1].str()};
            }
            if (match[1] == "accessory" && field == "id")
                return CharacterAssetBinding{accessory, "outfit.accessory" + std::to_string(slot) +
                                                            ".ChaFileAccessory.PartsInfo.id"};
        }
        std::map<std::string, CharacterAssetBinding> bindings{
            {"face.headId", {sex + 10, "ChaFileFace.headId"}},
            {"face.skinId", {sex + 11, "ChaFileFace.skinId"}},
            {"face.detailId", {sex + 12, "ChaFileFace.detailId"}},
            {"face.beardId", {121, "ChaFileFace.beardId"}},
            {"face.eyebrowId", {314, "ChaFileFace.eyebrowId"}},
            {"face.eyelashesId", {315, "ChaFileFace.eyelashesId"}},
            {"face.hlId", {319, "ChaFileFace.hlId"}},
            {"face.moleId", {323, "ChaFileFace.moleId"}},
            {"face.makeup.cheekId", {320, "MakeupInfo.cheekId"}},
            {"face.makeup.eyeshadowId", {316, "MakeupInfo.eyeshadowId"}},
            {"face.makeup.lipId", {322, "MakeupInfo.lipId"}},
            {"body.skinId", {sex + 31, "ChaFileBody.skinId"}},
            {"body.detailId", {sex + 32, "ChaFileBody.detailId"}},
            {"body.sunburnId", {sex + 33, "ChaFileBody.sunburnId"}},
            {"body.nipId", {334, "ChaFileBody.nipId"}},
            {"body.underhairId", {335, "ChaFileBody.underhairId"}}};
        auto found = bindings.find(path);
        return found == bindings.end() ? std::nullopt : std::optional(found->second);
    }

    std::vector<ModReference> CharacterAssetReferences::All(const CharacterDocument &document)
    {
        std::map<std::string, ModReference> references;
        if (document.Extensions())
            for (auto &resolver : Resolvers)
                for (auto &[raw, info] : ReadReferences(document.Extensions()->Find(resolver)))
                {
                    auto property = Property(info);
                    references[property] = {info.Require(std::string("ModID")).Text(),
                                            static_cast<int>(info.Require(std::string("Slot")).Integer()),
                                            static_cast<int>(info.Require(std::string("CategoryNo")).Integer()),
                                            property};
                }
        std::vector<ModReference> result;
        for (auto &[property, reference] : references)
            result.push_back(reference);
        return result;
    }

    std::optional<ModReference> CharacterAssetReferences::Find(const CharacterDocument &document,
                                                               const std::string &path)
    {
        auto binding = Describe(document, path);
        if (!binding || !document.Extensions())
            return {};
        for (auto i = Resolvers.rbegin(); i != Resolvers.rend(); ++i)
            for (auto &[raw, info] : ReadReferences(document.Extensions()->Find(*i)))
                if (Property(info) == binding->Property)
                    return ModReference{info.Require(std::string("ModID")).Text(),
                                        static_cast<int>(info.Require(std::string("Slot")).Integer()),
                                        binding->Category, binding->Property};
        return {};
    }

    CharacterDocument CharacterAssetReferences::Select(const CharacterDocument &document, const std::string &path,
                                                       const CharacterAsset &asset)
    {
        auto binding = Describe(document, path);
        if (!binding)
            throw std::invalid_argument("此字段不是素材选择项");
        if (binding->Category != asset.Category)
            throw std::invalid_argument("素材类别与参数不匹配");
        auto next = document.WithValue(path, static_cast<std::int64_t>(asset.Id));
        auto extensions = next.Extensions().value_or(MessagePack::Map());
        for (auto &resolver : Resolvers)
        {
            auto existing = extensions.Find(resolver);
            if (!existing && (resolver != Resolvers[0] || asset.Guid.empty()))
                continue;
            auto plugin =
                existing ? *existing : MessagePack::Array({MessagePack::Scalar(std::int64_t(0)), MessagePack::Map()});
            std::vector<MessagePack> entries;
            for (auto &[raw, info] : ReadReferences(&plugin))
                if (Property(info) != binding->Property)
                    entries.push_back(raw);
            if (!asset.Guid.empty())
            {
                auto info = MessagePack::Map(
                    {{std::string("ModID"), MessagePack::Scalar(asset.Guid)},
                     {std::string("Slot"), MessagePack::Scalar(static_cast<std::int64_t>(asset.Id))},
                     {std::string("LocalSlot"), MessagePack::Scalar(static_cast<std::int64_t>(asset.Id))},
                     {std::string("Property"), MessagePack::Scalar(binding->Property)},
                     {std::string("CategoryNo"), MessagePack::Scalar(static_cast<std::int64_t>(asset.Category))},
                     {std::string("Name"), MessagePack::Scalar(asset.Source)},
                     {std::string("Author"), MessagePack::Scalar(std::string())},
                     {std::string("Website"), MessagePack::Scalar(std::string())}});
                entries.push_back(MessagePack::Binary(info.ToBytes()));
            }
            auto data =
                plugin.Require(std::int64_t(1)).Set(std::string("info"), MessagePack::Array(std::move(entries)), true);
            extensions = extensions.Set(resolver, plugin.Set(std::int64_t(1), data), true);
        }
        return next.WithExtensions(extensions.Count() == 0 && !document.Extensions() ? std::nullopt
                                                                                     : std::optional(extensions));
    }

    CharacterDocument CharacterAssetReferences::Restore(const CharacterDocument &draft,
                                                        const CharacterDocument &baseline, const std::string &path)
    {
        if (!draft.Extensions())
            return draft;
        auto binding = Describe(baseline, path);
        auto matches = [&](const std::string &property)
        {
            if (binding)
                return property == binding->Property;
            std::string prefix = path == "face"          ? "ChaFileFace."
                                 : path == "face.makeup" ? "MakeupInfo."
                                 : path == "body"        ? "ChaFileBody."
                                 : path == "hair"        ? "ChaFileHair."
                                 : path == "clothes"     ? "outfit.ChaFileClothes."
                                 : path == "accessory"   ? "outfit.accessory"
                                                         : "";
            return !prefix.empty() && property.starts_with(prefix);
        };
        auto extras = *draft.Extensions();
        for (auto &resolver : Resolvers)
        {
            auto pointer = extras.Find(resolver);
            if (!pointer)
                continue;
            auto plugin = *pointer;
            auto original = baseline.Extensions() ? baseline.Extensions()->Find(resolver) : nullptr;
            std::vector<MessagePack> entries;
            for (auto &[raw, info] : ReadReferences(&plugin))
                if (!matches(Property(info)))
                    entries.push_back(raw);
            std::size_t index = 0;
            for (auto &[raw, info] : ReadReferences(original))
            {
                if (matches(Property(info)))
                    entries.insert(entries.begin() + (std::min)(index, entries.size()), raw);
                index++;
            }
            if (!original && entries.empty())
                extras = extras.Remove(resolver);
            else
                extras = extras.Set(
                    resolver, plugin.Set(std::int64_t(1),
                                         plugin.Require(std::int64_t(1))
                                             .Set(std::string("info"), MessagePack::Array(std::move(entries)), true)));
        }
        return draft.WithExtensions(extras.Count() == 0 && !baseline.Extensions() ? std::nullopt
                                                                                  : std::optional(extras));
    }

    CharacterDocument CharacterEditRules::SetValue(const CharacterDocument &document, const std::string &path,
                                                   MessagePack::Value value)
    {
        auto next = document.WithValue(path, value);
        std::smatch match;
        if (std::regex_match(path, match, std::regex(R"(^face\.pupil\[([01])\](.+)$)")) &&
            Boolean(document, "face.pupilSameSetting"))
            next =
                next.WithValue("face.pupil[" + std::to_string(std::stoi(match[1]) ^ 1) + "]" + match[2].str(), value);
        if (std::regex_match(path, match, std::regex(R"(^hair\.parts\[(\d+)\]\.(metallic|smoothness)$)")) &&
            Boolean(document, "hair.sameSetting"))
            for (std::size_t i = 0; i < document.Node("hair.parts").Count(); i++)
                next = next.WithValue("hair.parts[" + std::to_string(i) + "]." + match[2].str(), value);
        if (std::regex_match(path, match,
                             std::regex(R"(^(hair\.parts\[\d+\]\.dictBundle)\{\d+\}\.(moveRate|rotRate)\[[012]\]$)")) &&
            Boolean(document, "hair.ctrlTogether"))
        {
            std::string root = match[1], vector = match[2];
            auto node = next.Node(path.substr(0, path.rfind('[')));
            auto &map = document.Node(root);
            for (std::size_t i = 0; i < map.Children.size(); i += 2)
                next = next.WithNode(root + "{" + std::to_string(map.Children[i].Integer()) + "}." + vector, node);
        }
        if (std::regex_match(path, std::regex(R"(^accessory\.parts\[\d+\]\.type$)")))
        {
            auto root = path.substr(0, path.size() - 5);
            int category = static_cast<int>(Numeric(value));
            next =
                CharacterAssetReferences::Select(next, root + ".id", CharacterAsset{category, 0, "无", "", "原版", ""});
            next = next.WithValue(root + ".parentKey", std::string()).WithValue(root + ".noShake", false);
            auto moves = next.Node(root + ".addMove");
            auto p = root + ".addMove";
            if (moves.Count() == 3 && moves.Children[2].Type == MessagePack::Kind::Array)
            {
                moves = moves.Children[2];
                p += "[2]";
            }
            for (std::size_t vector = 0; vector < moves.Count(); vector++)
                for (int axis = 0; axis < 3; axis++)
                    next = next.WithValue(p + "[" + std::to_string(vector) + "][" + std::to_string(axis) + "]",
                                          vector % 3 == 2 ? 1.f : 0.f);
        }
        return next;
    }

    CharacterDocument CharacterEditRules::Color(const CharacterDocument &document, const std::string &path,
                                                const std::vector<float> &color)
    {
        auto next = SetColor(document, path, color);
        std::smatch match;
        if (std::regex_match(path, match, std::regex(R"(^face\.pupil\[([01])\](.+)$)")) &&
            Boolean(document, "face.pupilSameSetting"))
            next =
                SetColor(next, "face.pupil[" + std::to_string(std::stoi(match[1]) ^ 1) + "]" + match[2].str(), color);
        if (!std::regex_match(path, match,
                              std::regex(R"(^hair\.parts\[(\d+)\]\.(baseColor|topColor|underColor|specular)$)")))
            return next;
        std::string field = match[2];
        int selected = std::stoi(match[1]);
        for (std::size_t i = 0; i < document.Node("hair.parts").Count(); i++)
        {
            if (static_cast<int>(i) != selected && !Boolean(document, "hair.sameSetting"))
                continue;
            auto root = "hair.parts[" + std::to_string(i) + "].";
            next = SetColor(next, root + field, color);
            if (field != "baseColor" || !Boolean(document, "hair.autoSetting"))
                continue;
            auto [h, s, v] = Hsv(color);
            next = SetColor(next, root + "topColor", Rgb(h, s, (std::max)(v - .15f, 0.f)));
            next = SetColor(next, root + "underColor", Rgb(h, (std::max)(s - .1f, 0.f), (std::min)(v + .44f, 1.f)));
            next = SetColor(next, root + "specular", Rgb(h, s, (std::min)(v + .17f, 1.f)));
        }
        return next;
    }

    CharacterDocument CharacterEditRules::Select(const CharacterDocument &document, const std::string &path,
                                                 const CharacterAsset &asset)
    {
        auto next = CharacterAssetReferences::Select(document, path, asset);
        std::smatch match;
        if (std::regex_match(path, match, std::regex(R"(^face\.pupil\[([01])\](.+)$)")) &&
            Boolean(document, "face.pupilSameSetting"))
            next = CharacterAssetReferences::Select(
                next, "face.pupil[" + std::to_string(std::stoi(match[1]) ^ 1) + "]" + match[2].str(), asset);
        if (std::regex_match(path, std::regex(R"(^hair\.parts\[\d+\]\.id$)")))
        {
            auto root = path.substr(0, path.size() - 3);
            next = next.WithValue(root + ".bundleId", static_cast<std::int64_t>(asset.Id))
                       .WithNode(root + ".dictBundle", MessagePack::Map());
        }
        if (std::regex_match(path, std::regex(R"(^accessory\.parts\[\d+\]\.id$)")))
        {
            auto root = path.substr(0, path.size() - 3);
            next = next.WithValue(root + ".parentKey", asset.Parent).WithValue(root + ".noShake", false);
        }
        return next;
    }
}
