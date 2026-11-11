#include "Model/ModelExportInternal.h"
#include "Export/CardExport.h"
#include <windows.h>
#include <algorithm>
#include <cmath>
#include <set>

namespace hs2
{
    namespace
    {
        struct ComApartment
        {
            bool Owned = false;

            ComApartment()
            {
                auto result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
                Owned = SUCCEEDED(result);
                if (FAILED(result) && result != RPC_E_CHANGED_MODE)
                    CheckHResult(result, "Model export COM initialization");
            }

            ~ComApartment()
            {
                if (Owned)
                    CoUninitialize();
            }
        };

        void WriteText(const fs::path &path, const std::string &text)
        {
            ExportPaths::NoLinks(path);
            WriteAtomic(path, {reinterpret_cast<const std::uint8_t *>(text.data()), text.size()});
        }

        void RemoveOwned(const fs::path &owner, const fs::path &path)
        {
            auto root = fs::absolute(owner).lexically_normal(), target = fs::absolute(path).lexically_normal();
            if (SamePath(root, target) || !Within(target, root))
                throw std::runtime_error("拒绝清理工作目录之外的文件。");
            if (!fs::exists(target))
                return;
            ExportPaths::NoLinks(target);
            fs::remove_all(target);
        }

        struct OwnedDirectory
        {
            fs::path Owner, Path;

            ~OwnedDirectory()
            {
                try
                {
                    if (!Path.empty())
                        RemoveOwned(Owner, Path);
                }
                catch (...)
                {
                }
            };

            void Clear()
            {
                if (!Path.empty())
                {
                    RemoveOwned(Owner, Path);
                    Path.clear();
                }
            }
        };

        std::string StatusName(ModelExportStatus value)
        {
            switch (value)
            {
            case ModelExportStatus::Complete:
                return "完成";
            case ModelExportStatus::Incomplete:
                return "存在缺失";
            case ModelExportStatus::Failed:
                return "失败";
            case ModelExportStatus::Cancelled:
                return "取消";
            }
            throw std::invalid_argument("Invalid model export status.");
        }

        fs::path Publish(const fs::path &staging, const fs::path &output, const std::string &name)
        {
            for (int i = 0; i < 100; ++i)
            {
                auto target = output / Path(ExportPaths::UniqueDirectoryName(output, name, {}));
                ExportPaths::NoLinks(target);
                if (MoveFileExW(staging.c_str(), target.c_str(), MOVEFILE_WRITE_THROUGH))
                    return target;
                auto error = GetLastError();
                if (!fs::exists(target))
                    throw std::system_error(error, std::system_category(), "发布模型导出包");
            }
            throw std::runtime_error("无法分配模型导出目录。");
        }
    }

    void to_json(Json &j, const ModelExportView &v)
    {
        j = {{"HasCamera", v.HasCamera},
             {"Position", {v.Position.x, v.Position.y, v.Position.z}},
             {"Target", {v.Target.x, v.Target.y, v.Target.z}},
             {"Up", {v.Up.x, v.Up.y, v.Up.z}},
             {"FieldOfView", v.FieldOfView},
             {"Width", v.Width},
             {"Height", v.Height},
             {"UseGameBackground", v.UseGameBackground},
             {"Background", {v.Background.x, v.Background.y, v.Background.z, v.Background.w}},
             {"KeyLightMultiplier", v.KeyLightMultiplier}};
    }

    std::string ModelExportReport::Summary() const
    {
        std::string text = std::string("3D 模型导出") + (Cancelled ? "已取消" : "结束") + "：";
        for (auto status : {ModelExportStatus::Complete, ModelExportStatus::Incomplete, ModelExportStatus::Failed,
                            ModelExportStatus::Cancelled})
        {
            if (status != ModelExportStatus::Complete)
                text += "，";
            text += StatusName(status) + " " +
                    std::to_string(std::count_if(Items.begin(), Items.end(),
                                                 [&](auto &item)
                                                 {
                                                     return item.Status == status;
                                                 }));
        }
        return text + "，未处理 " + std::to_string(std::max(0, Requested - static_cast<int>(Items.size())));
    }

    ModelExportService::ModelExportService(fs::path runtimeDirectory) : runtime_(fs::absolute(runtimeDirectory))
    {
    }

