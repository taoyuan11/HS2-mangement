"""Internal native exporter. Request/result files belong to one owned process run."""
import json
import math
import sys
import traceback
from pathlib import Path

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import bpy
from mathutils import Vector
import BlenderImport as importer


def position(v, scale=0.1):
    return Vector((-v[0] * scale, -v[2] * scale, v[1] * scale))


def image(folder, relative, color=False):
    result = bpy.data.images.load(str(importer.package_path(folder, relative)), check_existing=True)
    result.colorspace_settings.name = "sRGB" if color else "Non-Color"
    return result


def setup_world(scene, data, folder):
    world = bpy.data.worlds.new("HS2 Preview Environment")
    scene.world = world
    world.use_nodes = True
    nodes, links = world.node_tree.nodes, world.node_tree.links
    nodes.clear()
    output = nodes.new("ShaderNodeOutputWorld")
    output.location = (900, 0)
    coordinates = nodes.new("ShaderNodeTexCoord")
    mapping = nodes.new("ShaderNodeMapping")
    mapping.inputs["Rotation"].default_value[2] = math.pi / 2
    links.new(coordinates.outputs["Generated"], mapping.inputs["Vector"])

    def background(name, strength, color, texture=""):
        node = nodes.new("ShaderNodeBackground")
        node.name = node.label = name
        node.inputs["Color"].default_value = tuple(color)
        node.inputs["Strength"].default_value = strength
        if texture:
            env = nodes.new("ShaderNodeTexEnvironment")
            env.image = image(folder, texture)
            links.new(mapping.outputs["Vector"], env.inputs["Vector"])
            links.new(env.outputs["Color"], node.inputs["Color"])
        return node

    environment = background("HS2 ambient illumination", data["AmbientIntensity"], data["AmbientSky"], data["EnvironmentTexture"])
    if not data["EnvironmentTexture"] and data["AmbientMode"] == 1:
        separate = nodes.new("ShaderNodeSeparateXYZ")
        links.new(coordinates.outputs["Normal"], separate.inputs[0])
        remap = nodes.new("ShaderNodeMath")
        remap.operation = "MULTIPLY_ADD"
        links.new(separate.outputs["Z"], remap.inputs[0])
        remap.inputs[1].default_value = 0.5
        remap.inputs[2].default_value = 0.5
        ramp = nodes.new("ShaderNodeValToRGB")
        ramp.color_ramp.elements[0].color = data["AmbientGround"]
        ramp.color_ramp.elements[1].color = data["AmbientSky"]
        ramp.color_ramp.elements.new(0.5).color = data["AmbientEquator"]
        links.new(remap.outputs[0], ramp.inputs[0])
        links.new(ramp.outputs[0], environment.inputs["Color"])
    reflection = background("HS2 reflections", data["ReflectionIntensity"], data["AmbientSky"], data["ReflectionTexture"] or data["EnvironmentTexture"])
    backdrop = background("HS2 visible background", 1, data["Background"], data["EnvironmentTexture"] if data["SkyBackground"] else "")
    rays = nodes.new("ShaderNodeLightPath")
    gloss = nodes.new("ShaderNodeMixShader")
    links.new(rays.outputs["Is Glossy Ray"], gloss.inputs[0])
    links.new(environment.outputs[0], gloss.inputs[1])
    links.new(reflection.outputs[0], gloss.inputs[2])
    camera = nodes.new("ShaderNodeMixShader")
    links.new(rays.outputs["Is Camera Ray"], camera.inputs[0])
    links.new(gloss.outputs[0], camera.inputs[1])
    links.new(backdrop.outputs[0], camera.inputs[2])
    links.new(camera.outputs[0], output.inputs[0])


def compositor(scene):
    if hasattr(scene, "compositing_node_group"):
        tree = bpy.data.node_groups.new("HS2 Preview Color", "CompositorNodeTree")
        tree.interface.new_socket(name="Image", in_out="OUTPUT", socket_type="NodeSocketColor")
        scene.compositing_node_group = tree
        output = tree.nodes.new("NodeGroupOutput")
    else:
        scene.use_nodes = True
        tree = scene.node_tree
        tree.nodes.clear()
        output = tree.nodes.new("CompositorNodeComposite")
    return tree, output


