#include "Character/CharacterExtensions.h"
#include "Character/CharacterCatalog.h"
#include "Assets/AssetReader.h"
#include "Animation/Animation.h"
#include "Animation/ShapeRig.h"
#include "Mods/ModIndex.h"
#include <windows.h>
#include <shlwapi.h>
#include <xmllite.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <charconv>
#include <cmath>
#include <set>
#include <sstream>

namespace hs2
{
    namespace
    {
        using MP = MessagePack;

        void Require(bool condition, const std::string &message)
        {
            if (!condition)
                throw std::runtime_error(message);
        }

        std::string Lower(std::string value)
        {
            for (auto &c : value)
                if (c >= 'A' && c <= 'Z')
                    c = char(c - 'A' + 'a');
            return value;
        }

        std::string Trim(const std::string &value)
        {
            auto first = value.find_first_not_of(" \t\r\n"), last = value.find_last_not_of(" \t\r\n");
            return first == value.npos ? std::string() : value.substr(first, last - first + 1);
        }

        bool Null(const MP *value)
        {
            return !value ||
                   (value->Type == MP::Kind::Scalar && std::holds_alternative<std::monostate>(value->ScalarValue));
        }

        const MP *Get(const MP &value, const char *name)
        {
            return value.Find(std::string(name));
        }

        std::string Text(const MP *value, std::string fallback = {})
        {
            if (Null(value))
                return fallback;
            auto text = std::get_if<std::string>(&value->ScalarValue);
            return text ? *text : fallback;
        }

        float Number(const MP *value, float fallback = 0)
        {
            if (Null(value))
                return fallback;
            float result = std::visit(
                [](auto &item) -> float
                {
                    using T = std::decay_t<decltype(item)>;
                    if constexpr (std::is_arithmetic_v<T>)
                        return static_cast<float>(item);
                    else if constexpr (std::is_same_v<T, std::string>)
                    {
                        float result;
                        auto text = Trim(item);
                        auto [end, error] = std::from_chars(text.data(), text.data() + text.size(), result);
                        if (error == std::errc() && end == text.data() + text.size())
                            return result;
                        throw std::runtime_error("扩展数值文本无效。");
                    }
                    else
                        throw std::runtime_error("扩展数值无效。");
                },
                value->ScalarValue);
            Require(std::isfinite(result), "扩展数值不是有限值。");
            return result;
        }

        std::int64_t Integer(const MP *value, std::int64_t fallback = 0)
        {
            if (Null(value))
                return fallback;
            if (auto text = std::get_if<std::string>(&value->ScalarValue))
            {
                std::int64_t result;
                auto t = Trim(*text);
                auto [end, error] = std::from_chars(t.data(), t.data() + t.size(), result);
                Require(error == std::errc() && end == t.data() + t.size(), "扩展整数文本无效。");
                return result;
            }
            return value->Integer();
        }

        int Int(const MP *value, int fallback = 0)
        {
            auto number = Integer(value, fallback);
            Require(number >= INT_MIN && number <= INT_MAX, "扩展整数超限。");
            return static_cast<int>(number);
        }

        bool Flag(const MP *value, bool fallback = false)
        {
            if (Null(value))
                return fallback;
            auto flag = std::get_if<bool>(&value->ScalarValue);
            return flag ? *flag : fallback;
        }

        std::vector<float> Flatten(const MP &value)
        {
            std::vector<float> output;
            std::function<void(const MP &)> visit = [&](const MP &node)
            {
                if (node.Type == MP::Kind::Array)
                    for (auto &child : node.Children)
                        visit(child);
                else
                    output.push_back(Number(&node));
            };
            visit(value);
            return output;
        }

        std::vector<float> Flatten(const Parameter &value)
        {
            std::vector<float> output;
            std::function<void(const Parameter &)> visit = [&](const Parameter &node)
            {
                if (node.Children.empty())
                {
                    Require(std::isfinite(node.Number), "角色参数不是有限值。");
                    output.push_back(static_cast<float>(node.Number));
                }
                else
                    for (auto &child : node.Children)
                        visit(child);
            };
            visit(value);
            return output;
        }

        Vector3 Vector(const MP &value)
        {
            auto values = Flatten(value);
            Require(values.size() == 3, "ABMX 向量无效。");
            return {values[0], values[1], values[2]};
        }

        std::optional<MP> Unpack(const MP &data, const char *key)
        {
            auto *value = Get(data, key);
            if (Null(value))
                return {};
            Require(value->Type == MP::Kind::Binary, "扩展字段不是二进制数据。");
            return MP::Parse(value->Payload());
        }

        std::vector<const MP *> Items(const std::optional<MP> &value)
        {
            if (!value)
                return {};
            Require(value->Type == MP::Kind::Array && value->Children.size() <= 4096, "扩展属性列表无效。");
            std::vector<const MP *> result;
            for (auto &row : value->Children)
            {
                Require(row.Type == MP::Kind::Map, "扩展属性不是字典。");
                if (Integer(Get(row, "CoordinateIndex")) == 0)
                    result.push_back(&row);
            }
            return result;
        }

