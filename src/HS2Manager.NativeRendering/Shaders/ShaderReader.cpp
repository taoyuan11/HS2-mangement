#include "Shaders/ShaderMetadata.h"
#include "Assets/AssetReader.h"
#include <algorithm>
#include <cstring>
#include <stdexcept>

namespace hs2
{
    namespace
    {
        Parameter TakeField(Parameter &value, std::string_view name)
        {
            for (auto &field : value.Children)
                if (field.Name == name)
                    return std::move(field);
            return {};
        }

        std::vector<Parameter> &Array(Parameter &value)
        {
            for (auto &field : value.Children)
                if (field.Name == "Array")
                    return field.Children;
            return value.Children;
        }

        std::vector<std::uint8_t> Bytes(const Parameter &field)
        {
            if (!field.Bytes.empty())
                return field.Bytes;
            std::vector<std::uint8_t> bytes;
            for (const auto &value : field.Array())
            {
                if (value.Number < 0 || value.Number > 255)
                    throw std::runtime_error("Invalid shader byte.");
                bytes.push_back(static_cast<std::uint8_t>(value.Number));
            }
            return bytes;
        }

        std::int64_t At(const Parameter &array, size_t index)
        {
            const auto values = array.Array();
            if (index >= values.size())
                throw std::runtime_error("Shader platform table is truncated.");
            return static_cast<std::int64_t>(values[index].Number);
        }
    }

    NativeShaderAsset ReadShader(NativeGpuAssetReader &reader, std::int64_t pathId)
    {
        if (reader.AssetClass(pathId) != 48)
            throw std::invalid_argument("Resource is not a Shader.");
        auto shader = reader.ReadMetadata(pathId);
        const auto platforms = shader.Get("platforms").Array();
        auto found = std::find_if(platforms.begin(), platforms.end(),
                                  [](const Parameter &p)
                                  {
                                      return p.Number == 4;
                                  });
        if (found == platforms.end())
            throw std::runtime_error("Shader contains no D3D11 programs.");
        const auto platform = static_cast<size_t>(found - platforms.begin());
        const auto offset = At(shader.Get("offsets"), platform), length = At(shader.Get("compressedLengths"), platform),
                   size = At(shader.Get("decompressedLengths"), platform);
        const auto bytes = Bytes(shader.Get("compressedBlob"));
        if (size < 4 || size > 256 * 1024 * 1024 || offset < 0 || length < 1 ||
            static_cast<std::uint64_t>(offset) + length > bytes.size())
            throw std::runtime_error("Shader compressed data is invalid.");
        auto decoded = DecompressUnityBlock(
            std::span<const std::uint8_t>(bytes).subspan(static_cast<size_t>(offset), static_cast<size_t>(length)),
            static_cast<size_t>(size), 2);
        auto parsed = TakeField(shader, "m_ParsedForm");
        NativeShaderAsset result;
        result.Name = parsed.String("m_Name");
        result.PathId = pathId;
        result.Properties = TakeField(parsed, "m_PropInfo");
        result.SourceBundle = reader.Path;
        int index = 0;
        auto subShaders = TakeField(parsed, "m_SubShaders");
        for (auto &sub : Array(subShaders))
        {
            auto passes = TakeField(sub, "m_Passes");
            for (auto &pass : Array(passes))
            {
                // 选择器只使用 D3D11 (15..22)；其余平台的参数树无需随预览常驻。
                for (auto &stage : pass.Children)
                    if (stage.Name == "progFragment" || stage.Name == "progVertex" || stage.Name == "progGeometry" ||
                        stage.Name == "progHull" || stage.Name == "progDomain")
                        for (auto &programs : stage.Children)
                            if (programs.Name == "m_SubPrograms")
                                std::erase_if(Array(programs),
                                              [](const Parameter &program)
                                              {
                                                  auto type = program.Get("m_GpuProgramType").Number;
                                                  return type < 15 || type > 22;
                                              });
                result.Passes.push_back({std::move(pass), index});
            }
            Parameter summary;
            summary.Children = {sub.Get("m_Tags"), sub.Get("m_LOD")};
            result.SubShaders.push_back(std::move(summary));
            ++index;
        }
        result.Programs = DecodeShaderPrograms(decoded);
        return result;
    }

    NativeComputeAsset ReadComputeShader(NativeGpuAssetReader &reader, std::int64_t pathId)
    {
        if (reader.AssetClass(pathId) != 72)
            throw std::invalid_argument("Resource is not a ComputeShader.");
        const auto field = reader.ReadMetadata(pathId);
        const auto variants = field.Get("variants").Array();
        auto variant = std::find_if(variants.begin(), variants.end(),
                                    [](const Parameter &p)
                                    {
                                        return p.Int("targetRenderer") == 2;
                                    });
        if (variant == variants.end())
            throw std::runtime_error("Compute shader contains no D3D11 variant.");
        NativeComputeAsset result;
        result.Name = field.String("m_Name");
        constexpr std::uint8_t signature[]{'D', 'X', 'B', 'C'};
        for (const auto &k : variant->Get("kernels").Array())
        {
            const auto raw = Bytes(k.Get("code"));
            auto start = std::search(raw.begin(), raw.end(), std::begin(signature), std::end(signature));
            if (start == raw.end() || raw.end() - start < 32)
                throw std::runtime_error("Compute program has no DXBC container.");
            std::uint32_t length;
            std::memcpy(&length, &*(start + 24), 4);
            if (length < 32 || static_cast<size_t>(raw.end() - start) < length)
                throw std::runtime_error("Compute shader is truncated.");
            NativeComputeKernel kernel;
            kernel.Name = k.String("name");
            kernel.Program = {
                static_cast<int>(result.Kernels.size()), NativeShaderStage::Compute, {start, start + length}};
            if (ValidateShaderBytecode(kernel.Program.Bytecode) != NativeShaderStage::Compute)
                throw std::runtime_error("Compute program has another shader stage.");
            const auto groups = k.Get("threadGroupSize").Array();
            if (groups.size() != 3)
                throw std::runtime_error("Invalid compute thread group size.");
            for (size_t i = 0; i < 3; ++i)
            {
                if (groups[i].Number < 1 || groups[i].Number > 1024)
                    throw std::runtime_error("Invalid compute thread group dimension.");
                kernel.GroupSize[i] = static_cast<std::uint32_t>(groups[i].Number);
            }
            if (std::uint64_t(kernel.GroupSize[0]) * kernel.GroupSize[1] * kernel.GroupSize[2] > 1024)
                throw std::runtime_error("Compute group exceeds D3D11 thread limit.");
            kernel.Metadata = k;
            kernel.ConstantBuffers = variant->Get("constantBuffers");
            result.Kernels.push_back(std::move(kernel));
        }
        return result;
    }
}
