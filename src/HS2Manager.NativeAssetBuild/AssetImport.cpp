#include "AssetGeometry.h"
#include "Animation/SimulationMath.h"
#include <windows.h>
#include <algorithm>
#include <cctype>
#include <cstring>
#include <map>
#include <set>
#pragma warning(push, 0)
#define CGLTF_IMPLEMENTATION
#include "ThirdParty/cgltf.h"
#pragma warning(pop)

namespace hs2::asset
{
    using namespace sim;

    namespace
    {
        Geometry ReadFbx(const fs::path &path, const fs::path &bridge)
        {
            Require(bridge.is_absolute(), "FBX 适配层路径必须为绝对路径");
            auto module = LoadLibraryExW(bridge.c_str(), nullptr,
                                         LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
            Require(module != nullptr, "无法加载原生 FBX 适配层");

            struct Module
            {
                HMODULE Value;

                ~Module()
                {
                    FreeLibrary(Value);
                }
            } owner{module};

            auto import =
                reinterpret_cast<const char *(*)(const char *)>(GetProcAddress(module, "hs2_fbx_import_json"));
            auto error = reinterpret_cast<const char *(*)()>(GetProcAddress(module, "hs2_fbx_error"));
            Require(import && error, "FBX 适配层版本过旧，请重新构建");
            const char *json = import(Utf8(path).c_str());
            if (!json)
                throw std::runtime_error(error() ? error() : "FBX 导入失败");
            auto result = GeometryOf(Json::parse(json));
            for (auto &bone : result.Bones)
                bone.Added = false;
            return result;
        }

        struct GltfFiles
        {
            fs::path Root;
            std::stop_token Token;
            std::size_t Total = 0;
            std::vector<std::string> Hashes;

            static cgltf_result Read(const cgltf_memory_options *, const cgltf_file_options *options, const char *path,
                                     cgltf_size *size, void **data)
            {
                auto &context = *static_cast<GltfFiles *>(options->user_data);
                try
                {
                    CheckCancelled(context.Token);
                    auto file = fs::weakly_canonical(Path(path));
                    auto relative = file.lexically_relative(context.Root);
                    if (relative.empty() || relative.is_absolute() || *relative.begin() == "..")
                        return cgltf_result_io_error;
                    auto bytes = ReadFile(file, 256ull * 1024 * 1024, context.Token);
                    if (bytes.size() > 512ull * 1024 * 1024 - context.Total)
                        return cgltf_result_out_of_memory;
                    context.Total += bytes.size();
                    context.Hashes.push_back(Sha256(bytes));
                    auto *memory = std::malloc(bytes.size() ? bytes.size() : 1);
                    if (!memory)
                        return cgltf_result_out_of_memory;
                    std::memcpy(memory, bytes.data(), bytes.size());
                    *size = bytes.size();
                    *data = memory;
                    return cgltf_result_success;
                }
                catch (const std::exception &)
                {
                    return cgltf_result_io_error;
                }
            }

            static void Release(const cgltf_memory_options *, const cgltf_file_options *, void *data)
            {
                std::free(data);
            }
        };

        const cgltf_accessor *Attribute(const cgltf_primitive &primitive, cgltf_attribute_type type, int index = 0)
        {
            for (std::size_t i = 0; i < primitive.attributes_count; ++i)
                if (primitive.attributes[i].type == type && primitive.attributes[i].index == index)
                    return primitive.attributes[i].data;
            return nullptr;
        }

        std::array<float, 4> ReadFloat(const cgltf_accessor *accessor, std::size_t index, std::size_t count)
        {
            std::array<float, 4> result{};
            Require(accessor && index < accessor->count &&
                        cgltf_accessor_read_float(accessor, index, result.data(), count),
                    "glTF 顶点属性不可读取");
            for (auto f : result)
                Require(std::isfinite(f), "glTF 顶点属性不是有限值");
            return result;
        }

        Matrix4x4 World(cgltf_node *node)
        {
            Matrix4x4 result;
            cgltf_node_transform_world(node, &result.m[0][0]);
            return result;
        }

        Geometry ReadGltf(const fs::path &path, std::stop_token token)
        {
            auto bytes = ReadFile(path, 256ull * 1024 * 1024, token);
            GltfFiles files{fs::weakly_canonical(path.parent_path()), token};
            cgltf_options options{};
            options.file.read = GltfFiles::Read;
            options.file.release = GltfFiles::Release;
            options.file.user_data = &files;
            cgltf_data *data = nullptr;
            Require(cgltf_parse(&options, bytes.data(), bytes.size(), &data) == cgltf_result_success,
                    "glTF/GLB 文件结构无效");

            struct Owner
            {
                cgltf_data *Data;

                ~Owner()
                {
                    cgltf_free(Data);
                }
            } owner{data};

            Require(data->nodes_count <= 100000 && data->meshes_count <= 1024, "glTF 场景超过节点或网格预算");
            Require(cgltf_load_buffers(&options, data, Utf8(path).c_str()) == cgltf_result_success,
                    "glTF 缓冲区缺失、越界或读取失败");
            Require(cgltf_validate(data) == cgltf_result_success, "glTF 数据验证失败");
            Geometry result;
            std::map<cgltf_node *, std::string> boneNames;
            std::set<std::string> usedNames;
            std::function<std::string(cgltf_node *)> bone = [&](cgltf_node *node) -> std::string
            {
                if (!node)
                    return {};
                if (auto found = boneNames.find(node); found != boneNames.end())
                    return found->second;
                auto parent = bone(node->parent);
                std::string name = node->name ? node->name : "gltf_joint_" + std::to_string(node - data->nodes);
                Require(usedNames.insert(name).second, "glTF 骨骼名称重复");
                auto world = World(node);
                DirectX::XMVECTOR scale, rotation, translation;
                Require(DirectX::XMMatrixDecompose(&scale, &rotation, &translation, DirectX::XMLoadFloat4x4(&world)),
                        "glTF 骨骼矩阵不可分解");
                Quaternion q;
                DirectX::XMStoreFloat4(&q, rotation);
                result.Bones.push_back(
                    {name, parent, {-world._41 * 10, world._42 * 10, world._43 * 10}, {q.x, -q.y, -q.z, q.w}, false});
                auto &importedBone = result.Bones.back();
                DirectX::XMStoreFloat4x4(&importedBone.Matrix, DirectX::XMMatrixScaling(-.1f, .1f, .1f) *
                                                                   DirectX::XMLoadFloat4x4(&world) *
                                                                   DirectX::XMMatrixScaling(-10, 10, 10));
                importedBone.HasMatrix = true;
                boneNames.emplace(node, name);
                return name;
            };
            std::size_t vertices = 0, triangles = 0;
            for (std::size_t nodeIndex = 0; nodeIndex < data->nodes_count; ++nodeIndex)
            {
                CheckCancelled(token);
                auto *node = &data->nodes[nodeIndex];
                if (!node->mesh)
                    continue;
                auto world = World(node), inverse = InverseMatrix(world);
                Matrix4x4 normal;
                DirectX::XMStoreFloat4x4(&normal, DirectX::XMMatrixTranspose(DirectX::XMLoadFloat4x4(&inverse)));
                bool mirrored =
                    DirectX::XMVectorGetX(DirectX::XMMatrixDeterminant(DirectX::XMLoadFloat4x4(&world))) < 0;
                if (node->skin)
                    for (std::size_t j = 0; j < node->skin->joints_count; ++j)
                        bone(node->skin->joints[j]);
                for (std::size_t p = 0; p < node->mesh->primitives_count; ++p)
                {
                    auto &primitive = node->mesh->primitives[p];
                    Require(!primitive.has_draco_mesh_compression, "glTF Draco 压缩需要先解压为普通 glTF");
                    Require(primitive.type == cgltf_primitive_type_triangles, "glTF 素材必须使用三角形网格");
                    auto positions = Attribute(primitive, cgltf_attribute_type_position),
                         normals = Attribute(primitive, cgltf_attribute_type_normal),
                         uv = Attribute(primitive, cgltf_attribute_type_texcoord),
                         uv1 = Attribute(primitive, cgltf_attribute_type_texcoord, 1),
                         colors = Attribute(primitive, cgltf_attribute_type_color);
                    Require(positions && uv && positions->count == uv->count, "glTF 网格缺少位置或 UV");
                    vertices += positions->count;
                    auto count = primitive.indices ? primitive.indices->count : positions->count;
                    triangles += count / 3;
                    Require(vertices <= 1000000 && triangles <= 250000 && count % 3 == 0,
                            "glTF 网格超过预算或索引数量无效");
                    Mesh mesh;
                    mesh.Name = std::string(node->name ? node->name : "gltf_mesh") + "_" + std::to_string(nodeIndex) +
                                "_" + std::to_string(p);
                    mesh.HasUv = true;
                    mesh.HasNormals = normals != nullptr;
                    for (std::size_t v = 0; v < positions->count; ++v)
                    {
                        auto pos = ReadFloat(positions, v, 3);
                        auto pt = TransformPoint({pos[0], pos[1], pos[2]}, world);
                        Vertex vertex;
                        vertex.Position = {-pt.x * 10, pt.y * 10, pt.z * 10};
                        if (normals)
                        {
                            auto n = ReadFloat(normals, v, 3);
                            auto t = Unit(TransformVector({n[0], n[1], n[2]}, normal));
                            vertex.Normal = {-t.x, t.y, t.z};
                        }
                        auto tex = ReadFloat(uv, v, 2);
                        vertex.Uv = {tex[0], 1 - tex[1]};
                        if (uv1)
                        {
                            auto t = ReadFloat(uv1, v, 2);
                            vertex.Uv1 = {t[0], 1 - t[1]};
                        }
                        if (colors)
                        {
                            auto c = ReadFloat(colors, v, cgltf_num_components(colors->type));
                            vertex.Color = {c[0], c[1], c[2], colors->type == cgltf_type_vec4 ? c[3] : 1.f};
                        }
                        for (int set = 0; node->skin && set < 2; ++set)
                        {
                            auto joints = Attribute(primitive, cgltf_attribute_type_joints, set),
                                 weights = Attribute(primitive, cgltf_attribute_type_weights, set);
                            if (!joints && !weights)
                                continue;
                            Require(joints && weights, "glTF 蒙皮索引和权重不匹配");
                            std::array<cgltf_uint, 4> indices{};
                            Require(cgltf_accessor_read_uint(joints, v, indices.data(), 4), "glTF 蒙皮索引不可读取");
                            auto w = ReadFloat(weights, v, 4);
                            for (int c = 0; c < 4; ++c)
                            {
                                if (w[c] <= 0)
                                    continue;
                                Require(indices[c] < node->skin->joints_count, "glTF 蒙皮骨骼越界");
                                vertex.Weights.push_back({boneNames.at(node->skin->joints[indices[c]]), w[c]});
                            }
                        }
                        mesh.Vertices.push_back(std::move(vertex));
                    }
                    for (std::size_t i = 0; i < count; i += 3)
                    {
                        std::array<std::uint32_t, 3> index;
                        for (int j = 0; j < 3; ++j)
                        {
                            auto value =
                                primitive.indices ? cgltf_accessor_read_index(primitive.indices, i + j) : i + j;
                            Require(value < positions->count, "glTF 索引越界");
                            index[j] = static_cast<std::uint32_t>(value);
                        }
                        if (!mirrored)
                            std::swap(index[1], index[2]);
                        mesh.Indices.insert(mesh.Indices.end(), index.begin(), index.end());
                        mesh.Materials.push_back(
                            primitive.material ? static_cast<int>(primitive.material - data->materials) : 0);
                    }
                    for (std::size_t target = 0; target < primitive.targets_count; ++target)
                    {
                        Shape shape;
                        shape.Name = node->mesh->target_names_count > target ? node->mesh->target_names[target]
                                                                             : "Morph_" + std::to_string(target);
                        for (std::size_t a = 0; a < primitive.targets[target].attributes_count; ++a)
                        {
                            auto &attribute = primitive.targets[target].attributes[a];
                            if (attribute.type != cgltf_attribute_type_position &&
                                attribute.type != cgltf_attribute_type_normal &&
                                attribute.type != cgltf_attribute_type_tangent)
                                continue;
                            auto &values = attribute.type == cgltf_attribute_type_position ? shape.Positions
                                           : attribute.type == cgltf_attribute_type_normal ? shape.Normals
                                                                                           : shape.Tangents;
                            for (std::size_t v = 0; v < positions->count; ++v)
                            {
                                auto n = ReadFloat(attribute.data, v, 3);
                                bool point = attribute.type == cgltf_attribute_type_position;
                                auto t = TransformVector(
                                    {n[0], n[1], n[2]}, attribute.type == cgltf_attribute_type_normal ? normal : world);
                                values.push_back(
                                    {-t.x * (point ? 10 : 1), t.y * (point ? 10 : 1), t.z * (point ? 10 : 1)});
                            }
                        }
                        if (shape.Positions.empty())
                            shape.Positions.resize(mesh.Vertices.size());
                        mesh.Shapes.push_back(std::move(shape));
                    }
                    result.Meshes.push_back(std::move(mesh));
                }
            }
            Require(!result.Meshes.empty(), "glTF 场景没有网格");
            result.InputHashes = std::move(files.Hashes);
            return result;
        }
    }

    Geometry ImportModel(const fs::path &source, const fs::path &bridge, std::stop_token token)
    {
        CheckCancelled(token);
        Require(fs::is_regular_file(source) && fs::file_size(source) <= 256ull * 1024 * 1024,
                "模型文件不存在或超过 256 MiB");
        auto extension = Utf8(source.extension());
        std::transform(extension.begin(), extension.end(), extension.begin(),
                       [](unsigned char c)
                       {
                           return static_cast<char>(std::tolower(c));
                       });
        Geometry result;
        if (extension == ".fbx" || extension == ".obj")
            result = ReadFbx(source, bridge);
        else if (extension == ".glb" || extension == ".gltf")
            result = ReadGltf(source, token);
        else
            throw std::invalid_argument("素材模型必须为 FBX、GLB/GLTF 或 OBJ");
        result.InputHashes.push_back(HashFile(source, token));
        CheckCancelled(token);
        return result;
    }
}
