"""独立重新导入素材 FBX，检查发布几何、UV、骨架和权重是否完整。"""

import json
import math
import pathlib
import sys

import bpy
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1 :]
work = pathlib.Path(args[0])
kind = args[1]
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.fbx(filepath=str(work / "asset.fbx"))
meshes = [obj for obj in bpy.data.objects if obj.type == "MESH"]
armatures = [obj for obj in bpy.data.objects if obj.type == "ARMATURE"]
assert len(meshes) == 1, "FBX 不应包含参考人物的其它网格"
assert bool(armatures) == (kind != "accessory"), "FBX 骨架范围不正确"
vertices = 0
expected = json.loads((work / "result.json").read_text(encoding="utf-8")).get("bounds", {})
for mesh in meshes:
    if mesh.name in expected:
        corners = [mesh.matrix_world @ Vector(corner) for corner in mesh.bound_box]
        for axis in range(3):
            assert (
                abs(min(point[axis] for point in corners) - expected[mesh.name]["minimum"][axis]) < 0.001
            ), "FBX 回读改变了网格世界位置或尺寸"
            assert (
                abs(max(point[axis] for point in corners) - expected[mesh.name]["maximum"][axis]) < 0.001
            ), "FBX 回读改变了网格世界位置或尺寸"
    assert mesh.data.uv_layers.active and len(mesh.data.uv_layers.active.data), "回读后缺少 UV"
    for vertex in mesh.data.vertices:
        assert all(math.isfinite(value) for value in vertex.co), "回读后存在非有限坐标"
        if kind != "accessory":
            bones = set(armatures[0].data.bones.keys())
            weights = [group for group in vertex.groups if group.weight > 0.00001]
            assert 1 <= len(weights) <= 4 and abs(sum(group.weight for group in weights) - 1) < 0.01
            assert all(mesh.vertex_groups[group.group].name in bones for group in weights)
        vertices += 1
(work / "roundtrip.json").write_text(
    json.dumps({"valid": True, "meshes": len(meshes), "vertices": vertices, "armatures": len(armatures)}),
    encoding="utf-8",
)
