#include "Assets/MonoSchema.h"
#include "Serialization/CliMetadata.h"
#include <algorithm>
#include <map>
#include <mutex>
#include <set>

namespace hs2
{
    namespace
    {
        using Assembly = metadata::Assembly;
        using Cursor = metadata::Cursor;

        void Require(bool condition, const std::string &error)
        {
            if (!condition)
                throw std::runtime_error(error);
        }

        std::string Lower(std::string name)
        {
            for (auto &c : name)
                if (c >= 'A' && c <= 'Z')
                    c = char(c - 'A' + 'a');
            return name;
        }

        struct TypeRef
        {
            std::shared_ptr<Assembly> Module;
            std::uint32_t Row = 0;
            int Primitive = 0;
            std::vector<TypeRef> Arguments;
            std::shared_ptr<TypeRef> Element;
            bool Array = false;
        };

        struct SchemaNode
        {
            std::string Type, Name;
            int Size = -1;
            bool Align = false;
            std::vector<SchemaNode> Children;
        };

        SchemaNode Scalar(std::string type, std::string name, int size, bool align = false)
        {
            return {std::move(type), std::move(name), size, align, {}};
        }

        std::string Name(const TypeRef &type)
        {
            return type.Module && type.Row ? type.Module->TypeName(2, type.Row)
                   : type.Array            ? "Array"
                   : type.Primitive == 14  ? "System.String"
                                           : "";
        }

        std::string ShortName(const TypeRef &type)
        {
            auto name = Name(type);
            auto dot = name.find_last_of('.');
            return dot == name.npos ? name : name.substr(dot + 1);
        }

        std::uint32_t Flags(const TypeRef &type)
        {
            return type.Module->Row(2, type.Row).Read<std::uint32_t>();
        }

        SchemaNode Pointer(const std::string &name, const std::string &type)
        {
            return {"PPtr<$" + type + ">",
                    name,
                    -1,
                    false,
                    {Scalar("int", "m_FileID", 4), Scalar("SInt64", "m_PathID", 8)}};
        }

        SchemaNode Vector(SchemaNode element, bool collectionType)
        {
            auto name = element.Name, type = element.Type;
            element.Name = "data";
            element.Align = false;
            return {collectionType ? type : "vector",
                    name,
                    -1,
                    false,
                    {{"Array", "Array", -1, true, {Scalar("int", "size", 4), std::move(element)}}}};
        }
    }

    struct NativeMonoSchema::Impl
    {
        fs::path Directory;
        std::shared_ptr<const UnityClassDatabase> Database;
        std::map<std::string, std::shared_ptr<Assembly>> Modules;
        std::map<std::pair<const Assembly *, std::uint32_t>, bool> SerializedFields;
        std::map<std::string, std::vector<UnityTypeNode>> Cache;
        std::mutex Gate;

        Impl(fs::path directory, std::shared_ptr<const UnityClassDatabase> database)
            : Directory(fs::absolute(directory)), Database(std::move(database))
        {
        }

        std::shared_ptr<Assembly> Load(std::string assembly)
        {
            if (!Lower(assembly).ends_with(".dll"))
                assembly += ".dll";
            Require(Path(assembly) == Path(assembly).filename(), "Script assembly path is not a file name.");
            auto key = Lower(assembly);
            if (auto found = Modules.find(key); found != Modules.end())
                return found->second;
            auto value = std::make_shared<Assembly>(Directory / Path(assembly));
            Require(value->Managed, "Script assembly has no CLI metadata.");
            Modules[key] = value;
            return value;
        }

        std::string AssemblyReference(const Assembly &module, std::uint32_t row)
        {
            auto source = module.Row(35, row);
            source.Skip(12 + module.BlobSize);
            return module.Text(source.Index(module.StringSize));
        }

        TypeRef Find(std::shared_ptr<Assembly> module, const std::string &name, int depth = 0)
        {
            Require(depth < 64, "Script forwarded type chain is too deep.");
            for (std::uint32_t i = 1; i <= module->Rows[2]; i++)
                if (module->TypeName(2, i) == name)
                    return {module, i};
            for (std::uint32_t i = 1; i <= module->Rows[39]; i++)
            {
                auto row = module->Row(39, i);
                row.Skip(8);
                auto typeName = module->Text(row.Index(module->StringSize)),
                     space = module->Text(row.Index(module->StringSize));
                if ((space.empty() ? typeName : space + "." + typeName) != name)
                    continue;
                auto implementation = row.Index(module->Coded(2, {38, 35, 39}));
                if ((implementation & 3) == 1)
                    return Find(Load(AssemblyReference(*module, implementation >> 2)), name, depth + 1);
            }
            throw std::runtime_error("Script metadata type was not found: " + name);
        }

