#include "Shaders/ShaderMetadata.h"
#include <algorithm>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>
#include <stdexcept>

namespace hs2
{
    namespace
    {
        std::uint32_t U32(std::span<const std::uint8_t> bytes, size_t offset)
        {
            if (offset > bytes.size() || bytes.size() - offset < 4)
                throw std::runtime_error("Truncated shader data.");
            std::uint32_t value;
            std::memcpy(&value, bytes.data() + offset, 4);
            return value;
        }

        bool Tag(std::span<const std::uint8_t> bytes, size_t offset, const char *value)
        {
            return offset <= bytes.size() && bytes.size() - offset >= 4 &&
                   std::memcmp(bytes.data() + offset, value, 4) == 0;
        }
    }

    NativeShaderStage ValidateShaderBytecode(std::span<const std::uint8_t> bytes)
    {
        if (bytes.size() < 32 || !Tag(bytes, 0, "DXBC") || U32(bytes, 24) != bytes.size())
            throw std::runtime_error("Invalid DXBC container.");
        const auto chunks = U32(bytes, 28);
        if (chunks < 1 || chunks > 64 || 32ull + chunks * 4ull > bytes.size())
            throw std::runtime_error("Invalid DXBC chunk table.");
        std::optional<NativeShaderStage> stage;
        for (std::uint32_t c = 0; c < chunks; ++c)
        {
            const auto offset = U32(bytes, 32 + c * 4);
            if (offset < 32 + chunks * 4 || static_cast<size_t>(offset) + 8 > bytes.size())
                throw std::runtime_error("Invalid DXBC chunk offset.");
            const auto length = U32(bytes, offset + 4);
            if (static_cast<std::uint64_t>(offset) + 8 + length > bytes.size())
                throw std::runtime_error("Truncated DXBC chunk.");
            if (!Tag(bytes, offset, "SHDR") && !Tag(bytes, offset, "SHEX"))
                continue;
            if (length < 8 || stage)
                throw std::runtime_error("Invalid DXBC shader chunk.");
            const auto type = U32(bytes, offset + 8) >> 16;
            if (type > 5)
                throw std::runtime_error("Unsupported DXBC stage.");
            const auto words = U32(bytes, offset + 12);
            if (words < 2 || static_cast<std::uint64_t>(words) * 4 != length)
                throw std::runtime_error("Invalid shader token length.");
            stage = static_cast<NativeShaderStage>(type);
        }
        if (!stage)
            throw std::runtime_error("DXBC has no shader program.");
        return *stage;
    }

    std::vector<NativeShaderProgram> DecodeShaderPrograms(std::span<const std::uint8_t> blob)
    {
        const auto count = U32(blob, 0);
        if (count > 100000 || 4ull + count * 8ull > blob.size())
            throw std::runtime_error("Invalid shader program table.");
        std::vector<NativeShaderProgram> result;
        result.reserve(count);
        constexpr std::uint8_t magic[]{'D', 'X', 'B', 'C'};
        for (std::uint32_t i = 0; i < count; ++i)
        {
            const auto offset = U32(blob, 4 + i * 8), length = U32(blob, 8 + i * 8);
            if (offset < 4 + count * 8 || !length || static_cast<std::uint64_t>(offset) + length > blob.size())
                throw std::runtime_error("Shader program lies outside its blob.");
            auto program = blob.subspan(offset, length);
            auto start = std::search(program.begin(), program.end(), std::begin(magic), std::end(magic));
            if (start == program.end())
                throw std::runtime_error("D3D11 program has no DXBC container.");
            auto dxbc = program.subspan(static_cast<size_t>(start - program.begin()));
            const auto size = U32(dxbc, 24);
            if (size < 32 || size > dxbc.size())
                throw std::runtime_error("DXBC length is invalid.");
            dxbc = dxbc.first(size);
            result.push_back({static_cast<int>(i), ValidateShaderBytecode(dxbc), {dxbc.begin(), dxbc.end()}});
        }
        return result;
    }

