#include "MeshDocument.h"
#include "Animation/SimulationMath.h"
#include <algorithm>
#include <numeric>
#include <queue>

namespace hs2::creation
{
    using namespace sim;
    using asset::Vector2Of;
    using asset::Vector3Of;
    using asset::Vector4Of;
    using asset::VectorJson;

    bool Finite(Vector3 v)
    {
        return std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z);
    }

    namespace
    {
        void Require(bool value, const char *message)
        {
            if (!value)
                throw std::invalid_argument(message);
        }

        Json VertexJson(const MeshVertex &v)
        {
            Json weights = Json::array(), shapes = Json::object();
            for (const auto &w : v.Weights)
                weights.push_back({w.Bone, w.Weight});
            for (const auto &[name, d] : v.Shapes)
                shapes[name] = {VectorJson(d.Position), VectorJson(d.Normal), VectorJson(d.Tangent)};
            return {{"position", VectorJson(v.Position)},
                    {"color", VectorJson(v.Color)},
                    {"weights", weights},
                    {"shapes", shapes},
                    {"mask", v.Mask}};
        }
    }

    MeshDocument MeshDocument::FromMesh(const asset::Mesh &source)
    {
        MeshDocument mesh;
        mesh.Name = source.Name;
        mesh.Role = source.Role;
        std::vector<ElementId> ids;
        std::map<std::string, ElementId> identical;
        for (std::size_t i = 0; i < source.Vertices.size(); ++i)
        {
            const auto &v = source.Vertices[i];
            MeshVertex point{v.Position, v.Color, v.Weights};
            for (const auto &shape : source.Shapes)
            {
                // 多帧形变使用名称和权重共同标识，导出时恢复原始通道。
                auto key = shape.Name + "\x1f" + std::to_string(shape.Weight);
                mesh.ShapeWeights[key] = shape.Weight;
                point.Shapes[key] = {shape.Positions.at(i), shape.Normals.empty() ? Vector3{} : shape.Normals.at(i),
                                     shape.Tangents.empty() ? Vector3{} : shape.Tangents.at(i)};
            }
            auto key = VertexJson(point).dump();
            auto [it, added] = identical.emplace(key, mesh.NextId);
            if (added)
                mesh.Vertices.emplace(mesh.NextId++, std::move(point));
            ids.push_back(it->second);
        }
        for (std::size_t i = 0; i < source.Indices.size(); i += 3)
        {
            std::vector<MeshCorner> corners;
            for (std::size_t j = 0; j < 3; ++j)
            {
                auto at = source.Indices.at(i + j);
                const auto &v = source.Vertices.at(at);
                corners.push_back({ids.at(at), v.Uv, v.Uv1, v.Normal});
            }
            if (corners[0].Vertex != corners[1].Vertex && corners[1].Vertex != corners[2].Vertex &&
                corners[2].Vertex != corners[0].Vertex)
                mesh.AddFace(std::move(corners), source.Materials.empty() ? 0 : source.Materials.at(i / 3));
        }
        mesh.RebuildEdges();
        // 导入时识别已有面角 UV 的断开边，不把 UV 接缝误焊成同一岛。
        std::map<std::pair<ElementId, ElementId>, std::pair<Vector2, Vector2>> uv;
        for (const auto &[id, face] : mesh.Faces)
            for (std::size_t i = 0; i < face.Corners.size(); ++i)
            {
                auto a = face.Corners[i], b = face.Corners[(i + 1) % face.Corners.size()];
                if (a.Vertex > b.Vertex)
                    std::swap(a, b);
                auto key = std::pair{a.Vertex, b.Vertex};
                auto [it, inserted] = uv.emplace(key, std::pair{a.Uv, b.Uv});
                if (!inserted)
                {
                    auto [x, y] = it->second;
                    if (std::abs(x.x - a.Uv.x) + std::abs(x.y - a.Uv.y) + std::abs(y.x - b.Uv.x) +
                            std::abs(y.y - b.Uv.y) >
                        1e-5f)
                        mesh.Edges.at(key).Seam = true;
                }
                (void)id;
            }
        mesh.Validate();
        return mesh;
    }

    Json MeshDocument::ToJson() const
    {
        Json vertices = Json::array(), faces = Json::array(), edges = Json::array();
        for (const auto &[id, vertex] : Vertices)
            vertices.push_back({id, VertexJson(vertex)});
        for (const auto &[id, face] : Faces)
        {
            Json corners = Json::array();
            for (const auto &c : face.Corners)
                corners.push_back({c.Vertex, VectorJson(c.Uv), VectorJson(c.Uv1), VectorJson(c.Normal)});
            faces.push_back({id, face.Material, face.Smooth, corners});
        }
        for (const auto &[key, edge] : Edges)
        {
            edges.push_back({edge.Id, edge.A, edge.B, edge.Seam});
            (void)key;
        }
        return {{"version", 1},         {"name", Name},   {"role", Role},   {"next_id", NextId},
                {"vertices", vertices}, {"faces", faces}, {"edges", edges}, {"shape_weights", ShapeWeights}};
    }

    MeshDocument MeshDocument::FromJson(const Json &json)
    {
        Require(json.at("version") == 1, "不支持的网格工程版本");
        MeshDocument result;
        result.Name = json.at("name");
        result.Role = json.value("role", "");
        result.NextId = json.at("next_id");
        result.ShapeWeights = json.value("shape_weights", std::map<std::string, float>{});
        for (const auto &row : json.at("vertices"))
        {
            const auto &j = row.at(1);
            MeshVertex v;
            v.Position = Vector3Of(j.at("position"));
            v.Color = Vector4Of(j.at("color"));
            v.Mask = j.value("mask", 0.f);
            for (const auto &w : j.at("weights"))
                v.Weights.push_back({w.at(0), w.at(1)});
            for (const auto &[name, shape] : j.at("shapes").items())
                v.Shapes[name] = {Vector3Of(shape.at(0)), Vector3Of(shape.at(1)), Vector3Of(shape.at(2))};
            Require(result.Vertices.emplace(row.at(0).get<ElementId>(), std::move(v)).second, "重复的顶点编号");
        }
        for (const auto &row : json.at("faces"))
        {
            MeshFace f;
            f.Material = row.at(1);
            f.Smooth = row.at(2);
            for (const auto &c : row.at(3))
                f.Corners.push_back({c.at(0), Vector2Of(c.at(1)), Vector2Of(c.at(2)), Vector3Of(c.at(3))});
            Require(result.Faces.emplace(row.at(0).get<ElementId>(), std::move(f)).second, "重复的面编号");
        }
        for (const auto &row : json.at("edges"))
        {
            MeshEdge edge{row.at(0), row.at(1), row.at(2), row.at(3)};
            Require(edge.A < edge.B && result.Edges.emplace(std::pair{edge.A, edge.B}, edge).second,
                    "重复或无效的边编号");
        }
        result.Validate();
        return result;
    }

    ElementId MeshDocument::AddFace(std::vector<MeshCorner> corners, int material, bool smooth)
    {
        auto id = NextId++;
        Faces.emplace(id, MeshFace{std::move(corners), material, smooth});
        return id;
    }

    void MeshDocument::RebuildEdges()
    {
        std::map<std::pair<ElementId, ElementId>, MeshEdge> next;
        for (const auto &[id, face] : Faces)
            for (std::size_t i = 0; i < face.Corners.size(); ++i)
            {
                auto a = face.Corners[i].Vertex, b = face.Corners[(i + 1) % face.Corners.size()].Vertex;
                if (a > b)
                    std::swap(a, b);
                auto key = std::pair{a, b};
                if (a == b || next.contains(key))
                    continue;
                auto previous = Edges.find(key);
                next.emplace(key, previous == Edges.end() ? MeshEdge{NextId++, a, b, false} : previous->second);
                (void)id;
            }
        Edges = std::move(next);
    }

    void MeshDocument::Validate() const
    {
        Require(Vertices.size() <= 1000000, "网格超过一百万顶点");
        std::set<ElementId> ids;
        auto unique = [&](ElementId id)
        {
            Require(id > 0 && id < NextId && ids.insert(id).second, "网格元素编号无效或重复");
        };
        for (const auto &[id, v] : Vertices)
        {
            unique(id);
            Require(Finite(v.Position) && std::isfinite(v.Mask) && v.Mask >= 0 && v.Mask <= 1, "网格含非法坐标或遮罩");
            Require(Finite({v.Color.x, v.Color.y, v.Color.z}) && std::isfinite(v.Color.w), "网格顶点颜色无效");
            for (const auto &w : v.Weights)
                Require(!w.Bone.empty() && std::isfinite(w.Weight) && w.Weight >= 0, "网格权重无效");
            for (const auto &[name, shape] : v.Shapes)
                Require(ShapeWeights.contains(name) && Finite(shape.Position) && Finite(shape.Normal) &&
                            Finite(shape.Tangent),
                        "网格形变无效");
        }
        std::size_t triangles = 0;
        std::set<std::pair<ElementId, ElementId>> expected;
        for (const auto &[id, f] : Faces)
        {
            unique(id);
            Require(f.Corners.size() >= 3 && f.Corners.size() <= 4096 && f.Material >= 0, "多边形无效");
            std::set<ElementId> vertices;
            triangles += f.Corners.size() - 2;
            for (std::size_t i = 0; i < f.Corners.size(); ++i)
            {
                const auto &c = f.Corners[i];
                Require(Vertices.contains(c.Vertex) && vertices.insert(c.Vertex).second, "多边形顶点无效或重复");
                Require(std::isfinite(c.Uv.x) && std::isfinite(c.Uv.y) && std::isfinite(c.Uv1.x) &&
                            std::isfinite(c.Uv1.y) && Finite(c.Normal),
                        "面角属性无效");
                expected.insert(std::minmax(c.Vertex, f.Corners[(i + 1) % f.Corners.size()].Vertex));
            }
        }
        Require(triangles <= 250000, "网格超过二十五万三角面");
        Require(expected.size() == Edges.size(), "网格边表与拓扑不一致");
        for (const auto &[key, edge] : Edges)
        {
            unique(edge.Id);
            Require(expected.contains(key) && key.first == edge.A && key.second == edge.B, "网格边引用无效");
        }
    }

    Vector3 MeshDocument::FaceNormal(ElementId id) const
    {
        const auto &f = Faces.at(id);
        Vector3 normal{};
        for (std::size_t i = 0; i < f.Corners.size(); ++i)
        {
            auto a = Vertices.at(f.Corners[i].Vertex).Position;
            auto b = Vertices.at(f.Corners[(i + 1) % f.Corners.size()].Vertex).Position;
            normal += Cross(a, b);
        }
        return Unit(normal, {0, 1, 0});
    }

    std::vector<std::array<std::size_t, 3>> Triangles(const MeshDocument &mesh, const MeshFace &face)
    {
        std::vector<std::array<std::size_t, 3>> result;
        std::vector<std::size_t> remaining(face.Corners.size());
        std::iota(remaining.begin(), remaining.end(), 0);
        Vector3 normal{};
        for (std::size_t i = 0; i < remaining.size(); ++i)
            normal += Cross(mesh.Vertices.at(face.Corners[i].Vertex).Position,
                            mesh.Vertices.at(face.Corners[(i + 1) % remaining.size()].Vertex).Position);
        Require(LengthSquared(normal) > 1e-18f, "零面积多边形无法三角化，请删除或修复");
        normal = Unit(normal);
        auto point = [&](std::size_t i)
        {
            return mesh.Vertices.at(face.Corners[i].Vertex).Position;
        };
        while (remaining.size() > 3)
        {
            bool clipped = false;
            for (std::size_t i = 0; i < remaining.size(); ++i)
            {
                auto a = remaining[(i + remaining.size() - 1) % remaining.size()], b = remaining[i],
                     c = remaining[(i + 1) % remaining.size()];
                auto pa = point(a), pb = point(b), pc = point(c);
                if (Dot(Cross(pb - pa, pc - pb), normal) <= 1e-10f)
                    continue;
                bool inside = false;
                for (auto k : remaining)
                    if (k != a && k != b && k != c)
                    {
                        auto p = point(k);
                        if (Dot(Cross(pb - pa, p - pa), normal) >= -1e-7f &&
                            Dot(Cross(pc - pb, p - pb), normal) >= -1e-7f &&
                            Dot(Cross(pa - pc, p - pc), normal) >= -1e-7f)
                        {
                            inside = true;
                            break;
                        }
                    }
                if (!inside)
                {
                    result.push_back({a, b, c});
                    remaining.erase(remaining.begin() + i);
                    clipped = true;
                    break;
                }
            }
            Require(clipped, "自交或退化多边形无法三角化，请修复所选面");
        }
        result.push_back({remaining[0], remaining[1], remaining[2]});
        return result;
    }

    asset::Mesh MeshDocument::Triangulate() const
    {
        Validate();
        asset::Mesh result;
        result.Name = Name;
        result.Role = Role;
        result.HasUv = result.HasNormals = true;
        std::map<std::string, std::size_t> shapes;
        for (const auto &[name, weight] : ShapeWeights)
        {
            shapes[name] = result.Shapes.size();
            asset::Shape shape;
            shape.Name = name.substr(0, name.find('\x1f'));
            shape.Weight = weight;
            result.Shapes.push_back(std::move(shape));
        }
        std::map<ElementId, Vector3> normals;
        for (const auto &[id, face] : Faces)
            if (face.Smooth)
                for (const auto &corner : face.Corners)
                    normals[corner.Vertex] += FaceNormal(id);
        for (const auto &[id, face] : Faces)
        {
            auto start = static_cast<std::uint32_t>(result.Vertices.size());
            for (const auto &c : face.Corners)
            {
                const auto &v = Vertices.at(c.Vertex);
                asset::Vertex vertex;
                vertex.Position = v.Position;
                vertex.Normal = LengthSquared(c.Normal) > 1e-10f ? Unit(c.Normal)
                                : face.Smooth                    ? Unit(normals[c.Vertex], FaceNormal(id))
                                                                 : FaceNormal(id);
                vertex.Color = v.Color;
                vertex.Uv = c.Uv;
                vertex.Uv1 = c.Uv1;
                vertex.Weights = v.Weights;
                result.Vertices.push_back(vertex);
                for (const auto &[name, index] : shapes)
                {
                    auto found = v.Shapes.find(name);
                    auto d = found == v.Shapes.end() ? ShapeDelta{} : found->second;
                    result.Shapes[index].Positions.push_back(d.Position);
                    result.Shapes[index].Normals.push_back(d.Normal);
                    result.Shapes[index].Tangents.push_back(d.Tangent);
                }
            }
            for (auto t : Triangles(*this, face))
            {
                for (auto i : t)
                    result.Indices.push_back(start + static_cast<std::uint32_t>(i));
                result.Materials.push_back(face.Material);
            }
        }
        asset::RebuildNormalsTangents(result);
        return result;
    }

    std::set<ElementId> MeshDocument::SelectedVertices(const MeshSelection &selection) const
    {
        std::set<ElementId> result;
        if (selection.Mode == SelectionMode::Object)
            for (const auto &[id, v] : Vertices)
            {
                result.insert(id);
                (void)v;
            }
        else if (selection.Mode == SelectionMode::Vertex)
        {
            for (auto id : selection.Ids)
                if (Vertices.contains(id))
                    result.insert(id);
        }
        else if (selection.Mode == SelectionMode::Edge)
        {
            for (const auto &[key, e] : Edges)
                if (selection.Ids.contains(e.Id))
                {
                    result.insert(e.A);
                    result.insert(e.B);
                    (void)key;
                }
        }
        else
        {
            for (auto id : selection.Ids)
                if (auto f = Faces.find(id); f != Faces.end())
                    for (const auto &c : f->second.Corners)
                        result.insert(c.Vertex);
        }
        return result;
    }

    std::set<ElementId> MeshDocument::SelectedFaces(const MeshSelection &selection) const
    {
        std::set<ElementId> result;
        auto vertices = SelectedVertices(selection);
        for (const auto &[id, f] : Faces)
            if (selection.Mode == SelectionMode::Object ||
                ((selection.Mode == SelectionMode::Face || selection.Mode == SelectionMode::Uv) &&
                 selection.Ids.contains(id)) ||
                ((selection.Mode == SelectionMode::Vertex || selection.Mode == SelectionMode::Edge) &&
                 std::all_of(f.Corners.begin(), f.Corners.end(),
                             [&](const auto &c)
                             {
                                 return vertices.contains(c.Vertex);
                             })))
                result.insert(id);
        return result;
    }

    Vector3 MeshDocument::Center(const MeshSelection &selection) const
    {
        auto ids = SelectedVertices(selection);
        Vector3 center{};
        for (auto id : ids)
            center += Vertices.at(id).Position;
        return ids.empty() ? center : center / static_cast<float>(ids.size());
    }

    std::vector<std::vector<ElementId>> MeshDocument::UvIslands() const
    {
        std::map<std::pair<ElementId, ElementId>, std::vector<ElementId>> adjacent;
        for (const auto &[id, f] : Faces)
            for (std::size_t i = 0; i < f.Corners.size(); ++i)
                adjacent[std::minmax(f.Corners[i].Vertex, f.Corners[(i + 1) % f.Corners.size()].Vertex)].push_back(id);
        std::map<ElementId, std::vector<ElementId>> neighbors;
        for (const auto &[key, faces] : adjacent)
            if (!Edges.at(key).Seam && faces.size() == 2)
            {
                neighbors[faces[0]].push_back(faces[1]);
                neighbors[faces[1]].push_back(faces[0]);
            }
        std::set<ElementId> seen;
        std::vector<std::vector<ElementId>> islands;
        for (const auto &[id, f] : Faces)
            if (seen.insert(id).second)
            {
                std::vector<ElementId> island{id};
                for (std::size_t i = 0; i < island.size(); ++i)
                    for (auto n : neighbors[island[i]])
                        if (seen.insert(n).second)
                            island.push_back(n);
                islands.push_back(std::move(island));
                (void)f;
            }
        return islands;
    }

    std::optional<MeshHit> MeshDocument::Pick(Vector3 origin, Vector3 direction, bool backfaces) const
    {
        std::optional<MeshHit> best;
        for (const auto &[id, face] : Faces)
            for (auto t : Triangles(*this, face))
            {
                auto ca = face.Corners[t[0]], cb = face.Corners[t[1]], cc = face.Corners[t[2]];
                auto a = Vertices.at(ca.Vertex).Position, b = Vertices.at(cb.Vertex).Position,
                     c = Vertices.at(cc.Vertex).Position;
                auto ab = b - a, ac = c - a, p = Cross(direction, ac);
                float det = Dot(ab, p);
                if (backfaces ? std::abs(det) < 1e-8f : det < 1e-8f)
                    continue;
                float u = Dot(origin - a, p) / det;
                auto q = Cross(origin - a, ab);
                float v = Dot(direction, q) / det, distance = Dot(ac, q) / det;
                if (u < 0 || v < 0 || u + v > 1 || distance < 0 || (best && distance >= best->Distance))
                    continue;
                best = MeshHit{id,
                               {ca.Vertex, cb.Vertex, cc.Vertex},
                               origin + direction * distance,
                               Unit(Cross(ab, ac)),
                               {1 - u - v, u, v},
                               {ca.Uv.x * (1 - u - v) + cb.Uv.x * u + cc.Uv.x * v,
                                ca.Uv.y * (1 - u - v) + cb.Uv.y * u + cc.Uv.y * v},
                               distance};
            }
        return best;
    }

    ElementId MeshDocument::Interpolate(std::span<const std::pair<ElementId, float>> sources)
    {
        MeshVertex result;
        result.Color = {};
        std::map<std::string, float> weights;
        for (const auto &[id, fraction] : sources)
        {
            const auto &v = Vertices.at(id);
            result.Position += v.Position * fraction;
            result.Color.x += v.Color.x * fraction;
            result.Color.y += v.Color.y * fraction;
            result.Color.z += v.Color.z * fraction;
            result.Color.w += v.Color.w * fraction;
            result.Mask += v.Mask * fraction;
            for (const auto &w : v.Weights)
                weights[w.Bone] += w.Weight * fraction;
            for (const auto &[name, d] : v.Shapes)
            {
                auto &s = result.Shapes[name];
                s.Position += d.Position * fraction;
                s.Normal += d.Normal * fraction;
                s.Tangent += d.Tangent * fraction;
            }
        }
        for (const auto &[name, weight] : weights)
            if (weight > 1e-8f)
                result.Weights.push_back({name, weight});
        auto id = NextId++;
        Vertices.emplace(id, std::move(result));
        return id;
    }

    MeshCorner InterpolateCorner(const MeshCorner &a, const MeshCorner &b, ElementId vertex, float t)
    {
        return {vertex,
                {a.Uv.x * (1 - t) + b.Uv.x * t, a.Uv.y * (1 - t) + b.Uv.y * t},
                {a.Uv1.x * (1 - t) + b.Uv1.x * t, a.Uv1.y * (1 - t) + b.Uv1.y * t},
                Unit(a.Normal * (1 - t) + b.Normal * t)};
    }

    void MeshDocument::RemoveUnused()
    {
        std::set<ElementId> used;
        for (const auto &[id, f] : Faces)
        {
            for (const auto &c : f.Corners)
                used.insert(c.Vertex);
            (void)id;
        }
        std::erase_if(Vertices,
                      [&](const auto &row)
                      {
                          return !used.contains(row.first);
                      });
        RebuildEdges();
    }

    MeshDocument MeshDocument::Extract(const MeshSelection &selection) const
    {
        auto result = *this;
        auto faces = SelectedFaces(selection);
        std::erase_if(result.Faces,
                      [&](const auto &row)
                      {
                          return !faces.contains(row.first);
                      });
        result.RemoveUnused();
        return result;
    }

    void MeshDocument::Append(const MeshDocument &source)
    {
        std::map<ElementId, ElementId> vertices;
        for (const auto &[name, weight] : source.ShapeWeights)
        {
            if (ShapeWeights.contains(name) && ShapeWeights.at(name) != weight)
                throw std::invalid_argument("形变通道权重不一致，不能合并");
            ShapeWeights[name] = weight;
        }
        for (const auto &[id, v] : source.Vertices)
        {
            auto next = NextId++;
            vertices[id] = next;
            Vertices.emplace(next, v);
        }
        for (const auto &[id, f] : source.Faces)
        {
            auto corners = f.Corners;
            for (auto &c : corners)
                c.Vertex = vertices.at(c.Vertex);
            AddFace(std::move(corners), f.Material, f.Smooth);
            (void)id;
        }
        RebuildEdges();
        for (const auto &[key, edge] : source.Edges)
        {
            Edges.at(std::minmax(vertices.at(edge.A), vertices.at(edge.B))).Seam = edge.Seam;
            (void)key;
        }
        Validate();
    }

    void MeshDocument::Execute(const EditCommand &command, const MeshSelection &selection)
    {
        auto next = *this;
        auto &op = command.Operation;
        if (op.starts_with("uv_") || op.starts_with("sculpt_") || op.starts_with("weight_") || op == "transform" ||
            op == "normal" || op == "material" || op == "mask")
            next.Surface(command, selection);
        else
            next.Topology(command, selection);
        next.RebuildEdges();
        next.Validate();
        *this = std::move(next);
    }
}
