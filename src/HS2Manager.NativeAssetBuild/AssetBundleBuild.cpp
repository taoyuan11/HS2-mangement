#include "AssetBundleBuild.h"
#include "AssetParameters.h"
#include "Assets/MonoSchema.h"
#include "Archive/ZipArchive.h"
#include "Animation/SimulationMath.h"
#include <windows.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <map>
#include <set>
#include <sstream>

namespace hs2::asset
{
    using namespace sim;
    using namespace fields;

    namespace
    {
        GpuAssetPointer PointerAt(const fs::path &bundle, const Parameter &value)
        {
            return {bundle, static_cast<std::int32_t>(value.Int("m_FileID")), value.Int("m_PathID")};
        }

        std::string ComponentName(NativeGpuAssetStore &store, const fs::path &bundle, const Parameter &component)
        {
            auto script = store.Metadata(PointerAt(bundle, component.Get("m_Script")));
            return script.String("m_ClassName");
        }

        std::string TypeKey(const UnitySerializedType &type)
        {
            std::string value = std::to_string(type.ClassId);
            for (const auto &node : type.Nodes)
                value += "|" + node.Type + ":" + node.Name + ":" + std::to_string(node.Level) + ":" +
                         std::to_string(node.Flags) + ":" + std::to_string(node.Size) + ":" +
                         std::to_string(node.Version);
            Bytes bytes(value.begin(), value.end());
            bytes.insert(bytes.end(), type.TypeHash.begin(), type.TypeHash.end());
            bytes.insert(bytes.end(), type.ScriptId.begin(), type.ScriptId.end());
            return Sha256(bytes);
        }

        struct Graph
        {
            struct Object
            {
                std::int32_t Type = 0;
                Parameter Value;
            };

            std::map<std::int64_t, Object> Objects;
            std::vector<UnitySerializedType> Types;
            std::map<std::string, std::int32_t> TypeIds;
            std::map<std::string, std::int64_t> Copies;
            std::map<int, UnitySerializedType> Defaults;
            std::set<std::string> Dependencies;
            std::vector<UnityExternalReference> Externals;
            std::shared_ptr<NativeGpuAssetStore> Store;
            std::shared_ptr<const UnityClassDatabase> Database;
            std::unique_ptr<NativeMonoSchema> Schemas;
            std::string Version;
            std::stop_token Token;
            std::int64_t Next = 10;

            Graph(const MaterialTemplate &source, const fs::path &game,
                  std::shared_ptr<const UnityClassDatabase> database, std::stop_token token)
                : Store(source.Assets), Database(std::move(database)),
                  Schemas(std::make_unique<NativeMonoSchema>(game / "HoneySelect2_Data/Managed", Database)),
                  Version(source.Assets->Reader(source.Prefab.Bundle)->UnityVersion()), Token(token)
            {
                auto reader = Store->Reader(source.Prefab.Bundle);
                Dependencies.insert(Utf8(source.Prefab.Bundle));
                for (const auto &object : reader->AssetIndex())
                    if (object.ClassId != 114)
                        Defaults.try_emplace(object.ClassId, reader->TypeOf(object.PathId));
            }

            std::int32_t Type(UnitySerializedType type)
            {
                type.Layout.clear();
                auto key = TypeKey(type);
                if (auto it = TypeIds.find(key); it != TypeIds.end())
                    return it->second;
                auto index = static_cast<std::int32_t>(Types.size());
                Types.push_back(std::move(type));
                TypeIds.emplace(std::move(key), index);
                return index;
            }

            UnitySerializedType Standard(int id)
            {
                if (auto it = Defaults.find(id); it != Defaults.end())
                    return it->second;
                UnitySerializedType type;
                type.ClassId = id;
                type.Nodes = Database->Type(id, Version);
                return type;
            }

            std::int64_t Add(UnitySerializedType type, Parameter value)
            {
                CheckCancelled(Token);
                Require(Objects.size() < 100000, "生成资源对象数量超过限制");
                // Unity's bundle loader opens the AssetBundle header at local object ID 1.
                // Native readers search by class, which would otherwise hide this compatibility requirement.
                auto id = type.ClassId == 142 ? std::int64_t{1} : Next++;
                Require(!Objects.contains(id), "资源包包含重复的主对象");
                Objects.emplace(id, Object{Type(std::move(type)), std::move(value)});
                return id;
            }

            std::int64_t New(int type)
            {
                auto t = Standard(type);
                auto p = DefaultUnityValue(t.Nodes);
                return Add(std::move(t), std::move(p));
            }

            Parameter &Get(std::int64_t id)
            {
                return Objects.at(id).Value;
            }

            const UnitySerializedType &Info(std::int64_t id)
            {
                return Types.at(Objects.at(id).Type);
            }

            Parameter Element(std::int64_t id, std::initializer_list<std::string_view> path)
            {
                return DefaultUnityElement(Info(id).Nodes, std::span(path.begin(), path.size()));
            }

            UnitySerializedType SourceType(const GpuAssetPointer &pointer, const Parameter &p)
            {
                auto type = Store->Reader(pointer.Bundle)->TypeOf(pointer.PathId);
                if (type.ClassId == 114)
                {
                    auto script = Store->Metadata(PointerAt(pointer.Bundle, p.Get("m_Script")));
                    if (type.Nodes.size() <= Database->Type(114, Version).size())
                        type.Nodes = Schemas->Type(script.String("m_AssemblyName"), script.String("m_Namespace"),
                                                   script.String("m_ClassName"), Version);
                }
                return type;
            }