    ShaderVariant SelectShaderVariant(const NativeShaderAsset &shader, const NativeShaderPass &pass,
                                      NativeShaderStage stage, const std::unordered_set<std::string> &keywords)
    {
        static constexpr const char *groups[]{"progFragment", "progVertex", "progGeometry", "progHull", "progDomain"};
        if (stage == NativeShaderStage::Compute)
            throw std::invalid_argument("Compute shaders do not use material passes.");
        std::unordered_map<int, std::string> names;
        for (const auto &name : pass.Metadata.Get("m_NameIndices").Children)
            names.emplace(static_cast<int>(name.Get("second").Number), name.String("first"));

        struct Candidate
        {
            const Parameter *Data;
            std::unordered_set<std::string> Keywords;
        };

        std::vector<Candidate> candidates;
        std::unordered_set<std::string> known;
        for (const auto &p : pass.Metadata.Get(groups[static_cast<int>(stage)]).Get("m_SubPrograms").Children)
        {
            const auto type = p.Get("m_GpuProgramType").Number;
            if (type < 15 || type > 22)
                continue;
            Candidate candidate{&p, {}};
            for (const auto &index : p.Get("m_KeywordIndices").Children)
                candidate.Keywords.insert(names.at(static_cast<int>(index.Number)));
            known.insert(candidate.Keywords.begin(), candidate.Keywords.end());
            candidates.push_back(std::move(candidate));
        }
        std::unordered_set<std::string> selected;
        for (const auto &keyword : keywords)
            if (known.contains(keyword))
                selected.insert(keyword);
        if (!candidates.empty())
            for (const auto &keyword : candidates.front().Keywords)
                if (std::all_of(candidates.begin(), candidates.end(),
                                [&](const Candidate &c)
                                {
                                    return c.Keywords.contains(keyword);
                                }))
                    selected.insert(keyword);
        const Candidate *match = nullptr;
        for (const auto &candidate : candidates)
            if (candidate.Keywords == selected && (!match || candidate.Data->Get("m_ShaderHardwareTier").Number >
                                                                 match->Data->Get("m_ShaderHardwareTier").Number))
                match = &candidate;
        if (!match)
            throw std::runtime_error("Shader variant missing: " + shader.Name);
        const auto &data = *match->Data;
        auto program = std::find_if(shader.Programs.begin(), shader.Programs.end(),
                                    [&](const auto &p)
                                    {
                                        return p.Index == data.Get("m_BlobIndex").Number;
                                    });
        if (program == shader.Programs.end() || program->Stage != stage)
            throw std::runtime_error("Shader variant references an invalid GPU program.");
        ShaderVariant result;
        result.Program = *program;
        result.Keywords.assign(selected.begin(), selected.end());
        result.HardwareTier = static_cast<int>(data.Get("m_ShaderHardwareTier").Number);
        std::unordered_map<int, int> slots;
        for (const auto &binding : data.Get("m_ConstantBufferBindings").Children)
            slots.emplace(static_cast<int>(binding.Get("m_NameIndex").Number),
                          static_cast<int>(binding.Get("m_Index").Number));
        for (const auto &buffer : data.Get("m_ConstantBuffers").Children)
        {
            const int name = static_cast<int>(buffer.Get("m_NameIndex").Number);
            if (!slots.contains(name))
                throw std::runtime_error("Constant buffer has no binding.");
            if (!buffer.Get("m_StructParams").Children.empty())
                throw std::runtime_error("Structured shader constants require explicit layout support.");
            const int slot = slots.at(name), size = static_cast<int>(buffer.Get("m_Size").Number);
            if (slot < 0 || slot > 13 || size < 1 || size > 65536)
                throw std::runtime_error("Invalid shader buffer size or slot.");
            ShaderBuffer layout{names.at(name), slot, (size + 15) & ~15, {}};
            for (bool matrix : {false, true})
                for (const auto &constant : buffer.Get(matrix ? "m_MatrixParams" : "m_VectorParams").Children)
                {
                    const int components = static_cast<int>(constant.Get(matrix ? "m_RowCount" : "m_Dim").Number);
                    const int array = std::max(1, static_cast<int>(constant.Get("m_ArraySize").Number));
                    const int offset = static_cast<int>(constant.Get("m_Index").Number);
                    const auto byteCount = matrix      ? std::int64_t(components) * 16 * array
                                           : array > 1 ? std::int64_t(array - 1) * 16 + components * 4
                                                       : components * 4;
                    if (components < 1 || components > 4 || offset < 0 || offset + byteCount > size)
                        throw std::runtime_error("Invalid shader constant layout.");
                    layout.Constants.push_back({names.at(static_cast<int>(constant.Get("m_NameIndex").Number)), offset,
                                                components, array, matrix,
                                                static_cast<int>(constant.Get("m_Type").Number)});
                }
            result.Buffers.push_back(std::move(layout));
        }
        for (const auto &binding : data.Get("m_TextureParams").Children)
            result.Textures.push_back({names.at(static_cast<int>(binding.Get("m_NameIndex").Number)),
                                       static_cast<int>(binding.Get("m_Index").Number),
                                       static_cast<int>(binding.Get("m_SamplerIndex").Number),
                                       static_cast<int>(binding.Get("m_Dim").Number)});
        for (const auto &channel : data.Get("m_Channels").Get("m_Channels").Children)
            result.Channels.push_back(
                {static_cast<int>(channel.Get("source").Number), static_cast<int>(channel.Get("target").Number)});
        return result;
    }

