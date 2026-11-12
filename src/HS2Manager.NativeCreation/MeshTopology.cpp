#include "MeshDocument.h"
#include "Animation/SimulationMath.h"
#include <algorithm>
#include <array>
#include <numeric>

namespace hs2::creation
{
    using namespace sim;

    namespace
    {
        using EdgeKey = std::pair<ElementId, ElementId>;

        EdgeKey Key(ElementId a, ElementId b)
        {
            return std::minmax(a, b);
        }

        void Require(bool value, const char *message)
        {
            if (!value)
                throw std::invalid_argument(message);
        }

        std::vector<MeshCorner> Walk(const MeshFace &face, std::size_t a, std::size_t b)
        {
            std::vector<MeshCorner> corners;
            for (auto i = a;; i = (i + 1) % face.Corners.size())
            {
                corners.push_back(face.Corners[i]);
                if (i == b)
                    return corners;
            }
        }

        std::map<EdgeKey, ElementId> SplitEdges(MeshDocument &mesh, const std::set<EdgeKey> &edges, float t)
        {
            std::map<EdgeKey, ElementId> midpoints;
            auto previous = mesh.Edges;
            for (auto [a, b] : edges)
            {
                std::array sources{std::pair{a, 1 - t}, std::pair{b, t}};
                midpoints[{a, b}] = mesh.Interpolate(sources);
            }
            for (auto &[id, face] : mesh.Faces)
            {
                std::vector<MeshCorner> corners;
                for (std::size_t i = 0; i < face.Corners.size(); ++i)
                {
                    auto a = face.Corners[i], b = face.Corners[(i + 1) % face.Corners.size()];
                    corners.push_back(a);
                    if (auto found = midpoints.find(Key(a.Vertex, b.Vertex)); found != midpoints.end())
                        corners.push_back(InterpolateCorner(a, b, found->second, a.Vertex < b.Vertex ? t : 1 - t));
                }
                face.Corners = std::move(corners);
                (void)id;
            }
            mesh.RebuildEdges();
            for (const auto &[key, id] : midpoints)
                if (auto old = previous.find(key); old != previous.end() && old->second.Seam)
                {
                    mesh.Edges.at(Key(key.first, id)).Seam = true;
                    mesh.Edges.at(Key(id, key.second)).Seam = true;
                }
            return midpoints;
        }

        void ClearNormals(MeshDocument &mesh)
        {
            for (auto &[id, face] : mesh.Faces)
            {
                for (auto &c : face.Corners)
                    c.Normal = {};
                (void)id;
            }
        }
    }

