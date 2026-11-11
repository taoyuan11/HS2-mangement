#pragma once
#include "Mods/ModIndex.h"
#include <map>

namespace hs2
{
    class TemporaryPreviewStorage;

    // 只读资源视图；持有临时目录租约，确保渲染及设备恢复结束前资源始终存在。
    struct PreviewResourceContext
    {
        std::string Id = NewId();
        fs::path Game, Data, Package;
        // 已安装游戏资源的清单缓存跨临时预览复用；包内资源仍使用会话目录。
        fs::path CatalogCache;
        std::vector<fs::path> Roots;
        ModIndex Index;
        std::map<std::string, fs::path> Files;
        std::map<fs::path, size_t> Priorities;
        std::shared_ptr<TemporaryPreviewStorage> Storage;

        std::optional<fs::path> Loose(std::string relative) const;
        fs::path File(std::string relative) const;
        std::optional<fs::path> Plugin(std::wstring_view name) const;
        std::vector<fs::path> Enumerate(std::string relative) const;
        size_t Priority(const fs::path &path) const;
    };

    using PreviewResources = std::shared_ptr<const PreviewResourceContext>;
    fs::path PreviewResourceFile(const fs::path &game, const PreviewResources &resources, std::string relative);
}
