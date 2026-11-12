#include "Agent.h"
#include "Pipeline/NativeFramePipeline.h"
#include "Runtime/CharacterRuntime.h"
#include "Images/ImageFiles.h"
#include "Export/CardExport.h"
#include <cmath>
#include <set>

namespace hs2::agent
{
    Json Service::Render(Json &session, const CharacterDocument &document, const Json &args, std::stop_token token)
    {
        auto card = CardParser().Parse(document.SourcePath(), token);
        NativeCharacterService service(game_, data_ / "Cache", runtime_ / "classdata.tpk", Resources(session, token));
        CharacterPreviewVisibility visibility;
        visibility.Clothing = args.value("clothing_state", 0);
        auto prepared = service.Prepare(card, &document, visibility, {}, token);
        GraphicsDevice graphics;
        NativeCharacterRuntime runtime(prepared.Scene, prepared.Assets, runtime_ / "native/win-x64");
        runtime.Update(0);
        float sampleTime = args.value("sample_time", 0.f);
        if (!std::isfinite(sampleTime) || sampleTime < 0 || sampleTime > 5)
            throw std::invalid_argument("sample_time 必须介于 0 到 5 秒");
        for (float time = 1.f / 30; time <= sampleTime; time += 1.f / 30)
        {
            CheckCancelled(token);
            runtime.Update(time);
        }
        PoseCharacterFace(*prepared.Scene, 1);
        NativeFrameOptions options;
        options.StaticReference = true;
        NativeFramePipeline pipeline(graphics, *prepared.Scene, *prepared.Assets, options);
        Vector3 lo{INFINITY, INFINITY, INFINITY}, hi{-INFINITY, -INFINITY, -INFINITY};
        Vector3 faceLo = lo, faceHi = hi;
        for (auto &renderer : prepared.Scene->Renderers)
            if (renderer->Visible() && renderer->HasGeometry())
            {
                // Unity 的保守蒙皮包围盒可能远大于可见人物；用实际形变顶点取景。
                auto deformed = renderer->Deform();
                auto world = renderer->Node->WorldMatrix();
                for (auto &vertex : deformed.Positions)
                {
                    auto point = TransformPoint(vertex, world);
                    if (!std::isfinite(point.x) || !std::isfinite(point.y) || !std::isfinite(point.z))
                        throw std::runtime_error("形变产生非有限坐标");
                    lo = {(std::min)(lo.x, point.x), (std::min)(lo.y, point.y), (std::min)(lo.z, point.z)};
                    hi = {(std::max)(hi.x, point.x), (std::max)(hi.y, point.y), (std::max)(hi.z, point.z)};
                    if (renderer->Node->Name == "o_head" || renderer->Role == "head")
                    {
                        faceLo = {(std::min)(faceLo.x, point.x), (std::min)(faceLo.y, point.y),
                                  (std::min)(faceLo.z, point.z)};
                        faceHi = {(std::max)(faceHi.x, point.x), (std::max)(faceHi.y, point.y),
                                  (std::max)(faceHi.z, point.z)};
                    }
                }
            }
        if (!std::isfinite(lo.y) || hi.y <= lo.y)
            throw std::runtime_error("人物没有可渲染几何");
        int width = (std::clamp)(args.value("width", 512), 128, 2048);
        int height = (std::clamp)(args.value("height", 640), 128, 2048);
        auto directory = SessionPath(session.at("session_id")) / "Renders" / Path(NewId());
        fs::create_directories(directory);
        Json images = Json::array();
        auto views = args.value("views", Json::array({"front", "side", "back", "face"}));
        if (views.empty() || views.size() > 5)
            throw std::invalid_argument("视图数量必须为 1 到 5");
        for (auto &v : views)
        {
            CheckCancelled(token);
            auto view = v.get<std::string>();
            float angle = view == "front" || view == "face" ? 0.f
                          : view == "side"                  ? 1.5707963f
                          : view == "back"                  ? 3.1415926f
                          : view == "three_quarter"         ? .7853982f
                                                            : INFINITY;
            if (!std::isfinite(angle))
                throw std::invalid_argument("未知视图");
            NativeRenderCamera camera;
            camera.Target = {(lo.x + hi.x) * .5f, (lo.y + hi.y) * .5f, (lo.z + hi.z) * .5f};
            float size = hi.y - lo.y;
            float span = (std::max)(hi.x - lo.x, hi.z - lo.z);
            float depth = hi.z - lo.z;
            if (view == "face")
            {
                if (!std::isfinite(faceLo.y))
                    throw std::runtime_error("无法定位头部几何");
                camera.Target = {(faceLo.x + faceHi.x) * .5f, (faceLo.y + faceHi.y) * .5f, (faceLo.z + faceHi.z) * .5f};
                size = (faceHi.y - faceLo.y) * 1.25f;
                span = (faceHi.x - faceLo.x) * 1.25f;
                depth = faceHi.z - faceLo.z;
            }
            camera.FieldOfView = args.value("field_of_view", 35.f);
            if (!std::isfinite(camera.FieldOfView) || camera.FieldOfView < 10 || camera.FieldOfView > 90)
                throw std::invalid_argument("视角必须介于 10 和 90 度");
            float distance =
                (std::max)(size, span * height / width) / (2 * std::tan(camera.FieldOfView * .008726646f)) * 1.12f +
                depth * .5f;
            camera.Position = {camera.Target.x + std::sin(angle) * distance, camera.Target.y,
                               camera.Target.z + std::cos(angle) * distance};
            pipeline.ResetHistory();
            auto &frame = pipeline.Draw(camera, width, height, sampleTime);
            auto pixels = graphics.Readback(frame.Texture.Get());
            std::set<std::uint32_t> colors;
            for (size_t p = 0; p < pixels.size(); p += 4)
            {
                pixels[p + 3] = 255;
                colors.insert((pixels[p] << 16) | (pixels[p + 1] << 8) | pixels[p + 2]);
            }
            if (colors.size() < 64)
                throw std::runtime_error("渲染结果为空或缺少有效颜色");
            auto output = directory / Path(view + ".png");
            SavePng(output, width, height, pixels);
            images.push_back({{"path", Utf8(output)}, {"mime_type", "image/png"}, {"view", view}});
        }
        Json result{
            {"images", images}, {"revision", session["revision"]}, {"diagnostics", prepared.Scene->Diagnostics}};
        if (!args.value("probe", false))
        {
            session["render_revision"] = session["revision"];
            session["last_render"] = result;
            WriteJson(SessionPath(session.at("session_id")) / "session.json", session);
        }
        return result;
    }

