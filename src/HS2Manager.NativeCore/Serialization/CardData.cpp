#include "Common/Core.h"
#include <algorithm>
#include <array>
#include <charconv>
#include <map>
#include <set>
#include <regex>

namespace hs2
{
    namespace
    {
        class Reader
        {
        public:
            std::span<const std::uint8_t> Data;
            std::size_t Position{};

            explicit Reader(std::span<const std::uint8_t> bytes) : Data(bytes)
            {
            }

            std::span<const std::uint8_t> Take(std::size_t length)
            {
                if (Position > Data.size() || length > Data.size() - Position)
                    throw std::runtime_error("人物数据截断");
                auto result = Data.subspan(Position, length);
                Position += length;
                return result;
            }

            std::uint64_t Number(int size, bool big = false)
            {
                auto bytes = Take(size);
                std::uint64_t result = 0;
                for (int i = 0; i < size; i++)
                    result |= std::uint64_t(bytes[i]) << ((big ? size - 1 - i : i) * 8);
                return result;
            }

            std::int32_t Int()
            {
                return static_cast<std::int32_t>(Number(4));
            }

            std::string Text()
            {
                std::uint32_t length = 0;
                for (int shift = 0;; shift += 7)
                {
                    if (shift > 28)
                        throw std::runtime_error("字符串长度损坏");
                    auto b = Number(1);
                    length |= static_cast<std::uint32_t>(b & 127) << shift;
                    if (!(b & 128))
                        break;
                }
                if (length > 65536)
                    throw std::runtime_error("字符串长度越界");
                auto bytes = Take(length);
                return {reinterpret_cast<const char *>(bytes.data()), bytes.size()};
            }
        };

        std::string TextOr(const MessagePack &node, const std::string &key, std::string value = {})
        {
            auto p = node.Find(key);
            return p && std::holds_alternative<std::string>(p->ScalarValue) ? p->Text() : value;
        }

        std::int64_t IntOr(const MessagePack &node, const std::string &key, std::int64_t value = 0)
        {
            auto p = node.Find(key);
            if (!p)
                return value;
            try
            {
                return p->Integer();
            }
            catch (...)
            {
                return value;
            }
        }

        void ExtractReferences(const MessagePack &node, std::vector<ModReference> &output, int depth)
        {
            if (depth > 20)
                return;
            if (node.Type == MessagePack::Kind::Binary)
            {
                ExtractReferences(MessagePack::Parse(node.Payload()), output, depth + 1);
            }
            else if (node.Type == MessagePack::Kind::Map)
            {
                auto guid = TextOr(node, "ModID");
                if (!guid.empty())
                {
                    ModReference reference{guid, static_cast<int>(IntOr(node, "Slot", -1)),
                                           static_cast<int>(IntOr(node, "CategoryNo", -1)), TextOr(node, "Property")};
                    if (std::find(output.begin(), output.end(), reference) == output.end())
                        output.push_back(reference);
                }
                else
                    for (std::size_t i = 1; i < node.Children.size(); i += 2)
                        ExtractReferences(node.Children[i], output, depth + 1);
            }
            else if (node.Type == MessagePack::Kind::Array)
                for (auto &child : node.Children)
                    ExtractReferences(child, output, depth + 1);
        }

        void WriteNumber(Bytes &output, std::uint64_t value, int bytes)
        {
            for (int i = 0; i < bytes; i++)
                output.push_back(static_cast<std::uint8_t>(value >> (8 * i)));
        }

        void Append(Bytes &output, std::span<const std::uint8_t> bytes)
        {
            output.insert(output.end(), bytes.begin(), bytes.end());
        }

        std::vector<MessagePack::Value> Tokens(std::string_view path)
        {
            std::vector<MessagePack::Value> tokens;
            std::size_t at = 0;
            while (at < path.size())
            {
                if (path[at] == '.')
                {
                    at++;
                    continue;
                }
                if (path[at] == '[' || path[at] == '{')
                {
                    char close = path[at++] == '[' ? ']' : '}';
                    auto end = path.find(close, at);
                    if (end == std::string_view::npos)
                        throw std::invalid_argument("参数路径格式错误");
                    std::int64_t number{};
                    auto [stop, error] = std::from_chars(path.data() + at, path.data() + end, number);
                    if (error != std::errc() || stop != path.data() + end)
                        throw std::invalid_argument("参数路径格式错误");
                    tokens.push_back(number);
                    at = end + 1;
                }
                else
                {
                    auto end = path.find_first_of(".[{", at);
                    if (end == std::string_view::npos)
                        end = path.size();
                    tokens.push_back(std::string(path.substr(at, end - at)));
                    at = end;
                }
            }
            if (tokens.empty() || !std::holds_alternative<std::string>(tokens[0]))
                throw std::invalid_argument("参数路径为空");
            return tokens;
        }
    }