        TypeRef Nested(TypeRef parent, const std::string &child)
        {
            for (std::uint32_t i = 1; i <= parent.Module->Rows[41]; i++)
            {
                auto row = parent.Module->Row(41, i);
                auto nested = row.Index(parent.Module->IndexSize(2)),
                     enclosing = row.Index(parent.Module->IndexSize(2));
                if (enclosing == parent.Row)
                {
                    auto source = parent.Module->Row(2, nested);
                    source.Skip(4);
                    if (parent.Module->Text(source.Index(parent.Module->StringSize)) == child)
                        return {parent.Module, nested};
                }
            }
            throw std::runtime_error("Nested script type was not found: " + child);
        }

        TypeRef Token(std::shared_ptr<Assembly> module, std::uint32_t token, const std::vector<TypeRef> &arguments = {},
                      int depth = 0)
        {
            Require(depth < 64, "Script type signature nesting exceeds limit.");
            auto row = token >> 2;
            switch (token & 3)
            {
            case 0:
                Require(row && row <= module->Rows[2], "Script TypeDef is invalid.");
                return {module, row};
            case 1:
            {
                auto source = module->Row(1, row);
                auto scope = source.Index(module->Coded(2, {0, 26, 35, 1}));
                auto name = module->Text(source.Index(module->StringSize)),
                     space = module->Text(source.Index(module->StringSize));
                if ((scope & 3) == 3)
                    return Nested(Token(module, ((scope >> 2) << 2) | 1, arguments, depth + 1), name);
                auto target = (scope & 3) == 2 ? Load(AssemblyReference(*module, scope >> 2)) : module;
                return Find(target, space.empty() ? name : space + "." + name);
            }
            case 2:
            {
                auto source = module->Row(27, row);
                Cursor signature(module->Blob(source.Index(module->BlobSize)));
                return Signature(module, signature, arguments, depth + 1);
            }
            default:
                throw std::runtime_error("Script TypeDefOrRef coded index is invalid.");
            }
        }

        TypeRef Signature(std::shared_ptr<Assembly> module, Cursor &source, const std::vector<TypeRef> &arguments,
                          int depth = 0)
        {
            Require(depth < 64, "Script type signature is too deep.");
            int kind = source.Read<std::uint8_t>();
            if (kind >= 2 && kind <= 14)
                return {{}, 0, kind};
            if (kind == 0x11 || kind == 0x12)
                return Token(module, source.Compressed(), arguments, depth + 1);
            if (kind == 0x13)
            {
                auto index = source.Compressed();
                Require(index < arguments.size(), "Unbound generic field parameter.");
                return arguments[index];
            }
            if (kind == 0x1d)
            {
                TypeRef type;
                type.Array = true;
                type.Element = std::make_shared<TypeRef>(Signature(module, source, arguments, depth + 1));
                return type;
            }
            if (kind == 0x15)
            {
                auto baseKind = source.Read<std::uint8_t>();
                Require(baseKind == 0x11 || baseKind == 0x12, "Invalid generic field type.");
                auto type = Token(module, source.Compressed(), arguments, depth + 1);
                auto count = source.Compressed();
                Require(count <= 64, "Generic field arity exceeds limit.");
                for (std::uint32_t i = 0; i < count; i++)
                    type.Arguments.push_back(Signature(module, source, arguments, depth + 1));
                return type;
            }
            if (kind == 0x1c)
                return {{}, 0, kind};
            if (kind == 0x1f || kind == 0x20)
            {
                source.Compressed();
                return Signature(module, source, arguments, depth + 1);
            }
            if (kind == 0x0f || kind == 0x10)
            {
                Signature(module, source, arguments, depth + 1);
                return {{}, 0, kind};
            }
            throw std::runtime_error("Unsupported script field signature kind " + std::to_string(kind));
        }

        TypeRef Base(const TypeRef &type)
        {
            if (!type.Module || !type.Row)
                return {};
            auto source = type.Module->Row(2, type.Row);
            source.Skip(4 + type.Module->StringSize * 2);
            auto token = source.Index(type.Module->Coded(2, {2, 1, 27}));
            return token ? Token(type.Module, token, type.Arguments) : TypeRef{};
        }

