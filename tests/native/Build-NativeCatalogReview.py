"""生成成对总览和原始分辨率的本机证据，不授予视觉验收通过。"""

import argparse
import hashlib
import json
import math
import shutil
from pathlib import Path

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageEnhance, ImageOps
from scipy.ndimage import uniform_filter


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def reuse(entry, catalog, previous):
    """Carry an observed review only when every reviewed image is byte-identical."""
    source = previous / "visual-review" / entry["Id"]
    evidence = source / "evidence.json"
    if not evidence.is_file():
        return False
    manifest = read(evidence)
    if manifest.get("ReviewStatus") != "passed" or manifest.get("CardSHA256") != entry["Hash"]:
        return False
    samples = manifest.get("Samples", [])
    expected = {v + ".png" for v in ["front", "side", "back", "face"]} | {
        f"dynamic/frame-{f:03}.png" for f in range(15, 361, 15)
    }
    if len(samples) != 28 or {v["Image"] for v in samples} != expected:
        return False
    frozen = {v["Name"]: v for v in entry["Evidence"] if v["Source"] == "legacy-preview"}
    for sample in samples:
        name = sample["Image"]
        if (
            sample["ReferenceSHA256"] != frozen[name]["SHA256"]
            or sha(Path(frozen[name]["Path"])) != sample["ReferenceSHA256"]
            or sha(catalog / entry["Id"] / name) != sample["NativeSHA256"]
        ):
            return False
    files = ["overview.png", "local.png"]
    extra = manifest.get("AdditionalLocalReview", {}).get("File")
    if extra:
        if Path(extra).name != extra:
            return False
        files.append(extra)
    if not all((source / name).is_file() for name in files):
        return False
    destination = catalog / "visual-review" / entry["Id"]
    destination.mkdir(parents=True, exist_ok=True)
    for name in files:
        shutil.copyfile(source / name, destination / name)
    manifest["ReusedReviewFrom"] = str(source.resolve())
    manifest["ReuseValidation"] = "All 28 reference and native image SHA256 values match the observed review."
    (destination / "evidence.json").write_text(json.dumps(manifest, indent=2), encoding="utf8")
    return True


