#include "VisualReview.h"
#include <set>

namespace hs2::agent
{
    namespace
    {
        const std::set<std::string> Areas{"face", "hair", "body", "outfit", "accessories", "materials"};

        bool Text(const Json &value)
        {
            return value.is_string() &&
                   value.get_ref<const std::string &>().find_first_not_of(" \t\r\n") != std::string::npos;
        }
    }

    bool ReviewView(std::string_view view)
    {
        return view == "front" || view == "side" || view == "back" || view == "face" || view == "face_side" ||
               view == "three_quarter";
    }

    Json ReviewRequirements(const Json &session)
    {
        return {{"areas", Areas},
                {"reference_regions_required", true},
                {"required_views", {"front", "side", "back", "face", "face_side"}},
                {"reference_version", session.value("reference_version", 0)},
                {"review_tool", "hs2_review_render"},
                {"automatic_similarity", false}};
    }

    Json AssessRender(const Json &session, const Json &args)
    {
        if (!session.contains("last_render") || !session.at("last_render").contains("render_id"))
            throw std::runtime_error("REVIEW_RENDER_REQUIRED：请先渲染当前版本的正侧背和正侧脸");
        const auto &render = session.at("last_render");
        if (session.value("render_revision", -1) != session.at("revision").get<int>() ||
            args.at("render_id") != render.at("render_id") ||
            render.value("reference_version", -1) != session.value("reference_version", 0))
            throw std::runtime_error("REVIEW_STALE：请评估当前版本和当前参考区域的最新渲染");
        auto references = session.value("reference_regions", Json::array());
        if (references.empty())
            throw std::runtime_error("REFERENCE_REGIONS_REQUIRED：请先指定原图的可见视图区域");
        std::set<std::string> views, compared;
        for (const auto &image : render.at("images"))
            views.insert(image.at("view"));
        for (const auto &image : render.at("comparison_images"))
            compared.insert(image.at("view"));
        for (const auto &reference : references)
            if (!compared.contains(reference.at("view").get<std::string>()))
                throw std::runtime_error("REVIEW_VIEWS_MISSING：请渲染所有已标注的参考视图");
        auto requirements = ReviewRequirements(session);
        for (const auto &view : requirements.at("required_views"))
            if (!views.contains(view.get<std::string>()))
                throw std::runtime_error("REVIEW_VIEWS_MISSING：需同时检查正面、侧面、背面、正脸和侧脸");
        if (render.value("clothing_state", 0) != 0 || render.value("sample_time", 0.f) != 0.f)
            throw std::runtime_error("REVIEW_BASE_RENDER_REQUIRED：验收使用完整穿着的静态渲染，动态采样另行检查");
        if (!render.at("diagnostics").empty())
            throw std::runtime_error("REVIEW_RENDER_DIAGNOSTICS：先处理原生渲染诊断");
        const auto &areas = args.at("areas");
        if (!areas.is_array() || areas.size() != Areas.size())
            throw std::invalid_argument("需要逐项评估 face/hair/body/outfit/accessories/materials");
        bool passed = true, observed = false;
        std::set<std::string> seen;
        for (const auto &area : areas)
        {
            auto name = area.at("area").get<std::string>();
            auto status = area.at("status").get<std::string>();
            if (!Areas.contains(name) || !seen.insert(name).second ||
                (status != "matched" && status != "needs_work" && status != "not_visible"))
                throw std::invalid_argument("视觉评估类别重复或状态无效");
            if (!Text(area.at("reference")) || !Text(area.at("observed")))
                throw std::invalid_argument("每项需记录参考特征和渲染观察；不可用空说明通过");
            if (status == "needs_work")
            {
                passed = false;
                if (!area.contains("correction") || !Text(area.at("correction")))
                    throw std::invalid_argument("待修项需要具体 correction");
            }
            observed |= status != "not_visible";
        }
        if (!observed)
            throw std::invalid_argument("不能将所有参考特征都标记为不可见");
        if (!args.at("unseen_regions").is_string())
            throw std::invalid_argument("unseen_regions 记录遮挡补全，无补全部位可留空");
        return {{"revision", session.at("revision")},
                {"render_id", render.at("render_id")},
                {"reference_version", session.value("reference_version", 0)},
                {"status", passed ? "passed" : "needs_work"},
                {"areas", areas},
                {"unseen_regions", args.at("unseen_regions")},
                {"reviewer", "caller_ai"},
                {"created_utc", UtcNow()}};
    }

    bool CurrentReviewPassed(const Json &session)
    {
        const auto review = session.value("visual_review", Json::object());
        const auto render = session.value("last_render", Json::object());
        return review.is_object() && review.value("status", "") == "passed" &&
               review.value("revision", -1) == session.at("revision").get<int>() &&
               session.value("render_revision", -1) == session.at("revision").get<int>() &&
               review.value("reference_version", -1) == session.value("reference_version", 0) &&
               !review.value("render_id", "").empty() && review.value("render_id", "") == render.value("render_id", "");
    }
}