    void MeshDocument::Topology(const EditCommand &command, const MeshSelection &selection)
    {
        const auto &op = command.Operation;
        const auto &args = command.Arguments;
        auto selected = SelectedFaces(selection);
        auto vertices = SelectedVertices(selection);
        if (op == "delete")
        {
            if (selection.Mode == SelectionMode::Vertex || selection.Mode == SelectionMode::Edge)
                std::erase_if(Faces,
                              [&](const auto &row)
                              {
                                  return std::any_of(row.second.Corners.begin(), row.second.Corners.end(),
                                                     [&](const auto &c)
                                                     {
                                                         return vertices.contains(c.Vertex);
                                                     });
                              });
            else
                for (auto id : selected)
                    Faces.erase(id);
            RemoveUnused();
        }
        else if (op == "extrude" || op == "inset")
        {
            Require(!selected.empty(), "请选择需要挤出或内插的面");
            float distance = args.value("distance", .1f), amount = args.value("amount", .15f);
            Require(std::isfinite(distance) && std::isfinite(amount) && amount > 0 && amount < 1,
                    "挤出距离或内插比例无效");
            if (op == "inset")
            {
                for (auto id : selected)
                {
                    auto face = Faces.at(id);
                    std::vector<std::pair<ElementId, float>> centerSources;
                    for (const auto &c : face.Corners)
                        centerSources.push_back({c.Vertex, 1.f / static_cast<float>(face.Corners.size())});
                    auto center = Interpolate(centerSources);
                    Vector2 uv{}, uv1{};
                    for (const auto &c : face.Corners)
                    {
                        uv.x += c.Uv.x / face.Corners.size();
                        uv.y += c.Uv.y / face.Corners.size();
                        uv1.x += c.Uv1.x / face.Corners.size();
                        uv1.y += c.Uv1.y / face.Corners.size();
                    }
                    MeshCorner middle{center, uv, uv1};
                    std::vector<MeshCorner> inset;
                    for (const auto &c : face.Corners)
                    {
                        std::array sources{std::pair{c.Vertex, 1 - amount}, std::pair{center, amount}};
                        inset.push_back(InterpolateCorner(c, middle, Interpolate(sources), amount));
                    }
                    Faces.at(id).Corners = inset;
                    for (std::size_t i = 0; i < inset.size(); ++i)
                    {
                        auto j = (i + 1) % inset.size();
                        AddFace({face.Corners[i], face.Corners[j], inset[j], inset[i]}, face.Material, face.Smooth);
                    }
                    Vertices.erase(center);
                }
            }
            else
            {
                const auto previousEdges = Edges;

                struct Boundary
                {
                    MeshCorner A, B;
                    int Material;
                };

                std::map<EdgeKey, std::vector<Boundary>> boundaries;
                std::map<ElementId, Vector3> normals;
                std::map<ElementId, ElementId> duplicates;
                for (auto id : selected)
                {
                    const auto &face = Faces.at(id);
                    auto normal = FaceNormal(id);
                    for (std::size_t i = 0; i < face.Corners.size(); ++i)
                    {
                        auto a = face.Corners[i], b = face.Corners[(i + 1) % face.Corners.size()];
                        normals[a.Vertex] += normal;
                        boundaries[Key(a.Vertex, b.Vertex)].push_back({a, b, face.Material});
                    }
                }
                auto direction = args.contains("direction") ? asset::Vector3Of(args.at("direction")) : Vector3{};
                for (const auto &[id, normal] : normals)
                {
                    auto next = NextId++;
                    auto v = Vertices.at(id);
                    v.Position += (LengthSquared(direction) > 0 ? Unit(direction) : Unit(normal)) * distance;
                    Vertices.emplace(next, std::move(v));
                    duplicates[id] = next;
                }
                for (auto id : selected)
                    for (auto &c : Faces.at(id).Corners)
                        c.Vertex = duplicates.at(c.Vertex);
                for (const auto &[key, entries] : boundaries)
                    if (entries.size() == 1)
                    {
                        auto edge = entries[0];
                        auto a = edge.A, b = edge.B;
                        a.Vertex = duplicates.at(a.Vertex);
                        b.Vertex = duplicates.at(b.Vertex);
                        // 新侧面生成独立 UV，旧面角坐标保持不变。
                        auto lowerA = edge.A, lowerB = edge.B;
                        lowerA.Uv = {0, 0};
                        lowerB.Uv = {1, 0};
                        a.Uv = {0, 1};
                        b.Uv = {1, 1};
                        AddFace({lowerA, lowerB, b, a}, edge.Material);
                        (void)key;
                    }
                RemoveUnused();
                for (const auto &[key, edge] : previousEdges)
                    if (edge.Seam && duplicates.contains(key.first) && duplicates.contains(key.second))
                    {
                        auto top = Key(duplicates.at(key.first), duplicates.at(key.second));
                        if (Edges.contains(top))
                            Edges.at(top).Seam = true;
                    }
            }
            ClearNormals(*this);
        }
        else if (op == "subdivide")
        {
            Require(!selected.empty(), "请选择需要细分的面");
            auto original = Faces;
            std::set<EdgeKey> edges;
            for (auto id : selected)
            {
                const auto &f = original.at(id);
                for (std::size_t i = 0; i < f.Corners.size(); ++i)
                    edges.insert(Key(f.Corners[i].Vertex, f.Corners[(i + 1) % f.Corners.size()].Vertex));
            }
            auto middle = SplitEdges(*this, edges, .5f);
            for (auto id : selected)
            {
                const auto &f = original.at(id);
                std::vector<std::pair<ElementId, float>> sources;
                Vector2 uv{}, uv1{};
                for (const auto &c : f.Corners)
                {
                    sources.push_back({c.Vertex, 1.f / static_cast<float>(f.Corners.size())});
                    uv.x += c.Uv.x / f.Corners.size();
                    uv.y += c.Uv.y / f.Corners.size();
                    uv1.x += c.Uv1.x / f.Corners.size();
                    uv1.y += c.Uv1.y / f.Corners.size();
                }
                MeshCorner center{Interpolate(sources), uv, uv1};
                Faces.erase(id);
                for (std::size_t i = 0; i < f.Corners.size(); ++i)
                {
                    const auto &a = f.Corners[(i + f.Corners.size() - 1) % f.Corners.size()];
                    const auto &b = f.Corners[i];
                    const auto &c = f.Corners[(i + 1) % f.Corners.size()];
                    AddFace({b, InterpolateCorner(b, c, middle.at(Key(b.Vertex, c.Vertex))), center,
                             InterpolateCorner(a, b, middle.at(Key(a.Vertex, b.Vertex)))},
                            f.Material, f.Smooth);
                }
            }
            ClearNormals(*this);
        }
        else if (op == "split" || op == "loop_cut")
        {
            float ratio = args.value("ratio", .5f);
            Require(std::isfinite(ratio) && ratio > .001f && ratio < .999f, "切分比例必须介于 0 和 1");
            std::set<EdgeKey> edges;
            for (const auto &[key, edge] : Edges)
                if (selection.Mode == SelectionMode::Object ||
                    (selection.Mode == SelectionMode::Edge && selection.Ids.contains(edge.Id)))
                    edges.insert(key);
            Require(!edges.empty(), "请选择需要切分的边");
            auto original = Faces;
            if (op == "loop_cut")
            {
                // 沿四边面传播到对边，遇到非四边面停止，不破坏三角形或已有接缝。
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    for (const auto &[id, f] : original)
                        if (f.Corners.size() == 4)
                            for (std::size_t i = 0; i < 4; ++i)
                                if (edges.contains(Key(f.Corners[i].Vertex, f.Corners[(i + 1) % 4].Vertex)))
                                    changed |=
                                        edges.insert(Key(f.Corners[(i + 2) % 4].Vertex, f.Corners[(i + 3) % 4].Vertex))
                                            .second;
                }
            }
            auto midpoints = SplitEdges(*this, edges, ratio);
            if (op == "loop_cut")
                for (const auto &[id, f] : original)
                {
                    if (f.Corners.size() != 4)
                        continue;
                    std::vector<ElementId> mids;
                    for (std::size_t i = 0; i < 4; ++i)
                        if (auto it = midpoints.find(Key(f.Corners[i].Vertex, f.Corners[(i + 1) % 4].Vertex));
                            it != midpoints.end())
                            mids.push_back(it->second);
                    Require(mids.size() <= 2, "环切存在交叉，请一次只选择同一边环");
                    if (mids.size() != 2)
                        continue;
                    auto split = Faces.at(id);
                    std::size_t a = 0, b = 0;
                    for (std::size_t i = 0; i < split.Corners.size(); ++i)
                    {
                        if (split.Corners[i].Vertex == mids[0])
                            a = i;
                        if (split.Corners[i].Vertex == mids[1])
                            b = i;
                    }
                    Faces.at(id).Corners = Walk(split, a, b);
                    AddFace(Walk(split, b, a), f.Material, f.Smooth);
                }
        }
        else if (op == "cut")
        {
            Require(!selected.empty(), "请选择需要切开的多边形");
            for (auto id : selected)
            {
                auto f = Faces.at(id);
                Require(f.Corners.size() >= 4, "三角形请先切分边，再连接顶点");
                std::size_t a = args.value("corner_a", std::size_t(0));
                std::size_t b = args.value("corner_b", f.Corners.size() / 2);
                Require(a < f.Corners.size() && b < f.Corners.size() && a != b && (a + 1) % f.Corners.size() != b &&
                            (b + 1) % f.Corners.size() != a,
                        "请选择两个不相邻的面角");
                Faces.at(id).Corners = Walk(f, a, b);
                AddFace(Walk(f, b, a), f.Material, f.Smooth);
            }
        }
        else if (op == "dissolve")
        {
            Require(selection.Mode == SelectionMode::Edge, "溶解操作需要选择边");
            std::vector<EdgeKey> selectedEdges;
            for (const auto &[key, edge] : Edges)
                if (selection.Ids.contains(edge.Id))
                {
                    Require(!edge.Seam, "UV 接缝不能直接溶解，请先取消接缝并统一 UV");
                    selectedEdges.push_back(key);
                }
            Require(!selectedEdges.empty(), "请选择需要溶解的边");
            for (auto [a, b] : selectedEdges)
            {
                std::vector<std::pair<ElementId, std::size_t>> incident;
                for (const auto &[id, f] : Faces)
                    for (std::size_t i = 0; i < f.Corners.size(); ++i)
                        if (Key(f.Corners[i].Vertex, f.Corners[(i + 1) % f.Corners.size()].Vertex) == Key(a, b))
                            incident.push_back({id, i});
                Require(incident.size() == 2, "只能溶解两个面之间的内部边");
                auto [first, i] = incident[0];
                auto [second, j] = incident[1];
                auto f = Faces.at(first), g = Faces.at(second);
                Require(f.Material == g.Material && f.Smooth == g.Smooth, "不同材质或法线模式的面不能溶解");
                Require(f.Corners[i].Vertex == g.Corners[(j + 1) % g.Corners.size()].Vertex,
                        "相邻面绕序不一致，请先调整法线");
                auto compatible = [](const MeshCorner &a, const MeshCorner &b)
                {
                    return std::abs(a.Uv.x - b.Uv.x) + std::abs(a.Uv.y - b.Uv.y) + std::abs(a.Uv1.x - b.Uv1.x) +
                                   std::abs(a.Uv1.y - b.Uv1.y) <
                               1e-5f &&
                           LengthSquared(a.Normal - b.Normal) < 1e-8f;
                };
                Require(compatible(f.Corners[i], g.Corners[(j + 1) % g.Corners.size()]) &&
                            compatible(f.Corners[(i + 1) % f.Corners.size()], g.Corners[j]),
                        "此边两侧的 UV 或法线不连续，溶解会丢失面角属性");
                auto corners = Walk(f, (i + 1) % f.Corners.size(), i);
                auto tail = Walk(g, (j + 1) % g.Corners.size(), j);
                for (std::size_t k = 1; k + 1 < tail.size(); ++k)
                    corners.push_back(tail[k]);
                Faces.at(first).Corners = std::move(corners);
                Faces.erase(second);
                Triangles(*this, Faces.at(first));
            }
            RemoveUnused();
        }
        else if (op == "weld")
        {
            const auto previousEdges = Edges;
            Require(vertices.size() >= 2, "焊接至少需要两个顶点");
            float distance = args.value("distance", .001f);
            Require(std::isfinite(distance) && distance > 0, "焊接距离必须为正");
            std::map<ElementId, ElementId> mapping;
            // 空间网格限制邻域搜索，避免大模型全顶点两两比较。
            using Cell = std::array<std::int64_t, 3>;
            std::map<Cell, std::vector<ElementId>> cells;
            for (auto id : vertices)
            {
                auto p = Vertices.at(id).Position;
                Cell cell{static_cast<std::int64_t>(std::floor(p.x / distance)),
                          static_cast<std::int64_t>(std::floor(p.y / distance)),
                          static_cast<std::int64_t>(std::floor(p.z / distance))};
                ElementId found = 0;
                for (int x = -1; x <= 1 && !found; ++x)
                    for (int y = -1; y <= 1 && !found; ++y)
                        for (int z = -1; z <= 1 && !found; ++z)
                            if (auto it = cells.find({cell[0] + x, cell[1] + y, cell[2] + z}); it != cells.end())
                                for (auto other : it->second)
                                    if (LengthSquared(Vertices.at(other).Position - p) <= distance * distance)
                                    {
                                        found = other;
                                        break;
                                    }
                mapping[id] = found ? found : id;
                if (!found)
                    cells[cell].push_back(id);
            }
            std::map<ElementId, std::vector<ElementId>> groups;
            for (const auto &[id, target] : mapping)
                groups[target].push_back(id);
            for (const auto &[target, group] : groups)
                if (group.size() > 1)
                {
                    std::vector<std::pair<ElementId, float>> sources;
                    for (auto id : group)
                        sources.push_back({id, 1.f / group.size()});
                    auto point = Interpolate(sources);
                    Vertices.at(target) = Vertices.at(point);
                    Vertices.erase(point);
                }
            for (auto &[id, f] : Faces)
            {
                for (auto &c : f.Corners)
                    if (mapping.contains(c.Vertex))
                        c.Vertex = mapping.at(c.Vertex);
                std::vector<MeshCorner> corners;
                for (auto c : f.Corners)
                    if (corners.empty() || corners.back().Vertex != c.Vertex)
                        corners.push_back(c);
                if (corners.size() > 1 && corners.front().Vertex == corners.back().Vertex)
                    corners.pop_back();
                f.Corners = std::move(corners);
                (void)id;
            }
            std::erase_if(Faces,
                          [](const auto &row)
                          {
                              return row.second.Corners.size() < 3;
                          });
            RemoveUnused();
            for (const auto &[key, edge] : previousEdges)
                if (edge.Seam)
                {
                    auto a = mapping.contains(key.first) ? mapping.at(key.first) : key.first;
                    auto b = mapping.contains(key.second) ? mapping.at(key.second) : key.second;
                    if (a != b && Edges.contains(Key(a, b)))
                        Edges.at(Key(a, b)).Seam = true;
                }
            ClearNormals(*this);
        }
        else if (op == "fill")
        {
            std::map<ElementId, std::vector<MeshCorner>> boundary;
            std::map<EdgeKey, int> counts;
            for (const auto &[id, f] : Faces)
                for (std::size_t i = 0; i < f.Corners.size(); ++i)
                {
                    ++counts[Key(f.Corners[i].Vertex, f.Corners[(i + 1) % f.Corners.size()].Vertex)];
                    (void)id;
                }
            for (const auto &[id, f] : Faces)
                for (std::size_t i = 0; i < f.Corners.size(); ++i)
                {
                    auto a = f.Corners[i], b = f.Corners[(i + 1) % f.Corners.size()];
                    auto key = Key(a.Vertex, b.Vertex);
                    if (counts.at(key) == 1 &&
                        (selection.Mode == SelectionMode::Object ||
                         (selection.Mode == SelectionMode::Edge && selection.Ids.contains(Edges.at(key).Id)) ||
                         (vertices.contains(a.Vertex) && vertices.contains(b.Vertex))))
                        boundary[b.Vertex].push_back(a);
                    (void)id;
                }
            Require(!boundary.empty(), "请选择闭合的边界环");
            while (!boundary.empty())
            {
                auto first = boundary.begin()->first, current = first;
                std::vector<MeshCorner> corners;
                do
                {
                    auto it = boundary.find(current);
                    Require(it != boundary.end() && it->second.size() == 1, "边界环不闭合或存在分叉");
                    auto next = it->second[0];
                    corners.push_back({current, {}, {}});
                    boundary.erase(it);
                    current = next.Vertex;
                } while (current != first);
                Require(corners.size() >= 3, "补面至少需要三个顶点");
                auto id = AddFace(std::move(corners), args.value("material", 0));
                Triangles(*this, Faces.at(id));
                MeshSelection faceSelection{SelectionMode::Face, {id}};
                Surface({"uv_project", {{"mode", "planar"}}}, faceSelection);
            }
        }
        else if (op == "mirror")
        {
            int axis = args.value("axis", 0);
            Require(axis >= 0 && axis < 3, "镜像轴无效");
            float origin = args.value("origin", 0.f);
            auto reflected = Extract(selection);
            for (auto &[id, v] : reflected.Vertices)
            {
                (&v.Position.x)[axis] = 2 * origin - (&v.Position.x)[axis];
                for (auto &[name, d] : v.Shapes)
                {
                    (&d.Position.x)[axis] *= -1;
                    (&d.Normal.x)[axis] *= -1;
                    (&d.Tangent.x)[axis] *= -1;
                    (void)name;
                }
                (void)id;
            }
            for (auto &[id, f] : reflected.Faces)
            {
                std::reverse(f.Corners.begin(), f.Corners.end());
                for (auto &c : f.Corners)
                    (&c.Normal.x)[axis] *= -1;
                (void)id;
            }
            if (args.value("duplicate", true))
                Append(reflected);
            else
            {
                for (auto id : selected)
                    Faces.erase(id);
                RemoveUnused();
                Append(reflected);
            }
        }
        else if (op == "bevel" && selection.Mode != SelectionMode::Vertex)
        {
            float amount = args.value("amount", .12f);
            Require(std::isfinite(amount) && amount > 0 && amount < .49f, "倒角比例必须大于 0 且小于 0.49");
            std::map<EdgeKey, int> counts;
            auto original = Faces;
            auto oldEdges = Edges;
            std::set<EdgeKey> chosen;
            for (auto &[id, f] : original)
                for (size_t i = 0; i < f.Corners.size(); ++i)
                {
                    auto key = Key(f.Corners[i].Vertex, f.Corners[(i + 1) % f.Corners.size()].Vertex);
                    ++counts[key];
                    if (selection.Mode == SelectionMode::Object ||
                        (selection.Mode == SelectionMode::Edge && selection.Ids.contains(Edges.at(key).Id)) ||
                        ((selection.Mode == SelectionMode::Face || selection.Mode == SelectionMode::Uv) &&
                         selected.contains(id)))
                        chosen.insert(key);
                }
            Require(!chosen.empty(), "请选择需要倒角的边");
            Require(std::all_of(counts.begin(), counts.end(),
                                [](auto &row)
                                {
                                    return row.second == 2;
                                }),
                    "边倒角需要闭合的流形网格，请先补齐边界");
            std::map<std::pair<ElementId, ElementId>, ElementId> cuts;
            auto cut = [&](ElementId a, ElementId b)
            {
                auto key = std::pair{a, b};
                if (cuts.contains(key))
                    return cuts.at(key);
                std::array sources{std::pair{a, 1 - amount}, std::pair{b, amount}};
                return cuts[key] = Interpolate(sources);
            };
            // 先计算所有边端点；同一未选边上的切点在相邻面间共享。
            for (auto &[id, f] : original)
                for (size_t i = 0; i < f.Corners.size(); ++i)
                {
                    auto a = f.Corners[(i + f.Corners.size() - 1) % f.Corners.size()].Vertex, b = f.Corners[i].Vertex,
                         c = f.Corners[(i + 1) % f.Corners.size()].Vertex;
                    bool left = chosen.contains(Key(a, b)), right = chosen.contains(Key(b, c));
                    if (left && !right)
                        cut(b, c);
                    if (right && !left)
                        cut(b, a);
                    (void)id;
                }

            struct Side
            {
                MeshCorner A, B;
                int Material;
            };

            std::map<EdgeKey, std::vector<Side>> sides;
            std::set<EdgeKey> seams;
            for (auto &[id, f] : original)
            {
                std::vector<std::vector<MeshCorner>> corners;
                for (size_t i = 0; i < f.Corners.size(); ++i)
                {
                    auto a = f.Corners[(i + f.Corners.size() - 1) % f.Corners.size()], b = f.Corners[i],
                         c = f.Corners[(i + 1) % f.Corners.size()];
                    bool left = chosen.contains(Key(a.Vertex, b.Vertex)),
                         right = chosen.contains(Key(b.Vertex, c.Vertex));
                    std::vector<MeshCorner> row;
                    if (left && right)
                    {
                        std::array sources{std::pair{b.Vertex, 1 - 2 * amount}, std::pair{a.Vertex, amount},
                                           std::pair{c.Vertex, amount}};
                        MeshCorner moved = b;
                        moved.Vertex = Interpolate(sources);
                        moved.Uv = {b.Uv.x * (1 - 2 * amount) + (a.Uv.x + c.Uv.x) * amount,
                                    b.Uv.y * (1 - 2 * amount) + (a.Uv.y + c.Uv.y) * amount};
                        moved.Uv1 = {b.Uv1.x * (1 - 2 * amount) + (a.Uv1.x + c.Uv1.x) * amount,
                                     b.Uv1.y * (1 - 2 * amount) + (a.Uv1.y + c.Uv1.y) * amount};
                        row.push_back(moved);
                    }
                    else if (left)
                        row.push_back(InterpolateCorner(b, c, cut(b.Vertex, c.Vertex), amount));
                    else if (right)
                        row.push_back(InterpolateCorner(b, a, cut(b.Vertex, a.Vertex), amount));
                    else
                    {
                        bool cutLeft = cuts.contains({b.Vertex, a.Vertex}),
                             cutRight = cuts.contains({b.Vertex, c.Vertex});
                        if (cutLeft)
                            row.push_back(InterpolateCorner(b, a, cuts.at({b.Vertex, a.Vertex}), amount));
                        if (!cutLeft || !cutRight)
                            row.push_back(b);
                        if (cutRight)
                            row.push_back(InterpolateCorner(b, c, cuts.at({b.Vertex, c.Vertex}), amount));
                    }
                    corners.push_back(std::move(row));
                }
                Faces.at(id).Corners.clear();
                for (size_t i = 0; i < corners.size(); ++i)
                {
                    auto &row = corners[i];
                    Faces.at(id).Corners.insert(Faces.at(id).Corners.end(), row.begin(), row.end());
                    auto j = (i + 1) % corners.size();
                    auto key = Key(f.Corners[i].Vertex, f.Corners[j].Vertex);
                    if (chosen.contains(key))
                        sides[key].push_back({row.back(), corners[j].front(), f.Material});
                    if (oldEdges.at(key).Seam)
                        seams.insert(Key(row.back().Vertex, corners[j].front().Vertex));
                }
            }
            for (auto &[key, edges] : sides)
            {
                Require(edges.size() == 2, "倒角边必须恰好连接两个面");
                auto a = edges[0], b = edges[1];
                auto id = AddFace({a.B, a.A, b.B, b.A}, a.Material);
                Surface({"uv_project", {{"mode", "planar"}}}, {SelectionMode::Face, {id}});
                if (oldEdges.at(key).Seam)
                {
                    seams.insert(Key(a.A.Vertex, a.B.Vertex));
                    seams.insert(Key(b.A.Vertex, b.B.Vertex));
                }
            }
            RemoveUnused();
            std::map<EdgeKey, int> remaining;
            for (auto &[id, f] : Faces)
                for (size_t i = 0; i < f.Corners.size(); ++i)
                {
                    ++remaining[Key(f.Corners[i].Vertex, f.Corners[(i + 1) % f.Corners.size()].Vertex)];
                    (void)id;
                }
            if (std::any_of(remaining.begin(), remaining.end(),
                            [](auto &row)
                            {
                                return row.second == 1;
                            }))
                Topology({"fill", {{"material", args.value("material", 0)}}}, {});
            RebuildEdges();
            for (auto key : seams)
                if (Edges.contains(key))
                    Edges.at(key).Seam = true;
            ClearNormals(*this);
        }
        else if (op == "bevel")
        {
            const auto previousEdges = Edges;
            // 倒角按所选边两端的截角环构建，保留各面的独立面角属性。
            float amount = args.value("amount", .12f);
            Require(std::isfinite(amount) && amount > 0 && amount < .49f, "倒角比例必须大于 0 且小于 0.49");
            std::set<ElementId> targets;
            if (selection.Mode == SelectionMode::Vertex)
                targets = vertices;
            else
                for (const auto &[key, e] : Edges)
                    if (selection.Mode == SelectionMode::Object || selection.Ids.contains(e.Id))
                    {
                        targets.insert(e.A);
                        targets.insert(e.B);
                        (void)key;
                    }
            Require(!targets.empty(), "请选择需要倒角的顶点或边");
            std::map<std::pair<ElementId, ElementId>, ElementId> cuts;
            auto cut = [&](ElementId a, ElementId b)
            {
                auto key = std::pair{a, b};
                if (auto found = cuts.find(key); found != cuts.end())
                    return found->second;
                std::array sources{std::pair{a, 1 - amount}, std::pair{b, amount}};
                return cuts[key] = Interpolate(sources);
            };
            std::map<ElementId, std::map<ElementId, MeshCorner>> caps;
            auto original = Faces;
            for (const auto &[id, f] : original)
            {
                std::vector<MeshCorner> corners;
                for (std::size_t i = 0; i < f.Corners.size(); ++i)
                {
                    auto a = f.Corners[(i + f.Corners.size() - 1) % f.Corners.size()], b = f.Corners[i],
                         c = f.Corners[(i + 1) % f.Corners.size()];
                    if (!targets.contains(b.Vertex))
                    {
                        corners.push_back(b);
                        continue;
                    }
                    auto left = InterpolateCorner(b, a, cut(b.Vertex, a.Vertex), amount);
                    auto right = InterpolateCorner(b, c, cut(b.Vertex, c.Vertex), amount);
                    corners.push_back(left);
                    corners.push_back(right);
                    Require(caps[b.Vertex].emplace(right.Vertex, left).second, "非流形顶点不能倒角");
                }
                Faces.at(id).Corners = std::move(corners);
            }
            for (auto &[vertex, ring] : caps)
            {
                Require(ring.size() >= 3, "边界顶点倒角需要封闭的相邻面，请先补面");
                auto first = ring.begin()->first, current = first;
                std::vector<MeshCorner> corners;
                do
                {
                    auto it = ring.find(current);
                    Require(it != ring.end(), "顶点倒角环不闭合");
                    corners.push_back({current, it->second.Uv, it->second.Uv1});
                    current = it->second.Vertex;
                    ring.erase(it);
                } while (current != first);
                Require(ring.empty(), "顶点包含多个不连通的面环，不能倒角");
                AddFace(std::move(corners), args.value("material", 0), false);
                (void)vertex;
            }
            RemoveUnused();
            for (const auto &[key, edge] : previousEdges)
                if (edge.Seam)
                {
                    auto a = cuts.contains({key.first, key.second}) ? cuts.at({key.first, key.second}) : key.first;
                    auto b = cuts.contains({key.second, key.first}) ? cuts.at({key.second, key.first}) : key.second;
                    if (Edges.contains(Key(a, b)))
                        Edges.at(Key(a, b)).Seam = true;
                }
            ClearNormals(*this);
        }
        else
            throw std::invalid_argument("未知拓扑操作：" + op);
    }
}
