#pragma once
#include "Assets/AssetReader.h"
#include <list>
#include <stop_token>

namespace hs2
{
    class NativeGpuAssetStore
    {
    public:
        NativeGpuAssetStore(std::vector<std::filesystem::path> bundles, std::filesystem::path game,
                            std::shared_ptr<const UnityClassDatabase> database, std::stop_token cancellation = {});
        std::shared_ptr<NativeGpuAssetReader> Reader(const std::filesystem::path &path);
        void SetScriptSchema(
            std::function<std::vector<UnityTypeNode>(const GpuAssetPointer &, std::string_view)> resolver);
        void AddPath(const std::filesystem::path &path);
        GpuAssetPointer Resolve(const GpuAssetPointer &pointer);
        std::shared_ptr<GpuMeshData> Mesh(const GpuAssetPointer &pointer);
        GpuTextureData Texture(const GpuAssetPointer &pointer);
        std::shared_ptr<GpuMaterialData> Material(const GpuAssetPointer &pointer);
        Parameter Metadata(const GpuAssetPointer &pointer);
        bool ContainsAsset(const GpuAssetPointer &pointer);
        std::optional<int> AssetClass(const GpuAssetPointer &pointer);
        GpuAssetPointer GameResource(std::string_view name, int classId, int textureSize = 0);

        const std::vector<std::filesystem::path> &Paths() const
        {
            return paths_;
        }

    private:
        std::filesystem::path game_;
        std::shared_ptr<const UnityClassDatabase> database_;
        std::stop_token cancellation_;
        std::vector<std::filesystem::path> paths_;
        std::unordered_map<std::string, std::shared_ptr<NativeGpuAssetReader>> readers_;
        std::list<std::string> recent_;
        std::unordered_map<std::string, std::vector<std::filesystem::path>> cabs_;
        std::unordered_map<std::string, std::shared_ptr<GpuMeshData>> meshes_;
        std::unordered_map<std::string, std::shared_ptr<GpuMaterialData>> materials_;
        std::unordered_map<std::string, GpuAssetPointer> resolved_, gameResources_;
        std::unordered_map<std::string, std::optional<int>> classes_;
        size_t indexed_ = 0;
        std::function<std::vector<UnityTypeNode>(const GpuAssetPointer &, std::string_view)> scriptSchema_;
        void Cancel() const;
    };
}
