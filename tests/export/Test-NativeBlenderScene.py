"""重新打开交付的 Blender 场景，验证形变并渲染与 Unity 匹配的视角。
运行参数为 -- ROOT OUTPUT；ROOT 包含 final-{0,1,2} 和 review-{i}-{view}。"""

import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector

arguments = sys.argv[sys.argv.index("--") + 1 :]
root, output = [Path(p).resolve() for p in arguments[:2]]
aligned = "--aligned" in arguments[2:]
output.mkdir(parents=True, exist_ok=True)
report = {"Passed": False, "ToolVersion": bpy.app.version_string, "Samples": []}


def position(p, scale=0.1):
    return Vector((-p[0] * scale, -p[2] * scale, p[1] * scale))


def geometry(obj):
    bpy.context.view_layer.update()
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = evaluated.to_mesh()
    try:
        return [obj.matrix_world @ v.co for v in mesh.vertices]
    finally:
        evaluated.to_mesh_clear()


for sample in range(3):
    native_folder = f"aligned-blender-{sample}" if aligned and sample == 2 else f"verified-blender-{sample}"
    bpy.ops.wm.open_mainfile(filepath=str(root / native_folder / "Blender" / "character.blend"))
    scene = bpy.context.scene
    sample_report = {"Sample": sample, "Expressions": [], "ArmDeformation": False, "Views": []}
    meshes = [o for o in scene.objects if o.type == "MESH"]
    for obj in meshes:
        if not obj.data.shape_keys:
            continue
        for suffix in ("e01_close", "k01_open0"):
            block = next((b for b in obj.data.shape_keys.key_blocks if b.name.endswith(suffix)), None)
            if block is None:
                continue
            before = geometry(obj)
            block.value = 1
            after = geometry(obj)
            block.value = 0
            delta = max((a - b).length for a, b in zip(before, after))
            assert delta > 0.00001, (obj.name, suffix, delta)
            sample_report["Expressions"].append(
                {"Mesh": obj.name, "Shape": block.name, "MaximumMovementMm": delta * 1000}
            )
    assert sample_report["Expressions"]
    rig = next(o for o in scene.objects if o.type == "ARMATURE")
    arm = next(b for b in rig.pose.bones if "J_ArmLow01_L" in b.name)
    body = next(o for o in meshes if "o_body" in o.name)
    before = geometry(body)
    original = arm.matrix_basis.copy()
    arm.rotation_mode = "XYZ"
    arm.rotation_euler.x += math.radians(20)
    after = geometry(body)
    arm.matrix_basis = original
    assert max((a - b).length for a, b in zip(before, after)) > 0.001
    sample_report["ArmDeformation"] = True
    for name in ("front", "face", "sideback"):
        folder = root / (f"final-{sample}" if name == "front" else f"review-{sample}-{name}") / "intermediate"
        if aligned and sample == 2:
            folder = root / f"aligned-2-{name}" / "intermediate"
        data = json.loads((folder / "scene.json").read_text(encoding="utf-8-sig"))
        view = data["View"]
        scene.camera.location = position(view["Position"])
        scene.camera.rotation_euler = (
            (position(view["Target"]) - scene.camera.location).to_track_quat("-Z", "Y").to_euler()
        )
        for light in data["Lights"]:
            obj = scene.objects.get(light["Name"])
            assert obj is not None
            obj.location = position(light["Position"])
            obj.rotation_euler = position(light["Direction"], 1).to_track_quat("-Z", "Y").to_euler()
        for node in scene.compositing_node_group.nodes:
            if node.type == "R_LAYERS":
                node.scene = scene
                node.layer = scene.view_layers[0].name
        scene.render.filepath = str(output / f"blender-{sample}-{name}.png")
        bpy.ops.render.render(write_still=True)
        sample_report["Views"].append(scene.render.filepath)
    report["Samples"].append(sample_report)
report["Passed"] = True
(output / "blender-scene-validation.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
