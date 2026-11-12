#include "SharedAssets.h"
#include "Export/CardExport.h"
#include "Archive/ZipArchive.h"
#include <algorithm>

namespace hs2::creation
{
    namespace
    {
        MessagePack S(std::string value)
        {
            return MessagePack::Scalar(std::move(value));
        }

        MessagePack I(std::int64_t value)
        {
            return MessagePack::Scalar(value);
        }

        MessagePack Existing(const MessagePack &map, const MessagePack::Value &key)
        {
            auto found = map.Find(key);
            return found ? *found : MessagePack::Map();
        }

        void Number(Bytes &bytes, std::uint64_t value, int width)
        {
            for (int i = 0; i < width; ++i)
                bytes.push_back(static_cast<std::uint8_t>(value >> (8 * i)));
        }

        // 新素材包使用无压缩 ZIP；资源本身由 Unity LZ4 压缩，避免新增运行库。
        void Zip(const fs::path &root, const fs::path &output, std::stop_token token)
        {
            Bytes bytes, central;
            unsigned count = 0;
            for (auto &entry : fs::recursive_directory_iterator(root))
            {
                CheckCancelled(token);
                ExportPaths::NoLinks(entry.path());
                if (!entry.is_regular_file())
                    continue;
                auto name = Utf8(entry.path().lexically_relative(root));
                std::replace(name.begin(), name.end(), '\\', '/');
                if (name.ends_with(".manifest"))
                    continue;
                auto content = ReadFile(entry.path(), 512ull * 1024 * 1024, token);
                if (bytes.size() + content.size() > 1024ull * 1024 * 1024 || count >= 65535 || name.size() > 65535)
                    throw std::runtime_error("素材包超过 1 GiB 或 ZIP 条目限制");
                auto offset = bytes.size();
                auto crc = Crc32(content);
                Number(bytes, 0x04034b50, 4);
                Number(bytes, 20, 2);
                Number(bytes, 0x800, 2);
                Number(bytes, 0, 2);
                Number(bytes, 0, 4);
                Number(bytes, crc, 4);
                Number(bytes, content.size(), 4);
                Number(bytes, content.size(), 4);
                Number(bytes, name.size(), 2);
                Number(bytes, 0, 2);
                bytes.insert(bytes.end(), name.begin(), name.end());
                bytes.insert(bytes.end(), content.begin(), content.end());
                Number(central, 0x02014b50, 4);
                Number(central, 20, 2);
                Number(central, 20, 2);
                Number(central, 0x800, 2);
                Number(central, 0, 2);
                Number(central, 0, 4);
                Number(central, crc, 4);
                Number(central, content.size(), 4);
                Number(central, content.size(), 4);
                Number(central, name.size(), 2);
                Number(central, 0, 2);
                Number(central, 0, 2);
                Number(central, 0, 2);
                Number(central, 0, 2);
                Number(central, 0, 4);
                Number(central, offset, 4);
                central.insert(central.end(), name.begin(), name.end());
                ++count;
            }
            auto offset = bytes.size();
            bytes.insert(bytes.end(), central.begin(), central.end());
            Number(bytes, 0x06054b50, 4);
            Number(bytes, 0, 4);
            Number(bytes, count, 2);
            Number(bytes, count, 2);
            Number(bytes, central.size(), 4);
            Number(bytes, offset, 4);
            Number(bytes, 0, 2);
            WriteAtomic(output, bytes);
            ZipArchive check(output);
            for (auto &entry : check.Entries())
                check.Read(entry);
        }

        std::string Xml(std::string value)
        {
            std::string out;
            for (char c : value)
                if (c == '&')
                    out += "&amp;";
                else if (c == '<')
                    out += "&lt;";
                else if (c == '>')
                    out += "&gt;";
                else
                    out += c;
            return out;
        }

