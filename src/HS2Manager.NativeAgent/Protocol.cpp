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
        add("hs2_get_schema",
            "读取实际可编辑字段、类型、范围和当前值；kind 0 数值、1 颜色、2 开关、3 素材、4 选项、5 预设。", session,
            {"session_id"}, true);
        auto search = session;
        search.update(Json{
            {"category", integer}, {"query", string}, {"source", integer}, {"offset", integer}, {"limit", integer}});
        add("hs2_search_assets", "异步检索本机素材及生成素材；返回 asset_id 和缩略图。source：0 全部、1 原版、2 MOD。",
            search, {"session_id", "category"}, true);
        add("hs2_apply_changes", "原子修改草稿；素材值使用 asset_id，颜色使用 RGB/RGBA 数组。每次调用消耗一次修正。",
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
        add("hs2_render", "异步原生渲染；视图 front/side/back/face/three_quarter。返回图片供 AI 比较。",
            fields({{"views", {{"type", "array"}, {"items", string}}},
                    {"width", integer},
                    {"height", integer},
                    {"field_of_view", {{"type", "number"}}},
                    {"sample_time", {{"type", "number"}}},
                    {"clothing_state", integer}}),
            {"session_id", "expected_revision"}, true);
        add("hs2_probe_fields", "异步绘制最多六个数值字段的负向、基准、正向扰动，草稿不变。",
            fields({{"paths", {{"type", "array"}, {"items", string}, {"maxItems", 6}}}}),
            {"session_id", "expected_revision", "paths"}, true);
        add("hs2_prepare_asset", "原生导出当前人物的参考 FBX、结构化网格、骨架、UV PNG 和兼容素材模板。",
            fields({{"kind", string}}), {"session_id", "expected_revision"}, false);
        add("hs2_build_asset",
            "原生异步制作 texture/accessory/hair/clothes；建模 spec 使用 schema_version:2。格式见随包 "
            "skills/hs2-card-agent/references/assets.md。三次尝试后保留草稿。",
            fields({{"spec", object}}), {"session_id", "expected_revision", "spec"}, false);
        add("hs2_validate", "检查结构、依赖与当前版本渲染状态；真实游戏验证独立标记。", revision,
            {"session_id", "expected_revision"}, true);
        add("hs2_finalize_card", "视觉评估与技术检查通过后制作新姓名和封面，导出独立角色包。",
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
