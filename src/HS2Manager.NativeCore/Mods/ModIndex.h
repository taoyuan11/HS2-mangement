#pragma once
#include "Common/Core.h"
#include <map>

namespace hs2
{
    struct ModMigration
    {
        std::string Kind, OldGuid, NewGuid, Category;
        int OldSlot = -1, NewSlot = -1;
    };

    struct ModArchiveEntry
    {
        std::string Name;
        std::int64_t Length = 0;
    };

    struct ExportShader
    {
        std::string Name;
        std::vector<std::string> Bundles;
    };

    struct ModExportMetadata
    {
        std::vector<ExportShader> Shaders;
        bool BonesFramework = false;
    };

    struct ModFileInfo
    {
        std::string Path;
        std::int64_t Size = 0, ModifiedUtcTicks = 0;
        std::string Guid, Name, Version, Error;
        bool Compatible = true;
        std::vector<ModMigration> Migrations;
        std::optional<std::vector<ModArchiveEntry>> ArchiveEntries;
        std::optional<ModExportMetadata> ExportMetadata;
    };

    struct PluginDependency
    {
        std::string Guid, MinimumVersion;
        bool Optional = false;
        bool operator==(const PluginDependency &) const = default;
    };

    struct PluginAssemblyReference
    {
        std::string Name, Version;
    };

    struct PluginDefinition
    {
        std::string Guid, Name, Version;
        std::vector<PluginDependency> Dependencies;
        std::vector<std::string> DataIds;
    };

    struct PluginFileInfo
    {
        std::string Path;
        std::int64_t Size = 0, ModifiedUtcTicks = 0;
        std::string AssemblyName, Version;
        std::vector<PluginDefinition> Plugins;
        std::vector<PluginAssemblyReference> References;
        std::vector<std::string> NativeReferences;
        std::string Error;
    };

    struct ModIndex
    {
        std::string GamePath, UpdatedUtc;
        bool Complete = false, MigrationEnabled = true;
        std::vector<ModFileInfo> Files;
        std::map<std::string, std::string> Plugins;
        std::vector<PluginFileInfo> PluginFiles;
        std::vector<std::string> Warnings;
    };

    void to_json(Json &, const ModMigration &);
    void from_json(const Json &, ModMigration &);
    void to_json(Json &, const ModArchiveEntry &);
    void from_json(const Json &, ModArchiveEntry &);
    void to_json(Json &, const ExportShader &);
    void from_json(const Json &, ExportShader &);
    void to_json(Json &, const ModExportMetadata &);
    void from_json(const Json &, ModExportMetadata &);
    void to_json(Json &, const ModFileInfo &);
    void from_json(const Json &, ModFileInfo &);
    void to_json(Json &, const PluginDependency &);
    void from_json(const Json &, PluginDependency &);
    void to_json(Json &, const PluginAssemblyReference &);
    void from_json(const Json &, PluginAssemblyReference &);
    void to_json(Json &, const PluginDefinition &);
    void from_json(const Json &, PluginDefinition &);
    void to_json(Json &, const PluginFileInfo &);
    void from_json(const Json &, PluginFileInfo &);
    void to_json(Json &, const ModIndex &);
    void from_json(const Json &, ModIndex &);

    class NativeModSelection
    {
    public:
        static std::vector<ModFileInfo> Select(const std::vector<ModFileInfo> &files);
        static int CompareVersions(const std::string &first, const std::string &second);
    };

    class NativeModCategories
    {
    public:
        static std::optional<int> Parse(std::string category);
    };

    class PluginFileIndex
    {
    public:
        static PluginFileInfo Read(const fs::path &path);
        static std::vector<PluginFileInfo> Scan(const fs::path &folder, std::vector<std::string> &warnings,
                                                std::stop_token cancellation = {});
        static std::string PluginGuid(const std::string &dataId);
        static int CompareVersions(const std::string &first, const std::string &second);
    };

    class ModDependencyService
    {
    public:
        explicit ModDependencyService(CardRepository &repository);
        ModIndex Scan(const fs::path &gamePath, Progress progress = {}, std::stop_token cancellation = {});
        // 临时预览只读取资源，索引持久化由正常资料库扫描单独负责。
        static ModIndex ScanTransient(const fs::path &gamePath, const ModIndex *previous = nullptr,
                                      Progress progress = {}, std::stop_token cancellation = {});

        const std::optional<ModIndex> &Index() const
        {
            return index_;
        }

        std::vector<DependencyResult> Diagnose(const CardRecord &card, const fs::path &gamePath,
                                               bool sourceAvailable = true) const;
        static ModFileInfo ReadManifest(const fs::path &path);

    private:
        CardRepository &repository_;
        std::optional<ModIndex> index_;
    };
}
