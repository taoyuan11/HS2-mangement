#pragma once
#include "Assets/AssetStore.h"
#include "Character/CharacterDefinition.h"
#include "Common/Core.h"

namespace hs2
{
    struct NativeAssetEntry
    {
        int Category = 0, Id = 0;
        std::string Bundle, Asset, MaleAsset, Guid, Source;
        std::filesystem::path Archive;
        std::unordered_map<std::string, std::string> Values;
        std::string Get(std::string_view key) const;
        CharacterAsset Editable() const;
    };

    class CharacterCatalog
    {
    public:
        CharacterCatalog(std::filesystem::path game, std::filesystem::path data, std::filesystem::path tpk);
        ~CharacterCatalog();
        CharacterCatalog(const CharacterCatalog &) = delete;
        CharacterCatalog &operator=(const CharacterCatalog &) = delete;
        void Load(Progress progress = {}, std::stop_token cancellation = {});
        const std::vector<NativeAssetEntry> &Entries() const;
        const std::vector<NativeAssetEntry> &StockEntries() const;
        const std::vector<std::string> &Warnings() const;
        std::vector<NativeAssetEntry> Query(int category, std::string_view text = {}, int source = 0) const;
        std::optional<NativeAssetEntry> Selected(const CharacterDocument &document, std::string_view path) const;
        NativeAssetEntry Resolve(const ModReference &reference) const;
        std::filesystem::path Bundle(std::string relative, const std::filesystem::path &archive = {});
        AssetReference Asset(std::string relative, std::string name, const std::filesystem::path &archive = {});
        std::optional<AssetReference> Thumbnail(const NativeAssetEntry &entry);
        std::vector<std::filesystem::path> Dependencies(std::string relative,
                                                        const std::filesystem::path &archive = {});
        std::shared_ptr<const UnityClassDatabase> Database() const;
        CharacterDocument Select(const CharacterDocument &document, const std::string &path,
                                 const NativeAssetEntry &entry);

    private:
        struct Impl;
        std::unique_ptr<Impl> impl_;
    };

    Parameter ParameterOf(const MessagePack &value, std::string name = {});
}
