#include "AssetGeometry.h"
#include "Animation/SimulationMath.h"
#include <algorithm>
#include <map>
#include <set>
#include <numbers>

namespace hs2::asset
{
    using namespace sim;

    namespace
    {
        constexpr float Pi = std::numbers::pi_v<float>;

        std::uint32_t Add(Mesh &mesh, Vector3 p, Vector2 uv, Vector3 n = {})
        {
            Require(mesh.Vertices.size() < 1000000, "建模操作超过顶点限制");
            Vertex v;
            v.Position = p;
            v.Uv = uv;
            v.Normal = n;
            mesh.Vertices.push_back(v);
            return static_cast<std::uint32_t>(mesh.Vertices.size() - 1);
        }

        void Triangle(Mesh &mesh, std::uint32_t a, std::uint32_t b, std::uint32_t c, int material = 0)
        {
            Require(mesh.Indices.size() < 750000, "建模操作超过面数限制");
            mesh.Indices.insert(mesh.Indices.end(), {a, b, c});
            mesh.Materials.push_back(material);
        }

        void Quad(Mesh &mesh, std::uint32_t a, std::uint32_t b, std::uint32_t c, std::uint32_t d)
        {
            Triangle(mesh, a, b, c);
            Triangle(mesh, a, c, d);
        }

        Mesh Primitive(const Json &op)
        {
            Mesh mesh;
            mesh.Name = op.at("name");
            mesh.HasUv = true;
            auto primitive = op.at("primitive").get<std::string>();
            if (primitive == "plane" || primitive == "box")
            {
                auto size = Vector3Of(op.value("size", Json::array({1, 1, 1})));
                Require(size.x > 0 && size.y > 0 && size.z > 0, "基本体尺寸必须为正");
                auto face = [&](Vector3 a, Vector3 b, Vector3 c, Vector3 d)
                {
                    auto n = Unit(Cross(b - a, c - a));
                    auto i = Add(mesh, a, {0, 0}, n);
                    Add(mesh, b, {1, 0}, n);
                    Add(mesh, c, {1, 1}, n);
                    Add(mesh, d, {0, 1}, n);
                    Quad(mesh, i, i + 1, i + 2, i + 3);
                };
                auto x = size.x * .5f, y = size.y * .5f, z = size.z * .5f;
                if (primitive == "plane")
                    face({-x, 0, -z}, {-x, 0, z}, {x, 0, z}, {x, 0, -z});
                else
                {
                    face({-x, -y, z}, {x, -y, z}, {x, y, z}, {-x, y, z});
                    face({x, -y, -z}, {-x, -y, -z}, {-x, y, -z}, {x, y, -z});
                    face({x, -y, z}, {x, -y, -z}, {x, y, -z}, {x, y, z});
                    face({-x, -y, -z}, {-x, -y, z}, {-x, y, z}, {-x, y, -z});
                    face({-x, y, z}, {x, y, z}, {x, y, -z}, {-x, y, -z});
                    face({-x, -y, -z}, {x, -y, -z}, {x, -y, z}, {-x, -y, z});
                }
                mesh.HasNormals = true;
            }
            else if (primitive == "sphere" || primitive == "cylinder")
            {
                int segments = op.value("segments", 24), rings = op.value("rings", primitive == "sphere" ? 16 : 1);
                float radius = op.value("radius", .5f), height = op.value("height", 1.f);
                Require(segments >= 3 && segments <= 512 && rings >= 1 && rings <= 512 && radius > 0 &&
                            std::isfinite(radius) && height > 0 && std::isfinite(height),
                        "基本体细分或尺寸无效");
                for (int row = 0; row <= rings; ++row)
                {
                    float v = float(row) / rings;
                    for (int col = 0; col <= segments; ++col)
                    {
                        float u = float(col) / segments, angle = u * 2 * Pi;
                        auto normal = primitive == "sphere"
                                          ? Vector3{std::sin(Pi * v) * std::cos(angle), std::cos(Pi * v),
                                                    std::sin(Pi * v) * std::sin(angle)}
                                          : Vector3{std::cos(angle), 0, std::sin(angle)};
                        auto point = primitive == "sphere"
                                         ? normal * radius
                                         : Vector3{normal.x * radius, (.5f - v) * height, normal.z * radius};
                        Add(mesh, point, {u, 1 - v}, normal);
                    }
                }
                for (int row = 0; row < rings; ++row)
                    for (int col = 0; col < segments; ++col)
                    {
                        auto a = static_cast<std::uint32_t>(row * (segments + 1) + col), b = a + segments + 1;
                        if (primitive != "sphere" || row > 0)
                            Triangle(mesh, a, a + 1, b);
                        if (primitive != "sphere" || row + 1 < rings)
                            Triangle(mesh, a + 1, b + 1, b);
                    }
                if (primitive == "cylinder")
                    for (int cap = 0; cap < 2; ++cap)
                    {
                        float y = cap ? -.5f * height : .5f * height;
                        Vector3 n{0, cap ? -1.f : 1.f, 0};
                        auto center = Add(mesh, {0, y, 0}, {.5f, .5f}, n);
                        for (int col = 0; col <= segments; ++col)
                        {
                            float angle = float(col) * 2 * Pi / segments;
                            Add(mesh, {std::cos(angle) * radius, y, std::sin(angle) * radius},
                                {.5f + .5f * std::cos(angle), .5f + .5f * std::sin(angle)}, n);
                        }
                        for (int col = 0; col < segments; ++col)
                            if (cap)
                                Triangle(mesh, center, center + 1 + col, center + 2 + col);
                            else
                                Triangle(mesh, center, center + 2 + col, center + 1 + col);
                    }
                mesh.HasNormals = true;
            }
            else
                throw std::invalid_argument("不支持的基本体；可用 plane/box/sphere/cylinder");
            return mesh;
        }