    std::size_t CardParser::FindPngEnd(std::span<const std::uint8_t> bytes)
    {
        Reader input(bytes);
        constexpr std::array<std::uint8_t, 8> signature{137, 80, 78, 71, 13, 10, 26, 10};
        auto first = input.Take(8);
        if (!std::equal(first.begin(), first.end(), signature.begin()))
            throw std::runtime_error("PNG 文件头无效");
        for (int i = 0; i < 100000; i++)
        {
            auto length = input.Number(4, true);
            auto typeBytes = input.Take(4);
            std::string type(typeBytes.begin(), typeBytes.end());
            if (i == 0 && (type != "IHDR" || length != 13))
                throw std::runtime_error("PNG 缺少 IHDR");
            input.Take(static_cast<std::size_t>(length));
            input.Take(4);
            if (type == "IEND")
            {
                if (length)
                    throw std::runtime_error("PNG IEND 无效");
                return input.Position;
            }
        }
        throw std::runtime_error("PNG 数据块过多");
    }

    CardRecord CardParser::Parse(const fs::path &path, std::stop_token token) const
    {
        CardRecord card;
        card.Path = Utf8(fs::absolute(path));
        card.Name = Utf8(path.stem());
        bool opened = false;
        try
        {
            card.Size = fs::file_size(path);
            card.ModifiedUtcTicks = ModifiedTicks(path);
            auto bytes = ReadFile(path, 512ull * 1024 * 1024, token);
            opened = true;
            Reader reader(bytes);
            card.PngLength = FindPngEnd(bytes);
            reader.Position = static_cast<std::size_t>(card.PngLength);
            if (reader.Position == bytes.size())
            {
                card.Status = CardStatus::ImageOnly;
                return card;
            }
            auto product = reader.Int();
            if (product < 0 || product > 1000)
            {
                card.Status = CardStatus::Unsupported;
                card.Error = "无法识别的附加数据";
                return card;
            }
            auto marker = reader.Text();
            if (marker != "【AIS_Chara】" && marker != "〖AIS_Chara〗")
            {
                card.Status = CardStatus::Unsupported;
                card.Error = "不是 HS2 / AI 人物卡：" + marker;
                return card;
            }
            card.Version = reader.Text();
            reader.Int();
            reader.Text();
            reader.Text();
            auto tableSize = reader.Int();
            if (tableSize < 1 || tableSize > 1024 * 1024)
                throw std::runtime_error("人物数据块目录损坏");
            auto table = MessagePack::Parse(reader.Take(tableSize));
            auto dataLength = reader.Number(8);
            if (dataLength > bytes.size() - reader.Position)
                throw std::runtime_error("人物数据长度越界");
            auto dataStart = reader.Position;
            auto &entries = table.Require(std::string("lstInfo"));
            if (entries.Type != MessagePack::Kind::Array)
                throw std::runtime_error("人物块目录无效");
            bool parameter = false;
            for (auto &block : entries.Children)
            {
                CheckCancelled(token);
                auto name = TextOr(block, "name");
                auto offset = IntOr(block, "pos"), length = IntOr(block, "size");
                if (offset < 0 || length < 0 || static_cast<std::uint64_t>(offset) > dataLength ||
                    static_cast<std::uint64_t>(length) > dataLength - offset)
                    throw std::runtime_error("人物数据块越界");
                auto blockBytes = std::span(bytes).subspan(dataStart + static_cast<std::size_t>(offset),
                                                           static_cast<std::size_t>(length));
                if (name == "Parameter")
                {
                    auto fields = MessagePack::Parse(blockBytes);
                    card.Name = TextOr(fields, "fullname", card.Name);
                    if (card.Name.empty())
                        card.Name = Utf8(path.stem());
                    card.Sex = static_cast<int>(IntOr(fields, "sex"));
                    parameter = true;
                }
                else if (name == "KKEx")
                {
                    try
                    {
                        auto extensions = MessagePack::Parse(blockBytes);
                        if (extensions.Type != MessagePack::Kind::Map)
                            throw std::runtime_error("扩展数据格式无效");
                        for (std::size_t i = 0; i < extensions.Children.size(); i += 2)
                        {
                            auto key = extensions.Children[i].Text();
                            card.PluginDataIds.push_back(key);
                            if (key == "com.bepis.sideloader.universalautoresolver" ||
                                key == "EC.Core.Sideloader.UniversalAutoResolver")
                                ExtractReferences(extensions.Children[i + 1], card.Dependencies, 0);
                        }
                    }
                    catch (const std::exception &error)
                    {
                        card.DependenciesComplete = false;
                        card.Error = std::string("部分扩展依赖无法解析：") + error.what();
                    }
                }
            }
            if (!parameter)
                throw std::runtime_error("缺少 Parameter 数据块");
            card.Status = CardStatus::Valid;
        }
        catch (const Cancelled &)
        {
            throw;
        }
        catch (const std::exception &error)
        {
            card.Status = opened ? CardStatus::Corrupt : CardStatus::Unreadable;
            card.Error = error.what();
        }
        return card;
    }

