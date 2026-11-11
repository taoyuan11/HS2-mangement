#pragma once
#include "../HS2Manager.NativeScene/SceneTypes.h"
#include <array>
#include <unordered_set>

namespace hs2 {
enum class NativeShaderStage { Pixel, Vertex, Geometry, Hull, Domain, Compute };
struct NativeShaderProgram { int Index = 0; NativeShaderStage Stage = NativeShaderStage::Pixel; std::vector<std::uint8_t> Bytecode; };
struct NativeShaderPass { Parameter Metadata; int SubShader = 0; };
struct NativeShaderAsset {
    std::string Name;
    std::int64_t PathId = 0;
    Parameter Properties;
    std::vector<NativeShaderPass> Passes;
    std::vector<NativeShaderProgram> Programs;
    std::vector<Parameter> SubShaders;
    std::filesystem::path SourceBundle;
};
struct NativeComputeKernel {
    std::string Name;
    NativeShaderProgram Program;
    Parameter Metadata, ConstantBuffers;
    std::array<std::uint32_t, 3> GroupSize{1, 1, 1};
};
struct NativeComputeAsset { std::string Name; std::vector<NativeComputeKernel> Kernels; };
NativeShaderStage ValidateShaderBytecode(std::span<const std::uint8_t> bytes);
std::vector<NativeShaderProgram> DecodeShaderPrograms(std::span<const std::uint8_t> blob);
class NativeGpuAssetReader;
NativeShaderAsset ReadShader(NativeGpuAssetReader& reader, std::int64_t pathId);
NativeComputeAsset ReadComputeShader(NativeGpuAssetReader& reader, std::int64_t pathId);
struct ShaderConstant { std::string Name; int Offset = 0, Components = 0, ArrayCount = 1; bool Matrix = false; int Type = 0; };
struct ShaderBuffer { std::string Name; int Slot = 0, Size = 0; std::vector<ShaderConstant> Constants; };
struct ShaderTextureBinding { std::string Name; int Slot = 0, SamplerSlot = -1, Dimension = 2; };
struct ShaderVertexBinding { int Source = 0, Target = 0; };
struct ShaderVariant {
    NativeShaderProgram Program;
    std::vector<std::string> Keywords;
    int HardwareTier = 0;
    std::vector<ShaderBuffer> Buffers;
    std::vector<ShaderTextureBinding> Textures;
    std::vector<ShaderVertexBinding> Channels;
};
using ShaderValues = std::unordered_map<std::string, std::vector<float>>;
ShaderVariant SelectShaderVariant(const NativeShaderAsset& shader, const NativeShaderPass& pass,
    NativeShaderStage stage, const std::unordered_set<std::string>& keywords);
std::vector<std::uint8_t> PackShaderBuffer(const ShaderBuffer& buffer, const ShaderValues& values);
}
