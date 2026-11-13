#include "SharedAssets.h"
#include "Pipeline/NativeFramePipeline.h"
#include "Runtime/CharacterRuntime.h"
#include "Images/ImageFiles.h"
#include <cmath>
#include <set>

namespace hs2::creation
{
    Json RenderCharacter(const fs::path &game, const fs::path &data, const fs::path &runtimeDirectory,
                         const CharacterDocument &document, const Json &args, const fs::path &output,
                         PreviewResources resources, std::stop_token token)
    {
        CardRecord card;
        card.Path = Utf8(document.SourcePath());
        card.Sex = document.Sex();
        NativeCharacterService service(game, data, runtimeDirectory / "classdata.tpk", resources);
        CharacterPreviewVisibility visibility;
        visibility.Clothing = args.value("clothing_state", 0);
        auto prepared = service.Prepare(card, &document, visibility, {}, token);
        GraphicsDevice graphics;
        NativeCharacterRuntime runtime(prepared.Scene, prepared.Assets, runtimeDirectory / "native/win-x64");
        runtime.Update(0);
        float sampleTime = args.value("sample_time", 0.f);
        if (!std::isfinite(sampleTime) || sampleTime < 0 || sampleTime > 5)
            throw std::invalid_argument("sample_time 必须介于 0 到 5 秒");
        for (float time = 1.f / 30; time <= sampleTime; time += 1.f / 30)
        {
            CheckCancelled(token);
            runtime.Update(time);
        }
        PoseCharacterFace(*prepared.Scene, 1);
        NativeFrameOptions options;
        options.StaticReference = true;
        auto background = args.value("background", "game");
        if (background != "game" && background != "neutral")
            throw std::invalid_argument("background 必须为 game/neutral");
        if (background == "neutral")
        {
            options.UseGameBackground = false;
            options.Background = Vector4{.72f, .72f, .72f, 1};
        }
        auto side = args.value("side", "right");
        if (side != "left" && side != "right")
            throw std::invalid_argument("side 必须为 left/right");
        NativeFramePipeline pipeline(graphics, *prepared.Scene, *prepared.Assets, options);
        Vector3 lo{INFINITY, INFINITY, INFINITY}, hi{-INFINITY, -INFINITY, -INFINITY};
        Vector3 faceLo = lo, faceHi = hi;
        for (auto &renderer : prepared.Scene->Renderers)
            if (renderer->Visible() && renderer->HasGeometry())
            {
                // Unity 的保守蒙皮包围盒可能远大于可见人物；用实际形变顶点取景。
                auto deformed = renderer->Deform();
                auto world = renderer->Node->WorldMatrix();
                for (auto &vertex : deformed.Positions)
                {
                    auto point = TransformPoint(vertex, world);
                    if (!std::isfinite(point.x) || !std::isfinite(point.y) || !std::isfinite(point.z))
                        throw std::runtime_error("形变产生非有限坐标");
                    lo = {(std::min)(lo.x, point.x), (std::min)(lo.y, point.y), (std::min)(lo.z, point.z)};
                    hi = {(std::max)(hi.x, point.x), (std::max)(hi.y, point.y), (std::max)(hi.z, point.z)};
                    if (renderer->Node->Name == "o_head" || renderer->Role == "head")
                    {
                        faceLo = {(std::min)(faceLo.x, point.x), (std::min)(faceLo.y, point.y),
                                  (std::min)(faceLo.z, point.z)};
                        faceHi = {(std::max)(faceHi.x, point.x), (std::max)(faceHi.y, point.y),
                                  (std::max)(faceHi.z, point.z)};
                    }
                }
            }
        if (!std::isfinite(lo.y) || hi.y <= lo.y)
            throw std::runtime_error("人物没有可渲染几何");
        int width = (std::clamp)(args.value("width", 512), 128, 2048);
        int height = (std::clamp)(args.value("height", 640), 128, 2048);
        auto directory = output;
        fs::create_directories(directory);
        Json images = Json::array();
        auto views = args.value("views", Json::array({"front", "side", "back", "face"}));
        if (!views.is_array() || views.empty() || views.size() > 6)
            throw std::invalid_argument("视图数量必须为 1 到 6");
        std::set<std::string> seenViews;
        for (auto &v : views)
        {
            CheckCancelled(token);
            auto view = v.get<std::string>();
            float angle = view == "front" || view == "face"       ? 0.f
                          : view == "side" || view == "face_side" ? (side == "left" ? -1.5707963f : 1.5707963f)
                          : view == "back"                        ? 3.1415926f
                          : view == "three_quarter"               ? .7853982f
                                                                  : INFINITY;
            if (!std::isfinite(angle))
                throw std::invalid_argument("未知视图");
            if (!seenViews.insert(view).second)
                throw std::invalid_argument("视图不能重复");
            NativeRenderCamera camera;
            camera.Target = {(lo.x + hi.x) * .5f, (lo.y + hi.y) * .5f, (lo.z + hi.z) * .5f};
            float size = hi.y - lo.y;
            float span = (std::max)(hi.x - lo.x, hi.z - lo.z);
            float depth = hi.z - lo.z;
            if (view == "face" || view == "face_side")
            {
                if (!std::isfinite(faceLo.y))
                    throw std::runtime_error("无法定位头部几何");
                camera.Target = {(faceLo.x + faceHi.x) * .5f, (faceLo.y + faceHi.y) * .5f, (faceLo.z + faceHi.z) * .5f};
                size = (faceHi.y - faceLo.y) * 1.25f;
                span = (view == "face_side" ? faceHi.z - faceLo.z : faceHi.x - faceLo.x) * 1.25f;
                depth = view == "face_side" ? faceHi.x - faceLo.x : faceHi.z - faceLo.z;
            }
            camera.FieldOfView = args.value("field_of_view", 35.f);
            if (!std::isfinite(camera.FieldOfView) || camera.FieldOfView < 10 || camera.FieldOfView > 90)
                throw std::invalid_argument("视角必须介于 10 和 90 度");
            float distance =
                (std::max)(size, span * height / width) / (2 * std::tan(camera.FieldOfView * .008726646f)) * 1.12f +
                depth * .5f;
            float zoom = args.value("zoom", 1.f), offset = args.value("vertical_offset", 0.f);
            if (!std::isfinite(zoom) || zoom < .2f || zoom > 5 || !std::isfinite(offset) || std::abs(offset) > 10)
                throw std::invalid_argument("封面取景参数超出范围");
            if (view == "face" || view == "face_side")
            {
                float faceZoom = args.value("face_zoom", 1.f), faceOffset = args.value("face_vertical_offset", 0.f);
                if (!std::isfinite(faceZoom) || faceZoom < .5f || faceZoom > 2.5f || !std::isfinite(faceOffset) ||
                    std::abs(faceOffset) > .5f)
                    throw std::invalid_argument("face_zoom 需介于 0.5 到 2.5，face_vertical_offset 需介于 -0.5 到 0.5");
                // 头部网格还包含后脑和颈部；允许将五官对照中心移至眉眼到下颌区域。
                zoom *= faceZoom;
                offset += faceOffset * (faceHi.y - faceLo.y);
            }
            distance /= zoom;
            camera.Target.y += offset;
            camera.Position = {camera.Target.x + std::sin(angle) * distance, camera.Target.y,
                               camera.Target.z + std::cos(angle) * distance};
            pipeline.ResetHistory();
            auto &frame = pipeline.Draw(camera, width, height, sampleTime);
            auto pixels = graphics.Readback(frame.Texture.Get());
            std::set<std::uint32_t> colors;
            for (size_t p = 0; p < pixels.size(); p += 4)
            {
                pixels[p + 3] = 255;
                colors.insert((pixels[p] << 16) | (pixels[p + 1] << 8) | pixels[p + 2]);
            }
            if (colors.size() < 64)
                throw std::runtime_error("渲染结果为空或缺少有效颜色");
            auto imagePath = directory / Path(view + ".png");
            SavePng(imagePath, width, height, pixels);
            images.push_back({{"path", Utf8(imagePath)},
                              {"mime_type", "image/png"},
                              {"view", view},
                              {"camera",
                               {{"field_of_view", camera.FieldOfView},
                                {"side", side},
                                {"target", {camera.Target.x, camera.Target.y, camera.Target.z}},
                                {"distance", distance}}}});
        }
        Json result{{"images", images},
                    {"diagnostics", prepared.Scene->Diagnostics},
                    {"background", background},
                    {"clothing_state", visibility.Clothing},
                    {"sample_time", sampleTime}};
        return result;
    }
}