        void Set(Parameter &parameters, Parameter value)
        {
            auto found = std::find_if(parameters.Children.begin(), parameters.Children.end(),
                                      [&](auto &item)
                                      {
                                          return item.Name == value.Name;
                                      });
            if (found == parameters.Children.end())
                parameters.Children.push_back(std::move(value));
            else
                *found = std::move(value);
        }

        void Set(Parameter &parameters, std::string name, double value)
        {
            Parameter p;
            p.Name = std::move(name);
            p.Number = value;
            Set(parameters, std::move(p));
        }

        std::string Property(const MP &item)
        {
            auto property = Text(Get(item, "Property"));
            auto first = property.find_first_not_of('_');
            return "_" + (first == property.npos ? std::string() : property.substr(first));
        }

        MaterialEdit Edit(const MP &item, const std::string &kind)
        {
            MaterialEdit result;
            result.ObjectType = Int(Get(item, "ObjectType"));
            result.Slot = Int(Get(item, "Slot"));
            result.Name = Text(Get(item, kind == "renderer" ? "RendererName" : "MaterialName"));
            result.Kind = kind;
            return result;
        }

        struct Reader
        {
            CharacterDefinition &Definition;
            CharacterExtensionState State;
            const CharacterDocument &Document;
            fs::path Cache;
            std::stop_token Cancellation;

            void Warning(std::string text)
            {
                Definition.Diagnostics.push_back(std::move(text));
            }

            std::optional<EmbeddedTextureReference> Texture(const MP *binary)
            {
                if (Null(binary))
                    return {};
                Require(binary->Type == MP::Kind::Binary, "内嵌贴图不是二进制数据。");
                auto bytes = binary->Payload();
                if (bytes.empty())
                    return {};
                Require(bytes.size() <= 256ull * 1024 * 1024 && bytes.size() <= INT_MAX, "内嵌贴图大小超限。");
                CheckCancelled(Cancellation);
                auto hash = Sha256(bytes);
                auto target = Cache / Path(hash + ".bin");
                if (!fs::exists(target) || fs::file_size(target) != bytes.size() ||
                    HashFile(target, Cancellation) != hash)
                    WriteAtomic(target, bytes);
                return EmbeddedTextureReference{target, 0, static_cast<int>(bytes.size())};
            }

            void Bones(const MP &data)
            {
                auto packed = Unpack(data, "boneData");
                if (!packed)
                    return;
                if (packed->Type == MP::Kind::Binary ||
                    (packed->Type == MP::Kind::Extension && packed->Integer() == 99))
                {
                    auto bytes = packed->Payload();
                    Require(!bytes.empty(), "ABMX 压缩数据为空。");
                    auto marker = bytes[0];
                    std::size_t header = marker <= 0x7f                     ? 1
                                         : marker == 0xcc || marker == 0xd0 ? 2
                                         : marker == 0xcd || marker == 0xd1 ? 3
                                         : marker == 0xce || marker == 0xd2 ? 5
                                         : marker == 0xcf || marker == 0xd3 ? 9
                                                                            : 0;
                    Require(header && bytes.size() >= header, "ABMX 解压长度无效。");
                    auto length = MP::Parse(bytes.first(header)).Integer();
                    Require(length > 0 && length <= 4 * 1024 * 1024, "ABMX 解压长度越界。");
                    packed =
                        MP::Parse(DecompressUnityBlock(bytes.subspan(header), static_cast<std::size_t>(length), 2));
                }
                Require(packed->Type == MP::Kind::Array && packed->Children.size() <= 4096, "ABMX 骨骼数据无效。");
                for (auto &item : packed->Children)
                {
                    CheckCancelled(Cancellation);
                    Require(item.Type == MP::Kind::Array && item.Children.size() >= 2 &&
                                item.Children[1].Type == MP::Kind::Array && !item.Children[1].Children.empty(),
                            "ABMX 骨骼参数无效。");
                    auto &fields = item.Children[1].Children[0];
                    Require(fields.Type == MP::Kind::Array && fields.Children.size() >= 4, "ABMX 骨骼参数截断。");
                    BoneAdjustment change;
                    change.Name = item.Children[0].Text();
                    change.Scale = Vector(fields.Children[0]);
                    change.Length = Number(&fields.Children[1]);
                    change.Position = Vector(fields.Children[2]);
                    change.Rotation = Vector(fields.Children[3]);
                    Require(change.Name.size() <= 256 && std::abs(change.Length) <= 100 &&
                                std::abs(change.Scale.x) <= 100 && std::abs(change.Scale.y) <= 100 &&
                                std::abs(change.Scale.z) <= 100 && std::abs(change.Position.x) <= 10000 &&
                                std::abs(change.Position.y) <= 10000 && std::abs(change.Position.z) <= 10000,
                            "ABMX 骨骼参数越界。");
                    Definition.BoneModifiers.push_back(change);
                }
            }

