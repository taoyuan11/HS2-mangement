#pragma once
#include "../third_party/json.hpp"
#include <cstdint>
#include <filesystem>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <span>
#include <stop_token>
#include <string>
#include <variant>
#include <vector>

namespace hs2
{
    namespace fs = std::filesystem;
    using Json = nlohmann::json;
    using Bytes = std::vector<std::uint8_t>;

    class DatabaseError : public std::runtime_error
    {
    public:
        int ExtendedCode, SystemCode;

        DatabaseError(std::string message, int extendedCode, int systemCode)
            : std::runtime_error("SQLite (" + std::to_string(extendedCode) + ", Win32 " + std::to_string(systemCode) +
                                 "): " + message),
              ExtendedCode(extendedCode), SystemCode(systemCode)
        {
        }
    };

    class Cancelled : public std::runtime_error
    {
    public:
        Cancelled() : std::runtime_error("操作已取消")
        {
        }
    };

    void CheckCancelled(std::stop_token cancellation);
    std::string Utf8(const fs::path &path);
    fs::path Path(std::string_view utf8);
    std::wstring NativeFilePath(const fs::path &path);
    std::string NewId();
    std::string UtcNow();
    std::int64_t ModifiedTicks(const fs::path &path);
    std::string Sha256(std::span<const std::uint8_t> bytes);
    std::string HashFile(const fs::path &path, std::stop_token cancellation = {});
    Bytes ReadFile(const fs::path &path, std::size_t limit = 512ull * 1024 * 1024, std::stop_token cancellation = {});
    void WriteAtomic(const fs::path &path, std::span<const std::uint8_t> bytes);
    void WriteJson(const fs::path &path, const Json &value);
    bool SamePath(const fs::path &left, const fs::path &right);
    bool Within(const fs::path &path, const fs::path &directory);

    enum class CardStatus
    {
        Valid,
        ImageOnly,
        Unsupported,
        Corrupt,
        Unreadable
    };
    enum class FileAvailability
    {
        Available,
        Offline,
        Missing,
        Quarantined
    };
    enum class DependencyState
    {
        Found,
        Missing,
        Unknown
    };
    enum class CardExportLevel
    {
        CardOnly = 1,
        WithMods = 2,
        WithPlugins = 3,
        WithResources = 4
    };

    struct ModReference
    {
        std::string Guid;
        int Slot = -1, Category = -1;
        std::string Property;
        bool operator==(const ModReference &) const = default;
    };

    struct DependencyResult
    {
        std::string Guid;
        DependencyState State;
        std::string Detail;
    };

    struct CardRecord
    {
        std::string Id = NewId(), Path, Root, Name;
        int Sex = -1;
        std::string Version;
        CardStatus Status = CardStatus::Valid;
        std::string Error;
        std::int64_t Size = 0, ModifiedUtcTicks = 0, PngLength = 0;
        std::string CoverFile, Hash;
        bool Favorite = false;
        std::vector<std::string> Tags;
        std::string Collection, Notes;
        std::vector<ModReference> Dependencies;
        std::vector<std::string> PluginDataIds;
        bool DependenciesComplete = true;
        FileAvailability Availability = FileAvailability::Available;
        std::string OriginalPath;
        std::int64_t QuarantinedUtcTicks = 0;
        std::string FileName() const;
        std::string Folder() const;
        std::string SexLabel() const;
        std::string StatusLabel() const;
        std::string AvailabilityLabel() const;
    };

    void to_json(Json &, const ModReference &);
    void from_json(const Json &, ModReference &);
    void to_json(Json &, const CardRecord &);
    void from_json(const Json &, CardRecord &);

    struct AppSettings
    {
        CardExportLevel LastCardExportLevel = CardExportLevel::WithMods;
        std::string GamePath = "Z:\\games\\hs2\\HS two DX - TSYMQ";
        std::vector<std::string> ExtraRoots, Collections;
        bool RecursiveCardSearch = false;
        bool AutoCheckUpdates = true;
        int64_t LastUpdateCheck = 0;
        std::string Theme = "System", BlenderPath, UnrealEditorPath, Sort = "名称";
        double WindowWidth = 1440, WindowHeight = 900;
        std::vector<fs::path> ScanRoots() const;
        static AppSettings Load(const fs::path &directory);
        void Save(const fs::path &directory) const;
    };

