#include "Agent.h"
#include "VisualReview.h"
#include "SharedAssets.h"
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
        auto renderId = NewId();
        auto directory = SessionPath(session.at("session_id")) / "Renders" / Path(renderId);
        Json options{{"width", 768},
                     {"height", 1024},
                     {"field_of_view", 25.f},
                     {"background", "neutral"},
                     {"side", "left"},
                     {"face_zoom", 1.4f},
                     {"face_vertical_offset", -.12f},
                     {"views", {"front", "side", "back", "face", "face_side"}}};
        options.update(args);
        auto result = creation::RenderCharacter(game_, data_ / "Cache", runtime_, document, options, directory,
                                                Resources(session, token), token);
        result["revision"] = session["revision"];
        result["render_id"] = renderId;
        result["reference_version"] = session.value("reference_version", 0);
        result["comparison_images"] =
            args.value("probe", false)
                ? Json::array()
                : ComparisonImages(session.value("reference_regions", Json::array()), result.at("images"), directory);
        result["review_requirements"] = ReviewRequirements(session);
        if (!args.value("probe", false))
        {
            session.erase("visual_review");
            session["validation"] = nullptr;
            session["phase"] = "draft";
            session.erase("result");
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
                    {"visual_assessment", CurrentReviewPassed(session) ? "passed" : "needs_review"},
                    {"review_requirements", ReviewRequirements(session)},
                    {"game_validation", "not_run"}};
        session["validation"] = result;
        WriteJson(SessionPath(session.at("session_id")) / "session.json", session);
        return result;
    }

    Json Service::Finalize(Json &session, const Json &args, std::stop_token token)
    {
        if (!args.at("visual_assessment").at("passed").get<bool>())
            throw std::runtime_error("视觉评估未通过，保留草稿");
        if (!CurrentReviewPassed(session))
            return {{"status", "needs_work"},
                    {"reason", "VISUAL_REVIEW_REQUIRED"},
                    {"review_requirements", ReviewRequirements(session)},
                    {"visual_review", session.value("visual_review", Json::object())}};
        auto validation = Validate(session, token);
        if (!validation.at("structure").get<bool>() || !validation.at("dependencies").get<bool>() ||
            !validation.at("native_render").get<bool>())
            return {{"status", "needs_work"}, {"validation", validation}, {"session", session}};
        if (!session.at("last_render").at("diagnostics").empty())
            return {
                {"status", "needs_work"}, {"diagnostics", session["last_render"]["diagnostics"]}, {"session", session}};
        auto document = Document(session);
        auto previews = session.at("last_render");
        // 使用已经逐项评估的正面图；重新拍封面会改变取景并使验收证据失效。
        auto front = std::find_if(previews.at("images").begin(), previews.at("images").end(),
                                  [](const auto &image)
                                  {
                                      return image.at("view") == "front";
                                  });
        if (front == previews.at("images").end())
            throw std::runtime_error("已评估的正面预览缺失");
        auto cover = ReadFile(Path(front->at("path")), 32 * 1024 * 1024, token);
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
                    {"visual_review", session.at("visual_review")},
                    {"game_validation", "not_run"},
                    {"analysis", session["analysis"]},
                    {"revision", session["revision"]},
                    {"images", previews["images"]},
                    {"comparison_images", previews["comparison_images"]}};
        result["validation"]["visual_assessment"] = "passed";
        for (auto &directory : report.OutputFolders)
        {
            auto target = Path(directory);
            for (auto &image : previews["images"])
            {
                auto file = Path(image["path"]);
                WriteAtomic(target / "Previews" / file.filename(), ReadFile(file));
            }
            for (auto &image : previews["comparison_images"])
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
