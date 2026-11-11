# Native model export

`hs2_model_export` implements the model export workflow in C++20. It uses the
same prepared character and D3D11 material resources as the native preview.
The app does not start a CLR process for preparation, material baking, FBX
generation, reference rendering, or export reporting.

The exporter preserves reference geometry, bones, normalized skin weights,
all authored UV sets, vertex colors, normals, tangents, multi-frame blend
shapes, and a reset-expression shape when the card has nonzero default
expression weights. The Autodesk FBX adapter independently reopens each
written file and checks its scene counts. Bone mapping and sampled reference
positions remain available to the target-application validators.

PBR maps are baked on D3D11 and saved with WIC. Color maps retain sRGB transfer;
normal and scalar maps retain linear values. Native scene export captures the
reference image, HDR sky and reflection panoramas, an uncompressed HALF OpenEXR
grading LUT, camera, lights, and post-processing parameters. Engine-specific
approximations remain explicit warnings in the delivered report.

`ModelExportService` owns its COM apartment, graphics resources, staging
directory, and converter session. A package is published only after validation
and file hashing. Failure and cancellation remove that character's unpublished
staging data. Blender and Unreal use the packaged Python import resources in
their own applications, then reopen the result in a fresh process. Conversion
scripts are not included in delivered packages. The process runner uses a
Windows job object to reclaim child processes on completion, cancellation, and
timeout.

Validation on 2026-11-11:

- `hs2_model_export_checks`: 29 checks, including actual D3D11 PBR output,
  independent WIC PNG decoding, RGBE scanline values, HALF EXR channels, and
  camera validation.
- `hs2_export_checks`: native SDK create/write/reopen, UVs, skin, blend shapes,
  overwrite protection, and a forward-slash DLL-path regression.
- Real card `HS2ChaF_20240623005417695.png`: 34 meshes, 88,766 vertices, 842
  FBX bones, 150 blend shapes, and 34 materials. All 180 generic-package file
  hashes were independently verified.
- Blender 5.3.0 Release Candidate: converted and reopened successfully with
  175 packed images. Maximum sampled reference error was 0.000528 mm; all
  eight delivered-package file hashes matched.
- Unreal 5.8.4: converted and reopened successfully with one merged skeletal
  mesh, 34 materials, 146 morph targets, and 214 native assets. All 224
  delivered-package file hashes matched. The fresh local shader cache was
  built within the original 900-second conversion timeout.
- After all three real service exports, no converter, shader worker, Zen,
  trace-server, or export-check process remained; session directories were
  empty. Native packages contained no conversion scripts.

The real card reports one missing source accessory (`accessory.parts[2].id`),
matching both managed baseline records (`配饰 3资源不存在：359/99999999`), so its
package correctly receives the `Incomplete` status. Unknown shader and
cross-engine shading approximations are recorded separately as warnings.
