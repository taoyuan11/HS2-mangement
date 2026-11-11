#pragma once
#include "Scene/SceneTypes.h"
#include <algorithm>
#include <cmath>
#include <limits>
#include <stdexcept>

namespace hs2::sim
{
    inline Vector3 operator+(Vector3 a, Vector3 b)
    {
        return {a.x + b.x, a.y + b.y, a.z + b.z};
    }

    inline Vector3 operator-(Vector3 a, Vector3 b)
    {
        return {a.x - b.x, a.y - b.y, a.z - b.z};
    }

    inline Vector3 operator-(Vector3 a)
    {
        return {-a.x, -a.y, -a.z};
    }

    inline Vector3 operator*(Vector3 a, float b)
    {
        return {a.x * b, a.y * b, a.z * b};
    }

    inline Vector3 operator*(float b, Vector3 a)
    {
        return a * b;
    }

    inline Vector3 operator/(Vector3 a, float b)
    {
        return {a.x / b, a.y / b, a.z / b};
    }

    inline Vector3 &operator+=(Vector3 &a, Vector3 b)
    {
        a = a + b;
        return a;
    }

    inline Vector3 &operator-=(Vector3 &a, Vector3 b)
    {
        a = a - b;
        return a;
    }

    inline float Dot(Vector3 a, Vector3 b)
    {
        return static_cast<float>(double(a.x) * b.x + double(a.y) * b.y + double(a.z) * b.z);
    }

    inline Vector3 Cross(Vector3 a, Vector3 b)
    {
        return {static_cast<float>(double(a.y) * b.z - double(a.z) * b.y),
                static_cast<float>(double(a.z) * b.x - double(a.x) * b.z),
                static_cast<float>(double(a.x) * b.y - double(a.y) * b.x)};
    }

    inline float LengthSquared(Vector3 a)
    {
        return Dot(a, a);
    }

    inline float Length(Vector3 a)
    {
        return std::sqrt(LengthSquared(a));
    }

    inline Vector3 Unit(Vector3 a, Vector3 fallback = {})
    {
        auto length = Length(a);
        return length > .00001f ? a / length : fallback;
    }

    inline Vector3 Product(Vector3 a, Vector3 b)
    {
        return {a.x * b.x, a.y * b.y, a.z * b.z};
    }

    inline Quaternion Mul(Quaternion a, Quaternion b)
    {
        return {static_cast<float>(double(a.w) * b.x + double(a.x) * b.w + double(a.y) * b.z - double(a.z) * b.y),
                static_cast<float>(double(a.w) * b.y + double(a.y) * b.w + double(a.z) * b.x - double(a.x) * b.z),
                static_cast<float>(double(a.w) * b.z + double(a.z) * b.w + double(a.x) * b.y - double(a.y) * b.x),
                static_cast<float>(double(a.w) * b.w - double(a.x) * b.x - double(a.y) * b.y - double(a.z) * b.z)};
    }

    inline Quaternion Inverse(Quaternion q)
    {
        return {-q.x, -q.y, -q.z, q.w};
    }

    inline Quaternion Normalize(Quaternion q)
    {
        float n = std::sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return n > 0 ? Quaternion{q.x / n, q.y / n, q.z / n, q.w / n} : Quaternion{0, 0, 0, 1};
    }

    inline Vector3 Rotate(Quaternion q, Vector3 p)
    {
        float x = q.x * 2, y = q.y * 2, z = q.z * 2, xx = q.x * x, yy = q.y * y, zz = q.z * z, xy = q.x * y,
              xz = q.x * z, yz = q.y * z, wx = q.w * x, wy = q.w * y, wz = q.w * z;
        return {static_cast<float>((1 - (double(yy) + zz)) * p.x + (double(xy) - wz) * p.y + (double(xz) + wy) * p.z),
                static_cast<float>((double(xy) + wz) * p.x + (1 - (double(xx) + zz)) * p.y + (double(yz) - wx) * p.z),
                static_cast<float>((double(xz) - wy) * p.x + (double(yz) + wx) * p.y + (1 - (double(xx) + yy)) * p.z)};
    }

    inline Quaternion AxisAngle(Vector3 axis, float radians)
    {
        axis = Unit(axis);
        float s = std::sin(radians * .5f);
        return {axis.x * s, axis.y * s, axis.z * s, std::cos(radians * .5f)};
    }

    inline Quaternion AngleAxisDegrees(float degrees, Vector3 axis)
    {
        float length = std::sqrt(axis.x * axis.x + axis.y * axis.y + axis.z * axis.z);
        if (length <= 1e-6f)
            return {0, 0, 0, 1};
        float angle = ((degrees / 360) * 2) * 3.14159265358979323846f * .5f, scale = std::sin(angle) / length;
        return {axis.x * scale, axis.y * scale, axis.z * scale, std::cos(angle)};
    }

    inline Quaternion FromTo(Vector3 from, Vector3 to);

    inline float Angle(Vector3 a, Vector3 b)
    {
        float denominator = std::sqrt(LengthSquared(a) * LengthSquared(b));
        return denominator < 1e-15f
                   ? 0
                   : static_cast<float>(std::acos(std::clamp(Dot(a, b) / denominator, -1.f, 1.f))) * 57.29577951308232f;
    }