    void to_json(Json &, const AppSettings &);
    void from_json(const Json &, AppSettings &);

    struct WorkProgress
    {
        int Completed = 0, Total = 0;
        std::string Message;
    };

    using Progress = std::function<void(const WorkProgress &)>;

    struct ScanReport
    {
        int Candidates = 0, Valid = 0, Invalid = 0, Unchanged = 0;
        std::vector<std::string> Warnings;
    };

    // 不可变语法树：未修改的节点完整保留原始二进制编码。
    class MessagePack
    {
    public:
        enum class Kind
        {
            Scalar,
            Array,
            Map,
            Binary,
            Extension
        };
        using Value = std::variant<std::monostate, bool, std::int64_t, std::uint64_t, float, double, std::string>;
        Kind Type = Kind::Scalar;
        Value ScalarValue;
        std::vector<MessagePack> Children;
        static MessagePack Parse(std::span<const std::uint8_t> bytes);
        static MessagePack Scalar(Value value);
        static MessagePack Array(std::vector<MessagePack> values);
        static MessagePack Map(std::vector<std::pair<Value, MessagePack>> values = {});
        static MessagePack Binary(Bytes value);
        const MessagePack *Find(const Value &key) const;
        const MessagePack &Require(const Value &key) const;
        MessagePack Set(const Value &key, const MessagePack &value, bool allowAdd = false) const;
        MessagePack Remove(const Value &key) const;
        MessagePack WithScalar(Value value) const;
        bool ContentEquals(const MessagePack &other) const;
        std::size_t Count() const;
        std::int64_t Integer() const;
        std::string Text() const;
        std::span<const std::uint8_t> Payload() const;
        Bytes ToBytes() const;
        void Write(Bytes &output) const;

    private:
        std::shared_ptr<Bytes> source_;
        std::size_t rawOffset_ = 0, rawSize_ = 0, payloadOffset_ = 0, payloadSize_ = 0;
        friend class MessagePackReader;
    };

    class CardParser
    {
    public:
        CardRecord Parse(const fs::path &path, std::stop_token cancellation = {}) const;
        static std::size_t FindPngEnd(std::span<const std::uint8_t> bytes);
        static void CopyCover(const fs::path &source, std::int64_t length, const fs::path &output,
                              std::stop_token cancellation = {});
    };

    class CardRepository
    {
    public:
        explicit CardRepository(fs::path dataPath, bool exclusiveAccess = false);
        ~CardRepository();
        CardRepository(const CardRepository &) = delete;
        CardRepository &operator=(const CardRepository &) = delete;
        const fs::path &DataPath() const;
        std::vector<CardRecord> All() const;
        std::optional<CardRecord> FindById(const std::string &id) const;
        std::optional<CardRecord> FindByPath(const fs::path &path) const;
        void Save(const CardRecord &card);
        CardRecord SaveScan(CardRecord parsed);
        void UpdateMetadata(const std::vector<std::string> &ids, const std::function<void(CardRecord &)> &update);
        void Remove(const std::string &id);
        void StoreMetadata(const std::string &key, const Json &value);
        std::optional<Json> LoadMetadata(const std::string &key) const;
        void Backup(const fs::path &target) const;

    private:
        struct Impl;
        std::unique_ptr<Impl> impl_;
    };

    class CardLibraryService
    {
    public:
        explicit CardLibraryService(CardRepository &repository) : repository_(repository)
        {
        }

        ScanReport Scan(const std::vector<fs::path> &roots, Progress progress = {}, std::stop_token cancellation = {},
                        bool recursive = false);
        void ComputeHashes(Progress progress = {}, std::stop_token cancellation = {});
        static std::vector<fs::path> EnumerateCards(const fs::path &root, bool recursive = false,
                                                    std::stop_token cancellation = {});

    private:
        CardRepository &repository_;
        std::mutex scanGate_;
    };

    struct FileOperationRecord
    {
        std::string Id = NewId(), Kind, Source, Destination, Staging, Hash, Phase = "Planned", Error;
        std::string ExpectedSourceHash, BackupPath, SwapBackup, CreatedUtc = UtcNow();
        std::optional<std::int64_t> ExpectedSize, ExpectedModifiedUtcTicks;
        std::optional<CardRecord> Card;
    };

