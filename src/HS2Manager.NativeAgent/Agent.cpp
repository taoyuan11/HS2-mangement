#include "Agent.h"
#include "VisualReview.h"
#include "AssetGeometry.h"
#include "AssetTargets.h"
#include "Export/CardExport.h"
#include <windows.h>
#include <objbase.h>
#include <algorithm>
#include <cmath>
#include <set>
#include "PostProcessing/NativeFullscreenPass.h"
#include "Images/ImageFiles.h"

namespace hs2::agent
{
    namespace
    {
        bool Id(std::string_view value)
        {
            return value.size() == 32 && value.find_first_not_of("0123456789abcdefABCDEF") == value.npos;
        }

        bool Async(std::string_view name)
        {
            return name == "hs2_render" || name == "hs2_probe_fields" || name == "hs2_prepare_asset" ||
                   name == "hs2_build_asset" || name == "hs2_validate" || name == "hs2_finalize_card";
        }
    }

    Service::Service(Json config, fs::path runtime) : config_(std::move(config)), runtime_(std::move(runtime))
    {
        data_ = fs::absolute(Path(config_.at("data_directory").get<std::string>()));
        game_ = fs::absolute(Path(config_.at("game_path").get<std::string>()));
        CardExportService::ValidateOutput(game_, data_);
        ExportPaths::NoLinks(data_);
        if (GetDriveTypeW(data_.root_path().c_str()) == DRIVE_REMOTE)
            throw std::invalid_argument("Agent 数据目录必须位于本机磁盘");
        auto identity = data_.lexically_normal().wstring();
        CharUpperBuffW(identity.data(), static_cast<DWORD>(identity.size()));
        auto key = Utf8(fs::path(identity));
        auto mutexName = L"Local\\HS2Agent-" + Path(Sha256(Bytes(key.begin(), key.end()))).wstring();
        auto mutex = CreateMutexW(nullptr, FALSE, mutexName.c_str());
        auto mutexError = GetLastError();
        if (!mutex)
            throw std::runtime_error("无法锁定 Agent 数据目录");
        instance_ = std::shared_ptr<void>(mutex,
                                          [](void *handle)
                                          {
                                              CloseHandle(handle);
                                          });
        if (mutexError == ERROR_ALREADY_EXISTS)
            throw std::runtime_error("Agent 数据目录已由其它服务实例打开");
        fs::create_directories(data_ / "Sessions");
        fs::create_directories(data_ / "Jobs");
        fs::create_directories(data_ / "Assets");
        repository_ = std::make_unique<CardRepository>(data_ / "Library");
        for (auto &entry : fs::directory_iterator(data_ / "Jobs"))
        {
            if (entry.path().extension() != ".json")
                continue;
            auto state = ReadJson(entry.path());
            if (state.value("status", "") == "running" || state.value("status", "") == "queued")
            {
                state["status"] = "interrupted";
                state["error"] = "上次服务中断；草稿已保留，可重新提交操作";
                WriteJson(entry.path(), state);
            }
        }
    }

    Service::~Service()
    {
        Stop();
    }

    void Service::Stop()
    {
        std::vector<std::shared_ptr<Job>> jobs;
        {
            std::lock_guard guard(jobsGate_);
            for (auto &[id, job] : jobs_)
            {
                job->Thread.request_stop();
                jobs.push_back(job);
            }
        }
        for (auto &job : jobs)
            if (job->Thread.joinable())
                job->Thread.join();
    }

    fs::path Service::SessionPath(const std::string &id) const
    {
        if (!Id(id))
            throw std::invalid_argument("会话标识无效");
        auto path = data_ / "Sessions" / Path(id);
        ExportPaths::NoLinks(path);
        return path;
    }

    Json Service::Session(const Json &args) const
    {
        return ReadJson(SessionPath(args.at("session_id").get<std::string>()) / "session.json");
    }

    CharacterDocument Service::Document(const Json &session) const
    {
        return CharacterDocument::Open(SessionPath(session.at("session_id")) /
                                       Path("revision-" + std::to_string(session.at("revision").get<int>()) + ".png"));
    }

