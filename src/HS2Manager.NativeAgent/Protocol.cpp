#include "Agent.h"

namespace hs2::agent
{
    Json Service::Tools()
    {
        auto string = Json{{"type", "string"}};
        auto integer = Json{{"type", "integer"}};
        auto object = Json{{"type", "object"}};
        Json tools = Json::array();
        auto add = [&](std::string name, std::string description, Json properties, Json required, bool read)
        {
            tools.push_back(
                {{"name", name},
                 {"description", description},
                 {"inputSchema",
                  {{"type", "object"},
                   {"properties", properties},
                   {"required", required},
                   {"additionalProperties", false}}},
                 {"annotations", {{"readOnlyHint", read}, {"destructiveHint", false}, {"openWorldHint", false}}}});
        };
        Json session{{"session_id", string}};
        Json revision = session;
        revision["expected_revision"] = integer;
        auto fields = [&](Json value)
        {
            auto result = revision;
            result.update(value);
            return result;
        };
        add("hs2_capabilities", "检查本地图片捏人环境；内容由调用方 AI 生成。", Json::object(), Json::array(), true);
        add("hs2_begin_card", "传入参考图片并选择预设男女基准卡，创建独立草稿。",
            {{"sex", {{"type", "string"}, {"enum", {"male", "female"}}}},
             {"name", string},
             {"analysis", object},
             {"reference_image",
              {{"type", "object"},
               {"properties", {{"path", string}, {"mime_type", string}, {"base64", string}}},
               {"oneOf", Json::array({Json{{"required", {"path"}}}, Json{{"required", {"mime_type", "base64"}}}})}}}},
            {"sex", "reference_image"}, false);
        add("hs2_get_session", "读取草稿、版本、分析、最佳版本和验证记录。", session, {"session_id"}, true);
        auto view = Json{{"type", "string"}, {"enum", {"front", "side", "back", "face", "face_side", "three_quarter"}}};
        add("hs2_set_reference_regions",
            "标注参考原图中的可见视图，裁剪后逐张确认。rect 为归一化 "
            "[x,y,width,height]；不推测不可见视图。更新后须重新渲染和评估。",
            fields({{"regions",
                     {{"type", "array"},
                      {"minItems", 1},
                      {"maxItems", 6},
                      {"items",
                       {{"type", "object"},
                        {"properties",
                         {{"view", view},
                          {"rect",
                           {{"type", "array"},
                            {"minItems", 4},
                            {"maxItems", 4},
                            {"items", {{"type", "number"}, {"minimum", 0}, {"maximum", 1}}}}}}},
                        {"required", {"view", "rect"}},
                        {"additionalProperties", false}}}}}}),
            {"session_id", "expected_revision", "regions"}, false);
        add("hs2_get_schema",
            "读取实际可编辑字段、类型、范围和当前值；kind 0 数值、1 颜色、2 开关、3 素材、4 选项、5 预设。", session,
            {"session_id"}, true);
        auto search = session;
        search.update(Json{
            {"category", integer}, {"query", string}, {"source", integer}, {"offset", integer}, {"limit", integer}});
        add("hs2_search_assets", "异步检索本机素材及生成素材；返回 asset_id 和缩略图。source：0 全部、1 原版、2 MOD。",
            search, {"session_id", "category"}, true);
        add("hs2_apply_changes",
            "原子修改草稿；素材值使用 asset_id，颜色使用 RGB/RGBA 数组。每次调用消耗一次修正。mark_best "
            "仅标记候选，需渲染评估后确认。",
            fields({{"changes",
                     {{"type", "array"},
                      {"minItems", 1},
                      {"items",
                       {{"type", "object"},
                        {"properties", {{"path", string}, {"value", Json::object()}}},
                        {"required", {"path", "value"}}}}}},
                    {"analysis", object},
                    {"mark_best", {{"type", "boolean"}}}}),
            {"session_id", "expected_revision", "changes"}, false);
        add("hs2_restore_revision", "将历史草稿恢复为一个新版本。", fields({{"revision", integer}}),
            {"session_id", "expected_revision", "revision"}, false);
        add("hs2_render",
            "异步原生渲染，默认正侧背及正侧脸、浅灰背景、768×1024。已标注视图返回左参考右渲染对照图；用 render_id "
            "提交逐项评估。",
            fields({{"views",
                     {{"type", "array"}, {"items", view}, {"minItems", 1}, {"maxItems", 6}, {"uniqueItems", true}}},
                    {"width", integer},
                    {"height", integer},
                    {"field_of_view", {{"type", "number"}}},
                    {"face_zoom", {{"type", "number"}, {"minimum", .5}, {"maximum", 2.5}}},
                    {"face_vertical_offset", {{"type", "number"}, {"minimum", -.5}, {"maximum", .5}}},
                    {"side", {{"type", "string"}, {"enum", {"left", "right"}}}},
                    {"background", {{"type", "string"}, {"enum", {"neutral", "game"}}}},
                    {"sample_time", {{"type", "number"}}},
                    {"clothing_state", integer}}),
            {"session_id", "expected_revision"}, true);
        add("hs2_probe_fields",
            "异步探测滑条；脸部默认正侧脸，其他默认正侧全身。delta 为范围比例，默认 0.05；不修改草稿或消耗修正。",
            fields({{"paths", {{"type", "array"}, {"items", string}, {"minItems", 1}, {"maxItems", 6}}},
                    {"delta", {{"type", "number"}, {"minimum", .01}, {"maximum", .25}}},
                    {"field_of_view", {{"type", "number"}, {"minimum", 10}, {"maximum", 90}}},
                    {"face_zoom", {{"type", "number"}, {"minimum", .5}, {"maximum", 2.5}}},
                    {"face_vertical_offset", {{"type", "number"}, {"minimum", -.5}, {"maximum", .5}}},
                    {"views",
                     {{"type", "array"}, {"items", view}, {"minItems", 1}, {"maxItems", 2}, {"uniqueItems", true}}},
                    {"side", {{"type", "string"}, {"enum", {"left", "right"}}}}}),
            {"session_id", "expected_revision", "paths"}, true);
        add("hs2_review_render",
            "查看当前渲染对照后逐项记录参考特征、观察和修正；存在 needs_work 不允许完成导出。mark_best "
            "保存已评估的最佳草稿，不能自动测量相似度。",
            fields(
                {{"render_id", string},
                 {"mark_best", {{"type", "boolean"}}},
                 {"unseen_regions", string},
                 {"areas",
                  {{"type", "array"},
                   {"minItems", 6},
                   {"maxItems", 6},
                   {"items",
                    {{"type", "object"},
                     {"properties",
                      {{"area",
                        {{"type", "string"}, {"enum", {"face", "hair", "body", "outfit", "accessories", "materials"}}}},
                       {"status", {{"type", "string"}, {"enum", {"matched", "needs_work", "not_visible"}}}},
                       {"reference", string},
                       {"observed", string},
                       {"correction", string}}},
                     {"required", {"area", "status", "reference", "observed"}},
                     {"additionalProperties", false}}}}}}),
            {"session_id", "expected_revision", "render_id", "areas", "unseen_regions"}, false);
        add("hs2_prepare_asset", "原生导出当前人物的参考 FBX、结构化网格、骨架、UV PNG 和兼容素材模板。",
            fields({{"kind", string}}), {"session_id", "expected_revision"}, false);
        add("hs2_build_asset",
            "原生异步制作 texture/accessory/hair/clothes；建模 spec 使用 schema_version:2。格式见随包 "
            "skills/hs2-card-agent/references/assets.md。三次尝试后保留草稿。",
            fields({{"spec", object}}), {"session_id", "expected_revision", "spec"}, false);
        add("hs2_validate", "检查结构、依赖与当前版本渲染状态；真实游戏验证独立标记。", revision,
            {"session_id", "expected_revision"}, true);
        add("hs2_finalize_card",
            "hs2_review_render 的当前渲染逐项评估与技术检查通过后导出独立角色包；仅 passed:true 不能替代逐项评估。",
            fields({{"output_directory", string},
                    {"visual_assessment",
                     {{"type", "object"},
                      {"properties", {{"passed", {{"type", "boolean"}}}, {"notes", string}}},
                      {"required", {"passed", "notes"}}}}}),
            {"session_id", "expected_revision", "visual_assessment"}, false);
        add("hs2_get_job", "查询异步任务；completed 的 result 含结果和可见图片。", {{"job_id", string}}, {"job_id"},
            true);
        add("hs2_cancel_job", "取消本服务创建的任务并回收其工具子进程，保留草稿。", {{"job_id", string}}, {"job_id"},
            false);
        return tools;
    }
}
