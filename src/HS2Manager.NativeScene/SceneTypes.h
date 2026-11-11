#pragma once

#include <DirectXMath.h>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace hs2 {
struct CharacterPart;
struct CharacterDefinition;
struct NativeScenePart;
using Vector2 = DirectX::XMFLOAT2;
using Vector3 = DirectX::XMFLOAT3;
using Vector4 = DirectX::XMFLOAT4;
using Quaternion = DirectX::XMFLOAT4;
using Matrix4x4 = DirectX::XMFLOAT4X4;
struct BoneDefinition {std::string Name,Parent;Vector3 Position{},Scale{1,1,1};Quaternion Rotation{0,0,0,1};};
struct BoneAdjustment {std::string Name;Vector3 Scale{1,1,1},Position{},Rotation{};float Length=1;};

Matrix4x4 IdentityMatrix();
Matrix4x4 MultiplyMatrix(const Matrix4x4& a, const Matrix4x4& b);
Matrix4x4 InverseMatrix(const Matrix4x4& value);
Vector3 TransformPoint(Vector3 value, const Matrix4x4& matrix);
Vector3 TransformVector(Vector3 value, const Matrix4x4& matrix);

// This value tree is independent of the CLR and preserves signed/unsigned
// object IDs separately from floating point fields.
struct Parameter {
    std::string Name, Encoding, Text;
    double Number = 0;
    std::int64_t Integer = 0;
    std::uint64_t Unsigned = 0;
    std::vector<std::uint8_t> Bytes;
    std::vector<Parameter> Children;
    const Parameter& Get(std::string_view name) const;
    bool Has(std::string_view name) const;
    float Float(std::string_view name, float fallback = 0) const;
    std::int64_t Int(std::string_view name, std::int64_t fallback = 0) const;
    std::string String(std::string_view name) const;
    std::span<const Parameter> Array() const;
};

struct GpuAssetPointer {
    std::filesystem::path Bundle;
    std::int32_t FileId = 0;
    std::int64_t PathId = 0;
    bool operator==(const GpuAssetPointer&) const = default;
};
struct GpuVertexChannel { int Semantic = 0, Stream = 0, Offset = 0, Format = 0, Dimension = 0; };
struct GpuSubmesh { int FirstIndex = 0, IndexCount = 0, BaseVertex = 0, Topology = 0; };
struct GpuBlendFrame {
    float Weight = 0;
    std::vector<std::uint32_t> Indices;
    std::vector<Vector3> Positions, Normals, Tangents;
};
struct GpuBlendShape { std::string Name; std::uint32_t Hash = 0; std::vector<GpuBlendFrame> Frames; };
struct GpuMeshData {
    std::string Name;
    int VertexCount = 0;
    std::vector<GpuVertexChannel> Channels;
    std::vector<int> StreamOffsets, StreamStrides;
    std::vector<std::uint8_t> VertexBytes;
    int IndexSize = 2;
    std::vector<std::uint8_t> IndexBytes;
    std::vector<GpuSubmesh> Submeshes;
    std::vector<Matrix4x4> BindPoses;
    std::vector<GpuBlendShape> BlendShapes;
    float Read(int semantic, int vertex, int component) const;
    std::uint32_t Index(std::size_t index) const;
    void Validate() const;
    static int FormatSize(int format);
    static GpuMeshData FromMetadata(const Parameter& value, std::vector<std::uint8_t> vertexBytes);
};
struct GpuTextureData {
    std::string Name;
    int Width = 0, Height = 0, MipCount = 1, ImageCount = 1, Dimension = 2, Format = 0;
    bool Srgb = true;
    int Filter = 1, Anisotropy = 1;
    float MipBias = 0;
    int WrapU = 0, WrapV = 0, WrapW = 0;
    std::vector<std::uint8_t> Bytes;
};
struct GpuTextureBinding { GpuAssetPointer Texture; Vector2 Scale{1, 1}, Offset{}; };
struct GpuMaterialData {
    std::string Name;
    GpuAssetPointer Shader;
    int Queue = -1;
    std::vector<std::string> Keywords;
    std::unordered_map<std::string, float> Floats;
    std::unordered_map<std::string, Vector4> Colors;
    std::unordered_map<std::string, GpuTextureBinding> Textures;
    std::unordered_map<std::string, std::string> OverrideTags;
};
struct GpuNodeData {
    std::int64_t Id = 0, ParentId = 0;
    std::string Name;
    bool Active = true;
    int Layer = 0;
    Vector3 Position{}, Scale{1, 1, 1};
    Quaternion Rotation{0, 0, 0, 1};
    std::vector<std::int64_t> Children;
};
struct GpuRendererData {
    std::int64_t NodeId = 0;
    bool Enabled = true, Skinned = false;
    GpuAssetPointer Mesh, RootBone;
    std::vector<GpuAssetPointer> Materials, Bones;
    std::vector<float> BlendWeights;
    Parameter Settings;
};
struct GpuComponentData { std::int64_t NodeId = 0, PathId = 0; int ClassId = 0; Parameter Data; };
struct GpuPrefabData {
    std::int64_t RootId = 0;
    std::vector<GpuNodeData> Nodes;
    std::vector<GpuRendererData> Renderers;
    std::vector<GpuComponentData> Components;
    std::vector<std::string> Diagnostics;
};

