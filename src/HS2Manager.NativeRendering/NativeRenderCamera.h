#pragma once
#include "ShaderMetadata.h"

namespace hs2 {
struct NativeRenderCamera {
    Vector3 Position{0,1,-5},Target{0,1,0},Up{0,1,0};
    float FieldOfView=30,Near=.01f,Far=100;
    Vector2 ProjectionJitter{};
    bool BottomOrigin=false;
    Matrix4x4 View()const;
    Matrix4x4 Projection(float aspect,bool jittered=true)const;
    Matrix4x4 World()const;
};
std::vector<float> MatrixValues(const Matrix4x4& matrix);
}