    void CardParser::CopyCover(const fs::path &source, std::int64_t length, const fs::path &output,
                               std::stop_token token)
    {
        if (length < 1 || length > 32 * 1024 * 1024)
            return;
        auto bytes = ReadFile(source, 512ull * 1024 * 1024, token);
        if (static_cast<std::uint64_t>(length) > bytes.size())
            throw std::runtime_error("封面数据截断");
        CheckCancelled(token);
        WriteAtomic(output, std::span(bytes).first(static_cast<std::size_t>(length)));
    }

    struct CharacterDocument::Data
    {
        struct Block
        {
            std::size_t TableIndex, Offset, Length;
            std::string Name;
        };

        struct Section
        {
            std::string Block;
            std::size_t Index;
            MessagePack Node;
        };

        std::shared_ptr<const Bytes> Original;
        fs::path SourcePath;
        std::string SourceHash;
        int Sex{};
        std::size_t PngLength{}, PrefixLength{}, DataStart{}, DataLength{};
        MessagePack Table;
        std::vector<Block> Blocks;
        std::map<std::string, Section> Sections, SourceSections;
        std::optional<MessagePack> Extensions, SourceExtensions;
        std::optional<Bytes> Cover;
    };

    CharacterDocument CharacterDocument::Open(const fs::path &path, std::stop_token token)
    {
        return FromBytes(ReadFile(path, 512ull * 1024 * 1024, token), path);
    }