            void Materials(const MP &data)
            {
                for (auto field : {"MaterialColorPropertyList", "MaterialFloatPropertyList", "RendererPropertyList"})
                {
                    auto list = Unpack(data, field);
                    for (auto *row : Items(list))
                    {
                        CheckCancelled(Cancellation);
                        std::string kind = std::string(field) == "MaterialColorPropertyList"   ? "color"
                                           : std::string(field) == "MaterialFloatPropertyList" ? "float"
                                                                                               : "renderer";
                        auto edit = Edit(*row, kind);
                        if (kind == "color")
                        {
                            auto value = Get(*row, "Value");
                            Require(value, "材质颜色缺失。");
                            edit.Property = Property(*row);
                            edit.Values = Flatten(*value);
                            Require(edit.Values.size() >= 3 && edit.Values.size() <= 4, "材质颜色向量无效。");
                            if (edit.Values.size() == 3)
                                edit.Values.push_back(1);
                        }
                        else if (kind == "float")
                        {
                            edit.Property = Property(*row);
                            edit.Number = Number(Get(*row, "Value"));
                        }
                        else
                        {
                            int property = Int(Get(*row, "Property")), value = Int(Get(*row, "Value"));
                            Require(property >= 0 && property <= 2 && value >= 0 && value <= (property == 1 ? 3 : 1),
                                    "材质编辑器渲染器参数无效。");
                            edit.Property = property == 1 ? "shadowCasting" : property == 2 ? "receiveShadows" : "";
                            edit.Number = static_cast<float>(value);
                        }
                        Definition.MaterialEdits.push_back(std::move(edit));
                    }
                }
                auto textureList = Unpack(data, "MaterialTexturePropertyList");
                auto textureRows = Items(textureList);
                std::set<std::int64_t> wanted;
                for (auto *row : textureRows)
                    if (auto id = Get(*row, "TexID"); !Null(id))
                        wanted.insert(id->Integer());
                std::map<std::int64_t, EmbeddedTextureReference> textures;
                if (!wanted.empty())
                    if (auto dictionary = Unpack(data, "TextureDictionary"))
                    {
                        Require(dictionary->Type == MP::Kind::Map, "材质贴图字典无效。");
                        for (std::size_t i = 0; i < dictionary->Children.size(); i += 2)
                        {
                            auto id = dictionary->Children[i].Integer();
                            if (wanted.contains(id))
                                if (auto texture = Texture(&dictionary->Children[i + 1]))
                                    textures[id] = *texture;
                        }
                    }
                for (auto *row : textureRows)
                {
                    auto edit = Edit(*row, "texture");
                    edit.Property = Property(*row);
                    if (auto id = Get(*row, "TexID"); !Null(id))
                    {
                        auto number = id->Integer();
                        auto found = textures.find(number);
                        if (found != textures.end())
                            edit.Texture = found->second;
                        else
                            Warning("MaterialEditor：找不到内嵌贴图 #" + std::to_string(number) + "（" + edit.Name +
                                    " / " + edit.Property + "）。");
                    }
                    for (auto key : {"Offset", "Scale"})
                        if (auto value = Get(*row, key); !Null(value))
                        {
                            auto vector = Flatten(*value);
                            Require(vector.size() == 2, "材质贴图坐标无效。");
                            if (std::string(key) == "Offset")
                                edit.Offset = std::move(vector);
                            else
                                edit.Scale = std::move(vector);
                        }
                    Definition.MaterialEdits.push_back(std::move(edit));
                }
                auto shaders = Unpack(data, "MaterialShaderList");
                for (auto *row : Items(shaders))
                {
                    if (auto queue = Get(*row, "RenderQueue"); !Null(queue))
                    {
                        auto edit = Edit(*row, "queue");
                        edit.Number = static_cast<float>(Int(queue));
                        Definition.MaterialEdits.push_back(std::move(edit));
                    }
                    if (auto name = Text(Get(*row, "ShaderName")); !name.empty())
                    {
                        auto edit = Edit(*row, "shader");
                        edit.Text = name;
                        Definition.MaterialEdits.push_back(std::move(edit));
                    }
                }
                if (auto copies = Unpack(data, "MaterialCopyList");
                    copies && copies->Type == MP::Kind::Array && !copies->Children.empty())
                    Warning("MaterialEditor：复制材质尚未还原。");
            }

