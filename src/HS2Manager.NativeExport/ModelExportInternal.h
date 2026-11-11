#pragma once
#include "ModelExport.h"
#include "../HS2Manager.NativeScene/CharacterPreparation.h"
#include "../HS2Manager.NativeRendering/GraphicsDevice.h"
namespace hs2 {
// Converter owns and stops every external process before returning or throwing.
// The result object uses the existing ModelExportResult JSON schema.
void ProbeModelExport(const ModelExportOptions& options,const fs::path& executable,const fs::path& runtime,
    const fs::path& workspace,const fs::path& data,std::stop_token cancellation={});
void ConvertModelExport(const ModelExportOptions& options,const fs::path& executable,const fs::path& runtime,
    const fs::path& workspace,const fs::path& data,const fs::path& source,const fs::path& output,
    Json& result,std::stop_token cancellation={});
Json ExportNativeCharacter(GraphicsDevice& graphics,NativeCharacterScene& scene,NativeGpuAssetStore& assets,
    const fs::path& bridge,const fs::path& directory,std::int64_t revision,Progress progress={},std::stop_token cancellation={});
std::vector<std::string> ExportNativeScene(GraphicsDevice& graphics,NativeCharacterScene& scene,NativeGpuAssetStore& assets,
    const fs::path& directory,ModelExportView view,bool includeScene,Progress progress={},std::stop_token cancellation={});
}