    void Service::CheckRevision(const Json &session, const Json &args) const
    {
        if (session.at("revision") != args.at("expected_revision"))
            throw std::runtime_error("REVISION_CONFLICT：请读取最新会话后重试");
    }

    void Service::SaveRevision(Json &session, const CharacterDocument &document, const Json &args)
    {
        CheckRevision(session, args);
        auto path = SessionPath(session.at("session_id"));
        int revision = session.at("revision").get<int>() + 1;
        auto file = path / Path("revision-" + std::to_string(revision) + ".png");
        WriteAtomic(file, document.ToBytes());
        auto read = CharacterDocument::Open(file);
        if (read.Sex() != document.Sex())
            throw std::runtime_error("草稿回读失败");
        session["revision"] = revision;
        session["phase"] = "draft";
        session["validation"] = nullptr;
        session.erase("visual_review");
        session.erase("best_candidate_revision");
        session.erase("result");
        session["render_revision"] = -1;
        session["updated_utc"] = UtcNow();
        WriteJson(path / "session.json", session);
    }

    PreviewResources Service::Resources(const Json &session, std::stop_token token)
    {
        auto context = std::make_shared<PreviewResourceContext>();
        context->Game = game_;
        context->Data = data_ / "Cache";
        context->CatalogCache = context->Data;
        context->Package = SessionPath(session.at("session_id"));
        if (!baseIndex_)
        {
            auto saved = repository_->LoadMetadata("mods-v1");
            std::optional<ModIndex> previous;
            if (saved)
                previous = saved->get<ModIndex>();
            baseIndex_ = ModDependencyService::ScanTransient(game_, previous ? &*previous : nullptr, {}, token);
            repository_->StoreMetadata("mods-v1", *baseIndex_);
        }
        context->Index = *baseIndex_;
        context->Index.Files = NativeModSelection::Select(context->Index.Files);
        std::set<std::string> required;
        for (const auto &ref : CharacterAssetReferences::All(Document(session)))
            required.insert(ref.Guid);
        for (const auto &guid : session.value("resource_guids", Json::array()))
            required.insert(guid.get<std::string>());
        std::set<fs::path> loaded;
        for (const auto &record : session.value("assets", Json::array()))
            if (record.contains("guid") && required.contains(record.at("guid").get<std::string>()) &&
                record.contains("path") && loaded.insert(Path(record.at("path"))).second)
            {
                CheckCancelled(token);
                auto path = Path(record.at("path"));
                auto mod = ModDependencyService::ReadManifest(path);
                if (!mod.Error.empty())
                    throw std::runtime_error(mod.Error);
                context->Index.Files.push_back(mod);
                context->Priorities[path] = 1;
            }
        if (session.contains("candidate_mod"))
        {
            auto file = Path(session.at("candidate_mod"));
            context->Index.Files.push_back(ModDependencyService::ReadManifest(file));
            context->Priorities[file] = 2;
        }
        return context;
    }

    Json Service::Start(const std::string &name, const Json &args)
    {
        auto job = std::make_shared<Job>();
        auto id = NewId();
        job->State = {{"job_id", id}, {"tool", name}, {"status", "queued"}, {"session_id", args.at("session_id")}};
        std::lock_guard guard(jobsGate_);
        WriteJson(data_ / "Jobs" / Path(id + ".json"), job->State);
        jobs_[id] = job;
        job->Thread = std::jthread(
            [this, job, id, name, args](std::stop_token token)
            {
                HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
                Json result;
                try
                {
                    std::lock_guard work(workGate_);
                    CheckCancelled(token);
                    {
                        std::lock_guard lock(jobsGate_);
                        job->State["status"] = "running";
                    }
                    result = Execute(name, args, token);
                    std::lock_guard lock(jobsGate_);
                    job->State["status"] = "completed";
                    job->State["result"] = result;
                }
                catch (const std::exception &e)
                {
                    std::lock_guard lock(jobsGate_);
                    job->State["status"] = token.stop_requested() ? "cancelled" : "failed";
                    job->State["error"] = e.what();
                }
                {
                    std::lock_guard lock(jobsGate_);
                    try
                    {
                        WriteJson(data_ / "Jobs" / Path(id + ".json"), job->State);
                    }
                    catch (...)
                    {
                        job->State["persistence_error"] = true;
                    }
                }
                if (SUCCEEDED(com))
                    CoUninitialize();
                job->Finished.store(true);
            });
        return job->State;
    }

