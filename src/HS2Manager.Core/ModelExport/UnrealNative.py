"""Internal UE native asset/scene exporter, run only in an isolated owned project."""
import json
import math
import re
import sys
import traceback
from pathlib import Path

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import unreal
import UnrealImport as importer

ROOT = "/Game/HS2Character"


def position(values, scale=10):
    return unreal.Vector(-values[0] * scale, values[2] * scale, values[1] * scale)


def color(values):
    return unreal.LinearColor(*values)


def custom_input(name):
    value = unreal.CustomInput()
    value.set_editor_property("input_name", name)
    return value


def scene_texture(folder, relative, name):
    texture = next((a for a in importer.import_asset(importer.package_path(folder, relative), ROOT + "/Environment", name)
                    if isinstance(a, (unreal.Texture2D, unreal.TextureCube))), None)
    if texture is None:
        raise RuntimeError("不能导入环境贴图：" + relative)
    texture.set_editor_property("srgb", False)
    texture.set_editor_property("compression_settings", unreal.TextureCompressionSettings.TC_HDR)
    texture.set_editor_property("mip_gen_settings", unreal.TextureMipGenSettings.TMGS_NO_MIPMAPS if name == "PreviewLUT" else unreal.TextureMipGenSettings.TMGS_FROM_TEXTURE_GROUP)
    if isinstance(texture, unreal.Texture2D):
        texture.set_editor_property("address_x", unreal.TextureAddress.TA_CLAMP)
        texture.set_editor_property("address_y", unreal.TextureAddress.TA_CLAMP)
        texture.set_editor_property("filter", unreal.TextureFilter.TF_BILINEAR)
    return texture


def post_material(folder, data):
    material = unreal.AssetToolsHelpers.get_asset_tools().create_asset("M_PreviewColor", ROOT + "/Environment", unreal.Material, unreal.MaterialFactoryNew())
    material.set_editor_property("material_domain", unreal.MaterialDomain.MD_POST_PROCESS)
    material.set_editor_property("blendable_location", unreal.BlendableLocation.BL_REPLACING_TONEMAPPER)
    graph = importer.Graph(material)
    source = graph.node("SceneTexture", "Scene linear color", scene_texture_id=unreal.SceneTextureId.PPI_POST_PROCESS_INPUT0)
    inputs = [custom_input("SceneColor")]
    code = "float3 c=max(SceneColor.rgb,0); return lerp(c*12.92,1.055*pow(c,1.0/2.4)-0.055,step(0.0031308,c));"
    lut = None
    if data["LutTexture"]:
        lut = scene_texture(folder, data["LutTexture"], "PreviewLUT")
        if not isinstance(lut, unreal.Texture2D):
            raise RuntimeError("预览 LUT 必须是二维条带贴图。")
        inputs.append(custom_input("PreviewLut"))
        n = data["LutSize"]
        if data["LutEncoding"] == "LogC":
            encode = f"float3 p=saturate(0.244161*log10(5.555556*max(SceneColor.rgb,0)*{2 ** data['Post']['Exposure']:.9g}+0.047996)+0.386036);"
            decode = "result=max(result,0); return lerp(result*12.92,1.055*pow(result,1.0/2.4)-0.055,step(0.0031308,result));"
        else:
            encode = "float3 c=saturate(SceneColor.rgb); float3 p=lerp(c*12.92,1.055*pow(max(c,0),1.0/2.4)-0.055,step(0.0031308,c));"
            decode = "return result;"
        code = encode + f"""
float z=p.b*{n-1}.0;
float lo=floor(z),hi=min(lo+1,{n-1}.0);
float2 a=float2((p.r*{n-1}.0+0.5+lo*{n}.0)/{n*n}.0,1-(p.g*{n-1}.0+0.5)/{n}.0);
float2 b=float2((p.r*{n-1}.0+0.5+hi*{n}.0)/{n*n}.0,a.y);
float3 result=lerp(Texture2DSampleLevel(PreviewLut,PreviewLutSampler,a,0).rgb,Texture2DSampleLevel(PreviewLut,PreviewLutSampler,b,0).rgb,z-lo);
""" + decode
    transform = graph.node("Custom", "Evaluated Unity preview color LUT", code=code,
                           output_type=unreal.CustomMaterialOutputType.CMOT_FLOAT3, inputs=inputs)
    graph.link(source, transform, "SceneColor", "Color")
    if lut:
        texture = graph.node("TextureObjectParameter", "Preview LUT", parameter_name="PreviewLUT", texture=lut, sampler_type=unreal.MaterialSamplerType.SAMPLERTYPE_LINEAR_COLOR)
        graph.link(texture, transform, "PreviewLut")
    graph.output(transform, "EMISSIVE_COLOR")
    unreal.MaterialEditingLibrary.recompile_material(material)
    unreal.MaterialEditingLibrary.layout_material_expressions(material)
    return material


