"""Exercise the Text Editor entry point: -- PACKAGE OUTPUT IMPORTER."""
import json
import sys
import tempfile
from pathlib import Path

import bpy

folder, output, importer = [Path(p).resolve() for p in sys.argv[sys.argv.index("--") + 1:]]
output.mkdir(parents=True, exist_ok=True)
sys.stdout = (output / "checks.log").open("w", encoding="utf-8", buffering=1)
sys.stderr = sys.stdout
# Alt+P has no package argument. Leaving the harness's -- arguments in place
# would silently test the CLI path and miss the Text Editor regression.
sys.argv = sys.argv[:sys.argv.index("--")]
bpy.ops.wm.read_factory_settings(use_empty=True)
area = bpy.context.screen.areas[0]
area.type = "TEXT_EDITOR"
text = bpy.data.texts.load(str(folder / "Blender导入.py"))
source = importer.read_text(encoding="utf-8-sig")
text.from_string(source)
area.spaces.active.text = text
namespace = {"__name__": "path_checks", "__file__": "\\Blender导入.py"}
exec(compile(source, "\\Blender导入.py", "exec"), namespace)
checks = []

with bpy.context.temp_override(area=area):
    assert namespace["package_directory"]() == folder
    checks.append("Text.filepath overrides synthetic __file__ in unsaved scene")
    text.filepath = ""
    try:
        namespace["package_directory"]()
        raise AssertionError("An internal text must not use a synthetic path")
    except ValueError as error:
        assert "文本 → 打开" in str(error)
    checks.append("Internal text gives actionable Chinese instructions")
    sys.argv += ["--", str(folder)]
    assert namespace["package_directory"]() == folder
    sys.argv = sys.argv[:sys.argv.index("--")]
    checks.append("Explicit CLI package still works")
    text.filepath = str(folder / "Blender导入.py")
    sentinel = bpy.data.objects.new("Existing user object", None)
    bpy.context.scene.collection.objects.link(sentinel)
    # This invokes the exact same operator as Alt+P, not import_character().
    assert bpy.ops.text.run_script() == {"FINISHED"}
    collection = next(c for c in bpy.data.collections if c.get("HS2_Package") == str(folder))
    meshes = [o for o in collection.objects if o.type == "MESH"]
    materials = {s.material for o in meshes for s in o.material_slots}
    records = json.loads((folder / "materials.json").read_text(encoding="utf-8-sig"))["Materials"]
    assert {m["HS2_Id"] for m in materials} == {r["Id"] for r in records}
    images = {n.image for m in materials for n in m.node_tree.nodes if n.type == "TEX_IMAGE"}
    assert all(i.size[0] > 0 for i in images)
    assert sentinel.name in bpy.data.objects
    checks.append("Actual Text Editor Run Script imports all materials and images")
    # Windows cannot express a relative path between C: and a NAS share.
    # Put this scratch .blend on the package volume for the relative-path case.
    with tempfile.TemporaryDirectory(prefix="hs2-path-check-", dir=folder.parent) as scratch:
        bpy.ops.wm.save_as_mainfile(filepath=str(Path(scratch) / "path-check.blend"))
        text.filepath = bpy.path.relpath(str(folder / "Blender导入.py"))
        assert text.filepath.startswith("//")
        assert namespace["package_directory"]() == folder
    checks.append("Relative Text.filepath resolves against saved .blend")
    # Do not persist the test script's modified buffer in the deliverable scene.
    bpy.data.texts.remove(text)
    bpy.data.objects.remove(sentinel, do_unlink=True)
    area.type = "VIEW_3D"
    area.spaces.active.shading.type = "MATERIAL"
    with bpy.context.temp_override(region=next(r for r in area.regions if r.type == "WINDOW")):
        bpy.ops.view3d.view_all()
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(output / "character.blend"))

namespace["__file__"] = str(folder / "Blender导入.py")
assert namespace["package_directory"]() == folder
checks.append("CLI script location works without a package argument")
try:
    namespace["package_path"](folder, "missing.fbx")
    raise AssertionError("Missing file accepted")
except ValueError as error:
    assert str(folder / "missing.fbx") in str(error)
checks.append("Missing-file diagnostic includes the full attempted path")
try:
    namespace["package_path"](folder, "../character.fbx")
    raise AssertionError("Path traversal accepted")
except ValueError as error:
    assert "角色文件夹之外" in str(error)
checks.append("Package traversal remains blocked")
report = {"Passed": True, "Blender": bpy.app.version_string, "Package": str(folder),
          "Meshes": len(meshes), "Materials": len(materials), "Images": len(images), "Checks": checks}
(output / "validation.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print("TEXT EDITOR IMPORT PASSED", json.dumps(report, ensure_ascii=False))
