#include "AssetGeometry.h"
#include "Animation/SimulationMath.h"
#include "Images/ImageFiles.h"
#include <algorithm>
#include <map>
#include <set>
#include <numeric>

namespace hs2::asset
{
    using namespace sim;

    namespace
    {
        Vector3 Min(Vector3 a, Vector3 b)
        {
            return {std::min(a.x, b.x), std::min(a.y, b.y), std::min(a.z, b.z)};
        }

        Vector3 Max(Vector3 a, Vector3 b)
        {
            return {std::max(a.x, b.x), std::max(a.y, b.y), std::max(a.z, b.z)};
        }

        void VectorSize(const Json &value, int count)
        {
            Require(value.is_array() && value.size() == static_cast<std::size_t>(count), "建模向量分量数量无效");
            for (const auto &item : value)
                Require(item.is_number() && std::isfinite(item.get<float>()), "建模向量包含非有限数值");
        }

        Vector3 Low(const Mesh &mesh)
        {
            Vector3 result{INFINITY, INFINITY, INFINITY};
            for (const auto &v : mesh.Vertices)
                result = Min(result, v.Position);
            return result;
        }

        Vector3 High(const Mesh &mesh)
        {
            Vector3 result{-INFINITY, -INFINITY, -INFINITY};
            for (const auto &v : mesh.Vertices)
                result = Max(result, v.Position);
            return result;
        }

        void NormalizeWeights(Vertex &vertex, const std::set<std::string> &bones)
        {
            std::map<std::string, float> sums;
            for (const auto &weight : vertex.Weights)
            {
                Require(std::isfinite(weight.Weight) && weight.Weight >= 0, "顶点权重不是非负有限值");
                if (weight.Weight <= .00001f)
                    continue;
                Require(bones.contains(weight.Bone), "顶点引用不存在的目标骨骼");
                sums[weight.Bone] += weight.Weight;
            }
            vertex.Weights.clear();
            for (auto &[bone, weight] : sums)
                vertex.Weights.push_back({bone, weight});
            std::sort(vertex.Weights.begin(), vertex.Weights.end(),
                      [](const auto &a, const auto &b)
                      {
                          return a.Weight != b.Weight ? a.Weight > b.Weight : a.Bone < b.Bone;
                      });
            if (vertex.Weights.size() > 4)
                vertex.Weights.resize(4);
            float total = 0;
            for (const auto &weight : vertex.Weights)
                total += weight.Weight;
            Require(std::isfinite(total) && total > .00001f, "网格存在未绑定顶点");
            for (auto &weight : vertex.Weights)
                weight.Weight /= total;
        }

