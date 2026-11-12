#pragma once
#include "Common/Core.h"
#include "Character/CharacterPreparation.h"
#include <array>

namespace hs2::asset
{
    inline constexpr int SchemaVersion = 2;
    inline constexpr std::string_view BuilderVersion = "native-assets-2.0.1";

    struct Influence
    {
        std::string Bone;
        float Weight = 0;
    };

    struct Vertex
    {
        Vector3 Position{}, Normal{};
        Vector4 Tangent{1, 0, 0, 1}, Color{1, 1, 1, 1};
        Vector2 Uv{}, Uv1{};
        std::vector<Influence> Weights;
    };

    struct Shape
    {
        std::string Name;
        float Weight = 100;
        std::vector<Vector3> Positions, Normals, Tangents;
    };

    struct Mesh
    {
        std::string Name, Role;
        int ReferenceSlot = -1;
        std::vector<Vertex> Vertices;
        std::vector<std::uint32_t> Indices;
        std::vector<int> Materials;
        std::vector<Shape> Shapes;
        bool HasUv = false, HasNormals = false, HasTangents = false;
    };

    struct Bone
    {
        std::string Name, Parent;
        Vector3 Position{};
        Quaternion Rotation{0, 0, 0, 1};
        bool Added = false;
        Matrix4x4 Matrix{};
        bool HasMatrix = false;
    };

    struct Geometry
    {
        std::vector<Mesh> Meshes;
        std::vector<Bone> Bones;
        std::vector<std::string> Warnings;
        std::vector<std::string> InputHashes;
    };

    Vector3 Vector3Of(const Json &value);
    Vector2 Vector2Of(const Json &value);
    Vector4 Vector4Of(const Json &value);
    Json VectorJson(Vector2 value);
    Json VectorJson(Vector3 value);
    Json VectorJson(Vector4 value);
    Matrix4x4 BoneWorld(const Bone &bone);
    Matrix4x4 TransformOf(const Json &spec);
    void TransformMesh(Mesh &mesh, const Matrix4x4 &transform);
    void RebuildNormalsTangents(Mesh &mesh);
    Geometry CaptureReference(const PreparedCharacter &character, std::stop_token token = {});
    Json GeometryJson(const Geometry &geometry);
    Geometry GeometryOf(const Json &json);
    Json WriteReference(const Geometry &geometry, const fs::path &output, std::stop_token token = {});
    Geometry BuildRecipe(const Json &recipe, const Geometry &reference, std::stop_token token = {});
    Geometry ImportModel(const fs::path &source, const fs::path &bridge, std::stop_token token = {});
    void AdaptGeometry(Geometry &geometry, const Geometry &reference, const Json &spec, std::stop_token token = {});
    Json GeometryReport(const Geometry &geometry);
}