    CharacterDocument CharacterDocument::FromBytes(Bytes bytes, fs::path path)
    {
        auto data = std::make_shared<Data>();
        data->Original = std::make_shared<Bytes>(std::move(bytes));
        data->SourcePath = fs::absolute(path);
        data->SourceHash = Sha256(*data->Original);
        Reader reader(*data->Original);
        data->PngLength = CardParser::FindPngEnd(reader.Data);
        reader.Position = data->PngLength;
        auto product = reader.Int();
        auto marker = reader.Text();
        auto version = reader.Text();
        if (product < 0 || product > 100 || (marker != "【AIS_Chara】" && marker != "〖AIS_Chara〗") ||
            version.empty() || version.find_first_not_of("0123456789.") != std::string::npos)
            throw std::runtime_error("不是支持编辑的 HS2 / AI 人物卡");
        reader.Int();
        reader.Text();
        reader.Text();
        data->PrefixLength = reader.Position;
        auto tableSize = reader.Int();
        if (tableSize < 1 || tableSize > 1024 * 1024)
            throw std::runtime_error("人物块目录越界");
        data->Table = MessagePack::Parse(reader.Take(tableSize));
        auto length = reader.Number(8);
        data->DataStart = reader.Position;
        if (length > reader.Data.size() - reader.Position)
            throw std::runtime_error("人物数据长度越界");
        data->DataLength = static_cast<std::size_t>(length);
        auto &entries = data->Table.Require(std::string("lstInfo"));
        if (entries.Type != MessagePack::Kind::Array || entries.Count() > 4096)
            throw std::runtime_error("人物块目录格式无效");
        for (std::size_t i = 0; i < entries.Children.size(); i++)
        {
            auto &entry = entries.Children[i];
            auto offset = entry.Require(std::string("pos")).Integer(),
                 size = entry.Require(std::string("size")).Integer();
            if (offset < 0 || size < 0 || static_cast<std::uint64_t>(offset) > length ||
                static_cast<std::uint64_t>(size) > length - offset)
                throw std::runtime_error("人物块越界");
            data->Blocks.push_back({i, static_cast<std::size_t>(offset), static_cast<std::size_t>(size),
                                    entry.Require(std::string("name")).Text()});
        }
        std::sort(data->Blocks.begin(), data->Blocks.end(),
                  [](auto &a, auto &b)
                  {
                      return a.Offset < b.Offset;
                  });
        std::size_t lastEnd = 0;
        std::set<std::string> known;
        for (auto &block : data->Blocks)
        {
            if (block.Offset < lastEnd)
                throw std::runtime_error("人物块重叠，无法安全编辑");
            lastEnd = block.Offset + block.Length;
            if (block.Name == "Custom" || block.Name == "Coordinate" || block.Name == "Parameter" ||
                block.Name == "Parameter2" || block.Name == "GameInfo" || block.Name == "GameInfo2" ||
                block.Name == "KKEx")
                if (!known.insert(block.Name).second)
                    throw std::runtime_error("重复的人物块无法安全编辑");
        }
        auto blockBytes = [&](const std::string &name)
        {
            for (auto &block : data->Blocks)
                if (block.Name == name)
                    return reader.Data.subspan(data->DataStart + block.Offset, block.Length);
            throw std::runtime_error("人物卡缺少 " + name + " 数据");
        };
        auto parameter = MessagePack::Parse(blockBytes("Parameter"));
        data->Sex = static_cast<int>(parameter.Require(std::string("sex")).Integer());
        if (data->Sex != 0 && data->Sex != 1)
            throw std::runtime_error("人物性别未知");
        auto addSections = [&](const std::string &block, const std::vector<std::string> &names)
        {
            Reader sections(blockBytes(block));
            for (std::size_t i = 0; i < names.size(); i++)
            {
                auto size = sections.Int();
                if (size < 1 || size > 16 * 1024 * 1024)
                    throw std::runtime_error("人物外观长度越界");
                auto node = MessagePack::Parse(sections.Take(size));
                if (node.Type != MessagePack::Kind::Map)
                    throw std::runtime_error("人物外观参数不是字典");
                data->Sections.emplace(names[i], Data::Section{block, i, std::move(node)});
            }
        };
        addSections("Custom", {"face", "body", "hair"});
        addSections("Coordinate", {"clothes", "accessory"});
        data->Sections.emplace("parameter", Data::Section{"Parameter", 0, std::move(parameter)});
        for (auto [block, name] :
             {std::pair{"Parameter2", "parameter2"}, {"GameInfo", "gameinfo"}, {"GameInfo2", "gameinfo2"}})
            if (known.contains(block))
            {
                // 新版本的未知布局仍原样保存，不把它误当作可编辑字典。
                try
                {
                    auto node = MessagePack::Parse(blockBytes(block));
                    if (node.Type == MessagePack::Kind::Map)
                        data->Sections.emplace(name, Data::Section{block, 0, std::move(node)});
                }
                catch (const std::exception &)
                {
                }
            }
        if (known.contains("KKEx"))
        {
            data->Extensions = MessagePack::Parse(blockBytes("KKEx"));
            if (data->Extensions->Type != MessagePack::Kind::Map)
                throw std::runtime_error("扩展数据格式无效");
        }
        data->SourceSections = data->Sections;
        data->SourceExtensions = data->Extensions;
        return CharacterDocument(data);
    }

    const fs::path &CharacterDocument::SourcePath() const
    {
        return data_->SourcePath;
    }

    const std::string &CharacterDocument::SourceHash() const
    {
        return data_->SourceHash;
    }

    int CharacterDocument::Sex() const
    {
        return data_->Sex;
    }

    std::size_t CharacterDocument::PngLength() const
    {
        return data_->Cover ? data_->Cover->size() : data_->PngLength;
    }

    std::span<const std::uint8_t> CharacterDocument::Cover() const
    {
        return data_->Cover ? std::span<const std::uint8_t>(*data_->Cover)
                            : std::span(*data_->Original).first(data_->PngLength);
    }