            void Skin(const MP &data, std::int64_t version)
            {
                Require(version >= 0 && version <= 2, "KSOX 数据版本不支持。");
                if (version < 2)
                {
                    std::array<const char *, 11> names{"Unknown",   "BodyOver",  "FaceOver", "BodyUnder",
                                                       "FaceUnder", "EyeUnder",  "EyeOver",  "EyeUnderL",
                                                       "EyeOverL",  "EyeUnderR", "EyeOverR"};
                    for (int type = 1; type < static_cast<int>(names.size()); type++)
                        if (auto texture = Texture(Get(data, names[type])))
                            Definition.Overlays.push_back({type, {}, false, *texture});
                    return;
                }
                auto lookup = Unpack(data, "Lookup");
                if (!lookup)
                    return;
                Require(lookup->Type == MP::Kind::Map, "KSOX 覆盖贴图索引无效。");
                auto current = lookup->Find(std::int64_t(0));
                if (!current)
                    current = lookup->Find(std::string("0"));
                if (!current)
                    return;
                Require(current->Type == MP::Kind::Map, "KSOX 当前坐标索引无效。");
                for (std::size_t i = 0; i < current->Children.size(); i += 2)
                {
                    int type = Int(&current->Children[i]);
                    auto id = current->Children[i + 1].Integer();
                    Require(type >= 1 && type <= 10, "KSOX 覆盖贴图类型无效。");
                    auto key = "_TextureID_" + std::to_string(id);
                    if (auto texture = Texture(Get(data, key.c_str())))
                        Definition.Overlays.push_back({type, {}, false, *texture});
                    else
                        Warning("KSOX：找不到内嵌贴图 #" + std::to_string(id) + "。");
                }
            }

            void Clothes(const MP &data)
            {
                auto packed = Unpack(data, "Overlays");
                if (!packed)
                    return;
                Require(packed->Type == MP::Kind::Map, "KCOX 服装坐标索引无效。");
                auto current = packed->Find(std::int64_t(0));
                if (!current)
                    return;
                Require(current->Type == MP::Kind::Map, "KCOX 服装索引无效。");
                for (std::size_t i = 0; i < current->Children.size(); i += 2)
                {
                    auto name = current->Children[i].Text();
                    auto &fields = current->Children[i + 1];
                    Require(fields.Type == MP::Kind::Array && fields.Children.size() >= 2, "KCOX 覆盖参数截断。");
                    auto texture = Texture(&fields.Children[0]);
                    auto replace = std::get_if<bool>(&fields.Children[1].ScalarValue);
                    Require(replace, "KCOX 覆盖模式无效。");
                    if (texture || *replace)
                        Definition.Overlays.push_back({0, name, *replace, texture});
                }
            }