    Json Service::Validate(Json &session, std::stop_token token)
    {
        auto doc = Document(session);
        auto card = CardParser().Parse(doc.SourcePath(), token);
        auto resources = Resources(session, token);
        CardExportDependencies collector(game_, resources->Index, token, runtime_ / "classdata.tpk");
        auto dependencies = collector.Collect(card, CardExportLevel::WithResources);
        Json result{{"revision", session["revision"]},
                    {"structure", card.Status == CardStatus::Valid},
                    {"dependencies", dependencies.Issues.empty()},
                    {"dependency_issues", dependencies.Issues},
                    {"warnings", dependencies.Warnings},
                    {"native_render", session.value("render_revision", -1) == session.at("revision").get<int>()},
                    {"visual_assessment", "not_run"},
                    {"game_validation", "not_run"}};
        session["validation"] = result;
        WriteJson(SessionPath(session.at("session_id")) / "session.json", session);
        return result;
    }

    Json Service::Finalize(Json &session, const Json &args, std::stop_token token)
    {
        if (!args.at("visual_assessment").at("passed").get<bool>())
            throw std::runtime_error("视觉评估未通过，保留草稿");
        auto validation = Validate(session, token);
        if (!validation.at("structure").get<bool>() || !validation.at("dependencies").get<bool>() ||
            !validation.at("native_render").get<bool>())
            return {{"status", "needs_work"}, {"validation", validation}, {"session", session}};
        if (!session.at("last_render").at("diagnostics").empty())
            return {
                {"status", "needs_work"}, {"diagnostics", session["last_render"]["diagnostics"]}, {"session", session}};
        auto document = Document(session);
        auto previews = session.at("last_render");
        auto render = Render(session, document, {{"views", {"front"}}}, token);
        auto cover = ReadFile(Path(render["images"][0]["path"]), 32 * 1024 * 1024, token);
        document = document.WithName(session.at("name")).WithCover(std::move(cover));
        auto folder = SessionPath(session.at("session_id")) / "Created" / Path(NewId());
        fs::create_directories(folder);
        auto source = CardParser().Parse(document.SourcePath(), token);
        auto created = CardEditService(*repository_).Save(document, source, false, folder / "character.png", token);
        auto output = Path(args.value("output_directory", config_.value("output_directory", Utf8(data_ / "Output"))));
        CardExportService::ValidateOutput(game_, output);
        auto resources = Resources(session, token);
        CardExportService exporter(*repository_, runtime_ / "classdata.tpk");
        auto plan =
            exporter.BuildPlan({created.Card}, game_, output, {CardExportMode::PerCard, CardExportLevel::WithResources},
                               {}, token, &resources->Index);
        auto report = exporter.Export(plan, {}, token);
        Json result{{"status", report.Status == CardExportStatus::Complete ? "complete" : "needs_work"},
                    {"folders", report.OutputFolders},
                    {"export_report", report.ReportPath},
                    {"validation", validation},
                    {"visual_assessment", args["visual_assessment"]},
                    {"game_validation", "not_run"},
                    {"analysis", session["analysis"]},
                    {"revision", session["revision"]},
                    {"images", previews["images"]}};
        result["validation"]["visual_assessment"] = "passed";
        for (auto &directory : report.OutputFolders)
        {
            auto target = Path(directory);
            for (auto &image : previews["images"])
            {
                auto file = Path(image["path"]);
                WriteAtomic(target / "Previews" / file.filename(), ReadFile(file));
            }
            WriteJson(target / "agent-report.json", result);
        }
        session["phase"] = result["status"];
        session["validation"] = result["validation"];
        session["result"] = result;
        WriteJson(SessionPath(session.at("session_id")) / "session.json", session);
        return result;
    }
}
