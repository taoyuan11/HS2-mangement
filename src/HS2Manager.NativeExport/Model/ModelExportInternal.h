#pragma once
#include "Model/ModelExport.h"
#include "Character/CharacterPreparation.h"
#include "Device/GraphicsDevice.h"

namespace hs2
{
    // 转换器拥有所有外部进程，并在返回或抛出异常前将其停止；
    // 结果沿用现有 ModelExportResult JSON 结构。
    void ProbeModelExport(const ModelExportOptions &options, const fs::path &executable, const fs::path &runtime,
                          const fs::path &workspace, const fs::path &data, std::stop_token cancellation = {});
    void ConvertModelExport(const ModelExportOptions &options, const fs::path &executable, const fs::path &runtime,
                            const fs::path &workspace, const fs::path &data, const fs::path &source,
                            const fs::path &output, Json &result, std::stop_token cancellation = {});
    Json ExportNativeCharacter(GraphicsDevice &graphics, NativeCharacterScene &scene, NativeGpuAssetStore &assets,
                               const fs::path &bridge, const fs::path &directory, std::int64_t revision,
                               Progress progress = {}, std::stop_token cancellation = {});
    std::vector<std::string> ExportNativeScene(GraphicsDevice &graphics, NativeCharacterScene &scene,
                                               NativeGpuAssetStore &assets, const fs::path &directory,
                                               ModelExportView view, bool includeScene, Progress progress = {},
                                               std::stop_token cancellation = {});
}