    void Service::ReapJobs()
    {
        std::vector<std::shared_ptr<Job>> finished;
        {
            std::lock_guard guard(jobsGate_);
            for (auto it = jobs_.begin(); it != jobs_.end();)
                if (it->second->Finished.load() && !it->second->State.value("persistence_error", false))
                {
                    finished.push_back(it->second);
                    it = jobs_.erase(it);
                }
                else
                    ++it;
        }
        for (auto &job : finished)
            if (job->Thread.joinable())
                job->Thread.join();
    }

    Json Service::Call(const std::string &name, const Json &args)
    {
        ReapJobs();
        if (name == "hs2_search_assets")
            throw std::invalid_argument("ASSET_SEARCH_REMOVED：成品素材搜索已停用；请使用 "
                                        "hs2_get_schema、hs2_prepare_asset 和 hs2_build_asset 直接制作");
        if (name == "hs2_get_job" || name == "hs2_cancel_job")
        {
            auto id = args.at("job_id").get<std::string>();
            if (!Id(id))
                throw std::invalid_argument("任务标识无效");
            std::lock_guard guard(jobsGate_);
            if (auto it = jobs_.find(id); it != jobs_.end())
            {
                if (name == "hs2_cancel_job")
                    it->second->Thread.request_stop();
                return it->second->State;
            }
            return ReadJson(data_ / "Jobs" / Path(id + ".json"));
        }
        if (Async(name))
            return Start(name, args);
        if (name == "hs2_get_session")
            return Session(args);
        std::unique_lock guard(workGate_, std::try_to_lock);
        if (!guard.owns_lock())
            throw std::runtime_error("AGENT_BUSY：请通过 hs2_get_job 查询当前作业或取消后重试");
        return Execute(name, args, {});
    }

