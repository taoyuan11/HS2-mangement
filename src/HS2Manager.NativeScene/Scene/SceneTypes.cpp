#include "Scene/SceneTypes.h"
#include <DirectXPackedVector.h>
#include <immintrin.h>
#include <algorithm>
#include <bit>
#include <cmath>
#include <cstring>
#include <functional>
#include <limits>
#include <stdexcept>

namespace hs2
{
    NativeCharacterScene::NativeCharacterScene() = default;
    NativeCharacterScene::~NativeCharacterScene() = default;

    namespace
    {
        void Require(bool condition, const char *error)
        {
            if (!condition)
                throw std::runtime_error(error);
        }

        void Finite(Vector3 v)
        {
            Require(std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z), "Non-finite scene transform.");
        }

        Vector3 Add(Vector3 a, Vector3 b)
        {
            return {a.x + b.x, a.y + b.y, a.z + b.z};
        }

        Vector3 Sub(Vector3 a, Vector3 b)
        {
            return {a.x - b.x, a.y - b.y, a.z - b.z};
        }

        Vector3 Scale(Vector3 a, float b)
        {
            return {a.x * b, a.y * b, a.z * b};
        }

        Vector3 Product(Vector3 a, Vector3 b)
        {
            return {a.x * b.x, a.y * b.y, a.z * b.z};
        }

        Vector3 Min(Vector3 a, Vector3 b)
        {
            return {std::min(a.x, b.x), std::min(a.y, b.y), std::min(a.z, b.z)};
        }

        Vector3 Max(Vector3 a, Vector3 b)
        {
            return {std::max(a.x, b.x), std::max(a.y, b.y), std::max(a.z, b.z)};
        }

        Vector3 Abs(Vector3 a)
        {
            return {std::abs(a.x), std::abs(a.y), std::abs(a.z)};
        }

        Quaternion Conjugate(Quaternion a)
        {
            return {-a.x, -a.y, -a.z, a.w};
        }

        Quaternion MultiplyRotation(Quaternion p, Quaternion q)
        {
            return {-(((p.z * q.y - p.y * q.z) - p.w * q.x) - p.x * q.w),
                    -(((p.x * q.z - p.z * q.x) - p.w * q.y) - p.y * q.w),
                    -(((p.y * q.x - p.w * q.z) - p.z * q.w) - p.x * q.y),
                    ((p.w * q.w - p.x * q.x) - p.z * q.z) - p.y * q.y};
        }

        Quaternion ScaleRotation(Quaternion q, Vector3 s)
        {
            return {(s.y < 0) != (s.z < 0) ? -q.x : q.x, (s.x < 0) != (s.z < 0) ? -q.y : q.y,
                    (s.x < 0) != (s.y < 0) ? -q.z : q.z, q.w};
        }

        Vector3 Signs(Vector3 v, Vector3 s)
        {
            return {s.x < 0 ? -v.x : v.x, s.y < 0 ? -v.y : v.y, s.z < 0 ? -v.z : v.z};
        }

        Vector3 Rotate(Quaternion q, Vector3 p)
        {
            float xx = (-2 * q.y) * q.y - (2 * q.z) * q.z, xy = (2 * q.x) * q.y - (-2 * q.z) * q.w,
                  xz = (-2 * q.y) * q.w - (-2 * q.z) * q.x;
            float yx = (-2 * q.z) * q.w - (-2 * q.x) * q.y, yy = (-2 * q.z) * q.z - (2 * q.x) * q.x,
                  yz = (2 * q.z) * q.y - (-2 * q.x) * q.w;
            float zx = (2 * q.x) * q.z - (-2 * q.y) * q.w, zy = (-2 * q.x) * q.w - (-2 * q.y) * q.z,
                  zz = (-2 * q.x) * q.x - (2 * q.y) * q.y;
            return {(zx * p.z + yx * p.y) + (xx * p.x + p.x), (zy * p.z + yy * p.y) + (xy * p.x + p.y),
                    (zz * p.z + yz * p.y) + (xz * p.x + p.z)};
        }

        Vector3 Reciprocal(Vector3 value)
        {
            // 保留参考实现中的两次 SSE 倒数精化。
            auto v = _mm_set_ps(1, value.z, value.y, value.x), r = _mm_rcp_ps(v);
            r = _mm_mul_ps(r, _mm_sub_ps(_mm_set1_ps(2.000000476837158f), _mm_mul_ps(r, v)));
            r = _mm_mul_ps(r, _mm_sub_ps(_mm_set1_ps(2), _mm_mul_ps(r, v)));
            float out[4];
            _mm_storeu_ps(out, r);
            return {std::abs(value.x) < 1e-9f ? 0 : out[0], std::abs(value.y) < 1e-9f ? 0 : out[1],
                    std::abs(value.z) < 1e-9f ? 0 : out[2]};
        }

        Matrix4x4 Affine(const Matrix4x4 &a, const Matrix4x4 &b)
        {
            Matrix4x4 r{};
            for (int row = 0; row < 4; ++row)
                for (int col = 0; col < 4; ++col)
                    r.m[row][col] = b.m[0][col] * a.m[row][0] +
                                    (b.m[1][col] * a.m[row][1] + b.m[2][col] * a.m[row][2]) +
                                    (row == 3 ? b.m[3][col] : 0);
            return r;
        }