def background_material(data, cube):
    material = unreal.AssetToolsHelpers.get_asset_tools().create_asset("M_PreviewBackground", ROOT + "/Environment", unreal.Material, unreal.MaterialFactoryNew())
    material.set_editor_property("shading_model", unreal.MaterialShadingModel.MSM_UNLIT)
    material.set_editor_property("two_sided", True)
    material.set_editor_property("is_sky", True)
    graph = importer.Graph(material)
    if data["SkyBackground"] and cube:
        ray = graph.node("CameraVectorWS", "Camera to sky")
        transform = graph.node("Custom", "Environment orientation", code="return float3(Direction.x,Direction.y,-Direction.z);",
                               output_type=unreal.CustomMaterialOutputType.CMOT_FLOAT3, inputs=[custom_input("Direction")])
        graph.link(ray, transform, "Direction")
        sample = graph.node("TextureSampleParameterCube", "Visible sky", parameter_name="Sky", texture=cube, sampler_type=unreal.MaterialSamplerType.SAMPLERTYPE_LINEAR_COLOR)
        graph.link(transform, sample, "UVs")
        graph.output(sample, "EMISSIVE_COLOR", "RGB")
    else:
        node = graph.node("VectorParameter", "Preview background", parameter_name="Background", default_value=color(data["Background"]))
        graph.output(node, "EMISSIVE_COLOR")
    unreal.MaterialEditingLibrary.recompile_material(material)
    return material