    Json Service::Execute(const std::string &name, const Json &args, std::stop_token token)
    {
        if (name == "hs2_capabilities")
        {
            Json result{{"game_path", Utf8(game_)},      {"game_available", fs::is_directory(game_ / "abdata")},
                        {"data_directory", Utf8(data_)}, {"runtime", Utf8(runtime_)},
                        {"max_iterations", nullptr},     {"max_asset_attempts", nullptr},
                        {"asset_policy", "create_new"},  {"base_cards", Json::object()}};
            for (auto sex : {"male", "female"})
            {
                auto base = config_.value("base_cards", Json::object()).value(sex, "");
                auto parsed = base.empty() ? CardRecord{} : CardParser().Parse(Path(base));
                result["base_cards"][sex] = {{"configured", !base.empty()},
                                             {"valid", !base.empty() && parsed.Status == CardStatus::Valid &&
                                                           parsed.Sex == (std::string(sex) == "male" ? 0 : 1)}};
            }
            result["native_asset_build"] = {
                {"available", true},
                {"builder_version", asset::BuilderVersion},
                {"schema_version", asset::SchemaVersion},
                {"accepted_schema_versions", {2, 3}},
                {"kinds", {"texture", "accessory", "hair", "clothes", "catalog_texture", "layout", "head"}},
                {"input_formats", {"fbx", "obj", "glb", "gltf"}},
                {"recipe", true},
                {"requires_external_editors", false},
                {"fbx_import_available", fs::is_regular_file(runtime_ / "native/win-x64/HS2Manager.FbxBridge.dll")}};
            result["resource_database_available"] = fs::is_regular_file(runtime_ / "classdata.tpk");
            result["content_generation"] = "caller_ai";
            result["visual_review"] = ReviewRequirements(Json::object());
            return result;
        }
        if (name == "hs2_begin_card")
        {
            auto sex = args.at("sex").get<std::string>();
            if (sex != "male" && sex != "female")
                throw std::invalid_argument("sex 必须为 male/female");
            auto base = Path(config_.at("base_cards").at(sex).get<std::string>());
            auto document = CharacterDocument::Open(base, token);
            if (document.Sex() != (sex == "male" ? 0 : 1))
                throw std::invalid_argument("基准卡性别不匹配");
            Json session{{"session_id", NewId()},
                         {"revision", 0},
                         {"iterations", 0},
                         {"sex", sex},
                         {"name", args.value("name", "AI创建人物")},
                         {"phase", "draft"},
                         {"created_utc", UtcNow()},
                         {"base_hash", document.SourceHash()},
                         {"analysis", args.value("analysis", Json::object())},
                         {"render_revision", -1},
                         {"reference_version", 0},
                         {"reference_regions", Json::array()},
                         {"best_revision", nullptr},
                         {"assets", Json::array()}};
            auto path = SessionPath(session.at("session_id"));
            fs::create_directories(path);
            auto image = args.at("reference_image");
            Bytes bytes;
            std::string mime;
            if (image.contains("path"))
                bytes = ReadFile(Path(image.at("path")), 32 * 1024 * 1024, token);
            else
            {
                mime = image.at("mime_type");
                if (mime != "image/png" && mime != "image/jpeg")
                    throw std::invalid_argument("图片 MIME 类型无效");
                bytes = Unbase64(image.at("base64").get<std::string>());
            }
            auto input = path / "reference.image";
            WriteAtomic(input, bytes);
            ValidateImage(input);
            session["reference_image"] = Utf8(input);
            WriteAtomic(path / "revision-0.png", document.ToBytes());
            WriteJson(path / "session.json", session);
            return session;
        }
        auto session = Session(args);
        if (name == "hs2_get_session")
            return session;
        if (name == "hs2_set_reference_regions")
        {
            CheckRevision(session, args);
            auto path = SessionPath(session.at("session_id"));
            session["reference_regions"] = ReferenceRegions(Path(session.at("reference_image")), args.at("regions"),
                                                            path / "ReferenceImages" / Path(NewId()));
            session["reference_version"] = session.value("reference_version", 0) + 1;
            session.erase("visual_review");
            session["validation"] = nullptr;
            session["render_revision"] = -1;
            session["phase"] = "draft";
            session.erase("result");
            WriteJson(path / "session.json", session);
            return {{"session_id", session.at("session_id")},
                    {"revision", session.at("revision")},
                    {"reference_version", session.at("reference_version")},
                    {"images", session.at("reference_regions")}};
        }
        if (name == "hs2_review_render")
        {
            CheckRevision(session, args);
            auto review = AssessRender(session, args);
            session["visual_review"] = review;
            if (!session.contains("reviews"))
                session["reviews"] = Json::array();
            session["reviews"].push_back(review);
            if (args.value("mark_best", false) ||
                session.value("best_candidate_revision", -1) == session.at("revision").get<int>())
            {
                session["best_revision"] = session.at("revision");
                session["best_review"] = review;
            }
            session["phase"] = "draft";
            session["validation"] = nullptr;
            session.erase("result");
            WriteJson(SessionPath(session.at("session_id")) / "session.json", session);
            return review;
        }
        auto document = Document(session);
        if (name == "hs2_get_schema")
        {
            Json fields = Json::array();
            for (auto &field : CharacterEditSchema::Build(document, CharacterSliderLimits::Read(game_)))
            {
                Json choices = Json::array();
                for (auto &choice : field.Choices)
                    choices.push_back({{"value", ScalarJson(choice.Value)}, {"label", choice.Label}});
                Json value;
                if (field.Kind == CharacterFieldKind::Color)
                    value = CharacterEditSchema::ColorValue(document, field.Path);
                else if (document.Contains(field.Path))
                    value = ScalarJson(document.Node(field.Path).ScalarValue);
                fields.push_back({{"path", field.Path},
                                  {"label", field.Label},
                                  {"category", field.Category},
                                  {"group", field.Group},
                                  {"kind", static_cast<int>(field.Kind)},
                                  {"minimum", field.Minimum},
                                  {"maximum", field.Maximum},
                                  {"display_scale", field.DisplayScale},
                                  {"value", value},
                                  {"choices", choices}});
                if (field.Kind == CharacterFieldKind::Asset || field.Kind == CharacterFieldKind::Preset)
                {
                    fields.back()["creation"] = asset::CreationTarget(document, field.Path);
                    fields.back()["creation"]["enabled"] = asset::TargetEnabled(document, field.Path);
                }
            }
            return {{"revision", session["revision"]}, {"fields", fields}};
        }
        if (name == "hs2_restore_revision")
        {
            CheckRevision(session, args);
            int target = args.at("revision");
            if (target < 0 || target > session.at("revision").get<int>())
                throw std::invalid_argument("历史版本不存在");
            auto old = CharacterDocument::Open(SessionPath(session.at("session_id")) /
                                               Path("revision-" + std::to_string(target) + ".png"));
            SaveRevision(session, old, args);
            return session;
        }
        if (name == "hs2_apply_changes")
        {
            CheckRevision(session, args);
            if (!args.at("changes").is_array() || args.at("changes").empty() || args.at("changes").size() > 512)
                throw std::invalid_argument("changes 必须包含 1 到 512 项修改");
            auto schema = CharacterEditSchema::Build(document, CharacterSliderLimits::Read(game_));
            std::unique_ptr<CharacterCatalog> catalog;
            for (auto &change : args.at("changes"))
            {
                auto path = change.at("path").get<std::string>();
                auto field = std::find_if(schema.begin(), schema.end(),
                                          [&](const auto &f)
                                          {
                                              return f.Path == path;
                                          });
                if (field == schema.end())
                    throw std::invalid_argument("不可编辑字段：" + path);
                auto value = change.at("value");
                if (field->Kind == CharacterFieldKind::Asset || field->Kind == CharacterFieldKind::Preset)
                {
                    auto assetId = value.get<std::string>();
                    std::string generatedGuid;
                    for (const auto &record : session.value("assets", Json::array()))
                        if (record.contains("guid") && record.contains("spec"))
                        {
                            const auto &spec = record.at("spec");
                            if (spec.contains("category") && spec.contains("slot_id") &&
                                std::to_string(spec.at("category").get<int>()) + ":" +
                                        record.at("guid").get<std::string>() + ":" +
                                        std::to_string(spec.at("slot_id").get<int>()) ==
                                    assetId)
                                generatedGuid = record.at("guid");
                        }
                    if (generatedGuid.empty())
                        throw std::invalid_argument(
                            "ASSET_CREATION_REQUIRED：素材须由本会话制作；布局请使用 kind:layout");
                    if (!catalog)
                    {
                        auto context = session;
                        context["resource_guids"] = Json::array();
                        for (const auto &record : session.value("assets", Json::array()))
                            if (record.contains("guid"))
                                context["resource_guids"].push_back(record.at("guid"));
                        catalog = std::make_unique<CharacterCatalog>(game_, data_ / "Cache", runtime_ / "classdata.tpk",
                                                                     Resources(context, token));
                        catalog->Load({}, token);
                    }
                    auto entry = std::find_if(
                        catalog->Entries().begin(), catalog->Entries().end(),
                        [&](const auto &e)
                        {
                            return std::to_string(e.Category) + ":" + e.Guid + ":" + std::to_string(e.Id) == assetId;
                        });
                    if (entry == catalog->Entries().end())
                        throw std::invalid_argument("素材标识已失效");
                    document = catalog->Select(document, path, *entry);
                }
                else if (field->Kind == CharacterFieldKind::Color)
                {
                    if (!value.is_array() || value.size() < 3 || value.size() > 4)
                        throw std::invalid_argument("颜色需要 RGB 或 RGBA 数组");
                    document = CharacterEditRules::Color(document, path, value.get<std::vector<float>>());
                }
                else
                {
                    if (field->Kind == CharacterFieldKind::Number &&
                        (!value.is_number() || !std::isfinite(value.get<double>()) ||
                         value.get<double>() < field->Minimum || value.get<double>() > field->Maximum))
                        throw std::invalid_argument("数值超出字段范围：" + path);
                    if (field->Kind == CharacterFieldKind::Toggle && !value.is_boolean())
                        throw std::invalid_argument("预期布尔值");
                    if (!field->Choices.empty() && std::none_of(field->Choices.begin(), field->Choices.end(),
                                                                [&](const auto &c)
                                                                {
                                                                    return ScalarJson(c.Value) == value;
                                                                }))
                        throw std::invalid_argument("选项无效");
                    document = CharacterEditRules::SetValue(document, path, Scalar(value));
                }
            }
            session["iterations"] = session.at("iterations").get<std::int64_t>() + 1;
            session["analysis"] = args.value("analysis", session["analysis"]);
            SaveRevision(session, document, args);
            if (args.value("mark_best", false))
            {
                // 先标记候选；查看实际渲染后才确认最佳版本。
                session["best_candidate_revision"] = session["revision"];
                WriteJson(SessionPath(session.at("session_id")) / "session.json", session);
            }
            return session;
        }
        CheckRevision(session, args);
        if (name == "hs2_render")
            return Render(session, document, args, token);
        if (name == "hs2_probe_fields")
        {
            Json outputs = Json::array();
            auto schema = CharacterEditSchema::Build(document, CharacterSliderLimits::Read(game_));
            if (!args.at("paths").is_array() || args.at("paths").empty() || args.at("paths").size() > 6)
                throw std::invalid_argument("每次探测一到六个滑条");
            double fraction = args.value("delta", .05);
            if (!std::isfinite(fraction) || fraction < .01 || fraction > .25)
                throw std::invalid_argument("delta 必须为滑条范围的 0.01 到 0.25");
            for (auto &pathValue : args.at("paths"))
            {
                auto path = pathValue.get<std::string>();
                auto field = std::find_if(schema.begin(), schema.end(),
                                          [&](const auto &f)
                                          {
                                              return f.Path == path;
                                          });
                if (field == schema.end() || field->Kind != CharacterFieldKind::Number)
                    throw std::invalid_argument("只能探测数值字段");
                double value = ScalarJson(document.Node(path).ScalarValue).get<double>();
                for (int sign : {-1, 0, 1})
                {
                    double next = std::clamp(value + sign * (field->Maximum - field->Minimum) * fraction,
                                             field->Minimum, field->Maximum);
                    auto draft = CharacterEditRules::SetValue(document, path, next);
                    auto views = args.value("views", field->Category == "face" ? Json::array({"face", "face_side"})
                                                                               : Json::array({"front", "side"}));
                    if (!views.is_array() || views.empty() || views.size() > 2)
                        throw std::invalid_argument("探测视图必须为一到两个");
                    auto renderArgs = args;
                    renderArgs["views"] = views;
                    renderArgs["probe"] = true;
                    auto render = Render(session, draft, renderArgs, token);
                    outputs.push_back({{"path", path}, {"value", next}, {"sign", sign}, {"render", render}});
                }
            }
            auto images = ProbeImages(outputs, SessionPath(session.at("session_id")) / "Probes" / Path(NewId()));
            return {{"probes", outputs}, {"images", images}};
        }
        if (name == "hs2_prepare_asset")
            return PrepareAsset(session, args, token);
        if (name == "hs2_build_asset")
            return BuildAsset(session, args, token);
        if (name == "hs2_validate")
            return Validate(session, token);
        if (name == "hs2_finalize_card")
            return Finalize(session, args, token);
        throw std::invalid_argument("未知工具：" + name);
    }
}
