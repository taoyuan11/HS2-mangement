#pragma once
#include "Materials/NativeMaterialPass.h"

namespace hs2
{
    // 保留源网格与双缓冲形变数据流，供全部材质和阴影通道复用。
    class NativeGpuMeshDeformer final
    {
    public:
        explicit NativeGpuMeshDeformer(GraphicsDevice &graphics);
        ~NativeGpuMeshDeformer();
        NativeGpuVertexStream Deform(const NativeSceneRenderer &renderer);
        NativeDeformedVertices Readback(NativeGpuVertexStream stream);

    private:
        struct State;
        GraphicsDevice &graphics_;
        ComPtr<ID3D11ComputeShader> shader_;
        ComPtr<ID3D11Buffer> constants_;
        std::unordered_map<const NativeSceneRenderer *, std::unique_ptr<State>> states_;
    };
}
