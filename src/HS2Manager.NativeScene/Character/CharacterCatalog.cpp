#include "Character/CharacterCatalog.h"
#include "Character/CharacterAssembly.h"
#include "Archive/ZipArchive.h"
#include "Mods/ModIndex.h"
#include <Windows.h>
#include <algorithm>
#include <charconv>
#include <cctype>
#include <fstream>
#include <set>
#include <sstream>

namespace hs2
{
    namespace
    {
        std::string Lower(std::string value)
        {
            std::transform(value.begin(), value.end(), value.begin(),
                           [](unsigned char c)
                           {
                               return char(std::tolower(c));
                           });
            return value;
        }

        std::string Normal(std::string value)
        {
            std::replace(value.begin(), value.end(), '\\', '/');
            return Lower(std::move(value));
        }

        bool Present(std::string_view value)
        {
            return !value.empty() && value != "0";
        }

        std::string Key(int category, int id, std::string guid)
        {
            return std::to_string(category) + ":" + Lower(guid) + ":" + std::to_string(id);
        }

        std::optional<int> Number(std::string_view value)
        {
            int n = 0;
            auto [end, error] = std::from_chars(value.data(), value.data() + value.size(), n);
            if (error != std::errc{} || end != value.data() + value.size())
                return {};
            return n;
        }

        std::string Hash(std::string_view value)
        {
            return Sha256(std::span(reinterpret_cast<const uint8_t *>(value.data()), value.size()));
        }

        std::string Stamp(const fs::path &path)
        {
            return Utf8(fs::absolute(path)) + "|" + std::to_string(fs::file_size(path)) + "|" +
                   std::to_string(ModifiedTicks(path));
        }

        std::string Trim(std::string value)
        {
            auto first = value.find_first_not_of(" \t\r\n"), last = value.find_last_not_of(" \t\r\n");
            return first == value.npos ? std::string{} : value.substr(first, last - first + 1);
        }

        std::string ScalarText(const MessagePack &value)
        {
            return std::visit(
                [](const auto &v) -> std::string
                {
                    using T = std::decay_t<decltype(v)>;
                    if constexpr (std::is_same_v<T, std::string>)
                        return v;
                    else if constexpr (std::is_arithmetic_v<T>)
                        return std::to_string(v);
                    else
                        return {};
                },
                value.ScalarValue);
        }

