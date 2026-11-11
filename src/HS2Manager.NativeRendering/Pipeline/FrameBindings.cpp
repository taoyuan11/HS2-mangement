#include "Pipeline/NativeForwardRenderer.h"
#include "Common/RenderMath.h"
#include <algorithm>
#include <cfloat>
#include <stdexcept>

namespace hs2
{
    ShaderValues NativeForwardRenderer::MaterialValues(const NativeSceneRenderer &renderer,
                                                       const GpuMaterialInstance &material, const ShaderValues &globals)
    {
        auto values = globals;
        for (const auto &[name, v] : material.Values)
            values[name] = v;
        auto colors = material.ColorProperties;
        colors.insert(material.GammaProperties.begin(), material.GammaProperties.end());
        for (const auto &color : colors)
        {
            auto found = values.find(color);
            if (found != values.end())
                for (size_t c = 0; c < std::min<size_t>(3, found->second.size()); ++c)
                    found->second[c] = GpuMaterialComposer::Linear(found->second[c]);
        }
        auto transform = [&](const std::string &name)
        {
            auto it = material.Textures.find(name);
            values[name + "_ST"] = it == material.Textures.end()
                                       ? std::vector<float>{1, 1, 0, 0}
                                       : std::vector<float>{it->second.Scale.x, it->second.Scale.y, it->second.Offset.x,
                                                            it->second.Offset.y};
        };
        for (const auto &[name, _] : material.DefaultTextures)
            transform(name);
        for (const auto &[name, _] : material.Textures)
            transform(name);
        const auto world = renderer.RenderWorldMatrix();
        values["unity_ObjectToWorld"] = MatrixValues(world);
        values["unity_WorldToObject"] = MatrixValues(InverseMatrix(world));
        values["unity_WorldTransformParams"] = {0, 0, 0, Determinant(world) < 0 ? -1.f : 1.f};
        return values;
    }

    bool NativeForwardRenderer::GeometryMirrored(const NativeSceneRenderer &renderer)
    {
        return Determinant(renderer.Node->WorldMatrix()) < 0;
    }

    std::string NativeForwardRenderer::Tag(const NativeShaderPass &pass, const std::string &key)
    {
        for (const auto &tag : pass.Metadata.Get("m_State").Get("m_Tags").Get("tags").Children)
            if (LowerAscii(tag.String("first")) == LowerAscii(key))
                return tag.String("second");
        return {};
    }

    std::vector<NativeDirectionalLight> NativeForwardRenderer::DirectionalLights(
        const EnvironmentDefinition &environment, const NativeRenderCamera &camera, float keyMultiplier)
    {
        NativeSceneNode cameraNode("camera-light-frame");
        cameraNode.LocalRotation(LookRotation(Sub(camera.Target, camera.Position), camera.Up));
        std::vector<NativeDirectionalLight> result;
        for (const auto &light : environment.Lights)
        {
            if (static_cast<int>(light.Parameters.Float("m_Type")) != 1)
                throw std::runtime_error(
                    "Local light attenuation is not supported by the original native forward renderer.");
            const auto &r = light.Rotation;
            NativeSceneNode node("directional-light");
            node.LocalRotation({r[0], r[1], r[2], r[3]});
            if (light.CameraRelative)
                node.SetParent(&cameraNode);
            const auto direction = node.TransformDirection({0, 0, -1});
            const auto &color = light.Parameters.Get("m_Color");
            float intensity = light.Parameters.Float("m_Intensity", 1);
            if (environment.DhhEnabled && light.Name.find("Back") != std::string::npos)
                intensity = environment.DhhFloats[34];
            if (light.Name.find("Key") != std::string::npos)
                intensity *= keyMultiplier;
            const auto &shadow = light.Parameters.Get("m_Shadows");
            float strength = shadow.Float("m_Type") == 0 ? 0 : shadow.Float("m_Strength", 1);
            if (environment.DhhEnabled && light.Name.find("Key") != std::string::npos)
                strength = environment.DhhFloats[36];
            const auto maskValue = light.Parameters.Get("m_CullingMask").Get("m_Bits").Number;
            const UINT mask =
                maskValue < 0 ? static_cast<UINT>(static_cast<std::int64_t>(maskValue)) : static_cast<UINT>(maskValue);
            result.push_back({direction,
                              {GpuMaterialComposer::Linear(color.Float("r") * intensity),
                               GpuMaterialComposer::Linear(color.Float("g") * intensity),
                               GpuMaterialComposer::Linear(color.Float("b") * intensity)},
                              mask,
                              intensity,
                              strength,
                              shadow.Float("m_Bias"),
                              shadow.Float("m_NormalBias"),
                              shadow.Float("m_NearPlane", .2f),
                              node.Rotation()});
        }
        if (environment.DhhEnabled && environment.DhhBools[8])
        {
            const auto &c = environment.DhhColors;
            Quaternion q;
            DirectX::XMStoreFloat4(&q, DirectX::XMQuaternionRotationRollPitchYaw(c[6] * (DirectX::XM_PI / 180),
                                                                                 c[7] * (DirectX::XM_PI / 180), 0));
            NativeSceneNode extra("additional-directional-light");
            extra.LocalRotation(q);
            extra.SetParent(&cameraNode);
            result.push_back({extra.TransformDirection({0, 0, -1}),
                              {GpuMaterialComposer::Linear(c[9] / 255 * environment.DhhFloats[38]),
                               GpuMaterialComposer::Linear(c[10] / 255 * environment.DhhFloats[38]),
                               GpuMaterialComposer::Linear(c[11] / 255 * environment.DhhFloats[38])},
                              ~0u,
                              environment.DhhFloats[38],
                              environment.DhhFloats[39],
                              .05f,
                              .4f,
                              .2f,
                              extra.Rotation()});
        }
        return result;
    }

