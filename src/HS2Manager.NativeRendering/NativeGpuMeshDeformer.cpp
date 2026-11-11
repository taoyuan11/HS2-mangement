#include "NativeGpuMeshDeformer.h"
#include <d3dcompiler.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <stdexcept>

namespace hs2 {
namespace {
constexpr char Code[]=R"hlsl(
struct SourceVertex {float4 position;float4 normal;float4 tangent;float4 weights;uint4 bones;};
struct Delta {float4 position;float4 normal;float4 tangent;};
struct Bone {row_major float4x4 transform;};
StructuredBuffer<SourceVertex> vertices:register(t0);
StructuredBuffer<Bone> bones:register(t1);
StructuredBuffer<uint2> ranges:register(t2);
StructuredBuffer<Delta> deltas:register(t3);
StructuredBuffer<float> coefficients:register(t4);
RWByteAddressBuffer outputVertices:register(u0);
cbuffer Settings:register(b0) {uint vertexCount;uint skinned;uint blendCount;uint normalScaled;row_major float4x4 normalCompensation;};
[numthreads(64,1,1)] void CS(uint3 dispatch:SV_DispatchThreadID) {
    uint index=dispatch.x;if(index>=vertexCount)return;SourceVertex v=vertices[index];
    precise float3 p=v.position.xyz;precise float3 n=v.normal.xyz;precise float3 t=v.tangent.xyz;
    if(blendCount!=0){uint2 range=ranges[index];for(uint i=0;i<range.y;i++){Delta delta=deltas[range.x+i];float amount=coefficients[(uint)delta.position.w];p+=delta.position.xyz*amount;n+=delta.normal.xyz*amount;t+=delta.tangent.xyz*amount;}}
    if(skinned==1){precise float4 bx=0,by=0,bz=0,bt=0;[unroll]for(uint influence=0;influence<4;influence++){float weight=v.weights[influence];if(weight!=0){Bone bone=bones[v.bones[influence]];bx+=bone.transform[0]*weight;by+=bone.transform[1]*weight;bz+=bone.transform[2]*weight;bt+=bone.transform[3]*weight;}}p=((p.x*bx.xyz+p.y*by.xyz)+p.z*bz.xyz)+bt.xyz;n=(n.x*bx.xyz+n.y*by.xyz)+n.z*bz.xyz;t=(t.x*bx.xyz+t.y*by.xyz)+t.z*bz.xyz;}
    else if(skinned==2){Bone bone=bones[0];p=mul(float4(p,1),bone.transform).xyz;n=mul(float4(n,0),bone.transform).xyz;t=mul(float4(t,0),bone.transform).xyz;}
    if(normalScaled!=0)n=mul(float4(n,0),normalCompensation).xyz;
    outputVertices.Store4(index*48,asuint(float4(p,1)));outputVertices.Store4(index*48+16,asuint(float4(n,0)));outputVertices.Store4(index*48+32,asuint(float4(t,v.tangent.w)));
})hlsl";
struct Structured {
    ComPtr<ID3D11Buffer> Buffer;ComPtr<ID3D11ShaderResourceView> View;
    Structured()=default;
    Structured(GraphicsDevice& g,UINT bytes,UINT stride,const void* data=nullptr){D3D11_BUFFER_DESC d{};d.ByteWidth=std::max(bytes,stride);d.BindFlags=D3D11_BIND_SHADER_RESOURCE;d.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;d.StructureByteStride=stride;D3D11_SUBRESOURCE_DATA initial{};initial.pSysMem=data;CheckHResult(g.Device()->CreateBuffer(&d,data?&initial:nullptr,&Buffer),"Create deformation structured buffer");CheckHResult(g.Device()->CreateShaderResourceView(Buffer.Get(),nullptr,&View),"Create deformation structured view");}
};
struct SourceVertex {Vector4 Position,Normal,Tangent,Weights;std::array<UINT,4>Bones;};
struct Delta {Vector4 Position,Normal,Tangent;};
static_assert(sizeof(SourceVertex)==80&&sizeof(Delta)==48);
Vector4 Point(Vector3 v,float w){return {v.x,v.y,v.z,w};}
Matrix4x4 Transpose(const Matrix4x4& m){Matrix4x4 result;DirectX::XMStoreFloat4x4(&result,DirectX::XMMatrixTranspose(DirectX::XMLoadFloat4x4(&m)));return result;}
}
struct NativeGpuMeshDeformer::State {
    std::shared_ptr<GpuMeshData> Mesh;
    bool Skinned=false;
    Structured Vertices,Ranges,Deltas,Bones,Coefficients;
    std::vector<int> FrameOffsets;
    int FrameCount=0,Current=-1;
    std::array<ComPtr<ID3D11Buffer>,2> Output;
    std::array<ComPtr<ID3D11UnorderedAccessView>,2> Views;
    State(GraphicsDevice& g,const NativeSceneRenderer& renderer):Mesh(renderer.Mesh),Skinned(renderer.Source.Skinned&&!Mesh->BindPoses.empty()){
        const auto& mesh=*Mesh;mesh.Validate();if(mesh.VertexCount<1)throw std::invalid_argument("Cannot deform an empty mesh.");
        auto has=[&](int semantic){return std::any_of(mesh.Channels.begin(),mesh.Channels.end(),[&](const auto& c){return c.Semantic==semantic;});};
        if(Skinned&&(!has(12)||!has(13)))throw std::runtime_error("Skinned mesh lacks weight/index channels.");
        std::vector<SourceVertex> source(mesh.VertexCount);
        for(int v=0;v<mesh.VertexCount;++v){auto& vertex=source[v];vertex.Position={mesh.Read(0,v,0),mesh.Read(0,v,1),mesh.Read(0,v,2),1};vertex.Normal={mesh.Read(1,v,0),mesh.Read(1,v,1),mesh.Read(1,v,2),0};vertex.Tangent={mesh.Read(2,v,0),mesh.Read(2,v,1),mesh.Read(2,v,2),mesh.Read(2,v,3)};std::array<float,4>weights{};float total=0;
            for(int i=0;i<4;++i){float weight=Skinned?mesh.Read(12,v,i):0;weights[i]=weight;if(!std::isfinite(weight)||weight<0)throw std::runtime_error("Invalid GPU skin weight.");if(weight==0)continue;float index=mesh.Read(13,v,i);if(!std::isfinite(index)||index<0||index>=mesh.BindPoses.size()||std::floor(index)!=index)throw std::runtime_error("Invalid GPU skin bone index.");vertex.Bones[i]=static_cast<UINT>(index);total+=weight;}
            if(Skinned&&total<=0)throw std::runtime_error("GPU vertex has no skin influences.");vertex.Weights={weights[0],weights[1],weights[2],weights[3]};
        }
        Vertices=Structured(g,static_cast<UINT>(source.size()*sizeof(SourceVertex)),sizeof(SourceVertex),source.data());
        FrameOffsets.resize(mesh.BlendShapes.size()+1);std::vector<std::vector<Delta>> entries(mesh.VertexCount);
        for(size_t shape=0;shape<mesh.BlendShapes.size();++shape){const auto& frames=mesh.BlendShapes[shape].Frames;FrameOffsets[shape]=FrameCount;for(size_t f=0;f<frames.size();++f){const auto& frame=frames[f];if(!std::isfinite(frame.Weight)||frame.Weight<=0||(f&&frame.Weight<=frames[f-1].Weight))throw std::runtime_error("GPU blend weights must be positive and increasing.");for(size_t i=0;i<frame.Indices.size();++i)entries.at(frame.Indices[i]).push_back({Point(frame.Positions[i],static_cast<float>(FrameCount)),Point(frame.Normals[i],0),Point(frame.Tangents[i],0)});++FrameCount;}}
        FrameOffsets.back()=FrameCount;std::vector<UINT> ranges(mesh.VertexCount*2);std::vector<Delta>deltas;
        for(int v=0;v<mesh.VertexCount;++v){ranges[v*2]=static_cast<UINT>(deltas.size());ranges[v*2+1]=static_cast<UINT>(entries[v].size());deltas.insert(deltas.end(),entries[v].begin(),entries[v].end());}if(deltas.empty())deltas.resize(1);
        Ranges=Structured(g,static_cast<UINT>(ranges.size()*4),8,ranges.data());Deltas=Structured(g,static_cast<UINT>(deltas.size()*sizeof(Delta)),sizeof(Delta),deltas.data());
        Bones=Structured(g,static_cast<UINT>(std::max<size_t>(1,mesh.BindPoses.size())*64),64);Coefficients=Structured(g,std::max(1,FrameCount)*4,4);
        for(int i=0;i<2;++i){D3D11_BUFFER_DESC d{};d.ByteWidth=mesh.VertexCount*NativeGpuVertexStream::Stride;d.BindFlags=D3D11_BIND_VERTEX_BUFFER|D3D11_BIND_UNORDERED_ACCESS;d.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS;CheckHResult(g.Device()->CreateBuffer(&d,nullptr,&Output[i]),"Create deformed vertex stream");D3D11_UNORDERED_ACCESS_VIEW_DESC view{};view.Format=DXGI_FORMAT_R32_TYPELESS;view.ViewDimension=D3D11_UAV_DIMENSION_BUFFER;view.Buffer.NumElements=d.ByteWidth/4;view.Buffer.Flags=D3D11_BUFFER_UAV_FLAG_RAW;CheckHResult(g.Device()->CreateUnorderedAccessView(Output[i].Get(),&view,&Views[i]),"Create deformation output view");}
    }
    std::vector<float> Weights(std::span<const float> weights)const{if(weights.size()!=Mesh->BlendShapes.size())throw std::invalid_argument("GPU blend weights differ from source mesh.");std::vector<float> result(std::max(1,FrameCount));for(size_t shape=0;shape<weights.size();++shape){const float weight=weights[shape];if(!std::isfinite(weight))throw std::runtime_error("Non-finite GPU blend weight.");const auto& frames=Mesh->BlendShapes[shape].Frames;if(frames.empty()||weight==0)continue;const int offset=FrameOffsets[shape];auto found=std::find_if(frames.begin(),frames.end(),[&](const auto& f){return f.Weight>=weight;});auto upper=static_cast<size_t>(found-frames.begin());if(upper==0||frames.size()==1)result[offset]=weight/frames[0].Weight;else{if(upper==frames.size())upper=frames.size()-1;const float amount=(weight-frames[upper-1].Weight)/(frames[upper].Weight-frames[upper-1].Weight);result[offset+upper-1]=1-amount;result[offset+upper]=amount;}}return result;}
};
NativeGpuMeshDeformer::NativeGpuMeshDeformer(GraphicsDevice& g):graphics_(g){
    g.VerifyThread();ComPtr<ID3DBlob> code,error;const auto result=D3DCompile(Code,sizeof(Code)-1,"NativeMeshSkin",nullptr,nullptr,"CS","cs_5_0",D3DCOMPILE_IEEE_STRICTNESS,0,&code,&error);if(FAILED(result))throw std::runtime_error(error?static_cast<const char*>(error->GetBufferPointer()):"GPU deformation shader compilation failed.");CheckHResult(g.Device()->CreateComputeShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&shader_),"Create GPU deformation shader");constants_=g.CreateBuffer(80,D3D11_BIND_CONSTANT_BUFFER);
}
NativeGpuMeshDeformer::~NativeGpuMeshDeformer()=default;
NativeGpuVertexStream NativeGpuMeshDeformer::Deform(const NativeSceneRenderer& renderer){
    graphics_.VerifyThread();if(!renderer.HasGeometry())throw std::invalid_argument("Cannot deform an empty renderer.");auto& pointer=states_[&renderer];if(!pointer)pointer=std::make_unique<State>(graphics_,renderer);auto& state=*pointer;if(state.Mesh!=renderer.Mesh)throw std::runtime_error("Renderer mesh changed without rebuilding GPU state.");int last=state.Current;state.Current=(state.Current+1)&1;auto* output=state.Output[state.Current].Get();auto* context=graphics_.Context();context->ClearState();
    if(renderer.SimulatedVertices){const auto& simulated=*renderer.SimulatedVertices;const auto count=static_cast<size_t>(renderer.Mesh->VertexCount);if(simulated.Positions.size()!=count||simulated.Normals.size()!=count||simulated.Tangents.size()!=count)throw std::runtime_error("Simulated mesh size differs from source.");std::vector<Vector4> data(count*3);const auto transform=MultiplyMatrix(renderer.Node->WorldMatrix(),renderer.RenderWorldToLocal());const auto normalTransform=MultiplyMatrix(Transpose(renderer.Node->InverseWorldMatrix()),Transpose(renderer.RenderWorldMatrix()));for(size_t i=0;i<count;++i){data[i*3]=Point(TransformPoint(simulated.Positions[i],transform),1);data[i*3+1]=Point(TransformVector(simulated.Normals[i],normalTransform),0);auto t=simulated.Tangents[i];data[i*3+2]=Point(TransformVector({t.x,t.y,t.z},transform),t.w);}context->UpdateSubresource(output,0,nullptr,data.data(),0,0);
    }else{
        std::vector<Matrix4x4> matrices(std::max<size_t>(1,renderer.Mesh->BindPoses.size()),IdentityMatrix());if(renderer.Source.Skinned){if(renderer.Bones.size()!=renderer.Mesh->BindPoses.size())throw std::runtime_error("GPU bones differ from bind poses.");if(!renderer.Bones.empty())matrices=renderer.SkinMatrices(renderer.RenderWorldToLocal());}
        context->UpdateSubresource(state.Bones.Buffer.Get(),0,nullptr,matrices.data(),0,0);auto coefficients=state.Weights(renderer.BlendWeights);context->UpdateSubresource(state.Coefficients.Buffer.Get(),0,nullptr,coefficients.data(),0,0);
        struct Settings{UINT Count,Skinned,BlendCount,NormalScaled;Matrix4x4 Compensation;};Settings settings{static_cast<UINT>(renderer.Mesh->VertexCount),state.Skinned?1u:0u,static_cast<UINT>(state.FrameCount),0,MultiplyMatrix(renderer.RenderWorldMatrix(),Transpose(renderer.RenderWorldMatrix()))};static_assert(sizeof(Settings)==80);context->UpdateSubresource(constants_.Get(),0,nullptr,&settings,0,0);
        auto* constant=constants_.Get();context->CSSetShader(shader_.Get(),nullptr,0);context->CSSetConstantBuffers(0,1,&constant);ID3D11ShaderResourceView* views[]{state.Vertices.View.Get(),state.Bones.View.Get(),state.Ranges.View.Get(),state.Deltas.View.Get(),state.Coefficients.View.Get()};context->CSSetShaderResources(0,5,views);auto* view=state.Views[state.Current].Get();context->CSSetUnorderedAccessViews(0,1,&view,nullptr);context->Dispatch((renderer.Mesh->VertexCount+63)/64,1,1);context->ClearState();
    }
    return {output,renderer.Mesh->VertexCount,last<0?nullptr:state.Output[last].Get()};
}
NativeDeformedVertices NativeGpuMeshDeformer::Readback(NativeGpuVertexStream stream){
    graphics_.VerifyThread();if(!stream.Buffer||stream.VertexCount<1)throw std::invalid_argument("Invalid GPU vertex stream.");D3D11_BUFFER_DESC d{};stream.Buffer->GetDesc(&d);if(d.ByteWidth!=static_cast<UINT>(stream.VertexCount)*NativeGpuVertexStream::Stride)throw std::runtime_error("GPU stream byte size differs from vertex count.");d.BindFlags=0;d.Usage=D3D11_USAGE_STAGING;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;d.MiscFlags=0;d.StructureByteStride=0;ComPtr<ID3D11Buffer> staging;CheckHResult(graphics_.Device()->CreateBuffer(&d,nullptr,&staging),"Create deformation readback");graphics_.Context()->CopyResource(staging.Get(),stream.Buffer);
    NativeDeformedVertices output;output.Positions.resize(stream.VertexCount);output.Normals.resize(stream.VertexCount);output.Tangents.resize(stream.VertexCount);D3D11_MAPPED_SUBRESOURCE mapped{};CheckHResult(graphics_.Context()->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped),"Map deformation readback");const auto* values=static_cast<const Vector4*>(mapped.pData);for(int i=0;i<stream.VertexCount;++i){const auto& p=values[i*3];const auto& n=values[i*3+1];output.Positions[i]={p.x,p.y,p.z};output.Normals[i]={n.x,n.y,n.z};output.Tangents[i]=values[i*3+2];}graphics_.Context()->Unmap(staging.Get(),0);return output;
}
}