        bool Object(TypeRef type)
        {
            std::set<std::pair<const Assembly *, std::uint32_t>> seen;
            while (type.Module && type.Row)
            {
                if (Name(type) == "UnityEngine.Object")
                    return true;
                if (!seen.emplace(type.Module.get(), type.Row).second)
                    throw std::runtime_error("Cyclic script type inheritance.");
                if (Flags(type) & 0x20)
                    return false;
                type = Base(type);
            }
            return false;
        }

        bool Enum(const TypeRef &type)
        {
            return type.Module && Name(Base(type)) == "System.Enum";
        }

        bool ValueType(const TypeRef &type)
        {
            auto name = Name(Base(type));
            return name == "System.ValueType" || name == "System.Enum";
        }

        bool HasGenericParameters(const TypeRef &type)
        {
            for (std::uint32_t i = 1; i <= type.Module->Rows[42]; i++)
            {
                auto row = type.Module->Row(42, i);
                row.Skip(4);
                auto owner = row.Index(type.Module->Coded(1, {2, 6}));
                if (!(owner & 1) && (owner >> 1) == type.Row)
                    return true;
            }
            return false;
        }

        bool SerializeField(const TypeRef &type, std::uint32_t field)
        {
            auto key = std::pair(type.Module.get(), field);
            if (auto found = SerializedFields.find(key); found != SerializedFields.end())
                return found->second;
            auto &module = *type.Module;
            bool found = false;
            for (std::uint32_t i = 1; i <= module.Rows[12]; i++)
            {
                auto row = module.Row(12, i);
                auto parent = row.Index(
                    module.Coded(5, {6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43}));
                auto constructor = row.Index(module.Coded(3, {6, 10}));
                if ((parent & 31) == 1 && (parent >> 5) == field)
                {
                    auto attribute = module.AttributeType(constructor).first;
                    if (attribute == "UnityEngine.SerializeField")
                        found = true;
                    if (attribute == "UnityEngine.SerializeReference")
                        throw std::runtime_error(
                            "Managed-reference serialization is unavailable in the supported Unity 2018 schema.");
                }
            }
            SerializedFields[key] = found;
            return found;
        }

        static bool Special(const TypeRef &type)
        {
            static const std::set<std::string> names{"UnityEngine.Vector2",
                                                     "UnityEngine.Vector3",
                                                     "UnityEngine.Vector4",
                                                     "UnityEngine.Vector2Int",
                                                     "UnityEngine.Vector3Int",
                                                     "UnityEngine.Quaternion",
                                                     "UnityEngine.Color",
                                                     "UnityEngine.Color32",
                                                     "UnityEngine.Matrix4x4",
                                                     "UnityEngine.Bounds",
                                                     "UnityEngine.BoundsInt",
                                                     "UnityEngine.Rect",
                                                     "UnityEngine.RectOffset",
                                                     "UnityEngine.LayerMask",
                                                     "UnityEngine.Gradient",
                                                     "UnityEngine.AnimationCurve",
                                                     "UnityEngine.GUIStyle",
                                                     "UnityEngine.PropertyName",
                                                     "UnityEngine.Rendering.SphericalHarmonicsL2"};
            return names.contains(Name(type));
        }

