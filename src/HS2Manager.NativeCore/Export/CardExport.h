#pragma once
#include "Mods/ModIndex.h"
#include <set>

namespace hs2
{
    enum class CardExportMode
    {
        PerCard,
        Merged
    };
    enum class CardExportStatus
    {
        Complete,
        Incomplete,
        Cancelled
    };
    enum class CardExportFileKind
    {
        Card,
        Mod,
        Plugin,
        Resource
    };

    struct CardExportOptions
    {
        CardExportMode Mode = CardExportMode::PerCard;
        CardExportLevel Level = CardExportLevel::WithMods;
    };

    struct CardExportCard
    {
        std::string Id, Name, Source;
    };

    struct CardExportIssue
    {
        std::string CardId, Message;
        bool operator==(const CardExportIssue &) const = default;
    };

    struct CardExportFile
    {
        std::string Source, RelativePath;
        CardExportFileKind Kind;
        std::int64_t Size = 0, ModifiedUtcTicks = 0;
        std::vector<std::string> CardIds;
    };

    struct CardExportPackage
    {
        std::string FolderName;
        std::vector<CardExportCard> Cards;
        std::vector<CardExportFile> Files;
        std::vector<CardExportIssue> Issues, Warnings;
        std::set<std::string> Conflicts;
    };

    struct CardExportPlan
    {
        std::string OutputDirectory, GamePath;
        CardExportMode Mode = CardExportMode::PerCard;
        CardExportLevel Level = CardExportLevel::WithResources;
        int RequestedCards = 0;
        std::vector<CardExportPackage> Packages;
        std::vector<CardExportIssue> Issues;
    };

    struct CardExportFileResult
    {
        std::string Package;
        CardExportFile File;
        bool Success;
        std::string Message;
    };

    struct CardExportReport
    {
        CardExportLevel Level = CardExportLevel::WithResources;
        CardExportStatus Status = CardExportStatus::Incomplete;
        std::string ReportPath;
        std::vector<std::string> OutputFolders;
        std::vector<CardExportFileResult> Files;
        std::vector<CardExportIssue> Issues, Warnings;
        std::string ScopeDescription() const;
        std::string StatusText() const;
        std::string Summary() const;
        int ExportedCards() const;
    };

    std::string ExportLevelLabel(CardExportLevel level);
    std::string ExportLevelContents(CardExportLevel level);
    std::string ExportLevelScope(CardExportLevel level);

    class ExportPaths
    {
    public:
        static fs::path Relative(const std::string &path);
        static fs::path Destination(const fs::path &root, const std::string &relative);
        static void NoLinks(const fs::path &path);
        static std::string SafeName(const std::string &name);
        static std::string UniqueDirectoryName(const fs::path &parent, const std::string &name,
                                               const std::set<std::string> &reserved);
    };

    struct ExportCandidate
    {
        std::string Source, RelativePath;
        CardExportFileKind Kind;
        bool operator==(const ExportCandidate &) const = default;
    };

    struct CollectedDependencies
    {
        std::vector<ExportCandidate> Files;
        std::vector<std::string> Issues, Warnings;
    };

    class CardExportDependencies
    {
    public:
        CardExportDependencies(fs::path game, ModIndex index, std::stop_token cancellation = {},
                               std::optional<fs::path> classDatabase = {});
        ~CardExportDependencies();
        CollectedDependencies Collect(const CardRecord &card, CardExportLevel level);

    private:
        struct Impl;
        std::unique_ptr<Impl> impl_;
    };

    class CardExportService
    {
    public:
        explicit CardExportService(CardRepository &repository, std::optional<fs::path> classDatabase = {})
            : repository_(repository), classDatabase_(std::move(classDatabase))
        {
        }

        CardExportPlan BuildPlan(const std::vector<CardRecord> &cards, const fs::path &game, const fs::path &output,
                                 CardExportOptions options = {}, Progress progress = {},
                                 std::stop_token cancellation = {});
        CardExportReport Export(const CardExportPlan &plan, Progress progress = {}, std::stop_token cancellation = {});
        static void ValidateOutput(const fs::path &game, const fs::path &output);

    private:
        CardRepository &repository_;
        std::optional<fs::path> classDatabase_;
    };
}