    void to_json(Json &, const FileOperationRecord &);
    void from_json(const Json &, FileOperationRecord &);

    struct FileOperationResult
    {
        std::string Source, Destination;
        bool Success = false;
        std::string Message;
    };

    struct CardDeleteRequest
    {
        std::string Id, Path;
        std::int64_t Size, ModifiedUtcTicks;
        static CardDeleteRequest FromCard(const CardRecord &card);
    };

    class CardFileService
    {
    public:
        explicit CardFileService(CardRepository &repository) : repository_(repository)
        {
        }

        std::vector<FileOperationResult> Import(const std::vector<fs::path> &sources, const fs::path &gamePath,
                                                std::optional<fs::path> destination = {}, Progress progress = {},
                                                std::stop_token cancellation = {});
        std::vector<FileOperationResult> Export(const std::vector<CardRecord> &cards, const fs::path &folder,
                                                Progress progress = {}, std::stop_token cancellation = {});
        void Move(const CardRecord &card, const fs::path &folder, std::stop_token cancellation = {});
        void Rename(const CardRecord &card, std::string name, std::stop_token cancellation = {});
        void Quarantine(const CardRecord &card, const fs::path &gamePath, std::stop_token cancellation = {});
        void Restore(const CardRecord &card, std::stop_token cancellation = {});
        std::vector<FileOperationResult> Delete(const std::vector<CardDeleteRequest> &requests, Progress progress = {},
                                                std::stop_token cancellation = {});
        std::vector<FileOperationRecord> Operations() const;
        std::vector<std::string> Recover(std::stop_token cancellation = {});
        void ExportFile(const fs::path &source, const fs::path &destination, std::int64_t size, std::int64_t modified,
                        std::stop_token cancellation = {});
        static fs::path UniqueDestination(const fs::path &folder, const fs::path &fileName);

    private:
        CardRepository &repository_;
        void Save(const FileOperationRecord &operation) const;
        FileOperationRecord NewOperation(std::string kind, const fs::path &source, const fs::path &destination,
                                         std::optional<CardRecord> card = {});
        void CopyVerified(FileOperationRecord &operation, std::stop_token cancellation);
        void IndexOperation(FileOperationRecord &operation);
        void MoveCore(const CardRecord &card, const fs::path &destination, std::string kind,
                      std::stop_token cancellation);
        void CleanupDeleted(const FileOperationRecord &operation);
    };

    class CharacterDocument
    {
    public:
        static CharacterDocument Open(const fs::path &path, std::stop_token cancellation = {});
        static CharacterDocument FromBytes(Bytes bytes, fs::path path);
        const fs::path &SourcePath() const;
        const std::string &SourceHash() const;
        int Sex() const;
        std::size_t PngLength() const;
        std::span<const std::uint8_t> Cover() const;
        std::vector<std::string> SectionNames() const;
        std::span<const std::uint8_t> BlockBytes(std::string_view name) const;
        const MessagePack &Node(std::string_view path) const;
        const std::optional<MessagePack> &Extensions() const;
        bool Contains(std::string_view path) const;
        bool IsOriginal() const;
        bool ContentEquals(const CharacterDocument &other) const;
        CharacterDocument WithValue(std::string_view path, MessagePack::Value value) const;
        CharacterDocument WithNode(std::string_view path, const MessagePack &replacement) const;
        CharacterDocument WithExtensions(std::optional<MessagePack> value) const;
        CharacterDocument WithName(std::string name) const;
        CharacterDocument WithCover(Bytes png) const;
        Bytes ToBytes() const;

    private:
        struct Data;
        std::shared_ptr<const Data> data_;
        explicit CharacterDocument(std::shared_ptr<const Data> data) : data_(std::move(data)) {};
    };

    class CharacterEditSession
    {
    public:
        CharacterEditSession(CardRecord card, CharacterDocument document);
        CardRecord Card;
        CharacterDocument Document, Baseline;
        std::uint64_t Revision = 0;
        std::function<void()> Changed;
        bool IsDirty() const;
        bool CanUndo() const;
        bool CanRedo() const;
        void BeginGesture();
        void EndGesture();
        void Apply(CharacterDocument next);
        void SetValue(std::string_view path, MessagePack::Value value);
        void SetColor(std::string_view path, const std::vector<float> &color);
        void Restore(std::string_view path);
        void RestoreCategory(std::string_view category);
        void RestoreAll();
        void Undo();
        void Redo();
        void AcceptSaved(CardRecord card, CharacterDocument document, std::uint64_t revision);