        Mesh Curve(const Json &op, std::stop_token token)
        {
            Mesh mesh;
            mesh.Name = op.at("name");
            mesh.HasUv = true;
            std::vector<Vector3> points;
            for (const auto &p : op.at("points"))
                points.push_back(Vector3Of(p));
            Require(points.size() >= 2 && points.size() <= 1024, "曲线控制点数量无效");
            int divisions = op.value("subdivisions", 8), sides = op.value("sides", 8);
            auto mode = op.value("mode", std::string("ribbon"));
            Require(divisions >= 1 && divisions <= 64 && sides >= 3 && sides <= 64 &&
                        (mode == "ribbon" || mode == "tube"),
                    "曲线模式或细分无效");
            auto widths = op.value("widths", std::vector<float>(points.size(), op.value("width", .2f)));
            auto boneNames = op.value("bones", std::vector<std::string>{});
            Require(widths.size() == points.size() && (boneNames.empty() || boneNames.size() == points.size()),
                    "曲线宽度／骨骼数量不匹配");
            for (float width : widths)
                Require(std::isfinite(width) && width > 0, "发束宽度必须为正");
            std::vector<Vector3> samples;
            std::vector<float> sampleWidths, positions;
            for (std::size_t segment = 0; segment + 1 < points.size(); ++segment)
                for (int j = 0; j < divisions + (segment + 2 == points.size() ? 1 : 0); ++j)
                {
                    auto a = points[segment ? segment - 1 : segment], b = points[segment], c = points[segment + 1],
                         d = points[std::min(segment + 2, points.size() - 1)];
                    float t = float(j) / divisions;
                    auto p = (b * 2 + (c - a) * t + (a * 2 - b * 5 + c * 4 - d) * (t * t) +
                              (b * 3 - a - c * 3 + d) * (t * t * t)) *
                             .5f;
                    samples.push_back(p);
                    sampleWidths.push_back(Lerp(widths[segment], widths[segment + 1], t));
                    positions.push_back(float(segment) + t);
                }
            int columns = mode == "tube" ? sides + 1 : 2;
            Vector3 previous{1, 0, 0};
            float length = 0;
            std::vector<float> distances{0};
            for (std::size_t i = 1; i < samples.size(); ++i)
            {
                length += Length(samples[i] - samples[i - 1]);
                distances.push_back(length);
            }
            Require(length > 1e-5f, "曲线长度为零");
            for (std::size_t i = 0; i < samples.size(); ++i)
            {
                CheckCancelled(token);
                auto direction = Unit(samples[std::min(i + 1, samples.size() - 1)] - samples[i ? i - 1 : 0], {0, 1, 0});
                auto axis = Unit(previous - direction * Dot(previous, direction));
                if (LengthSquared(axis) < .1f)
                    axis = Unit(Cross(direction, std::abs(direction.y) < .9f ? Vector3{0, 1, 0} : Vector3{0, 0, 1}));
                previous = axis;
                auto other = Unit(Cross(direction, axis));
                for (int column = 0; column < columns; ++column)
                {
                    float u = float(column) / (columns - 1);
                    auto offset = mode == "tube" ? (axis * std::cos(u * 2 * Pi) + other * std::sin(u * 2 * Pi)) *
                                                       (sampleWidths[i] * .5f)
                                                 : axis * ((u - .5f) * sampleWidths[i]);
                    Add(mesh, samples[i] + offset, {u, distances[i] / length});
                    if (!boneNames.empty())
                    {
                        auto segment = std::min(static_cast<std::size_t>(positions[i]), boneNames.size() - 2);
                        float t = positions[i] - float(segment);
                        mesh.Vertices.back().Weights = {{boneNames[segment], 1 - t}, {boneNames[segment + 1], t}};
                    }
                }
                if (i)
                    for (int column = 0; column + 1 < columns; ++column)
                    {
                        auto a = static_cast<std::uint32_t>((i - 1) * columns + column), b = a + columns;
                        Quad(mesh, a, b, b + 1, a + 1);
                    }
            }
            return mesh;
        }