    std::vector<std::string> CharacterDocument::SectionNames() const
    {
        std::vector<std::string> result;
        for (auto &[name, section] : data_->Sections)
            result.push_back(name);
        return result;
    }

    std::span<const std::uint8_t> CharacterDocument::BlockBytes(std::string_view name) const
    {
        for (auto &block : data_->Blocks)
            if (block.Name == name)
                return std::span(*data_->Original).subspan(data_->DataStart + block.Offset, block.Length);
        throw std::runtime_error("人物卡缺少数据块：" + std::string(name));
    }

    const MessagePack &CharacterDocument::Node(std::string_view path) const
    {
        auto tokens = Tokens(path);
        auto found = data_->Sections.find(std::get<std::string>(tokens[0]));
        if (found == data_->Sections.end())
            throw std::invalid_argument("未知外观分类");
        auto node = &found->second.Node;
        for (std::size_t i = 1; i < tokens.size(); i++)
            node = &node->Require(tokens[i]);
        return *node;
    }

    const std::optional<MessagePack> &CharacterDocument::Extensions() const
    {
        return data_->Extensions;
    }

    bool CharacterDocument::Contains(std::string_view path) const
    {
        try
        {
            Node(path);
            return true;
        }
        catch (const std::runtime_error &)
        {
            return false;
        }
        catch (const std::invalid_argument &)
        {
            return false;
        }
    }

    bool CharacterDocument::IsOriginal() const
    {
        if (data_->Cover)
            return false;
        for (auto &[name, section] : data_->Sections)
            if (!section.Node.ContentEquals(data_->SourceSections.at(name).Node))
                return false;
        return data_->Extensions.has_value() == data_->SourceExtensions.has_value() &&
               (!data_->Extensions || data_->Extensions->ContentEquals(*data_->SourceExtensions));
    }

    bool CharacterDocument::ContentEquals(const CharacterDocument &other) const
    {
        if (data_ == other.data_)
            return true;
        if (SourceHash() != other.SourceHash())
            return false;
        if (data_->Cover != other.data_->Cover)
            return false;
        for (auto &[name, section] : data_->Sections)
            if (!section.Node.ContentEquals(other.data_->Sections.at(name).Node))
                return false;
        return data_->Extensions.has_value() == other.data_->Extensions.has_value() &&
               (!data_->Extensions || data_->Extensions->ContentEquals(*other.data_->Extensions));
    }

    CharacterDocument CharacterDocument::WithValue(std::string_view path, MessagePack::Value value) const
    {
        return WithNode(path, Node(path).WithScalar(std::move(value)));
    }

    CharacterDocument CharacterDocument::WithNode(std::string_view path, const MessagePack &replacement) const
    {
        auto tokens = Tokens(path);
        auto name = std::get<std::string>(tokens[0]);
        auto &root = data_->Sections.at(name).Node;
        std::function<MessagePack(const MessagePack &, std::size_t)> replace =
            [&](const MessagePack &current, std::size_t depth)
        {
            return depth == tokens.size()
                       ? replacement
                       : current.Set(tokens[depth], replace(current.Require(tokens[depth]), depth + 1));
        };
        auto updated = replace(root, 1);
        if (name == "parameter" && updated.Require(std::string("sex")).Integer() != data_->Sex)
            throw std::invalid_argument("性别在创建工程时确定，不能直接修改已有角色的性别字段");
        if (updated.ContentEquals(root))
            return *this;
        auto next = std::make_shared<Data>(*data_);
        next->Sections.at(name).Node = std::move(updated);
        return CharacterDocument(next);
    }

    CharacterDocument CharacterDocument::WithExtensions(std::optional<MessagePack> value) const
    {
        if (value && value->Type != MessagePack::Kind::Map)
            throw std::invalid_argument("扩展数据必须是字典");
        auto next = std::make_shared<Data>(*data_);
        next->Extensions = std::move(value);
        return CharacterDocument(next);
    }

    CharacterDocument CharacterDocument::WithName(std::string name) const
    {
        if (name.empty() || name.size() > 1024 || name.find('\0') != std::string::npos)
            throw std::invalid_argument("人物姓名为空或过长");
        return WithNode("parameter",
                        Node("parameter").Set(std::string("fullname"), MessagePack::Scalar(std::move(name)), true));
    }

