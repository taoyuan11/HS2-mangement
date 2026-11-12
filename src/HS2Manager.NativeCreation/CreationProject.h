#pragma once
#include "PaintDocument.h"
#include <functional>
#include <algorithm>

namespace hs2::creation
{
    struct CreationObject
    {
        std::string Id = NewId();
        MeshDocument Mesh;
        Json Generator = nullptr;
        std::string GeneratorMeshHash;
        bool Hidden = false, Locked = false, HalfState = false;
    };

    struct CreationTexture
    {
        std::string Id = NewId(), Name = "贴图", Kind = "material", Target = "face", Property = "_MainTex";
        std::string ClothingName = "ct_clothesTop";
        int MaterialSlot = 0;
        bool Replace = false;
        PaintDocument Image = PaintDocument::Blank(1024, 1024);
    };

    struct CreationAsset
    {
        std::string Id = NewId(), Name = "新素材", Kind = "accessory";
        int Slot = 100000;
        std::string TargetPath = "accessory.parts[0].id", TemplateId, Parent = "N_Head";
        std::vector<CreationObject> Objects;
        std::vector<asset::Bone> Bones;
        std::set<std::string> LockedBones;
        std::vector<std::string> DynamicRoots;
        Json DynamicSettings = Json::object();
        Json TemplateSource = Json::object();
        std::vector<CreationTexture> Textures;
        Json MaterialValues = Json::array();
        bool IndependentHalfState = false;
    };

    inline int ClothingOverlaySlot(const std::string &name)
    {
        const std::vector<std::string> names{"ct_clothesTop", "ct_clothesBot", "ct_inner_t", "ct_inner_b",
                                             "ct_gloves",     "ct_panst",      "ct_socks",   "ct_shoes"};
        auto it = std::find(names.begin(), names.end(), name);
        return it == names.end() ? -1 : int(it - names.begin());
    }

    inline bool OverlayMatches(const asset::Mesh &mesh, const CreationTexture &texture)
    {
        if (texture.Target == "face")
            return mesh.Role == "head" && mesh.Name.starts_with("o_head");
        if (texture.Target == "body")
            return mesh.Role == "body";
        return mesh.Role == "clothes" && mesh.ReferenceSlot == ClothingOverlaySlot(texture.ClothingName);
    }

    struct CreationState
    {
        std::string Name = "新工程", Kind = "character", Token = NewId();
        std::optional<CharacterDocument> Character;
        std::vector<CreationAsset> Assets;
        std::vector<CreationTexture> Overlays;
        Json CoverSettings = {{"mode", "render"}, {"view", "front"}};
        std::string CoverToken;
        Json LastBuild = nullptr;
    };

    struct ProjectSummary
    {
        fs::path Path;
        std::string Id, Name, Kind, UpdatedUtc, Error;
        int Sex = -1;
        bool HasRecovery = false;
    };

    struct PreparedProjectSave
    {
        std::string Snapshot, Token;
        std::uint64_t Revision = 0;
    };

    class CreationProject
    {
    public:
        static std::shared_ptr<CreationProject> Create(const fs::path &root, std::string name, std::string kind,
                                                       CharacterDocument character);
        static std::shared_ptr<CreationProject> Open(const fs::path &path, bool recovery = false);
        static std::vector<ProjectSummary> List(const fs::path &root);
        static std::shared_ptr<CreationProject> Duplicate(const fs::path &root, const fs::path &source,
                                                          std::string name);

        const CreationState &State() const
        {
            return *state_;
        }

        std::shared_ptr<const CreationState> Snapshot() const
        {
            return state_;
        }

        const fs::path &Directory() const
        {
            return directory_;
        }

        const std::string &Id() const
        {
            return id_;
        }

        std::string Guid() const
        {
            return "hs2manager." + id_;
        }

        std::uint64_t Revision() const
        {
            return revision_;
        }

        bool IsDirty() const
        {
            return state_->Token != savedToken_;
        }

        bool CanUndo() const
        {
            return !undo_.empty() || gesture_ != nullptr;
        }

        bool CanRedo() const
        {
            return !redo_.empty();
        }

        void Edit(std::string label, const std::function<void(CreationState &)> &operation);
        void BeginGesture();
        void EndGesture();
        void CancelGesture();
        void Undo();
        void Redo();
        void Save(bool recovery = false);
        PreparedProjectSave PrepareSave(std::shared_ptr<const CreationState> state, std::uint64_t revision) const;
        void CommitSave(const PreparedProjectSave &save, bool recovery);
        void AcceptBuild(Json result, CharacterDocument character, std::uint64_t expectedRevision);
        void AcceptCover(CharacterDocument character, Json settings, std::uint64_t expectedRevision);
        fs::path ImportSource(const fs::path &source);
        std::function<void()> Changed;
        static Json Encode(const CreationState &state);
        static CreationState Decode(const Json &json, const fs::path &source);
        static void Validate(const CreationState &state);

    private:
        explicit CreationProject(fs::path directory);
        void Lock();
        void Notify();
        fs::path directory_;
        std::string id_, savedToken_;
        std::uint64_t revision_ = 0;
        std::shared_ptr<void> lock_;
        mutable std::mutex saveGate_;
        std::shared_ptr<const CreationState> state_, gesture_;
        std::vector<std::shared_ptr<const CreationState>> undo_, redo_;
    };
}
