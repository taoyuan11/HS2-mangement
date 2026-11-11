#pragma once
#include "Assets/AssetReader.h"
#include <list>
#include <stop_token>
#include <unordered_set>

namespace hs2
{
    class NativeGpuAssetStore
    {
    public:
        NativeGpuAssetStore(std::vector<std::filesystem::path> bundles, std::filesystem::path game,
                            std::shared_ptr<const UnityClassDatabase> database, std::stop_token cancellation = {},
                            std::shared_ptr<UnityBlockCache> blockCache = {});
        std::shared_ptr<NativeGpuAssetReader> Reader(const std::filesystem::path &path);
        std::shared_ptr<const GpuPrefabData> Prefab(const std::filesystem::path &path, const std::string &name);
        void IndexShaderNames(const std::shared_ptr<NativeGpuAssetReader> &reader);
        std::optional<GpuAssetPointer> FindShader(const std::filesystem::path &path, std::string_view name);
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

        struct ReaderStatistics
        {
            std::size_t Loads = 0, Hits = 0, Evictions = 0;
            double LoadSeconds = 0;
            std::unordered_map<std::string, std::pair<size_t, double>> Resources;
        };

        const ReaderStatistics &ReaderStats() const
        {
            return readerStats_;
        }

        UnityBlockCache::Statistics BlockStats() const
        {
            return blockCache_ ? blockCache_->Stats() : UnityBlockCache::Statistics{};
        }

        const std::vector<std::filesystem::path> &Paths() const
        {
            return paths_;
        }

    private:
        std::filesystem::path game_;
        std::shared_ptr<const UnityClassDatabase> database_;
        std::shared_ptr<UnityBlockCache> blockCache_;
        std::stop_token cancellation_;
        std::vector<std::filesystem::path> paths_;
        std::unordered_set<std::string> pathKeys_;
        std::unordered_map<std::string, std::shared_ptr<NativeGpuAssetReader>> readers_;
        std::list<std::string> recent_;
        std::unordered_map<std::string, std::vector<std::filesystem::path>> cabs_;
        std::unordered_map<std::string, std::string> cabNames_;
        std::unordered_map<std::string, NativeGpuAssetReader::NameLookup> shaderNames_;
        std::unordered_map<std::string, std::shared_ptr<GpuMeshData>> meshes_;
        std::unordered_map<std::string, std::unordered_map<std::string, std::shared_ptr<const GpuPrefabData>>> prefabs_;
        std::unordered_map<std::string, std::shared_ptr<GpuMaterialData>> materials_;
        std::unordered_map<std::string, GpuAssetPointer> resolved_, gameResources_;
        std::unordered_map<std::string, std::optional<int>> classes_;
        size_t indexed_ = 0;
        ReaderStatistics readerStats_;
        std::function<std::vector<UnityTypeNode>(const GpuAssetPointer &, std::string_view)> scriptSchema_;
        void Cancel() const;
        void TrimReaders(const std::string &keep);
        void RememberReader(const std::string &key, const std::shared_ptr<NativeGpuAssetReader> &reader,
                            double seconds);
        void IndexCabNames();
    };
}
