#include "Agent.h"
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
        auto directory = SessionPath(session.at("session_id")) / "Renders" / Path(NewId());
        auto result = creation::RenderCharacter(game_, data_ / "Cache", runtime_, document, args, directory,
                                                Resources(session, token), token);
        result["revision"] = session["revision"];
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
