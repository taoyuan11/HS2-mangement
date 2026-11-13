#pragma once
#include "Shaders/ShaderMetadata.h"

namespace hs2
{
    struct NativeRenderCamera
    {
        Vector3 Position{0, 1, -5}, Target{0, 1, 0}, Up{0, 1, 0};
        float FieldOfView = 30, Near = .01f, Far = 100;
        Vector2 ProjectionJitter{};
        bool BottomOrigin = false;
        Matrix4x4 View() const;
        Matrix4x4 Projection(float aspect, bool jittered = true) const;
        Matrix4x4 World() const;
        // 保持整个包围球位于远裁剪面内，包括拉远和偏离人物中心的平移。
        void IncludeBounds(Vector3 center, Vector3 extents);
    };

    std::vector<float> MatrixValues(const Matrix4x4 &matrix);
}
