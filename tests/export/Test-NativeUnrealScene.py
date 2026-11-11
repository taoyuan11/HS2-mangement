"""读取 HS2_NATIVE_REVIEW 请求，并从匹配视角渲染原生关卡。
使用 -ExecCmds="py this.py"，使 Slate 截图回调能够执行完成。"""

import json
import math
import os
import time
import traceback
from pathlib import Path

import unreal

request = json.loads(Path(os.environ["HS2_NATIVE_REVIEW"]).read_text(encoding="utf-8-sig"))
output = Path(request["Output"])
output.mkdir(parents=True, exist_ok=True)
sample = request["Sample"]
root = Path(request["Root"])
report = {"Passed": False, "Sample": sample, "ToolVersion": unreal.SystemLibrary.get_engine_version(), "Views": []}
handle = None
task = None
index = 0
frames = 0
inside = False
started = time.monotonic()
views = request.get("Views", ("front", "face", "sideback"))


def position(p, scale=10):
    return unreal.Vector(-p[0] * scale, p[2] * scale, p[1] * scale)


def finish(error=None):
    if handle is not None:
        unreal.unregister_slate_post_tick_callback(handle)
    report["Passed"] = error is None
    if error:
        report["Error"] = error
    (output / f"unreal-scene-{sample}.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    unreal.SystemLibrary.quit_editor()


def tick(delta):
    global inside, frames, index, task
    if inside:
        return
    inside = True
    try:
        if time.monotonic() - started > 600:
            raise RuntimeError("Native scene screenshot timeout")
        if task:
            if not task.is_task_done():
                return
            report["Views"].append(str(output / f"unreal-{sample}-{views[index]}.png"))
            task = None
            index += 1
            frames = 0
            if index == len(views):
                finish()
                return
        name = views[index]
        if frames == 0:
            folder = root / (f"final-{sample}" if name == "front" else f"review-{sample}-{name}") / "intermediate"
            if name in request.get("SourceFolders", {}):
                folder = Path(request["SourceFolders"][name])
            data = json.loads((folder / "scene.json").read_text(encoding="utf-8-sig"))
            view = data["View"]
            eye, target = position(view["Position"]), position(view["Target"])
            rotation = unreal.MathLibrary.find_look_at_rotation(eye, target)
            camera.set_actor_location(eye, False, False)
            camera.set_actor_rotation(rotation, False)
            unreal.EditorLevelLibrary.set_level_viewport_camera_info(eye, rotation)
            for light in data["Lights"]:
                actor = next(a for a in actors if a.get_actor_label() == light["Name"])
                actor.set_actor_location(position(light["Position"]), False, False)
                actor.set_actor_rotation(
                    unreal.MathLibrary.find_look_at_rotation(unreal.Vector(), position(light["Direction"], 1)), False
                )
        frames += 1
        if frames < 24:
            return
        unreal.log("Native scene capture: " + str(sample) + "/" + name)
        unreal.AutomationLibrary.finish_loading_before_screenshot()
        task = unreal.AutomationLibrary.take_high_res_screenshot(
            1280, 960, str(output / f"unreal-{sample}-{name}.png"), camera=camera
        )
    except Exception:
        finish(traceback.format_exc())
    finally:
        inside = False


try:
    assert unreal.EditorLoadingAndSavingUtils.load_map("/Game/HS2Character/Preview")
    actors = unreal.get_editor_subsystem(unreal.EditorActorSubsystem).get_all_level_actors()
    camera = next(a for a in actors if isinstance(a, unreal.CameraActor))
    mesh = (
        next(a for a in actors if isinstance(a, unreal.SkeletalMeshActor))
        .get_component_by_class(unreal.SkeletalMeshComponent)
        .get_skeletal_mesh_asset()
    )
    export = unreal.AssetExportTask()
    export.object = mesh
    export.filename = str(output / f"unreal-{sample}-roundtrip.fbx")
    export.automated = True
    export.prompt = False
    export.replace_identical = True
    export.exporter = unreal.SkeletalMeshExporterFBX()
    export.options = unreal.FbxExportOption()
    export.options.set_editor_property("export_morph_targets", True)
    export.options.set_editor_property("level_of_detail", False)
    export.options.set_editor_property("collision", False)
    export.options.set_editor_property("bake_material_inputs", unreal.FbxMaterialBakeMode.DISABLED)
    assert unreal.Exporter.run_asset_export_task(export), "Native mesh roundtrip export failed"
    world = unreal.get_editor_subsystem(unreal.UnrealEditorSubsystem).get_editor_world()
    for command in (
        "r.ScreenPercentage 100",
        "r.HighResScreenshotDelay 32",
        "r.EyeAdaptationQuality 0",
        "Slate.bAllowThrottling 0",
        "t.IdleWhenNotForeground 0",
    ):
        unreal.SystemLibrary.execute_console_command(world, command)
    handle = unreal.register_slate_post_tick_callback(tick)
except Exception:
    finish(traceback.format_exc())
