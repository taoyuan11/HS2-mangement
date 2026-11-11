"""在 UE 自身执行 FBX 往返后，验证原生资源的几何、权重与形变。
先运行 Test-NativeUnrealScene.py，再于 Blender 中以 -- ROOT REVIEW_DIRECTORY 运行本脚本。
往返文件仅作为测试证据，不包含在原生交付包中。"""

import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector
from mathutils.kdtree import KDTree

root, review = [Path(p) for p in sys.argv[sys.argv.index("--") + 1 :]]
report = {"Passed": False, "Samples": []}


def vertices(obj):
    bpy.context.view_layer.update()
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = evaluated.to_mesh()
    try:
        return [obj.matrix_world @ v.co for v in mesh.vertices]
    finally:
        evaluated.to_mesh_clear()


for sample in range(3):
    source = root / ("production-unreal-0" if sample == 0 else f"delivery-unreal-{sample}") / "intermediate"
    expected = json.loads((source / "model-result.json").read_text(encoding="utf-8-sig"))
    manifest = json.loads(
        (root / f"delivery-unreal-{sample}" / "Unreal/native-assets.json").read_text(encoding="utf-8")
    )
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=str(review / f"unreal-{sample}-roundtrip.fbx"))
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    points = [v for obj in meshes for v in vertices(obj)]
    tree = KDTree(len(points))
    for i, point in enumerate(points):
        tree.insert(point, i)
    tree.balance()
    error = 0
    for record in expected["Geometry"]:
        positions = record["ReferencePositions"]
        for i in range(0, len(positions), 3):
            point = Vector((-positions[i], -positions[i + 2], positions[i + 1]))
            error = max(error, tree.find(point)[2])
    assert error <= 0.002, f"Native UE reference error {error * 1000} mm"
    shapes = {b.name for obj in meshes if obj.data.shape_keys for b in list(obj.data.shape_keys.key_blocks)[1:]}
    assert shapes == set(manifest["MorphTargets"]), "Native morph roundtrip mismatch"
    sample_report = {
        "Sample": sample,
        "MaximumReferenceErrorMm": error * 1000,
        "MorphTargets": len(shapes),
        "Expressions": [],
        "ArmDeformation": False,
    }
    for obj in meshes:
        if not obj.data.shape_keys:
            continue
        for name in ("head_e01_close", "head_k01_warai1_op"):
            block = obj.data.shape_keys.key_blocks.get(name)
            assert block is not None, name
            before = vertices(obj)
            block.value = 1
            after = vertices(obj)
            block.value = 0
            distance = max((a - b).length for a, b in zip(before, after))
            assert distance > 0.00001, name
            sample_report["Expressions"].append({"Shape": name, "MaximumMovementMm": distance * 1000})
    rig = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
    arm = next(b for b in rig.pose.bones if "J_ArmLow01_L" in b.name)
    before = [v for obj in meshes for v in vertices(obj)]
    arm.rotation_mode = "XYZ"
    arm.rotation_euler.x += math.radians(20)
    after = [v for obj in meshes for v in vertices(obj)]
    distance = max((a - b).length for a, b in zip(before, after))
    assert distance > 0.001, "Native UE skin weights lost"
    sample_report["ArmDeformation"] = True
    sample_report["ArmMovementMm"] = distance * 1000
    report["Samples"].append(sample_report)
report["Passed"] = True
(review / "unreal-geometry.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
