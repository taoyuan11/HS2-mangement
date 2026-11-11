"""比较显式诊断采集，不据此认定捏人环境或动态画面完全一致。"""

import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image
from skimage.metrics import structural_similarity


def compare(reference: Path, actual: Path):
    result = {"kind": "diagnostic-fixture", "visualParityVerified": False, "images": [], "materials": [], "bones": []}
    for name in [
        "angle-0.png",
        "angle-90.png",
        "angle-180.png",
        "face.png",
        "o_head_MainTex.png",
        "o_head_DetailMainTex.png",
        "o_body_cf_MainTex.png",
        "o_body_cf_DetailMainTex.png",
    ]:
        if not (reference / name).exists() or not (actual / name).exists():
            result["images"].append({"name": name, "error": "missing"})
            continue
        a = np.array(Image.open(reference / name).convert("RGB"))
        b = np.array(Image.open(actual / name).convert("RGB"))
        if a.shape != b.shape:
            result["images"].append({"name": name, "error": "size mismatch"})
            continue
        delta = np.abs(a.astype(np.float32) - b.astype(np.float32))
        item = {"name": name, "meanChannelError255": float(delta.mean()), "maxChannelError255": float(delta.max())}
        if a.shape[0] <= 720:
            # 使用前景并集，避免空渲染结果仅凭背景一致而通过检查。
            background = a[0, 0].astype(np.int16)
            mask = (np.abs(a.astype(np.int16) - background).max(axis=2) > 3) | (
                np.abs(b.astype(np.int16) - background).max(axis=2) > 3
            )
            if not mask.any():
                item["error"] = "empty foreground"
            else:
                score, field = structural_similarity(a, b, data_range=255, channel_axis=2, full=True)
                item["ssimFullFrame"] = float(score)
                item["ssimForeground"] = float(field.mean(axis=2)[mask].mean())
                item["meanForegroundError255"] = float(delta[mask].mean())
        result["images"].append(item)
    ref = json.loads((reference / "state.json").read_text(encoding="utf-8-sig"))
    act = json.loads((actual / "state.json").read_text(encoding="utf-8-sig"))
    actual_renderers = {r["Name"]: r for r in act["Renderers"]}
    for renderer in ref["Renderers"]:
        if not renderer["Active"]:
            continue
        other = actual_renderers.get(renderer["Name"])
        if other is None:
            result["materials"].append({"renderer": renderer["Name"], "missing": True})
            continue
        for i, material in enumerate(renderer["Materials"]):
            if i >= len(other["Materials"]):
                result["materials"].append({"renderer": renderer["Name"], "slot": i, "missing": True})
                continue
            theirs = other["Materials"][i]
            changes = {}
            for prop in ["Shader", "Queue", "Keywords", "Properties", "Textures"]:
                if material[prop] != theirs[prop]:
                    changes[prop] = {"expected": material[prop], "actual": theirs[prop]}
            if changes:
                result["materials"].append({"renderer": renderer["Name"], "slot": i, "changes": changes})
    # 全局骨架先创建；
    # 重复的局部绑定骨架不作为权威来源。
    actual_bones = {}
    for bone in act["Bones"]:
        actual_bones.setdefault(bone["Name"], bone)
    for bone in ref["Bones"]:
        if not bone["Name"].startswith(("cf_J_", "N_")) or bone["Name"] not in actual_bones:
            continue
        other = actual_bones[bone["Name"]]
        delta = sum((bone["World"][k] - other["World"][k]) ** 2 for k in "xyz") ** 0.5
        if delta > 0.001:
            result["bones"].append({"name": bone["Name"], "worldDistance": delta, "expected": bone, "actual": other})
    result["bones"].sort(key=lambda b: b["worldDistance"], reverse=True)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("reference", type=Path)
    parser.add_argument("actual", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    report = compare(args.reference, args.actual)
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(
        json.dumps(
            {
                "images": report["images"],
                "materialDifferences": len(report["materials"]),
                "boneDifferences": len(report["bones"]),
                "largestBoneDifferences": [
                    {"name": b["name"], "distance": b["worldDistance"]} for b in report["bones"][:15]
                ],
            },
            indent=2,
        )
    )