    private:
        std::vector<CharacterDocument> undo_, redo_;
        std::optional<CharacterDocument> gesture_;
    };

    struct CardEditSaveResult
    {
        CardRecord Card;
        CharacterDocument Document;
        std::string OperationId, BackupPath;
    };

    class CardEditService
    {
    public:
        explicit CardEditService(CardRepository &repository) : repository_(repository)
        {
        }

        CardEditSaveResult Save(const CharacterDocument &document, const CardRecord &source, bool overwrite,
                                std::optional<fs::path> destination = {}, std::stop_token cancellation = {});
        CardEditSaveResult SaveNew(const CharacterDocument &document, const fs::path &destination,
                                   std::stop_token cancellation = {});
        CardEditSaveResult RestoreBackup(const FileOperationRecord &operation, std::stop_token cancellation = {});

    private:
        CardRepository &repository_;
        std::mutex gate_;
        CardEditSaveResult SaveCore(const CharacterDocument &document, const CardRecord &source,
                                    const fs::path &destination, std::string kind, const std::string &expectedHash,
                                    std::stop_token cancellation);
    };

    struct CharacterAsset
    {
        int Category = 0, Id = 0;
        std::string Name, Guid, Source, Parent;
    };

    struct CharacterAssetBinding
    {
        int Category;
        std::string Property;
    };

    class CharacterAssetReferences
    {
    public:
        static std::optional<CharacterAssetBinding> Describe(const CharacterDocument &document,
                                                             const std::string &path);
        static std::optional<CharacterAssetBinding> DescribePath(int sex, std::string path,
                                                                 int accessoryCategory = 350);
        static std::vector<ModReference> All(const CharacterDocument &document);
        static std::optional<ModReference> Find(const CharacterDocument &document, const std::string &path);
        static CharacterDocument Select(const CharacterDocument &document, const std::string &path,
                                        const CharacterAsset &asset);
        static CharacterDocument Restore(const CharacterDocument &draft, const CharacterDocument &baseline,
                                         const std::string &path);
    };

    class CharacterEditRules
    {
    public:
        static CharacterDocument SetValue(const CharacterDocument &document, const std::string &path,
                                          MessagePack::Value value);
        static CharacterDocument Color(const CharacterDocument &document, const std::string &path,
                                       const std::vector<float> &color);
        static CharacterDocument Select(const CharacterDocument &document, const std::string &path,
                                        const CharacterAsset &asset);
        static CharacterDocument InitializeAsset(CharacterDocument document, const std::string &path,
                                                 const std::vector<struct ComponentDefinition> &components);
    };
    enum class CharacterFieldKind
    {
        Number,
        Color,
        Toggle,
        Asset,
        Choice,
        Preset,
        Text
    };

    struct CharacterChoice
    {
        MessagePack::Value Value;
        std::string Label;
    };

    struct CharacterField
    {
        std::string Path, Category, Group, Label;
        CharacterFieldKind Kind;
        double Minimum = 0, Maximum = 1, DisplayScale = 100;
        std::vector<CharacterChoice> Choices;
    };

    struct CharacterSliderLimits
    {
        double Minimum = 0, Maximum = 1, InputMinimum = 0, InputMaximum = 1;
        static CharacterSliderLimits Read(const fs::path &game);
    };

    class CharacterEditSchema
    {
    public:
        static std::vector<CharacterField> Build(const CharacterDocument &document, CharacterSliderLimits limits = {});
        static const std::vector<std::pair<std::string, std::string>> &Categories();
        static const std::vector<std::string> &HairNames();
        static const std::vector<std::string> &ClothesNames();
        static const std::vector<CharacterChoice> &AccessoryTypes();
        static const std::vector<CharacterChoice> &AccessoryParents();
        static int AutoClothingState(const std::string &category, const std::string &group);
        static std::vector<float> ColorValue(const CharacterDocument &document, const std::string &path);
    };
}