            std::int64_t Copy(GpuAssetPointer pointer)
            {
                CheckCancelled(Token);
                if (!pointer.PathId)
                    return 0;
                pointer = Store->Resolve(pointer);
                auto key = Utf8(pointer.Bundle) + ":" + std::to_string(pointer.PathId);
                if (auto it = Copies.find(key); it != Copies.end())
                    return it->second;
                auto reader = Store->Reader(pointer.Bundle);
                auto p = reader->Metadata(pointer.PathId);
                auto type = SourceType(pointer, p);
                Require(WriteUnityValue(type.Nodes, p) == reader->RawObject(pointer.PathId),
                        "模板资源字段回写与原始对象不一致");
                Require(type.ClassId == 21 || type.ClassId == 28 || type.ClassId == 48 || type.ClassId == 89 ||
                            type.ClassId == 115,
                        "材质依赖包含不支持复制的对象类型");
                auto id = Add(type, p);
                Copies.emplace(key, id);
                Dependencies.insert(Utf8(pointer.Bundle));
                // Streaming resources become inline data, so no source file/CAB name leaks into the generated package.
                if (p.Has("m_StreamData"))
                {
                    auto &stream = At(p, "m_StreamData");
                    if (stream.Int("size"))
                    {
                        Require(type.ClassId == 28 || type.ClassId == 89, "不支持的外部资源流");
                        At(p, "image data").Bytes =
                            reader->ResourceStream(stream.String("path"), stream.Int("offset"), stream.Int("size"));
                        Set(stream, "offset", 0);
                        Set(stream, "size", 0);
                        Text(stream, "path", "");
                    }
                }
                VisitUnityPointers(p,
                                   [&](std::int32_t &file, std::int64_t &path)
                                   {
                                       if (!path)
                                       {
                                           file = 0;
                                           return;
                                       }
                                       auto resolved = Store->Resolve({pointer.Bundle, file, path});
                                       auto filename = Utf8(resolved.Bundle.filename());
                                       if (filename == "unity_builtin_extra" || filename == "unity default resources")
                                       {
                                           // Built-in player resources must keep Unity's well-known external identity.
                                           // In particular, built-in shaders cannot be rehomed into an AssetBundle.
                                           Dependencies.insert(Utf8(resolved.Bundle));
                                           auto it = std::find_if(Externals.begin(), Externals.end(),
                                                                  [&](const auto &ref)
                                                                  {
                                                                      return ref.Path == filename;
                                                                  });
                                           if (it == Externals.end())
                                           {
                                               UnityExternalReference ref;
                                               ref.Path = filename;
                                               Externals.push_back(ref);
                                               it = Externals.end() - 1;
                                           }
                                           file = static_cast<std::int32_t>(it - Externals.begin() + 1);
                                           path = resolved.PathId;
                                       }
                                       else
                                       {
                                           path = Copy(resolved);
                                           file = 0;
                                       }
                                   });
                Get(id) = std::move(p);
                return id;
            }

            std::int64_t Component(GpuAssetPointer pointer)
            {
                pointer = Store->Resolve(pointer);
                auto p = Store->Metadata(pointer);
                auto type = SourceType(pointer, p);
                auto script = Copy(PointerAt(pointer.Bundle, p.Get("m_Script")));
                VisitUnityPointers(p,
                                   [](auto &file, auto &path)
                                   {
                                       file = 0;
                                       path = 0;
                                   });
                Point(p, "m_Script", script);
                return Add(std::move(type), std::move(p));
            }

            std::int64_t Node(std::string name, std::int64_t parent = 0, Vector3 position = {},
                              Quaternion rotation = {0, 0, 0, 1}, bool active = true)
            {
                auto go = New(1), transform = New(4);
                Text(Get(go), "m_Name", std::move(name));
                Set(Get(go), "m_Layer", 10);
                Set(Get(go), "m_IsActive", active);
                Point(Get(transform), "m_GameObject", go);
                Point(Get(transform), "m_Father", parent);
                Vector(At(Get(transform), "m_LocalPosition"), position);
                Vector(At(Get(transform), "m_LocalRotation"), rotation);
                Vector(At(Get(transform), "m_LocalScale"), Vector3{1, 1, 1});
                Attach(transform, transform);
                if (parent)
                    Array(At(Get(parent), "m_Children")).Children.push_back(fields::Pointer(transform));
                return transform;
            }

            std::int64_t GameObject(std::int64_t transform)
            {
                return Get(transform).Get("m_GameObject").Int("m_PathID");
            }

            void Attach(std::int64_t transform, std::int64_t component)
            {
                auto go = GameObject(transform);
                auto element = Element(go, {"m_Component"});
                if (element.Has("component"))
                    Point(element, "component", component);
                else
                    element = fields::Pointer(component, element.Name);
                Array(At(Get(go), "m_Component")).Children.push_back(std::move(element));
                if (component != transform)
                    Point(Get(component), "m_GameObject", go);
            }

            UnitySerializedDocument Document()
            {
                UnitySerializedDocument doc;
                doc.UnityVersion = Version;
                doc.Types = Types;
                doc.Externals = Externals;
                for (const auto &[id, object] : Objects)
                {
                    const auto &type = Types.at(object.Type);
                    doc.Objects.push_back({id, object.Type, WriteUnityValue(type.Nodes, object.Value)});
                }
                std::map<std::int64_t, std::int16_t> scripts;
                for (std::size_t i = 0; i < Types.size(); ++i)
                    if (Types[i].ClassId == 114)
                    {
                        auto object = std::find_if(Objects.begin(), Objects.end(),
                                                   [&](const auto &o)
                                                   {
                                                       return o.second.Type == static_cast<int>(i);
                                                   });
                        Require(object != Objects.end(), "脚本类型没有实例");
                        auto script = object->second.Value.Get("m_Script").Int("m_PathID");
                        Require(script != 0 && Objects.contains(script), "脚本标识缺失");
                        auto [it, added] = scripts.emplace(script, static_cast<std::int16_t>(scripts.size()));
                        if (added)
                            doc.ScriptReferences.emplace_back(0, script);
                        doc.Types[i].ScriptTypeIndex = it->second;
                    }
                return doc;
            }
        };