        // 匹配 StreamReader 的 BOM 检测与替换回退规则，使显示名称包含旧编码字节的 MOD 清单行仍可读取。
        std::vector<uint8_t> CsvUtf8(std::span<const uint8_t> data)
        {
            if (data.empty())
                return {};
            std::wstring text;
            bool utf32 = data.size() >= 4 && ((data[0] == 0xff && data[1] == 0xfe && data[2] == 0 && data[3] == 0) ||
                                              (data[0] == 0 && data[1] == 0 && data[2] == 0xfe && data[3] == 0xff));
            bool utf16 =
                data.size() >= 2 && ((data[0] == 0xff && data[1] == 0xfe) || (data[0] == 0xfe && data[1] == 0xff));
            if (utf32)
            {
                bool big = data[0] == 0;
                for (size_t i = 4; i < data.size(); i += 4)
                {
                    uint32_t cp = 0xfffd;
                    if (data.size() - i >= 4)
                    {
                        cp = 0;
                        for (int b = 0; b < 4; ++b)
                            cp |= uint32_t(data[i + b]) << (big ? (3 - b) * 8 : b * 8);
                        if (cp > 0x10ffff || (cp >= 0xd800 && cp <= 0xdfff))
                            cp = 0xfffd;
                    }
                    if (cp <= 0xffff)
                        text.push_back(static_cast<wchar_t>(cp));
                    else
                    {
                        cp -= 0x10000;
                        text.push_back(static_cast<wchar_t>(0xd800 + (cp >> 10)));
                        text.push_back(static_cast<wchar_t>(0xdc00 + (cp & 1023)));
                    }
                }
            }
            else if (utf16)
            {
                bool big = data[0] == 0xfe;
                for (size_t i = 2; i < data.size(); i += 2)
                    text.push_back(i + 1 < data.size() ? static_cast<wchar_t>(big ? (data[i] << 8) | data[i + 1]
                                                                                  : data[i] | (data[i + 1] << 8))
                                                       : L'\ufffd');
            }
            else
            {
                int size = MultiByteToWideChar(CP_UTF8, 0, reinterpret_cast<const char *>(data.data()),
                                               static_cast<int>(data.size()), nullptr, 0);
                if (size <= 0)
                    throw std::runtime_error("MOD CSV text cannot be decoded.");
                text.resize(size);
                MultiByteToWideChar(CP_UTF8, 0, reinterpret_cast<const char *>(data.data()),
                                    static_cast<int>(data.size()), text.data(), size);
            }
            if (text.empty())
                return {};
            int size = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr,
                                           nullptr);
            if (size <= 0)
                throw std::runtime_error("MOD CSV text cannot be encoded.");
            std::vector<uint8_t> result(size);
            WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()),
                                reinterpret_cast<char *>(result.data()), size, nullptr, nullptr);
            return result;
        }

        std::vector<std::vector<std::string>> Csv(std::span<const uint8_t> data)
        {
            auto decoded = CsvUtf8(data);
            data = decoded;
            std::vector<std::vector<std::string>> rows;
            std::vector<std::string> row;
            std::string cell;
            bool quote = false, started = false;
            size_t i = data.size() >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf ? 3 : 0;
            for (; i < data.size(); ++i)
            {
                char c = static_cast<char>(data[i]);
                if (c == '"')
                {
                    if (quote && i + 1 < data.size() && data[i + 1] == '"')
                    {
                        cell += '"';
                        ++i;
                    }
                    else if (!started || quote)
                    {
                        quote = !quote;
                        started = true;
                    }
                    else
                        cell += c;
                }
                else if (!quote && (c == ',' || c == '\n' || c == '\r'))
                {
                    row.push_back(Trim(std::move(cell)));
                    cell.clear();
                    started = false;
                    if (c != ',')
                    {
                        if (c == '\r' && i + 1 < data.size() && data[i + 1] == '\n')
                            ++i;
                        if (row.size() != 1 || !row[0].empty())
                            rows.push_back(std::move(row));
                        row.clear();
                        if (rows.size() > 100010)
                            throw std::runtime_error("MOD list exceeds row limit.");
                    }
                }
                else
                {
                    cell += c;
                    if (c != ' ' && c != '\t')
                        started = true;
                }
            }
            if (quote)
                throw std::runtime_error("Unterminated MOD CSV quoted field.");
            if (!cell.empty() || !row.empty())
            {
                row.push_back(Trim(std::move(cell)));
                rows.push_back(std::move(row));
            }
            return rows;
        }

        void Finish(NativeAssetEntry &entry)
        {
            entry.Bundle = entry.Get("MainAB");
            entry.Asset = entry.Get("MainData");
            entry.MaleAsset = entry.Get("MainData02");
        }

        Json Serialize(const NativeAssetEntry &e)
        {
            return Json{
                {"Category", e.Category},   {"Id", e.Id},     {"Bundle", e.Bundle}, {"Asset", e.Asset},
                {"MaleAsset", e.MaleAsset}, {"Guid", e.Guid}, {"Source", e.Source}, {"Archive", Utf8(e.Archive)},
                {"Values", e.Values}};
        }

        NativeAssetEntry Deserialize(const Json &j)
        {
            NativeAssetEntry e;
            e.Category = j.at("Category");
            e.Id = j.at("Id");
            e.Bundle = j.at("Bundle");
            e.Asset = j.at("Asset");
            e.MaleAsset = j.at("MaleAsset");
            e.Guid = j.at("Guid");
            e.Source = j.at("Source");
            e.Archive = Path(j.at("Archive").get<std::string>());
            e.Values = j.at("Values").get<decltype(e.Values)>();
            return e;
        }
    }

    std::string NativeAssetEntry::Get(std::string_view key) const
    {
        auto found = Values.find(std::string(key));
        return found == Values.end() ? "" : found->second;
    }

    CharacterAsset NativeAssetEntry::Editable() const
    {
        return {Category, Id, Get("Name"), Guid, Source, Get("Parent")};
    }

    Parameter ParameterOf(const MessagePack &value, std::string name)
    {
        Parameter result;
        result.Name = std::move(name);
        if (value.Type == MessagePack::Kind::Map)
        {
            for (size_t i = 0; i + 1 < value.Children.size(); i += 2)
                result.Children.push_back(ParameterOf(value.Children[i + 1], ScalarText(value.Children[i])));
        }
        else if (value.Type == MessagePack::Kind::Array)
        {
            for (size_t i = 0; i < value.Children.size(); ++i)
                result.Children.push_back(ParameterOf(value.Children[i], std::to_string(i)));
        }
        else if (value.Type == MessagePack::Kind::Binary || value.Type == MessagePack::Kind::Extension)
        {
            result.Encoding = "bytes";
            auto b = value.Payload();
            result.Bytes.assign(b.begin(), b.end());
        }
        else
            std::visit(
                [&](const auto &v)
                {
                    using T = std::decay_t<decltype(v)>;
                    if constexpr (std::is_same_v<T, std::string>)
                        result.Text = v;
                    else if constexpr (std::is_arithmetic_v<T>)
                    {
                        result.Number = static_cast<double>(v);
                        if constexpr (std::is_integral_v<T>)
                        {
                            result.Integer = static_cast<int64_t>(v);
                            result.Unsigned = static_cast<uint64_t>(v);
                        }
                    }
                },
                value.ScalarValue);
        return result;
    }

    struct CharacterCatalog::Impl
    {
        fs::path Game, Data, Cache;
        std::shared_ptr<UnityClassDatabase> Database;
        std::vector<NativeAssetEntry> Entries, StockEntries;
        std::vector<std::string> Warnings;
        std::vector<ModFileInfo> Mods;
        std::unordered_map<std::string, std::vector<fs::path>> Bundles;
        std::unordered_map<std::string, size_t> EntryKeys;
        std::unordered_map<std::string, std::vector<std::string>> Manifest, Exports;
        std::unordered_map<std::string, std::vector<fs::path>> DependencyPaths;
        bool Loaded = false, ManifestLoaded = false, Migration = true;
        std::stop_token Cancellation;

        Impl(fs::path game, fs::path data, fs::path tpk)
            : Game(fs::absolute(game)), Data(fs::absolute(data)), Cache(Data / "NativeCpp" / "resources"),
              Database(std::make_shared<UnityClassDatabase>(tpk))
        {
            fs::create_directories(Cache);
        }

        fs::path Physical(std::string relative) const
        {
            std::replace(relative.begin(), relative.end(), '\\', '/');
            auto root = fs::weakly_canonical(Game / "abdata");
            auto target = fs::weakly_canonical(root / Path(relative));
            if (!Within(target, root))
                throw std::runtime_error("Asset reference escapes abdata: " + relative + " -> " + Utf8(target) +
                                         " (root " + Utf8(root) + ")");
            return target;
        }

        std::optional<fs::path> Extract(const fs::path &archive, const std::string &relative)
        {
            CheckCancelled(Cancellation);
            ZipArchive zip(archive);
            auto *entry = zip.Find("abdata/" + relative);
            if (!entry)
                return {};
            auto target = Cache / Path(Hash(Stamp(archive) + "|" + entry->Name) +
                                       (Normal(relative).ends_with(".png") ? ".png" : ".unity3d"));
            if (fs::exists(target) && fs::file_size(target) == entry->Size)
                return target;
            auto data = zip.Read(*entry);
            CheckCancelled(Cancellation);
            WriteAtomic(target, data);
            return target;
        }

        std::vector<NativeAssetEntry> Stock(const fs::path &path)
        {
            NativeGpuAssetReader reader(path, Database.get());
            std::vector<NativeAssetEntry> output;
            for (auto &asset : reader.AssetIndex())
                if (asset.ClassId == 49)
                {
                    CheckCancelled(Cancellation);
                    auto bytes = reader.ReadText(reader.Metadata(asset.PathId).String("m_Name"));
                    auto root = MessagePack::Parse(bytes);
                    auto *mark = root.Find(std::string("mark"));
                    if (!mark || mark->Text() != "【ChaListData】")
                        continue;
                    auto category = root.Require(std::string("categoryNo")).Integer();
                    auto &keys = root.Require(std::string("lstKey"));
                    auto &rows = root.Require(std::string("dictList"));
                    if (category < 0 || category > 10000 || keys.Type != MessagePack::Kind::Array ||
                        rows.Type != MessagePack::Kind::Map)
                        throw std::runtime_error("Invalid stock asset list.");
                    for (size_t i = 0; i + 1 < rows.Children.size(); i += 2)
                    {
                        auto id = rows.Children[i].Integer();
                        if (id < 0 || id > INT_MAX)
                            throw std::runtime_error("Invalid stock asset ID.");
                        NativeAssetEntry e;
                        e.Category = static_cast<int>(category);
                        e.Id = static_cast<int>(id);
                        e.Source = "原版";
                        auto &row = rows.Children[i + 1];
                        for (size_t k = 0; k < std::min(keys.Children.size(), row.Children.size()); ++k)
                            e.Values[keys.Children[k].Text()] = row.Children[k].Text();
                        Finish(e);
                        output.push_back(std::move(e));
                    }
                }
            return output;
        }

        std::vector<NativeAssetEntry> Mod(const ModFileInfo &mod)
        {
            std::vector<NativeAssetEntry> result;
            ZipArchive zip(Path(mod.Path));
            for (auto &file : zip.Entries())
            {
                auto name = Normal(file.Name);
                if (!name.starts_with("abdata/list/characustom/") || !name.ends_with(".csv"))
                    continue;
                CheckCancelled(Cancellation);
                auto rows = Csv(zip.Read(file, 8 * 1024 * 1024));
                if (rows.empty() || rows.front().empty())
                    continue;
                auto category = Number(rows.front()[0]);
                if (!category)
                    continue;
                size_t header = 1;
                while (header < rows.size() && header <= 8 && (rows[header].empty() || rows[header][0] != "ID"))
                    ++header;
                if (header >= rows.size() || header > 8)
                    continue;
                for (size_t i = header + 1; i < rows.size(); ++i)
                {
                    auto id = rows[i].empty() ? std::optional<int>{} : Number(rows[i][0]);
                    if (!id)
                        continue;
                    NativeAssetEntry e;
                    e.Category = *category;
                    e.Id = *id;
                    e.Archive = Path(mod.Path);
                    e.Guid = mod.Guid;
                    e.Source = mod.Name.empty() ? mod.Guid : mod.Name;
                    for (size_t k = 0; k < std::min(rows[header].size(), rows[i].size()); ++k)
                        if (!rows[header][k].empty())
                            e.Values[rows[header][k]] = rows[i][k];
                    Finish(e);
                    result.push_back(std::move(e));
                }
            }
            return result;
        }

        template <class F> std::vector<NativeAssetEntry> Cached(const fs::path &source, F read, std::string stamp = {})
        {
            auto cache = Cache / Path("list-v1-" + Hash(stamp.empty() ? Stamp(source) : stamp) + ".json");
            try
            {
                if (fs::exists(cache))
                {
                    auto json = Json::parse(ReadFile(cache));
                    std::vector<NativeAssetEntry> rows;
                    for (auto &j : json)
                        rows.push_back(Deserialize(j));
                    return rows;
                }
            }
            catch (const std::exception &)
            {
            }
            auto rows = read();
            Json json = Json::array();
            for (auto &e : rows)
                json.push_back(Serialize(e));
            WriteJson(cache, json);
            return rows;
        }

        std::optional<NativeAssetEntry> Resolve(const ModReference &reference, std::set<std::string> &visited,
                                                int depth) const
        {
            auto key = Key(reference.Category, reference.Slot, reference.Guid);
            if (depth >= 16 || !visited.insert(key).second)
                return {};
            if (Migration)
                for (std::string kind : {"Migrate", "MigrateAll"})
                    for (auto &mod : Mods)
                        for (auto &rule : mod.Migrations)
                            if (rule.Kind == kind && Lower(rule.OldGuid) == Lower(reference.Guid) &&
                                (kind == "MigrateAll" ||
                                 (rule.OldSlot == reference.Slot &&
                                  NativeModCategories::Parse(rule.Category) == reference.Category)))
                            {
                                auto next = reference;
                                next.Guid = rule.NewGuid;
                                if (kind != "MigrateAll")
                                    next.Slot = rule.NewSlot;
                                if (auto found = Resolve(next, visited, depth + 1))
                                    return found;
                            }
            if (auto found = EntryKeys.find(key); found != EntryKeys.end())
                return Entries[found->second];
            return {};
        }
    };

    CharacterCatalog::CharacterCatalog(fs::path game, fs::path data, fs::path tpk)
        : impl_(std::make_unique<Impl>(std::move(game), std::move(data), std::move(tpk)))
    {
    }

    CharacterCatalog::~CharacterCatalog() = default;

    void CharacterCatalog::Load(Progress progress, std::stop_token cancellation)
    {
        auto &p = *impl_;
        p.Cancellation = cancellation;
        if (p.Loaded)
            return;
        std::vector<NativeAssetEntry> entries;
        p.Warnings.clear();
        std::vector<fs::path> stock;
        auto directory = p.Game / "abdata/list/characustom";
        if (!fs::is_directory(directory))
            throw std::runtime_error("Game character lists are missing.");
        for (auto &f : fs::recursive_directory_iterator(directory))
            if (f.is_regular_file() && Normal(Utf8(f.path().extension())) == ".unity3d")
                stock.push_back(f.path());
        std::sort(stock.begin(), stock.end(),
                  [](auto &a, auto &b)
                  {
                      return Lower(Utf8(a)) < Lower(Utf8(b));
                  });
        for (size_t i = 0; i < stock.size(); ++i)
        {
            CheckCancelled(cancellation);
            if (progress)
                progress({static_cast<int>(i), static_cast<int>(stock.size()), "读取原版素材清单"});
            try
            {
                auto rows = p.Cached(stock[i],
                                     [&]
                                     {
                                         return p.Stock(stock[i]);
                                     });
                entries.insert(entries.end(), std::make_move_iterator(rows.begin()),
                               std::make_move_iterator(rows.end()));
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &e)
            {
                p.Warnings.push_back(Utf8(stock[i].filename()) + ": " + e.what());
            }
        }
        p.StockEntries = entries;
        CardRepository repository(p.Data);
        std::optional<ModIndex> index;
        if (auto saved = repository.LoadMetadata("mods-v1"))
            try
            {
                index = saved->get<ModIndex>();
                if (!SamePath(Path(index->GamePath), p.Game) || std::any_of(index->Files.begin(), index->Files.end(),
                                                                            [](auto &mod)
                                                                            {
                                                                                return mod.Error.empty() &&
                                                                                       !mod.ArchiveEntries;
                                                                            }))
                    index.reset();
            }
            catch (const std::exception &)
            {
            }
        if (!index)
            index = ModDependencyService(repository).Scan(p.Game, progress, cancellation);
        p.Mods = NativeModSelection::Select(index->Files);
        p.Migration = index->MigrationEnabled;
        for (size_t i = 0; i < p.Mods.size(); ++i)
        {
            CheckCancelled(cancellation);
            auto &mod = p.Mods[i];
            if (progress && i % 40 == 0)
                progress({static_cast<int>(i), static_cast<int>(p.Mods.size()), "读取 MOD 素材清单"});
            try
            {
                auto rows = p.Cached(
                    Path(mod.Path),
                    [&]
                    {
                        return p.Mod(mod);
                    },
                    Utf8(fs::absolute(Path(mod.Path))) + "|" + std::to_string(mod.Size) + "|" +
                        std::to_string(mod.ModifiedUtcTicks));
                entries.insert(entries.end(), std::make_move_iterator(rows.begin()),
                               std::make_move_iterator(rows.end()));
                if (mod.ArchiveEntries)
                    for (auto &e : *mod.ArchiveEntries)
                    {
                        auto name = Normal(e.Name);
                        if (name.starts_with("abdata/") && (name.ends_with(".unity3d") || name.ends_with(".png")))
                            p.Bundles[name.substr(7)].push_back(Path(mod.Path));
                    }
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &e)
            {
                p.Warnings.push_back(Utf8(Path(mod.Path).filename()) + ": " + e.what());
            }
        }
        std::unordered_map<std::string, NativeAssetEntry> unique;
        for (auto &e : entries)
            unique[Key(e.Category, e.Id, e.Guid)] = std::move(e);
        p.Entries.reserve(unique.size());
        for (auto &[key, e] : unique)
            p.Entries.push_back(std::move(e));
        std::sort(p.Entries.begin(), p.Entries.end(),
                  [](auto &a, auto &b)
                  {
                      if (a.Guid.empty() != b.Guid.empty())
                          return a.Guid.empty();
                      if (a.Category != b.Category)
                          return a.Category < b.Category;
                      return a.Get("Name") < b.Get("Name");
                  });
        for (size_t i = 0; i < p.Entries.size(); ++i)
        {
            auto &e = p.Entries[i];
            p.EntryKeys[Key(e.Category, e.Id, e.Guid)] = i;
        }
        p.Loaded = true;
    }

    const std::vector<NativeAssetEntry> &CharacterCatalog::Entries() const
    {
        return impl_->Entries;
    }

    const std::vector<NativeAssetEntry> &CharacterCatalog::StockEntries() const
    {
        return impl_->StockEntries;
    }

    const std::vector<std::string> &CharacterCatalog::Warnings() const
    {
        return impl_->Warnings;
    }

    std::vector<NativeAssetEntry> CharacterCatalog::Query(int category, std::string_view text, int source) const
    {
        std::vector<NativeAssetEntry> result;
        auto needle = Lower(std::string(text));
        for (auto &e : impl_->Entries)
            if (e.Category == category &&
                (source == 0 || (source == 1 && e.Guid.empty()) || (source == 2 && !e.Guid.empty())) &&
                (needle.empty() || Lower(e.Get("Name")).find(needle) != std::string::npos ||
                 Lower(e.Source).find(needle) != std::string::npos ||
                 std::to_string(e.Id).find(needle) != std::string::npos))
                result.push_back(e);
        return result;
    }

    std::optional<NativeAssetEntry> CharacterCatalog::Selected(const CharacterDocument &document,
                                                               std::string_view path) const
    {
        auto binding = CharacterAssetReferences::Describe(document, std::string(path));
        if (!binding)
            return {};
        auto reference = CharacterAssetReferences::Find(document, std::string(path));
        if (reference)
        {
            std::set<std::string> visited;
            return impl_->Resolve(*reference, visited, 0);
        }
        auto key = Key(binding->Category, static_cast<int>(document.Node(path).Integer()), "");
        if (auto found = impl_->EntryKeys.find(key); found != impl_->EntryKeys.end())
            return impl_->Entries[found->second];
        return {};
    }

    NativeAssetEntry CharacterCatalog::Resolve(const ModReference &reference) const
    {
        std::set<std::string> visited;
        if (auto found = impl_->Resolve(reference, visited, 0))
            return *found;
        throw std::runtime_error("Missing MOD asset: " + reference.Guid + "/" + std::to_string(reference.Category) +
                                 "/" + std::to_string(reference.Slot));
    }

    fs::path CharacterCatalog::Bundle(std::string relative, const fs::path &archive)
    {
        auto &p = *impl_;
        auto physical = p.Physical(relative);
        std::replace(relative.begin(), relative.end(), '\\', '/');
        if (!archive.empty())
            if (auto own = p.Extract(archive, relative))
                return *own;
        if (fs::is_regular_file(physical))
            return physical;
        if (auto list = p.Bundles.find(Normal(relative)); list != p.Bundles.end())
            for (auto &other : list->second)
                if (auto found = p.Extract(other, relative))
                    return *found;
        throw std::runtime_error("Missing asset bundle: " + relative);
    }

    AssetReference CharacterCatalog::Asset(std::string relative, std::string name, const fs::path &archive)
    {
        auto &p = *impl_;
        p.Physical(relative);
        std::replace(relative.begin(), relative.end(), '\\', '/');
        if (auto list = p.Bundles.find(Normal(relative)); list != p.Bundles.end())
            for (auto &other : list->second)
                if (auto path = p.Extract(other, relative))
                {
                    auto cacheKey = Utf8(*path);
                    auto known = p.Exports.find(cacheKey);
                    if (known == p.Exports.end())
                    {
                        NativeGpuAssetReader reader(*path, p.Database.get());
                        known = p.Exports.emplace(cacheKey, reader.ExportNames()).first;
                    }
                    for (auto &exported : known->second)
                    {
                        auto base = Normal(Utf8(Path(exported).filename()));
                        if (base == Lower(name) || Lower(Utf8(Path(base).stem())) == Lower(name))
                            return {*path, name};
                    }
                }
        return {Bundle(relative, archive), std::move(name)};
    }

    std::optional<AssetReference> CharacterCatalog::Thumbnail(const NativeAssetEntry &entry)
    {
        auto bundle = entry.Get("ThumbAB"), name = entry.Get("ThumbTex");
        if (!Present(bundle) || !Present(name))
            return {};
        return Asset(bundle, name, entry.Archive);
    }

    std::vector<fs::path> CharacterCatalog::Dependencies(std::string relative, const fs::path &archive)
    {
        auto &p = *impl_;
        auto key = Utf8(archive) + "|" + Normal(relative);
        if (auto saved = p.DependencyPaths.find(key); saved != p.DependencyPaths.end())
            return saved->second;
        std::vector<fs::path> result{Bundle(relative, archive)};
        if (!archive.empty())
        {
            ZipArchive zip(archive);
            for (auto &e : zip.Entries())
            {
                auto name = Normal(e.Name);
                if (name.starts_with("abdata/") && name.ends_with(".unity3d"))
                    result.push_back(Bundle(e.Name.substr(7), archive));
            }
            p.DependencyPaths[key] = result;
            return result;
        }
        if (!p.ManifestLoaded)
        {
            for (auto &f : fs::directory_iterator(p.Game / "abdata"))
            {
                auto name = Utf8(f.path().filename());
                if (!f.is_regular_file() || (name != "abdata" && !(name.starts_with("add") && name.size() > 3 &&
                                                                   std::all_of(name.begin() + 3, name.end(),
                                                                               [](unsigned char c)
                                                                               {
                                                                                   return std::isdigit(c) != 0;
                                                                               }))))
                    continue;
                NativeGpuAssetReader reader(f.path(), p.Database.get());
                for (auto &a : reader.AssetIndex())
                    if (a.ClassId == 290)
                    {
                        auto &value = reader.Metadata(a.PathId);
                        std::unordered_map<int64_t, std::string> names;
                        for (auto &row : value.Get("AssetBundleNames").Array())
                            names[row.Get("first").Integer] = row.Get("second").Text;
                        for (auto &row : value.Get("AssetBundleInfos").Array())
                        {
                            auto bundleName = names.find(row.Get("first").Integer);
                            if (bundleName == names.end())
                                continue;
                            for (auto &d : row.Get("second").Get("AssetBundleDependencies").Array())
                                if (names.contains(d.Integer))
                                    p.Manifest[bundleName->second].push_back(names.at(d.Integer));
                        }
                    }
            }
            p.ManifestLoaded = true;
        }
        std::set<std::string> visited;
        std::function<void(std::string, int)> visit = [&](std::string name, int depth)
        {
            if (depth > 32 || visited.size() > 512)
                throw std::runtime_error("Asset dependency graph exceeds limit.");
            if (!visited.insert(name).second)
                return;
            result.push_back(Bundle(name));
            for (auto &child : p.Manifest[name])
                visit(child, depth + 1);
        };
        visit(std::move(relative), 0);
        p.DependencyPaths[key] = result;
        return result;
    }

    std::shared_ptr<const UnityClassDatabase> CharacterCatalog::Database() const
    {
        return impl_->Database;
    }

    CharacterDocument CharacterCatalog::Select(const CharacterDocument &document, const std::string &path,
                                               const NativeAssetEntry &entry)
    {
        if (path == "face.moleLayout" || (path.starts_with("face.makeup.paintInfo[") && path.ends_with(".layout")))
        {
            if (entry.Category != (path == "face.moleLayout" ? 6 : 7))
                throw std::runtime_error("Preset category mismatch.");
            auto next = document;
            const char *keys[]{"Scale", "Scale", "PosX", "PosY"};
            for (int i = 0; i < 4; ++i)
                next = next.WithValue(path + "[" + std::to_string(i) + "]", std::stof(entry.Get(keys[i])));
            return next;
        }
        for (auto &bundle : {entry.Bundle, entry.Get("MainTexAB"), entry.Get("TexAB")})
            if (Present(bundle))
                Bundle(bundle, entry.Archive);
        auto next = CharacterEditRules::Select(document, path, entry.Editable());
        if (path.ends_with(".id") && (path.starts_with("hair.") || path.starts_with("accessory.")))
        {
            auto name = document.Sex() == 0 && Present(entry.MaleAsset) ? entry.MaleAsset : entry.Asset;
            if (Present(entry.Bundle) && Present(name) && name != "p_dummy")
            {
                auto ref = Asset(entry.Bundle, name, entry.Archive);
                NativeGpuAssetReader reader(ref.Bundle, impl_->Database.get());
                auto prefab = reader.Prefab(name);
                std::vector<std::string> diagnostics;
                next = CharacterEditRules::InitializeAsset(next, path,
                                                           ReadCharacterComponents(reader, prefab, diagnostics));
                impl_->Warnings.insert(impl_->Warnings.end(), diagnostics.begin(), diagnostics.end());
            }
        }
        return next;
    }
}