// Nodes are owned by the scene. Parent/child pointers never own nodes.
class NativeSceneNode {
public:
    explicit NativeSceneNode(std::string name = {}, std::string sourceId = {});
    ~NativeSceneNode();
    NativeSceneNode(const NativeSceneNode&) = delete;
    NativeSceneNode& operator=(const NativeSceneNode&) = delete;
    std::string Name, SourceId;
    bool Active = true;
    int Layer = 0;
    NativeSceneNode* Parent() const noexcept { return parent_; }
    std::span<NativeSceneNode* const> Children() const { return children_; }
    void SetParent(NativeSceneNode* next);
    bool ActiveInHierarchy() const;
    Vector3 LocalPosition() const { return position_; }
    Vector3 LocalScale() const { return scale_; }
    Quaternion LocalRotation() const { return rotation_; }
    void LocalPosition(Vector3 value);
    void LocalScale(Vector3 value);
    void LocalRotation(Quaternion value);
    Vector3 Position() const;
    Quaternion Rotation() const;
    Vector3 LossyScale() const;
    void Position(Vector3 value);
    void Rotation(Quaternion value);
    void RotateWorld(Vector3 axis,float radians);
    Matrix4x4 LocalMatrix() const;
    Matrix4x4 WorldMatrix() const;
    Matrix4x4 InverseWorldMatrix() const;
    Vector3 TransformPoint(Vector3 value) const;
    Vector3 InverseTransformPoint(Vector3 value) const;
    Vector3 TransformVector(Vector3 value) const;
    Vector3 TransformDirection(Vector3 value) const;
    Vector3 InverseTransformDirection(Vector3 value) const;
    std::vector<NativeSceneNode*> Traverse();
    static Matrix4x4 InverseRigidMatrix(Vector3 position, Quaternion rotation);
private:
    void Invalidate();
    void InvalidateLocal();
    Matrix4x4 InverseLocalMatrix() const;
    NativeSceneNode* parent_ = nullptr;
    std::vector<NativeSceneNode*> children_;
    Vector3 position_{}, scale_{1, 1, 1};
    Quaternion rotation_{0, 0, 0, 1};
    mutable Matrix4x4 local_{}, world_{}, inverseLocal_{};
    mutable bool localDirty_ = true, worldDirty_ = true, inverseLocalDirty_ = true;
};
struct NativeDeformedVertices { std::vector<Vector3> Positions, Normals; std::vector<Vector4> Tangents; };
NativeDeformedVertices DeformMesh(const GpuMeshData& mesh, std::span<const float> blendWeights,
                                std::span<const Matrix4x4> boneMatrices = {});
std::vector<Matrix4x4> BuildSkinMatrices(std::span<NativeSceneNode* const> bones,
    std::span<const Matrix4x4> bindPoses, const Matrix4x4& worldToLocal);
struct NativeRendererBounds {
    Vector3 Center{}, Extents{};
    NativeRendererBounds Transform(const Matrix4x4& matrix) const;
    bool Intersects(const Matrix4x4& viewProjection) const;
};
struct NativeSceneRenderer {
    struct MeshBoundsCache {std::weak_ptr<GpuMeshData> Mesh;NativeRendererBounds Raw;std::vector<std::optional<NativeRendererBounds>> Bones;};
    mutable std::optional<MeshBoundsCache> CachedBounds;
    std::shared_ptr<CharacterPart> Part;
    NativeSceneNode* Node = nullptr;
    NativeSceneNode* RootBone = nullptr;
    std::string Role;
    int Slot = 0;
    GpuRendererData Source;
    std::shared_ptr<GpuMeshData> Mesh;
    std::vector<std::shared_ptr<GpuMaterialData>> Materials;
    std::vector<NativeSceneNode*> Bones;
    std::vector<float> BlendWeights;
    bool Enabled = true;
    std::optional<bool> LastRenderedVisibility;
    int ShadowCasting = 1;
    bool ReceiveShadows = true;
    std::optional<NativeRendererBounds> FixedLocalBounds;
    std::optional<NativeDeformedVertices> SimulatedVertices;
    bool Visible() const;
    bool HasGeometry() const;
    Matrix4x4 RenderWorldMatrix() const;
    Matrix4x4 RenderWorldToLocal() const;
    std::vector<Matrix4x4> SkinMatrices(const Matrix4x4& worldToLocal) const;
    NativeDeformedVertices Deform() const;
    NativeDeformedVertices DeformAnimation() const;
    NativeDeformedVertices BakeReferenceMesh() const;
    NativeRendererBounds Bounds() const;
};
struct NativeCharacterScene {
    NativeCharacterScene();
    ~NativeCharacterScene();
    NativeCharacterScene(const NativeCharacterScene&)=delete;
    NativeCharacterScene& operator=(const NativeCharacterScene&)=delete;
    std::shared_ptr<CharacterDefinition> Definition;
    std::string Name;
    int Sex = 0;
    std::unique_ptr<NativeSceneNode> Root = std::make_unique<NativeSceneNode>("Independent character", "root");
    std::vector<std::unique_ptr<NativeSceneNode>> Nodes;
    std::unordered_map<std::string, NativeSceneNode*> Bones;
    std::vector<std::shared_ptr<NativeSceneRenderer>> Renderers;
    std::vector<std::shared_ptr<NativeScenePart>> Parts;
    std::unordered_map<NativeSceneNode*,BoneDefinition> ScopedBaselines;
    std::unordered_map<NativeSceneNode*,BoneAdjustment> ScopedAdjustments;
    std::vector<std::string> Diagnostics;
};
}
