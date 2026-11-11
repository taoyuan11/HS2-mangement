#pragma once
#include "Common/Core.h"
#include "Scene/SceneTypes.h"

namespace hs2
{
    enum class ModelExportTarget
    {
        GenericFbx,
        Blender,
        Unreal
    };
    enum class ModelExportStatus
    {
        Complete,
        Incomplete,
        Failed,
        Cancelled
    };

    struct ModelExportView
    {
        bool HasCamera = false;
        Vector3 Position{}, Target{}, Up{0, 1, 0};
        float FieldOfView = 35;
        int Width = 1280, Height = 960;
        bool UseGameBackground = true;
        Vector4 Background{.16f, .17f, .19f, 1};
        float KeyLightMultiplier = 1;
    };

    struct ModelExportOptions
    {
        int Clothing = 0;
        bool Accessories = true;
        ModelExportTarget Target = ModelExportTarget::GenericFbx;
        std::string BlenderPath, UnrealEditorPath;
        int ToolTimeoutSeconds = 900;
        ModelExportView View;
    };

    struct ModelExportSource
    {
        CardRecord Card;
        std::optional<CharacterDocument> Draft;
        std::int64_t Revision = 0;
    };

    struct ModelExportItem
    {
        std::string Source, Directory;
        ModelExportStatus Status = ModelExportStatus::Failed;
        std::string Message;
    };

    struct ModelExportReport
    {
        std::vector<ModelExportItem> Items;
        bool Cancelled = false;
        int Requested = 0;
        std::string ReportPath;
        std::string Summary() const;
    };

    class ModelExportTools
    {
    public:
        static std::string Name(ModelExportTarget target);
        static std::string Detect(ModelExportTarget target);
        static std::string Resolve(const ModelExportOptions &options);
    };

    // 在工作线程调用 Export；
    // D3D 设备、资源读取器和工具进程仅属于本次调用，并在返回前释放。
    class ModelExportService
    {
    public:
        // 运行目录包含 Resources/classdata.tpk、native/win-x64 下的 FBX 适配层，以及 Resources/ModelExport 下的转换模板。
        explicit ModelExportService(fs::path runtimeDirectory);
        ModelExportReport Export(const std::vector<ModelExportSource> &sources, const fs::path &game,
                                 const fs::path &data, const fs::path &output, ModelExportOptions options = {},
                                 Progress progress = {}, std::stop_token cancellation = {});
        static void ValidateView(const ModelExportView &view);

    private:
        fs::path runtime_;
    };

    void to_json(Json &, const ModelExportView &);
}