    void ModelExportService::ValidateView(const ModelExportView &v)
    {
        auto finite = [](Vector3 a)
        {
            return std::isfinite(a.x) && std::isfinite(a.y) && std::isfinite(a.z);
        };
        if (!finite(v.Position) || !finite(v.Target) || !finite(v.Up) ||
            !finite({v.Background.x, v.Background.y, v.Background.z}) || !std::isfinite(v.Background.w) ||
            !std::isfinite(v.FieldOfView) || v.FieldOfView < 5 || v.FieldOfView > 100 || v.Width < 64 ||
            v.Width > 8192 || v.Height < 64 || v.Height > 8192 || !std::isfinite(v.KeyLightMultiplier) ||
            v.KeyLightMultiplier < 0 || v.KeyLightMultiplier > 4)
            throw std::invalid_argument("无效的模型预览场景参数。");
        if (v.HasCamera)
        {
            Vector3 direction{v.Target.x - v.Position.x, v.Target.y - v.Position.y, v.Target.z - v.Position.z};
            Vector3 cross{direction.y * v.Up.z - direction.z * v.Up.y, direction.z * v.Up.x - direction.x * v.Up.z,
                          direction.x * v.Up.y - direction.y * v.Up.x};
            if (direction.x == 0 && direction.y == 0 && direction.z == 0)
                throw std::invalid_argument("模型预览相机不能与观察点重合。");
            if (cross.x * cross.x + cross.y * cross.y + cross.z * cross.z < 1e-16f)
                throw std::invalid_argument("模型预览相机朝向与向上轴不能平行。");
        }
    }

