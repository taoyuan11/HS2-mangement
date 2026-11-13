#pragma once
#include "Scene/SceneTypes.h"
#include "Assets/UnityBlockCache.h"
#include <functional>
#include <array>

namespace hs2
{
    // 分配内存或创建跨度视图之前，先检查所有长度和偏移量。
    std::vector<std::uint8_t> DecompressUnityBlock(std::span<const std::uint8_t> compressed, std::size_t outputSize,
                                                   unsigned compression);

    struct UnityBundleEntry
    {
        std::string Name;
        std::uint64_t Offset = 0, Size = 0;
        std::uint32_t Flags = 0;
    };

    class UnityBundle
    {
    public:
        explicit UnityBundle(std::span<const std::uint8_t> bytes, std::shared_ptr<UnityBlockCache> cache = {},
                             std::stop_token cancellation = {});
        explicit UnityBundle(const std::filesystem::path &path, std::shared_ptr<UnityBlockCache> cache = {},
                             std::stop_token cancellation = {});

        const std::vector<UnityBundleEntry> &Entries() const noexcept
        {
            return entries_;
        }

        std::span<const std::uint8_t> Entry(std::string_view name) const;
        std::span<const std::uint8_t> Entry(std::size_t index) const;
        std::vector<std::uint8_t> ReadEntry(std::string_view name, std::uint64_t offset, std::uint64_t size) const;
        std::size_t ResidentBytes() const;
        std::string UnityVersion;

    private:
        struct Block
        {
            std::size_t Offset = 0, PackedOffset = 0;
            std::uint32_t Size = 0, PackedSize = 0;
            unsigned Compression = 0;
        };

        std::vector<UnityBundleEntry> entries_;
        std::vector<Block> blocks_;
        struct Source;
        mutable std::shared_ptr<Source> source_;
        mutable std::unordered_map<std::size_t, std::vector<std::uint8_t>> entryData_;
        mutable std::size_t recentBlock_ = SIZE_MAX;
        mutable std::shared_ptr<const UnityBlockCache::Bytes> blockData_;
        std::shared_ptr<UnityBlockCache> cache_;
        std::stop_token cancellation_;
        void ReadDirectory();
        std::vector<std::uint8_t> ReadRange(std::uint64_t offset, std::uint64_t size) const;
    };

    struct UnityTypeNode
    {
        std::string Type, Name;
        int Size = -1;
        std::uint32_t Flags = 0;
        std::uint8_t Level = 0;
        std::uint16_t Version = 1;
        std::uint8_t TypeFlags = 0;
        std::int32_t Index = 0;
        std::uint64_t ReferenceTypeHash = 0;
    };

    class UnityClassDatabase
    {
    public:
        explicit UnityClassDatabase(const std::filesystem::path &tpk);
        std::vector<UnityTypeNode> Type(int classId, std::string_view unityVersion) const;

    private:
        struct TypeVersion
        {
            std::uint64_t Version = 0;
            std::uint16_t Root = 65535;
        };

        struct Node
        {
            std::uint16_t Type = 0, Name = 0;
            int Size = -1;
            std::uint32_t Flags = 0;
            std::vector<std::uint16_t> Children;
        };

        std::unordered_map<int, std::vector<TypeVersion>> types_;
        std::vector<Node> nodes_;
        std::vector<std::string> strings_;
    };

    struct UnityAssetInfo
    {
        std::int64_t PathId = 0;
        int ClassId = 0;
        std::uint64_t Offset = 0;
        std::uint32_t Size = 0;
        int TypeIndex = 0;
    };

    struct UnityTypeLayout
    {
        std::size_t End = 0, Children = 0;
    };

    struct UnitySerializedType
    {
        int ClassId = 0;
        bool Stripped = false;
        std::int16_t ScriptTypeIndex = -1;
        std::array<std::uint8_t, 16> ScriptId{}, TypeHash{};
        std::vector<UnityTypeNode> Nodes;
        std::vector<UnityTypeLayout> Layout;
    };

    struct UnityExternalReference
    {
        std::string Name, Path;
        std::array<std::uint8_t, 16> Guid{};
        std::int32_t Type = 0;
    };

    struct UnitySerializedObject
    {
        std::int64_t PathId = 0;
        std::int32_t TypeIndex = 0;
        std::vector<std::uint8_t> Data;
    };

