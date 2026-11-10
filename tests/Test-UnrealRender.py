"""UE editor screenshot review. Uses an isolated Test-UnrealExport project.
Set HS2_UNREAL_VALIDATION to its request.json, then -ExecCmds="py this.py".
"""
import json
import os
import runpy
import time
import traceback
import uuid
from pathlib import Path

import unreal

request = json.loads(Path(os.environ["HS2_UNREAL_VALIDATION"]).read_text(encoding="utf-8-sig"))
output = Path(request["Output"])
validation = json.loads((output / "validation.json").read_text(encoding="utf-8"))
actors = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)
level = unreal.get_editor_subsystem(unreal.LevelEditorSubsystem)
report = {"Passed": False, "Screenshots": []}
handle = None
started = time.monotonic()
task = None
index = 0
pending = None
capture_time = 0
inside_tick = False
prepared = False
warm_frames = 0


def setup():
    unreal.log("HS2 render: creating review scene")
    if not level.new_level("/Game/Validation/RenderReview_" + uuid.uuid4().hex):
        raise RuntimeError("Could not create review level")
    world = unreal.get_editor_subsystem(unreal.UnrealEditorSubsystem).get_editor_world()
    for name, value in (("r.AntiAliasingMethod", 4),
                         ("r.ScreenPercentage", 100), ("r.EyeAdaptationQuality", 0),
                         ("r.HighResScreenshotDelay", 64),
                         ("r.Shadow.Virtual.Enable", 0), ("r.Lumen.DiffuseIndirect.Allow", 0)):
        unreal.SystemLibrary.execute_console_command(world, name + " " + str(value))
    actor = actors.spawn_actor_from_class(unreal.SkeletalMeshActor, unreal.Vector())
    component = actor.get_component_by_class(unreal.SkeletalMeshComponent)
    mesh_path = validation["Samples"][0]["Destination"] + "/SK_Character"
    optimized = unreal.load_asset(mesh_path)
    component.set_skeletal_mesh_asset(optimized)
    unreal.log("HS2 render: skeletal mesh loaded")
    origin, extent = actor.get_actor_bounds(False)
    height = extent.z * 2
    report["Bounds"] = {"Origin": [origin.x, origin.y, origin.z], "Extent": [extent.x, extent.y, extent.z]}
    # A dark neutral room with three white lights exposes color/normal errors.
    for location, intensity in ((unreal.Vector(-height * 2, height, height * 2), 4.0),
                                (unreal.Vector(height * 2, height, height * 1.5), 3.0)):
        light = actors.spawn_actor_from_class(unreal.DirectionalLight, location)
        light.set_actor_rotation(unreal.MathLibrary.find_look_at_rotation(location, origin), False)
        light.get_component_by_class(unreal.DirectionalLightComponent).set_intensity(intensity)
    sky = actors.spawn_actor_from_class(unreal.SkyLight, unreal.Vector())
    sky_component = sky.get_component_by_class(unreal.SkyLightComponent)
    sky_component.set_editor_property("source_type", unreal.SkyLightSourceType.SLS_SPECIFIED_CUBEMAP)
    sky_component.set_editor_property("cubemap", unreal.load_asset("/Engine/MapTemplates/Sky/DaylightAmbientCubemap.DaylightAmbientCubemap"))
    sky_component.set_intensity(.6)
    camera = actors.spawn_actor_from_class(unreal.CameraActor, unreal.Vector())
    camera_component = camera.get_component_by_class(unreal.CameraComponent)
    camera_component.set_editor_property("field_of_view", 35.0)
    camera_component.set_editor_property("aspect_ratio", 1.0)
    camera_component.set_editor_property("constrain_aspect_ratio", True)
    hidden = unreal.AssetToolsHelpers.get_asset_tools().create_asset("Hidden_" + uuid.uuid4().hex,
        "/Game/Validation", unreal.Material, unreal.MaterialFactoryNew())
    hidden.set_editor_property("blend_mode", unreal.BlendMode.BLEND_TRANSLUCENT)
    hidden.set_editor_property("shading_model", unreal.MaterialShadingModel.MSM_UNLIT)
    zero = unreal.MaterialEditingLibrary.create_material_expression(hidden, unreal.MaterialExpressionConstant)
    zero.set_editor_property("r", 0.0)
    unreal.MaterialEditingLibrary.connect_material_property(zero, "", unreal.MaterialProperty.MP_OPACITY)
    unreal.MaterialEditingLibrary.set_material_usage(hidden, unreal.MaterialUsage.MATUSAGE_SKELETAL_MESH)
    unreal.MaterialEditingLibrary.recompile_material(hidden)
    views = []
    for sample_index, sample in enumerate(validation["Samples"]):
        optimized = unreal.load_asset(sample["Destination"] + "/SK_Character")
        component.set_skeletal_mesh_asset(optimized)
        center, bounds = actor.get_actor_bounds(False)
        h = bounds.z * 2
        folder = Path(sample["Package"])
        records = json.loads((folder / "materials.json").read_text(encoding="utf-8-sig"))["Materials"]
        accessories = {r["Id"] for r in records if r.get("Role") == "accessory"}
        importer = runpy.run_path(request["Importer"], run_name="hs2_render_importer")
        options = unreal.FbxImportUI()
        for key, value in dict(automated_import_should_detect_type=False, import_as_skeletal=True,
                               import_mesh=True, import_materials=True, import_textures=True,
                               import_animations=False, create_physics_asset=False,
                               mesh_type_to_import=unreal.FBXImportType.FBXIT_SKELETAL_MESH).items():
            options.set_editor_property(key, value)
        raw_options = options.get_editor_property("skeletal_mesh_import_data")
        raw_options.set_editor_property("normal_import_method", unreal.FBXNormalImportMethod.FBXNIM_IMPORT_NORMALS_AND_TANGENTS)
        raw_options.set_editor_property("convert_scene_unit", True)
        raw_assets = importer["import_asset"](folder / "character.fbx", "/Game/Validation/Raw_" + uuid.uuid4().hex,
                                              "Raw", options, unreal.FbxFactory())
        raw = next(a for a in raw_assets if isinstance(a, unreal.SkeletalMesh))
        variants = [("before", raw, True), ("after", optimized, True)]
        for prefix, mesh, casts_shadow in variants:
            for view in ("front", "face"):
                target = center if view == "front" else unreal.Vector(center.x, center.y, center.z + h * .36)
                position = unreal.Vector(center.x, center.y + h * (2 if view == "front" else .48), target.z)
                views.append(("sample-" + str(sample_index) + "-" + prefix + "-" + view, position, target, mesh,
                              accessories if view == "face" else set(), casts_shadow))
    return world, actor, camera, views, hidden


