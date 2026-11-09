"""Real Blender import/material/render regression. Run with -- FOLDER OUTPUT IMPORTER."""
import importlib.machinery
import importlib.util
import json
import re
import sys
from pathlib import Path

sys.dont_write_bytecode = True

import bpy
from mathutils import Vector


folder, output, script = [Path(p).resolve() for p in sys.argv[sys.argv.index("--") + 1:]]
output.mkdir(parents=True, exist_ok=True)
loader = importlib.machinery.SourceFileLoader("hs2_import", str(script))
spec = importlib.util.spec_from_loader(loader.name, loader)
module = importlib.util.module_from_spec(spec)
loader.exec_module(module)
records = json.loads((folder / "materials.json").read_text(encoding="utf-8-sig"))["Materials"]
roles = {r["Id"]: r["Role"] for r in records}


def setup_scene():
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 24
    scene.cycles.use_denoising = True
    scene.render.resolution_x = 600
    scene.render.resolution_y = 800
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    scene.view_settings.exposure = 0
    scene.view_settings.gamma = 1
    world = bpy.data.worlds.new("Validation neutral world")
    scene.world = world
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.32, 0.32, 0.32, 1)
    world.node_tree.nodes["Background"].inputs[1].default_value = 0.25
    points = [obj.matrix_world @ Vector(v) for obj in scene.objects if obj.type == "MESH" for v in obj.bound_box]
    lower = Vector([min(p[i] for p in points) for i in range(3)])
    upper = Vector([max(p[i] for p in points) for i in range(3)])
    center = (lower + upper) / 2
    height = upper.z - lower.z
    camera_data = bpy.data.cameras.new("Validation camera")
    camera = bpy.data.objects.new("Validation camera", camera_data)
    scene.collection.objects.link(camera)
    scene.camera = camera
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = height * 1.12
    camera.location = center + Vector((0, -height * 2.5, 0))
    camera.rotation_euler = (center - camera.location).to_track_quat("-Z", "Y").to_euler()
    for name, offset, energy, size in (
        ("Key", (-0.8, -1.1, 0.9), 35, 1.0),
        ("Fill", (0.9, -0.8, 0.3), 12, 1.0),
        ("Rim", (0.3, 0.7, 0.7), 24, 0.8),
    ):
        data = bpy.data.lights.new(name, "AREA")
        data.energy = energy * height * height
        data.shape = "DISK"
        data.size = size * height
        light = bpy.data.objects.new(name, data)
        scene.collection.objects.link(light)
        light.location = center + Vector(offset) * height
        light.rotation_euler = (center - light.location).to_track_quat("-Z", "Y").to_euler()
    return lower, upper


def render_views(prefix, lower, upper):
    scene = bpy.context.scene
    scene.render.filepath = str(output / (prefix + "-front.png"))
    bpy.ops.render.render(write_still=True)
    head = next((o for o in scene.objects if o.type == "MESH" and "o_head" in o.name), None)
    if head:
        # Face inspection removes accessories in both renders so glasses and
        # helmets cannot hide broken eyes. The saved scene retains all objects.
        hidden = {}
        for obj in scene.objects:
            if obj.type != "MESH":
                continue
            ids = [slot.material.get("HS2_Id", re.sub(r"\.\d{3,}$", "", slot.material.name)) for slot in obj.material_slots if slot.material]
            if ids and all(roles.get(i) == "accessory" for i in ids):
                hidden[obj] = obj.hide_render
                obj.hide_render = True
        points = [head.matrix_world @ Vector(v) for v in head.bound_box]
        low = Vector([min(p[i] for p in points) for i in range(3)])
        high = Vector([max(p[i] for p in points) for i in range(3)])
        center = (low + high) / 2
        scene.camera.location.x = center.x
        scene.camera.location.z = center.z
        scene.camera.data.ortho_scale = (high.z - low.z) * 1.9
        scene.render.filepath = str(output / (prefix + "-face.png"))
        bpy.ops.render.render(write_still=True)
        for obj, value in hidden.items():
            obj.hide_render = value


bpy.ops.wm.read_factory_settings(use_empty=True)
if bpy.app.version >= (5, 0, 0):
    bpy.ops.wm.fbx_import(filepath=str(folder / "character.fbx"), use_anim=False)
else:
    bpy.ops.import_scene.fbx(filepath=str(folder / "character.fbx"), use_anim=False)
lower, upper = setup_scene()
render_views("before", lower, upper)
bpy.ops.wm.read_factory_settings(use_empty=True)
sentinel = bpy.data.objects.new("Existing user object", None)
bpy.context.scene.collection.objects.link(sentinel)
user_material = bpy.data.materials.new("mat_000")
user_material.diffuse_color = (0.1, 0.2, 0.3, 1)
collection = module.import_character(folder)
materials = {slot.material["HS2_Id"]: slot.material for obj in collection.objects if obj.type == "MESH" for slot in obj.material_slots}
assert set(materials) == {r["Id"] for r in records}
for record in records:
    mat = materials[record["Id"]]
    bsdf = next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    for key, socket in (("BaseColor", "Base Color"), ("Normal", "Normal"), ("Metallic", "Metallic"), ("Roughness", "Roughness"), ("Emission", "Emission Color")):
        if record.get(key):
            assert bsdf.inputs[socket].is_linked, (record["Id"], key)
            image = mat.node_tree.nodes["HS2 " + key].image
            assert image.size[0] > 0 and len(image.pixels) > 0, (record["Id"], key, image.filepath)
            assert image.colorspace_settings.name == ("sRGB" if key in ("BaseColor", "Emission") else "Non-Color")
    assert bsdf.inputs["Alpha"].is_linked == (record["AlphaMode"] != "Opaque")
    assert mat.use_backface_culling == (not record["DoubleSided"])
    if record["AlphaMode"] == "Mask":
        assert abs(mat.node_tree.nodes["HS2 Alpha cutoff"].inputs[1].default_value - record["AlphaCutoff"]) < 1e-6
    if module.material_kind(record) == "EyeShadow":
        surface = next(n for n in mat.node_tree.nodes if n.type == "OUTPUT_MATERIAL").inputs["Surface"]
        assert surface.links[0].from_node.type == "BSDF_TRANSPARENT"
lower, upper = setup_scene()
render_views("after", lower, upper)
bpy.ops.file.pack_all()
assert all(n.image.packed_file for mat in materials.values() for n in mat.node_tree.nodes if n.type == "TEX_IMAGE" and n.image)
bpy.ops.wm.save_as_mainfile(filepath=str(output / "character.blend"))
# A second character must not reuse or overwrite the first one's mat_000 etc.
second = module.import_character(folder)
second_materials = {slot.material for obj in second.objects if obj.type == "MESH" for slot in obj.material_slots}
assert not set(materials.values()) & second_materials
assert bpy.data.objects.get(sentinel.name) is sentinel
assert tuple(round(v, 5) for v in user_material.diffuse_color) == (0.1, 0.2, 0.3, 1.0)
report = {"Passed": True, "Blender": bpy.app.version_string, "Package": str(folder), "Materials": len(materials),
          "Meshes": sum(o.type == "MESH" for o in collection.objects), "Kinds": sorted({m["HS2_Kind"] for m in materials.values()}),
          "HeightMeters": upper.z - lower.z, "RepeatedImportIsolated": True, "ExistingScenePreserved": True,
          "FaceInspectionHidesAccessories": True,
          "TexturesPacked": True,
          "Images": [p.name for p in output.glob("*.png")]}
(output / "validation.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print("BLENDER EXPORT CHECKS PASSED", json.dumps(report))
