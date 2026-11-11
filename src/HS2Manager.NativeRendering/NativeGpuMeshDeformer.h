#pragma once
#include "NativeMaterialPass.h"

namespace hs2 {
// Retains source meshes and ping-pong deformed streams for all material/shadow passes.
class NativeGpuMeshDeformer final {
public:
    explicit NativeGpuMeshDeformer(GraphicsDevice& graphics);
    ~NativeGpuMeshDeformer();
    NativeGpuVertexStream Deform(const NativeSceneRenderer& renderer);
    NativeDeformedVertices Readback(NativeGpuVertexStream stream);
private:
    struct State;
    GraphicsDevice& graphics_;
    ComPtr<ID3D11ComputeShader> shader_;
    ComPtr<ID3D11Buffer> constants_;
    std::unordered_map<const NativeSceneRenderer*,std::unique_ptr<State>> states_;
};
}