        CharacterDocument Overlay(CharacterDocument document, const Json &spec, Bytes png)
        {
            auto extensions = document.Extensions().value_or(MessagePack::Map());
            auto target = spec.at("target").get<std::string>();
            bool clothes = target == "clothes";
            auto key = std::string(clothes ? "KCOX" : "KSOX");
            auto existing = extensions.Find(key);
            auto data = existing ? existing->Require(std::int64_t(1)) : MessagePack::Map();
            int version = existing ? static_cast<int>(existing->Require(std::int64_t(0)).Integer()) : clothes ? 1 : 2;
            if (clothes)
            {
                if (version != 1)
                    throw std::runtime_error("KCOX 版本不支持写入");
                auto name = spec.at("clothing_name").get<std::string>();
                static const std::vector<std::string> names{"ct_clothesTop", "ct_clothesBot", "ct_inner_t",
                                                            "ct_inner_b",    "ct_gloves",     "ct_panst",
                                                            "ct_socks",      "ct_shoes"};
                if (std::find(names.begin(), names.end(), name) == names.end())
                    throw std::invalid_argument("服装覆盖目标无效");
                auto packed = data.Find(std::string("Overlays"));
                auto overlays = packed ? MessagePack::Parse(packed->Payload()) : MessagePack::Map();
                auto coordinate = Existing(overlays, std::int64_t(0));
                coordinate = coordinate.Set(name,
                                            MessagePack::Array({MessagePack::Binary(std::move(png)),
                                                                MessagePack::Scalar(spec.value("replace", false))}),
                                            true);
                overlays = overlays.Set(std::int64_t(0), coordinate, true);
                data = data.Set(std::string("Overlays"), MessagePack::Binary(overlays.ToBytes()), true);
            }
            else
            {
                int type = target == "face" ? 2 : target == "body" ? 1 : 0;
                if (!type)
                    throw std::invalid_argument("覆盖目标必须为 face/body/clothes");
                if (version < 0 || version > 2)
                    throw std::runtime_error("KSOX 版本不支持写入");
                auto lookup = MessagePack::Map();
                auto coordinate = MessagePack::Map();
                if (version == 2)
                {
                    if (auto packed = data.Find(std::string("Lookup")))
                        lookup = MessagePack::Parse(packed->Payload());
                    coordinate = Existing(lookup, std::int64_t(0));
                }
                else
                {
                    std::vector<std::string> names{"",          "BodyOver",  "FaceOver", "BodyUnder",
                                                   "FaceUnder", "EyeUnder",  "EyeOver",  "EyeUnderL",
                                                   "EyeOverL",  "EyeUnderR", "EyeOverR"};
                    for (int i = 1; i < static_cast<int>(names.size()); ++i)
                        if (auto texture = data.Find(names[i]); texture && texture->Type == MessagePack::Kind::Binary)
                        {
                            data = data.Set(std::string("_TextureID_") + std::to_string(i), *texture, true);
                            coordinate = coordinate.Set(std::int64_t(i), I(i), true);
                        }
                }
                // 分配未占用的正编号，兼容游戏插件的贴图索引实现。
                int textureId = 1;
                while (data.Find(std::string("_TextureID_") + std::to_string(textureId)))
                    if (++textureId > 1000000)
                        throw std::runtime_error("覆盖贴图编号耗尽");
                data = data.Set(std::string("_TextureID_") + std::to_string(textureId),
                                MessagePack::Binary(std::move(png)), true);
                coordinate = coordinate.Set(std::int64_t(type), I(textureId), true);
                lookup = lookup.Set(std::int64_t(0), coordinate, true);
                data = data.Set(std::string("Lookup"), MessagePack::Binary(lookup.ToBytes()), true);
                version = 2;
            }
            extensions = extensions.Set(key, MessagePack::Array({I(version), data}), true);
            return document.WithExtensions(extensions);
        }
    }

    CharacterDocument ApplyOverlay(CharacterDocument document, const Json &spec, Bytes png)
    {
        return Overlay(std::move(document), spec, std::move(png));
    }

    void WriteAssetZip(const fs::path &root, const fs::path &output, std::stop_token token)
    {
        Zip(root, output, token);
    }

    std::string XmlText(std::string value)
    {
        return Xml(std::move(value));
    }
}
