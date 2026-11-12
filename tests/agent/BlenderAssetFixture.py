"""在真实人物参考上创建三个小型测试素材，用于检查导出、UV 与自动蒙皮。"""

import bpy
import bmesh
from mathutils import Vector

kind = HS2_SPEC["kind"]
if kind == "clothes":
    source = next(body for body in HS2_REFERENCE_BODIES if "o_body" in body.name)
    mesh = bpy.data.objects.new("agent_test_clothing", source.data.copy())
    bpy.context.collection.objects.link(mesh)
    mesh.matrix_world = source.matrix_world.copy()
    points = [source.matrix_world @ Vector(corner) for corner in source.bound_box]
    bottom, top = min(point.z for point in points), max(point.z for point in points)
    height = top - bottom
    edit = bmesh.new()
    edit.from_mesh(mesh.data)
    # 简单宽松上衣，保留胸腹和袖口；不把整个人体外壳当成服装样本。
    removed = [
        vertex
        for vertex in edit.verts
        if (mesh.matrix_world @ vertex.co).z < bottom + height * 0.46
        or abs((mesh.matrix_world @ vertex.co).x) > height * 0.38
    ]
    bmesh.ops.delete(edit, geom=removed, context="VERTS")
    edit.to_mesh(mesh.data)
    edit.free()
    inverse = mesh.matrix_world.inverted()
    for vertex in mesh.data.vertices:
        position = mesh.matrix_world @ vertex.co
        radial = Vector((position.x, position.y, 0))
        if radial.length > 0.00001:
            vertex.co = inverse @ (position + radial.normalized() * 0.08)
else:
    head = HS2_REFERENCE_ARMATURE.data.bones["cf_J_Head"]
    body = next(body for body in HS2_REFERENCE_BODIES if "o_body" in body.name)
    points = [body.matrix_world @ Vector(corner) for corner in body.bound_box]
    height = max(point.z for point in points) - min(point.z for point in points)
    if height < 0.1:
        height = max(point.y for point in points) - min(point.y for point in points)
    if kind == "accessory":
        # 配饰导出为挂点局部空间，不能再带一次世界头部平移。
        position = Vector((height * 0.1, 0, height * 0.12))
    else:
        position = HS2_REFERENCE_ARMATURE.matrix_world @ head.head_local
        position.z += height * 0.12
    bpy.ops.mesh.primitive_uv_sphere_add(segments=12, ring_count=8, radius=height * 0.05, location=position)
    bpy.context.object.name = "agent_test_" + kind