def setup_compositor(scene, data, folder):
    tree, output = compositor(scene)
    nodes, links = tree.nodes, tree.links
    render = nodes.new("CompositorNodeRLayers")
    render.scene = scene
    render.layer = scene.view_layers[0].name
    color = render.outputs["Image"]
    post = data["Post"]

    def connect(value, socket):
        if isinstance(value, (float, int)):
            socket.default_value = value
        else:
            links.new(value, socket)

    def math_node(operation, a, b=0):
        kind = "ShaderNodeMath" if bpy.app.version >= (5, 0, 0) else "CompositorNodeMath"
        node = nodes.new(kind)
        node.operation = operation
        connect(a, node.inputs[0])
        connect(b, node.inputs[1])
        return node.outputs[0]

    def separate(socket):
        node = nodes.new("CompositorNodeSeparateColor")
        node.mode = "RGB"
        links.new(socket, node.inputs[0])
        return node.outputs

    def combine(r, g, b):
        node = nodes.new("CompositorNodeCombineColor")
        node.mode = "RGB"
        for source, target in zip((r, g, b, 1), node.inputs):
            connect(source, target)
        return node.outputs[0]

    def srgb(value, inverse=False):
        # Match Unity's full precision piecewise sRGB conversion.
        if inverse:
            low = math_node("DIVIDE", value, 12.92)
            high = math_node("POWER", math_node("DIVIDE", math_node("ADD", value, 0.055), 1.055), 2.4)
            select = math_node("GREATER_THAN", value, 0.04045)
        else:
            low = math_node("MULTIPLY", value, 12.92)
            high = math_node("SUBTRACT", math_node("MULTIPLY", math_node("POWER", math_node("MAXIMUM", value, 0), 1 / 2.4), 1.055), 0.055)
            select = math_node("GREATER_THAN", value, 0.0031308)
        return math_node("ADD", low, math_node("MULTIPLY", select, math_node("SUBTRACT", high, low)))

    if post["Bloom"] and post["BloomIntensity"] > 0:
        glare = nodes.new("CompositorNodeGlare")
        glare.name = "HS2 Bloom approximation"
        if hasattr(glare, "glare_type"):
            glare.glare_type = "FOG_GLOW"
            glare.threshold = post["BloomThreshold"]
            glare.quality = "HIGH"
        else:
            glare.inputs["Type"].default_value = "Fog Glow"
            glare.inputs["Threshold"].default_value = post["BloomThreshold"]
            glare.inputs["Strength"].default_value = 2 ** (post["BloomIntensity"] / 10) - 1
        links.new(color, glare.inputs["Image"])
        color = glare.outputs["Image"]
    if data["LutTexture"]:
        # Map the evaluated Unity LUT directly, including tone mapping and blended profiles.
        channels = separate(color)
        xyz = []
        for channel in channels[:3]:
            if data["LutEncoding"] == "LogC":
                channel = math_node("MULTIPLY", channel, 2 ** post["Exposure"])
                channel = math_node("MAXIMUM", channel, 0)
                channel = math_node("ADD", math_node("MULTIPLY", channel, 5.555556), 0.047996)
                channel = math_node("ADD", math_node("MULTIPLY", math_node("LOGARITHM", channel, 10), 0.244161), 0.386036)
            else:
                channel = srgb(channel)
            xyz.append(math_node("MINIMUM", math_node("MAXIMUM", channel, 0), 1))
        size = data["LutSize"]
        blue = math_node("MULTIPLY", xyz[2], size - 1)
        lower = math_node("FLOOR", blue)
        upper = math_node("MINIMUM", math_node("ADD", lower, 1), size - 1)
        fraction = math_node("SUBTRACT", blue, lower)
        lut = nodes.new("CompositorNodeImage")
        lut.name = "HS2 evaluated preview LUT"
        lut.image = image(folder, data["LutTexture"])
        sampled = []
        for shift in (lower, upper):
            x = math_node("DIVIDE", math_node("ADD", math_node("ADD", math_node("MULTIPLY", shift, size), math_node("MULTIPLY", xyz[0], size - 1)), 0.5), size * size)
            y = math_node("DIVIDE", math_node("ADD", math_node("MULTIPLY", xyz[1], size - 1), 0.5), size)
            uv = combine(x, y, 1)
            lookup = nodes.new("CompositorNodeMapUV")
            lookup.name = "HS2 LUT slice"
            if "Interpolation" in lookup.inputs:
                lookup.inputs["Interpolation"].default_value = "Bilinear"
            links.new(lut.outputs["Image"], lookup.inputs["Image"])
            links.new(uv, lookup.inputs["UV"])
            sampled.append(separate(lookup.outputs["Image"]))
        result = []
        for low, high in zip(sampled[0][:3], sampled[1][:3]):
            value = math_node("ADD", low, math_node("MULTIPLY", fraction, math_node("SUBTRACT", high, low)))
            result.append(srgb(value, True) if data["LutEncoding"] == "sRGB" else value)
        color = combine(*result)
    links.new(color, output.inputs["Image"])
    for i, node in enumerate(nodes):
        node.location = ((i % 10) * 200, -(i // 10) * 200)
    # Source LUT already includes the source tone curve; Standard applies only display sRGB.
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    scene.view_settings.exposure = 0
    scene.view_settings.gamma = 1


def setup_scene(data, folder):
    scene = bpy.context.scene
    for engine in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
        try:
            scene.render.engine = engine
            break
        except TypeError:
            pass
    else:
        raise RuntimeError("此 Blender 不支持 Eevee 渲染。")
    view = data["View"]
    scene.render.resolution_x, scene.render.resolution_y = view["Width"], view["Height"]
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.film_transparent = False
    scene.render.fps = 30
    scene.frame_set(1)
    collection = bpy.data.collections.new("HS2 Preview Scene")
    scene.collection.children.link(collection)
    camera_data = bpy.data.cameras.new("HS2 Preview Camera")
    camera = bpy.data.objects.new(camera_data.name, camera_data)
    collection.objects.link(camera)
    camera.location = position(view["Position"])
    camera.rotation_euler = (position(view["Target"]) - camera.location).to_track_quat("-Z", "Y").to_euler()
    camera_data.type = "PERSP"
    camera_data.sensor_fit = "VERTICAL"
    camera_data.sensor_height = 24
    camera_data.lens = 12 / math.tan(math.radians(view["FieldOfView"]) / 2)
    camera_data.clip_start = max(0.001, data["NearClip"] * 0.1)
    camera_data.clip_end = data["FarClip"] * 0.1
    scene.camera = camera
    for source in data["Lights"]:
        kind = {0: "SPOT", 1: "SUN", 2: "POINT", 3: "AREA"}.get(source["Type"], "SUN")
        light_data = bpy.data.lights.new(source["Name"], kind)
        light_data.color = source["Color"][:3]
        light_data.energy = source["Intensity"] * math.pi
        if kind != "SUN":
            light_data.energy *= max(1, source["Range"] * 0.1) ** 2
        light_data.use_shadow = source["Shadows"]
        if kind == "SPOT":
            light_data.spot_size = math.radians(source["SpotAngle"])
        light = bpy.data.objects.new(source["Name"], light_data)
        collection.objects.link(light)
        light.location = position(source["Position"])
        light.rotation_euler = position(source["Direction"], 1).to_track_quat("-Z", "Y").to_euler()
    if hasattr(scene.eevee, "use_gtao"):
        scene.eevee.use_gtao = data["Post"]["AmbientOcclusion"]
        scene.eevee.gtao_distance = data["Post"]["AoRadius"] * 0.1
        scene.eevee.gtao_factor = data["Post"]["AoIntensity"]
    setup_world(scene, data, folder)
    # Eevee needs an irradiance volume for diffuse illumination from the world.
    # Bake only the environment: direct lights remain evaluated dynamically.
    if bpy.app.version >= (4, 2, 0):
        probe_data = bpy.data.lightprobes.new("HS2 Environment Irradiance", "VOLUME")
        probe_data.resolution_x = probe_data.resolution_y = probe_data.resolution_z = 3
        probe_data.capture_world = True
        probe_data.capture_indirect = False
        probe_data.capture_emission = False
        probe = bpy.data.objects.new(probe_data.name, probe_data)
        collection.objects.link(probe)
        corners = [o.matrix_world @ Vector(v) for o in scene.objects if o.type == "MESH" for v in o.bound_box]
        low = Vector(tuple(min(v[i] for v in corners) for i in range(3)))
        high = Vector(tuple(max(v[i] for v in corners) for i in range(3)))
        probe.location = (low + high) / 2
        probe.scale = (high - low) / 2 + Vector((0.25, 0.25, 0.25))
        world_tree = scene.world.node_tree
        world_output = next(n for n in world_tree.nodes if n.type == "OUTPUT_WORLD")
        world_surface = world_output.inputs["Surface"].links[0].from_socket
        world_tree.links.new(world_tree.nodes["HS2 ambient illumination"].outputs[0], world_output.inputs["Surface"])
        try:
            bpy.context.view_layer.update()
            bpy.ops.object.lightprobe_cache_bake(subset="ALL")
        finally:
            world_tree.links.new(world_surface, world_output.inputs["Surface"])
            bpy.context.view_layer.update()
    setup_compositor(scene, data, folder)
    scene["HS2_ExportScene"] = json.dumps(data)
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type == "VIEW_3D":
                area.spaces.active.region_3d.view_perspective = "CAMERA"
                area.spaces.active.shading.type = "RENDERED"
                area.spaces.active.shading.use_scene_world_render = True
                area.spaces.active.shading.use_scene_lights_render = True


def check_scene(expected):
    scene = bpy.context.scene
    meshes = [o for o in scene.objects if o.type == "MESH"]
    rigs = [o for o in scene.objects if o.type == "ARMATURE"]
    materials = {slot.material.get("HS2_Id") for o in meshes for slot in o.material_slots if slot.material}
    if not meshes or not rigs or materials != set(expected):
        raise RuntimeError("原生场景的网格、骨架或材质槽不完整。")
    images = [i for i in bpy.data.images if i.source == "FILE" and i.users > 0]
    if not images or any(not i.packed_file or i.size[0] == 0 for i in images):
        raise RuntimeError("原生场景存在未打包或无法读取的贴图。")
    if scene.camera is None or "HS2_ExportScene" not in scene:
        raise RuntimeError("原生场景缺少预览相机或场景快照。")
    data = json.loads(scene["HS2_ExportScene"])
    expected_geometry = json.loads(scene["HS2_ExportGeometry"])
    from mathutils.kdtree import KDTree
    max_error = 0.0
    shapes = 0
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for source in expected_geometry:
        obj = next((o for o in meshes if o.name == source["Node"]), None)
        if obj is None:
            raise RuntimeError("原生场景缺少网格：" + source["Node"])
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh()
        try:
            tree = KDTree(len(mesh.vertices))
            for vertex in mesh.vertices:
                tree.insert(obj.matrix_world @ vertex.co, vertex.index)
            tree.balance()
            reference = source["ReferencePositions"]
            for i in range(0, len(reference), 3):
                # Diagnostic samples are Unity's left-handed Y-up meters.
                p = Vector((-reference[i], -reference[i + 2], reference[i + 1]))
                max_error = max(max_error, tree.find(p)[2])
        finally:
            evaluated.to_mesh_clear()
        blocks = obj.data.shape_keys.key_blocks if obj.data.shape_keys else []
        shapes += max(0, len(blocks) - 1)
        if source["BlendShapeNames"] and len(blocks) - 1 < len(source["BlendShapeNames"]):
            raise RuntimeError("形变通道丢失：" + obj.name)
        if any(name not in blocks for name in source["BlendShapeNames"]):
            raise RuntimeError("形变通道名称不匹配：" + obj.name)
        if any(abs(block.value) > 1e-6 for block in list(blocks)[1:]):
            raise RuntimeError("默认表情被重复叠加：" + obj.name)
    if max_error > .002:
        raise RuntimeError("原生参考形态偏差超过 2 毫米：" + str(max_error * 1000))
    warnings = list(data["Warnings"])
    if data["Post"]["Vignette"]:
        warnings.append("Blender 预览未迁移源暗角；调色 LUT 已应用。")
    if data["Post"]["AmbientOcclusion"] and not hasattr(scene.eevee, "use_gtao"):
        warnings.append("Blender Eevee 使用自身遮蔽实现，源屏幕空间 AO 半径与强度未逐项等价迁移。")
    return {"Passed": True, "ToolVersion": bpy.app.version_string, "Meshes": len(meshes),
            "Bones": sum(len(o.data.bones) for o in rigs), "Materials": len(materials),
            "PackedImages": len(images), "BlendShapes": shapes, "MaximumReferenceErrorMm": max_error * 1000,
            "Warnings": warnings, "Reopened": True}


def main(request):
    if bpy.app.version < (4, 0, 0):
        raise RuntimeError("原生模型导出需要 Blender 4.0 或更新版本。")
    if request["Mode"] == "probe":
        if not hasattr(bpy.ops.wm, "save_as_mainfile") or not hasattr(bpy.ops.file, "pack_all"):
            raise RuntimeError("此 Blender 缺少原生保存或资源打包接口。")
        return {"Passed": True, "ToolVersion": bpy.app.version_string}
    output = Path(request["Output"])
    if request["Mode"] == "convert":
        folder = Path(request["Source"]).resolve()
        bpy.ops.wm.read_factory_settings(use_empty=True)
        collection = importer.import_character(folder)
        collection.name = "HS2 Character"
        if "HS2_Package" in collection:
            del collection["HS2_Package"]
        data = json.loads(importer.package_path(folder, "scene.json").read_text(encoding="utf-8-sig"))
        setup_scene(data, folder)
        bpy.context.scene["HS2_ExportGeometry"] = json.dumps(json.loads(importer.package_path(folder, "model-result.json").read_text(encoding="utf-8-sig"))["Geometry"])
        bpy.ops.file.pack_all()
        for img in bpy.data.images:
            if img.source == "FILE" and img.packed_file:
                img.filepath = "//Packed/" + Path(img.filepath).name
        report = check_scene(request["MaterialIds"])
        report["Reopened"] = False
        bpy.ops.wm.save_as_mainfile(filepath=str(output / "character.blend"), compress=True)
        return report
    if request["Mode"] == "verify":
        bpy.ops.wm.open_mainfile(filepath=str(output / "character.blend"))
        tree = bpy.context.scene.compositing_node_group if hasattr(bpy.context.scene, "compositing_node_group") else bpy.context.scene.node_tree
        for node in tree.nodes:
            if node.type == "R_LAYERS":
                node.scene = bpy.context.scene
                node.layer = bpy.context.scene.view_layers[0].name
        report = check_scene(request["MaterialIds"])
        bpy.context.scene.render.filepath = str(output / "预览.png")
        bpy.ops.render.render(write_still=True)
        return report
    raise ValueError("Unknown native export mode")


if __name__ == "__main__":
    request = json.loads(Path(__file__).with_name("request.json").read_text(encoding="utf-8-sig"))
    try:
        report = main(request)
    except Exception:
        report = {"Passed": False, "ToolVersion": bpy.app.version_string, "Error": traceback.format_exc()}
        Path(request["Result"]).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        raise
    Path(request["Result"]).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