    ModelExportReport ModelExportService::Export(const std::vector<ModelExportSource> &sources,
                                                 const fs::path &gamePath, const fs::path &dataPath,
                                                 const fs::path &outputPath, ModelExportOptions options,
                                                 Progress progress, std::stop_token cancellation)
    {
        ComApartment apartment;
        if (options.Clothing < 0 || options.Clothing > 2 ||
            (options.Target != ModelExportTarget::GenericFbx && options.Target != ModelExportTarget::Blender &&
             options.Target != ModelExportTarget::Unreal) ||
            options.ToolTimeoutSeconds < 30 || options.ToolTimeoutSeconds > 7200)
            throw std::invalid_argument("无效的模型导出选项。");
        ValidateView(options.View);
        auto executable = Path(ModelExportTools::Resolve(options));
        auto game = fs::absolute(gamePath), data = fs::absolute(dataPath), output = fs::absolute(outputPath);
        CardExportService::ValidateOutput(game, output);
        ExportPaths::NoLinks(data);
        fs::create_directories(output);
        std::vector<const ModelExportSource *> items;
        for (auto &source : sources)
            if (std::none_of(items.begin(), items.end(),
                             [&](auto *other)
                             {
                                 return SamePath(Path(other->Card.Path), Path(source.Card.Path));
                             }))
                items.push_back(&source);
        if (items.empty())
            throw std::invalid_argument("请先选择人物卡。");
        auto classDatabase = runtime_ / "classdata.tpk";
        if (!fs::is_regular_file(classDatabase))
            classDatabase = runtime_ / "Resources/classdata.tpk";
        if (!fs::is_regular_file(classDatabase))
            throw std::runtime_error("原生导出组件缺失：classdata.tpk");
        auto bridge = runtime_ / "native/win-x64/HS2Manager.FbxBridge.dll";
        if (!fs::is_regular_file(bridge))
            throw std::runtime_error("原生导出组件缺失：HS2Manager.FbxBridge.dll");
        ModelExportReport report;
        report.Requested = static_cast<int>(items.size());
        auto sessions = data / "ModelExport/sessions";
        auto workspace = sessions / Path(NewId());
        fs::create_directories(workspace);
        OwnedDirectory session{sessions, workspace};
        NativeCharacterService characters(game, data, classDatabase);
        try
        {
            if (options.Target != ModelExportTarget::GenericFbx)
            {
                if (progress)
                    progress({0, report.Requested, "检查 " + ModelExportTools::Name(options.Target) + " 转换环境…"});
                ProbeModelExport(options, executable, runtime_, workspace, data, cancellation);
            }
            for (std::size_t index = 0; index < items.size(); ++index)
            {
                auto &source = *items[index];
                auto staging = output / Path(".hs2-model-" + NewId());
                auto intermediate = options.Target == ModelExportTarget::GenericFbx ? staging : workspace / "character";
                OwnedDirectory staged{output, staging};
                OwnedDirectory temporary{workspace,
                                         options.Target == ModelExportTarget::GenericFbx ? fs::path{} : intermediate};
                try
                {
                    CheckCancelled(cancellation);
                    auto card = source.Draft ? source.Card : CardParser().Parse(Path(source.Card.Path), cancellation);
                    if (!source.Draft && card.Status != CardStatus::Valid)
                        throw std::runtime_error("不是有效人物卡：" + card.Error);
                    auto stageProgress = [&](const WorkProgress &work)
                    {
                        if (progress)
                            progress({static_cast<int>(index), report.Requested,
                                      source.Card.FileName() + " · " + work.Message});
                    };
                    auto prepared =
                        characters.Prepare(card, source.Draft ? &*source.Draft : nullptr,
                                           {options.Clothing, options.Accessories}, stageProgress, cancellation);
                    prepared.Scene->Definition->EditRevision = source.Revision;
                    ApplyCharacterReferenceHeels(*prepared.Scene);
                    fs::create_directory(staging);
                    fs::create_directories(intermediate);
                    GraphicsDevice graphics;
                    auto result = ExportNativeCharacter(graphics, *prepared.Scene, *prepared.Assets, bridge,
                                                        intermediate, source.Revision, stageProgress, cancellation);
                    auto sceneWarnings =
                        ExportNativeScene(graphics, *prepared.Scene, *prepared.Assets, intermediate, options.View,
                                          options.Target != ModelExportTarget::GenericFbx, stageProgress, cancellation);
                    for (auto &warning : sceneWarnings)
                        if (std::find(result["Warnings"].begin(), result["Warnings"].end(), warning) ==
                            result["Warnings"].end())
                            result["Warnings"].push_back(warning);
                    WriteJson(intermediate / "model-result.json", result);
                    if (result.value("Meshes", 0) < 1 || result.value("Bones", 0) < 1 ||
                        result.value("Vertices", 0) < 1 || fs::file_size(intermediate / "character.fbx") < 100)
                        throw std::runtime_error("模型结构校验失败。");
                    if (options.Target != ModelExportTarget::GenericFbx)
                    {
                        stageProgress({0, 0, "生成并重新打开 " + ModelExportTools::Name(options.Target) + "…"});
                        ConvertModelExport(options, executable, runtime_, workspace, data, intermediate, staging,
                                           result, cancellation);
                    }
                    for (auto &file : fs::recursive_directory_iterator(staging))
                    {
                        ExportPaths::NoLinks(file.path());
                        if (file.is_regular_file() && file.path().extension() == L".py")
                            throw std::runtime_error("交付包不应包含转换脚本。");
                    }
                    WriteJson(staging / "model-result.json", result);
                    auto state =
                        result.at("Issues").empty() ? ModelExportStatus::Complete : ModelExportStatus::Incomplete;
                    std::string detail =
                        (state == ModelExportStatus::Complete ? std::string("导出完成") : std::string("导出存在缺失")) +
                        " · " + ModelExportTools::Name(options.Target) + "\n" + source.Card.FileName() + "\n打开：" +
                        result.value("EntryPoint", "") + "\n生成软件版本：" + result.value("ToolVersion", "") +
                        "\n草稿版本：" + std::to_string(source.Revision) + "\n网格 " +
                        std::to_string(result.value("Meshes", 0)) + "，顶点 " +
                        std::to_string(result.value("Vertices", 0)) + "，骨骼 " +
                        std::to_string(result.value("Bones", 0)) + "，表情 " +
                        std::to_string(result.value("BlendShapes", 0)) + "\n";
                    for (auto key : {"Issues", "Warnings"})
                        for (auto &entry : result.at(key))
                            detail += entry.get<std::string>() + "\n";
                    WriteText(staging / Path("导出报告.txt"), detail);
                    Json files = Json::array();
                    for (auto &file : fs::recursive_directory_iterator(staging))
                        if (file.is_regular_file())
                        {
                            CheckCancelled(cancellation);
                            files.push_back({{"Path", Utf8(fs::relative(file.path(), staging))},
                                             {"Size", file.file_size()},
                                             {"Sha256", HashFile(file.path(), cancellation)}});
                        }
                    WriteJson(staging / "files.json", files);
                    auto name = ExportPaths::SafeName(card.Name.empty() ? Utf8(Path(card.Path).stem()) : card.Name);
                    CheckCancelled(cancellation);
                    auto target = Publish(staging, output, name);
                    staged.Path.clear();
                    report.Items.push_back({source.Card.Path, Utf8(target), state, detail});
                }
                catch (const Cancelled &)
                {
                    report.Cancelled = true;
                    report.Items.push_back(
                        {source.Card.Path, {}, ModelExportStatus::Cancelled, "已取消，未发布此角色。"});
                    break;
                }
                catch (const std::exception &error)
                {
                    if (cancellation.stop_requested())
                    {
                        report.Cancelled = true;
                        report.Items.push_back(
                            {source.Card.Path, {}, ModelExportStatus::Cancelled, "已取消，未发布此角色。"});
                        break;
                    }
                    report.Items.push_back({source.Card.Path, {}, ModelExportStatus::Failed, error.what()});
                }
                staged.Clear();
                temporary.Clear();
                if (progress)
                    progress({static_cast<int>(index + 1), report.Requested, "已处理 " + source.Card.FileName()});
            }
        }
        catch (const Cancelled &)
        {
            report.Cancelled = true;
        }
        session.Clear();
        auto timestamp = UtcNow();
        std::replace(timestamp.begin(), timestamp.end(), ':', '-');
        report.ReportPath = Utf8(output / Path("模型导出报告-" + timestamp + "-" + NewId().substr(0, 6) + ".txt"));
        std::string summary = report.Summary() + "\n\n";
        for (auto &item : report.Items)
            summary += item.Source + "\n" + item.Directory + "\n" + item.Message + "\n\n";
        WriteText(Path(report.ReportPath), summary);
        if (progress)
            progress({static_cast<int>(report.Items.size()), report.Requested, report.Summary()});
        return report;
    }
}
