#include "Agent.h"
#include "Export/CardExport.h"
#include <windows.h>
#include <objbase.h>
#include <algorithm>
#include <cmath>
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
            return name == "hs2_render" || name == "hs2_probe_fields" || name == "hs2_search_assets" ||
                   name == "hs2_prepare_asset" || name == "hs2_build_asset" || name == "hs2_validate" ||
                   name == "hs2_finalize_card";
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
        for (auto &entry : fs::directory_iterator(data_ / "Assets"))
            if (entry.path().extension() == ".zipmod")
            {
                CheckCancelled(token);
                auto mod = ModDependencyService::ReadManifest(entry.path());
                if (!mod.Error.empty())
                    throw std::runtime_error(mod.Error);
                context->Index.Files.push_back(mod);
                context->Priorities[entry.path()] = 1;
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
            });
        return job->State;
    }

    Json Service::Call(const std::string &name, const Json &args)
    {
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
            Json result{{"game_path", Utf8(game_)},
                        {"game_available", fs::is_directory(game_ / "abdata")},
                        {"data_directory", Utf8(data_)},
                        {"runtime", Utf8(runtime_)},
                        {"max_iterations", config_.value("max_iterations", 12)},
                        {"base_cards", Json::object()}};
            for (auto sex : {"male", "female"})
            {
                auto base = config_.value("base_cards", Json::object()).value(sex, "");
                auto parsed = base.empty() ? CardRecord{} : CardParser().Parse(Path(base));
                result["base_cards"][sex] = {{"configured", !base.empty()},
                                             {"valid", !base.empty() && parsed.Status == CardStatus::Valid &&
                                                           parsed.Sex == (std::string(sex) == "male" ? 0 : 1)}};
            }
            for (auto key : {"blender_path", "unity_path", "modding_project"})
                result[key] = {{"path", config_.value(key, "")},
                               {"available", fs::exists(Path(config_.value(key, "")))}};
            result["resource_database_available"] = fs::is_regular_file(runtime_ / "classdata.tpk");
            result["content_generation"] = "caller_ai";
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
                         {"best_revision", 0},
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
            if (session.at("iterations").get<int>() >= config_.value("max_iterations", 12))
                throw std::runtime_error("ITERATION_LIMIT：已达修正上限，草稿保留");
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
                    if (!catalog)
                    {
                        catalog = std::make_unique<CharacterCatalog>(game_, data_ / "Cache", runtime_ / "classdata.tpk",
                                                                     Resources(session, token));
                        catalog->Load({}, token);
                    }
                    auto assetId = value.get<std::string>();
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
            session["iterations"] = session.at("iterations").get<int>() + 1;
            session["analysis"] = args.value("analysis", session["analysis"]);
            SaveRevision(session, document, args);
            if (args.value("mark_best", false))
            {
                session["best_revision"] = session["revision"];
                WriteJson(SessionPath(session.at("session_id")) / "session.json", session);
            }
            return session;
        }
        if (name == "hs2_search_assets")
        {
            if (args.at("category").get<int>() == -1)
            {
                Json textures = Json::array();
                int offset = (std::max)(0, args.value("offset", 0));
                int limit = (std::clamp)(args.value("limit", 20), 1, 50);
                int total = 0;
                for (auto &entry : fs::directory_iterator(data_ / "Assets"))
                {
                    CheckCancelled(token);
                    if (entry.path().extension() != ".json")
                        continue;
                    auto record = ReadJson(entry.path());
                    if (record.value("kind", "") != "texture" ||
                        record.value("asset_key", "").find(args.value("query", "")) == std::string::npos)
                        continue;
                    if (total >= offset && total < offset + limit)
                        textures.push_back(record);
                    ++total;
                }
                return {{"items", textures}, {"total", total}, {"offset", offset}};
            }
            CharacterCatalog catalog(game_, data_ / "Cache", runtime_ / "classdata.tpk", Resources(session, token));
            catalog.Load({}, token);
            auto entries = catalog.Query(args.at("category"), args.value("query", ""), args.value("source", 0));
            Json items = Json::array();
            Json images = Json::array();
            int offset = (std::max)(0, args.value("offset", 0));
            int limit = (std::clamp)(args.value("limit", 20), 1, 50);
            for (int i = offset; i < static_cast<int>(entries.size()) && i < offset + limit; ++i)
            {
                auto &e = entries[i];
                items.push_back({{"asset_id", std::to_string(e.Category) + ":" + e.Guid + ":" + std::to_string(e.Id)},
                                 {"name", e.Editable().Name},
                                 {"category", e.Category},
                                 {"guid", e.Guid},
                                 {"id", e.Id},
                                 {"source", e.Source}});
                try
                {
                    CheckCancelled(token);
                    if (auto reference = catalog.Thumbnail(e))
                    {
                        NativeGpuAssetReader reader(reference->Bundle, catalog.Database().get());
                        auto pointer = reader.Find(reference->Asset, 28);
                        auto texture = reader.Texture(pointer.PathId);
                        GraphicsDevice graphics(true);
                        GpuTexture input(graphics, texture);
                        float scale = (std::min)(128.f / texture.Width, 128.f / texture.Height);
                        int width = (std::max)(1, static_cast<int>(texture.Width * scale));
                        int height = (std::max)(1, static_cast<int>(texture.Height * scale));
                        GpuTexture output(graphics, width, height, true, DXGI_FORMAT_B8G8R8A8_UNORM, false);
                        GpuCopyPass(graphics).Copy(input, output, true);
                        auto pixels = graphics.Readback(output.Texture.Get());
                        auto file = SessionPath(session.at("session_id")) / "Thumbnails" / Path(NewId() + ".png");
                        fs::create_directories(file.parent_path());
                        SavePng(file, width, height, pixels);
                        Json image{
                            {"path", Utf8(file)}, {"mime_type", "image/png"}, {"asset_id", items.back()["asset_id"]}};
                        images.push_back(image);
                        items.back()["thumbnail"] = image;
                    }
                }
                catch (const Cancelled &)
                {
                    throw;
                }
                catch (const std::exception &e)
                {
                    items.back()["thumbnail_error"] = e.what();
                }
            }
            return {{"items", items}, {"images", images}, {"total", entries.size()}, {"offset", offset}};
        }
        CheckRevision(session, args);
        if (name == "hs2_render")
            return Render(session, document, args, token);
        if (name == "hs2_probe_fields")
        {
            Json outputs = Json::array();
            auto schema = CharacterEditSchema::Build(document, CharacterSliderLimits::Read(game_));
            if (args.at("paths").size() > 6)
                throw std::invalid_argument("每次最多探测六个滑条");
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
                    double next = std::clamp(value + sign * (field->Maximum - field->Minimum) * .1, field->Minimum,
                                             field->Maximum);
                    auto draft = CharacterEditRules::SetValue(document, path, next);
                    auto render = Render(session, draft, {{"views", {"face"}}, {"probe", true}}, token);
                    outputs.push_back({{"path", path}, {"value", next}, {"render", render}});
                }
            }
            return {{"probes", outputs}};
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
