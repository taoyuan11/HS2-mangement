"""在独立 UE 命令程序中运行，HS2_UNREAL_VALIDATION 指向请求 JSON。"""

import json
import os
import runpy
import hashlib
import traceback
from pathlib import Path

import unreal

request = json.loads(Path(os.environ["HS2_UNREAL_VALIDATION"]).read_text(encoding="utf-8-sig"))
output = Path(request["Output"])
report = {
    "Passed": False,
    "Engine": unreal.SystemLibrary.get_engine_version(),
    "Substrate": unreal.SystemLibrary.get_console_variable_int_value("r.Substrate"),
    "Samples": [],
}
try:
    expected_importer = Path(request["Importer"]).read_bytes()
    for index, folder in enumerate(request["Packages"]):
        script = Path(request["Importer"])
        importer = runpy.run_path(str(script), run_name="hs2_unreal")
        records = json.loads((Path(folder) / "materials.json").read_text(encoding="utf-8-sig"))["Materials"]
        result = importer["import_character"](folder, "/Game/Validation/Sample_" + str(index))
        sample = {
            "Package": folder,
            "Destination": result["destination"],
            "Meshes": len(result["meshes"]),
            "MeshAssets": [m.get_path_name() for m in result["meshes"]],
            "Materials": len(result["materials"]),
            "Kinds": {},
            "Slots": 0,
            "MorphTargets": 0,
        }
        for record in records:
            material = result["materials"][record["Id"]]
            kind = importer["material_kind"](record)
            sample["Kinds"][kind] = sample["Kinds"].get(kind, 0) + 1
            assert unreal.EditorAssetLibrary.get_metadata_tag(material, "HS2_Id") == record["Id"]
            if kind in ("Hair", "Eyelash"):
                assert material.get_editor_property("blend_mode") == unreal.BlendMode.BLEND_MASKED
                assert material.get_editor_property("two_sided")
            if kind == "Hair":
                assert material.get_editor_property("dither_opacity_mask")
            if kind == "Skin":
                assert material.get_editor_property("shading_model") == unreal.MaterialShadingModel.MSM_SUBSURFACE
            if kind == "Eye":
                assert material.get_editor_property("blend_mode") == unreal.BlendMode.BLEND_OPAQUE
            if kind == "EyeShadow":
                assert material.get_editor_property("blend_mode") == unreal.BlendMode.BLEND_MODULATE
                assert material.get_editor_property("shading_model") == unreal.MaterialShadingModel.MSM_UNLIT
                assert (
                    material.get_editor_property("translucency_lighting_mode")
                    == unreal.TranslucencyLightingMode.TLM_VOLUMETRIC_NON_DIRECTIONAL
                )
            for key in ("BaseColor", "Normal", "Metallic", "Roughness", "Occlusion", "Emission"):
                if not record.get(key):
                    continue
                texture = unreal.load_asset(result["destination"] + "/Textures/T_" + record["Id"] + "_" + key)
                assert texture and texture.get_editor_property("srgb") == (key in ("BaseColor", "Emission")), key
                if key == "Normal":
                    assert (
                        texture.get_editor_property("compression_settings")
                        == unreal.TextureCompressionSettings.TC_NORMALMAP
                    )
                    assert texture.get_editor_property("flip_green_channel")
                unreal.MaterialEditingLibrary.get_material_default_texture_parameter_value(material, key)
        for mesh in result["meshes"]:
            slots = mesh.get_editor_property("materials")
            sample["Slots"] += len(slots)
            for slot in slots:
                ident = str(slot.get_editor_property("imported_material_slot_name"))
                assert slot.get_editor_property("material_interface") == result["materials"][ident], ident
            sample["MorphTargets"] += len(mesh.get_editor_property("morph_targets"))
        assert {
            str(s.get_editor_property("imported_material_slot_name"))
            for m in result["meshes"]
            for s in m.get_editor_property("materials")
        } == {r["Id"] for r in records}, "Missing material slots"
        report["Samples"].append(sample)
    # 重新导入时创建独立资源，保留用户已有修改。
    first = report["Samples"][0]["Destination"]
    first_records = json.loads((Path(request["Packages"][0]) / "materials.json").read_text(encoding="utf-8-sig"))[
        "Materials"
    ]
    first_material_path = first + "/Materials/M_" + first_records[0]["Id"]
    original = unreal.load_asset(first_material_path)
    unreal.EditorAssetLibrary.set_metadata_tag(original, "UserEditSentinel", "Preserved")
    repeated = importer["import_character"](request["Packages"][0], first)
    assert repeated["destination"] != first
    assert unreal.load_asset(first_material_path) == original
    assert unreal.EditorAssetLibrary.get_metadata_tag(original, "UserEditSentinel") == "Preserved"
    report["RepeatedImport"] = repeated["destination"]
    try:
        importer["package_path"](Path(request["Packages"][0]).resolve(), "../escape.png")
        raise AssertionError("Path traversal accepted")
    except ValueError:
        report["PathBoundary"] = True
    report["ImporterSha256"] = hashlib.sha256(expected_importer).hexdigest().upper()
    report["Passed"] = True
except Exception:
    report["Error"] = traceback.format_exc()
    unreal.log_error(report["Error"])
finally:
    (output / "validation.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
if not report["Passed"]:
    raise RuntimeError(report["Error"])