def build(entry, catalog):
    actual = catalog / entry["Id"]
    comparison = read(actual / "comparison.json")
    reference = Path(comparison["reference"])
    output = catalog / "visual-review" / entry["Id"]
    output.mkdir(parents=True, exist_ok=True)
    frozen = {v["Name"]: v["SHA256"] for v in entry["Evidence"] if v["Source"] == "legacy-preview"}
    groups = comparison["views"] + comparison["dynamic"]
    if len(groups) != 28:
        raise ValueError("All 28 samples must exist before review: " + entry["Id"])
    sheet = Image.new("RGB", (1440, 2280), "#20242b")
    draw = ImageDraw.Draw(sheet)
    draw.text((12, 8), entry["Id"] + " | Reference LEFT / Native RIGHT | four views + 24 dynamic samples", fill="white")
    samples = []
    worst = min(groups, key=lambda g: min(v["ssim"] for v in g["regions"].values()))
    for index, group in enumerate(groups):
        view = group["view"]
        name = f"dynamic/frame-{group['frame']:03}.png" if "frame" in group else view + ".png"
        if sha(reference / name) != frozen[name]:
            raise ValueError("Frozen reference hash changed: " + str(reference / name))
        images = [Image.open(folder / name).convert("RGB") for folder in [reference, actual]]
        backgrounds = [Image.open(folder / (view + "-background.png")).convert("RGB") for folder in [reference, actual]]
        mask = np.zeros((720, 1280), dtype=bool)
        for image, background in zip(images, backgrounds):
            mask |= np.abs(np.asarray(image, dtype=np.int16) - np.asarray(background, dtype=np.int16)).max(axis=2) > 4
        ys, xs = np.nonzero(mask)
        if not len(ys):
            raise ValueError("No visible character: " + str(actual / name))
        box = (
            max(0, int(xs.min()) - 16),
            max(0, int(ys.min()) - 16),
            min(1280, int(xs.max()) + 17),
            min(720, int(ys.max()) + 17),
        )
        x, y = index % 3 * 480, index // 3 * 224 + 30
        score = min(v["ssim"] for v in group["regions"].values())
        draw.text((x + 8, y + 4), f"{name} | min SSIM {score:.6f}", fill="white")
        for column, image in enumerate(images):
            thumb = ImageOps.contain(image.crop(box), (238, 198), Image.Resampling.LANCZOS)
            sheet.paste(thumb, (x + column * 240 + (238 - thumb.width) // 2, y + 24 + (198 - thumb.height) // 2))
        samples.append(
            {"Image": name, "ReferenceSHA256": frozen[name], "NativeSHA256": sha(actual / name), "OverviewBox": box}
        )
        if group is worst:
            error = np.abs(np.asarray(images[0], dtype=float) - np.asarray(images[1], dtype=float)).mean(axis=2)
            # 选取真实的局部差异，以原始分辨率保留周围 360 像素区域，避免放大缩略图。
            heat = uniform_filter(error, size=61) * mask
            cy, cx = np.unravel_index(np.argmax(heat), heat.shape)
            left, top = min(920, max(0, int(cx) - 180)), min(360, max(0, int(cy) - 180))
            crop = (left, top, left + 360, top + 360)
            detail = Image.new("RGB", (1080, 390), "#20242b")
            detail_draw = ImageDraw.Draw(detail)
            a, b = [image.crop(crop) for image in images]
            for column, (label, image) in enumerate(
                [
                    ("Reference", a),
                    ("Native", b),
                    ("Absolute error x8", ImageEnhance.Brightness(ImageChops.difference(a, b)).enhance(8)),
                ]
            ):
                detail.paste(image, (column * 360, 30))
                detail_draw.text((column * 360 + 6, 8), label + " / " + name, fill="white")
            detail.save(output / "local.png")
            local = {"Image": name, "Box": crop, "MinimumSSIM": score}
    sheet.save(output / "overview.png")
    manifest = {
        "Id": entry["Id"],
        "CardSHA256": entry["Hash"],
        "Samples": samples,
        "Local": local,
        "NumericPass": comparison["numericPass"],
        "ReviewStatus": "pending",
        "Method": "28 paired character-region overviews and the lowest-SSIM sample at original resolution; errors enlarged x8.",
    }
    (output / "evidence.json").write_text(json.dumps(manifest, indent=2), encoding="utf8")
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("catalog", type=Path)
    parser.add_argument("--ids")
    parser.add_argument("--reuse-reviewed", type=Path)
    args = parser.parse_args()
    baseline = read(args.baseline)
    report = read(args.catalog / "report.json")
    complete = {row["Id"] for row in report["Cards"] if row.get("ExecutionPassed")}
    requested = set(args.ids.split(",")) if args.ids else complete
    if not requested <= complete:
        raise ValueError("A requested card has not completed capture.")
    rows = []
    for entry in baseline["Cards"]:
        if entry["Id"] not in requested:
            continue
        if sha(Path(entry["Card"])) != entry["Hash"]:
            raise ValueError("Source card hash changed: " + entry["Id"])
        current = args.catalog / "visual-review" / entry["Id"] / "evidence.json"
        if current.exists():
            if (
                read(current).get("ReviewStatus") != "passed"
                and args.reuse_reviewed
                and reuse(entry, args.catalog, args.reuse_reviewed)
            ):
                print(entry["Id"], "previous observed review retained; all image hashes identical", flush=True)
            continue
        if args.reuse_reviewed and reuse(entry, args.catalog, args.reuse_reviewed):
            print(entry["Id"], "previous observed review retained; all image hashes identical", flush=True)
            continue
        rows.append(build(entry, args.catalog))
        print(entry["Id"], "review evidence generated; approval pending", flush=True)
