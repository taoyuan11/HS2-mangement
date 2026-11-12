"""在受管 Blender 进程中导出人物 UV，或将调用方制作的素材适配到目标骨架。"""

import json
import math
import pathlib
import runpy
import struct
import sys
import zlib

import bpy
from mathutils import Vector


def import_model(path):
    path = pathlib.Path(path)
    if path.suffix.lower() == ".fbx":
        bpy.ops.import_scene.fbx(filepath=str(path))
    elif path.suffix.lower() in (".glb", ".gltf"):
        bpy.ops.import_scene.gltf(filepath=str(path))
    elif path.suffix.lower() == ".obj":
        bpy.ops.wm.obj_import(filepath=str(path))
    else:
        raise ValueError("素材模型必须为 FBX、GLB/GLTF 或 OBJ")


def uv_png(obj, path):
    # 直接栅格化 UV 边，避免依赖可选的 Blender UV 导出扩展。
    size = 1024
    pixels = bytearray(size * size * 4)
    if not obj.data.uv_layers.active:
        return False
    uvs = obj.data.uv_layers.active.data
    for polygon in obj.data.polygons:
        indices = list(polygon.loop_indices)
        for i, a in enumerate(indices):
            b = indices[(i + 1) % len(indices)]
            x0, y0 = (uvs[a].uv.x * (size - 1), (1 - uvs[a].uv.y) * (size - 1))
            x1, y1 = (uvs[b].uv.x * (size - 1), (1 - uvs[b].uv.y) * (size - 1))
            steps = min(size * 4, max(1, int(max(abs(x1 - x0), abs(y1 - y0)))))
            for j in range(steps + 1):
                x, y = round(x0 + (x1 - x0) * j / steps), round(y0 + (y1 - y0) * j / steps)
                if 0 <= x < size and 0 <= y < size:
                    offset = (y * size + x) * 4
                    pixels[offset : offset + 4] = b"\xff\xff\xff\xff"

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    scanlines = b"".join(b"\x00" + pixels[y * size * 4 : (y + 1) * size * 4] for y in range(size))
    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(scanlines))
        + chunk(b"IEND", b"")
    )
    return True


