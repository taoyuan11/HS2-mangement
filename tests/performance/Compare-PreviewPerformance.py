"""比较应用缓存性能与确定性的 A-B-A 视觉回归采集。"""

import argparse
import hashlib
import json
import math
import statistics
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def compare(before, after, reference, actual):
    baseline, updated = read(before / "measurements.json"), read(after / "measurements.json")
    phases = []
    for phase in ["prepare-first", "prepare-repeat", "open-first", "switch", "reopen"]:
        for kind in ["all"] + [s["Kind"] for s in read(before / "samples.json")]:

            def values(rows):
                return [r["Milliseconds"] for r in rows if r["Phase"] == phase and (kind == "all" or r["Kind"] == kind)]

            a, b = values(baseline), values(updated)
            if not a or not b:
                continue
            old, new = statistics.median(a), statistics.median(b)
            phases.append(
                dict(
                    phase=phase,
                    sample=kind,
                    beforeMs=old,
                    afterMs=new,
                    reductionPercent=(1 - new / old) * 100,
                    beforeCount=len(a),
                    afterCount=len(b),
                    afterP95Ms=sorted(b)[math.ceil(0.95 * len(b)) - 1],
                )
            )

    def row(phase, sample="all"):
        return next(r for r in phases if r["phase"] == phase and r["sample"] == sample)

    checks = {
        "repeatPreparationHalved": row("prepare-repeat")["reductionPercent"] >= 50,
        "switchFirstFrame30PercentFaster": row("switch")["reductionPercent"] >= 30,
        "reopenMedian30PercentFaster": row("reopen")["reductionPercent"] >= 30,
        "coldFirstFrameWithin10Percent": row("open-first", "stock-female")["reductionPercent"] >= -10,
    }
    definitions = {}
    for sample in read(before / "samples.json"):
        kind = sample["Kind"]

        def normalize(value, prefix):
            if isinstance(value, dict):
                return {k: normalize(v, prefix) for k, v in value.items()}
            if isinstance(value, list):
                return [normalize(v, prefix) for v in value]
            if isinstance(value, str):
                return value.replace(prefix, "CACHE")
            return value

        definitions[kind] = normalize(
            read(before / (kind + ".json")), str((before / "PrepareData").resolve())
        ) == normalize(read(after / (kind + ".json")), str((after / "PrepareData").resolve()))
    checks["definitionsUnchanged"] = all(definitions.values())
    events = [
        json.loads(line)
        for line in (after / "UiData/Logs/preview-performance.jsonl").read_text(encoding="utf-8-sig").splitlines()
    ]
    warm = [r for r in events if r["Kind"] == "prepare" and 5 <= r["Load"] <= 24]
    checks["warmArchiveOpensZero"] = len(warm) == 20 and all(r["Counters"].get("archiveOpens", 0) == 0 for r in warm)
    checks["warmModRescansZero"] = len(warm) == 20 and all(r["Counters"].get("modScans", 0) == 0 for r in warm)
    kernel = [r["Stages"] for r in events if r["Kind"] == "kernel" and 5 <= r["Load"] <= 24]
    checks["commonBundlesRetained"] = len(kernel) == 20 and all(r["RetainedBundles"] > 0 for r in kernel)
    memory = []
    for kind in [s["Kind"] for s in read(after / "samples.json")]:
        rows = [r for r in updated if r["Phase"] == "switch" and r["Kind"] == kind]
        memory.append(
            dict(
                sample=kind,
                firstPlayerBytes=rows[0]["PlayerBytes"],
                lastPlayerBytes=rows[-1]["PlayerBytes"],
                peakPlayerBytes=max(r["PlayerBytes"] for r in rows),
                peakHostBytes=max(r["HostBytes"] for r in rows),
            )
        )
    checks["memoryPlateau"] = all(r["lastPlayerBytes"] <= r["firstPlayerBytes"] * 1.05 + 64 * 1024**2 for r in memory)
    cache_bytes = sum(p.stat().st_size for p in (after / "UiData/UnityPreview/metadata-v1").glob("*.cache"))
    checks["metadataDiskBudget"] = cache_bytes <= 512 * 1024**2

    images, states, reused = [], {}, {}
    for kind in definitions:
        ref, act = reference / kind, actual / kind
        reused[kind] = (act / "reuse.txt").read_text(encoding="utf-8-sig")
        for path in sorted(ref.rglob("*.png")):
            relative = path.relative_to(ref)
            target = act / relative
            item = dict(sample=kind, image=str(relative), identical=False, numericPass=False)
            if target.exists():
                if hashlib.sha256(path.read_bytes()).digest() == hashlib.sha256(target.read_bytes()).digest():
                    item["identical"] = True
                    item["numericPass"] = True
                else:
                    import numpy as np
                    from PIL import Image

                    a, b = np.asarray(Image.open(path).convert("RGBA")), np.asarray(Image.open(target).convert("RGBA"))
                    if a.shape == b.shape:
                        item["identical"] = bool(np.array_equal(a, b))
                        delta = np.abs(a.astype(np.float32) - b.astype(np.float32))
                        item.update(meanChannelError255=float(delta.mean()), maxChannelError255=float(delta.max()))
                        # 仅对实际人物区域的并集评分，避免透明像素或背景掩盖脸部及服装变化。
                        from skimage.metrics import structural_similarity

                        _, similarity = structural_similarity(
                            a[:, :, :3], b[:, :, :3], data_range=255, channel_axis=2, full=True
                        )
                        mask = np.any(a[:, :, :3] != a[0, 0, :3], axis=2) | np.any(b[:, :, :3] != b[0, 0, :3], axis=2)
                        error = float(delta[:, :, :3][mask].mean()) if mask.any() else 0
                        ssim = float(similarity.mean(axis=2)[mask].mean()) if mask.any() else 1
                        item.update(
                            changedPixels=int(np.any(delta != 0, axis=2).sum()),
                            foregroundError255=error,
                            foregroundSSIM=ssim,
                            numericPass=error <= 0.01 and ssim >= 0.9999,
                        )
            images.append(item)
        a, b = read(ref / "state.json"), read(act / "state.json")
        states[kind] = dict(
            bonesEqual=a["Bones"] == b["Bones"],
            renderersEqual=a["Renderers"] == b["Renderers"],
            diagnosticsEqual=(ref / "diagnostics.txt").read_text(encoding="utf-8-sig")
            == (act / "diagnostics.txt").read_text(encoding="utf-8-sig"),
            dynamicPosesEqual=all(
                read(p) == read(act / "dynamic" / p.name) for p in (ref / "dynamic").glob("pose-*.json")
            ),
        )
        print(
            f'{kind}: {sum(r["identical"] for r in images if r["sample"] == kind)} identical images; {states[kind]}',
            flush=True,
        )
    checks["visualRegressionTolerance"] = len(images) >= 112 and all(r["numericPass"] for r in images)
    checks["geometryMaterialsMotionUnchanged"] = all(all(s.values()) for s in states.values())
    return dict(
        passed=all(checks.values()),
        checks=checks,
        performance=phases,
        definitions=definitions,
        memory=memory,
        metadataBytes=cache_bytes,
        retainedBundles=[r["RetainedBundles"] for r in kernel],
        reused=reused,
        states=states,
        images=images,
        visualTolerance=dict(foregroundSSIM=0.9999, foregroundMeanChannelError255=0.01),
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for argument in ["before", "after", "reference", "actual", "output"]:
        parser.add_argument("--" + argument, type=Path, required=True)
    args = parser.parse_args()
    result = compare(args.before, args.after, args.reference, args.actual)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result["checks"], ensure_ascii=False, indent=2))
    for row in result["performance"]:
        if row["sample"] == "all":
            print(
                f'{row["phase"]}: {row["beforeMs"]:.0f} -> {row["afterMs"]:.0f} ms ({row["reductionPercent"]:.1f}% faster)'
            )
    print(
        f'{sum(r["identical"] for r in result["images"])}/{len(result["images"])} identical images; metadata {result["metadataBytes"]/1024**2:.2f} MiB'
    )
    raise SystemExit(0 if result["passed"] else 1)
