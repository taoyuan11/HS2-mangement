#include "Agent.h"
#include "Update.h"
#include <windows.h>
#include <objbase.h>
#include <iostream>
#include <set>

using namespace hs2;

namespace
{
    void Images(const Json &value, Json &content, std::set<std::string> &seen)
    {
        if (value.is_object())
        {
            if (value.value("mime_type", "") == "image/png" && value.contains("path") && content.size() < 9)
            {
                auto path = value.at("path").get<std::string>();
                if (seen.insert(path).second)
                    content.push_back({{"type", "image"},
                                       {"mimeType", "image/png"},
                                       {"data", agent::Base64(ReadFile(Path(path), 32 * 1024 * 1024))}});
            }
            // 优先展示本次结果和对照图，避免历史素材缩略图占满图片额度。
            if (value.contains("result"))
                Images(value.at("result"), content, seen);
            bool comparisons = value.contains("comparison_images") && !value.at("comparison_images").empty();
            if (comparisons)
                Images(value.at("comparison_images"), content, seen);
            else if (value.contains("images"))
                Images(value.at("images"), content, seen);
            for (auto &[key, child] : value.items())
            {
                if (key == "result" || key == "images" || key == "comparison_images" || key == "session" ||
                    key == "assets" || key == "reviews" || key == "last_render" || key == "reference_assets")
                    continue;
                if (key == "probes" && value.contains("images"))
                    continue;
                if (child.is_structured())
                    Images(child, content, seen);
            }
        }
        else if (value.is_array())
            for (auto &child : value)
                Images(child, content, seen);
    }
}

int wmain(int argc, wchar_t **argv)
{
    HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    int exitCode = 0;
    try
    {
        fs::path configPath;
        bool check = false;
        for (int i = 1; i < argc; ++i)
        {
            std::wstring argument = argv[i];
            if (argument == L"--config" && i + 1 < argc)
                configPath = argv[++i];
            else if (argument == L"--check")
                check = true;
            else
                throw std::invalid_argument("用法：HS2Agent.exe --config <agent.json> [--check]");
        }
        if (configPath.empty())
            throw std::invalid_argument("请通过 --config 指定 Agent 配置文件");
        wchar_t executable[32768]{};
        GetModuleFileNameW(nullptr, executable, 32768);
        auto installationLease = update::InstallationLease(fs::path(executable).parent_path());
        auto config = agent::ReadJson(configPath);
        auto runtime = config.contains("runtime_directory") ? Path(config.at("runtime_directory"))
                                                            : fs::path(executable).parent_path();
        update::Handle runtimeLease;
        if (fs::weakly_canonical(runtime) != fs::weakly_canonical(fs::path(executable).parent_path()))
            runtimeLease = update::InstallationLease(runtime);
        agent::Service service(config, runtime);
        if (check)
            std::cout << service.Call("hs2_capabilities", Json::object()).dump(2) << std::endl;
        else
        {
            // stdout 仅承载换行分隔的 JSON-RPC，诊断写 stderr 或会话文件。
            std::string line;
            while (std::getline(std::cin, line))
            {
                Json request, response{{"jsonrpc", "2.0"}, {"id", nullptr}};
                try
                {
                    if (line.size() > 48 * 1024 * 1024)
                        throw std::invalid_argument("请求过大");
                    request = Json::parse(line);
                    if (!request.is_object() || request.value("jsonrpc", "") != "2.0")
                        throw std::invalid_argument("JSON-RPC 请求无效");
                    if (!request.contains("id"))
                        continue;
                    response["id"] = request["id"];
                    auto method = request.at("method").get<std::string>();
                    if (method == "initialize")
                        response["result"] = {
                            {"protocolVersion", "2025-11-25"},
                            {"capabilities", {{"tools", Json::object()}}},
                            {"serverInfo", {{"name", "hs2-card-agent"}, {"version", "1.1.0"}}},
                            {"instructions",
                             "按 skills/hs2-card-agent/SKILL.md 完成图片创建人物卡。模型推理由客户端负责。耗时工具返回 "
                             "job_id，需查询直到完成或失败。先用 hs2_set_reference_regions 标注参考视图，"
                             "用渲染对照图和 hs2_review_render 逐项修正；打包成功不代表外观还原。"}};
                    else if (method == "ping")
                        response["result"] = Json::object();
                    else if (method == "tools/list")
                        response["result"] = {{"tools", agent::Service::Tools()}};
                    else if (method == "tools/call")
                    {
                        auto params = request.at("params");
                        auto name = params.at("name").get<std::string>();
                        bool exists = false;
                        for (auto &tool : agent::Service::Tools())
                            exists |= tool["name"] == name;
                        if (!exists)
                            throw std::invalid_argument("未知工具");
                        try
                        {
                            auto result = service.Call(name, params.value("arguments", Json::object()));
                            Json content = Json::array({Json{{"type", "text"}, {"text", result.dump()}}});
                            std::set<std::string> seen;
                            Images(result, content, seen);
                            response["result"] = {
                                {"content", content}, {"structuredContent", result}, {"isError", false}};
                        }
                        catch (const std::exception &e)
                        {
                            response["result"] = {{"content", {{{"type", "text"}, {"text", e.what()}}}},
                                                  {"isError", true}};
                        }
                    }
                    else
                        response["error"] = {{"code", -32601}, {"message", "Method not found"}};
                }
                catch (const Json::parse_error &e)
                {
                    response["error"] = {{"code", -32700}, {"message", e.what()}};
                }
                catch (const std::exception &e)
                {
                    response["error"] = {{"code", -32602}, {"message", e.what()}};
                }
                std::cout << response.dump() << std::endl;
            }
        }
        service.Stop();
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << std::endl;
        exitCode = 1;
    }
    if (SUCCEEDED(com))
        CoUninitialize();
    return exitCode;
}