    CharacterDocument CharacterDocument::WithCover(Bytes png) const
    {
        if (png.size() > 32ull * 1024 * 1024 || CardParser::FindPngEnd(png) != png.size())
            throw std::invalid_argument("新封面必须是完整且不含尾部数据的 PNG");
        auto next = std::make_shared<Data>(*data_);
        next->Cover = std::move(png);
        return CharacterDocument(next);
    }

    Bytes CharacterDocument::ToBytes() const
    {
        if (IsOriginal())
            return *data_->Original;
        auto original = std::span(*data_->Original);
        auto entries = data_->Table.Require(std::string("lstInfo"));
        Bytes payload;
        std::size_t originalOffset = 0;
        for (auto &block : data_->Blocks)
        {
            Append(payload, original.subspan(data_->DataStart + originalOffset, block.Offset - originalOffset));
            auto offset = payload.size();
            auto raw = original.subspan(data_->DataStart + block.Offset, block.Length);
            Bytes replacement(raw.begin(), raw.end());
            if (block.Name == "Custom" || block.Name == "Coordinate")
            {
                std::vector<Data::Section> sections;
                bool changed = false;
                for (auto &[name, part] : data_->Sections)
                    if (part.Block == block.Name)
                    {
                        sections.push_back(part);
                        if (!part.Node.ContentEquals(data_->SourceSections.at(name).Node))
                            changed = true;
                    }
                if (changed)
                {
                    replacement.clear();
                    std::sort(sections.begin(), sections.end(),
                              [](auto &a, auto &b)
                              {
                                  return a.Index < b.Index;
                              });
                    Reader old(raw);
                    for (auto &section : sections)
                    {
                        auto encoded = section.Node.ToBytes();
                        WriteNumber(replacement, encoded.size(), 4);
                        Append(replacement, encoded);
                        auto length = old.Int();
                        if (length < 0)
                            throw std::runtime_error("人物外观长度无效");
                        old.Take(length);
                    }
                    Append(replacement, raw.subspan(old.Position));
                }
            }
            else if (block.Name == "KKEx" && (!data_->Extensions || !data_->SourceExtensions ||
                                              !data_->Extensions->ContentEquals(*data_->SourceExtensions)))
                replacement = data_->Extensions ? data_->Extensions->ToBytes() : MessagePack::Map().ToBytes();
            else
                for (auto &[name, section] : data_->Sections)
                    if (section.Block == block.Name && !section.Node.ContentEquals(data_->SourceSections.at(name).Node))
                        replacement = section.Node.ToBytes();
            auto row = entries.Require(static_cast<std::int64_t>(block.TableIndex));
            row = row.Set(std::string("pos"),
                          row.Require(std::string("pos")).WithScalar(static_cast<std::int64_t>(offset)));
            row = row.Set(std::string("size"),
                          row.Require(std::string("size")).WithScalar(static_cast<std::int64_t>(replacement.size())));
            entries = entries.Set(static_cast<std::int64_t>(block.TableIndex), row);
            Append(payload, replacement);
            originalOffset = block.Offset + block.Length;
        }
        Append(payload, original.subspan(data_->DataStart + originalOffset, data_->DataLength - originalOffset));
        if (!data_->SourceExtensions && data_->Extensions)
        {
            auto extension = data_->Extensions->ToBytes();
            auto row = MessagePack::Map(
                {{std::string("name"), MessagePack::Scalar(std::string("KKEx"))},
                 {std::string("version"), MessagePack::Scalar(std::string("3"))},
                 {std::string("pos"), MessagePack::Scalar(static_cast<std::int64_t>(payload.size()))},
                 {std::string("size"), MessagePack::Scalar(static_cast<std::int64_t>(extension.size()))}});
            auto children = entries.Children;
            children.push_back(row);
            entries = MessagePack::Array(std::move(children));
            Append(payload, extension);
        }
        auto table = data_->Table.Set(std::string("lstInfo"), entries).ToBytes();
        Bytes output;
        Append(output, Cover());
        Append(output, original.subspan(data_->PngLength, data_->PrefixLength - data_->PngLength));
        WriteNumber(output, table.size(), 4);
        Append(output, table);
        WriteNumber(output, payload.size(), 8);
        Append(output, payload);
        Append(output, original.subspan(data_->DataStart + data_->DataLength));
        return output;
    }