        // Closest-point barycentrics; the BVH searches triangles, never merely nearest vertices.
        Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            auto ab = b - a, ac = c - a, ap = p - a;
            float d1 = Dot(ab, ap), d2 = Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0)
                return {1, 0, 0};
            auto bp = p - b;
            float d3 = Dot(ab, bp), d4 = Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3)
                return {0, 1, 0};
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0)
            {
                float v = d1 / (d1 - d3);
                return {1 - v, v, 0};
            }
            auto cp = p - c;
            float d5 = Dot(ab, cp), d6 = Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6)
                return {0, 0, 1};
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0)
            {
                float v = d2 / (d2 - d6);
                return {1 - v, 0, v};
            }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 >= d3 && d5 >= d6)
            {
                float v = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return {0, 1 - v, v};
            }
            float sum = va + vb + vc;
            if (std::abs(sum) < 1e-20f)
                return {1, 0, 0};
            return {va / sum, vb / sum, vc / sum};
        }

        struct Surface
        {
            struct Triangle
            {
                const Vertex *A, *B, *C;
                Vector3 Center;
            };

            struct Node
            {
                Vector3 Min{}, Max{};
                std::size_t Begin = 0, End = 0;
                int Left = -1, Right = -1;
            };

            std::vector<Triangle> Triangles;
            std::vector<Node> Nodes;

            explicit Surface(const Geometry &reference)
            {
                for (const auto &mesh : reference.Meshes)
                    if (mesh.Role == "body")
                        for (std::size_t i = 0; i + 2 < mesh.Indices.size(); i += 3)
                        {
                            auto &a = mesh.Vertices.at(mesh.Indices[i]), &b = mesh.Vertices.at(mesh.Indices[i + 1]),
                                 &c = mesh.Vertices.at(mesh.Indices[i + 2]);
                            if (LengthSquared(Cross(b.Position - a.Position, c.Position - a.Position)) < 1e-16f)
                                continue;
                            Triangles.push_back({&a, &b, &c, (a.Position + b.Position + c.Position) / 3.f});
                        }
                Require(!Triangles.empty(), "参考场景缺少可转移权重的身体表面");
                Build(0, Triangles.size());
            }

            int Build(std::size_t begin, std::size_t end)
            {
                Node n;
                n.Begin = begin;
                n.End = end;
                n.Min = {INFINITY, INFINITY, INFINITY};
                n.Max = {-INFINITY, -INFINITY, -INFINITY};
                for (auto i = begin; i < end; ++i)
                    for (auto *v : {Triangles[i].A, Triangles[i].B, Triangles[i].C})
                    {
                        n.Min = Min(n.Min, v->Position);
                        n.Max = Max(n.Max, v->Position);
                    }
                int index = static_cast<int>(Nodes.size());
                Nodes.push_back(n);
                if (end - begin > 8)
                {
                    auto extent = n.Max - n.Min;
                    int axis = extent.y > extent.x ? 1 : 0;
                    if (extent.z > (axis == 0 ? extent.x : extent.y))
                        axis = 2;
                    auto coordinate = [axis](Vector3 v)
                    {
                        return axis == 0 ? v.x : axis == 1 ? v.y : v.z;
                    };
                    auto mid = begin + (end - begin) / 2;
                    std::nth_element(Triangles.begin() + begin, Triangles.begin() + mid, Triangles.begin() + end,
                                     [&](const auto &a, const auto &b)
                                     {
                                         return coordinate(a.Center) < coordinate(b.Center);
                                     });
                    auto left = Build(begin, mid), right = Build(mid, end);
                    Nodes[index].Left = left;
                    Nodes[index].Right = right;
                }
                return index;
            }

            void Transfer(Vertex &vertex) const
            {
                float distance = INFINITY;
                const Triangle *best = nullptr;
                Vector3 bary{};
                std::function<void(int)> search = [&](int index)
                {
                    const auto &n = Nodes[index];
                    auto near = Max(n.Min, Min(n.Max, vertex.Position));
                    if (LengthSquared(near - vertex.Position) > distance)
                        return;
                    if (n.Left >= 0)
                    {
                        auto cost = [&](int i)
                        {
                            return LengthSquared(Max(Nodes[i].Min, Min(Nodes[i].Max, vertex.Position)) -
                                                 vertex.Position);
                        };
                        if (cost(n.Left) < cost(n.Right))
                        {
                            search(n.Left);
                            search(n.Right);
                        }
                        else
                        {
                            search(n.Right);
                            search(n.Left);
                        }
                        return;
                    }
                    for (auto i = n.Begin; i < n.End; ++i)
                    {
                        auto &t = Triangles[i];
                        auto b = Closest(vertex.Position, t.A->Position, t.B->Position, t.C->Position);
                        auto p = t.A->Position * b.x + t.B->Position * b.y + t.C->Position * b.z;
                        float d = LengthSquared(p - vertex.Position);
                        if (d < distance)
                        {
                            distance = d;
                            best = &t;
                            bary = b;
                        }
                    }
                };
                search(0);
                Require(best != nullptr, "找不到身体权重表面");
                for (auto [v, weight] :
                     {std::pair{best->A, bary.x}, std::pair{best->B, bary.y}, std::pair{best->C, bary.z}})
                    for (const auto &source : v->Weights)
                        vertex.Weights.push_back({source.Bone, source.Weight * weight});
            }
        };
    }

    Vector3 Vector3Of(const Json &value)
    {
        VectorSize(value, 3);
        return {value[0], value[1], value[2]};
    }

    Vector2 Vector2Of(const Json &value)
    {
        VectorSize(value, 2);
        return {value[0], value[1]};
    }

    Vector4 Vector4Of(const Json &value)
    {
        VectorSize(value, 4);
        return {value[0], value[1], value[2], value[3]};
    }

    Json VectorJson(Vector2 v)
    {
        return {v.x, v.y};
    }

    Json VectorJson(Vector3 v)
    {
        return {v.x, v.y, v.z};
    }

    Json VectorJson(Vector4 v)
    {
        return {v.x, v.y, v.z, v.w};
    }

    Matrix4x4 BoneWorld(const Bone &bone)
    {
        if (bone.HasMatrix)
            return bone.Matrix;
        return InverseMatrix(NativeSceneNode::InverseRigidMatrix(bone.Position, bone.Rotation));
    }

    Matrix4x4 TransformOf(const Json &spec)
    {
        auto position = Vector3Of(spec.value("position", Json::array({0, 0, 0})));
        auto rotation = Vector3Of(spec.value("rotation", Json::array({0, 0, 0})));
        auto scale = Vector3Of(spec.value("scale", Json::array({1, 1, 1})));
        Require(std::abs(scale.x * scale.y * scale.z) > 1e-12f, "模型变换包含零缩放");
        Matrix4x4 result;
        // Unity Euler convention: Z, X, Y; row-vector matrices apply in that order.
        constexpr float radians = .017453292519943295f;
        DirectX::XMStoreFloat4x4(&result, DirectX::XMMatrixScaling(scale.x, scale.y, scale.z) *
                                              DirectX::XMMatrixRotationZ(rotation.z * radians) *
                                              DirectX::XMMatrixRotationX(rotation.x * radians) *
                                              DirectX::XMMatrixRotationY(rotation.y * radians) *
                                              DirectX::XMMatrixTranslation(position.x, position.y, position.z));
        return result;
    }

    void TransformMesh(Mesh &mesh, const Matrix4x4 &matrix)
    {
        auto inverse = InverseMatrix(matrix);
        Matrix4x4 normal;
        DirectX::XMStoreFloat4x4(&normal, DirectX::XMMatrixTranspose(DirectX::XMLoadFloat4x4(&inverse)));
        bool mirrored = DirectX::XMVectorGetX(DirectX::XMMatrixDeterminant(DirectX::XMLoadFloat4x4(&matrix))) < 0;
        for (auto &vertex : mesh.Vertices)
        {
            vertex.Position = TransformPoint(vertex.Position, matrix);
            vertex.Normal = Unit(TransformVector(vertex.Normal, normal));
            auto t = Unit(TransformVector({vertex.Tangent.x, vertex.Tangent.y, vertex.Tangent.z}, matrix));
            vertex.Tangent = {t.x, t.y, t.z, vertex.Tangent.w * (mirrored ? -1.f : 1.f)};
        }
        for (auto &shape : mesh.Shapes)
        {
            for (auto &v : shape.Positions)
                v = TransformVector(v, matrix);
            for (auto &v : shape.Normals)
                v = TransformVector(v, normal);
            for (auto &v : shape.Tangents)
                v = TransformVector(v, matrix);
        }
        if (mirrored)
            for (std::size_t i = 0; i + 2 < mesh.Indices.size(); i += 3)
                std::swap(mesh.Indices[i + 1], mesh.Indices[i + 2]);
    }

    void RebuildNormalsTangents(Mesh &mesh)
    {
        std::vector<Vector3> normals(mesh.Vertices.size()), tangents(mesh.Vertices.size()),
            bitangents(mesh.Vertices.size());
        for (std::size_t i = 0; i + 2 < mesh.Indices.size(); i += 3)
        {
            auto a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            auto &va = mesh.Vertices.at(a), &vb = mesh.Vertices.at(b), &vc = mesh.Vertices.at(c);
            auto ab = vb.Position - va.Position, ac = vc.Position - va.Position, n = Cross(ab, ac);
            float u1 = vb.Uv.x - va.Uv.x, v1 = vb.Uv.y - va.Uv.y, u2 = vc.Uv.x - va.Uv.x, v2 = vc.Uv.y - va.Uv.y;
            float det = u1 * v2 - u2 * v1;
            auto t = std::abs(det) > 1e-12f ? (ab * v2 - ac * v1) / det : Vector3{};
            auto bt = std::abs(det) > 1e-12f ? (ac * u1 - ab * u2) / det : Vector3{};
            for (auto index : {a, b, c})
            {
                normals[index] += n;
                tangents[index] += t;
                bitangents[index] += bt;
            }
        }
        for (std::size_t i = 0; i < mesh.Vertices.size(); ++i)
        {
            auto &v = mesh.Vertices[i];
            v.Normal = Unit(mesh.HasNormals ? v.Normal : normals[i], {0, 1, 0});
            auto t = Unit(tangents[i] - v.Normal * Dot(v.Normal, tangents[i]));
            if (LengthSquared(t) < .1f)
                t = Unit(Cross(std::abs(v.Normal.y) < .9f ? Vector3{0, 1, 0} : Vector3{1, 0, 0}, v.Normal));
            if (!mesh.HasTangents)
                v.Tangent = {t.x, t.y, t.z, Dot(Cross(v.Normal, t), bitangents[i]) < 0 ? -1.f : 1.f};
        }
        mesh.HasNormals = mesh.HasTangents = true;
    }

    Geometry CaptureReference(const PreparedCharacter &character, std::stop_token token)
    {
        Require(bool(character.Scene), "缺少人物参考场景");
        const auto &scene = *character.Scene;
        Geometry result;
        std::map<std::string, NativeSceneNode *> bones;
        std::set<std::string> sharedBones;
        for (const auto &part : scene.Parts)
            if (part->Definition->Role == "rig" || part->Definition->Role == "headRig")
                for (auto *node : part->Nodes)
                    sharedBones.insert(node->Name);
        for (const auto &[name, node] : scene.Bones)
            bones.emplace(name, node);
        for (const auto &renderer : scene.Renderers)
            for (auto *node : renderer->Bones)
                for (; node && node != scene.Root.get(); node = node->Parent())
                    bones.try_emplace(node->Name, node);
        for (const auto &[name, node] : bones)
        {
            auto *parent = node->Parent();
            while (parent && !bones.contains(parent->Name))
                parent = parent->Parent();
            result.Bones.push_back(
                {name, parent ? parent->Name : "", scene.Root->InverseTransformPoint(node->Position()),
                 Mul(Inverse(scene.Root->Rotation()), node->Rotation()), !sharedBones.contains(name)});
            result.Bones.back().Matrix = MultiplyMatrix(node->WorldMatrix(), scene.Root->InverseWorldMatrix());
            result.Bones.back().HasMatrix = true;
        }
        for (const auto &renderer : scene.Renderers)
        {
            CheckCancelled(token);
            if (!renderer->Visible() || !renderer->HasGeometry())
                continue;
            Mesh mesh;
            mesh.Name = renderer->Node->Name + "_" + std::to_string(result.Meshes.size());
            mesh.Role = renderer->Role;
            mesh.ReferenceSlot = renderer->Slot;
            auto &source = *renderer->Mesh;
            auto channel = [&](int semantic)
            {
                return std::any_of(source.Channels.begin(), source.Channels.end(),
                                   [&](auto c)
                                   {
                                       return c.Semantic == semantic;
                                   });
            };
            auto matrices = renderer->Source.Skinned ? renderer->SkinMatrices(scene.Root->InverseWorldMatrix())
                                                     : std::vector<Matrix4x4>{};
            auto vertices = DeformMesh(source, renderer->BlendWeights, matrices);
            mesh.HasUv = channel(4);
            mesh.HasNormals = true;
            for (int i = 0; i < source.VertexCount; ++i)
            {
                Vertex vertex;
                vertex.Position = vertices.Positions.at(i);
                vertex.Normal = vertices.Normals.at(i);
                if (mesh.HasUv)
                    vertex.Uv = {source.Read(4, i, 0), source.Read(4, i, 1)};
                if (channel(5))
                    vertex.Uv1 = {source.Read(5, i, 0), source.Read(5, i, 1)};
                if (channel(12) && channel(13))
                    for (int j = 0; j < 4; ++j)
                    {
                        float weight = source.Read(12, i, j);
                        if (weight <= .00001f)
                            continue;
                        int bone = static_cast<int>(source.Read(13, i, j));
                        Require(bone >= 0 && static_cast<std::size_t>(bone) < renderer->Bones.size() &&
                                    renderer->Bones[bone],
                                "参考网格骨骼无效");
                        vertex.Weights.push_back({renderer->Bones[bone]->Name, weight});
                    }
                mesh.Vertices.push_back(std::move(vertex));
            }
            for (std::size_t s = 0; s < source.Submeshes.size(); ++s)
            {
                const auto &submesh = source.Submeshes[s];
                if (submesh.Topology != 0)
                    continue;
                for (int j = 0; j < submesh.IndexCount; ++j)
                    mesh.Indices.push_back(source.Index(submesh.FirstIndex + j) + submesh.BaseVertex);
                mesh.Materials.insert(mesh.Materials.end(), submesh.IndexCount / 3, static_cast<int>(s));
            }
            if (!renderer->Source.Skinned)
                TransformMesh(mesh, MultiplyMatrix(renderer->Node->WorldMatrix(), scene.Root->InverseWorldMatrix()));
            RebuildNormalsTangents(mesh);
            result.Meshes.push_back(std::move(mesh));
        }
        Require(!result.Meshes.empty() && !result.Bones.empty(), "人物参考缺少网格或骨架");
        return result;
    }

    Json GeometryJson(const Geometry &g)
    {
        Json result = {{"schema_version", SchemaVersion},
                       {"coordinates", {{"handedness", "left"}, {"up", "Y"}, {"meters_per_unit", .1}}},
                       {"meshes", Json::array()},
                       {"bones", Json::array()},
                       {"warnings", g.Warnings},
                       {"input_hashes", g.InputHashes}};
        for (const auto &bone : g.Bones)
        {
            auto matrix = BoneWorld(bone);
            Json values = Json::array();
            for (int r = 0; r < 4; ++r)
                for (int c = 0; c < 4; ++c)
                    values.push_back(matrix.m[r][c]);
            result["bones"].push_back({{"name", bone.Name},
                                       {"parent", bone.Parent},
                                       {"position", VectorJson(bone.Position)},
                                       {"rotation", VectorJson(bone.Rotation)},
                                       {"added", bone.Added},
                                       {"matrix", values}});
        }
        for (const auto &mesh : g.Meshes)
        {
            Json m = {{"name", mesh.Name},        {"role", mesh.Role},           {"reference_slot", mesh.ReferenceSlot},
                      {"indices", mesh.Indices},  {"materials", mesh.Materials}, {"positions", Json::array()},
                      {"normals", Json::array()}, {"tangents", Json::array()},   {"uv", Json::array()},
                      {"uv1", Json::array()},     {"colors", Json::array()},     {"weights", Json::array()},
                      {"shapes", Json::array()}};
            for (const auto &v : mesh.Vertices)
            {
                m["positions"].push_back(VectorJson(v.Position));
                if (mesh.HasNormals)
                    m["normals"].push_back(VectorJson(v.Normal));
                if (mesh.HasTangents)
                    m["tangents"].push_back(VectorJson(v.Tangent));
                if (mesh.HasUv)
                    m["uv"].push_back(VectorJson(v.Uv));
                m["uv1"].push_back(VectorJson(v.Uv1));
                m["colors"].push_back(VectorJson(v.Color));
                Json weights = Json::array();
                for (auto &w : v.Weights)
                    weights.push_back({{"bone", w.Bone}, {"weight", w.Weight}});
                m["weights"].push_back(std::move(weights));
            }
            for (const auto &shape : mesh.Shapes)
            {
                Json s = {{"name", shape.Name},
                          {"weight", shape.Weight},
                          {"positions", Json::array()},
                          {"normals", Json::array()},
                          {"tangents", Json::array()}};
                for (auto v : shape.Positions)
                    s["positions"].push_back(VectorJson(v));
                for (auto v : shape.Normals)
                    s["normals"].push_back(VectorJson(v));
                for (auto v : shape.Tangents)
                    s["tangents"].push_back(VectorJson(v));
                m["shapes"].push_back(std::move(s));
            }
            result["meshes"].push_back(std::move(m));
        }
        return result;
    }

    Geometry GeometryOf(const Json &json)
    {
        Geometry result;
        result.Warnings = json.value("warnings", std::vector<std::string>{});
        result.InputHashes = json.value("input_hashes", std::vector<std::string>{});
        Require(json.value("meshes", Json::array()).size() <= 1024 && json.value("bones", Json::array()).size() <= 8192,
                "输入网格或骨骼数量超过限制");
        std::size_t vertices = 0, indices = 0, shapeVertices = 0;
        for (const auto &b : json.value("bones", Json::array()))
        {
            result.Bones.push_back({b.at("name"), b.value("parent", ""), Vector3Of(b.at("position")),
                                    Normalize(Vector4Of(b.value("rotation", Json::array({0, 0, 0, 1})))),
                                    b.value("added", true)});
            if (b.contains("matrix"))
            {
                VectorSize(b.at("matrix"), 16);
                auto &bone = result.Bones.back();
                bone.HasMatrix = true;
                for (int r = 0; r < 4; ++r)
                    for (int c = 0; c < 4; ++c)
                        bone.Matrix.m[r][c] = b.at("matrix")[r * 4 + c];
                auto determinant =
                    DirectX::XMVectorGetX(DirectX::XMMatrixDeterminant(DirectX::XMLoadFloat4x4(&bone.Matrix)));
                Require(std::isfinite(determinant) && std::abs(determinant) > 1e-12f, "骨骼矩阵不可逆");
            }
        }
        for (const auto &m : json.value("meshes", Json::array()))
        {
            Mesh mesh;
            mesh.Name = m.at("name");
            mesh.Role = m.value("role", "");
            mesh.ReferenceSlot = m.value("reference_slot", -1);
            vertices += m.at("positions").size();
            indices += m.at("indices").size();
            Require(vertices <= 1000000 && indices <= 750000, "网格顶点或索引数量超过限制");
            mesh.Indices = m.at("indices").get<std::vector<std::uint32_t>>();
            mesh.Materials = m.value("materials", std::vector<int>{});
            if (mesh.Materials.empty())
                mesh.Materials.resize(mesh.Indices.size() / 3);
            mesh.HasUv = m.contains("uv") && !m["uv"].empty();
            mesh.HasNormals = m.contains("normals") && !m["normals"].empty();
            mesh.HasTangents = m.contains("tangents") && !m["tangents"].empty();
            for (std::size_t i = 0; i < m.at("positions").size(); ++i)
            {
                Vertex v;
                v.Position = Vector3Of(m["positions"][i]);
                if (mesh.HasUv)
                    v.Uv = Vector2Of(m.at("uv").at(i));
                if (mesh.HasNormals)
                    v.Normal = Vector3Of(m.at("normals").at(i));
                if (mesh.HasTangents)
                    v.Tangent = Vector4Of(m.at("tangents").at(i));
                if (m.contains("uv1") && !m["uv1"].empty())
                    v.Uv1 = Vector2Of(m.at("uv1").at(i));
                if (m.contains("colors") && !m["colors"].empty())
                    v.Color = Vector4Of(m.at("colors").at(i));
                if (m.contains("weights") && !m["weights"].empty())
                    for (const auto &w : m.at("weights").at(i))
                        v.Weights.push_back({w.at("bone"), w.at("weight")});
                mesh.Vertices.push_back(std::move(v));
            }
            for (const auto &s : m.value("shapes", Json::array()))
            {
                shapeVertices += s.at("positions").size();
                Require(shapeVertices <= 8000000, "形变数据超过预算");
                Shape shape;
                shape.Name = s.at("name");
                shape.Weight = s.value("weight", 100.f);
                for (const auto &v : s.at("positions"))
                    shape.Positions.push_back(Vector3Of(v));
                for (const auto &v : s.value("normals", Json::array()))
                    shape.Normals.push_back(Vector3Of(v));
                for (const auto &v : s.value("tangents", Json::array()))
                    shape.Tangents.push_back(Vector3Of(v));
                mesh.Shapes.push_back(std::move(shape));
            }
            result.Meshes.push_back(std::move(mesh));
        }
        return result;
    }

    Json WriteReference(const Geometry &g, const fs::path &output, std::stop_token token)
    {
        fs::create_directories(output);
        auto json = GeometryJson(g);
        WriteJson(output / "reference-scene.json", json);
        Json result = {{"schema_version", SchemaVersion},
                       {"reference_scene", Utf8(output / "reference-scene.json")},
                       {"coordinates", json["coordinates"]},
                       {"images", Json::array()},
                       {"bones", Json::array()},
                       {"bone_definitions", json["bones"]},
                       {"bounds", Json::object()}};
        for (const auto &bone : g.Bones)
            result["bones"].push_back(bone.Name);
        constexpr int size = 1024;
        for (std::size_t i = 0; i < g.Meshes.size(); ++i)
        {
            CheckCancelled(token);
            const auto &mesh = g.Meshes[i];
            result["bounds"][mesh.Name] = {
                {"minimum", VectorJson(Low(mesh))}, {"maximum", VectorJson(High(mesh))}, {"role", mesh.Role}};
            if (!mesh.HasUv)
                continue;
            Bytes pixels(size * size * 4);
            for (std::size_t t = 0; t + 2 < mesh.Indices.size(); t += 3)
                for (int edge = 0; edge < 3; ++edge)
                {
                    auto a = mesh.Vertices.at(mesh.Indices[t + edge]).Uv,
                         b = mesh.Vertices.at(mesh.Indices[t + (edge + 1) % 3]).Uv;
                    float x0 = a.x * (size - 1), y0 = (1 - a.y) * (size - 1), x1 = b.x * (size - 1),
                          y1 = (1 - b.y) * (size - 1);
                    int steps = static_cast<int>(
                        std::min(float(size * 4), std::max(1.f, std::max(std::abs(x1 - x0), std::abs(y1 - y0)))));
                    for (int j = 0; j <= steps; ++j)
                    {
                        float f = float(j) / steps;
                        auto x = std::round(x0 + (x1 - x0) * f), y = std::round(y0 + (y1 - y0) * f);
                        if (x >= 0 && x < size && y >= 0 && y < size)
                            std::fill_n(pixels.begin() + (static_cast<int>(y) * size + static_cast<int>(x)) * 4, 4,
                                        std::uint8_t{255});
                    }
                }
            auto path = output / Path("uv-" + std::to_string(i) + ".png");
            SavePng(path, size, size, pixels);
            result["images"].push_back(
                {{"path", Utf8(path)}, {"mime_type", "image/png"}, {"mesh", mesh.Name}, {"role", mesh.Role}});
        }
        return result;
    }

    void AdaptGeometry(Geometry &g, const Geometry &reference, const Json &spec, std::stop_token token)
    {
        const std::string kind = spec.at("kind");
        std::map<std::string, Bone> bones;
        for (const auto &bone : reference.Bones)
            bones.emplace(bone.Name, bone);
        std::map<std::string, Bone> incoming;
        std::set<std::string> requested, visiting;
        for (const auto &bone : g.Bones)
        {
            Require(incoming.emplace(bone.Name, bone).second, "输入骨架名称重复");
            if (bone.Added)
                requested.insert(bone.Name);
        }
        for (const auto &mesh : g.Meshes)
            for (const auto &vertex : mesh.Vertices)
                for (const auto &weight : vertex.Weights)
                    if (weight.Weight > 0)
                        requested.insert(weight.Bone);
        for (const auto &root : spec.value("dynamic_roots", Json::array()))
        {
            auto name = root.get<std::string>();
            requested.insert(name);
            for (const auto &[child, bone] : incoming)
            {
                (void)bone;
                auto p = child;
                std::set<std::string> chain;
                while (incoming.contains(p) && p != name)
                {
                    Require(chain.insert(p).second, "输入骨骼层级存在循环");
                    p = incoming.at(p).Parent;
                }
                if (p == name)
                    requested.insert(child);
            }
        }
        std::function<void(const std::string &)> include = [&](const std::string &name)
        {
            // Rebuilding against a preview of the previous package also exposes authored
            // bones as reference nodes. Keep their authored bind pose and Added marker.
            if (bones.contains(name) && (!incoming.contains(name) || !incoming.at(name).Added))
                return;
            Require(incoming.contains(name), "顶点引用不存在的目标骨骼");
            Require(visiting.insert(name).second, "输入骨骼层级存在循环");
            auto bone = incoming.at(name);
            bone.Added = true;
            Require(!bone.Parent.empty(), "新增骨骼必须连接到人物参考骨架");
            include(bone.Parent);
            bones.insert_or_assign(name, std::move(bone));
            visiting.erase(name);
        };
        if (kind != "accessory")
            for (const auto &name : requested)
                include(name);
        std::set<std::string> allowed;
        for (const auto &[name, bone] : bones)
        {
            Require(!name.empty() && (bone.Parent.empty() || bones.contains(bone.Parent)), "骨骼名称或父级无效");
            std::set<std::string> ancestors;
            auto current = name;
            while (!current.empty())
            {
                Require(ancestors.insert(current).second, "骨骼层级存在循环");
                current = bones.at(current).Parent;
            }
            allowed.insert(name);
        }
        std::string head;
        for (auto name : {"cf_J_Head", "cm_J_Head", "cf_J_Head_s"})
            if (bones.contains(name))
            {
                head = name;
                break;
            }
        std::unique_ptr<Surface> surface;
        std::size_t triangles = 0, vertices = 0;
        auto maxTriangles = spec.value("max_triangles", 250000);
        Require(maxTriangles > 0 && maxTriangles <= 250000, "max_triangles 必须在 1–250000 范围内");
        std::set<std::string> meshNames;
        for (auto &mesh : g.Meshes)
        {
            CheckCancelled(token);
            Require(!mesh.Name.empty() && meshNames.insert(mesh.Name).second, "网格名称为空或重复");
            Require(mesh.HasUv, "网格缺少 UV");
            Require(!mesh.Vertices.empty() && !mesh.Indices.empty() && mesh.Indices.size() % 3 == 0 &&
                        mesh.Materials.size() == mesh.Indices.size() / 3,
                    "网格为空或三角形／材质槽数量无效");
            for (auto index : mesh.Indices)
                Require(index < mesh.Vertices.size(), "网格索引越界");
            for (int material : mesh.Materials)
                Require(material >= 0 && material < 64, "材质槽超出范围");
            triangles += mesh.Indices.size() / 3;
            vertices += mesh.Vertices.size();
            Require(triangles <= static_cast<std::size_t>(maxTriangles) && vertices <= 1000000,
                    "素材超过面数或顶点数预算");
            TransformMesh(mesh, TransformOf(spec));
            for (std::size_t i = 0; i < mesh.Indices.size(); i += 3)
            {
                auto a = mesh.Vertices[mesh.Indices[i]].Position, b = mesh.Vertices[mesh.Indices[i + 1]].Position,
                     c = mesh.Vertices[mesh.Indices[i + 2]].Position;
                Require(LengthSquared(Cross(b - a, c - a)) > 1e-20f, "网格包含零面积三角形");
            }
            for (auto &vertex : mesh.Vertices)
            {
                CheckCancelled(token);
                for (float f : {vertex.Position.x, vertex.Position.y, vertex.Position.z, vertex.Uv.x, vertex.Uv.y})
                    Require(std::isfinite(f) && std::abs(f) <= 1000000, "网格坐标或 UV 无效");
                if (kind != "accessory")
                {
                    if (vertex.Weights.empty())
                    {
                        if (kind == "hair")
                        {
                            Require(!head.empty(), "参考骨架缺少头骨");
                            vertex.Weights.push_back({head, 1});
                        }
                        else
                        {
                            if (!surface)
                                surface = std::make_unique<Surface>(reference);
                            surface->Transfer(vertex);
                        }
                    }
                    NormalizeWeights(vertex, allowed);
                }
                else
                    vertex.Weights.clear();
            }
            for (const auto &shape : mesh.Shapes)
                Require(!shape.Name.empty() && std::isfinite(shape.Weight) && shape.Weight > 0 &&
                            shape.Positions.size() == mesh.Vertices.size() &&
                            (shape.Normals.empty() || shape.Normals.size() == mesh.Vertices.size()) &&
                            (shape.Tangents.empty() || shape.Tangents.size() == mesh.Vertices.size()),
                        "形变通道尺寸无效");
            RebuildNormalsTangents(mesh);
        }
        Require(triangles > 0, "素材没有三角形");
        g.Bones.clear();
        for (auto &[_, bone] : bones)
            g.Bones.push_back(std::move(bone));
        for (const auto &root : spec.value("dynamic_roots", Json::array()))
            Require(allowed.contains(root.get<std::string>()), "动态根引用不存在的骨骼");
    }

    Json GeometryReport(const Geometry &g)
    {
        std::size_t vertices = 0, triangles = 0;
        Json bounds = Json::object();
        for (const auto &mesh : g.Meshes)
        {
            vertices += mesh.Vertices.size();
            triangles += mesh.Indices.size() / 3;
            bounds[mesh.Name] = {{"minimum", VectorJson(Low(mesh))}, {"maximum", VectorJson(High(mesh))}};
        }
        return {{"valid", true},
                {"vertices", vertices},
                {"triangles", triangles},
                {"bounds", bounds},
                {"warnings", g.Warnings}};
    }
}
