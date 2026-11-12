#include "Agent.h"
#include "Model/ModelExport.h"
#include "Tools/OwnedToolProcess.h"
#include "Export/CardExport.h"
#include "Archive/ZipArchive.h"
#include <fstream>
#include <algorithm>

namespace hs2::agent
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

    Json Service::PrepareAsset(Json &session, const Json &, std::stop_token token)
    {
        auto document = Document(session);
        auto card = CardParser().Parse(document.SourcePath(), token);
        // FBX SDK 与旧版 Unity 的部分文件 API 不支持深层路径；缩短本机工具工作目录。
        auto output = data_ / "Reference" / Path(NewId());
        ModelExportOptions options;
        options.Clothing = 2;
        options.Accessories = false;
        auto report =
            ModelExportService(runtime_).Export({{card, document, session.at("revision")}}, game_, data_ / "Cache",
                                                output, options, {}, token, Resources(session, token));
        if (report.Items.empty() || report.Items.front().Status != ModelExportStatus::Complete)
            throw std::runtime_error("建模参考导出失败：" + report.Summary() +
                                     (report.Items.empty() ? std::string{} : "；" + report.Items.front().Message));
        auto model = Path(report.Items.front().Directory) / "character.fbx";
        auto work = output / "Blender";
        fs::create_directories(work);
        Json spec{{"mode", "reference"}, {"reference_model", Utf8(model)}, {"work_directory", Utf8(work)}};
        WriteJson(work / "spec.json", spec);
        auto blender = Path(config_.value("blender_path", ""));
        if (!fs::is_regular_file(blender))
            throw std::runtime_error("未配置 Blender；参考模型已保留：" + Utf8(model));
        int code = RunOwnedTool(
            blender,
            {L"--background", L"--factory-startup", L"--python-exit-code", L"1", L"--python",
             (runtime_ / "Resources/Agent/BlenderAsset.py").wstring(), L"--", (work / "spec.json").wstring()},
            work, std::chrono::seconds(config_.value("tool_timeout_seconds", 900)), work / "blender.log", token);
        if (code)
            throw std::runtime_error("Blender UV 参考导出失败；见 " + Utf8(work / "blender.log"));
        auto result = ReadJson(work / "result.json");
        result["reference_model"] = Utf8(model);
        session["reference_assets"] = result;
        WriteJson(SessionPath(session.at("session_id")) / "session.json", session);
        return result;
    }

    Json Service::BuildAsset(Json &session, const Json &args, std::stop_token token)
    {
        auto spec = args.at("spec");
        auto kind = spec.at("kind").get<std::string>();
        if (kind != "texture" && kind != "accessory" && kind != "hair" && kind != "clothes")
            throw std::invalid_argument("未知素材类型");
        auto key = spec.at("asset_key").get<std::string>();
        if (key.empty() || key.size() > 128)
            throw std::invalid_argument("asset_key 无效");
        auto attempts = session.value("build_attempts", Json::object());
        int count = attempts.value(key, 0);
        if (count >= 3)
            throw std::runtime_error("ASSET_BUILD_LIMIT：该素材已达三次尝试，草稿保留");
        attempts[key] = count + 1;
        session["build_attempts"] = attempts;
        WriteJson(SessionPath(session.at("session_id")) / "session.json", session);
        auto document = Document(session);
        if (kind == "texture")
        {
            auto source = Path(spec.at("source_image"));
            ValidateImage(source);
            auto bytes = ReadFile(source, 32 * 1024 * 1024, token);
            if (CardParser::FindPngEnd(bytes) != bytes.size())
                throw std::invalid_argument("覆盖贴图必须为无尾部数据的 PNG");
            auto hash = Sha256(bytes);
            auto next = ApplyOverlay(document, spec, bytes);
            auto trial = session;
            auto render = Render(trial, next, {{"views", {"front", "face"}}, {"probe", true}}, token);
            if (!render.at("diagnostics").empty())
                return {{"status", "needs_work"}, {"render", render}};
            WriteAtomic(data_ / "Assets" / Path(hash + ".png"), bytes);
            Json record{{"kind", kind},
                        {"hash", hash},
                        {"asset_key", key},
                        {"spec", spec},
                        {"path", Utf8(data_ / "Assets" / Path(hash + ".png"))},
                        {"mime_type", "image/png"}};
            WriteJson(data_ / "Assets" / Path(hash + ".json"), record);
            session["assets"].push_back(record);
            SaveRevision(session, next, args);
            return {{"status", "applied"}, {"session", session}, {"render", render}};
        }
        auto blender = Path(config_.value("blender_path", "")), unity = Path(config_.value("unity_path", ""));
        auto project = Path(config_.value("modding_project", ""));
        if (!fs::is_regular_file(blender) || !fs::is_regular_file(unity) || !fs::is_directory(project / "Assets"))
            throw std::runtime_error("缺少 Blender、Unity 2018.4.11f1 或 ModdingTool 工程；草稿保留");
        auto version = ReadFile(project / "ProjectSettings/ProjectVersion.txt", 4096);
        if (std::string(version.begin(), version.end()).find("2018.4.11f1") == std::string::npos)
            throw std::runtime_error("ModdingTool 工程必须固定到 Unity 2018.4.11f1");
        auto work = data_ / "Builds" / Path(NewId());
        fs::create_directories(work);
        spec["work_directory"] = Utf8(work);
        if (!session.contains("reference_assets"))
            throw std::runtime_error("请先运行 hs2_prepare_asset 导出目标人物参考");
        spec["reference_model"] = session["reference_assets"]["reference_model"];
        spec["mode"] = "build";
        auto target = spec.at("target_path").get<std::string>();
        auto binding = CharacterAssetReferences::Describe(document, target);
        if (!binding)
            throw std::invalid_argument("目标槽位不能选择素材");
        bool categoryMatches = kind == "hair"      ? target.starts_with("hair.")
                               : kind == "clothes" ? target.starts_with("clothes.")
                                                   : target.starts_with("accessory.");
        if (!categoryMatches)
            throw std::invalid_argument("素材类型和目标槽位不匹配");
        spec["category"] = binding->Category;
        spec["sex"] = document.Sex();
        WriteJson(work / "spec.json", spec);
        auto timeout = std::chrono::seconds(config_.value("tool_timeout_seconds", 900));
        if (RunOwnedTool(blender,
                         {L"--background", L"--factory-startup", L"--python-exit-code", L"1", L"--python",
                          (runtime_ / "Resources/Agent/BlenderAsset.py").wstring(), L"--",
                          (work / "spec.json").wstring()},
                         work, timeout, work / "blender.log", token))
            throw std::runtime_error("Blender 素材适配失败；见 " + Utf8(work / "blender.log"));
        auto geometry = ReadJson(work / "result.json");
        if (!geometry.at("valid").get<bool>())
            throw std::runtime_error("素材几何或权重检查未通过");
        auto fingerprintSpec = spec;
        fingerprintSpec.erase("work_directory");
        auto fingerprint = fingerprintSpec.dump() + HashFile(work / "asset.fbx", token);
        fingerprint += HashFile(runtime_ / "Resources/Agent/BlenderAsset.py", token);
        fingerprint += HashFile(runtime_ / "Resources/Agent/HS2AgentBuilder.cs", token);
        fingerprint += HashFile(project / "ProjectSettings/ProjectVersion.txt", token);
        if (fs::is_regular_file(project / "Packages/agent-package-lock.json"))
            fingerprint += HashFile(project / "Packages/agent-package-lock.json", token);
        for (auto &texture : geometry.value("textures", Json::array()))
            fingerprint += HashFile(Path(texture), token);
        auto hash = Sha256(Bytes(fingerprint.begin(), fingerprint.end()));
        spec["guid"] = "hs2agent." + hash;
        spec["slot_id"] = 100000 + static_cast<int>(std::stoul(hash.substr(0, 6), nullptr, 16));
        spec["model"] = Utf8(work / "asset.fbx");
        spec["package_directory"] = Utf8(work / "Package");
        // 在每次构建独享的工程副本中导入，避免污染用户工程和抢占 Library 锁。
        auto copy = work / "Project";
        fs::create_directories(copy);
        for (auto &root : {"Assets", "Packages", "ProjectSettings"})
            if (fs::exists(project / root))
                for (auto it = fs::recursive_directory_iterator(project / root);
                     it != fs::recursive_directory_iterator(); ++it)
                {
                    CheckCancelled(token);
                    ExportPaths::NoLinks(it->path());
                    auto dest = copy / it->path().lexically_relative(project);
                    if (it->is_directory())
                        fs::create_directories(dest);
                    else if (it->is_regular_file())
                    {
                        fs::create_directories(dest.parent_path());
                        fs::copy_file(it->path(), dest);
                    }
                }
        WriteAtomic(copy / "Assets/Editor/HS2AgentBuilder.cs",
                    ReadFile(runtime_ / "Resources/Agent/HS2AgentBuilder.cs"));
        WriteJson(work / "spec.json", spec);
        if (RunOwnedTool(unity,
                         {L"-batchmode", L"-quit", L"-projectPath", copy.wstring(), L"-executeMethod",
                          L"HS2AgentBuilder.Build", L"-hs2AgentSpec", (work / "spec.json").wstring(), L"-logFile",
                          (work / "unity.log").wstring()},
                         work, timeout, work / "unity-process.log", token))
        {
            auto logBytes = fs::is_regular_file(work / "unity.log")
                                ? ReadFile(work / "unity.log", 16 * 1024 * 1024, token)
                                : Bytes{};
            std::string log(logBytes.begin(), logBytes.end());
            if (log.find("not been activated with a valid License") != std::string::npos ||
                log.find("Failed to activate/update license") != std::string::npos)
                throw std::runtime_error(
                    "UNITY_LICENSE_REQUIRED：请在 Unity Editor 完成账号登录与许可激活；草稿保留。日志：" +
                    Utf8(work / "unity.log"));
            throw std::runtime_error("Unity 构建失败；见 " + Utf8(work / "unity.log"));
        }
        auto package = work / "Package";
        std::string manifest =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><manifest schema-ver=\"1\"><guid>" +
            spec.at("guid").get<std::string>() + "</guid><name>" + Xml(spec.value("name", key)) +
            "</name><version>1.0</version><author>HS2Agent</author><game>HoneySelect2</game></manifest>";
        WriteAtomic(package / "manifest.xml", Bytes(manifest.begin(), manifest.end()));
        auto candidate = work / Path(hash + ".zipmod");
        Zip(package, candidate, token);
        auto trial = session;
        trial["candidate_mod"] = Utf8(candidate);
        CharacterCatalog catalog(game_, data_ / "Cache", runtime_ / "classdata.tpk", Resources(trial, token));
        catalog.Load({}, token);
        auto entry = catalog.Resolve({spec["guid"], spec["slot_id"], spec["category"], binding->Property});
        auto next = catalog.Select(document, target, entry);
        auto render = Render(trial, next, {{"views", {"front", "side", "back", "face"}}, {"probe", true}}, token);
        if (!render.at("diagnostics").empty())
            return {{"status", "needs_work"}, {"render", render}, {"work_directory", Utf8(work)}};
        Json checks = Json::array();
        if (kind == "hair" || kind == "clothes")
        {
            auto motion =
                Render(trial, next, {{"views", {"front", "side"}}, {"sample_time", .5}, {"probe", true}}, token);
            if (!motion.at("diagnostics").empty())
                throw std::runtime_error("素材动态采样验证失败");
            checks.push_back({{"kind", "motion"}, {"render", motion}});
            auto fields = CharacterEditSchema::Build(next, CharacterSliderLimits::Read(game_));
            auto body = std::find_if(fields.begin(), fields.end(),
                                     [](const auto &field)
                                     {
                                         return field.Category == "body" && field.Kind == CharacterFieldKind::Number &&
                                                field.Path.starts_with("body.shapeValueBody[");
                                     });
            if (body == fields.end())
                throw std::runtime_error("找不到体型适配测试字段");
            auto value = ScalarJson(next.Node(body->Path).ScalarValue).get<double>();
            for (int sign : {-1, 1})
            {
                double changed =
                    std::clamp(value + sign * (body->Maximum - body->Minimum) * .1, body->Minimum, body->Maximum);
                auto variation = CharacterEditRules::SetValue(next, body->Path, changed);
                auto frame = Render(trial, variation, {{"views", {"front"}}, {"probe", true}}, token);
                if (!frame.at("diagnostics").empty())
                    throw std::runtime_error("素材体型适配验证失败");
                checks.push_back(
                    {{"kind", "body_variation"}, {"path", body->Path}, {"value", changed}, {"render", frame}});
            }
        }
        if (kind == "clothes")
            for (int state : {1, 2})
            {
                auto check =
                    Render(trial, next, {{"views", {"front"}}, {"clothing_state", state}, {"probe", true}}, token);
                if (!check.at("diagnostics").empty())
                    throw std::runtime_error("服装穿脱状态验证失败");
                checks.push_back({{"kind", "clothing_state"}, {"state", state}, {"render", check}});
            }
        auto installed = data_ / "Assets" / candidate.filename();
        if (!fs::exists(installed))
            WriteAtomic(installed, ReadFile(candidate, 1024ull * 1024 * 1024, token));
        Json record{{"kind", kind},
                    {"asset_key", key},
                    {"guid", spec["guid"]},
                    {"hash", hash},
                    {"path", Utf8(installed)},
                    {"geometry", geometry},
                    {"checks", checks},
                    {"game_validation", "not_run"}};
        WriteJson(data_ / "Assets" / Path(hash + ".json"), record);
        session["assets"].push_back(record);
        SaveRevision(session, next, args);
        return {{"status", "applied"}, {"session", session}, {"asset", record}, {"render", render}};
    }
}