        void Bounds(Parameter &value, const Mesh &mesh)
        {
            Vector3 minimum{INFINITY, INFINITY, INFINITY}, maximum{-INFINITY, -INFINITY, -INFINITY};
            for (const auto &vertex : mesh.Vertices)
            {
                minimum = {std::min(minimum.x, vertex.Position.x), std::min(minimum.y, vertex.Position.y),
                           std::min(minimum.z, vertex.Position.z)};
                maximum = {std::max(maximum.x, vertex.Position.x), std::max(maximum.y, vertex.Position.y),
                           std::max(maximum.z, vertex.Position.z)};
            }
            Vector(At(value, "m_Center"), (minimum + maximum) * .5f);
            Vector(At(value, "m_Extent"), (maximum - minimum) * .5f);
        }

        std::uint32_t NameHash(const std::string &name)
        {
            return Crc32({reinterpret_cast<const std::uint8_t *>(name.data()), name.size()});
        }
    }

    std::string AssetId(const NativeAssetEntry &entry)
    {
        return std::to_string(entry.Category) + ":" + entry.Guid + ":" + std::to_string(entry.Id);
    }

    Json TemplateChoices(CharacterCatalog &catalog, int category, int limit)
    {
        Json result = Json::array();
        for (const auto &entry : catalog.Query(category, {}, 0))
        {
            if (entry.Asset == "p_dummy" || entry.Asset.empty())
                continue;
            result.push_back({{"asset_id", AssetId(entry)}, {"name", entry.Get("Name")}, {"category", entry.Category}});
            if (static_cast<int>(result.size()) >= limit)
                break;
        }
        return result;
    }

    MaterialTemplate ResolveTemplate(CharacterCatalog &catalog, const CharacterDocument &document, const Json &spec,
                                     const fs::path &game, std::stop_token token)
    {
        auto target = spec.at("target_path").get<std::string>();
        auto binding = CharacterAssetReferences::Describe(document, target);
        Require(binding.has_value(), "素材目标槽位无效");
        std::optional<NativeAssetEntry> selected;
        if (spec.contains("template_asset_id"))
        {
            auto id = spec.at("template_asset_id").get<std::string>();
            for (const auto &entry : catalog.Entries())
                if (AssetId(entry) == id)
                {
                    selected = entry;
                    break;
                }
        }
        else
            selected = catalog.Selected(document, target);
        auto fail = [&]()
        {
            throw std::runtime_error("ASSET_TEMPLATE_REQUIRED：请选择同类别兼容模板；候选：" +
                                     TemplateChoices(catalog, binding->Category).dump());
        };
        if (!selected || selected->Category != binding->Category || selected->Asset == "p_dummy" ||
            selected->Asset.empty())
            fail();
        MaterialTemplate result;
        result.Entry = *selected;
        result.Database = catalog.Database();
        auto name = document.Sex() == 0 && !selected->MaleAsset.empty() && selected->MaleAsset != "0"
                        ? selected->MaleAsset
                        : selected->Asset;
        result.Prefab = catalog.Asset(selected->Bundle, name, selected->Archive);
        auto dependencies = catalog.Dependencies(selected->Bundle, selected->Archive);
        dependencies.push_back(result.Prefab.Bundle);
        result.Assets = std::make_shared<NativeGpuAssetStore>(dependencies, game, catalog.Database(), token);
        auto schemas = std::make_shared<NativeMonoSchema>(game / "HoneySelect2_Data/Managed", catalog.Database());
        result.Assets->SetScriptSchema(
            [weak = std::weak_ptr<NativeGpuAssetStore>(result.Assets), schemas](const GpuAssetPointer &ptr,
                                                                                std::string_view version)
            {
                auto store = weak.lock();
                Require(bool(store), "模板资源读取器已退出");
                auto script = store->Metadata(ptr);
                return schemas->Type(script.String("m_AssemblyName"), script.String("m_Namespace"),
                                     script.String("m_ClassName"), version);
            });
        result.Data = result.Assets->Prefab(result.Prefab.Bundle, name);
        auto kind = spec.at("kind").get<std::string>();
        result.ComponentName = kind == "hair" ? "CmpHair" : kind == "clothes" ? "CmpClothes" : "CmpAccessory";
        for (const auto &component : result.Data->Components)
            if (component.ClassId == 114 &&
                ComponentName(*result.Assets, result.Prefab.Bundle, component.Data) == result.ComponentName)
            {
                result.Component = {result.Prefab.Bundle, 0, component.PathId};
                break;
            }
        if (!result.Component.PathId || result.Data->Renderers.empty())
            fail();
        return result;
    }

