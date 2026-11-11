#pragma once
#include "NativeRenderCamera.h"
#include <cmath>

namespace hs2 {
inline Vector3 Add(Vector3 a,Vector3 b){return {a.x+b.x,a.y+b.y,a.z+b.z};}
inline Vector3 Sub(Vector3 a,Vector3 b){return {a.x-b.x,a.y-b.y,a.z-b.z};}
inline Vector3 Mul(Vector3 a,float b){return {a.x*b,a.y*b,a.z*b};}
inline float Dot(Vector3 a,Vector3 b){return (a.x*b.x+a.y*b.y)+a.z*b.z;}
inline float LengthSquared(Vector3 a){return Dot(a,a);}
inline Vector3 Cross(Vector3 a,Vector3 b){return {a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x};}
inline Vector3 Unit(Vector3 value){const float length=std::sqrt(LengthSquared(value));return length>1e-20f?Mul(value,1/length):Vector3{};}
inline float Determinant(const Matrix4x4& matrix){return DirectX::XMVectorGetX(DirectX::XMMatrixDeterminant(DirectX::XMLoadFloat4x4(&matrix)));}
inline Matrix4x4 MatrixScale(float x,float y,float z){Matrix4x4 m;DirectX::XMStoreFloat4x4(&m,DirectX::XMMatrixScaling(x,y,z));return m;}
inline Matrix4x4 MatrixTranspose(const Matrix4x4& m){Matrix4x4 result;DirectX::XMStoreFloat4x4(&result,DirectX::XMMatrixTranspose(DirectX::XMLoadFloat4x4(&m)));return result;}
inline std::vector<float> Values(Vector3 v){return {v.x,v.y,v.z};}
inline std::vector<float> Values(Vector4 v){return {v.x,v.y,v.z,v.w};}
inline Vector4 V4(Vector3 v,float w){return {v.x,v.y,v.z,w};}
inline Vector3 Rotate(Quaternion q,Vector3 v){Vector3 result;DirectX::XMStoreFloat3(&result,DirectX::XMVector3Rotate(DirectX::XMLoadFloat3(&v),DirectX::XMLoadFloat4(&q)));return result;}
inline Quaternion LookRotation(Vector3 forward,Vector3 up){forward=Unit(forward);auto right=Unit(Cross(up,forward));up=Cross(forward,right);Matrix4x4 m{right.x,right.y,right.z,0,up.x,up.y,up.z,0,forward.x,forward.y,forward.z,0,0,0,0,1};Quaternion result;DirectX::XMStoreFloat4(&result,DirectX::XMQuaternionRotationMatrix(DirectX::XMLoadFloat4x4(&m)));return result;}
inline void SetTarget(GraphicsDevice& g,ID3D11RenderTargetView* target,int width,int height,ID3D11DepthStencilView* depth=nullptr){g.Context()->OMSetRenderTargets(target?1:0,target?&target:nullptr,depth);D3D11_VIEWPORT viewport{0,0,static_cast<float>(width),static_cast<float>(height),0,1};g.Context()->RSSetViewports(1,&viewport);}
inline std::string LowerAscii(std::string value){for(auto& c:value)if(c>='A'&&c<='Z')c+=32;return value;}
inline std::string RenderPathKey(const std::filesystem::path& path){auto value=path.u8string();return {reinterpret_cast<const char*>(value.data()),value.size()};}
}