        template <class T> T ReadLittle(std::span<const std::uint8_t> b, std::size_t offset)
        {
            Require(offset <= b.size() && sizeof(T) <= b.size() - offset, "Vertex/index buffer is truncated.");
            T value;
            std::memcpy(&value, b.data() + offset, sizeof value);
            return value;
        }

        Vector3 V3(const Parameter &p)
        {
            return {p.Float("x"), p.Float("y"), p.Float("z")};
        }
    }

    Matrix4x4 IdentityMatrix()
    {
        Matrix4x4 m;
        DirectX::XMStoreFloat4x4(&m, DirectX::XMMatrixIdentity());
        return m;
    }

    Matrix4x4 MultiplyMatrix(const Matrix4x4 &a, const Matrix4x4 &b)
    {
        Matrix4x4 r{};
        for (int row = 0; row < 4; ++row)
            for (int col = 0; col < 4; ++col)
                r.m[row][col] = (b.m[0][col] * a.m[row][0] + b.m[1][col] * a.m[row][1]) +
                                (b.m[2][col] * a.m[row][2] + b.m[3][col] * a.m[row][3]);
        return r;
    }

    Matrix4x4 InverseMatrix(const Matrix4x4 &value)
    {
        DirectX::XMVECTOR determinant;
        auto matrix = DirectX::XMMatrixInverse(&determinant, DirectX::XMLoadFloat4x4(&value));
        Require(std::isfinite(DirectX::XMVectorGetX(determinant)) &&
                    std::abs(DirectX::XMVectorGetX(determinant)) > 1e-30f,
                "Singular transform.");
        Matrix4x4 out;
        DirectX::XMStoreFloat4x4(&out, matrix);
        return out;
    }

    Vector3 TransformVector(Vector3 p, const Matrix4x4 &m)
    {
        return {(p.x * m._11 + p.y * m._21) + p.z * m._31, (p.x * m._12 + p.y * m._22) + p.z * m._32,
                (p.x * m._13 + p.y * m._23) + p.z * m._33};
    }

    Vector3 TransformPoint(Vector3 p, const Matrix4x4 &m)
    {
        return Add(TransformVector(p, m), {m._41, m._42, m._43});
    }

    const Parameter &Parameter::Get(std::string_view name) const
    {
        for (auto &p : Children)
            if (p.Name == name)
                return p;
        static const Parameter empty;
        return empty;
    }

    bool Parameter::Has(std::string_view name) const
    {
        return std::any_of(Children.begin(), Children.end(),
                           [&](auto &p)
                           {
                               return p.Name == name;
                           });
    }

    float Parameter::Float(std::string_view name, float fallback) const
    {
        if (!Has(name))
            return fallback;
        const auto &p = Get(name);
        if (p.Encoding == "float-special")
        {
            if (p.Text == "Infinity")
                return INFINITY;
            if (p.Text == "-Infinity")
                return -INFINITY;
            if (p.Text == "NaN")
                return NAN;
        }
        return static_cast<float>(p.Number);
    }

    std::int64_t Parameter::Int(std::string_view name, std::int64_t fallback) const
    {
        if (!Has(name))
            return fallback;
        const auto &p = Get(name);
        return p.Encoding == "int"    ? p.Integer
               : p.Encoding == "uint" ? static_cast<std::int64_t>(p.Unsigned)
                                      : static_cast<std::int64_t>(p.Number);
    }

    std::string Parameter::String(std::string_view name) const
    {
        return Get(name).Text;
    }

    std::span<const Parameter> Parameter::Array() const
    {
        if (Has("Array"))
            return Get("Array").Children;
        return Children;
    }

    int GpuMeshData::FormatSize(int format)
    {
        switch (format)
        {
        case 0:
        case 10:
        case 11:
            return 4;
        case 1:
        case 4:
        case 5:
        case 8:
        case 9:
            return 2;
        case 2:
        case 3:
        case 6:
        case 7:
            return 1;
        default:
            throw std::runtime_error("Unknown vertex channel format.");
        }
    }

    float GpuMeshData::Read(int semantic, int vertex, int component) const
    {
        Require(vertex >= 0 && vertex < VertexCount && component >= 0, "Invalid vertex channel access.");
        auto p = std::find_if(Channels.begin(), Channels.end(),
                              [&](auto &c)
                              {
                                  return c.Semantic == semantic;
                              });
        if (p == Channels.end() || component >= p->Dimension)
            return 0;
        Require(p->Stream >= 0 && static_cast<size_t>(p->Stream) < StreamOffsets.size() &&
                    static_cast<size_t>(p->Stream) < StreamStrides.size(),
                "Invalid vertex stream.");
        auto off = static_cast<size_t>(StreamOffsets[p->Stream]) +
                   static_cast<size_t>(StreamStrides[p->Stream]) * vertex + p->Offset +
                   component * FormatSize(p->Format);
        switch (p->Format)
        {
        case 0:
            return ReadLittle<float>(VertexBytes, off);
        case 1:
            return DirectX::PackedVector::XMConvertHalfToFloat(ReadLittle<std::uint16_t>(VertexBytes, off));
        case 2:
            return ReadLittle<std::uint8_t>(VertexBytes, off) / 255.f;
        case 3:
            return std::max(-1.f, ReadLittle<std::int8_t>(VertexBytes, off) / 127.f);
        case 4:
            return ReadLittle<std::uint16_t>(VertexBytes, off) / 65535.f;
        case 5:
            return std::max(-1.f, ReadLittle<std::int16_t>(VertexBytes, off) / 32767.f);
        case 6:
            return ReadLittle<std::uint8_t>(VertexBytes, off);
        case 7:
            return ReadLittle<std::int8_t>(VertexBytes, off);
        case 8:
            return ReadLittle<std::uint16_t>(VertexBytes, off);
        case 9:
            return ReadLittle<std::int16_t>(VertexBytes, off);
        case 10:
            return static_cast<float>(ReadLittle<std::uint32_t>(VertexBytes, off));
        case 11:
            return static_cast<float>(ReadLittle<std::int32_t>(VertexBytes, off));
        default:
            throw std::runtime_error("Unknown vertex channel format.");
        }
    }

    std::uint32_t GpuMeshData::Index(std::size_t index) const
    {
        Require(index <= IndexBytes.size() / IndexSize, "Invalid index offset.");
        return IndexSize == 2 ? ReadLittle<std::uint16_t>(IndexBytes, index * 2)
                              : ReadLittle<std::uint32_t>(IndexBytes, index * 4);
    }

    void GpuMeshData::Validate() const
    {
        Require(VertexCount >= 0 && VertexCount <= 2000000, "Invalid vertex count.");
        Require(IndexSize == 2 || IndexSize == 4, "Invalid index format.");
        for (const auto &c : Channels)
        {
            Require(c.Stream >= 0 && static_cast<size_t>(c.Stream) < StreamOffsets.size() &&
                        static_cast<size_t>(c.Stream) < StreamStrides.size() && c.Offset >= 0 && c.Dimension > 0 &&
                        c.Dimension <= 4,
                    "Invalid vertex channel.");
            Require(StreamOffsets[c.Stream] >= 0 &&
                        StreamStrides[c.Stream] >= c.Offset + c.Dimension * FormatSize(c.Format),
                    "Invalid vertex stream layout.");
            if (VertexCount)
                Read(c.Semantic, VertexCount - 1, c.Dimension - 1);
        }
        for (const auto &s : Submeshes)
        {
            Require(s.FirstIndex >= 0 && s.IndexCount >= 0 &&
                        static_cast<std::uint64_t>(s.FirstIndex) + s.IndexCount <= IndexBytes.size() / IndexSize,
                    "Invalid submesh index range.");
            for (int i = 0; i < s.IndexCount; ++i)
            {
                auto index = static_cast<std::int64_t>(Index(s.FirstIndex + i)) + s.BaseVertex;
                Require(index >= 0 && index < VertexCount, "Invalid mesh vertex index.");
            }
        }
        for (auto &s : BlendShapes)
        {
            float previous = 0;
            for (auto &f : s.Frames)
            {
                Require(std::isfinite(f.Weight) && f.Weight > previous, "Invalid blend-shape frame weights.");
                previous = f.Weight;
                Require(f.Indices.size() == f.Positions.size() && f.Indices.size() == f.Normals.size() &&
                            f.Indices.size() == f.Tangents.size(),
                        "Invalid blend-shape channels.");
                for (auto i : f.Indices)
                    Require(i < static_cast<unsigned>(VertexCount), "Invalid blend-shape vertex.");
            }
        }
    }

    GpuMeshData GpuMeshData::FromMetadata(const Parameter &p, std::vector<std::uint8_t> bytes)
    {
        Require(p.Int("m_MeshCompression") == 0, "Compressed mesh channels require a packed-vector decoder.");
        GpuMeshData m;
        m.Name = p.String("m_Name");
        m.VertexCount = static_cast<int>(p.Get("m_VertexData").Int("m_VertexCount"));
        Require(m.VertexCount >= 0 && m.VertexCount <= 2000000, "Invalid vertex count.");
        m.VertexBytes = std::move(bytes);
        m.StreamOffsets.resize(16);
        m.StreamStrides.resize(16);
        int semantic = 0;
        for (const auto &c : p.Get("m_VertexData").Get("m_Channels").Array())
        {
            GpuVertexChannel channel{semantic++, static_cast<int>(c.Int("stream")), static_cast<int>(c.Int("offset")),
                                     static_cast<int>(c.Int("format")), static_cast<int>(c.Int("dimension")) & 15};
            if (!channel.Dimension)
                continue;
            Require(channel.Stream >= 0 && channel.Stream < 16 && channel.Offset >= 0 && channel.Dimension <= 4,
                    "Invalid vertex channel.");
            m.StreamStrides[channel.Stream] = std::max(m.StreamStrides[channel.Stream],
                                                       channel.Offset + channel.Dimension * FormatSize(channel.Format));
            m.Channels.push_back(channel);
        }
        size_t length = 0;
        for (int stream = 0; stream < 16; ++stream)
        {
            Require(length <= INT_MAX, "Vertex stream exceeds limits.");
            m.StreamOffsets[stream] = static_cast<int>(length);
            if (!m.StreamStrides[stream])
                continue;
            length += static_cast<size_t>(m.StreamStrides[stream]) * m.VertexCount;
            Require(length <= m.VertexBytes.size(), "Vertex stream is truncated.");
            length = (length + 15) & ~size_t(15);
        }
        const auto &index = p.Get("m_IndexBuffer");
        m.IndexBytes = index.Bytes.empty() ? index.Get("Array").Bytes : index.Bytes;
        m.IndexSize = p.Int("m_IndexFormat") == 0 ? 2 : 4;
        for (auto &s : p.Get("m_SubMeshes").Array())
            m.Submeshes.push_back({static_cast<int>(s.Int("firstByte")) / m.IndexSize,
                                   static_cast<int>(s.Int("indexCount")), static_cast<int>(s.Int("baseVertex")),
                                   static_cast<int>(s.Int("topology"))});
        for (auto &b : p.Get("m_BindPose").Array())
        {
            Matrix4x4 mat;
            for (int r = 0; r < 4; ++r)
                for (int c = 0; c < 4; ++c)
                    mat.m[r][c] = b.Float("e" + std::to_string(c) + std::to_string(r));
            m.BindPoses.push_back(mat);
        }
        auto &shapes = p.Get("m_Shapes");
        auto vertices = shapes.Get("vertices").Array(), frames = shapes.Get("shapes").Array(),
             weights = shapes.Get("fullWeights").Array();
        for (auto &c : shapes.Get("channels").Array())
        {
            auto first = c.Int("frameIndex"), count = c.Int("frameCount");
            Require(first >= 0 && count > 0 && static_cast<uint64_t>(first) + count <= frames.size() &&
                        static_cast<uint64_t>(first) + count <= weights.size(),
                    "Invalid blend-shape frame range.");
            GpuBlendShape shape;
            shape.Name = c.String("name");
            shape.Hash = static_cast<uint32_t>(c.Int("nameHash"));
            for (int64_t f = first; f < first + count; ++f)
            {
                GpuBlendFrame frame;
                frame.Weight = static_cast<float>(weights[f].Number);
                auto start = frames[f].Int("firstVertex"), size = frames[f].Int("vertexCount");
                Require(start >= 0 && size >= 0 && static_cast<uint64_t>(start) + size <= vertices.size(),
                        "Invalid blend-shape vertex range.");
                for (int64_t i = start; i < start + size; ++i)
                {
                    frame.Indices.push_back(static_cast<uint32_t>(vertices[i].Int("index")));
                    frame.Positions.push_back(V3(vertices[i].Get("vertex")));
                    frame.Normals.push_back(V3(vertices[i].Get("normal")));
                    frame.Tangents.push_back(V3(vertices[i].Get("tangent")));
                }
                shape.Frames.push_back(std::move(frame));
            }
            m.BlendShapes.push_back(std::move(shape));
        }
        m.Validate();
        return m;
    }

    NativeSceneNode::NativeSceneNode(std::string name, std::string sourceId)
        : Name(std::move(name)), SourceId(std::move(sourceId))
    {
    }

    NativeSceneNode::~NativeSceneNode()
    {
        if (parent_)
            std::erase(parent_->children_, this);
        for (auto *child : children_)
        {
            child->parent_ = nullptr;
            child->Invalidate();
        }
    }

    void NativeSceneNode::SetParent(NativeSceneNode *next)
    {
        if (next == parent_)
            return;
        int depth = 0;
        for (auto *node = next; node; node = node->parent_)
        {
            Require(node != this, "Scene hierarchy cycle.");
            Require(++depth <= 128, "Scene hierarchy exceeds depth limit.");
        }
        if (parent_)
            std::erase(parent_->children_, this);
        parent_ = next;
        if (parent_)
            parent_->children_.push_back(this);
        Invalidate();
    }

    bool NativeSceneNode::ActiveInHierarchy() const
    {
        for (auto *n = this; n; n = n->parent_)
            if (!n->Active)
                return false;
        return true;
    }

    void NativeSceneNode::Invalidate()
    {
        worldDirty_ = true;
        for (auto *child : children_)
            child->Invalidate();
    }

    void NativeSceneNode::InvalidateLocal()
    {
        localDirty_ = inverseLocalDirty_ = true;
        Invalidate();
    }

    void NativeSceneNode::LocalPosition(Vector3 v)
    {
        Finite(v);
        position_ = v;
        InvalidateLocal();
    }

    void NativeSceneNode::LocalScale(Vector3 v)
    {
        Finite(v);
        scale_ = v;
        InvalidateLocal();
    }

    void NativeSceneNode::LocalRotation(Quaternion v)
    {
        float x = v.x * v.x, y = v.y * v.y, z = v.z * v.z, w = v.w * v.w;
        Require(std::isfinite(x + y + z + w) && x + y + z + w >= 1e-20f, "Invalid node rotation.");
        float xz = std::sqrt((x + y) + (z + w)), yw = std::sqrt((y + z) + (w + x));
        rotation_ = {v.x / xz, v.y / yw, v.z / xz, v.w / yw};
        InvalidateLocal();
    }

    Matrix4x4 NativeSceneNode::LocalMatrix() const
    {
        if (localDirty_)
        {
            DirectX::XMStoreFloat4x4(&local_,
                                     DirectX::XMMatrixScaling(scale_.x, scale_.y, scale_.z) *
                                         DirectX::XMMatrixRotationQuaternion(DirectX::XMLoadFloat4(&rotation_)) *
                                         DirectX::XMMatrixTranslation(position_.x, position_.y, position_.z));
            localDirty_ = false;
        }
        return local_;
    }

    Matrix4x4 NativeSceneNode::WorldMatrix() const
    {
        if (worldDirty_)
        {
            world_ = LocalMatrix();
            for (auto *p = parent_; p; p = p->parent_)
                world_ = Affine(world_, p->LocalMatrix());
            worldDirty_ = false;
        }
        return world_;
    }

    Matrix4x4 NativeSceneNode::InverseRigidMatrix(Vector3 position, Quaternion rotation)
    {
        auto q = Conjugate(rotation);
        Vector3 x{(q.z * -2) * q.z + (q.y * -2) * q.y + 1, (q.z * 2) * q.w + (q.y * 2) * q.x,
                  (q.z * 2) * q.x + (q.y * -2) * q.w},
            y{(q.z * -2) * q.w + (q.x * 2) * q.y, (q.z * -2) * q.z + (q.x * -2) * q.x + 1,
              (q.z * 2) * q.y + (q.x * 2) * q.w},
            z{(q.x * 2) * q.z + (q.y * 2) * q.w, (q.x * -2) * q.w + (q.y * 2) * q.z,
              (q.x * -2) * q.x + (q.y * -2) * q.y + 1};
        auto t = Add(Scale(x, -position.x), Add(Scale(y, -position.y), Scale(z, -position.z)));
        return {x.x, x.y, x.z, 0, y.x, y.y, y.z, 0, z.x, z.y, z.z, 0, t.x, t.y, t.z, 1};
    }

    Matrix4x4 NativeSceneNode::InverseLocalMatrix() const
    {
        if (inverseLocalDirty_)
        {
            inverseLocal_ = InverseRigidMatrix(position_, rotation_);
            auto reciprocal = Reciprocal(scale_);
            for (int r = 0; r < 4; ++r)
            {
                inverseLocal_.m[r][0] *= reciprocal.x;
                inverseLocal_.m[r][1] *= reciprocal.y;
                inverseLocal_.m[r][2] *= reciprocal.z;
            }
            inverseLocalDirty_ = false;
        }
        return inverseLocal_;
    }

    Matrix4x4 NativeSceneNode::InverseWorldMatrix() const
    {
        auto result = InverseLocalMatrix();
        for (auto *p = parent_; p; p = p->parent_)
            result = Affine(p->InverseLocalMatrix(), result);
        return result;
    }

    Vector3 NativeSceneNode::Position() const
    {
        auto p = position_;
        for (auto *a = parent_; a; a = a->parent_)
            p = Add(Rotate(a->rotation_, Product(p, a->scale_)), a->position_);
        return p;
    }

    void NativeSceneNode::Position(Vector3 v)
    {
        LocalPosition(parent_ ? parent_->InverseTransformPoint(v) : v);
    }

    Quaternion NativeSceneNode::Rotation() const
    {
        auto q = rotation_;
        for (auto *a = parent_; a; a = a->parent_)
            q = MultiplyRotation(a->rotation_, ScaleRotation(q, a->scale_));
        return q;
    }

    void NativeSceneNode::Rotation(Quaternion v)
    {
        std::vector<const NativeSceneNode *> ancestors;
        for (auto *a = parent_; a; a = a->parent_)
            ancestors.push_back(a);
        for (auto i = ancestors.rbegin(); i != ancestors.rend(); ++i)
            v = ScaleRotation(MultiplyRotation(Conjugate((*i)->rotation_), v), (*i)->scale_);
        LocalRotation(v);
    }

    void NativeSceneNode::RotateWorld(Vector3 axis, float radians)
    {
        auto vector = _mm_setr_ps(axis.x, axis.y, axis.z, 0);
        auto squared = _mm_mul_ps(vector, vector);
        auto sum = _mm_add_ps(squared, _mm_shuffle_ps(squared, squared, 0x39));
        sum = _mm_add_ps(sum, _mm_shuffle_ps(sum, sum, 0x4e));
        auto inverse = _mm_min_ps(_mm_mul_ps(_mm_rsqrt_ps(sum), _mm_set1_ps(.9999998211860657f)),
                                  _mm_set1_ps(std::numeric_limits<float>::max()));
        auto refinement =
            _mm_mul_ps(_mm_sub_ps(_mm_set1_ps(3.000000238418579f), _mm_mul_ps(_mm_mul_ps(sum, inverse), inverse)),
                       _mm_mul_ps(_mm_set1_ps(.5f), inverse));
        vector = _mm_and_ps(_mm_mul_ps(_mm_mul_ps(refinement, vector), _mm_set1_ps(radians)),
                            _mm_cmpgt_ps(sum, _mm_set1_ps(2.341805152028776e-38f)));
        squared = _mm_mul_ps(vector, vector);
        sum = _mm_add_ps(squared, _mm_shuffle_ps(squared, squared, 0x39));
        auto magnitude = _mm_sqrt_ps(_mm_add_ps(sum, _mm_shuffle_ps(sum, sum, 0x4e)));
        auto phase = _mm_add_ps(_mm_mul_ps(_mm_mul_ps(magnitude, _mm_set1_ps(.5f)), _mm_set1_ps(.15915493667125702f)),
                                _mm_setr_ps(-.25f, -.25f, -.25f, 0));
        auto reduced = _mm_sub_ps(phase, _mm_cvtepi32_ps(_mm_cvtps_epi32(phase)));
        auto u = _mm_sub_ps(_mm_set1_ps(.25f), _mm_and_ps(reduced, _mm_castsi128_ps(_mm_set1_epi32(0x7fffffff))));
        auto u2 = _mm_mul_ps(u, u), u4 = _mm_mul_ps(u2, u2);
        auto polynomial = _mm_add_ps(
            _mm_add_ps(
                _mm_mul_ps(
                    _mm_add_ps(_mm_mul_ps(_mm_set1_ps(-76.56861877441406f), u2), _mm_set1_ps(81.60182189941406f)), u4),
                _mm_add_ps(_mm_mul_ps(_mm_set1_ps(-41.34165954589844f), u2), _mm_set1_ps(6.283185005187988f))),
            _mm_mul_ps(_mm_mul_ps(u4, u4), _mm_set1_ps(39.65703201293945f)));
        auto sine = _mm_mul_ps(polynomial, u);
        auto xyz = _mm_div_ps(_mm_mul_ps(_mm_set1_ps(_mm_cvtss_f32(sine)), vector), magnitude);
        float components[4], wave[4];
        _mm_storeu_ps(components, xyz);
        _mm_storeu_ps(wave, sine);
        Quaternion delta{components[0], components[1], components[2], wave[3]};
        if (_mm_cvtss_f32(magnitude) == 0)
            delta = {0, 0, 0, 1};
        Rotation(MultiplyRotation(delta, Rotation()));
    }

    Vector3 NativeSceneNode::LossyScale() const
    {
        auto m = WorldMatrix(), inv = InverseRigidMatrix({}, Rotation());
        return {(m._12 * inv._21 + m._13 * inv._31) + m._11 * inv._11,
                (m._22 * inv._22 + m._23 * inv._32) + m._21 * inv._12,
                (m._32 * inv._23 + m._33 * inv._33) + m._31 * inv._13};
    }

    Vector3 NativeSceneNode::TransformPoint(Vector3 p) const
    {
        for (auto *a = this; a; a = a->parent_)
            p = Add(Rotate(a->rotation_, Product(p, a->scale_)), a->position_);
        return p;
    }

    Vector3 NativeSceneNode::InverseTransformPoint(Vector3 p) const
    {
        std::vector<const NativeSceneNode *> ancestors;
        for (auto *a = this; a; a = a->parent_)
            ancestors.push_back(a);
        for (auto i = ancestors.rbegin(); i != ancestors.rend(); ++i)
            p = Product(Rotate(Conjugate((*i)->rotation_), Sub(p, (*i)->position_)), Reciprocal((*i)->scale_));
        return p;
    }

    Vector3 NativeSceneNode::TransformVector(Vector3 p) const
    {
        for (auto *a = this; a; a = a->parent_)
            p = Rotate(a->rotation_, Product(p, a->scale_));
        return p;
    }

    Vector3 NativeSceneNode::TransformDirection(Vector3 p) const
    {
        p = Rotate(rotation_, p);
        for (auto *a = parent_; a; a = a->parent_)
            p = Rotate(a->rotation_, Signs(p, a->scale_));
        return p;
    }

    Vector3 NativeSceneNode::InverseTransformDirection(Vector3 p) const
    {
        std::vector<const NativeSceneNode *> ancestors;
        for (auto *a = parent_; a; a = a->parent_)
            ancestors.push_back(a);
        for (auto i = ancestors.rbegin(); i != ancestors.rend(); ++i)
            p = Signs(Rotate(Conjugate((*i)->rotation_), p), (*i)->scale_);
        return Rotate(Conjugate(rotation_), p);
    }

    std::vector<NativeSceneNode *> NativeSceneNode::Traverse()
    {
        std::vector<NativeSceneNode *> result, stack{this};
        while (!stack.empty())
        {
            auto *n = stack.back();
            stack.pop_back();
            result.push_back(n);
            for (auto i = n->children_.rbegin(); i != n->children_.rend(); ++i)
                stack.push_back(*i);
        }
        return result;
    }

    NativeDeformedVertices DeformMesh(const GpuMeshData &mesh, std::span<const float> weights,
                                      std::span<const Matrix4x4> matrices)
    {
        Require(weights.size() == mesh.BlendShapes.size(), "Blend-shape weight count does not match mesh.");
        Require(matrices.empty() || matrices.size() == mesh.BindPoses.size(), "Skin matrices do not match bind poses.");
        mesh.Validate();
        NativeDeformedVertices output;
        for (int v = 0; v < mesh.VertexCount; ++v)
        {
            output.Positions.push_back({mesh.Read(0, v, 0), mesh.Read(0, v, 1), mesh.Read(0, v, 2)});
            output.Normals.push_back({mesh.Read(1, v, 0), mesh.Read(1, v, 1), mesh.Read(1, v, 2)});
            output.Tangents.push_back({mesh.Read(2, v, 0), mesh.Read(2, v, 1), mesh.Read(2, v, 2), mesh.Read(2, v, 3)});
        }
        auto apply = [&](const GpuBlendFrame &f, float amount)
        {
            if (amount == 0)
                return;
            for (size_t i = 0; i < f.Indices.size(); ++i)
            {
                auto v = f.Indices[i];
                output.Positions[v] = Add(output.Positions[v], Scale(f.Positions[i], amount));
                output.Normals[v] = Add(output.Normals[v], Scale(f.Normals[i], amount));
                output.Tangents[v].x += f.Tangents[i].x * amount;
                output.Tangents[v].y += f.Tangents[i].y * amount;
                output.Tangents[v].z += f.Tangents[i].z * amount;
            }
        };
        for (size_t i = 0; i < weights.size(); ++i)
        {
            float w = weights[i];
            Require(std::isfinite(w), "Non-finite blend weight.");
            const auto &frames = mesh.BlendShapes[i].Frames;
            if (frames.empty() || w == 0)
                continue;
            auto upper = std::find_if(frames.begin(), frames.end(),
                                      [&](auto &f)
                                      {
                                          return f.Weight >= w;
                                      });
            if (upper == frames.begin() || frames.size() == 1)
                apply(frames[0], w / frames[0].Weight);
            else
            {
                if (upper == frames.end())
                    --upper;
                auto &low = *(upper - 1);
                auto &high = *upper;
                float amount = (w - low.Weight) / (high.Weight - low.Weight);
                apply(low, 1 - amount);
                apply(high, amount);
            }
        }
        if (!matrices.empty())
            for (int v = 0; v < mesh.VertexCount; ++v)
            {
                Matrix4x4 blended{};
                float total = 0;
                for (int influence = 0; influence < 4; ++influence)
                {
                    float w = mesh.Read(12, v, influence);
                    Require(std::isfinite(w) && w >= 0, "Invalid skin weight.");
                    if (w == 0)
                        continue;
                    float index = mesh.Read(13, v, influence);
                    Require(std::isfinite(index) && index >= 0 && index < matrices.size() && std::floor(index) == index,
                            "Invalid skin bone index.");
                    const auto &m = matrices[static_cast<size_t>(index)];
                    for (int r = 0; r < 4; ++r)
                        for (int c = 0; c < 4; ++c)
                            blended.m[r][c] += m.m[r][c] * w;
                    total += w;
                }
                Require(total > 0, "Skinned vertex has no influences.");
                output.Positions[v] = TransformPoint(output.Positions[v], blended);
                output.Normals[v] = TransformVector(output.Normals[v], blended);
                auto &t = output.Tangents[v];
                auto result = TransformVector({t.x, t.y, t.z}, blended);
                t = {result.x, result.y, result.z, t.w};
            }
        return output;
    }

    std::vector<Matrix4x4> BuildSkinMatrices(std::span<NativeSceneNode *const> bones, std::span<const Matrix4x4> poses,
                                             const Matrix4x4 &inverse)
    {
        Require(bones.size() == poses.size(), "Skin bones differ from bind poses.");
        std::unordered_map<NativeSceneNode *, Matrix4x4> cache;
        std::function<Matrix4x4(NativeSceneNode *)> resolve = [&](auto *n)
        {
            if (auto found = cache.find(n); found != cache.end())
                return found->second;
            auto result = MultiplyMatrix(n->LocalMatrix(), n->Parent() ? resolve(n->Parent()) : inverse);
            cache.emplace(n, result);
            return result;
        };
        std::vector<Matrix4x4> result;
        for (size_t i = 0; i < bones.size(); ++i)
            result.push_back(bones[i] ? MultiplyMatrix(poses[i], resolve(bones[i])) : IdentityMatrix());
        return result;
    }

    NativeRendererBounds NativeRendererBounds::Transform(const Matrix4x4 &m) const
    {
        return {TransformPoint(Center, m), Add(Add(Scale(Abs(TransformVector({1, 0, 0}, m)), Extents.x),
                                                   Scale(Abs(TransformVector({0, 1, 0}, m)), Extents.y)),
                                               Scale(Abs(TransformVector({0, 0, 1}, m)), Extents.z))};
    }

    bool NativeRendererBounds::Intersects(const Matrix4x4 &m) const
    {
        int outside = 63;
        for (int i = 0; i < 8; ++i)
        {
            auto p = Add(Center, {(i & 1) ? Extents.x : -Extents.x, (i & 2) ? Extents.y : -Extents.y,
                                  (i & 4) ? Extents.z : -Extents.z});
            auto clip = TransformPoint(p, m);
            float w = p.x * m._14 + p.y * m._24 + p.z * m._34 + m._44;
            outside &= (clip.x < -w ? 1 : 0) | (clip.x > w ? 2 : 0) | (clip.y < -w ? 4 : 0) | (clip.y > w ? 8 : 0) |
                       (clip.z < 0 ? 16 : 0) | (clip.z > w ? 32 : 0);
        }
        return outside == 0;
    }

    bool NativeSceneRenderer::Visible() const
    {
        return Enabled && Node && Node->ActiveInHierarchy();
    }

    bool NativeSceneRenderer::HasGeometry() const
    {
        return Mesh && Mesh->VertexCount > 0 &&
               !(Source.Skinned && !Bones.empty() &&
                 std::all_of(Bones.begin(), Bones.end(),
                             [](auto *n)
                             {
                                 return !n;
                             }));
    }

    Matrix4x4 NativeSceneRenderer::RenderWorldMatrix() const
    {
        Require(Node && Mesh, "Incomplete renderer.");
        auto *root = RootBone ? RootBone : Node;
        if (!Source.Skinned || Mesh->BindPoses.empty())
            return Node->WorldMatrix();
        auto q = root->Rotation();
        auto p = root->Position();
        Matrix4x4 m;
        DirectX::XMStoreFloat4x4(&m, DirectX::XMMatrixRotationQuaternion(DirectX::XMLoadFloat4(&q)) *
                                         DirectX::XMMatrixTranslation(p.x, p.y, p.z));
        return m;
    }

    Matrix4x4 NativeSceneRenderer::RenderWorldToLocal() const
    {
        auto *root = RootBone ? RootBone : Node;
        Require(root && Mesh, "Incomplete renderer.");
        return Source.Skinned && !Mesh->BindPoses.empty()
                   ? NativeSceneNode::InverseRigidMatrix(root->Position(), root->Rotation())
                   : Node->InverseWorldMatrix();
    }

    std::vector<Matrix4x4> NativeSceneRenderer::SkinMatrices(const Matrix4x4 &inverse) const
    {
        Require(static_cast<bool>(Mesh), "Missing mesh.");
        return BuildSkinMatrices(Bones, Mesh->BindPoses, inverse);
    }

    NativeDeformedVertices NativeSceneRenderer::Deform() const
    {
        return SimulatedVertices ? *SimulatedVertices : DeformAnimation();
    }

    NativeDeformedVertices NativeSceneRenderer::DeformAnimation() const
    {
        if (!HasGeometry())
            return {};
        // CPU 布料使用完整渲染节点局部空间中的顶点；
        // GPU 数据流则使用 RenderWorldToLocal 定义的刚性根空间。
        std::vector<Matrix4x4> matrices;
        if (Source.Skinned)
        {
            Require(Bones.size() == Mesh->BindPoses.size(), "Renderer bind-pose count differs from its bones.");
            auto inverse = Node->InverseWorldMatrix();
            for (size_t i = 0; i < Bones.size(); ++i)
                matrices.push_back(
                    Bones[i] ? MultiplyMatrix(MultiplyMatrix(Mesh->BindPoses[i], Bones[i]->WorldMatrix()), inverse)
                             : IdentityMatrix());
        }
        auto vertices = DeformMesh(*Mesh, BlendWeights, matrices);
        if (Source.Skinned && !Mesh->BindPoses.empty())
        {
            auto world = Node->WorldMatrix();
            Matrix4x4 transpose;
            DirectX::XMStoreFloat4x4(&transpose, DirectX::XMMatrixTranspose(DirectX::XMLoadFloat4x4(&world)));
            auto compensation = MultiplyMatrix(world, transpose);
            for (auto &normal : vertices.Normals)
                normal = TransformVector(normal, compensation);
        }
        return vertices;
    }

    NativeDeformedVertices NativeSceneRenderer::BakeReferenceMesh() const
    {
        if (!HasGeometry())
            return {};
        auto matrices = Source.Skinned
                            ? SkinMatrices(NativeSceneNode::InverseRigidMatrix(Node->Position(), Node->Rotation()))
                            : std::vector<Matrix4x4>{};
        return DeformMesh(*Mesh, BlendWeights, matrices);
    }

    NativeRendererBounds NativeSceneRenderer::Bounds() const
    {
        Require(Node && Mesh, "Incomplete renderer.");
        auto *root = RootBone ? RootBone : Node;
        if (Source.Skinned && Source.Settings.Float("m_UpdateWhenOffscreen") == 0)
        {
            auto &p = Source.Settings.Get("m_AABB");
            return FixedLocalBounds.value_or(NativeRendererBounds{V3(p.Get("m_Center")), Abs(V3(p.Get("m_Extent")))})
                .Transform(root->WorldMatrix());
        }
        if (!CachedBounds || CachedBounds->Mesh.lock() != Mesh)
        {
            MeshBoundsCache bounds;
            bounds.Mesh = Mesh;
            Vector3 lo{INFINITY, INFINITY, INFINITY}, hi{-INFINITY, -INFINITY, -INFINITY};
            std::vector<Vector3> minimum(Mesh->BindPoses.size(), lo), maximum(Mesh->BindPoses.size(), hi);
            for (int vertex = 0; vertex < Mesh->VertexCount; ++vertex)
            {
                Vector3 position{Mesh->Read(0, vertex, 0), Mesh->Read(0, vertex, 1), Mesh->Read(0, vertex, 2)};
                lo = Min(lo, position);
                hi = Max(hi, position);
                for (int influence = 0; influence < 4; ++influence)
                {
                    if (Mesh->Read(12, vertex, influence) <= 0)
                        continue;
                    float index = Mesh->Read(13, vertex, influence);
                    Require(std::isfinite(index) && index >= 0 && index < minimum.size(),
                            "Bounds reference an invalid skin bone.");
                    auto bone = static_cast<size_t>(index);
                    auto local = TransformPoint(position, Mesh->BindPoses[bone]);
                    minimum[bone] = Min(minimum[bone], local);
                    maximum[bone] = Max(maximum[bone], local);
                }
            }
            if (Mesh->VertexCount)
                bounds.Raw = {Scale(Add(lo, hi), .5f), Scale(Sub(hi, lo), .5f)};
            bounds.Bones.resize(minimum.size());
            for (size_t i = 0; i < minimum.size(); ++i)
                if (std::isfinite(minimum[i].x))
                    bounds.Bones[i] = NativeRendererBounds{Scale(Add(minimum[i], maximum[i]), .5f),
                                                           Scale(Sub(maximum[i], minimum[i]), .5f)};
            CachedBounds = std::move(bounds);
        }
        auto &bounds = *CachedBounds;
        if (!Source.Skinned || Bones.empty())
            return bounds.Raw.Transform(Node->WorldMatrix());
        Require(Bones.size() <= bounds.Bones.size(), "Bounds bone count exceeds mesh bind poses.");
        Vector3 minimum{INFINITY, INFINITY, INFINITY}, maximum{-INFINITY, -INFINITY, -INFINITY};
        for (size_t i = 0; i < Bones.size(); ++i)
        {
            if (!Bones[i] || !bounds.Bones[i])
                continue;
            auto posed = bounds.Bones[i]->Transform(Bones[i]->WorldMatrix());
            minimum = Min(minimum, Sub(posed.Center, posed.Extents));
            maximum = Max(maximum, Add(posed.Center, posed.Extents));
        }
        if (!std::isfinite(minimum.x))
            return bounds.Raw.Transform(Node->WorldMatrix());
        return {Scale(Add(minimum, maximum), .5f), Scale(Sub(maximum, minimum), .5f)};
    }
}