    inline Vector3 Project(Vector3 v, Vector3 onto)
    {
        return LengthSquared(onto) < std::numeric_limits<float>::denorm_min()
                   ? Vector3{}
                   : onto * (Dot(v, onto) / LengthSquared(onto));
    }

    inline void OrthoNormalize(Vector3 &normal, Vector3 &tangent)
    {
        float length = std::sqrt((normal.x * normal.x + normal.y * normal.y) + normal.z * normal.z);
        normal = length > 1e-5f ? normal / length : Vector3{1, 0, 0};
        float dot = (tangent.y * normal.y + tangent.x * normal.x) + tangent.z * normal.z;
        tangent -= normal * dot;
        length = std::sqrt((tangent.x * tangent.x + tangent.y * tangent.y) + tangent.z * tangent.z);
        if (length >= 1e-5f)
            tangent = tangent / length;
        else if (std::abs(normal.z) > .70710678f)
        {
            float reciprocal = 1 / std::sqrt(normal.z * normal.z + normal.y * normal.y);
            tangent = {0, -normal.z * reciprocal, normal.y * reciprocal};
        }
        else
        {
            float reciprocal = 1 / std::sqrt(normal.x * normal.x + normal.y * normal.y);
            tangent = {-normal.y * reciprocal, normal.x * reciprocal, 0};
        }
    }

    inline Quaternion FromMatrix(const Matrix4x4 &m)
    {
        float trace = m._11 + m._22 + m._33;
        Quaternion q;
        if (trace > 0)
        {
            float root = std::sqrt(trace + 1), scale = .5f / root;
            q = {(m._23 - m._32) * scale, (m._31 - m._13) * scale, (m._12 - m._21) * scale, root * .5f};
        }
        else
        {
            float matrix[]{m._11, m._12, m._13, m._21, m._22, m._23, m._31, m._32, m._33};
            int i = m._22 > m._11 ? 1 : 0;
            if (m._33 > matrix[i * 3 + i])
                i = 2;
            int j = (i + 1) % 3, k = (j + 1) % 3;
            float root = std::sqrt(matrix[i * 3 + i] - matrix[j * 3 + j] - matrix[k * 3 + k] + 1), scale = .5f / root,
                  values[4]{};
            values[i] = root * .5f;
            values[3] = (matrix[j * 3 + k] - matrix[k * 3 + j]) * scale;
            values[j] = (matrix[i * 3 + j] + matrix[j * 3 + i]) * scale;
            values[k] = (matrix[i * 3 + k] + matrix[k * 3 + i]) * scale;
            q = {values[0], values[1], values[2], values[3]};
        }
        return Normalize(q);
    }

    // 引擎四元数辅助函数使用单精度向量运算；
    // 上方通用控制器的点积和叉积则保留原托管实现的双精度中间值。
    inline Vector3 NativeNormalized(Vector3 value)
    {
        float length = std::sqrt(value.x * value.x + value.y * value.y + value.z * value.z);
        return length >= 1e-5f ? value / length : Vector3{};
    }

    inline Vector3 NativeCross(Vector3 a, Vector3 b)
    {
        return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
    }

    inline Quaternion FromToNormalized(Vector3 from, Vector3 to)
    {
        float dot = from.x * to.x + from.y * to.y + from.z * to.z;
        if (dot > .999999)
            return {0, 0, 0, 1};
        if (dot < -.999999)
        {
            auto u = NativeCross(from, {1, 0, 0});
            if (u.x * u.x + u.y * u.y + u.z * u.z < 1e-6f)
                u = NativeCross(from, {0, 1, 0});
            float inverse = 1 / std::sqrt(u.x * u.x + u.y * u.y + u.z * u.z);
            u = u * inverse;
            auto v = NativeCross(u, from);
            return FromMatrix({-from.x * from.x + v.x * v.x - u.x * u.x, -from.x * from.y + v.x * v.y - u.x * u.y,
                               -from.x * from.z + v.x * v.z - u.x * u.z, 0, -from.x * from.y + v.x * v.y - u.x * u.y,
                               -from.y * from.y + v.y * v.y - u.y * u.y, -from.y * from.z + v.y * v.z - u.y * u.z, 0,
                               -from.x * from.z + v.x * v.z - u.x * u.z, -from.y * from.z + v.y * v.z - u.y * u.z,
                               v.z * v.z - from.z * from.z - u.z * u.z, 0, 0, 0, 0, 1});
        }
        auto c = NativeCross(from, to);
        float h = (1 - dot) / (c.y * c.y + c.x * c.x + c.z * c.z);
        float xx = h * c.x, yy = c.y * h, zz = h * c.z, xy = xx * c.y, xz = xx * c.z, yz = zz * c.y;
        return FromMatrix({xx * c.x + dot, c.z + xy, xz - c.y, 0, xy - c.z, yy * c.y + dot, c.x + yz, 0, c.y + xz,
                           yz - c.x, c.z * zz + dot, 0, 0, 0, 0, 1});
    }

