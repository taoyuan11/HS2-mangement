#include "MeshDocument.h"
#include "Animation/SimulationMath.h"
#include <algorithm>
#include <numbers>

namespace hs2::creation
{
    using namespace sim;

    namespace
    {
        void Require(bool value, const char *message)
        {
            if (!value)
                throw std::invalid_argument(message);
        }

        std::map<ElementId, std::set<ElementId>> Neighbors(const MeshDocument &mesh)
        {
            std::map<ElementId, std::set<ElementId>> result;
            for (const auto &[key, edge] : mesh.Edges)
            {
                result[edge.A].insert(edge.B);
                result[edge.B].insert(edge.A);
                (void)key;
            }
            return result;
        }

        float Weight(const MeshVertex &v, const std::string &bone)
        {
            float value = 0;
            for (const auto &w : v.Weights)
                if (w.Bone == bone)
                    value += w.Weight;
            return value;
        }

        std::string MirrorBone(std::string name)
        {
            for (const auto &[left, right] :
                 {std::pair{std::string("_L"), std::string("_R")}, {std::string(".L"), std::string(".R")}})
            {
                auto at = name.find(left);
                if (at != name.npos)
                {
                    name.replace(at, left.size(), right);
                    return name;
                }
                at = name.find(right);
                if (at != name.npos)
                {
                    name.replace(at, right.size(), left);
                    return name;
                }
            }
            return name;
        }
    }

    void MeshDocument::NormalizeWeights(const std::set<std::string> &locked)
    {
        for (auto &[id, v] : Vertices)
        {
            std::map<std::string, float> sum;
            for (auto &w : v.Weights)
                sum[w.Bone] += w.Weight;
            std::vector<asset::Influence> fixed, free;
            float fixedTotal = 0, freeTotal = 0;
            for (const auto &[bone, weight] : sum)
                if (weight > 1e-8f)
                {
                    if (locked.contains(bone))
                    {
                        fixed.push_back({bone, weight});
                        fixedTotal += weight;
                    }
                    else
                        free.push_back({bone, weight});
                }
            Require(fixed.size() <= 4 && fixedTotal <= 1.00001f, "锁定骨骼权重超过游戏限制");
            std::sort(free.begin(), free.end(),
                      [](const auto &a, const auto &b)
                      {
                          return a.Weight == b.Weight ? a.Bone < b.Bone : a.Weight > b.Weight;
                      });
            if (free.size() > 4 - fixed.size())
                free.resize(4 - fixed.size());
            for (const auto &w : free)
                freeTotal += w.Weight;
            Require(!free.empty() || fixed.empty() || std::abs(fixedTotal - 1) < .00001f,
                    "锁定权重不足 1，至少保留一个可调整的骨骼");
            for (auto &w : free)
                w.Weight = freeTotal > 0 ? w.Weight / freeTotal * (1 - fixedTotal) : 0;
            fixed.insert(fixed.end(), free.begin(), free.end());
            v.Weights = std::move(fixed);
            (void)id;
        }
    }