            void Accessories(const MP &data)
            {
                auto text = Text(Get(data, "additionalAccessories"));
                if (text.empty())
                    return;
                Require(text.size() <= 4 * 1024 * 1024, "扩展配饰 XML 长度超限。");
                using Microsoft::WRL::ComPtr;
                ComPtr<IStream> input;
                input.Attach(
                    SHCreateMemStream(reinterpret_cast<const BYTE *>(text.data()), static_cast<UINT>(text.size())));
                Require(input.Get() != nullptr, "配饰 XML 内存不足。");
                ComPtr<IXmlReader> xml;
                auto check = [](HRESULT code)
                {
                    Require(SUCCEEDED(code), "配饰 XML 格式无效或含禁止的 DTD。");
                };
                check(CreateXmlReader(__uuidof(IXmlReader), reinterpret_cast<void **>(xml.GetAddressOf()), nullptr));
                check(xml->SetProperty(XmlReaderProperty_DtdProcessing, DtdProcessing_Prohibit));
                check(xml->SetInput(input.Get()));
                XmlNodeType type;
                HRESULT status;
                int slot = 20, depth = 0;
                std::size_t nodes = 0;
                while ((status = xml->Read(&type)) == S_OK)
                {
                    CheckCancelled(Cancellation);
                    Require(++nodes <= 100000 && depth <= 64, "配饰 XML 复杂度超限。");
                    if (type == XmlNodeType_DocumentType)
                        throw std::runtime_error("配饰 XML 禁止 DTD。");
                    if (type == XmlNodeType_EndElement)
                    {
                        depth--;
                        continue;
                    }
                    if (type != XmlNodeType_Element)
                        continue;
                    const wchar_t *name = nullptr;
                    UINT length = 0;
                    check(xml->GetLocalName(&name, &length));
                    bool accessory = depth == 1 && std::wstring_view(name, length) == L"accessory";
                    bool empty = xml->IsEmptyElement();
                    if (!empty)
                        depth++;
                    if (!accessory)
                        continue;
                    Require(slot < 256, "扩展配饰数量超限。");
                    std::map<std::string, std::string> attributes;
                    if (xml->MoveToFirstAttribute() == S_OK)
                    {
                        do
                        {
                            const wchar_t *key = nullptr, *value = nullptr;
                            UINT ks = 0, vs = 0;
                            check(xml->GetLocalName(&key, &ks));
                            check(xml->GetValue(&value, &vs));
                            attributes[Utf8(fs::path(std::wstring(key, ks)))] = Utf8(fs::path(std::wstring(value, vs)));
                        } while (xml->MoveToNextAttribute() == S_OK);
                        check(xml->MoveToElement());
                    }
                    auto attribute = [&](const std::string &key, std::string fallback = {})
                    {
                        auto found = attributes.find(key);
                        return found == attributes.end() ? fallback : found->second;
                    };
                    auto numeric = [&](const std::string &key, float fallback)
                    {
                        auto found = attributes.find(key);
                        if (found == attributes.end())
                            return fallback;
                        float n;
                        auto [end, error] =
                            std::from_chars(found->second.data(), found->second.data() + found->second.size(), n);
                        return error == std::errc() && end == found->second.data() + found->second.size() &&
                                       std::isfinite(n)
                                   ? n
                                   : fallback;
                    };
                    auto integer = [&](const std::string &key, int fallback)
                    {
                        auto found = attributes.find(key);
                        if (found == attributes.end())
                            return fallback;
                        int n;
                        auto [end, error] =
                            std::from_chars(found->second.data(), found->second.data() + found->second.size(), n);
                        Require(error == std::errc() && end == found->second.data() + found->second.size(),
                                "配饰 XML 整数无效。");
                        return n;
                    };
                    auto flag = [&](const std::string &key, bool fallback)
                    {
                        auto found = attributes.find(key);
                        return found == attributes.end() ? fallback
                                                         : Lower(found->second) == "true" || found->second == "1";
                    };
                    ExtraCharacterAccessory part;
                    part.Slot = slot++;
                    part.Category = integer("type", 350);
                    if (part.Category < 351 || part.Category > 399)
                        continue;
                    part.Id = integer("id", 0);
                    part.Parent = attribute("parentKey");
                    auto binding = CharacterAssetReferences::DescribePath(
                        Document.Sex(), "accessory.parts[" + std::to_string(part.Slot) + "].id", part.Category);
                    for (auto &reference : CharacterAssetReferences::All(Document))
                        if (reference.Category == part.Category &&
                            (binding && Lower(reference.Property) == Lower(binding->Property) ||
                             Lower("." + reference.Property).find(".accessory" + std::to_string(part.Slot) + ".") !=
                                 std::string::npos))
                        {
                            part.Reference = reference;
                            break;
                        }
                    auto &parameters = part.Parameters;
                    Set(parameters, "type", part.Category);
                    Set(parameters, "id", part.Id);
                    Parameter parent;
                    parent.Name = "parentKey";
                    parent.Text = part.Parent;
                    Set(parameters, std::move(parent));
                    Parameter colors;
                    colors.Name = "colorInfo";
                    for (int i = 0; i < 4; i++)
                    {
                        Parameter info;
                        info.Name = std::to_string(i);
                        Parameter color;
                        color.Name = "color";
                        int axis = 0;
                        for (char c : std::string("rgba"))
                        {
                            float n = numeric("color" + std::to_string(i) + c, 1);
                            part.Colors.push_back(n);
                            Set(color, std::to_string(axis++), n);
                        }
                        Set(info, std::move(color));
                        for (auto key : {"glossPower", "metallicPower", "smoothnessPower"})
                            Set(info, key,
                                numeric(std::string(key) + std::to_string(i),
                                        std::string(key) == "metallicPower" ? 0.f : .5f));
                        colors.Children.push_back(std::move(info));
                    }
                    Set(parameters, std::move(colors));
                    Parameter moves;
                    moves.Name = "addMove";
                    for (int vector = 0; vector < 6; vector++)
                    {
                        Parameter v;
                        v.Name = std::to_string(vector);
                        for (int axis = 0; axis < 3; axis++)
                        {
                            auto key =
                                "addMove" + std::to_string(vector / 3) + std::to_string(vector % 3) + "xyz"[axis];
                            float n = numeric(key, vector % 3 == 2 ? 1.f : 0.f);
                            part.Adjustments.push_back(n);
                            Set(v, std::to_string(axis), n);
                        }
                        moves.Children.push_back(std::move(v));
                    }
                    Set(parameters, std::move(moves));
                    for (auto key : {"hideCategory", "hideTiming"})
                        Set(parameters, key, numeric(key, 0));
                    Set(parameters, "noShake", flag("noShake", false) ? 1 : 0);
                    Set(parameters, "show", flag("show", true) ? 1 : 0);
                    State.Accessories.push_back(std::move(part));
                }
                check(status);
                Require(depth == 0, "配饰 XML 结束元素无效。");
            }