    std::vector<std::uint8_t> PackShaderBuffer(const ShaderBuffer &buffer, const ShaderValues &values)
    {
        if (buffer.Size < 1 || buffer.Size > 65536 || buffer.Size % 16)
            throw std::invalid_argument("Invalid constant buffer size.");
        std::vector<std::uint8_t> bytes(buffer.Size);
        for (const auto &constant : buffer.Constants)
        {
            const int elements = constant.Matrix ? constant.Components * 4 : constant.Components;
            auto found = values.find(constant.Name);
            if (constant.Components < 1 || constant.Components > 4 || constant.ArrayCount < 1 || constant.Offset < 0 ||
                found == values.end())
                throw std::runtime_error("Missing or invalid shader constant: " + constant.Name);
            const auto &value = found->second;
            if (value.size() % constant.ArrayCount ||
                value.size() / constant.ArrayCount < static_cast<size_t>(elements) ||
                value.size() / constant.ArrayCount > static_cast<size_t>(constant.Matrix ? elements : 4))
                throw std::runtime_error("Shader constant dimensions differ: " + constant.Name);
            const auto stride = value.size() / constant.ArrayCount;
            for (int array = 0; array < constant.ArrayCount; ++array)
                for (int i = 0; i < elements; ++i)
                {
                    const auto offset = std::uint64_t(constant.Offset) +
                                        std::uint64_t(array) * (constant.Matrix ? elements * 4 : 16) + i * 4;
                    if (offset + 4 > bytes.size())
                        throw std::runtime_error("Shader constant lies outside buffer.");
                    const float number = value[array * stride + i];
                    if (!std::isfinite(number))
                        throw std::runtime_error("Non-finite shader constant: " + constant.Name);
                    std::uint32_t bits;
                    if (constant.Type == 0)
                        bits = std::bit_cast<std::uint32_t>(number);
                    else if (constant.Type == 1)
                    {
                        if (static_cast<double>(number) < std::numeric_limits<std::int32_t>::min() ||
                            static_cast<double>(number) > std::numeric_limits<std::int32_t>::max())
                            throw std::runtime_error("Integer shader constant overflow.");
                        bits = static_cast<std::uint32_t>(static_cast<std::int32_t>(number));
                    }
                    else if (constant.Type == 2)
                        bits = number == 0 ? 0 : 1;
                    else
                        throw std::runtime_error("Unsupported shader constant type.");
                    std::memcpy(bytes.data() + offset, &bits, 4);
                }
        }
        return bytes;
    }
}
