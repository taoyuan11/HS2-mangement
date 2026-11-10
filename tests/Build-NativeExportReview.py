"""Build matched-view review sheets and descriptive metrics (no visual pass threshold).
Usage: python Build-NativeExportReview.py ROOT ARTIFACT_DIRECTORY
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

root, artifact = map(Path, sys.argv[1:])
output = artifact / "review-final"
output.mkdir(parents=True, exist_ok=True)
views = ("front", "face", "sideback")
report = {"Method": "sRGB 0..255 MAE; fixed central vertical regions; Sobel edge F1 with 2px tolerance (includes internal edges, not an alpha silhouette)", "Comparisons": []}


def array(path):
    return np.asarray(Image.open(path).convert("RGB"), dtype=float)


def edges(rgb):
    gray = ndimage.gaussian_filter(rgb.mean(axis=2), .7)
    magnitude = np.hypot(ndimage.sobel(gray, 0), ndimage.sobel(gray, 1))
    return magnitude > 70


def compare(reference, actual):
    assert reference.shape == actual.shape
    error = np.abs(reference - actual)
    a, b = edges(reference), edges(actual)
    precision = np.mean(ndimage.maximum_filter(a, 5)[b]) if b.any() else 0
    recall = np.mean(ndimage.maximum_filter(b, 5)[a]) if a.any() else 0
    h, w = error.shape[:2]
    regions = {}
    for name, top, bottom in (("upper", .1, .35), ("middle", .35, .65), ("lower", .65, .9)):
        selection = np.s_[int(h * top):int(h * bottom), int(w * .3):int(w * .7)]
        regions[name] = {"MAE": float(error[selection].mean()),
                         "ReferenceMeanRGB": reference[selection].mean(axis=(0, 1)).tolist(),
                         "TargetMeanRGB": actual[selection].mean(axis=(0, 1)).tolist()}
    return {"MAE": float(error.mean()), "RMSE": float(np.sqrt(np.mean((reference - actual) ** 2))),
            "EdgeF1": float(2 * precision * recall / max(1e-9, precision + recall)), "Regions": regions}


for sample in range(3):
    sheet = Image.new("RGB", (1440, 1140), "#20242b")
    draw = ImageDraw.Draw(sheet)
    for row, view in enumerate(views):
        folder = root / (f"final-{sample}" if view == "front" else f"review-{sample}-{view}") / "intermediate"
        if sample == 2:
            folder = root / f"aligned-2-{view}/intermediate"
        paths = [folder / "预览参考.png", output / f"blender-{sample}-{view}.png", output / f"unreal-{sample}-{view}.png"]
        reference = array(paths[0])
        for column, (label, path) in enumerate(zip(("Unity", "Blender Eevee", "Unreal TSR"), paths)):
            thumbnail = Image.open(path).convert("RGB").resize((480, 360), Image.Resampling.LANCZOS)
            sheet.paste(thumbnail, (column * 480, row * 380 + 20))
            draw.text((column * 480 + 8, row * 380 + 4), f"{label} / {sample} / {view}", fill="white")
            if column:
                report["Comparisons"].append({"Sample": sample, "View": view, "Target": label,
                                              **compare(reference, array(path))})
    sheet.save(output / f"comparison-{sample}.png")
for label in ("Blender Eevee", "Unreal TSR"):
    subset = [r for r in report["Comparisons"] if r["Target"] == label]
    report[label] = {"MeanMAE": sum(r["MAE"] for r in subset) / len(subset),
                     "MeanEdgeF1": sum(r["EdgeF1"] for r in subset) / len(subset)}
(output / "visual-metrics.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps({k: v for k, v in report.items() if k not in ("Method", "Comparisons")}, indent=2))
