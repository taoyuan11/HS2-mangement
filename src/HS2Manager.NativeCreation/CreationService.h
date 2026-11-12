#pragma once
#include "CreationProject.h"
#include "SharedAssets.h"
#include "Export/CardExport.h"

namespace hs2::creation
{
    std::map<std::string, std::vector<CharacterChoice>> InformationChoices(const fs::path &game, const fs::path &tpk,
                                                                           std::stop_token token = {});

    struct BuildRequest
    {
        std::shared_ptr<const CreationState> State;
        fs::path ProjectDirectory;
        std::string Guid;
        std::uint64_t Revision = 0;
    };

    struct BuildResult
    {
        std::uint64_t Revision = 0;
        std::string Token;
        fs::path Directory, Mod, Card;
        std::optional<CharacterDocument> Character;
        Json Report;
    };

    struct InstallFile
    {
        fs::path Source, Destination;
        std::string Hash, PreviousHash, Action = "copy";
    };

    struct InstallPlan
    {
        std::string Id = NewId(), ProjectId, Guid;
        fs::path Game;
        std::vector<InstallFile> Files;
        std::map<fs::path, std::string> Dependencies;
        std::vector<std::string> Conflicts, Warnings;
        bool HasChanges() const;
        std::string Summary() const;
    };

    struct InstallResult
    {
        std::string Id, Status;
        std::vector<std::string> Messages;
        std::vector<fs::path> Cards;
    };

    struct FitPreview
    {
        std::shared_ptr<const CreationState> State;
        std::shared_ptr<const asset::Geometry> Reference;
    };

    class CreationService
    {
    public:
        CreationService(fs::path game, fs::path data, fs::path runtime);

        fs::path ProjectsRoot() const
        {
            return data_ / "Creation/Projects";
        }

        CharacterDocument DefaultCharacter(int sex, std::stop_token token = {}) const;
        std::shared_ptr<CreationProject> Create(std::string name, std::string kind, int sex,
                                                std::optional<fs::path> source = {}, std::stop_token token = {});
        PreviewResources Resources(const CreationState *state = nullptr, const fs::path &candidate = {},
                                   std::stop_token token = {});
        asset::Geometry Reference(const CreationState &state, std::stop_token token = {});
        FitPreview PreviewFit(const CreationState &state, float time, std::string bodyPath, float value,
                              std::stop_token token = {});
        Json Templates(const CreationState &state, const std::string &target, std::stop_token token = {});
        Json MaterialProperties(const CreationState &state, const CreationAsset &asset, std::stop_token token = {});
        CreationAsset NewAsset(const CreationState &state, std::string kind, std::string target, std::string name,
                               std::string templateId, std::stop_token token = {});
        void TransferWeights(CreationAsset &asset, const asset::Geometry &reference);
        BuildResult Build(const BuildRequest &request, Progress progress = {}, std::stop_token token = {});
        CharacterDocument Cover(const CreationState &state, const fs::path &output, std::stop_token token = {});
        CardRecord ExportCard(const CreationState &state, const fs::path &output, std::stop_token token = {});
        fs::path ExportMod(const CreationState &state, const fs::path &output, std::stop_token token = {});
        CardExportReport ExportPackage(const CreationState &state, const fs::path &output, std::stop_token token = {});
        InstallPlan PlanInstall(const CreationState &state, const std::string &projectId, std::stop_token token = {});
        InstallResult Install(const InstallPlan &plan, Progress progress = {}, std::stop_token token = {});
        InstallResult Rollback(const std::string &operationId, std::stop_token token = {});
        std::vector<Json> Installations() const;
        std::vector<InstallResult> RecoverInstallations();
        void InvalidateIndex();

    private:
        CharacterDocument ResolveBodySelection(const CharacterDocument &document, PreviewResources resources,
                                               std::stop_token token);
        void UpdateInstalledIndex(const Json &journal);
        void RequireCurrentBuild(const CreationState &state) const;
        fs::path game_, data_, runtime_;
        std::mutex indexGate_;
        std::optional<ModIndex> index_;
    };
}