    CharacterEditSession::CharacterEditSession(CardRecord card, CharacterDocument document)
        : Card(std::move(card)), Document(document), Baseline(std::move(document))
    {
    }

    bool CharacterEditSession::IsDirty() const
    {
        return !Document.ContentEquals(Baseline);
    }

    bool CharacterEditSession::CanUndo() const
    {
        return !undo_.empty() || (gesture_ && !Document.ContentEquals(*gesture_));
    }

    bool CharacterEditSession::CanRedo() const
    {
        return !redo_.empty();
    }

    void CharacterEditSession::BeginGesture()
    {
        EndGesture();
        gesture_ = Document;
    }

    void CharacterEditSession::EndGesture()
    {
        if (!gesture_)
            return;
        if (!Document.ContentEquals(*gesture_))
        {
            undo_.push_back(*gesture_);
            redo_.clear();
        }
        gesture_.reset();
    }

    void CharacterEditSession::Apply(CharacterDocument next)
    {
        if (next.SourceHash() != Document.SourceHash())
            throw std::invalid_argument("不能应用其他人物卡的草稿");
        if (Document.ContentEquals(next))
            return;
        if (!gesture_)
            undo_.push_back(Document);
        redo_.clear();
        Document = std::move(next);
        Revision++;
        if (Changed)
            Changed();
    }

    void CharacterEditSession::SetValue(std::string_view path, MessagePack::Value value)
    {
        Apply(CharacterEditRules::SetValue(Document, std::string(path), std::move(value)));
    }

    void CharacterEditSession::SetColor(std::string_view path, const std::vector<float> &color)
    {
        Apply(CharacterEditRules::Color(Document, std::string(path), color));
    }

    void CharacterEditSession::Restore(std::string_view input)
    {
        EndGesture();
        std::string path(input);
        auto next = Document.WithNode(path, Baseline.Node(path));
        if (std::regex_match(path, std::regex(R"(^hair\.parts\[\d+\]\.id$)")))
        {
            auto root = path.substr(0, path.size() - 3);
            for (auto field : {"bundleId", "dictBundle", "acsColorInfo"})
                if (Baseline.Contains(root + "." + field))
                    next = next.WithNode(root + "." + field, Baseline.Node(root + "." + field));
        }
        if (std::regex_match(path, std::regex(R"(^accessory\.parts\[\d+\]\.(id|type)$)")))
        {
            auto root = path.substr(0, path.rfind('.'));
            next = next.WithNode(root, Baseline.Node(root));
            path = root + ".id";
        }
        Apply(CharacterAssetReferences::Restore(next, Baseline, path));
    }

    void CharacterEditSession::RestoreCategory(std::string_view category)
    {
        EndGesture();
        if (category == "parameter")
        {
            auto next = Document;
            for (auto root : {"parameter", "parameter2", "gameinfo", "gameinfo2"})
                if (Baseline.Contains(root))
                    next = next.WithNode(root, Baseline.Node(root));
            Apply(next);
            return;
        }
        std::string root = category == "makeup" ? "face.makeup" : std::string(category);
        auto next = CharacterAssetReferences::Restore(Document.WithNode(root, Baseline.Node(root)), Baseline, root);
        if (category == "face" && Document.Contains("face.makeup"))
            next = next.WithNode("face.makeup", Document.Node("face.makeup"));
        Apply(next);
    }

    void CharacterEditSession::RestoreAll()
    {
        EndGesture();
        Apply(Baseline);
    }

    void CharacterEditSession::Undo()
    {
        EndGesture();
        if (undo_.empty())
            return;
        redo_.push_back(Document);
        Document = undo_.back();
        undo_.pop_back();
        Revision++;
        if (Changed)
            Changed();
    }

    void CharacterEditSession::Redo()
    {
        EndGesture();
        if (redo_.empty())
            return;
        undo_.push_back(Document);
        Document = redo_.back();
        redo_.pop_back();
        Revision++;
        if (Changed)
            Changed();
    }

    void CharacterEditSession::AcceptSaved(CardRecord card, CharacterDocument document, std::uint64_t revision)
    {
        if (revision != Revision)
            throw std::runtime_error("保存时仍有新的编辑");
        gesture_.reset();
        undo_.clear();
        redo_.clear();
        Card = std::move(card);
        Document = document;
        Baseline = std::move(document);
        Revision++;
        if (Changed)
            Changed();
    }
}