        Mesh BodySurface(const Json &op, const Geometry &reference)
        {
            const Mesh *source = nullptr;
            for (const auto &mesh : reference.Meshes)
                if (op.contains("reference_mesh") ? mesh.Name == op["reference_mesh"].get<std::string>()
                                                  : mesh.Role == "body")
                    if (!source || mesh.Vertices.size() > source->Vertices.size())
                        source = &mesh;
            Require(source != nullptr, "找不到身体参考网格");
            auto minimum = Vector3Of(op.value("minimum", Json::array({-1000000, -1000000, -1000000})));
            auto maximum = Vector3Of(op.value("maximum", Json::array({1000000, 1000000, 1000000})));
            float offset = op.value("offset", 0.f);
            Require(std::isfinite(offset), "表面偏移无效");
            Mesh mesh;
            mesh.Name = op.at("name");
            mesh.HasUv = source->HasUv;
            std::map<std::uint32_t, std::uint32_t> remap;
            auto inside = [&](Vector3 p)
            {
                return p.x >= minimum.x && p.y >= minimum.y && p.z >= minimum.z && p.x <= maximum.x &&
                       p.y <= maximum.y && p.z <= maximum.z;
            };
            for (std::size_t i = 0; i + 2 < source->Indices.size(); i += 3)
            {
                bool keep = true;
                for (int j = 0; j < 3; ++j)
                    keep = keep && inside(source->Vertices.at(source->Indices[i + j]).Position);
                if (!keep)
                    continue;
                auto a = source->Vertices.at(source->Indices[i]).Position,
                     b = source->Vertices.at(source->Indices[i + 1]).Position,
                     c = source->Vertices.at(source->Indices[i + 2]).Position;
                if (LengthSquared(Cross(b - a, c - a)) <= 1e-20f)
                    continue;
                for (int j = 0; j < 3; ++j)
                {
                    auto index = source->Indices[i + j];
                    auto [it, added] = remap.emplace(index, static_cast<std::uint32_t>(mesh.Vertices.size()));
                    if (added)
                    {
                        auto vertex = source->Vertices[index];
                        vertex.Position += vertex.Normal * offset;
                        if (op.value("transfer_weights", true))
                            vertex.Weights.clear();
                        mesh.Vertices.push_back(std::move(vertex));
                    }
                    mesh.Indices.push_back(it->second);
                }
                mesh.Materials.push_back(0);
            }
            Require(!mesh.Indices.empty(), "身体裁剪区域没有完整三角形");
            return mesh;
        }
    }