            void Controls(const MP &data)
            {
                auto packed = Unpack(data, "accessoryData");
                if (!packed)
                    return;
                Require(packed->Type == MP::Kind::Array && packed->Children.size() <= 256, "配饰联动槽位超限。");
                for (auto &item : packed->Children)
                {
                    Require(item.Type == MP::Kind::Array && item.Children.size() == 8, "配饰联动数据无效。");
                    auto &row = item.Children;
                    if (Flag(&row[0]))
                        continue;
                    int slot = Int(&row[1]);
                    Require(slot >= 0 && slot <= 255, "配饰联动槽位无效。");
                    CharacterAccessoryControl control;
                    control.Name = Text(&row[4]);
                    control.MatchHair = Flag(&row[5]);
                    control.Parent = Text(&row[7]);
                    if (row[6].Type == MP::Kind::Array)
                        for (auto &rule : row[6].Children)
                        {
                            Require(rule.Type == MP::Kind::Array && rule.Children.size() == 2, "配饰联动规则无效。");
                            int type = Int(&rule.Children[0]), modifier = Int(&rule.Children[1]);
                            if ((type >= 1 && type <= 4) ||
                                (type >= 10 && type <= 17 &&
                                 (modifier == 10 || modifier == 11 || modifier == 12 || modifier == 50)) ||
                                ((type == 20 || type == 21) && modifier >= 1 && modifier <= 256))
                                control.Rules.push_back({type, modifier});
                            else
                                Warning("AdditionalAccessoryControls：配饰 " + std::to_string(slot + 1) + " 的规则 " +
                                        std::to_string(type) + "/" + std::to_string(modifier) + " 尚未支持。");
                        }
                    if (!control.Parent.empty())
                        Warning("AdditionalAccessoryControls：配饰 " + std::to_string(slot + 1) +
                                " 的高级连接点尚未还原。");
                    State.AccessoryControls[slot] = std::move(control);
                }
                if (auto overrides = Unpack(data, "overrideData");
                    overrides && overrides->Type == MP::Kind::Array &&
                    std::any_of(overrides->Children.begin(), overrides->Children.end(),
                                [](auto &row)
                                {
                                    return row.Type != MP::Kind::Array || !row.Children.empty();
                                }))
                    Warning("AdditionalAccessoryControls：服装显示规则尚未还原。");
            }
        };

        Parameter PushSettings(const MP *saved, const fs::path &game)
        {
            std::map<std::string, std::string> settings;
            auto config = game / L"BepInEx" / L"config" / L"mikke.pushUpAI.cfg";
            if (fs::exists(config))
            {
                auto bytes = ReadFile(config, 4 * 1024 * 1024);
                std::istringstream lines(std::string(bytes.begin(), bytes.end()));
                std::string section;
                for (std::string line; std::getline(lines, line);)
                {
                    line = Trim(line);
                    if (line.empty() || line.starts_with('#'))
                        continue;
                    if (line.starts_with('['))
                    {
                        auto end = line.find(']');
                        section = end == line.npos ? line.substr(1) : line.substr(1, end - 1);
                        continue;
                    }
                    auto equal = line.find('=');
                    if (section == "Push-Up bra default settings" && equal != line.npos)
                        settings[Trim(line.substr(0, equal))] = Trim(line.substr(equal + 1));
                }
            }
            auto flag = [&](const std::string &key, bool fallback)
            {
                auto found = settings.find(key);
                if (found == settings.end())
                    return fallback;
                auto value = Lower(found->second);
                return value == "true" ? true : value == "false" ? false : fallback;
            };
            auto number = [&](const std::string &key, int fallback)
            {
                auto found = settings.find(key);
                int value = fallback;
                if (found != settings.end())
                {
                    auto [end, error] =
                        std::from_chars(found->second.data(), found->second.data() + found->second.size(), value);
                    if (error != std::errc() || end != found->second.data() + found->second.size())
                        value = fallback;
                }
                return std::clamp(value, 0, 100) / 100.f;
            };
            Parameter result;
            for (auto prefix : {std::string(), std::string("TOP_")})
            {
                bool bra = prefix.empty();
                Parameter garment;
                garment.Name = bra ? "Bra" : "Top";
                auto source = [&](const std::string &key)
                {
                    return saved ? saved->Find(prefix + key) : nullptr;
                };
                auto f = [&](const std::string &key, bool fallback)
                {
                    Set(garment, key, Flag(source(key), fallback) ? 1 : 0);
                };
                auto n = [&](const std::string &key, float fallback)
                {
                    auto value = Number(source(key), fallback);
                    Require(value >= -10 && value <= 10, "PushUp：塑形参数 " + prefix + key + " 越界。");
                    Set(garment, key, value);
                };
                f("ENABLE_PUSHUP", bra && flag("Enable pushUp by default", true));
                n("LIFT", bra ? number("Lift", 60) : 0);
                n("PUSH_TOGETHER", bra ? number("Push Together", 65) : 0);
                n("SQUEEZE", bra ? number("Squeeze", 60) : 0);
                n("CENTER_NIPPLES", bra ? number("Nipple Centering", 100) : 0);
                f("FLATTEN_NIPPLES", bra && flag("Flatten Nipples", true));
                f("HIDE_NIPPLES", bra && flag("Hide Nipples", false));
                f("HIDE_ACCESSORIES", false);
                n("CORSET", 0);
                n("FIRMNESS", bra ? number("Firmness", 90) : 0);
                result.Children.push_back(std::move(garment));
            }
            return result;
        }

