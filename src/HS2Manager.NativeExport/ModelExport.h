#pragma once
#include "../HS2Manager.NativeCore/Core.h"
#include "../HS2Manager.NativeScene/SceneTypes.h"
namespace hs2 {
enum class ModelExportTarget { GenericFbx,Blender,Unreal };
enum class ModelExportStatus { Complete,Incomplete,Failed,Cancelled };
struct ModelExportView {
    bool HasCamera=false;
    Vector3 Position{},Target{},Up{0,1,0};
    float FieldOfView=35;
    int Width=1280,Height=960;
    bool UseGameBackground=true;
    Vector4 Background{.16f,.17f,.19f,1};
    float KeyLightMultiplier=1;
};
struct ModelExportOptions {
    int Clothing=0;
    bool Accessories=true;
    ModelExportTarget Target=ModelExportTarget::GenericFbx;
    std::string BlenderPath,UnrealEditorPath;
    int ToolTimeoutSeconds=900;
    ModelExportView View;
};
struct ModelExportSource {CardRecord Card;std::optional<CharacterDocument> Draft;std::int64_t Revision=0;};
struct ModelExportItem {std::string Source,Directory;ModelExportStatus Status=ModelExportStatus::Failed;std::string Message;};
struct ModelExportReport {
    std::vector<ModelExportItem> Items;
    bool Cancelled=false;
    int Requested=0;
    std::string ReportPath;
    std::string Summary()const;
};
class ModelExportTools {
public:
    static std::string Name(ModelExportTarget target);
    static std::string Detect(ModelExportTarget target);
    static std::string Resolve(const ModelExportOptions& options);
};
// Call Export on a worker thread. Its D3D device, asset readers and owned tool
// processes remain confined to that call and are released before it returns.
class ModelExportService {
public:
    // Runtime directory contains Resources/classdata.tpk, the FBX bridge under
    // native/win-x64, and converter templates under Resources/ModelExport.
    explicit ModelExportService(fs::path runtimeDirectory);
    ModelExportReport Export(const std::vector<ModelExportSource>& sources,const fs::path& game,const fs::path& data,const fs::path& output,
        ModelExportOptions options={},Progress progress={},std::stop_token cancellation={});
    static void ValidateView(const ModelExportView& view);
private:fs::path runtime_;
};
void to_json(Json&,const ModelExportView&);
}
