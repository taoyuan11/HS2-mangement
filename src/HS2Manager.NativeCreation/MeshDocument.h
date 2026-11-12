#pragma once
#include "AssetGeometry.h"
#include <map>
#include <set>
#include <cmath>

namespace hs2::creation
{
    using ElementId = std::uint64_t;

    struct ShapeDelta
    {
        Vector3 Position{}, Normal{}, Tangent{};
    };

    struct MeshVertex
    {
        Vector3 Position{};
        Vector4 Color{1, 1, 1, 1};
        std::vector<asset::Influence> Weights;
        std::map<std::string, ShapeDelta> Shapes;
        float Mask = 0;
    };

    struct MeshCorner
    {
        ElementId Vertex = 0;
        Vector2 Uv{}, Uv1{};
        Vector3 Normal{};
    };

    struct MeshFace
    {
        std::vector<MeshCorner> Corners;
        int Material = 0;
        bool Smooth = true;
    };

    struct MeshEdge
    {
        ElementId Id = 0, A = 0, B = 0;
        bool Seam = false;
    };

    enum class SelectionMode
    {
        Object,
        Vertex,
        Edge,
        Face,
        Uv
    };

    struct MeshSelection
    {
        SelectionMode Mode = SelectionMode::Object;
        std::set<ElementId> Ids;
    };

    struct EditCommand
    {
        std::string Operation;
        Json Arguments = Json::object();
    };

    struct MeshHit
    {
        ElementId Face = 0;
        std::array<ElementId, 3> Vertices{};
        Vector3 Position{}, Normal{}, Barycentric{};
        Vector2 Uv{};
        float Distance = INFINITY;
    };

    // 编辑拓扑与游戏三角网格分离，UV 接缝存于面角，元素编号在修订间保持稳定。
    class MeshDocument
    {
    public:
        std::string Name = "网格", Role;
        ElementId NextId = 1;
        std::map<ElementId, MeshVertex> Vertices;
        std::map<ElementId, MeshFace> Faces;
        std::map<std::pair<ElementId, ElementId>, MeshEdge> Edges;
        std::map<std::string, float> ShapeWeights;

        static MeshDocument FromMesh(const asset::Mesh &source);
        static MeshDocument FromJson(const Json &json);
        Json ToJson() const;
        asset::Mesh Triangulate() const;
        void Validate() const;
        void RebuildEdges();
        std::set<ElementId> SelectedVertices(const MeshSelection &selection) const;
        std::set<ElementId> SelectedFaces(const MeshSelection &selection) const;
        std::vector<std::vector<ElementId>> UvIslands() const;
        Vector3 Center(const MeshSelection &selection) const;
        Vector3 FaceNormal(ElementId face) const;
        std::optional<MeshHit> Pick(Vector3 origin, Vector3 direction, bool backfaces = false) const;
        void Execute(const EditCommand &command, const MeshSelection &selection);
        MeshDocument Extract(const MeshSelection &selection) const;
        void Append(const MeshDocument &source);

        // 算法共享的属性插值同时处理权重、形变与雕刻遮罩。
        ElementId Interpolate(std::span<const std::pair<ElementId, float>> sources);
        ElementId AddFace(std::vector<MeshCorner> corners, int material = 0, bool smooth = true);
        void RemoveUnused();
        void NormalizeWeights(const std::set<std::string> &locked = {});

    private:
        void Topology(const EditCommand &command, const MeshSelection &selection);
        void Surface(const EditCommand &command, const MeshSelection &selection);
    };

    bool Finite(Vector3 value);
    std::vector<std::array<std::size_t, 3>> Triangles(const MeshDocument &mesh, const MeshFace &face);
    MeshCorner InterpolateCorner(const MeshCorner &a, const MeshCorner &b, ElementId vertex, float t = .5f);
}