        std::vector<SchemaNode> Fields(const TypeRef &type, int depth, int inheritance = 0)
        {
            Require(inheritance < 64, "Script inheritance depth exceeds limit.");
            std::vector<SchemaNode> fields;
            auto base = Base(type);
            auto baseName = Name(base);
            if (base.Module && baseName != "System.Object" && baseName != "System.ValueType" &&
                baseName != "System.Enum" && baseName != "UnityEngine.Object" &&
                baseName != "UnityEngine.MonoBehaviour" && baseName != "UnityEngine.ScriptableObject")
            {
                auto inherited = Fields(base, depth, inheritance + 1);
                fields.insert(fields.end(), std::make_move_iterator(inherited.begin()),
                              std::make_move_iterator(inherited.end()));
            }
            auto &module = *type.Module;
            auto first = module.Lists(type.Row).first,
                 last = type.Row < module.Rows[2] ? module.Lists(type.Row + 1).first : module.Rows[4] + 1;
            for (auto index = first; index < last; index++)
            {
                auto row = module.Row(4, index);
                auto flags = row.Read<std::uint16_t>();
                auto name = module.Text(row.Index(module.StringSize));
                auto bytes = module.Blob(row.Index(module.BlobSize));
                if ((flags & 7) != 6 && !SerializeField(type, index))
                    continue;
                if (flags & (0x10 | 0x20 | 0x40 | 0x80))
                    continue;
                Cursor signature(bytes);
                Require(signature.Read<std::uint8_t>() == 6, "Invalid CLI field signature prefix.");
                auto fieldType = Signature(type.Module, signature, type.Arguments);
                bool collection = fieldType.Array || (Name(fieldType) == "System.Collections.Generic.List`1" &&
                                                      fieldType.Arguments.size() == 1);
                TypeRef element = fieldType;
                if (collection)
                {
                    if (depth < 0)
                        continue;
                    element = fieldType.Array ? *fieldType.Element : fieldType.Arguments[0];
                    if (element.Array || Name(element) == "System.Collections.Generic.List`1")
                        continue;
                }
                else if (element.Module == type.Module && element.Row == type.Row && !Object(type))
                    continue;
                bool primitive = element.Primitive >= 2 && element.Primitive <= 14;
                if (!primitive)
                {
                    if (!element.Module || !element.Row)
                        continue;
                    bool object = Object(element), special = Special(element), enumeration = Enum(element);
                    if (HasGenericParameters(element))
                        continue;
                    if (enumeration)
                    {
                        auto baseFields = Fields(element, depth);
                        if (baseFields.empty() || baseFields[0].Size == 8)
                            continue;
                    }
                    else if (depth < 0)
                    {
                        if (!ValueType(element) || (!(Flags(element) & 0x2000) && !special))
                            continue;
                    }
                    else if (!object && !special)
                    {
                        if ((Flags(element) & 0x80) || !(Flags(element) & 0x2000) ||
                            Name(element).starts_with("System."))
                            continue;
                    }
                }
                auto node = Node(element, name, depth - 1);
                if (collection)
                    node = Vector(std::move(node), !primitive && !Enum(element) && !Object(element));
                fields.push_back(std::move(node));
            }
            return fields;
        }

        SchemaNode Node(const TypeRef &type, const std::string &name, int depth)
        {
            static const std::map<int, std::pair<const char *, int>> primitive{
                {2, {"UInt8", 1}},   {3, {"UInt16", 2}},  {4, {"SInt8", 1}},  {5, {"UInt8", 1}},
                {6, {"SInt16", 2}},  {7, {"UInt16", 2}},  {8, {"int", 4}},    {9, {"unsigned int", 4}},
                {10, {"SInt64", 8}}, {11, {"UInt64", 8}}, {12, {"float", 4}}, {13, {"double", 8}}};
            if (auto found = primitive.find(type.Primitive); found != primitive.end())
                return Scalar(found->second.first, name, found->second.second, found->second.second < 4);
            if (type.Primitive == 14)
                return {"string",
                        name,
                        -1,
                        true,
                        {{"Array", "Array", -1, false, {Scalar("int", "size", 4), Scalar("char", "data", 1)}}}};
            if (Enum(type))
            {
                auto fields = Fields(type, depth);
                Require(!fields.empty(), "Enum has no underlying field.");
                auto result = fields.front();
                result.Name = name;
                return result;
            }
            if (Object(type))
                return Pointer(name, ShortName(type));
            auto full = Name(type);
            SchemaNode result{ShortName(type), name, -1, false, {}};
            auto floats = [&](std::initializer_list<const char *> names)
            {
                for (auto component : names)
                    result.Children.push_back(Scalar("float", component, 4));
            };
            if (full == "UnityEngine.Vector2")
                floats({"x", "y"});
            else if (full == "UnityEngine.Vector3")
                floats({"x", "y", "z"});
            else if (full == "UnityEngine.Vector4" || full == "UnityEngine.Quaternion")
                floats({"x", "y", "z", "w"});
            else if (full == "UnityEngine.Color")
                floats({"r", "g", "b", "a"});
            else if (full == "UnityEngine.Color32")
                result.Children = {Scalar("unsigned int", "rgba", 4)};
            else if (full == "UnityEngine.Rect")
                floats({"x", "y", "width", "height"});
            else if (full == "UnityEngine.LayerMask")
                result.Children = {Scalar("unsigned int", "m_Bits", 4)};
            else if (full == "UnityEngine.RectOffset")
            {
                for (auto key : {"left", "right", "top", "bottom"})
                    result.Children.push_back(Scalar("int", key, 4));
            }
            else if (full == "UnityEngine.Vector2Int" || full == "UnityEngine.Vector3Int")
            {
                for (auto key : {"x", "y", "z"})
                {
                    result.Children.push_back(Scalar("int", key, 4));
                    if (full == "UnityEngine.Vector2Int" && std::string(key) == "y")
                        break;
                }
            }
            else if (full == "UnityEngine.Matrix4x4")
            {
                for (int i = 0; i < 16; i++)
                    result.Children.push_back(Scalar("float", "e" + std::to_string(i), 4));
            }
            else if (full == "UnityEngine.Bounds" || full == "UnityEngine.BoundsInt")
            {
                auto vector = [&](std::string n)
                {
                    SchemaNode v{full == "UnityEngine.Bounds" ? "Vector3f" : "Vector3Int", n, -1, false, {}};
                    for (auto key : {"x", "y", "z"})
                        v.Children.push_back(Scalar(full == "UnityEngine.Bounds" ? "float" : "int", key, 4));
                    return v;
                };
                result.Children = {vector("m_Center"), vector("m_Extent")};
            }
            else if (full == "UnityEngine.AnimationCurve")
            {
                SchemaNode key{"Keyframe", "data", -1, false, {}};
                for (auto f : {"time", "value", "inSlope", "outSlope"})
                    key.Children.push_back(Scalar("float", f, 4));
                key.Children.push_back(Scalar("int", "weightedMode", 4));
                key.Children.push_back(Scalar("float", "inWeight", 4));
                key.Children.push_back(Scalar("float", "outWeight", 4));
                key.Name = "m_Curve";
                result.Children.push_back(Vector(std::move(key), false));
                result.Children.push_back(Scalar("int", "m_PreInfinity", 4));
                result.Children.push_back(Scalar("int", "m_PostInfinity", 4));
                result.Children.push_back(Scalar("int", "m_RotationOrder", 4));
            }
            else if (full == "UnityEngine.Gradient")
            {
                for (int i = 0; i < 8; i++)
                {
                    SchemaNode color{"ColorRGBA", "key" + std::to_string(i), -1, false, {}};
                    for (auto component : {"r", "g", "b", "a"})
                        color.Children.push_back(Scalar("float", component, 4));
                    result.Children.push_back(std::move(color));
                }
                for (auto prefix : {"ctime", "atime"})
                    for (int i = 0; i < 8; i++)
                        result.Children.push_back(Scalar("UInt16", std::string(prefix) + std::to_string(i), 2));
                result.Children.push_back(Scalar("int", "m_Mode", 4));
                result.Children.push_back(Scalar("UInt8", "m_NumColorKeys", 1));
                result.Children.push_back(Scalar("UInt8", "m_NumAlphaKeys", 1));
                result.Align = true;
            }
            else
                result.Children = Fields(type, depth);
            return result;
        }

