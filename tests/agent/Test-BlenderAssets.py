"""运行真实 Blender 参考、配饰、发型和服装导出，并回读 FBX 验证几何与权重。"""

import argparse
import json
import pathlib
import subprocess


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--blender", required=True)
    parser.add_argument("--reference", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parents[2]
    output = pathlib.Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    builder = root / "src/HS2Manager.NativeAgent/Resources/BlenderAsset.py"
    report = {"reference": args.reference, "checks": []}
    for kind in ("reference", "accessory", "hair", "clothes"):
        work = output / kind
        work.mkdir(exist_ok=True)
        spec = {
            "mode": "reference" if kind == "reference" else "build",
            "kind": kind,
            "reference_model": str(pathlib.Path(args.reference).resolve()),
            "work_directory": str(work),
            "source_script": str(root / "tests/agent/BlenderAssetFixture.py"),
        }
        spec_path = work / "spec.json"
        spec_path.write_text(json.dumps(spec), encoding="utf-8")
        with (work / "blender.log").open("w", encoding="utf-8") as log:
            subprocess.run(
                [
                    args.blender,
                    "--background",
                    "--factory-startup",
                    "--python-exit-code",
                    "1",
                    "--python",
                    str(builder),
                    "--",
                    str(spec_path),
                ],
                check=True,
                timeout=120,
                stdout=log,
                stderr=subprocess.STDOUT,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
        result = json.loads((work / "result.json").read_text(encoding="utf-8"))
        if kind == "reference":
            assert result["bones"] and result["images"] and pathlib.Path(result["blend"]).is_file()
        else:
            assert result["valid"] and result["vertices"] > 0 and result["triangles"] > 0
            assert (work / "asset.fbx").stat().st_size > 100
            with (work / "roundtrip.log").open("w", encoding="utf-8") as log:
                subprocess.run(
                    [
                        args.blender,
                        "--background",
                        "--factory-startup",
                        "--python-exit-code",
                        "1",
                        "--python",
                        str(root / "tests/agent/BlenderInspectAsset.py"),
                        "--",
                        str(work),
                        kind,
                    ],
                    check=True,
                    timeout=120,
                    stdout=log,
                    stderr=subprocess.STDOUT,
                    creationflags=subprocess.CREATE_NO_WINDOW,
                )
            result["roundtrip"] = json.loads((work / "roundtrip.json").read_text(encoding="utf-8"))
        report["checks"].append({"kind": kind, "result": result})
        print("PASS Blender " + kind, flush=True)
    report["passed"] = True
    (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