def setup_scene(folder, data, imported):
    actors = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)
    level = unreal.get_editor_subsystem(unreal.LevelEditorSubsystem)
    if not level.new_level(ROOT + "/Preview"):
        raise RuntimeError("不能创建预览关卡。")
    for mesh in imported["meshes"]:
        actor = actors.spawn_actor_from_class(unreal.SkeletalMeshActor, unreal.Vector())
        actor.set_actor_label("Character")
        actor.set_folder_path("Character")
        actor.get_component_by_class(unreal.SkeletalMeshComponent).set_skeletal_mesh_asset(mesh)
    for source in data["Lights"]:
        actor_class = {0: unreal.SpotLight, 1: unreal.DirectionalLight, 2: unreal.PointLight, 3: unreal.RectLight}.get(source["Type"], unreal.DirectionalLight)
        light = actors.spawn_actor_from_class(actor_class, position(source["Position"]))
        light.set_actor_label(source["Name"])
        light.set_folder_path("Preview/Lights")
        direction = position(source["Direction"], 1)
        light.set_actor_rotation(unreal.MathLibrary.find_look_at_rotation(unreal.Vector(), direction), False)
        component = light.get_component_by_class(unreal.LightComponent)
        component.set_light_color(color(source["Color"]), False)
        component.set_intensity(source["Intensity"] * math.pi)
        component.set_cast_shadows(source["Shadows"])
        if source["Type"] in (0, 2):
            component.set_editor_property("attenuation_radius", source["Range"] * 10)
            component.set_editor_property("use_inverse_squared_falloff", False)
        if source["Type"] == 0:
            component.set_editor_property("outer_cone_angle", source["SpotAngle"] * 0.5)
    sky_cube = scene_texture(folder, data["EnvironmentTexture"], "PreviewSky") if data["EnvironmentTexture"] else None
    reflection = scene_texture(folder, data["ReflectionTexture"], "PreviewReflection") if data["ReflectionTexture"] else sky_cube
    if reflection:
        if not isinstance(reflection, unreal.TextureCube):
            raise RuntimeError("环境贴图没有导入为 TextureCube。")
        sky = actors.spawn_actor_from_class(unreal.SkyLight, unreal.Vector())
        sky.set_actor_label("Preview Environment")
        sky.set_folder_path("Preview/Lights")
        component = sky.get_component_by_class(unreal.SkyLightComponent)
        component.set_editor_property("source_type", unreal.SkyLightSourceType.SLS_SPECIFIED_CUBEMAP)
        component.set_editor_property("cubemap", reflection)
        component.set_editor_property("source_cubemap_angle", 180.0)
        component.set_editor_property("lower_hemisphere_is_black", False)
        component.set_intensity(data["AmbientIntensity"])
        component.set_editor_property("indirect_lighting_intensity", 1.0)
    background = actors.spawn_actor_from_class(unreal.StaticMeshActor, unreal.Vector())
    background.set_actor_label("Preview Background")
    background.set_folder_path("Preview/Background")
    component = background.get_component_by_class(unreal.StaticMeshComponent)
    component.set_static_mesh(unreal.load_asset("/Engine/BasicShapes/Sphere.Sphere"))
    component.set_material(0, background_material(data, sky_cube))
    component.set_cast_shadow(False)
    component.set_editor_property("visible_in_reflection_captures", False)
    component.set_editor_property("visible_in_ray_tracing", False)
    background.set_actor_scale3d(unreal.Vector(100, 100, 100))
    view = data["View"]
    camera = actors.spawn_actor_from_class(unreal.CameraActor, position(view["Position"]))
    camera.set_actor_label("Preview Camera")
    camera.set_folder_path("Preview")
    rotation = unreal.MathLibrary.find_look_at_rotation(position(view["Position"]), position(view["Target"]))
    camera.set_actor_rotation(rotation, False)
    component = camera.get_component_by_class(unreal.CameraComponent)
    aspect = view["Width"] / view["Height"]
    component.set_field_of_view(math.degrees(2 * math.atan(math.tan(math.radians(view["FieldOfView"]) / 2) * aspect)))
    component.set_aspect_ratio(aspect)
    component.set_editor_property("constrain_aspect_ratio", True)
    post_actor = actors.spawn_actor_from_class(unreal.PostProcessVolume, unreal.Vector())
    post_actor.set_actor_label("Preview Color and Effects")
    post_actor.set_folder_path("Preview")
    post_actor.set_editor_property("unbound", True)
    settings = post_actor.get_editor_property("settings")

    def setting(name, value):
        settings.set_editor_property("override_" + name, True)
        settings.set_editor_property(name, value)

    setting("auto_exposure_method", unreal.AutoExposureMethod.AEM_MANUAL)
    setting("auto_exposure_apply_physical_camera_exposure", False)
    setting("auto_exposure_bias", 0.0)
    setting("motion_blur_amount", 0.0)
    setting("bloom_intensity", 2 ** (data["Post"]["BloomIntensity"] / 10) - 1 if data["Post"]["Bloom"] else 0)
    setting("bloom_threshold", data["Post"]["BloomThreshold"])
    setting("ambient_occlusion_intensity", data["Post"]["AoIntensity"] if data["Post"]["AmbientOcclusion"] else 0)
    setting("ambient_occlusion_radius", data["Post"]["AoRadius"] * 10)
    setting("vignette_intensity", data["Post"]["VignetteIntensity"] if data["Post"]["Vignette"] else 0)
    settings.set_editor_property("weighted_blendables", unreal.WeightedBlendables(array=[unreal.WeightedBlendable(weight=1, object=post_material(folder, data))]))
    post_actor.set_editor_property("settings", settings)
    unreal.EditorLevelLibrary.set_level_viewport_camera_info(position(view["Position"]), rotation)
    if not level.save_current_level():
        raise RuntimeError("预览关卡保存失败。")
    return camera


def verify(output, expected):
    manifest = json.loads((output / "native-assets.json").read_text(encoding="utf-8"))
    if not unreal.EditorLoadingAndSavingUtils.load_map(ROOT + "/Preview"):
        raise RuntimeError("无法重新打开预览关卡。")
    materials = set()
    morphs = set()
    for path in manifest["Meshes"]:
        mesh = unreal.load_asset(path)
        if not isinstance(mesh, unreal.SkeletalMesh) or mesh.get_editor_property("skeleton") is None:
            raise RuntimeError("原生骨架模型缺失：" + path)
        morphs.update(m.get_name() for m in mesh.get_editor_property("morph_targets"))
        for slot in mesh.get_editor_property("materials"):
            material = slot.get_editor_property("material_interface")
            if material is None:
                raise RuntimeError("原生模型有空材质槽。")
            materials.add(unreal.EditorAssetLibrary.get_metadata_tag(material, "HS2_Id"))
    if materials != set(expected):
        raise RuntimeError("原生材质槽和导出清单不一致。")
    if morphs != set(manifest["MorphTargets"]):
        raise RuntimeError("重新打开后形变通道不完整。")
    registry = unreal.AssetRegistryHelpers.get_asset_registry()
    assets = unreal.EditorAssetLibrary.list_assets(ROOT, recursive=True, include_folder=False)
    for path in assets:
        if unreal.load_asset(path) is None:
            raise RuntimeError("不能加载原生资产：" + path)
        package = path.split(".")[0]
        dependencies = registry.get_dependencies(package, unreal.AssetRegistryDependencyOptions(include_soft_package_references=True, include_hard_package_references=True))
        for dependency in dependencies:
            dependency = str(dependency)
            if dependency.startswith("/Game/") and not unreal.EditorAssetLibrary.does_asset_exist(dependency):
                raise RuntimeError("原生资产依赖缺失：" + dependency)
    return {"Passed": True, "ToolVersion": unreal.SystemLibrary.get_engine_version(), "Meshes": len(manifest["Meshes"]),
            "Materials": len(materials), "MorphTargets": len(morphs), "Assets": len(assets), "Warnings": manifest["Warnings"], "Reopened": True}


