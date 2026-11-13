#include "Agent.h"
#include "VisualReview.h"
#include <iostream>

using namespace hs2;

namespace
{
    void Check(bool passed, const char *message)
    {
        if (!passed)
            throw std::runtime_error(message);
    }

    template <class F> void Reject(F action)
    {
        bool rejected = false;
        try
        {
            action();
        }
        catch (const std::exception &)
        {
            rejected = true;
        }
        Check(rejected, "quality check must reject invalid input");
    }
}

void QualityChecks(const fs::path &root, const fs::path &image, const fs::path &base)
{
    agent::Service service(
        {{"data_directory", Utf8(root)}, {"game_path", Utf8(root / "Game")}, {"base_cards", {{"female", Utf8(base)}}}},
        root);
    Check(service.Call("hs2_capabilities", Json::object()).at("max_iterations") == 24, "quality iteration default");
    auto session = service.Call("hs2_begin_card", {{"sex", "female"}, {"reference_image", {{"path", Utf8(image)}}}});
    Check(session.at("best_revision").is_null(), "unreviewed template is not best");
    auto id = session.at("session_id");
    Json args{{"session_id", id}, {"expected_revision", 0}};
    auto marked = args;
    marked["regions"] = {{{"view", "front"}, {"rect", {0, 0, 1, 1}}}, {{"view", "face"}, {"rect", {.25, .25, .5, .5}}}};
    auto references = service.Call("hs2_set_reference_regions", marked);
    Check(references.at("reference_version") == 1 && references.at("images").size() == 2, "reference views cropped");
    for (const auto &entry : references.at("images"))
        agent::ValidateImage(Path(entry.at("path")));
    auto invalid = marked;
    invalid["regions"][1]["rect"] = {.9, .1, .2, .3};
    Reject(
        [&]
        {
            service.Call("hs2_set_reference_regions", invalid);
        });
    invalid["regions"][1]["rect"] = {0, 0, 0, 1};
    Reject(
        [&]
        {
            service.Call("hs2_set_reference_regions", invalid);
        });
    invalid = marked;
    invalid["regions"][1]["view"] = "front";
    Reject(
        [&]
        {
            service.Call("hs2_set_reference_regions", invalid);
        });
    invalid = marked;
    invalid["expected_revision"] = 1;
    Reject(
        [&]
        {
            service.Call("hs2_set_reference_regions", invalid);
        });
    session = service.Call("hs2_get_session", {{"session_id", id}});
    Check(session.at("reference_version") == 1 && session.at("revision") == 0, "invalid reference edits atomic");
    auto comparison = agent::ComparisonImages(references.at("images"), references.at("images"), root / "Comparison");
    Check(comparison.size() == 2 && comparison[0]["layout"] == "left_reference_right_render", "matched view pairs");
    agent::ValidateImage(Path(comparison[0]["path"]));
    Json probes = Json::array();
    for (int sign : {-1, 0, 1})
        probes.push_back({{"path", "face.test"},
                          {"sign", sign},
                          {"value", .5 + sign * .05},
                          {"render", {{"images", references.at("images")}}}});
    auto probeImages = agent::ProbeImages(probes, root / "Probes");
    Check(probeImages.size() == 1 && probeImages[0]["rows"][1]["value"] == .5 && probeImages[0]["columns"].size() == 2,
          "probe image retains signed values and both views");
    agent::ValidateImage(Path(probeImages[0]["path"]));

    // 使用冻结渲染记录测试状态门禁，不把测试图片伪称为游戏渲染或相似度验收。
    auto path = root / "Sessions" / Path(id) / "session.json";
    Json render{{"render_id", "fixture-render"},
                {"reference_version", 1},
                {"diagnostics", Json::array()},
                {"comparison_images", comparison},
                {"images", Json::array()}};
    for (auto view : {"front", "side", "back", "face", "face_side"})
        render["images"].push_back({{"view", view}, {"path", Utf8(image)}});
    session["render_revision"] = 0;
    session["last_render"] = render;
    WriteJson(path, session);
    auto review = args;
    review["render_id"] = "fixture-render";
    review["unseen_regions"] = "测试参考不包含隐藏表面";
    review["areas"] = Json::array();
    for (auto area : {"face", "hair", "body", "outfit", "accessories", "materials"})
        review["areas"].push_back({{"area", area},
                                   {"status", "matched"},
                                   {"reference", "固定测试特征"},
                                   {"observed", "固定测试观察，仅验证协议"}});
    Check(agent::AssessRender(session, review).at("status") == "passed", "complete review accepted");
    auto noRender = session;
    noRender.erase("last_render");
    Reject(
        [&]
        {
            agent::AssessRender(noRender, review);
        });
    auto missing = session;
    missing["last_render"]["images"].erase(4);
    Reject(
        [&]
        {
            agent::AssessRender(missing, review);
        });
    missing = session;
    missing["last_render"]["comparison_images"] = Json::array();
    Reject(
        [&]
        {
            agent::AssessRender(missing, review);
        });
    missing = session;
    missing["last_render"]["diagnostics"] = {"missing mesh"};
    Reject(
        [&]
        {
            agent::AssessRender(missing, review);
        });
    missing = session;
    missing["last_render"]["clothing_state"] = 2;
    Reject(
        [&]
        {
            agent::AssessRender(missing, review);
        });
    auto badReview = review;
    badReview["render_id"] = "old-render";
    Reject(
        [&]
        {
            agent::AssessRender(session, badReview);
        });
    badReview = review;
    for (auto &area : badReview["areas"])
        area["status"] = "not_visible";
    Reject(
        [&]
        {
            agent::AssessRender(session, badReview);
        });
    badReview = review;
    badReview["areas"][0]["reference"] = "  \n";
    Reject(
        [&]
        {
            agent::AssessRender(session, badReview);
        });
    badReview = review;
    badReview["areas"][1]["area"] = "face";
    Reject(
        [&]
        {
            agent::AssessRender(session, badReview);
        });
    badReview = review;
    badReview["areas"][0]["status"] = "needs_work";
    Reject(
        [&]
        {
            agent::AssessRender(session, badReview);
        });
    badReview["areas"][0]["correction"] = "缩小眼高并重新检查侧脸";
    auto failed = service.Call("hs2_review_render", badReview);
    Check(failed.at("status") == "needs_work", "unresolved difference prevents pass");
    session = service.Call("hs2_get_session", {{"session_id", id}});
    Check(!agent::CurrentReviewPassed(session), "failed assessment blocks finalization");
    auto finalize = args;
    finalize["visual_assessment"] = {{"passed", true}, {"notes", "不能绕过差异检查"}};
    auto job = service.Call("hs2_finalize_card", finalize);
    for (int wait = 0; wait < 200 && (job["status"] == "queued" || job["status"] == "running"); ++wait)
    {
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
        job = service.Call("hs2_get_job", {{"job_id", job.at("job_id")}});
    }
    Check(job.at("status") == "completed" && job.at("result").at("reason") == "VISUAL_REVIEW_REQUIRED",
          "passed boolean cannot bypass recorded differences");
    review["mark_best"] = true;
    service.Call("hs2_review_render", review);
    session = service.Call("hs2_get_session", {{"session_id", id}});
    Check(agent::CurrentReviewPassed(session) && session.at("best_revision") == 0 && session.at("reviews").size() == 2,
          "review history and reviewed best saved");
    auto stale = session;
    stale["revision"] = 1;
    Check(!agent::CurrentReviewPassed(stale), "review invalid after edit");
    stale = session;
    stale["reference_version"] = 2;
    Check(!agent::CurrentReviewPassed(stale), "review invalid after recrop");
    stale = session;
    stale["last_render"]["render_id"] = "new-camera";
    Check(!agent::CurrentReviewPassed(stale), "review invalid after rerender");
    auto editedArgs = args;
    editedArgs["changes"] = {{{"path", "face.shapeValueFace[0]"}, {"value", .7}}};
    editedArgs["mark_best"] = true;
    auto edited = service.Call("hs2_apply_changes", editedArgs);
    Check(edited.at("best_revision") == 0 && edited.at("best_candidate_revision") == 1 &&
              !edited.contains("visual_review"),
          "unrendered edits cannot replace reviewed best");
    marked["expected_revision"] = 1;
    service.Call("hs2_set_reference_regions", marked);
    Reject(
        [&]
        {
            service.Call("hs2_review_render", review);
        });
    service.Stop();
    std::cout << "PASS: reference crops, comparison images, multi-view probes, review gates and stale evidence\n";
}