def main(spec):
    work = pathlib.Path(spec["work_directory"])
    work.mkdir(parents=True, exist_ok=True)
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    import_model(spec["reference_model"])
    reference_objects = set(bpy.data.objects)
    bodies = [obj for obj in reference_objects if obj.type == "MESH"]
    armatures = [obj for obj in reference_objects if obj.type == "ARMATURE"]
    if not bodies or not armatures:
        raise ValueError("人物参考缺少身体网格或骨架")
    armature = max(armatures, key=lambda obj: len(obj.data.bones))
    # FBX 导入的厘米比例和坐标轴旋转烘焙到骨架数据，避免新网格绑定后继承一遍 .01 缩放。
    bpy.ops.object.select_all(action="DESELECT")
    armature.select_set(True)
    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    if spec["mode"] == "reference":
        images = []
        for index, obj in enumerate(bodies):
            path = work / f"uv-{index:03}.png"
            if uv_png(obj, path):
                images.append({"path": str(path), "mime_type": "image/png", "mesh": obj.name})
        bpy.ops.wm.save_as_mainfile(filepath=str(work / "reference.blend"))
        return {
            "images": images,
            "blend": str(work / "reference.blend"),
            "bones": [bone.name for bone in armature.data.bones],
            "bounds": {obj.name: [list(point) for point in obj.bound_box] for obj in bodies},
        }

    if spec.get("source_model"):
        import_model(spec["source_model"])
    if spec.get("source_script"):
        runpy.run_path(
            spec["source_script"],
            init_globals={"HS2_SPEC": spec, "HS2_REFERENCE_BODIES": bodies, "HS2_REFERENCE_ARMATURE": armature},
        )
    meshes = [obj for obj in bpy.data.objects if obj not in reference_objects and obj.type == "MESH"]
    if not meshes:
        raise ValueError("调用方必须提供 source_model 或创建新网格的 source_script")
    vertices = 0
    triangles = 0
    bounds = {}
    head = next(
        (bone.name for bone in armature.data.bones if bone.name in ("cf_J_Head", "cm_J_Head", "cf_J_Head_s")), None
    )
    for obj in meshes:
        corners = [obj.matrix_world @ Vector(corner) for corner in obj.bound_box]
        bounds[obj.name] = {
            "minimum": [min(point[axis] for point in corners) for axis in range(3)],
            "maximum": [max(point[axis] for point in corners) for axis in range(3)],
        }
        if not obj.data.uv_layers.active:
            raise ValueError(f"网格 {obj.name} 缺少 UV")
        if any(not all(math.isfinite(value) for value in vertex.co) for vertex in obj.data.vertices):
            raise ValueError("网格坐标不是有限数值")
        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
        if spec["kind"] in ("hair", "clothes"):
            if not obj.vertex_groups:
                if spec["kind"] == "hair":
                    if not head:
                        raise ValueError("参考骨架缺少头部骨骼")
                    obj.vertex_groups.new(name=head).add(list(range(len(obj.data.vertices))), 1.0, "REPLACE")
                else:
                    source = max(bodies, key=lambda body: len(body.data.vertices))
                    transfer = obj.modifiers.new("HS2WeightTransfer", "DATA_TRANSFER")
                    transfer.object = source
                    transfer.use_vert_data = True
                    transfer.data_types_verts = {"VGROUP_WEIGHTS"}
                    transfer.vert_mapping = "POLYINTERP_NEAREST"
                    bpy.ops.object.datalayout_transfer(modifier=transfer.name)
                    bpy.ops.object.modifier_apply(modifier=transfer.name)
            bpy.ops.object.vertex_group_limit_total(limit=4)
            bpy.ops.object.vertex_group_normalize_all(lock_active=False)
            allowed = set(armature.data.bones.keys())
            for vertex in obj.data.vertices:
                groups = [group for group in vertex.groups if group.weight > 0.00001]
                if not groups or abs(sum(group.weight for group in groups) - 1) > 0.01:
                    raise ValueError("网格存在未绑定或未归一化顶点")
                if any(obj.vertex_groups[group.group].name not in allowed for group in groups):
                    raise ValueError("顶点引用不存在的目标骨骼")
            for modifier in list(obj.modifiers):
                if modifier.type == "ARMATURE":
                    obj.modifiers.remove(modifier)
            obj.modifiers.new("HS2Armature", "ARMATURE").object = armature
            world = obj.matrix_world.copy()
            obj.parent = armature
            obj.matrix_world = world
        obj.data.calc_loop_triangles()
        vertices += len(obj.data.vertices)
        triangles += len(obj.data.loop_triangles)
    if not vertices or not triangles or triangles > spec.get("max_triangles", 250000):
        raise ValueError("素材为空或超过面数预算")
    bpy.ops.object.select_all(action="DESELECT")
    for obj in meshes:
        obj.select_set(True)
    if spec["kind"] != "accessory":
        armature.select_set(True)
    bpy.ops.export_scene.fbx(
        filepath=str(work / "asset.fbx"),
        use_selection=True,
        object_types={"MESH", "ARMATURE"},
        add_leaf_bones=False,
        bake_anim=False,
        path_mode="COPY",
        embed_textures=True,
    )
    textures = []
    for texture in spec.get("textures", []):
        path = pathlib.Path(texture["path"])
        if not path.is_file():
            raise ValueError(f"缺少贴图：{path}")
        textures.append(str(path.resolve()))
    bpy.ops.wm.save_as_mainfile(filepath=str(work / "asset.blend"))
    return {"valid": True, "vertices": vertices, "triangles": triangles, "textures": textures, "bounds": bounds}


if __name__ == "__main__":
    spec = json.loads(pathlib.Path(sys.argv[sys.argv.index("--") + 1]).read_text(encoding="utf-8-sig"))
    result = main(spec)
    (pathlib.Path(spec["work_directory"]) / "result.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8"
    )
