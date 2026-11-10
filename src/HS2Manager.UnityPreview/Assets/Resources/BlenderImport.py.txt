"""Internal Blender material builder. Invoked by the native .blend converter.
The source package is intermediate data, not a user-facing Python workflow.
"""
import json
import re
import sys
from pathlib import Path

import bpy


def package_path(folder, relative):
    path = (folder / relative).resolve()
    if not path.is_relative_to(folder):
        raise ValueError("导出包文件不能指向角色文件夹之外：" + str(path))
    if not path.is_file():
        raise ValueError("模型转换中间文件缺失：" + str(path))
    return path


def material_kind(record):
    if record.get("Kind"):
        return record["Kind"]
    # Compatibility with older exports which did not store the renderer kind.
    shader = record.get("SourceShader", "").lower()
    name = record.get("Name", "").lower()
    if "eyelash" in shader or "eyelash" in name:
        return "Eyelash"
    if "eyeshadow" in shader:
        return "EyeShadow"
    if "namida" in shader:
        return "Tearline"
    if "hair" in shader:
        return "Hair"
    if "eye" in shader and "brow" not in shader:
        return "Eye"
    if "skin" in shader and record.get("Role") in ("body", "head"):
        return "Skin"
    return "Surface"


def make_material(folder, record):
    kind = material_kind(record)
    material = bpy.data.materials.new(record["Id"] + " · " + record.get("Name", kind))
    material.use_nodes = True
    material["HS2_Id"] = record["Id"]
    material["HS2_Kind"] = kind
    material["HS2_SourceShader"] = record.get("SourceShader", "")
    material["HS2_Approximation"] = "PBR; skin scattering, eye coat and hair anisotropy are editable approximations"
    material.use_backface_culling = not record.get("DoubleSided", False)
    tree = material.node_tree
    tree.nodes.clear()
    nodes, links = tree.nodes, tree.links
    output = nodes.new("ShaderNodeOutputMaterial")
    output.location = (700, 100)
    shader = nodes.new("ShaderNodeBsdfPrincipled")
    shader.location = (380, 100)
    links.new(shader.outputs["BSDF"], output.inputs["Surface"])
    shader.inputs["IOR"].default_value = 1.45

    def texture(key, row, color=False):
        relative = record.get(key)
        if not relative:
            return None
        image = bpy.data.images.load(str(package_path(folder, relative)), check_existing=False)
        image.colorspace_settings.name = "sRGB" if color else "Non-Color"
        image.alpha_mode = "STRAIGHT"
        node = nodes.new("ShaderNodeTexImage")
        node.name = "HS2 " + key
        node.label = key + (" · sRGB" if color else " · Non-Color")
        node.image = image
        node.location = (-900, row)
        return node

    base = texture("BaseColor", 650, True)
    ao = texture("Occlusion", 350)
    if base:
        color = base.outputs["Color"]
        streak = texture("HairStreakMask", 1000)
        if streak:
            uv = nodes.new("ShaderNodeUVMap")
            uv.uv_map = "UV" + str(record.get("HairStreakUv", 1))
            uv.name = "HS2 Hair streak UV"
            links.new(uv.outputs["UV"], streak.inputs["Vector"])
            strength = nodes.new("ShaderNodeMath")
            strength.operation = "MULTIPLY"
            strength.inputs[1].default_value = record.get("HairStreakStrength", 1.0)
            links.new(streak.outputs["Color"], strength.inputs[0])
            blend = nodes.new("ShaderNodeMixRGB")
            blend.name = "HS2 Hair streak color"
            links.new(strength.outputs[0], blend.inputs[0])
            links.new(color, blend.inputs[1])
            blend.inputs[2].default_value = record.get("HairStreakColor", (1, 1, 1, 1))
            color = blend.outputs[0]
        if ao:
            multiply = nodes.new("ShaderNodeMixRGB")
            multiply.name = "HS2 AO influence"
            multiply.label = "AO influence (approximation, 0 = disabled)"
            multiply.blend_type = "MULTIPLY"
            multiply.inputs[0].default_value = 0.35
            multiply.location = (-240, 650)
            links.new(color, multiply.inputs[1])
            links.new(ao.outputs["Color"], multiply.inputs[2])
            color = multiply.outputs[0]
        links.new(color, shader.inputs["Base Color"])
        mode = record.get("AlphaMode", "Opaque")
        if mode != "Opaque":
            alpha = base.outputs["Alpha"]
            if mode == "Mask":
                clip = nodes.new("ShaderNodeMath")
                clip.operation = "GREATER_THAN"
                clip.name = "HS2 Alpha cutoff"
                clip.location = (-240, 400)
                clip.inputs[1].default_value = record.get("AlphaCutoff", 0.5)
                links.new(alpha, clip.inputs[0])
                alpha = clip.outputs[0]
            links.new(alpha, shader.inputs["Alpha"])
            if hasattr(material, "surface_render_method"):
                material.surface_render_method = "DITHERED"
            else:
                material.blend_method = "CLIP" if mode == "Mask" else "HASHED"
                material.alpha_threshold = record.get("AlphaCutoff", 0.5)
    normal = texture("Normal", 30)
    if normal:
        convert = nodes.new("ShaderNodeNormalMap")
        convert.name = "HS2 Tangent normal"
        convert.space = "TANGENT"
        convert.location = (-230, 80)
        links.new(normal.outputs["Color"], convert.inputs["Color"])
        links.new(convert.outputs["Normal"], shader.inputs["Normal"])
    for key, row in (("Metallic", -290), ("Roughness", -610)):
        node = texture(key, row)
        if node:
            links.new(node.outputs["Color"], shader.inputs[key])
    emission = texture("Emission", -930, True)
    if emission:
        links.new(emission.outputs["Color"], shader.inputs["Emission Color"])
        shader.inputs["Emission Strength"].default_value = record.get("EmissionStrength", 1.0)
    if kind == "Skin":
        shader.subsurface_method = "RANDOM_WALK_SKIN"
        shader.inputs["Subsurface Weight"].default_value = 0.08
        shader.inputs["Subsurface Radius"].default_value = (1.0, 0.45, 0.2)
        shader.inputs["Subsurface Scale"].default_value = 0.002
    elif kind == "Hair":
        shader.inputs["Anisotropic"].default_value = 0.45
        shader.inputs["Anisotropic Rotation"].default_value = 0.25
        tangent = nodes.new("ShaderNodeTangent")
        tangent.direction_type = "UV_MAP"
        tangent.location = (-230, -230)
        links.new(tangent.outputs["Tangent"], shader.inputs["Tangent"])
        if record.get("SourceShader") in ("Shader Forge/hair_07", "Shader Forge/hair_08"):
            facing = nodes.new("ShaderNodeLayerWeight")
            facing.name = "HS2 Hair viewing angle"
            facing.inputs["Blend"].default_value = 0.5
            facing.location = (-230, -500)
            if normal:
                links.new(convert.outputs["Normal"], facing.inputs["Normal"])

            frame = nodes.new("NodeFrame")
            frame.label = "Original hair: viewing angle controls gloss and metallic"
            frame.location = (-650, -1300)
            math_count = 0

            def math_node(operation, a, b, name):
                nonlocal math_count
                node = nodes.new("ShaderNodeMath")
                node.operation = operation
                node.label = node.name = name
                node.parent = frame
                node.location = ((math_count % 3) * 210, -(math_count // 3) * 180)
                math_count += 1
                for index, value in enumerate((a, b)):
                    if isinstance(value, (int, float)):
                        node.inputs[index].default_value = value
                    else:
                        links.new(value, node.inputs[index])
                return node

            angle = math_node("MULTIPLY", facing.outputs["Facing"], -2, "Hair grazing angle")
            extra = record.get("HairExtraGloss", 0.0)
            rough = nodes.get("HS2 Roughness")
            metal = nodes.get("HS2 Metallic")
            if rough:
                smooth = math_node("SUBTRACT", 1, rough.outputs["Color"], "Hair smoothness")
                smooth = math_node("MAXIMUM", smooth.outputs[0], extra * 0.8, "Hair extra gloss")
                factor = math_node("ADD", angle.outputs[0], 1.3, "Hair gloss angle")
                factor.use_clamp = True
                smooth = math_node("MULTIPLY", smooth.outputs[0], factor.outputs[0], "Hair angular smoothness")
                roughness = math_node("SUBTRACT", 1, smooth.outputs[0], "Hair angular roughness")
                links.new(roughness.outputs[0], shader.inputs["Roughness"])
            if metal:
                factor = math_node("ADD", angle.outputs[0], 1.9, "Hair metallic angle")
                factor.use_clamp = True
                metallic = math_node("MAXIMUM", metal.outputs["Color"], extra * 0.5, "Hair extra metallic")
                metallic = math_node("MULTIPLY", metallic.outputs[0], factor.outputs[0], "Hair angular metallic")
                links.new(metallic.outputs[0], shader.inputs["Metallic"])
    elif kind == "Eye":
        # The exported eyeball already contains the iris. A clear coat keeps it
        # visible; transmitting through the whole mesh would erase the iris.
        shader.inputs["Coat Weight"].default_value = 0.35
        shader.inputs["Coat Roughness"].default_value = 0.08
        shader.inputs["Coat IOR"].default_value = 1.38
    elif kind == "EyeShadow" and base:
        # The source is a multiplicative overlay. A tinted Transparent BSDF
        # attenuates the underlying eye instead of lighting a gray polygon.
        transparent = nodes.new("ShaderNodeBsdfTransparent")
        transparent.location = (380, 650)
        color = base.outputs["Color"]
        if not record.get("Kind"):
            falloff = nodes.new("ShaderNodeMath")
            falloff.operation = "POWER"
            falloff.inputs[1].default_value = 0.5
            links.new(base.outputs["Alpha"], falloff.inputs[0])
            mix = nodes.new("ShaderNodeMixRGB")
            mix.inputs[1].default_value = (1, 1, 1, 1)
            links.new(falloff.outputs[0], mix.inputs[0])
            links.new(color, mix.inputs[2])
            color = mix.outputs[0]
        links.new(color, transparent.inputs["Color"])
        links.new(transparent.outputs[0], output.inputs["Surface"])
    elif kind == "Tearline":
        shader.inputs["Transmission Weight"].default_value = 1.0
        shader.inputs["IOR"].default_value = 1.333
        shader.inputs["Coat Weight"].default_value = 0.2
    return material


def import_character(directory):
    if bpy.app.version < (4, 0, 0):
        raise RuntimeError("HS2 material import requires Blender 4.0 or newer")
    folder = Path(directory).resolve()
    fbx = package_path(folder, "character.fbx")
    records = json.loads(package_path(folder, "materials.json").read_text(encoding="utf-8-sig"))["Materials"]
    ids = [r["Id"] for r in records]
    if len(set(ids)) != len(ids):
        raise ValueError("Duplicate material IDs in materials.json")
    for record in records:
        for key in ("BaseColor", "Normal", "Metallic", "Roughness", "Occlusion", "Emission", "HairStreakMask"):
            if record.get(key):
                package_path(folder, record[key])
    before = set(bpy.data.objects)
    old_materials, old_images = set(bpy.data.materials), set(bpy.data.images)
    # Blender 5 uses the native importer. Blender 4 uses the bundled FBX add-on.
    try:
        bpy.ops.wm.fbx_import.get_rna_type()
        native_fbx = bpy.app.version >= (5, 0, 0)
    except (AttributeError, RuntimeError):
        native_fbx = False
    if native_fbx:
        bpy.ops.wm.fbx_import(filepath=str(fbx), use_anim=False)
    else:
        bpy.ops.import_scene.fbx(filepath=str(fbx), use_anim=False, use_custom_normals=True)
    imported = set(bpy.data.objects) - before
    geometry_path = folder / "model-result.json"
    geometry = {m["Node"]: m for m in json.loads(geometry_path.read_text(encoding="utf-8-sig"))["Geometry"]} if geometry_path.is_file() else {}
    imported_materials = set(bpy.data.materials) - old_materials
    collection = bpy.data.collections.new("HS2 · " + folder.name)
    bpy.context.scene.collection.children.link(collection)
    collection["HS2_Package"] = str(folder)
    materials = {}
    for obj in imported:
        for old_collection in list(obj.users_collection):
            old_collection.objects.unlink(obj)
        collection.objects.link(obj)
        if obj.type != "MESH":
            continue
        # Blender 5's native importer names shape keys after FBX shape geometry,
        # not blend-shape channels. Restore the documented editable channel names.
        source = geometry.get(obj.name)
        if source and obj.data.shape_keys:
            for block in list(obj.data.shape_keys.key_blocks)[1:]:
                match = re.fullmatch(r"Shape_\d+_(\d+)_\d+", block.name)
                if match:
                    block.name = source["BlendShapeNames"][int(match.group(1))]
                elif block.name.startswith("NeutralExpressions_"):
                    block.name = "HS2_ResetExpressions"
        for index, uv in enumerate(obj.data.uv_layers):
            uv.name = "UV" + str(index)
        for slot in obj.material_slots:
            name = slot.material.name if slot.material else ""
            ident = re.sub(r"\.\d{3,}$", "", name)
            if ident not in ids:
                raise ValueError("FBX material absent from materials.json: " + name)
            if ident not in materials:
                materials[ident] = make_material(folder, records[ids.index(ident)])
            record = records[ids.index(ident)]
            if record.get("HairStreakMask") and len(obj.data.uv_layers) <= record.get("HairStreakUv", 1):
                raise ValueError("挑染材质缺少所需 UV 通道：" + obj.name)
            slot.material = materials[ident]
    # Drop only unused data created by this FBX import. Keep existing user data.
    for material in imported_materials:
        if material.users == 0:
            bpy.data.materials.remove(material)
    for image in set(bpy.data.images) - old_images:
        if image.users == 0:
            bpy.data.images.remove(image)
    bpy.context.view_layer.update()
    print("HS2 imported:", folder, "objects:", len(imported), "materials:", len(materials))
    return collection


def package_directory():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if args:
        return Path(bpy.path.abspath(args[0])).resolve()
    # Run Script supplies a synthetic __file__ based on the text-block name,
    # even when Text.filepath contains the actual external Python file path.
    # Resolve // paths against the current .blend via Blender's path utility.
    text = getattr(bpy.context.space_data, "text", None)
    filename = text.filepath if text is not None else globals().get("__file__", "")
    if not filename:
        raise ValueError("无法确定角色文件夹。请在文本编辑器中选择“文本 → 打开”，"
                         "打开角色文件夹里的 Blender导入.py，再运行脚本。")
    return Path(bpy.path.abspath(filename)).resolve().parent


def main():
    import_character(package_directory())


if __name__ == "__main__":
    main()