        std::vector<UnityTypeNode> Build(std::string assembly, const std::string &space, std::string className,
                                         std::string_view version)
        {
            Require(version.starts_with("2018."), "Native script schema supports the installed Unity 2018 format.");
            auto module = Load(std::move(assembly));
            auto slash = className.find('/');
            auto type = Find(module, (space.empty() ? "" : space + ".") + className.substr(0, slash));
            while (slash != className.npos)
            {
                auto next = className.find('/', slash + 1);
                type = Nested(type, className.substr(slash + 1, next == className.npos ? next : next - slash - 1));
                slash = next;
            }
            auto output = Database->Type(114, version);
            Require(!output.empty(), "MonoBehaviour header schema is missing.");
            std::function<void(const SchemaNode &, int)> append = [&](const SchemaNode &node, int level)
            {
                Require(level < 128 && output.size() < 100000, "Generated script schema exceeds bound.");
                output.push_back(
                    {node.Type, node.Name, node.Size, node.Align ? 0x4000u : 0u, static_cast<std::uint8_t>(level)});
                for (auto &child : node.Children)
                    append(child, level + 1);
            };
            for (auto &node : Fields(type, 7))
                append(node, 1);
            return output;
        }
    };

    NativeMonoSchema::NativeMonoSchema(fs::path directory, std::shared_ptr<const UnityClassDatabase> database)
        : impl_(std::make_unique<Impl>(std::move(directory), std::move(database)))
    {
    }

    NativeMonoSchema::~NativeMonoSchema() = default;

    std::vector<UnityTypeNode> NativeMonoSchema::Type(std::string assembly, std::string space, std::string name,
                                                      std::string_view version)
    {
        std::lock_guard lock(impl_->Gate);
        auto key = assembly + "|" + space + "|" + name + "|" + std::string(version);
        if (auto found = impl_->Cache.find(key); found != impl_->Cache.end())
            return found->second;
        auto nodes = impl_->Build(std::move(assembly), space, std::move(name), version);
        impl_->Cache[key] = nodes;
        return nodes;
    }
}