def main(request):
    if request["Mode"] == "probe":
        for name in ("FbxImportUI", "EditorActorSubsystem", "MaterialEditingLibrary", "CustomInput"):
            if not hasattr(unreal, name):
                raise RuntimeError("当前 Unreal 缺少原生导出接口：" + name)
        return {"Passed": True, "ToolVersion": unreal.SystemLibrary.get_engine_version()}
    output = Path(request["Output"])
    if request["Mode"] == "verify":
        return verify(output, request["MaterialIds"])
    if request["Mode"] != "convert":
        raise ValueError("Unknown native export mode")
    folder = Path(request["Source"]).resolve()
    data = json.loads(importer.package_path(folder, "scene.json").read_text(encoding="utf-8-sig"))
    result = importer.import_character(folder, ROOT + "/Character")
    setup_scene(folder, data, result)
    if not unreal.EditorAssetLibrary.save_directory(ROOT, only_if_is_dirty=False, recursive=True):
        raise RuntimeError("原生资产保存失败。")
    warnings = list(data["Warnings"])
    geometry = json.loads(importer.package_path(folder, "model-result.json").read_text(encoding="utf-8-sig"))["Geometry"]
    expected_morphs = {name.replace(".", "_") for mesh in geometry for name in mesh["BlendShapeNames"]}
    morphs = {m.get_name() for mesh in result["meshes"] for m in mesh.get_editor_property("morph_targets")}
    missing = expected_morphs - morphs
    displacements = {}
    for mesh in geometry:
        values = mesh.get("BlendShapeMaximumDisplacements", [])
        if len(values) != len(mesh["BlendShapeNames"]):
            raise RuntimeError("缺少形变位移清单，请使用当前内核重新导出。")
        for name, distance in zip(mesh["BlendShapeNames"], values):
            key = name.replace(".", "_")
            displacements[key] = max(displacements.get(key, 0), distance)
    # UE omits channels without positional deltas. Never whitelist by expression name.
    lost = {name for name in missing if displacements[name] > 1e-7}
    if lost:
        raise RuntimeError("Unreal 导入丢失形变通道：" + ", ".join(sorted(lost)))
    if missing:
        warnings.append("Unreal 省略无位移的默认形变通道：" + ", ".join(sorted(missing)))
    warnings.append("Unreal SkyLight 的漫反射和镜面环境响应采用统一环境源；反射强度与源预览可能存在差异。")
    manifest = {"Meshes": [m.get_path_name() for m in result["meshes"]], "MorphTargets": sorted(morphs), "Scene": ROOT + "/Preview", "Warnings": warnings,
                "Camera": data["View"], "ToolVersion": unreal.SystemLibrary.get_engine_version()}
    (output / "native-assets.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    project = output / "HS2Character.uproject"
    project_data = json.loads(project.read_text(encoding="utf-8-sig"))
    version = re.match(r"(\d+\.\d+)", unreal.SystemLibrary.get_engine_version())
    if version:
        project_data["EngineAssociation"] = version.group(1)
    project.write_text(json.dumps(project_data, indent=2), encoding="utf-8")
    return {"Passed": True, "ToolVersion": unreal.SystemLibrary.get_engine_version(), "Warnings": warnings, "Reopened": False}


if __name__ == "__main__":
    request = json.loads(Path(__file__).with_name("request.json").read_text(encoding="utf-8-sig"))
    try:
        report = main(request)
    except Exception:
        report = {"Passed": False, "ToolVersion": unreal.SystemLibrary.get_engine_version(), "Error": traceback.format_exc()}
        Path(request["Result"]).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        raise
    Path(request["Result"]).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
