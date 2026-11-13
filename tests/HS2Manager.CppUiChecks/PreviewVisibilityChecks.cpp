#include "Preview/NativePreviewWorker.h"
#include <cmath>
#include <iostream>

using namespace hs2;
using namespace hs2::ui;

int wmain(int argc, wchar_t **argv)
{
    if (argc != 5)
    {
        std::cerr << "Usage: hs2_preview_visibility_checks game data classdata.tpk card.png\n";
        return 2;
    }
    try
    {
        fs::path output = fs::path(argv[2]) / ("visibility-" + NewId());
        std::mutex mutex;
        std::condition_variable changed;
        int views = 0, ready = 0, progress = 0, screenshots = 0;
        int motionChanges = 0, selectedMotion = 0;
        std::string motionError;
        uint64_t viewGeneration = 0;
        NativeRendererBounds viewBounds;
        UINT firstPresentCount = 0;
        std::string error;
        std::optional<PreviewReady> result;
        PreviewCallbacks events;
        events.PrepareView = [&](uint64_t generation, NativeRendererBounds body, NativeRendererBounds)
        {
            std::lock_guard lock(mutex);
            viewGeneration = generation;
            viewBounds = body;
            ++views;
            changed.notify_all();
        };
        events.Ready = [&](PreviewReady value)
        {
            std::lock_guard lock(mutex);
            if (ready == 0 && FAILED(value.Chain->GetLastPresentCount(&firstPresentCount)))
                error = "Cannot inspect the first presented frame.";
            result = std::move(value);
            ++ready;
            changed.notify_all();
        };
        events.Progress = [&](auto, auto)
        {
            std::lock_guard lock(mutex);
            ++progress;
        };
        events.Failure = [&](auto, std::string message)
        {
            std::lock_guard lock(mutex);
            error = std::move(message);
            changed.notify_all();
        };
        events.Screenshot = [&](auto, std::string message)
        {
            std::lock_guard lock(mutex);
            error = std::move(message);
            ++screenshots;
            changed.notify_all();
        };
        events.MotionChanged = [&](auto, int, int selected, auto, auto, std::string message)
        {
            std::lock_guard lock(mutex);
            selectedMotion = selected;
            motionError = std::move(message);
            ++motionChanges;
            changed.notify_all();
        };
        auto wait = [&](auto complete)
        {
            std::unique_lock lock(mutex);
            if (!changed.wait_for(lock, std::chrono::seconds(60),
                                  [&]
                                  {
                                      return !error.empty() || complete();
                                  }))
                throw std::runtime_error("Preview command timed out.");
            if (!error.empty())
                throw std::runtime_error(error);
        };
        auto card = CardParser().Parse(argv[4]);
        if (card.Status != CardStatus::Valid)
            throw std::runtime_error(card.Error);
        NativePreviewWorker worker(argv[1], argv[2], argv[3], std::move(events));
        worker.Paused(true);
        NativeFrameOptions options;
        options.StaticReference = true;
        worker.Environment(options);
        worker.Resize({480, 640, 1});
        auto generation = worker.Load(card);
        worker.Visibility({2, false});
        wait(
            [&]
            {
                return views == 1;
            });
        // 普通镜头命令或已过期的界面回调都不能放行新人物的默认镜头。
        worker.View({}, false);
        if (worker.ConfirmView(generation - 1, {}))
            throw std::runtime_error("A stale camera acknowledgement was accepted.");
        {
            std::unique_lock lock(mutex);
            if (changed.wait_for(lock, std::chrono::milliseconds(250),
                                 [&]
                                 {
                                     return ready != 0 || !error.empty();
                                 }))
                throw std::runtime_error("Preview became ready before the UI framed the character: " + error);
        }
        if (viewGeneration != generation)
            throw std::runtime_error("Camera preparation belongs to a stale character.");
        auto bounds = viewBounds;
        NativeRenderCamera camera;
        camera.Target = bounds.Center;
        float distance = std::max(bounds.Extents.x * 2 / .75f, bounds.Extents.y * 2) /
                             (2 * std::tan(35.f * 3.14159265f / 360)) * 1.12f +
                         bounds.Extents.z * 2;
        camera.Position = {bounds.Center.x, bounds.Center.y, bounds.Center.z + distance};
        camera.FieldOfView = 35;
        if (!worker.ConfirmView(generation, camera))
            throw std::runtime_error("The current character camera was rejected.");
        wait(
            [&]
            {
                return ready == 1;
            });
        if (firstPresentCount != 1)
            throw std::runtime_error(
                "Ready must follow exactly one framed presentation, without a default-camera frame.");
        if (result->Generation != generation || result->Definition->Visibility != CharacterPreviewVisibility{2, false})
            throw std::runtime_error("Navigation during loading lost the latest visibility state.");
        int frame = 0;
        auto screenshot = [&]
        {
            auto path = output / (std::to_string(++frame) + ".png");
            worker.Screenshot({path, {}});
            wait(
                [&]
                {
                    return screenshots == frame;
                });
            return HashFile(path);
        };
        auto bare = screenshot();
        if (result->Motions.size() < 3 || result->MotionIndex != 0)
            throw std::runtime_error("Preview did not publish the character's motion choices.");
        auto chooseMotion = [&](int index, bool reject = false, bool optionalMod = false)
        {
            int before;
            int previous;
            {
                std::lock_guard lock(mutex);
                before = motionChanges;
                previous = selectedMotion;
            }
            worker.Motion(generation, index);
            wait(
                [&]
                {
                    return motionChanges > before;
                });
            if (optionalMod && !motionError.empty())
            {
                if (selectedMotion != previous)
                    throw std::runtime_error("An unavailable MOD motion replaced the current pose.");
                std::cout << "Unavailable MOD motion: " << result->Motions.at(index) << ": " << motionError << '\n';
            }
            else if (reject ? motionError.empty() : !motionError.empty() || selectedMotion != index)
                throw std::runtime_error("Motion selection failed: " + motionError);
            return screenshot();
        };
        // 遍历真实清单，验证跨资源包动作可以绑定，并在暂停时直接重绘。
        bool changedPose = false;
        for (size_t index = 1; index < result->Motions.size(); ++index)
        {
            auto hash =
                chooseMotion(static_cast<int>(index), false, result->Motions[index].find(" · ") != std::string::npos);
            changedPose = changedPose || hash != bare;
        }
        if (!changedPose)
            throw std::runtime_error("Switching motions never changed the paused pose.");
        auto lastMotion = selectedMotion;
        chooseMotion(static_cast<int>(result->Motions.size()) + 5, true);
        if (selectedMotion != lastMotion)
            throw std::runtime_error("Invalid motion changed the current selection.");
        chooseMotion(0);
        worker.Motion(generation - 1, 1);
        screenshot();
        if (selectedMotion != 0)
            throw std::runtime_error("A stale character changed the motion.");
        // 覆盖超过旧远裁剪面和最大滚轮距离的实际 GPU 帧。
        for (float zoom : {110.f, 250.f, 500.f})
        {
            auto distant = camera;
            distant.Position.z = bounds.Center.z + zoom;
            worker.View(distant, false);
            screenshot();
        }
        worker.View(camera, false);
        bare = chooseMotion(2);
        std::cout << "PASS motion choices=" << result->Motions.size()
                  << ", paused switching, invalid/stale selection, and zoom frames to distance 500\n";
        int initialProgress;
        {
            std::lock_guard lock(mutex);
            initialProgress = progress;
        }
        for (auto visibility : {CharacterPreviewVisibility{0, true}, {1, false}, {2, false}})
        {
            auto start = std::chrono::steady_clock::now();
            worker.Visibility(visibility);
            auto hash = screenshot();
            if (visibility.Clothing != 2 && hash == bare)
                throw std::runtime_error("Paused visibility switch did not redraw the clothes.");
            auto milliseconds =
                std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start).count();
            std::cout << "Visibility " << visibility.Clothing << " screenshot completed in " << milliseconds << " ms\n";
        }
        for (int index = 0; index < 60; ++index)
            worker.Visibility({index % 3, index % 2 == 0});
        worker.Visibility({2, false});
        // 后处理与物理重置不保证 PNG 字节相同；重绘完成后检查没有发起新的资源加载。
        screenshot();
        {
            std::lock_guard lock(mutex);
            if (ready != 1 || progress != initialProgress)
                throw std::runtime_error("Visibility-only navigation reloaded character resources.");
        }
        auto original = CharacterDocument::Open(argv[4]);
        auto draft = original;
        auto path = "body.shapeValueBody[0]";
        draft = draft.WithValue(path, .7f);
        auto chain = result->Chain.Get();
        auto update = [&](CharacterDocument next, const char *label)
        {
            int completed;
            {
                std::lock_guard lock(mutex);
                completed = ready;
            }
            auto start = std::chrono::steady_clock::now();
            worker.Update(std::move(next), {});
            worker.Visibility({2, false});
            wait(
                [&]
                {
                    return ready > completed;
                });
            if (result->Generation != generation || result->Chain.Get() != chain || progress != initialProgress ||
                result->Definition->Visibility != CharacterPreviewVisibility{2, false} || result->MotionIndex != 2)
                throw std::runtime_error("A parameter edit reloaded the preview or lost the current visibility.");
            auto milliseconds =
                std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start).count();
            std::cout << "Draft " << label << " applied in " << milliseconds
                      << " ms; resource loads=" << result->ResourceLoads << '\n';
        };
        update(draft, "body shape");
        if (screenshot() == bare)
            throw std::runtime_error("A pending draft update was lost during navigation.");
        auto resources = result->ResourceLoads;
        draft = draft.WithValue(path, .3f);
        update(draft, "body shape again");
        if (result->ResourceLoads != resources)
            throw std::runtime_error("A shape edit reread character resources.");
        draft = draft.WithValue("face.shapeValueFace[0]", .8f);
        update(draft, "face shape");
        if (result->ResourceLoads != resources)
            throw std::runtime_error("A face edit reread character resources.");
        for (auto color : {"body.skinColor", "hair.parts[0].baseColor"})
            if (draft.Contains(color))
            {
                auto before = screenshot();
                draft = CharacterEditRules::Color(draft, color, {.1f, .8f, .2f, 1});
                update(draft, color);
                if (screenshot() == before)
                    throw std::runtime_error("A color edit did not redraw the material.");
            }
        auto edited = draft;
        auto eyeOpen = result->Definition->EyeOpenMax;
        auto extensions = draft.Extensions().value_or(MessagePack::Map());
        extensions = extensions.Set(
            std::string("com.deathweasel.bepinex.eyecontrol"),
            MessagePack::Array({MessagePack::Scalar(int64_t(0)),
                                MessagePack::Map({{std::string("EyeOpenMax"), MessagePack::Scalar(.25f)},
                                                  {std::string("DisableBlinking"), MessagePack::Scalar(true)}})}),
            true);
        update(draft.WithExtensions(extensions), "extension eye control");
        if (std::abs(result->Definition->EyeOpenMax - .25f) > 1e-6 ||
            result->Definition->Face.Get("disableBlinking").Number != 1)
            throw std::runtime_error("Changed extensions reused stale cached parameters.");
        update(draft, "restore extensions");
        if (std::abs(result->Definition->EyeOpenMax - eyeOpen) > 1e-6)
            throw std::runtime_error("Restored extensions did not invalidate the cache.");
        update(original, "undo all");
        update(edited, "redo all");
        if (draft.Contains("accessory.parts[0].type") && draft.Node("accessory.parts[0].type").Integer() > 350)
        {
            auto parts = result->Definition->Parts.size();
            update(draft.WithValue("accessory.parts[0].type", int64_t(350)), "remove accessory");
            if (result->Definition->Parts.size() >= parts)
                throw std::runtime_error("Removing an accessory did not update the scene.");
            update(draft, "restore accessory");
            if (result->Definition->Parts.size() != parts)
                throw std::runtime_error("Restoring an accessory lost a scene part.");
        }
        for (int index = 0; index < 60; ++index)
            worker.Update(draft.WithValue(path, .1f + index * .01f), {2, false});
        wait(
            [&]
            {
                return std::abs(result->Definition->Body.Get("shapeValueBody").Children[0].Number - .69f) < 1e-6;
            });
        screenshot();
        if (result->Chain.Get() != chain || result->Generation != generation || progress != initialProgress)
            throw std::runtime_error("Rapid parameter edits rebuilt the preview surface.");
        worker.Quiesce();
        worker.Unload();
        // 资源就绪、等待界面取景时取消也必须立即返回；不能等待一个已离开的界面确认。
        generation = worker.Load(card);
        wait(
            [&]
            {
                return views == 2;
            });
        worker.Quiesce();
        worker.Unload();
        if (worker.ConfirmView(generation, camera))
            throw std::runtime_error("An unloaded character accepted a camera acknowledgement.");
        std::cout << "PASS: framed first presentation, delayed/stale camera callbacks, cancel before first frame, "
                     "paused redraw, reversible clothing, live shape/color/part edits, undo/redo and rapid "
                     "edits; frames="
                  << Utf8(output) << '\n';
        return 0;
    }
    catch (std::exception const &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
