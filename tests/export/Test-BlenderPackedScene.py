"""重新打开打包场景并验证 Eevee 脸部渲染；参数为 -- 场景文件 材质文件 输出目录。"""

import json
import sys
from pathlib import Path

import bpy

blend, manifest, output = [Path(p).resolve() for p in sys.argv[sys.argv.index("--") + 1 :]]
output.mkdir(parents=True, exist_ok=True)
bpy.ops.wm.open_mainfile(filepath=str(blend))
images = {
    node.image
    for mat in bpy.data.materials
    if mat.node_tree
    for node in mat.node_tree.nodes
    if node.type == "TEX_IMAGE" and node.image
}
assert images and all(image.packed_file and image.size[0] > 0 for image in images)
roles = {r["Id"]: r["Role"] for r in json.loads(manifest.read_text(encoding="utf-8-sig"))["Materials"]}
for obj in bpy.context.scene.objects:
    if obj.type == "MESH" and obj.material_slots:
        ids = [s.material.get("HS2_Id", "") for s in obj.material_slots if s.material]
        if ids and all(roles.get(i) == "accessory" for i in ids):
            obj.hide_render = True
scene = bpy.context.scene
for engine in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
    try:
        scene.render.engine = engine
        break
    except TypeError:
        pass
else:
    raise RuntimeError("Eevee engine is not available")
scene.render.filepath = str(output / "face-eevee.png")
bpy.ops.render.render(write_still=True)
report = {
    "Passed": True,
    "Blender": bpy.app.version_string,
    "Engine": scene.render.engine,
    "PackedImagesAfterReopen": len(images),
    "FaceInspectionHidesAccessories": True,
}
(output / "packed-scene-validation.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print("PACKED SCENE CHECKS PASSED", json.dumps(report))