    ShaderValues NativeForwardRenderer::Globals(const EnvironmentDefinition &environment,
                                                const NativeRenderCamera &camera, int width, int height,
                                                std::span<const float> sh, float time)
    {
        if (sh.size() != 27)
            throw std::invalid_argument("Ambient probe needs 27 coefficients.");
        const auto projection = camera.Projection(static_cast<float>(width) / height), view = camera.View();
        const auto inverseView = InverseMatrix(view), inverseProjection = InverseMatrix(projection);
        float reflectionIntensity = GpuMaterialComposer::Linear(
            environment.DhhEnabled ? environment.DhhFloats[33]
                                   : environment.RenderSettings.Float("m_ReflectionIntensity", 1));
        float ambientScale = GpuMaterialComposer::Linear(
            environment.DhhEnabled ? environment.DhhFloats[32]
                                   : environment.RenderSettings.Float("m_AmbientIntensity", 1));
        const auto &ambient = environment.RenderSettings.Get("m_AmbientSkyColor");
        ShaderValues result{
            {"unity_MatrixV", MatrixValues(view)},
            {"unity_MatrixInvV", MatrixValues(inverseView)},
            {"unity_MatrixVP", MatrixValues(MultiplyMatrix(view, projection))},
            {"glstate_matrix_projection", MatrixValues(projection)},
            {"unity_CameraProjection", MatrixValues(projection)},
            {"unity_CameraInvProjection", MatrixValues(inverseProjection)},
            {"_WorldSpaceCameraPos", Values(camera.Position)},
            {"_ProjectionParams", {camera.BottomOrigin ? -1.f : 1.f, camera.Near, camera.Far, 1 / camera.Far}},
            {"_ScreenParams",
             {static_cast<float>(width), static_cast<float>(height), 1 + 1.f / width, 1 + 1.f / height}},
            {"_ZBufferParams",
             {camera.Far / camera.Near - 1, 1, (camera.Far / camera.Near - 1) / camera.Far, 1 / camera.Far}},
            {"_Time", {time / 20, time, time * 2, time * 3}},
            {"_SinTime", {std::sin(time / 8), std::sin(time / 4), std::sin(time / 2), std::sin(time)}},
            {"_CosTime", {std::cos(time / 8), std::cos(time / 4), std::cos(time / 2), std::cos(time)}},
            {"unity_DeltaTime", {1.f / 30, 30, 1.f / 30, 30}},
            {"unity_OrthoParams", {0, 0, 0, 0}},
            {"glstate_lightmodel_ambient",
             {GpuMaterialComposer::Linear(ambient.Float("r")) * .5f,
              GpuMaterialComposer::Linear(ambient.Float("g")) * .5f,
              GpuMaterialComposer::Linear(ambient.Float("b")) * .5f, ambient.Float("a", 1) * .5f}},
            {"_TransShadowWeight", {0}},
            {"unity_OcclusionMaskSelector", {1, 0, 0, 0}},
            {"unity_ProbesOcclusion", {1, 1, 1, 1}},
            {"unity_ProbeVolumeParams", {0, 1, 1, 0}},
            {"unity_ProbeVolumeSizeInv", {1, 1, 1}},
            {"unity_ProbeVolumeMin", {0, 0, 0}},
            {"unity_ProbeVolumeWorldToObject", MatrixValues(IdentityMatrix())},
            {"unity_LightmapST", {1, 1, 0, 0}},
            {"unity_DynamicLightmapST", {1, 1, 0, 0}},
            {"unity_LightShadowBias", {0, 0, 0, 0}},
            {"_LightShadowData", {1, 0, 0, 0}},
            {"unity_SpecCube0_HDR", {reflectionIntensity, 1, 0, 0}},
            {"unity_SpecCube1_HDR", {reflectionIntensity, 1, 0, 0}},
            {"unity_SpecCube0_BoxMin", {-FLT_MAX, -FLT_MAX, -FLT_MAX, 0}},
            {"unity_SpecCube0_BoxMax", {FLT_MAX, FLT_MAX, FLT_MAX, 1}},
            {"unity_SpecCube0_ProbePosition", {0, 0, 0, 0}},
            {"unity_SpecCube1_BoxMin", {-FLT_MAX, -FLT_MAX, -FLT_MAX, 1}},
            {"unity_SpecCube1_BoxMax", {FLT_MAX, FLT_MAX, FLT_MAX, 1}},
            {"unity_SpecCube1_ProbePosition", {0, 0, 0, 0}}};
        constexpr const char *suffix[]{"r", "g", "b"};
        for (int channel = 0; channel < 3; ++channel)
        {
            auto c = sh.subspan(channel * 9, 9);
            result[std::string("unity_SHA") + suffix[channel]] = {c[3], c[1], c[2], c[0] - c[6]};
            result[std::string("unity_SHB") + suffix[channel]] = {c[4], c[5], c[6] * 3, c[7]};
        }
        result["unity_SHC"] = {sh[8], sh[17], sh[26], 1};
        for (const auto *name :
             {"unity_SHAr", "unity_SHAg", "unity_SHAb", "unity_SHBr", "unity_SHBg", "unity_SHBb", "unity_SHC"})
            for (int c = 0; c < (std::string_view(name) == "unity_SHC" ? 3 : 4); ++c)
                result[name][c] *= ambientScale;
        return result;
    }
}