    inline Quaternion FromTo(Vector3 from, Vector3 to)
    {
        from = NativeNormalized(from);
        to = NativeNormalized(to);
        if (LengthSquared(from) == 0 || LengthSquared(to) == 0)
            return {0, 0, 0, 1};
        return FromToNormalized(from, to);
    }

    inline Quaternion LookRotation(Vector3 forward, Vector3 upwards)
    {
        float forwardLength = std::sqrt(forward.x * forward.x + forward.y * forward.y + forward.z * forward.z);
        auto z = NativeNormalized(forward);
        if (LengthSquared(z) == 0)
            return {0, 0, 0, 1};
        auto x = NativeNormalized(NativeCross(upwards, z));
        if (LengthSquared(x) == 0)
            return forwardLength > 1e-5f ? FromToNormalized({0, 0, 1}, z) : Quaternion{0, 0, 0, 1};
        auto y = NativeCross(z, x);
        Matrix4x4 matrix{x.x, x.y, x.z, 0, y.x, y.y, y.z, 0, z.x, z.y, z.z, 0, 0, 0, 0, 1};
        return FromMatrix(matrix);
    }

    inline Vector3 Lerp(Vector3 a, Vector3 b, float t)
    {
        t = std::clamp(t, 0.f, 1.f);
        return {static_cast<float>(a.x + (double(b.x) - a.x) * t), static_cast<float>(a.y + (double(b.y) - a.y) * t),
                static_cast<float>(a.z + (double(b.z) - a.z) * t)};
    }

    inline Quaternion Lerp(Quaternion a, Quaternion b, float t)
    {
        t = std::clamp(t, 0.f, 1.f);
        float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
        return Normalize(dot < 0 ? Quaternion{a.x - (b.x + a.x) * t, a.y - (b.y + a.y) * t, a.z - (b.z + a.z) * t,
                                              a.w - (b.w + a.w) * t}
                                 : Quaternion{a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t,
                                              a.w + (b.w - a.w) * t});
    }

    inline Vector3 Slerp(Vector3 a, Vector3 b, float t)
    {
        t = std::clamp(t, 0.f, 1.f);
        float al = std::sqrt(a.x * a.x + a.y * a.y + a.z * a.z), bl = std::sqrt(b.x * b.x + b.y * b.y + b.z * b.z),
              inverseT = 1 - t;
        if (al < 1e-5f || bl < 1e-5f)
            return a * inverseT + b * t;
        float length = al * inverseT + bl * t, dot = (a.x * b.x + a.y * b.y + a.z * b.z) / (bl * al);
        if (dot > .99999f)
            return a * inverseT + b * t;
        auto n = a / al;
        Vector3 axis;
        float angle;
        if (dot < -.99999f)
        {
            if (std::abs(n.z) > .70710678f)
            {
                float r = 1 / std::sqrt(n.z * n.z + n.y * n.y);
                axis = {0, -n.z * r, n.y * r};
            }
            else
            {
                float r = 1 / std::sqrt(n.x * n.x + n.y * n.y);
                axis = {-n.y * r, n.x * r, 0};
            }
            angle = t * 3.14159265358979323846f;
        }
        else
        {
            axis = {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
            axis = axis / std::sqrt(axis.x * axis.x + axis.y * axis.y + axis.z * axis.z);
            angle = std::acos(dot) * t;
        }
        float s = std::sin(angle), c = std::cos(angle), h = 1 - c, xs = axis.x * s, ys = axis.y * s, zs = axis.z * s,
              xy = (axis.y * axis.x) * h, xz = (axis.z * axis.x) * h, yz = (axis.z * axis.y) * h,
              xx = (axis.x * axis.x) * h + c, yy = (axis.y * axis.y) * h + c, zz = (axis.z * axis.z) * h + c;
        return {((n.x * xx + n.y * (xy - zs)) + n.z * (xz + ys)) * length,
                ((n.x * (xy + zs) + n.y * yy) + n.z * (yz - xs)) * length,
                ((n.x * (xz - ys) + n.y * (yz + xs)) + n.z * zz) * length};
    }

    inline Vector3 Vector(const Parameter &p)
    {
        return {p.Float("x"), p.Float("y"), p.Float("z")};
    }

    inline const Parameter &At(const Parameter &p, size_t i)
    {
        static const Parameter empty;
        return i < p.Children.size() ? p.Children[i] : empty;
    }

    inline bool Descendant(const NativeSceneNode *node, const NativeSceneNode *ancestor)
    {
        for (auto *current = node; current; current = current->Parent())
            if (current == ancestor)
                return true;
        return false;
    }

    inline Matrix4x4 Translation(Matrix4x4 matrix, Vector3 position)
    {
        matrix._41 = position.x;
        matrix._42 = position.y;
        matrix._43 = position.z;
        return matrix;
    }

    inline float Lerp(float a, float b, float t)
    {
        return a + (b - a) * std::clamp(t, 0.f, 1.f);
    }

    inline void Require(bool value, const char *message)
    {
        if (!value)
            throw std::runtime_error(message);
    }
}