        bool PushInstalled(const fs::path &data, const fs::path &game, std::vector<std::string> &warnings,
                           std::stop_token token)
        {
            try
            {
                CardRepository repository(data);
                if (auto cached = repository.LoadMetadata("mods-v1"))
                {
                    auto index = cached->get<ModIndex>();
                    if (SamePath(Path(index.GamePath), game))
                    {
                        auto found = index.Plugins.find("mikke.pushUpAI");
                        if (found == index.Plugins.end())
                            return false;
                        auto plugin = std::find_if(index.PluginFiles.begin(), index.PluginFiles.end(),
                                                   [&](auto &file)
                                                   {
                                                       return std::any_of(file.Plugins.begin(), file.Plugins.end(),
                                                                          [](auto &plugin)
                                                                          {
                                                                              return plugin.Guid == "mikke.pushUpAI";
                                                                          });
                                                   });
                        if (plugin != index.PluginFiles.end() && fs::exists(Path(plugin->Path)) &&
                            static_cast<std::int64_t>(fs::file_size(Path(plugin->Path))) == plugin->Size &&
                            ModifiedTicks(Path(plugin->Path)) == plugin->ModifiedUtcTicks)
                            return true;
                    }
                }
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &)
            {
            }
            auto root = game / L"BepInEx" / L"plugins";
            if (!fs::is_directory(root))
                return false;
            auto files = PluginFileIndex::Scan(root, warnings, token);
            for (auto &file : files)
                for (auto &plugin : file.Plugins)
                    if (plugin.Guid == "mikke.pushUpAI")
                        return true;
            return false;
        }
    }

