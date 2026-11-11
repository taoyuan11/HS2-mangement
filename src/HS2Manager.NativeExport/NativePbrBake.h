#pragma once
#include "ExportGpuPass.h"
#include "../HS2Manager.NativeRendering/GpuMaterialComposer.h"
#include "../HS2Manager.NativeCore/Core.h"
#include <map>
#include <set>
namespace hs2 {
class NativePbrBakePass {
public:
    NativePbrBakePass(GraphicsDevice& graphics,GpuMaterialComposer& composer);
    std::unique_ptr<GpuTexture> Draw(const GpuMaterialInstance& material,const std::unordered_map<std::string,std::shared_ptr<GpuTexture>>& textures,const ShaderValues& overrides,int width,int height,bool color);
    static const std::vector<std::string>& TextureNames();
private:
    GraphicsDevice& graphics_;GpuMaterialComposer& composer_;ShaderBuffer constants_;std::unique_ptr<ExportGpuPass> pass_;
};
class NativePbrMaterialExporter {
public:
    NativePbrMaterialExporter(GraphicsDevice& graphics,GpuMaterialComposer& composer,fs::path directory);
    Json Get(const GpuMaterialInstance& source,const std::string& role,const std::string& renderer,const std::string& slot,std::stop_token cancellation={});
    void Save();
    const std::vector<Json>& Records()const{return records_;}
    std::vector<std::string> Warnings,Issues;
private:
    GpuMaterialComposer& composer_;NativePbrBakePass baker_;fs::path directory_;std::map<std::string,std::size_t> slots_;std::set<std::string> unknown_;std::vector<Json> records_;
};
}
