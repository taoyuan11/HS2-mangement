#include "NativeMaterialPass.h"
#include <d3dcompiler.h>
#include <d3d11shader.h>
#include <algorithm>
#include <cctype>
#include <stdexcept>

namespace hs2 {
namespace {
D3D11_COMPARISON_FUNC Compare(int v) {
    switch(v){case 0:case 8:return D3D11_COMPARISON_ALWAYS;case 1:return D3D11_COMPARISON_NEVER;case 2:return D3D11_COMPARISON_LESS;case 3:return D3D11_COMPARISON_EQUAL;case 4:return D3D11_COMPARISON_LESS_EQUAL;case 5:return D3D11_COMPARISON_GREATER;case 6:return D3D11_COMPARISON_NOT_EQUAL;case 7:return D3D11_COMPARISON_GREATER_EQUAL;default:throw std::runtime_error("Unknown depth comparison.");}
}
int ReverseCompare(int v){switch(v){case 2:return 5;case 4:return 7;case 5:return 2;case 7:return 4;default:return v;}}
D3D11_STENCIL_OP StencilOp(int v){if(v<0||v>7)throw std::runtime_error("Unknown stencil operation.");return static_cast<D3D11_STENCIL_OP>(v+1);}
D3D11_BLEND BlendFactor(int v){switch(v){case 0:return D3D11_BLEND_ZERO;case 1:return D3D11_BLEND_ONE;case 2:return D3D11_BLEND_DEST_COLOR;case 3:return D3D11_BLEND_SRC_COLOR;case 4:return D3D11_BLEND_INV_DEST_COLOR;case 5:return D3D11_BLEND_SRC_ALPHA;case 6:return D3D11_BLEND_INV_SRC_COLOR;case 7:return D3D11_BLEND_DEST_ALPHA;case 8:return D3D11_BLEND_INV_DEST_ALPHA;case 9:return D3D11_BLEND_SRC_ALPHA_SAT;case 10:case 11:return D3D11_BLEND_INV_SRC_ALPHA;default:throw std::runtime_error("Unknown blend factor.");}}
D3D11_BLEND AlphaFactor(int v){switch(v){case 2:v=7;break;case 3:v=5;break;case 4:v=8;break;case 6:v=10;break;case 9:v=1;break;default:break;}return BlendFactor(v);}
D3D11_BLEND_OP BlendOperation(int v){if(v<0||v>4)throw std::runtime_error("Logical blend operations require D3D11.1.");return static_cast<D3D11_BLEND_OP>(v+1);}
DXGI_FORMAT InputFormat(int c){switch(c){case 1:return DXGI_FORMAT_R32_FLOAT;case 2:return DXGI_FORMAT_R32G32_FLOAT;case 3:return DXGI_FORMAT_R32G32B32_FLOAT;case 4:return DXGI_FORMAT_R32G32B32A32_FLOAT;default:throw std::runtime_error("Invalid vertex component count.");}}
int Semantic(std::string name,UINT index){for(char& c:name)c=static_cast<char>(std::toupper(static_cast<unsigned char>(c)));if(name=="POSITION")return 0;if(name=="NORMAL")return 1;if(name=="TANGENT")return 2;if(name=="COLOR")return 3;if(name=="TEXCOORD"&&index<8)return 4+index;if(name=="BLENDWEIGHT")return 12;if(name=="BLENDINDICES")return 13;throw std::runtime_error("Unsupported vertex semantic: "+name);}
UINT ColorMask(int v){return ((v&8)>>3)|((v&4)>>1)|((v&2)<<1)|((v&1)<<3);}
}
NativeMaterialPass::NativeMaterialPass(GraphicsDevice& g,const NativeShaderAsset& shader,const NativeShaderPass& pass,
    const std::unordered_set<std::string>& keywords,const std::unordered_map<std::string,float>& properties,
    bool reversedDepth,std::optional<D3D11_CULL_MODE> culling,bool disableDepthOffset,
    std::optional<D3D11_DEPTH_STENCIL_DESC> depthOverride,std::optional<UINT> stencilOverride):graphics_(g){
    g.VerifyThread();vertex_=SelectShaderVariant(shader,pass,NativeShaderStage::Vertex,keywords);pixel_=SelectShaderVariant(shader,pass,NativeShaderStage::Pixel,keywords);
    if(ValidateShaderBytecode(vertex_.Program.Bytecode)!=NativeShaderStage::Vertex||ValidateShaderBytecode(pixel_.Program.Bytecode)!=NativeShaderStage::Pixel)throw std::runtime_error("Shader bytecode stage differs from metadata.");
    std::unordered_set<std::string> textureNames;
    for(const auto* variant:{&vertex_,&pixel_})for(const auto& t:variant->Textures)if(textureNames.insert(t.Name).second)textureBindings_.push_back(t);
    for(const auto* stage:{"progGeometry","progHull","progDomain"})if(!pass.Metadata.Get(stage).Get("m_SubPrograms").Children.empty())throw std::runtime_error("Material requires additional GPU stages.");
    CheckHResult(g.Device()->CreateVertexShader(vertex_.Program.Bytecode.data(),vertex_.Program.Bytecode.size(),nullptr,&vertexShader_),"Create material vertex shader");
    CheckHResult(g.Device()->CreatePixelShader(pixel_.Program.Bytecode.data(),pixel_.Program.Bytecode.size(),nullptr,&pixelShader_),"Create material pixel shader");
    ComPtr<ID3D11ShaderReflection> reflection;
    CheckHResult(D3DReflect(vertex_.Program.Bytecode.data(),vertex_.Program.Bytecode.size(),IID_PPV_ARGS(&reflection)),"Reflect material input signature");
    D3D11_SHADER_DESC sd{};CheckHResult(reflection->GetDesc(&sd),"Read material shader description");
    for(UINT i=0;i<sd.InputParameters;++i){D3D11_SIGNATURE_PARAMETER_DESC p{};CheckHResult(reflection->GetInputParameterDesc(i,&p),"Read input parameter");if(p.SystemValueType!=D3D_NAME_UNDEFINED)continue;
        UINT components=0;for(UINT mask=p.Mask;mask;mask>>=1)++components;if(!components)components=4;
        signature_.push_back({p.SemanticName,p.SemanticIndex,components,Semantic(p.SemanticName,p.SemanticIndex)});
    }
    std::vector<D3D11_INPUT_ELEMENT_DESC> elements,gpu,previous;
    for(size_t i=0;i<signature_.size();++i){const auto& s=signature_[i];elements.push_back({s.Name.c_str(),s.Index,InputFormat(s.Semantic<=1?3:s.Components),0,static_cast<UINT>(i*16),D3D11_INPUT_PER_VERTEX_DATA,0});
        auto e=elements.back();if(s.Semantic>=0&&s.Semantic<=2){e.InputSlot=1;e.AlignedByteOffset=s.Semantic*16;e.Format=InputFormat(s.Semantic<=1?3:4);}gpu.push_back(e);
        if(s.Semantic==8){e.InputSlot=2;e.AlignedByteOffset=0;e.Format=DXGI_FORMAT_R32G32B32_FLOAT;}previous.push_back(e);
    }
    auto input=[&](const auto& e,auto& layout){CheckHResult(g.Device()->CreateInputLayout(e.data(),static_cast<UINT>(e.size()),vertex_.Program.Bytecode.data(),vertex_.Program.Bytecode.size(),&layout),"Create material input layout");};
    input(elements,layout_);input(gpu,gpuLayout_);input(previous,gpuPreviousLayout_);
    for(bool v:{true,false})for(const auto& buffer:(v?vertex_:pixel_).Buffers)constants_.push_back({v,buffer,g.CreateBuffer(buffer.Size,D3D11_BIND_CONSTANT_BUFFER)});
    const auto& state=pass.Metadata.Get("m_State");
    auto value=[&](const Parameter& p,int fallback=0){const auto name=p.String("name");if(!name.empty()&&name!="<noninit>"){auto it=properties.find(name);if(it==properties.end())throw std::runtime_error("Render-state property missing: "+name);return static_cast<int>(it->second);}return p.Has("val")?static_cast<int>(p.Float("val")):fallback;};
    auto v=[&](const char* name,int fallback=0){return value(state.Get(name),fallback);};
    D3D11_RASTERIZER_DESC raster{};raster.FillMode=D3D11_FILL_SOLID;
    const int c=v("culling",2);if(!culling&&(c<0||c>2))throw std::runtime_error("Unknown culling mode.");raster.CullMode=culling.value_or(c==0?D3D11_CULL_NONE:c==1?D3D11_CULL_FRONT:D3D11_CULL_BACK);
    raster.DepthClipEnable=v("zClip",1)!=0;raster.DepthBias=disableDepthOffset?0:v("offsetUnits")*(reversedDepth?-1:1);raster.SlopeScaledDepthBias=disableDepthOffset?0:static_cast<float>(v("offsetFactor")*(reversedDepth?-1:1));raster.MultisampleEnable=TRUE;
    CheckHResult(g.Device()->CreateRasterizerState(&raster,&rasterizer_),"Create material rasterizer");raster.FrontCounterClockwise=TRUE;CheckHResult(g.Device()->CreateRasterizerState(&raster,&invertedRasterizer_),"Create mirrored rasterizer");
    D3D11_BLEND_DESC blend{};blend.AlphaToCoverageEnable=v("alphaToMask")!=0;blend.IndependentBlendEnable=state.Get("rtSeparateBlend").Number!=0;
    for(int i=0;i<8;++i){const auto& b=state.Get("rtBlend"+std::to_string(i));auto& t=blend.RenderTarget[i];t.SrcBlend=BlendFactor(value(b.Get("srcBlend"),1));t.DestBlend=BlendFactor(value(b.Get("destBlend")));t.SrcBlendAlpha=AlphaFactor(value(b.Get("srcBlendAlpha"),1));t.DestBlendAlpha=AlphaFactor(value(b.Get("destBlendAlpha")));t.BlendOp=BlendOperation(value(b.Get("blendOp")));t.BlendOpAlpha=BlendOperation(value(b.Get("blendOpAlpha")));t.RenderTargetWriteMask=static_cast<UINT8>(ColorMask(value(b.Get("colMask"),15)));t.BlendEnable=t.SrcBlend!=D3D11_BLEND_ONE||t.DestBlend!=D3D11_BLEND_ZERO||t.SrcBlendAlpha!=D3D11_BLEND_ONE||t.DestBlendAlpha!=D3D11_BLEND_ZERO||t.BlendOp!=D3D11_BLEND_OP_ADD||t.BlendOpAlpha!=D3D11_BLEND_OP_ADD;}
    CheckHResult(g.Device()->CreateBlendState(&blend,&blend_),"Create material blending");
    auto stencil=[&](const Parameter& s){return D3D11_DEPTH_STENCILOP_DESC{StencilOp(value(s.Get("fail"))),StencilOp(value(s.Get("zFail"))),StencilOp(value(s.Get("pass"))),Compare(value(s.Get("comp"),8))};};
    auto front=stencil(state.Get("stencilOpFront")),back=stencil(state.Get("stencilOpBack"));const auto& common=state.Get("stencilOp");
    if(value(common.Get("comp"),8)!=8||value(common.Get("pass"))||value(common.Get("fail"))||value(common.Get("zFail")))front=back=stencil(common);
    D3D11_DEPTH_STENCIL_DESC depth{};depth.DepthEnable=v("zTest",4)!=0||v("zWrite",1)!=0;depth.DepthWriteMask=v("zWrite",1)?D3D11_DEPTH_WRITE_MASK_ALL:D3D11_DEPTH_WRITE_MASK_ZERO;depth.DepthFunc=Compare(reversedDepth?ReverseCompare(v("zTest",4)):v("zTest",4));
    depth.StencilEnable=front.StencilFunc!=D3D11_COMPARISON_ALWAYS||back.StencilFunc!=D3D11_COMPARISON_ALWAYS||front.StencilPassOp!=D3D11_STENCIL_OP_KEEP||back.StencilPassOp!=D3D11_STENCIL_OP_KEEP;
    depth.StencilReadMask=static_cast<UINT8>(v("stencilReadMask",255));depth.StencilWriteMask=static_cast<UINT8>(v("stencilWriteMask",255));depth.FrontFace=front;depth.BackFace=back;
    stencilReference_=stencilOverride.value_or(v("stencilRef"));if(depthOverride)depth=*depthOverride;
    CheckHResult(g.Device()->CreateDepthStencilState(&depth,&depth_),"Create material depth state");
}
void NativeMaterialPass::Draw(const GpuMeshData& mesh,int submesh,const ShaderValues& values,const ShaderTextures& textures,
    const NativeDeformedVertices* deformation,const ShaderResources& resources,const NativeDeformedVertices* previous,
    const NativeGpuVertexStream* gpu,ID3D11Buffer* previousGpu,std::optional<bool> mirrored){
    graphics_.VerifyThread();auto* ctx=graphics_.Context();
    for(const auto& c:constants_){auto packed=PackShaderBuffer(c.Layout,values);ctx->UpdateSubresource(c.Buffer.Get(),0,nullptr,packed.data(),0,0);auto* b=c.Buffer.Get();if(c.Vertex)ctx->VSSetConstantBuffers(c.Layout.Slot,1,&b);else ctx->PSSetConstantBuffers(c.Layout.Slot,1,&b);}
    for(bool vertex:{true,false})for(const auto& binding:(vertex?vertex_:pixel_).Textures){if(binding.Slot<0||binding.Slot>=128||binding.SamplerSlot>=16)throw std::runtime_error("Invalid material resource slot.");ShaderResource resource;
        auto found=resources.find(binding.Name);if(found!=resources.end())resource=found->second;else{auto t=textures.find(binding.Name);if(t==textures.end()||!t->second)throw std::runtime_error("Missing shader texture: "+binding.Name);resource={t->second->View.Get(),t->second->Sampler.Get()};}
        if(vertex)ctx->VSSetShaderResources(binding.Slot,1,&resource.View);else ctx->PSSetShaderResources(binding.Slot,1,&resource.View);
        if(binding.SamplerSlot>=0){if(vertex)ctx->VSSetSamplers(binding.SamplerSlot,1,&resource.Sampler);else ctx->PSSetSamplers(binding.SamplerSlot,1,&resource.Sampler);}
    }
    auto found=meshes_.find(&mesh);
    if(found==meshes_.end()){
        mesh.Validate();MeshBuffers buffer;buffer.Base.resize(static_cast<size_t>(mesh.VertexCount)*signature_.size()*4);
        auto has=[&](int semantic){return std::any_of(mesh.Channels.begin(),mesh.Channels.end(),[&](const auto& c){return c.Semantic==semantic;});};
        for(int v=0;v<mesh.VertexCount;++v)for(size_t s=0;s<signature_.size();++s){int semantic=signature_[s].Semantic;if(semantic>=4&&semantic<=11&&!has(semantic)){int inherited=-1;for(const auto& c:mesh.Channels)if(c.Semantic>=4&&c.Semantic<semantic)inherited=std::max(inherited,c.Semantic);if(inherited>=0)semantic=inherited;}
            for(int c=0;c<4;++c)buffer.Base[(v*signature_.size()+s)*4+c]=(semantic==0&&c==3)||(semantic==3&&!has(3))?1:mesh.Read(semantic,v,c);
        }
        buffer.Data=buffer.Base;buffer.Vertices=graphics_.CreateBuffer(static_cast<UINT>(buffer.Data.size()*4),D3D11_BIND_VERTEX_BUFFER,buffer.Data.data());buffer.Indices=graphics_.CreateBuffer(static_cast<UINT>(mesh.IndexBytes.size()),D3D11_BIND_INDEX_BUFFER,mesh.IndexBytes.data());found=meshes_.emplace(&mesh,std::move(buffer)).first;
    }
    auto& buffers=found->second;
    if(deformation){if(deformation->Positions.size()!=static_cast<size_t>(mesh.VertexCount)||deformation->Normals.size()!=deformation->Positions.size()||deformation->Tangents.size()!=deformation->Positions.size()||(previous&&previous->Positions.size()!=deformation->Positions.size()))throw std::invalid_argument("Deformed mesh size differs from source.");
        buffers.Data=buffers.Base;
        for(int v=0;v<mesh.VertexCount;++v)for(size_t s=0;s<signature_.size();++s){const int semantic=signature_[s].Semantic;if((semantic<0||semantic>2)&&!(semantic==8&&previous))continue;Vector4 value;
            if(semantic==2)value=deformation->Tangents[v];else{const auto p=semantic==8?previous->Positions[v]:semantic==0?deformation->Positions[v]:deformation->Normals[v];value={p.x,p.y,p.z,semantic==1?0.f:1.f};}
            auto* out=buffers.Data.data()+(v*signature_.size()+s)*4;out[0]=value.x;out[1]=value.y;out[2]=value.z;out[3]=value.w;
        }
        ctx->UpdateSubresource(buffers.Vertices.Get(),0,nullptr,buffers.Data.data(),0,0);
    }
    // GPU skinning supplies positions/normals/tangents from a separate stream.
    // The remaining source attributes are immutable across lights and shadows.
    ctx->IASetInputLayout(!gpu?layout_.Get():!previousGpu?gpuLayout_.Get():gpuPreviousLayout_.Get());
    ID3D11Buffer* vertexBuffers[]{buffers.Vertices.Get(),gpu?gpu->Buffer:nullptr,previousGpu};UINT strides[]{static_cast<UINT>(signature_.size()*16),NativeGpuVertexStream::Stride,NativeGpuVertexStream::Stride},offsets[]{0,0,0};
    if(gpu&&(gpu->VertexCount!=mesh.VertexCount||!gpu->Buffer))throw std::invalid_argument("GPU deformed mesh size differs from source.");ctx->IASetVertexBuffers(0,3,vertexBuffers,strides,offsets);
    ctx->IASetIndexBuffer(buffers.Indices.Get(),mesh.IndexSize==2?DXGI_FORMAT_R16_UINT:DXGI_FORMAT_R32_UINT,0);
    const auto& part=mesh.Submeshes.at(submesh);D3D11_PRIMITIVE_TOPOLOGY topology;
    switch(part.Topology){case 0:topology=D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST;break;case 3:topology=D3D11_PRIMITIVE_TOPOLOGY_LINELIST;break;case 4:topology=D3D11_PRIMITIVE_TOPOLOGY_LINESTRIP;break;case 5:topology=D3D11_PRIMITIVE_TOPOLOGY_POINTLIST;break;default:throw std::runtime_error("Unsupported mesh topology.");}
    ctx->IASetPrimitiveTopology(topology);ctx->VSSetShader(vertexShader_.Get(),nullptr,0);ctx->PSSetShader(pixelShader_.Get(),nullptr,0);
    auto projection=values.find("_ProjectionParams");bool counterClockwise=projection!=values.end()&&!projection->second.empty()&&projection->second.front()<0;if(mirrored)counterClockwise^=*mirrored;
    ctx->RSSetState(counterClockwise?invertedRasterizer_.Get():rasterizer_.Get());ctx->OMSetBlendState(blend_.Get(),nullptr,~0u);ctx->OMSetDepthStencilState(depth_.Get(),stencilReference_);ctx->DrawIndexed(part.IndexCount,part.FirstIndex,part.BaseVertex);
}
NativeComputePass::NativeComputePass(GraphicsDevice& g,NativeComputeKernel kernel):graphics_(g),kernel_(std::move(kernel)){
    g.VerifyThread();if(ValidateShaderBytecode(kernel_.Program.Bytecode)!=NativeShaderStage::Compute)throw std::runtime_error("Compute program has another shader stage.");
    const auto& groups=kernel_.GroupSize;if(!groups[0]||!groups[1]||!groups[2]||std::uint64_t(groups[0])*groups[1]*groups[2]>1024)throw std::runtime_error("Invalid compute group size.");
    CheckHResult(g.Device()->CreateComputeShader(kernel_.Program.Bytecode.data(),kernel_.Program.Bytecode.size(),nullptr,&shader_),"Create compute shader");
    if(!kernel_.Metadata.Get("inBuffers").Children.empty())throw std::runtime_error("Compute structured buffers require explicit bindings.");
    for(const auto& builtin:kernel_.Metadata.Get("builtinSamplers").Children){const int encoded=static_cast<int>(builtin.Float("sampler"));if(encoded<0||encoded>255||(encoded&3)==3)throw std::runtime_error("Unknown compute sampler flags.");
        auto d=DefaultSampler(true,(encoded&3)==0?D3D11_FILTER_MIN_MAG_MIP_POINT:(encoded&3)==1?D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT:D3D11_FILTER_MIN_MAG_MIP_LINEAR);d.AddressU=TextureAddress((encoded>>2)&3);d.AddressV=TextureAddress((encoded>>4)&3);d.AddressW=TextureAddress((encoded>>6)&3);
        const int slot=static_cast<int>(builtin.Float("bindPoint"));if(slot<0||slot>=16)throw std::runtime_error("Invalid compute sampler slot.");ComPtr<ID3D11SamplerState> sampler;CheckHResult(g.Device()->CreateSamplerState(&d,&sampler),"Create builtin compute sampler");samplers_.emplace_back(slot,std::move(sampler));
    }
    for(const auto& binding:kernel_.Metadata.Get("cbs").Children){auto cb=std::find_if(kernel_.ConstantBuffers.Children.begin(),kernel_.ConstantBuffers.Children.end(),[&](const auto& c){return c.String("name")==binding.String("name");});if(cb==kernel_.ConstantBuffers.Children.end())throw std::runtime_error("Compute constant buffer is missing.");
        ShaderBuffer layout{cb->String("name"),static_cast<int>(binding.Float("bindPoint")),(static_cast<int>(cb->Float("byteSize"))+15)&~15,{}};if(layout.Slot<0||layout.Slot>13)throw std::runtime_error("Invalid compute constant buffer slot.");
        for(const auto& p:cb->Get("params").Children){bool matrix=p.Float("rowCount")!=1;layout.Constants.push_back({p.String("name"),static_cast<int>(p.Float("offset")),static_cast<int>(p.Float(matrix?"rowCount":"colCount")),std::max(1,static_cast<int>(p.Float("arraySize"))),matrix,static_cast<int>(p.Float("type"))});}
        auto buffer=g.CreateBuffer(layout.Size,D3D11_BIND_CONSTANT_BUFFER);buffers_.push_back({std::move(layout),std::move(buffer)});
    }
}
void NativeComputePass::Dispatch(UINT width,UINT height,UINT depth,const ShaderValues& values,const ShaderResources& textures,const std::unordered_map<std::string,ID3D11UnorderedAccessView*>& outputs){
    graphics_.VerifyThread();auto* ctx=graphics_.Context();ctx->ClearState();
    for(const auto& buffer:buffers_){auto data=PackShaderBuffer(buffer.Layout,values);ctx->UpdateSubresource(buffer.Object.Get(),0,nullptr,data.data(),0,0);auto* object=buffer.Object.Get();ctx->CSSetConstantBuffers(buffer.Layout.Slot,1,&object);}
    for(const auto& binding:kernel_.Metadata.Get("textures").Children){auto resource=textures.at(binding.String("name"));const int slot=static_cast<int>(binding.Float("bindPoint")),sampler=static_cast<int>(binding.Float("samplerBindPoint",-1));if(slot<0||slot>=128||sampler>=16)throw std::runtime_error("Invalid compute texture slot.");ctx->CSSetShaderResources(slot,1,&resource.View);if(sampler>=0)ctx->CSSetSamplers(sampler,1,&resource.Sampler);}
    for(const auto& binding:kernel_.Metadata.Get("outBuffers").Children){auto* output=outputs.at(binding.String("name"));const int slot=static_cast<int>(binding.Float("bindPoint"));if(slot<0||slot>=8)throw std::runtime_error("Invalid compute output slot.");ctx->CSSetUnorderedAccessViews(slot,1,&output,nullptr);}
    for(const auto& sampler:samplers_){auto* state=sampler.second.Get();ctx->CSSetSamplers(sampler.first,1,&state);}
    auto groups=[&](UINT size,UINT group){const auto count=(std::uint64_t(size)+group-1)/group;if(count>65535)throw std::invalid_argument("Compute dispatch exceeds D3D11 dimensions.");return static_cast<UINT>(count);};
    ctx->CSSetShader(shader_.Get(),nullptr,0);ctx->Dispatch(groups(width,kernel_.GroupSize[0]),groups(height,kernel_.GroupSize[1]),groups(depth,kernel_.GroupSize[2]));ctx->ClearState();
}
}