    CharacterExtensionState ReadCharacterExtensions(const CharacterDocument &document, CharacterDefinition &definition,
                                                    const fs::path &dataCache, const fs::path &game,
                                                    std::stop_token token)
    {
        Reader reader{definition, {}, document, dataCache / L"NativeCpp" / L"embedded", token};
        std::optional<MP> pushSaved;
        Set(definition.Face, "disableBlinking", 0);
        if (document.Extensions())
        {
            auto &extensions = *document.Extensions();
            Require(extensions.Type == MP::Kind::Map, "人物扩展数据格式无效。");
            for (std::size_t i = 0; i < extensions.Children.size(); i += 2)
            {
                CheckCancelled(token);
                auto id = extensions.Children[i].Text();
                if (id == "com.bepis.sideloader.universalautoresolver" ||
                    id == "EC.Core.Sideloader.UniversalAutoResolver")
                    continue;
                auto &plugin = extensions.Children[i + 1];
                try
                {
                    Require(plugin.Type == MP::Kind::Array && plugin.Children.size() >= 2 &&
                                plugin.Children[1].Type == MP::Kind::Map,
                            "扩展数据参数格式无效。");
                    auto version = plugin.Children[0].Integer();
                    auto &data = plugin.Children[1];
                    if (id == "moreAccessories")
                    {
                        reader.Accessories(data);
                        continue;
                    }
                    if (id == "KKABMPlugin.ABMData")
                    {
                        Require(version == 2, "ABMX 数据版本不支持。");
                        reader.Bones(data);
                        continue;
                    }
                    if (id == "com.deathweasel.bepinex.materialeditor")
                    {
                        Require(version >= 0 && version <= 1, "MaterialEditor 数据版本不支持。");
                        reader.Materials(data);
                        continue;
                    }
                    if (id == "KSOX")
                    {
                        reader.Skin(data, version);
                        continue;
                    }
                    if (id == "KCOX")
                    {
                        Require(version == 1, "KCOX 数据版本不支持。");
                        reader.Clothes(data);
                        continue;
                    }
                    if (id == "com.deathweasel.bepinex.uncensorselector")
                    {
                        Require(version >= 1 && version <= 2, "UncensorSelector 数据版本不支持。");
                        for (auto key : {"UncensorGUID", "BodyGUID", "PenisGUID", "BallsGUID", "DisplayBalls"})
                            if (auto value = Get(data, key))
                                reader.State.Uncensor.Children.push_back(ParameterOf(*value, key));
                        continue;
                    }
                    static const std::set<std::string> known{"KK_InvisibleBody",
                                                             "TestCharaCustomFunctionController",
                                                             "com.fairbair.hs2_boobsettings",
                                                             "com.deathweasel.bepinex.eyecontrol",
                                                             "KK_PregnancyPlus",
                                                             "mikke.pushUpAI",
                                                             "orange.spork.additionalaccessorycontrolsplugin",
                                                             "orange.spork.advikplugin",
                                                             "orange.spork.outfitpainter",
                                                             "marco.authordata",
                                                             "orange.spork.earwiggle"};
                    if (!known.contains(id))
                    {
                        reader.Warning("尚未适配的扩展数据：" + id);
                        continue;
                    }
                    Require(version == (id == "mikke.pushUpAI"                                   ? 3
                                        : (id == "KK_PregnancyPlus" || id == "marco.authordata") ? 1
                                                                                                 : 0),
                            "扩展数据版本尚未支持。");
                    auto unknownField = [&](const std::set<std::string> &keys)
                    {
                        for (std::size_t n = 0; n < data.Children.size(); n += 2)
                            if (!keys.contains(data.Children[n].Text()))
                                return true;
                        return false;
                    };
                    if (id == "KK_InvisibleBody")
                    {
                        auto value = Get(data, "Visible");
                        Require(Null(value) || std::holds_alternative<bool>(value->ScalarValue), "身体显示状态无效。");
                        definition.BodyVisible = Flag(value, true);
                    }
                    else if (id == "com.deathweasel.bepinex.eyecontrol")
                    {
                        definition.EyeOpenMax = Number(Get(data, "EyeOpenMax"), 1);
                        Require(definition.EyeOpenMax >= 0 && definition.EyeOpenMax <= 1, "眼睑开合值无效。");
                        Set(definition.Face, "disableBlinking", Flag(Get(data, "DisableBlinking")) ? 1 : 0);
                    }
                    else if (id == "mikke.pushUpAI")
                        pushSaved = data;
                    else if (id == "marco.authordata")
                    {
                        if (unknownField({"Authors"}))
                            reader.Warning("AuthorData：存在未识别的元数据字段。");
                    }
                    else if (id == "TestCharaCustomFunctionController")
                    {
                        if (unknownField({"id"}))
                            reader.Warning(id + "：存在未识别参数。");
                    }
                    else if (id == "KK_PregnancyPlus")
                    {
                        if (unknownField({"pluginVersion", "clothingOffsetVersion"}))
                            reader.Warning("PregnancyPlus：卡片含腹部形变参数，尚未还原。");
                    }
                    else if (id == "orange.spork.earwiggle")
                    {
                        if (Flag(Get(data, "EarWiggleEnabled")))
                            reader.Warning("EarWiggle：已启用的耳部动态物理尚未还原。");
                    }
                    else if (id == "com.fairbair.hs2_boobsettings")
                    {
                        for (auto key :
                             {"overridePhysics", "overrideGravity", "butt_overridePhysics", "butt_overrideGravity"})
                            if (Flag(Get(data, key)))
                            {
                                reader.Warning("BoobSettings：已启用的自定义胸部/臀部动态物理尚未还原。");
                                break;
                            }
                    }
                    else if (id == "orange.spork.advikplugin")
                    {
                        bool active = Number(Get(data, "SpineStiffness")) != 0;
                        for (auto key : {"ShoulderRotatorEnabled", "EnableSpineFKHints", "EnableShoulderFKHints",
                                         "EnableToeFKHints", "EnableHeelzHoverAll", "EnableHeelzHoverLeftFoot",
                                         "EnableHeelzHoverRightFoot", "BreathingEnabled"})
                            active |= Flag(Get(data, key));
                        if (active)
                            reader.Warning("AdvIK：已启用的附加姿势/呼吸控制尚未还原。");
                    }
                    else if (id == "orange.spork.additionalaccessorycontrolsplugin")
                        reader.Controls(data);
                    else if (id == "orange.spork.outfitpainter")
                    {
                        bool valid = false;
                        if (auto packed = Unpack(data, "OutfitPainterData");
                            packed && packed->Type == MP::Kind::Array && packed->Children.size() == 1 &&
                            packed->Children[0].Type == MP::Kind::Array)
                        {
                            valid = true;
                            for (auto &row : packed->Children[0].Children)
                            {
                                if (row.Type != MP::Kind::Array || row.Children.size() != 6 ||
                                    row.Children[5].Type != MP::Kind::Array)
                                {
                                    valid = false;
                                    break;
                                }
                                for (auto &binding : row.Children[5].Children)
                                    if (binding.Type != MP::Kind::Array || binding.Children.size() != 4 ||
                                        Integer(&binding.Children[0]) < 0 || Integer(&binding.Children[0]) > 8 ||
                                        Integer(&binding.Children[2]) < 1 || Integer(&binding.Children[2]) > 4)
                                    {
                                        valid = false;
                                        break;
                                    }
                            }
                        }
                        if (!valid && !Null(Get(data, "OutfitPainterData")))
                            reader.Warning("OutfitPainter：调色绑定数据格式尚未支持。");
                    }
                }
                catch (const Cancelled &)
                {
                    throw;
                }
                catch (const std::exception &error)
                {
                    reader.Warning(id + "：" + error.what());
                }
            }
        }
        if (std::any_of(definition.Overlays.begin(), definition.Overlays.end(),
                        [](auto &overlay)
                        {
                            return overlay.Type > 4;
                        }))
            reader.Warning("KSOX：眼部覆盖层尚未完成还原。");
        if (definition.Sex != 0)
        {
            try
            {
                if (PushInstalled(dataCache, game, definition.Diagnostics, token))
                    reader.State.PushUp = PushSettings(pushSaved ? &*pushSaved : nullptr, game);
                else if (pushSaved &&
                         (Flag(Get(*pushSaved, "ENABLE_PUSHUP")) || Flag(Get(*pushSaved, "TOP_ENABLE_PUSHUP"))))
                    reader.Warning("PushUp：卡片保存了参数，但本机没有安装该插件；与游戏一致，不应用这些参数。");
            }
            catch (const Cancelled &)
            {
                throw;
            }
            catch (const std::exception &error)
            {
                reader.Warning("PushUp：" + std::string(error.what()));
            }
        }
        return std::move(reader.State);
    }
}
