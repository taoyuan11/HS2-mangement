#pragma once
#include "Assets/AssetWriter.h"
#include "Common/Core.h"
#include <algorithm>
#include <bit>

namespace hs2::asset::fields
{
    inline Parameter &At(Parameter &value, std::string_view name)
    {
        auto it = std::find_if(value.Children.begin(), value.Children.end(),
                               [&](const auto &child)
                               {
                                   return child.Name == name;
                               });
        if (it == value.Children.end())
            throw std::runtime_error("模板缺少字段：" + std::string(name));
        return *it;
    }

    inline void Integer(Parameter &value, std::int64_t n)
    {
        if (value.Encoding == "bool" || value.Encoding == "float")
            value.Number = static_cast<double>(n);
        else if (value.Encoding == "uint")
        {
            value.Unsigned = static_cast<std::uint64_t>(n);
            value.Number = static_cast<double>(n);
        }
        else
        {
            value.Encoding = "int";
            value.Integer = n;
            value.Number = static_cast<double>(n);
        }
    }

    inline void Set(Parameter &value, std::string_view field, std::int64_t n)
    {
        Integer(At(value, field), n);
    }

    inline void Float(Parameter &value, std::string_view field, float n)
    {
        auto &v = At(value, field);
        v.Encoding = "float";
        v.Number = n;
    }

    inline void Text(Parameter &value, std::string_view field, std::string text)
    {
        auto &v = At(value, field);
        v.Encoding = "string";
        v.Text = std::move(text);
    }

    inline void Optional(Parameter &value, std::string_view field, std::int64_t n)
    {
        if (value.Has(field))
            Set(value, field, n);
    }

    inline void Vector(Parameter &value, Vector3 v)
    {
        Float(value, "x", v.x);
        Float(value, "y", v.y);
        Float(value, "z", v.z);
    }

    inline void Vector(Parameter &value, Vector4 v)
    {
        Vector(value, Vector3{v.x, v.y, v.z});
        Float(value, "w", v.w);
    }

    inline Parameter Pointer(std::int64_t id, std::string name = "data")
    {
        Parameter p;
        p.Name = std::move(name);
        p.Encoding = "object";
        Parameter f;
        f.Name = "m_FileID";
        f.Encoding = "int";
        Parameter i;
        i.Name = "m_PathID";
        i.Encoding = "int";
        i.Integer = id;
        i.Number = static_cast<double>(id);
        p.Children = {f, i};
        return p;
    }

    inline void Point(Parameter &value, std::string_view field, std::int64_t id)
    {
        At(value, field) = Pointer(id, std::string(field));
    }

    inline Parameter &Array(Parameter &value)
    {
        return value.Has("Array") ? At(value, "Array") : value;
    }

    inline void Clear(Parameter &value)
    {
        Array(value).Children.clear();
        Array(value).Bytes.clear();
    }

    inline void Pointers(Parameter &value, std::string_view field, std::span<const std::int64_t> ids)
    {
        auto &a = Array(At(value, field));
        a.Children.clear();
        a.Bytes.clear();
        for (auto id : ids)
            a.Children.push_back(Pointer(id));
    }

    template <class T> inline void Binary(Bytes &bytes, T value)
    {
        auto b = std::bit_cast<std::array<std::uint8_t, sizeof(T)>>(value);
        bytes.insert(bytes.end(), b.begin(), b.end());
    }
}