    namespace
    {
        std::int64_t MeshObject(Graph &graph, const Mesh &mesh, const std::map<std::string, Bone> &bones,
                                std::vector<std::string> &used)
        {
            auto id = graph.New(43);
            auto &value = graph.Get(id);
            Text(value, "m_Name", mesh.Name);
            Set(value, "m_IsReadable", 1);
            Optional(value, "m_KeepVertices", 1);
            Optional(value, "m_KeepIndices", 1);
            Optional(value, "m_MeshMetrics[0]", 1);
            Optional(value, "m_MeshMetrics[1]", 1);
            std::set<std::string> names;
            for (const auto &v : mesh.Vertices)
                for (const auto &w : v.Weights)
                    names.insert(w.Bone);
            used.assign(names.begin(), names.end());
            std::map<std::string, int> indices;
            for (std::size_t i = 0; i < used.size(); ++i)
                indices.emplace(used[i], static_cast<int>(i));
            auto &vertexData = At(value, "m_VertexData");
            Set(vertexData, "m_VertexCount", mesh.Vertices.size());
            const std::array<int, 14> dimensions{
                3, 3, 4, 4, 2, 2, 0, 0, 0, 0, 0, 0, used.empty() ? 0 : 4, used.empty() ? 0 : 4};
            int offset = 0;
            for (int channel = 0; channel < 14; ++channel)
            {
                auto c = graph.Element(id, {"m_VertexData", "m_Channels"});
                // Unity 2018 serializes bone indices with channel format 11 (32-bit integers).
                Set(c, "dimension", dimensions[channel]);
                Set(c, "stream", 0);
                Set(c, "format", channel == 13 ? 11 : 0);
                Set(c, "offset", dimensions[channel] ? offset : 0);
                offset += dimensions[channel] * 4;
                Array(At(vertexData, "m_Channels")).Children.push_back(std::move(c));
            }
            auto &data = At(vertexData, "m_DataSize").Bytes;
            for (const auto &vertex : mesh.Vertices)
            {
                for (float f : {vertex.Position.x, vertex.Position.y, vertex.Position.z, vertex.Normal.x,
                                vertex.Normal.y, vertex.Normal.z, vertex.Tangent.x, vertex.Tangent.y, vertex.Tangent.z,
                                vertex.Tangent.w, vertex.Color.x, vertex.Color.y, vertex.Color.z, vertex.Color.w,
                                vertex.Uv.x, vertex.Uv.y, vertex.Uv1.x, vertex.Uv1.y})
                    Binary(data, f);
                if (!used.empty())
                {
                    for (std::size_t i = 0; i < 4; ++i)
                        Binary(data, i < vertex.Weights.size() ? vertex.Weights[i].Weight : 0.f);
                    for (std::size_t i = 0; i < 4; ++i)
                        Binary<std::uint32_t>(data, i < vertex.Weights.size() ? indices.at(vertex.Weights[i].Bone) : 0);
                }
            }
            while (data.size() % 16)
                data.push_back(0);
            auto &indexData = At(value, "m_IndexBuffer").Bytes;
            bool wide = mesh.Vertices.size() > 65535;
            Set(value, "m_IndexFormat", wide ? 1 : 0);
            auto maxMaterial = *std::max_element(mesh.Materials.begin(), mesh.Materials.end());
            for (int material = 0; material <= maxMaterial; ++material)
            {
                auto submesh = graph.Element(id, {"m_SubMeshes"});
                auto start = indexData.size();
                for (std::size_t triangle = 0; triangle < mesh.Materials.size(); ++triangle)
                    if (mesh.Materials[triangle] == material)
                        for (int j = 0; j < 3; ++j)
                            if (wide)
                                Binary(indexData, mesh.Indices[triangle * 3 + j]);
                            else
                                Binary<std::uint16_t>(indexData,
                                                      static_cast<std::uint16_t>(mesh.Indices[triangle * 3 + j]));
                Set(submesh, "firstByte", start);
                Set(submesh, "indexCount", (indexData.size() - start) / (wide ? 4 : 2));
                Set(submesh, "vertexCount", mesh.Vertices.size());
                Bounds(At(submesh, "localAABB"), mesh);
                Array(At(value, "m_SubMeshes")).Children.push_back(std::move(submesh));
            }
            Bounds(At(value, "m_LocalAABB"), mesh);
            for (const auto &name : used)
            {
                const auto &bone = bones.at(name);
                auto bind = InverseMatrix(BoneWorld(bone));
                auto pose = graph.Element(id, {"m_BindPose"});
                for (int r = 0; r < 4; ++r)
                    for (int c = 0; c < 4; ++c)
                        Float(pose, "e" + std::to_string(c) + std::to_string(r), bind.m[r][c]);
                Array(At(value, "m_BindPose")).Children.push_back(std::move(pose));
                auto hash = graph.Element(id, {"m_BoneNameHashes"});
                Integer(hash, NameHash(name));
                Array(At(value, "m_BoneNameHashes")).Children.push_back(std::move(hash));
            }
            std::map<std::string, std::vector<const Shape *>> shapes;
            for (const auto &shape : mesh.Shapes)
                shapes[shape.Name].push_back(&shape);
            auto &shapeData = At(value, "m_Shapes");
            for (auto &[name, frames] : shapes)
            {
                std::sort(frames.begin(), frames.end(),
                          [](auto a, auto b)
                          {
                              return a->Weight < b->Weight;
                          });
                auto channel = graph.Element(id, {"m_Shapes", "channels"});
                Text(channel, "name", name);
                Set(channel, "nameHash", NameHash(name));
                Set(channel, "frameIndex", Array(At(shapeData, "shapes")).Children.size());
                Set(channel, "frameCount", frames.size());
                float previous = 0;
                for (auto *shape : frames)
                {
                    Require(shape->Weight > previous, "形变帧权重必须为正且不重复");
                    previous = shape->Weight;
                    auto frame = graph.Element(id, {"m_Shapes", "shapes"});
                    Set(frame, "firstVertex", Array(At(shapeData, "vertices")).Children.size());
                    Set(frame, "vertexCount", mesh.Vertices.size());
                    Set(frame, "hasNormals", !shape->Normals.empty());
                    Set(frame, "hasTangents", !shape->Tangents.empty());
                    for (std::size_t v = 0; v < mesh.Vertices.size(); ++v)
                    {
                        auto vertex = graph.Element(id, {"m_Shapes", "vertices"});
                        Set(vertex, "index", v);
                        Vector(At(vertex, "vertex"), shape->Positions[v]);
                        if (!shape->Normals.empty())
                            Vector(At(vertex, "normal"), shape->Normals[v]);
                        if (!shape->Tangents.empty())
                            Vector(At(vertex, "tangent"), shape->Tangents[v]);
                        Array(At(shapeData, "vertices")).Children.push_back(std::move(vertex));
                    }
                    Array(At(shapeData, "shapes")).Children.push_back(std::move(frame));
                    auto weight = graph.Element(id, {"m_Shapes", "fullWeights"});
                    weight.Number = shape->Weight;
                    Array(At(shapeData, "fullWeights")).Children.push_back(std::move(weight));
                }
                Array(At(shapeData, "channels")).Children.push_back(std::move(channel));
            }
            auto check = GpuMeshData::FromMetadata(value, data);
            check.Validate();
            return id;
        }