def finish(error=None):
    if handle is not None:
        unreal.unregister_slate_post_tick_callback(handle)
    report["Passed"] = error is None
    if error:
        report["Error"] = error
        unreal.log_error(error)
    (output / "render-validation.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    unreal.SystemLibrary.quit_editor()


def tick(delta):
    global task, index, pending, capture_time, inside_tick, prepared, warm_frames
    # finish_loading_before_screenshot pumps Slate while shaders/textures load.
    if inside_tick:
        return
    inside_tick = True
    try:
        if time.monotonic() - started > 600:
            raise RuntimeError("Screenshot timeout")
        if task:
            if not task.is_task_done():
                return
            report["Screenshots"].append(pending)
            task = None
            index += 1
            prepared = False
            capture_time = time.monotonic()
            if index >= len(views):
                finish()
                return
        if time.monotonic() - capture_time < 4:
            return
        name, position, target, mesh, accessories, casts_shadow = views[index]
        if not prepared:
            component = actor.get_component_by_class(unreal.SkeletalMeshComponent)
            component.set_skeletal_mesh_asset(mesh)
            component.set_editor_property("cast_shadow", casts_shadow)
            component.set_editor_property("override_materials", [])
            for slot_index, slot in enumerate(mesh.get_editor_property("materials")):
                if str(slot.get_editor_property("imported_material_slot_name")) in accessories:
                    component.set_material(slot_index, hidden_material)
            camera.set_actor_location(position, False, False)
            rotation = unreal.MathLibrary.find_look_at_rotation(position, target)
            camera.set_actor_rotation(rotation, False)
            unreal.EditorLevelLibrary.set_level_viewport_camera_info(position, rotation)
            prepared = True
            warm_frames = 0
            return
        warm_frames += 1
        if warm_frames < 96:
            return
        unreal.AutomationLibrary.finish_loading_before_screenshot()
        pending = str(output / ("ue-" + name + ".png"))
        unreal.log("HS2 render: capturing " + pending)
        task = unreal.AutomationLibrary.take_high_res_screenshot(1000, 1000, pending, camera=camera)
    except Exception:
        finish(traceback.format_exc())
    finally:
        inside_tick = False


try:
    world, actor, camera, views, hidden_material = setup()
    report["FaceAccessoriesHidden"] = True
    capture_time = time.monotonic()
    handle = unreal.register_slate_post_tick_callback(tick)
except Exception:
    finish(traceback.format_exc())