    void MeshDocument::Surface(const EditCommand &command, const MeshSelection &selection)
    {
        const auto &op = command.Operation;
        const auto &args = command.Arguments;
        auto ids = SelectedVertices(selection);
        auto faces = SelectedFaces(selection);
        if (op == "transform")
        {
            Require(!ids.empty(), "请选择需要变换的元素");
            auto pivot = args.contains("pivot") ? asset::Vector3Of(args.at("pivot")) : Center(selection);
            auto position = asset::Vector3Of(args.value("position", Json::array({0, 0, 0})));
            auto rotation = asset::Vector3Of(args.value("rotation", Json::array({0, 0, 0})));
            auto scale = asset::Vector3Of(args.value("scale", Json::array({1, 1, 1})));
            Require(std::abs(scale.x * scale.y * scale.z) > 1e-12f, "缩放不能为零");
            auto q = Mul(AngleAxisDegrees(rotation.y, {0, 1, 0}),
                         Mul(AngleAxisDegrees(rotation.x, {1, 0, 0}), AngleAxisDegrees(rotation.z, {0, 0, 1})));
            float snap = args.value("snap", 0.f);
            Require(std::isfinite(snap) && snap >= 0, "吸附步长无效");
            for (auto id : ids)
            {
                auto &v = Vertices.at(id);
                v.Position = pivot + position + Rotate(q, Product(v.Position - pivot, scale));
                if (snap > 0)
                {
                    v.Position.x = std::round(v.Position.x / snap) * snap;
                    v.Position.y = std::round(v.Position.y / snap) * snap;
                    v.Position.z = std::round(v.Position.z / snap) * snap;
                }
                for (auto &[name, d] : v.Shapes)
                {
                    d.Position = Rotate(q, Product(d.Position, scale));
                    d.Normal = Rotate(q, {d.Normal.x / scale.x, d.Normal.y / scale.y, d.Normal.z / scale.z});
                    d.Tangent = Rotate(q, Product(d.Tangent, scale));
                    (void)name;
                }
            }
            for (auto &[id, f] : Faces)
            {
                for (auto &c : f.Corners)
                    if (ids.contains(c.Vertex) && LengthSquared(c.Normal) > 1e-12f)
                        c.Normal = Unit(Rotate(q, {c.Normal.x / scale.x, c.Normal.y / scale.y, c.Normal.z / scale.z}));
                if (scale.x * scale.y * scale.z < 0 && faces.contains(id))
                    std::reverse(f.Corners.begin(), f.Corners.end());
            }
        }
        else if (op == "normal" || op == "material")
        {
            for (auto id : faces)
            {
                auto &f = Faces.at(id);
                if (op == "material")
                    f.Material = args.at("slot");
                else
                {
                    auto mode = args.value("mode", "smooth");
                    if (mode == "flip")
                        std::reverse(f.Corners.begin(), f.Corners.end());
                    else if (mode == "smooth" || mode == "flat")
                        f.Smooth = mode == "smooth";
                    else
                        Require(mode == "recalculate", "法线操作无效");
                    for (auto &c : f.Corners)
                        c.Normal = {};
                }
            }
        }
        else if (op == "uv_seam")
        {
            Require(selection.Mode == SelectionMode::Edge, "请在边模式选择 UV 接缝");
            for (auto &[key, edge] : Edges)
            {
                if (selection.Ids.contains(edge.Id))
                    edge.Seam = args.value("enabled", true);
                (void)key;
            }
        }
        else if (op == "uv_project")
        {
            auto mode = args.value("mode", "planar");
            Vector3 normal{};
            for (auto id : faces)
                normal += FaceNormal(id);
            if (LengthSquared(normal) < .00001f)
                normal = {0, 0, 1};
            normal = Unit(normal);
            int axis = args.value("axis", -1);
            if (axis >= 0 && axis < 3)
            {
                normal = {};
                (&normal.x)[axis] = 1;
            }
            auto tangent = Unit(Cross(std::abs(normal.y) > .9f ? Vector3{1, 0, 0} : Vector3{0, 1, 0}, normal));
            auto bitangent = Cross(normal, tangent);
            auto center = Center(selection);
            float scale = args.value("scale", 1.f);
            Require(std::isfinite(scale) && scale > 0, "UV 投影尺寸必须为正");
            for (auto id : faces)
                for (auto &c : Faces.at(id).Corners)
                {
                    auto p = Vertices.at(c.Vertex).Position - center;
                    if (mode == "cylinder")
                        c.Uv = {.5f + std::atan2(p.z, p.x) / (2 * std::numbers::pi_v<float>), .5f + p.y / scale};
                    else if (mode == "sphere")
                    {
                        auto unit = Unit(p, {0, 1, 0});
                        c.Uv = {.5f + std::atan2(unit.z, unit.x) / (2 * std::numbers::pi_v<float>),
                                std::acos(std::clamp(unit.y, -1.f, 1.f)) / std::numbers::pi_v<float>};
                    }
                    else
                    {
                        Require(mode == "planar", "UV 投影模式无效");
                        c.Uv = {.5f + Dot(p, tangent) / scale, .5f + Dot(p, bitangent) / scale};
                    }
                }
        }
        else if (op == "uv_transform")
        {
            auto move = asset::Vector2Of(args.value("position", Json::array({0, 0})));
            auto scale = asset::Vector2Of(args.value("scale", Json::array({1, 1})));
            auto pivot = asset::Vector2Of(args.value("pivot", Json::array({.5, .5})));
            float angle = args.value("rotation", 0.f) * std::numbers::pi_v<float> / 180;
            for (auto id : faces)
                for (auto &c : Faces.at(id).Corners)
                {
                    float x = (c.Uv.x - pivot.x) * scale.x, y = (c.Uv.y - pivot.y) * scale.y;
                    c.Uv = {pivot.x + move.x + x * std::cos(angle) - y * std::sin(angle),
                            pivot.y + move.y + x * std::sin(angle) + y * std::cos(angle)};
                }
        }
        else if (op == "uv_unwrap" || op == "uv_relax")
        {
            for (const auto &island : UvIslands())
            {
                if (std::none_of(island.begin(), island.end(),
                                 [&](auto id)
                                 {
                                     return faces.contains(id);
                                 }))
                    continue;
                std::map<std::pair<ElementId, ElementId>, int> edgeCount;
                std::map<ElementId, std::set<ElementId>> neighbors;
                std::map<ElementId, Vector2> uv;
                for (auto id : island)
                {
                    const auto &f = Faces.at(id);
                    for (std::size_t i = 0; i < f.Corners.size(); ++i)
                    {
                        auto a = f.Corners[i], b = f.Corners[(i + 1) % f.Corners.size()];
                        ++edgeCount[std::minmax(a.Vertex, b.Vertex)];
                        neighbors[a.Vertex].insert(b.Vertex);
                        neighbors[b.Vertex].insert(a.Vertex);
                        uv[a.Vertex] = a.Uv;
                    }
                }
                std::map<ElementId, std::vector<ElementId>> boundary;
                for (const auto &[edge, count] : edgeCount)
                    if (count == 1)
                    {
                        boundary[edge.first].push_back(edge.second);
                        boundary[edge.second].push_back(edge.first);
                    }
                if (op == "uv_unwrap")
                {
                    Require(boundary.size() >= 3, "闭合网格需要先标记接缝再展开");
                    std::vector<ElementId> loop;
                    auto start = boundary.begin()->first, current = start, previous = ElementId(0);
                    do
                    {
                        loop.push_back(current);
                        const auto &next = boundary.at(current);
                        Require(next.size() == 2 && loop.size() <= boundary.size(), "UV 岛边界不规则，请补充接缝");
                        auto target = next[0] == previous ? next[1] : next[0];
                        previous = current;
                        current = target;
                    } while (current != start);
                    Require(loop.size() == boundary.size(), "带孔的 UV 岛需要增加连接孔洞的接缝");
                    float perimeter = 0;
                    std::vector<float> lengths;
                    for (std::size_t i = 0; i < loop.size(); ++i)
                    {
                        lengths.push_back(
                            Length(Vertices.at(loop[i]).Position - Vertices.at(loop[(i + 1) % loop.size()]).Position));
                        perimeter += lengths.back();
                    }
                    Require(perimeter > 1e-6f, "UV 岛边界长度为零");
                    for (auto &[id, value] : uv)
                    {
                        value = {.5f, .5f};
                        (void)id;
                    }
                    float distance = 0;
                    for (std::size_t i = 0; i < loop.size(); ++i)
                    {
                        float angle = distance / perimeter * 2 * std::numbers::pi_v<float>;
                        uv[loop[i]] = {.5f + .48f * std::cos(angle), .5f + .48f * std::sin(angle)};
                        distance += lengths[i];
                    }
                }
                int iterations = args.value("iterations", op == "uv_unwrap" ? 300 : 20);
                Require(iterations > 0 && iterations <= 1000, "UV 松弛迭代次数无效");
                for (int iteration = 0; iteration < iterations; ++iteration)
                    for (auto &[id, value] : uv)
                    {
                        if (boundary.contains(id))
                            continue;
                        Vector2 average{};
                        float sum = 0;
                        for (auto n : neighbors.at(id))
                        {
                            float weight =
                                1 / std::max(.00001f, Length(Vertices.at(id).Position - Vertices.at(n).Position));
                            average.x += uv.at(n).x * weight;
                            average.y += uv.at(n).y * weight;
                            sum += weight;
                        }
                        if (sum > 0)
                            value = {average.x / sum, average.y / sum};
                    }
                for (auto id : island)
                    for (auto &c : Faces.at(id).Corners)
                        c.Uv = uv.at(c.Vertex);
            }
        }
        else if (op == "uv_pack")
        {
            auto islands = UvIslands();
            std::erase_if(islands,
                          [&](const auto &island)
                          {
                              return std::none_of(island.begin(), island.end(),
                                                  [&](auto id)
                                                  {
                                                      return faces.contains(id);
                                                  });
                          });
            Require(!islands.empty(), "请选择需要打包的 UV 岛");
            int side = static_cast<int>(std::ceil(std::sqrt(static_cast<double>(islands.size()))));
            float padding = args.value("padding", .01f);
            Require(padding >= 0 && padding < .2f / side, "UV 岛间距过大");
            for (std::size_t i = 0; i < islands.size(); ++i)
            {
                Vector2 low{INFINITY, INFINITY}, high{-INFINITY, -INFINITY};
                for (auto id : islands[i])
                    for (const auto &c : Faces.at(id).Corners)
                    {
                        low = {std::min(low.x, c.Uv.x), std::min(low.y, c.Uv.y)};
                        high = {std::max(high.x, c.Uv.x), std::max(high.y, c.Uv.y)};
                    }
                float span = std::max({high.x - low.x, high.y - low.y, .00001f});
                float scale = (1.f / side - 2 * padding) / span;
                for (auto id : islands[i])
                    for (auto &c : Faces.at(id).Corners)
                        c.Uv = {static_cast<float>(i % side) / side + padding + (c.Uv.x - low.x) * scale,
                                static_cast<float>(i / side) / side + padding + (c.Uv.y - low.y) * scale};
            }
        }
        else if (op.starts_with("sculpt_") || op == "mask" || op == "weight_paint" || op == "weight_smooth")
        {
            auto center = asset::Vector3Of(args.at("center"));
            auto normal = Unit(asset::Vector3Of(args.value("normal", Json::array({0, 1, 0}))));
            auto delta = asset::Vector3Of(args.value("delta", Json::array({0, 0, 0})));
            float radius = args.value("radius", .5f), strength = args.value("strength", .1f),
                  falloff = args.value("falloff", 2.f);
            Require(radius > 0 && std::isfinite(radius) && std::isfinite(strength) && falloff >= .1f && falloff <= 8,
                    "笔刷半径、强度或衰减无效");
            auto before = Vertices;
            auto neighbors = Neighbors(*this);
            auto bone = args.value("bone", "");
            auto locked = args.value("locked_bones", std::set<std::string>{});
            if (op.starts_with("weight_"))
                Require(!bone.empty() && !locked.contains(bone), "请选择未锁定的骨骼");
            int symmetry = args.value("symmetry", -1);
            std::vector<Vector3> centers{center};
            if (symmetry >= 0 && symmetry < 3)
            {
                auto reflected = center;
                (&reflected.x)[symmetry] *= -1;
                centers.push_back(reflected);
            }
            for (auto id : ids)
            {
                auto &v = Vertices.at(id);
                float distance = INFINITY;
                int mirror = 0;
                for (std::size_t i = 0; i < centers.size(); ++i)
                {
                    auto d = Length(before.at(id).Position - centers[i]);
                    if (d < distance)
                    {
                        distance = d;
                        mirror = static_cast<int>(i);
                    }
                }
                if (distance >= radius)
                    continue;
                float influence = std::pow(1 - distance / radius, falloff) * strength;
                if (op != "mask")
                    influence *= 1 - v.Mask;
                auto direction = normal, movement = delta;
                if (mirror)
                {
                    (&direction.x)[symmetry] *= -1;
                    (&movement.x)[symmetry] *= -1;
                }
                auto position = before.at(id).Position;
                if (op == "mask")
                    v.Mask = std::clamp(v.Mask + influence, 0.f, 1.f);
                else if (op == "sculpt_move")
                    v.Position += movement * influence;
                else if (op == "sculpt_inflate")
                    v.Position += direction * influence * radius;
                else if (op == "sculpt_flatten")
                    v.Position -=
                        direction * (Dot(position - centers[mirror], direction) * std::clamp(influence, -1.f, 1.f));
                else if (op == "sculpt_pinch")
                {
                    auto toward = centers[mirror] - position;
                    v.Position += (toward - direction * Dot(toward, direction)) * influence;
                }
                else if (op == "sculpt_smooth")
                {
                    Vector3 average{};
                    for (auto n : neighbors[id])
                        average += before.at(n).Position;
                    if (!neighbors[id].empty())
                        v.Position += (average / static_cast<float>(neighbors[id].size()) - position) *
                                      std::clamp(influence, -1.f, 1.f);
                }
                else if (op == "weight_paint" || op == "weight_smooth")
                {
                    auto target = mirror && args.value("mirror_bones", true) ? MirrorBone(bone) : bone;
                    Require(!locked.contains(target), "对称目标骨骼已锁定");
                    float value = Weight(before.at(id), target), targetValue = args.value("value", 1.f);
                    if (op == "weight_smooth")
                    {
                        targetValue = 0;
                        for (auto n : neighbors[id])
                            targetValue += Weight(before.at(n), target);
                        if (!neighbors[id].empty())
                            targetValue /= neighbors[id].size();
                    }
                    value = std::clamp(value + (targetValue - value) * std::clamp(influence, -1.f, 1.f), 0.f, 1.f);
                    float fixed = 0, others = 0;
                    for (const auto &w : v.Weights)
                        if (w.Bone != target)
                        {
                            if (locked.contains(w.Bone))
                                fixed += w.Weight;
                            else
                                others += w.Weight;
                        }
                    value = std::min(value, 1 - fixed);
                    std::erase_if(v.Weights,
                                  [&](const auto &w)
                                  {
                                      return w.Bone == target;
                                  });
                    for (auto &w : v.Weights)
                        if (!locked.contains(w.Bone))
                            w.Weight = others > 0 ? w.Weight / others * (1 - fixed - value) : 0;
                    v.Weights.push_back({target, others > 0 ? value : 1 - fixed});
                }
                else
                    throw std::invalid_argument("未知笔刷操作：" + op);
            }
            if (op.starts_with("weight_"))
                NormalizeWeights(locked);
            if (op.starts_with("sculpt_"))
                for (auto &[id, face] : Faces)
                {
                    for (auto &c : face.Corners)
                        if (ids.contains(c.Vertex))
                            c.Normal = {};
                    (void)id;
                }
        }
        else if (op == "weight_normalize")
            NormalizeWeights(args.value("locked_bones", std::set<std::string>{}));
        else if (op == "weight_assign")
        {
            auto bone = args.at("bone").get<std::string>();
            Require(!bone.empty(), "骨骼名称不能为空");
            auto locked = args.value("locked_bones", std::set<std::string>{});
            Require(!locked.contains(bone), "所选骨骼已锁定");
            for (auto id : ids)
            {
                auto &weights = Vertices.at(id).Weights;
                std::erase_if(weights,
                              [&](const auto &w)
                              {
                                  return !locked.contains(w.Bone);
                              });
                float fixed = 0;
                for (const auto &w : weights)
                    fixed += w.Weight;
                weights.push_back({bone, std::max(0.f, 1 - fixed)});
            }
            NormalizeWeights(locked);
        }
        else if (op == "weight_mirror")
        {
            int axis = args.value("axis", 0);
            float tolerance = args.value("tolerance", .01f);
            Require(axis >= 0 && axis < 3 && tolerance > 0, "权重镜像参数无效");
            auto before = Vertices;
            auto locked = args.value("locked_bones", std::set<std::string>{});
            using Cell = std::array<int, 3>;
            std::map<Cell, std::vector<ElementId>> grid;
            auto cell = [&](Vector3 p) -> Cell
            {
                return {static_cast<int>(std::floor(p.x / tolerance)), static_cast<int>(std::floor(p.y / tolerance)),
                        static_cast<int>(std::floor(p.z / tolerance))};
            };
            for (const auto &[id, v] : before)
                grid[cell(v.Position)].push_back(id);
            for (auto id : ids)
            {
                auto p = before.at(id).Position;
                (&p.x)[axis] *= -1;
                auto at = cell(p);
                float nearest = tolerance * tolerance;
                ElementId source = 0;
                for (int x = -1; x <= 1; ++x)
                    for (int y = -1; y <= 1; ++y)
                        for (int z = -1; z <= 1; ++z)
                            if (auto found = grid.find({at[0] + x, at[1] + y, at[2] + z}); found != grid.end())
                                for (auto candidate : found->second)
                                {
                                    float d = LengthSquared(before.at(candidate).Position - p);
                                    if (d <= nearest)
                                    {
                                        nearest = d;
                                        source = candidate;
                                    }
                                }
                if (!source)
                    continue;
                auto &weights = Vertices.at(id).Weights;
                std::erase_if(weights,
                              [&](const auto &w)
                              {
                                  return !locked.contains(w.Bone);
                              });
                for (auto w : before.at(source).Weights)
                {
                    if (args.value("mirror_bones", true))
                        w.Bone = MirrorBone(w.Bone);
                    if (!locked.contains(w.Bone))
                        weights.push_back(w);
                }
            }
            NormalizeWeights(locked);
        }
        else
            throw std::invalid_argument("未知表面操作：" + op);
    }
}