    Geometry BuildRecipe(const Json &recipe, const Geometry &reference, std::stop_token token)
    {
        Require(recipe.value("schema_version", SchemaVersion) == SchemaVersion,
                "建模配方版本不支持；需要 schema_version: 2");
        Geometry result = GeometryOf({{"meshes", recipe.value("meshes", Json::array())}});
        auto find = [&](const std::string &name) -> Mesh &
        {
            auto it = std::find_if(result.Meshes.begin(), result.Meshes.end(),
                                   [&](const auto &mesh)
                                   {
                                       return mesh.Name == name;
                                   });
            Require(it != result.Meshes.end(), "操作引用不存在的网格");
            return *it;
        };
        auto add = [&](Mesh mesh, const Json &op)
        {
            if (op.contains("anchor"))
            {
                auto name = op.at("anchor").get<std::string>();
                auto bone = std::find_if(reference.Bones.begin(), reference.Bones.end(),
                                         [&](const auto &b)
                                         {
                                             return b.Name == name;
                                         });
                Require(bone != reference.Bones.end(), "建模挂点骨骼不存在");
                TransformMesh(mesh, MultiplyMatrix(TransformOf(op), BoneWorld(*bone)));
            }
            else
                TransformMesh(mesh, TransformOf(op));
            if (op.contains("bone"))
                for (auto &v : mesh.Vertices)
                    v.Weights = {{op.at("bone").get<std::string>(), 1}};
            result.Meshes.push_back(std::move(mesh));
        };
        auto operations = recipe.value("operations", Json::array());
        Require(operations.size() <= 1024, "建模操作数量超过限制");
        for (const auto &op : operations)
        {
            CheckCancelled(token);
            auto type = op.at("op").get<std::string>();
            if (type == "primitive")
                add(Primitive(op), op);
            else if (type == "curve")
                add(Curve(op, token), op);
            else if (type == "body_surface")
                add(BodySurface(op, reference), op);
            else if (type == "transform")
                TransformMesh(find(op.at("mesh")), TransformOf(op));
            else if (type == "mirror")
            {
                auto copy = find(op.at("mesh"));
                copy.Name = op.at("name");
                auto axis = op.value("axis", std::string("X"));
                Require(axis == "X" || axis == "Y" || axis == "Z", "镜像轴无效");
                Json scale = Json::array({1, 1, 1});
                scale[axis == "X" ? 0 : axis == "Y" ? 1 : 2] = -1;
                TransformMesh(copy, TransformOf({{"scale", scale}}));
                result.Meshes.push_back(std::move(copy));
            }
            else if (type == "merge")
            {
                Mesh combined;
                combined.Name = op.at("name");
                combined.HasUv = true;
                std::set<std::string> inputs;
                for (const auto &name : op.at("meshes"))
                {
                    auto key = name.get<std::string>();
                    Require(inputs.insert(key).second, "合并网格重复");
                    const auto &mesh = find(key);
                    Require(mesh.Shapes.empty(), "带形变网格不能使用合并操作；请直接提交合并后的形变通道");
                    auto start = static_cast<std::uint32_t>(combined.Vertices.size());
                    combined.Vertices.insert(combined.Vertices.end(), mesh.Vertices.begin(), mesh.Vertices.end());
                    for (auto index : mesh.Indices)
                        combined.Indices.push_back(start + index);
                    combined.Materials.insert(combined.Materials.end(), mesh.Materials.begin(), mesh.Materials.end());
                    combined.HasUv = combined.HasUv && mesh.HasUv;
                }
                std::erase_if(result.Meshes,
                              [&](const auto &mesh)
                              {
                                  return inputs.contains(mesh.Name);
                              });
                result.Meshes.push_back(std::move(combined));
            }
            else
                throw std::invalid_argument("未知原生建模操作：" + type);
            std::size_t vertices = 0, indices = 0;
            for (const auto &mesh : result.Meshes)
            {
                vertices += mesh.Vertices.size();
                indices += mesh.Indices.size();
            }
            Require(vertices <= 1000000 && indices <= 750000, "建模操作超过总几何预算");
        }
        std::map<std::string, Bone> known;
        for (const auto &bone : reference.Bones)
            known.emplace(bone.Name, bone);
        auto pending = recipe.value("bones", Json::array());
        Require(pending.size() <= 4096, "新增骨骼数量超过限制");
        while (!pending.empty())
        {
            bool progress = false;
            for (auto it = pending.begin(); it != pending.end();)
            {
                auto name = it->at("name").get<std::string>(), parent = it->at("parent").get<std::string>();
                Require(!name.empty() && !known.contains(name), "新增骨骼名称为空或与已有骨骼重复");
                if (!known.contains(parent))
                {
                    ++it;
                    continue;
                }
                auto position = Vector3Of(it->value("position", Json::array({0, 0, 0})));
                auto rotation = Normalize(Vector4Of(it->value("rotation", Json::array({0, 0, 0, 1}))));
                const auto &base = known.at(parent);
                Bone bone{name, parent, TransformPoint(position, BoneWorld(base)), Mul(base.Rotation, rotation), true};
                bone.Matrix = MultiplyMatrix(InverseMatrix(NativeSceneNode::InverseRigidMatrix(position, rotation)),
                                             BoneWorld(base));
                bone.HasMatrix = true;
                known.emplace(name, bone);
                result.Bones.push_back(bone);
                it = pending.erase(it);
                progress = true;
            }
            Require(progress, "新增骨骼父级不存在或层级存在循环");
        }
        return result;
    }
}