        std::int64_t TextureObject(Graph &graph, const fs::path &file, const std::string &name, bool srgb)
        {
            using Microsoft::WRL::ComPtr;
            Require(fs::file_size(file) <= 32 * 1024 * 1024, "素材贴图超过 32 MiB");
            auto co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

            struct Co
            {
                bool Initialized;

                ~Co()
                {
                    if (Initialized)
                        CoUninitialize();
                }
            } com{SUCCEEDED(co)};

            auto check = [](HRESULT hr)
            {
                Require(SUCCEEDED(hr), "素材贴图无法解码");
            };
            ComPtr<IWICImagingFactory> factory;
            ComPtr<IWICBitmapDecoder> decoder;
            ComPtr<IWICBitmapFrameDecode> frame;
            check(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)));
            check(factory->CreateDecoderFromFilename(NativeFilePath(file).c_str(), nullptr, GENERIC_READ,
                                                     WICDecodeMetadataCacheOnLoad, &decoder));
            GUID format{};
            check(decoder->GetContainerFormat(&format));
            Require(format == GUID_ContainerFormatPng || format == GUID_ContainerFormatJpeg, "素材贴图只支持 PNG/JPEG");
            check(decoder->GetFrame(0, &frame));
            UINT width = 0, height = 0;
            check(frame->GetSize(&width, &height));
            Require(width && height && width <= 8192 && height <= 8192 && std::uint64_t(width) * height <= 32000000,
                    "素材贴图尺寸越界");
            ComPtr<IWICFormatConverter> converter;
            check(factory->CreateFormatConverter(&converter));
            check(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppRGBA, WICBitmapDitherTypeNone, nullptr, 0,
                                        WICBitmapPaletteTypeCustom));
            Bytes pixels(static_cast<std::size_t>(width) * height * 4);
            check(converter->CopyPixels(nullptr, width * 4, static_cast<UINT>(pixels.size()), pixels.data()));
            auto id = graph.New(28);
            auto &value = graph.Get(id);
            Text(value, "m_Name", name);
            Set(value, "m_Width", width);
            Set(value, "m_Height", height);
            Set(value, "m_CompleteImageSize", pixels.size());
            Set(value, "m_TextureFormat", 4);
            Set(value, "m_MipCount", 1);
            Set(value, "m_IsReadable", 1);
            Set(value, "m_ImageCount", 1);
            Set(value, "m_TextureDimension", 2);
            Set(value, "m_ColorSpace", srgb ? 1 : 0);
            auto &settings = At(value, "m_TextureSettings");
            Set(settings, "m_FilterMode", 1);
            Set(settings, "m_Aniso", 1);
            auto &bytes = At(value, "image data").Bytes;
            bytes.resize(pixels.size());
            for (UINT y = 0; y < height; ++y)
                std::copy_n(pixels.begin() + static_cast<std::size_t>(y) * width * 4, width * 4,
                            bytes.begin() + static_cast<std::size_t>(height - 1 - y) * width * 4);
            return id;
        }

        std::int64_t RendererObject(Graph &graph, std::int64_t node, std::int64_t meshId, const Mesh &mesh,
                                    std::span<const std::int64_t> materials, const std::vector<std::string> &used,
                                    const std::map<std::string, std::int64_t> &bones, std::int64_t rootBone)
        {
            bool skin = !used.empty();
            auto id = graph.New(skin ? 137 : 23);
            auto &value = graph.Get(id);
            graph.Attach(node, id);
            Set(value, "m_Enabled", 1);
            Set(value, "m_CastShadows", 1);
            Set(value, "m_ReceiveShadows", 1);
            Optional(value, "m_DynamicOccludee", 1);
            Optional(value, "m_LightProbeUsage", 1);
            Optional(value, "m_ReflectionProbeUsage", 1);
            Optional(value, "m_RenderingLayerMask", 1);
            Optional(value, "m_LightmapIndex", 65535);
            Optional(value, "m_LightmapIndexDynamic", 65535);
            if (value.Has("m_LightmapTilingOffset"))
                Vector(At(value, "m_LightmapTilingOffset"), Vector4{1, 1, 0, 0});
            if (value.Has("m_LightmapTilingOffsetDynamic"))
                Vector(At(value, "m_LightmapTilingOffsetDynamic"), Vector4{1, 1, 0, 0});
            Pointers(value, "m_Materials", materials);
            if (skin)
            {
                Point(value, "m_Mesh", meshId);
                Set(value, "m_UpdateWhenOffscreen", 1);
                Optional(value, "m_SkinnedMotionVectors", 1);
                std::vector<std::int64_t> ids;
                for (const auto &name : used)
                    ids.push_back(bones.at(name));
                Pointers(value, "m_Bones", ids);
                Point(value, "m_RootBone", rootBone);
                Bounds(At(value, "m_AABB"), mesh);
                std::set<std::string> shapes;
                for (const auto &s : mesh.Shapes)
                    shapes.insert(s.Name);
                for (const auto &name : shapes)
                {
                    (void)name;
                    auto weight = graph.Element(id, {"m_BlendShapeWeights"});
                    Array(At(value, "m_BlendShapeWeights")).Children.push_back(std::move(weight));
                }
            }
            else
            {
                auto filter = graph.New(33);
                Point(graph.Get(filter), "m_Mesh", meshId);
                graph.Attach(node, filter);
            }
            return id;
        }

        GpuAssetPointer DynamicTemplate(Graph &graph, const MaterialTemplate &source, const fs::path &game)
        {
            for (const auto &component : source.Data->Components)
                if (component.ClassId == 114 &&
                    ComponentName(*graph.Store, source.Prefab.Bundle, component.Data) == "DynamicBone")
                    return {source.Prefab.Bundle, 0, component.PathId};
            auto path = game / "abdata/chara/00/ao_head_00.unity3d";
            if (fs::is_regular_file(path))
            {
                auto reader = graph.Store->Reader(path);
                for (const auto &object : reader->AssetIndex())
                    if (object.ClassId == 114 &&
                        ComponentName(*graph.Store, path, reader->Metadata(object.PathId)) == "DynamicBone")
                        return {path, 0, object.PathId};
            }
            throw std::runtime_error("DYNAMIC_TEMPLATE_MISSING：本机资源中缺少兼容的 DynamicBone 类型模板");
        }
    }

    Json BuildGameBundle(const Geometry &geometry, const MaterialTemplate &source, const Json &spec,
                         const fs::path &game, const fs::path &output, std::stop_token token)
    {
        Graph graph(source, game, source.Database, token);
        auto kind = spec.at("kind").get<std::string>();
        auto root = graph.Node("hs2agent_generated");
        auto full = root, half = std::int64_t{0}, move1 = std::int64_t{0}, move2 = std::int64_t{0};
        if (kind == "accessory")
        {
            move1 = graph.Node("N_move", root);
            move2 = graph.Node("N_move2", move1);
            full = move2;
        }
        if (kind == "clothes")
        {
            full = graph.Node("n_clothes_def", root);
            half = graph.Node("n_clothes_half", root, {}, Quaternion{0, 0, 0, 1}, false);
        }
        std::map<std::string, Bone> definitions;
        for (const auto &bone : geometry.Bones)
            definitions.emplace(bone.Name, bone);
        if (kind == "hair")
            for (auto &[name, bone] : definitions)
                if (bone.Added && !bone.Parent.empty() && !definitions.at(bone.Parent).Added)
                {
                    // Hair is instantiated beneath N_hair_Root. BonesFramework searches that
                    // attachment subtree, so an ancestor such as cf_J_Head would match our copy
                    // and loop while reparenting it to itself. Preserve the world bind transform
                    // but attach each authored branch to the game's actual hair root.
                    Require(definitions.contains("N_hair_Root"), "发型参考缺少 N_hair_Root 挂点");
                    bone.Parent = "N_hair_Root";
                }
        std::set<std::string> required;
        for (const auto &mesh : geometry.Meshes)
            for (const auto &v : mesh.Vertices)
                for (const auto &w : v.Weights)
                    required.insert(w.Bone);
        for (const auto &dynamic : spec.value("dynamic_roots", Json::array()))
        {
            auto name = dynamic.get<std::string>();
            required.insert(name);
            for (const auto &[child, bone] : definitions)
            {
                auto p = child;
                while (!p.empty() && p != name)
                    p = definitions.at(p).Parent;
                if (p == name)
                    required.insert(child);
            }
        }
        std::map<std::string, std::int64_t> bones;
        std::function<std::int64_t(const std::string &)> boneNode = [&](const std::string &name)
        {
            if (auto it = bones.find(name); it != bones.end())
                return it->second;
            const auto &bone = definitions.at(name);
            auto parent = bone.Parent.empty() ? root : boneNode(bone.Parent);
            auto local = bone.Parent.empty()
                             ? BoneWorld(bone)
                             : MultiplyMatrix(BoneWorld(bone), InverseMatrix(BoneWorld(definitions.at(bone.Parent))));
            DirectX::XMVECTOR s, r, t;
            Require(DirectX::XMMatrixDecompose(&s, &r, &t, DirectX::XMLoadFloat4x4(&local)),
                    "骨骼局部矩阵无法分解为 TRS");
            Vector3 position, scale;
            Quaternion rotation;
            DirectX::XMStoreFloat3(&position, t);
            DirectX::XMStoreFloat3(&scale, s);
            DirectX::XMStoreFloat4(&rotation, r);
            auto id = graph.Node(name, parent, position, rotation);
            Vector(At(graph.Get(id), "m_LocalScale"), scale);
            bones.emplace(name, id);
            return id;
        };
        for (const auto &name : required)
            boneNode(name);
        auto rootBone = root;
        if (!bones.empty())
        {
            auto name = bones.begin()->first;
            while (!definitions.at(name).Parent.empty())
                name = definitions.at(name).Parent;
            rootBone = bones.at(name);
        }
        std::vector<GpuAssetPointer> templateMaterials;
        for (const auto &renderer : source.Data->Renderers)
            for (auto pointer : renderer.Materials)
                if (pointer.PathId)
                    templateMaterials.push_back(pointer);
        Require(!templateMaterials.empty(), "模板没有兼容材质");
        int materialCount = 1;
        for (const auto &mesh : geometry.Meshes)
            for (int slot : mesh.Materials)
                materialCount = std::max(materialCount, slot + 1);
        std::vector<std::int64_t> materials;
        for (int slot = 0; slot < materialCount; ++slot)
        {
            auto pointer = templateMaterials[std::min(static_cast<std::size_t>(slot), templateMaterials.size() - 1)];
            auto copied = graph.Copy(pointer);
            // Each output slot is independently editable, even when it uses the same source material.
            auto material = graph.Add(graph.Info(copied), graph.Get(copied));
            Text(graph.Get(material), "m_Name", "hs2agent_material_" + std::to_string(slot));
            materials.push_back(material);
        }
        std::vector<std::string> textureHashes;
        for (const auto &texture : spec.value("textures", Json::array()))
        {
            CheckCancelled(token);
            auto property = texture.at("property").get<std::string>(), path = texture.at("path").get<std::string>();
            bool srgb =
                texture.value("srgb", property == "_MainTex" || property == "_EmissionMap" || property == "_ColorTex");
            auto textureId =
                TextureObject(graph, Path(path), "hs2agent_texture_" + std::to_string(textureHashes.size()), srgb);
            textureHashes.push_back(HashFile(Path(path), token));
            int requested = texture.value("material_slot", -1);
            Require(requested >= -1 && requested < materialCount, "贴图材质槽越界");
            for (int slot = 0; slot < materialCount; ++slot)
            {
                if (requested >= 0 && requested != slot)
                    continue;
                auto &entries = Array(At(At(graph.Get(materials[slot]), "m_SavedProperties"), "m_TexEnvs")).Children;
                auto it = std::find_if(entries.begin(), entries.end(),
                                       [&](const auto &entry)
                                       {
                                           return entry.Get("first").Text == property;
                                       });
                Require(it != entries.end(), "模板材质不支持指定贴图属性");
                Point(At(*it, "second"), "m_Texture", textureId);
            }
        }
        for (const auto &property : spec.value("material_values", Json::array()))
        {
            int slot = property.at("slot");
            Require(slot >= 0 && slot < materialCount, "材质参数槽位越界");
            auto name = property.at("property").get<std::string>();
            auto &saved = At(graph.Get(materials.at(slot)), "m_SavedProperties");
            bool color = property.at("value").is_array();
            auto &entries = Array(At(saved, color ? "m_Colors" : "m_Floats")).Children;
            auto entry = std::find_if(entries.begin(), entries.end(),
                                      [&](const auto &value)
                                      {
                                          return value.Get("first").Text == name;
                                      });
            Require(entry != entries.end(), "模板材质不支持指定参数");
            if (color)
                Vector(At(*entry, "second"), Vector4Of(property.at("value")));
            else
            {
                auto value = property.at("value").get<double>();
                Require(std::isfinite(value), "材质参数必须为有限数值");
                At(*entry, "second").Number = value;
            }
        }
        bool independentHalf = spec.value("independent_half", false);
        Require(!independentHalf || kind == "clothes", "只有服装可设置独立半脱网格");
        if (independentHalf)
            Require(std::any_of(geometry.Meshes.begin(), geometry.Meshes.end(),
                                [](const auto &mesh)
                                {
                                    return mesh.Role == "clothes_half";
                                }),
                    "缺少独立半脱网格");
        std::vector<std::int64_t> renderers;
        for (const auto &mesh : geometry.Meshes)
        {
            std::vector<std::string> used;
            auto meshId = MeshObject(graph, mesh, definitions, used);
            auto slotCount = *std::max_element(mesh.Materials.begin(), mesh.Materials.end()) + 1;
            bool halfMesh = independentHalf && mesh.Role == "clothes_half";
            auto node = graph.Node(mesh.Name, halfMesh ? half : full);
            renderers.push_back(RendererObject(graph, node, meshId, mesh, std::span(materials).first(slotCount), used,
                                               bones, rootBone));
            if (half && !independentHalf)
            {
                auto halfNode = graph.Node(mesh.Name + "_half", half);
                renderers.push_back(RendererObject(graph, halfNode, meshId, mesh, std::span(materials).first(slotCount),
                                                   used, bones, rootBone));
            }
        }
        auto component = graph.Component(source.Component);
        graph.Attach(root, component);
        auto &cmp = graph.Get(component);
        Set(cmp, "m_Enabled", 1);
        // Component arrays contain only newly generated renderers; never retain hidden source prefab references.
        for (auto &field : cmp.Children)
            if (field.Name.starts_with("rend") || field.Name == "boneInfo")
                Clear(field);
        if (kind == "accessory")
        {
            Pointers(cmp, "rendNormal", renderers);
            Point(cmp, "trfMove01", move1);
            Point(cmp, "trfMove02", move2);
        }
        else if (kind == "hair")
            Pointers(cmp, "rendHair", renderers);
        else
        {
            Pointers(cmp, "rendNormal01", renderers);
            auto category = source.Entry.Category;
            bool lower = category == 141 || category == 241;
            Point(cmp, lower ? "objBotDef" : "objTopDef", graph.GameObject(full));
            Point(cmp, lower ? "objBotHalf" : "objTopHalf", graph.GameObject(half));
        }
        for (const auto &name : spec.value("dynamic_roots", Json::array()))
        {
            auto dynamic = graph.Component(DynamicTemplate(graph, source, game));
            graph.Attach(root, dynamic);
            auto &value = graph.Get(dynamic);
            Point(value, "m_Root", bones.at(name.get<std::string>()));
            Set(value, "m_Enabled", 1);
            for (auto &field : value.Children)
                if (field.Name.ends_with("Distrib") && field.Has("m_Curve"))
                    Clear(At(field, "m_Curve"));
            Float(value, "m_UpdateRate", 60);
            auto settings =
                spec.value("dynamic_settings", Json::object()).value(name.get<std::string>(), Json::object());
            for (auto [key, field, fallback] : {std::tuple{"damping", "m_Damping", .1f},
                                                {"elasticity", "m_Elasticity", .1f},
                                                {"stiffness", "m_Stiffness", .1f},
                                                {"inertia", "m_Inert", 0.f},
                                                {"radius", "m_Radius", 0.f}})
            {
                float amount = settings.value(key, fallback);
                Require(std::isfinite(amount) && amount >= 0 && amount <= (std::string(key) == "radius" ? 10.f : 1.f),
                        "动态骨骼参数超出范围");
                Float(value, field, amount);
            }
        }
        std::vector<std::string> additionalRoots;
        for (const auto &[name, id] : bones)
        {
            const auto &bone = definitions.at(name);
            if (bone.Added && (bone.Parent.empty() || !definitions.at(bone.Parent).Added))
            {
                Require(!bone.Parent.empty(), "新增骨骼根必须挂接已有游戏骨骼");
                Require(name.find_first_of("\t\r\n") == std::string::npos, "新增骨骼名称包含非法分隔符");
                additionalRoots.push_back(name);
            }
        }
        std::int64_t additional = 0;
        if (!additionalRoots.empty())
        {
            additional = graph.New(49);
            Text(graph.Get(additional), "m_Name", "additional_bones");
            std::string content = "hs2agent_generated";
            for (const auto &name : additionalRoots)
                content += '\t' + name;
            content += "\r\n";
            Text(graph.Get(additional), "m_Script", content);
        }
        std::vector<std::pair<std::string, std::int64_t>> masks;
        std::set<std::string> maskFields;
        for (const auto &mask : spec.value("masks", Json::array()))
        {
            auto field = mask.at("field").get<std::string>();
            static const std::set<std::string> allowed{"OverBodyMask",    "OverBraMask",    "OverBodyBMask",
                                                       "OverInnerTBMask", "OverInnerBMask", "OverPanstMask"};
            Require(kind == "clothes" && allowed.contains(field) && maskFields.insert(field).second,
                    "服装遮罩字段无效或重复");
            auto name = "mask_" + std::to_string(masks.size());
            masks.emplace_back(field, TextureObject(graph, Path(mask.at("path").get<std::string>()), name, false));
            textureHashes.push_back(HashFile(Path(mask.at("path").get<std::string>()), token));
        }
        auto bundle = graph.New(142);
        auto &header = graph.Get(bundle);
        Set(header, "m_RuntimeCompatibility", 1);
        Set(header, "m_PathFlags", 7);
        std::vector<std::int64_t> preload;
        for (const auto &[id, _] : graph.Objects)
            if (id != bundle)
                preload.push_back(id);
        Pointers(header, "m_PreloadTable", preload);
        auto container = [&](std::string path, std::int64_t id)
        {
            auto entry = graph.Element(bundle, {"m_Container"});
            Text(entry, "first", std::move(path));
            auto &info = At(entry, "second");
            Point(info, "asset", id);
            Set(info, "preloadIndex", 0);
            Set(info, "preloadSize", preload.size());
            Array(At(header, "m_Container")).Children.push_back(std::move(entry));
        };
        container("assets/hs2agentgenerated/character.prefab", graph.GameObject(root));
        if (additional)
            container("assets/hs2agentgenerated/additional_bones.txt", additional);
        for (std::size_t i = 0; i < masks.size(); ++i)
            container("assets/hs2agentgenerated/mask_" + std::to_string(i) + ".png", masks[i].second);
        auto canonical = spec;
        for (auto key : {"work_directory", "reference_model", "reference_scene", "source_model", "recipe_path"})
            canonical.erase(key);
        auto fingerprint = WriteUnitySerializedFile(graph.Document());
        auto description = canonical.dump() + std::string(BuilderVersion) + GeometryJson(geometry).dump() +
                           AssetId(source.Entry) + Json(source.Entry.Values).dump();
        for (const auto &hash : textureHashes)
            description += hash;
        for (const auto &dependency : graph.Dependencies)
            description += HashFile(Path(dependency), token);
        fingerprint.insert(fingerprint.end(), description.begin(), description.end());
        auto hash = Sha256(fingerprint), guid = spec.value("package_guid", "hs2agent." + hash);
        Require(!guid.empty() && guid.size() <= 128 &&
                    guid.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-") ==
                        guid.npos,
                "素材包 GUID 无效");
        auto slot = spec.value("slot_id", 100000 + static_cast<int>(std::stoul(hash.substr(0, 6), nullptr, 16)));
        Require(slot >= 100000 && slot < 1000000000, "素材编号越界");
        auto prefabName = "hs2agent_" + std::to_string(slot);
        if (additional)
        {
            std::string content = prefabName;
            for (const auto &name : additionalRoots)
                content += '\t' + name;
            Text(graph.Get(additional), "m_Script", content + "\r\n");
        }
        auto bundleName =
            "hs2agent/" + guid + (spec.contains("package_guid") ? "/" + std::to_string(slot) : "") + ".unity3d";
        std::transform(bundleName.begin(), bundleName.end(), bundleName.begin(),
                       [](unsigned char c)
                       {
                           return static_cast<char>(std::tolower(c));
                       });
        Text(graph.Get(graph.GameObject(root)), "m_Name", prefabName);
        Text(header, "m_Name", bundleName);
        Text(header, "m_AssetBundleName", bundleName);
        Text(Array(At(header, "m_Container")).Children.front(), "first",
             "assets/hs2agentgenerated/" + prefabName + ".prefab");
        auto serialized = WriteUnitySerializedFile(graph.Document());
        UnitySerializedFile roundtrip(serialized, source.Database.get());
        Require(roundtrip.Assets().size() == graph.Objects.size(), "生成资源对象回读数量不符");
        for (const auto &[id, object] : graph.Objects)
        {
            CheckCancelled(token);
            auto value = roundtrip.Read(id);
            VisitUnityPointers(
                value,
                [&](std::int32_t &file, std::int64_t &path)
                {
                    if (file == 0)
                        Require(!path || graph.Objects.contains(path), "生成包包含悬空对象引用");
                    else
                    {
                        Require(file > 0 && static_cast<std::size_t>(file) <= graph.Externals.size(),
                                "生成包包含无效外部引用");
                        auto resource = game / "HoneySelect2_Data/Resources" / Path(graph.Externals[file - 1].Path);
                        Require(graph.Store->Reader(resource)->AssetClass(path).has_value(), "游戏内置资源引用不存在");
                    }
                });
            (void)object;
        }
        auto path = output / "abdata" / Path(bundleName);
        std::vector<UnityBundleOutput> entries{{"CAB-" + hash.substr(0, 32), std::move(serialized), 4}};
        WriteAtomic(path, WriteUnityBundle(graph.Version, entries, token));
        NativeGpuAssetReader check(path, source.Database.get());
        Require(check.AssetClass(1) == 142, "生成资源包缺少游戏加载器要求的编号 1 主对象");
        auto prefab = check.Prefab(prefabName);
        Require(prefab.Renderers.size() == renderers.size(), "生成 Prefab 回读渲染器数量不符");
        for (auto &renderer : prefab.Renderers)
            check.Mesh(renderer.Mesh.PathId).Validate();
        Json maskNames = Json::object();
        for (std::size_t i = 0; i < masks.size(); ++i)
            maskNames[masks[i].first] = "mask_" + std::to_string(i);
        return {{"hash", hash},
                {"guid", guid},
                {"slot_id", slot},
                {"bundle", bundleName},
                {"prefab", prefabName},
                {"masks", maskNames},
                {"additional_bones", additionalRoots},
                {"builder_version", BuilderVersion},
                {"template_asset_id", AssetId(source.Entry)},
                {"source_dependencies", graph.Dependencies},
                {"objects", graph.Objects.size()},
                {"game_validation", "not_run"}};
    }
}