    struct UnitySerializedDocument
    {
        std::uint32_t FormatVersion = 17;
        std::int32_t Platform = 19;
        bool BigEndian = false, HasTypeTree = true;
        std::string UnityVersion, UserInformation;
        std::vector<UnitySerializedType> Types;
        std::vector<UnitySerializedObject> Objects;
        std::vector<std::pair<std::int32_t, std::int64_t>> ScriptReferences;
        std::vector<UnityExternalReference> Externals;
    };

    class UnitySerializedFile
    {
    public:
        explicit UnitySerializedFile(std::span<const std::uint8_t> bytes, const UnityClassDatabase *database = nullptr);
        explicit UnitySerializedFile(std::vector<std::uint8_t> &&bytes, const UnityClassDatabase *database = nullptr);

        const std::vector<UnityAssetInfo> &Assets() const noexcept
        {
            return assets_;
        }

        const UnityAssetInfo &Asset(std::int64_t id) const;
        std::optional<int> AssetClass(std::int64_t id) const;
        Parameter Read(std::int64_t id, bool allowTrailing = false) const;
        Parameter ReadScript(std::int64_t id, const std::vector<UnityTypeNode> &schema) const;
        std::optional<std::string> NamedPrefix(std::int64_t id) const;
        std::span<const std::uint8_t> Bytes(std::int64_t id) const;
        std::size_t ResidentBytes() const;
        UnitySerializedDocument Document() const;
        const UnitySerializedType &TypeOf(std::int64_t id) const;
        std::string UnityVersion;
        std::vector<std::string> ExternalNames;

    private:
        std::vector<std::uint8_t> bytes_;
        std::vector<UnityAssetInfo> assets_;
        std::unordered_map<std::int64_t, std::size_t> index_;
        std::vector<UnitySerializedType> types_;
        UnitySerializedDocument metadata_;
        bool bigEndian_ = false;
    };

    class NativeGpuAssetReader
    {
    public:
        explicit NativeGpuAssetReader(const std::filesystem::path &path, const UnityClassDatabase *database = nullptr,
                                      std::shared_ptr<UnityBlockCache> cache = {}, std::stop_token cancellation = {});
        static std::string ReadCabName(const std::filesystem::path &path, std::stop_token cancellation = {});
        std::filesystem::path Path;
        std::string CabName;
        std::vector<std::string> ExternalNames;
        std::function<std::vector<UnityTypeNode>(const GpuAssetPointer &, std::string_view)> ScriptSchema;
        const std::string &UnityVersion() const;
        const Parameter &Metadata(std::int64_t id);
        // 返回独立值，不把转换用的大型参数树留在读取器中；已有借用保持有效。
        Parameter ReadMetadata(std::int64_t id) const;
        Parameter MetadataHeader(std::int64_t id) const;
        const Parameter &ReadScript(std::int64_t id, const std::vector<UnityTypeNode> &schema);
        const std::vector<UnityAssetInfo> &AssetIndex() const;
        std::optional<int> AssetClass(std::int64_t id) const;
        std::vector<std::string> ExportNames();
        std::string AssetName(std::int64_t id);
        using NameLookup = std::unordered_map<std::string, std::vector<std::int64_t>>;
        NameLookup ShaderNames();
        GpuAssetPointer Find(std::string_view name, int classId, int textureSize = 0);
        std::optional<GpuAssetPointer> TryFind(std::string_view name, int classId, int textureSize = 0);
        std::vector<std::uint8_t> ReadText(std::string_view name);
        GpuMeshData Mesh(std::int64_t id);
        GpuTextureData Texture(std::int64_t id);
        GpuMaterialData Material(std::int64_t id);
        GpuPrefabData Prefab(std::string_view name);
        std::vector<std::uint8_t> ResourceStream(std::string_view name, std::uint64_t offset, std::uint64_t size) const;
        std::size_t ResidentBytes() const;
        const UnitySerializedType &TypeOf(std::int64_t id) const;
        std::vector<std::uint8_t> RawObject(std::int64_t id) const;

    private:
        std::unique_ptr<UnityBundle> bundle_;
        std::unique_ptr<UnitySerializedFile> file_;
        std::unordered_map<std::int64_t, Parameter> fields_;
        std::size_t fieldBytes_ = 0;
        std::unordered_map<int, std::unordered_map<std::string, std::vector<std::int64_t>>> names_;
        std::optional<std::unordered_map<int, NameLookup>> exports_;
        const NameLookup &ExportedNames(int classId);
        GpuAssetPointer Pointer(const Parameter &p) const;
    };
}
