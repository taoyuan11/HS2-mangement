"""汇总固定目录的数值和人工观察证据，不自动授予批准。"""

import argparse
import csv
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path

SAMPLES = {v + ".png" for v in ["front", "side", "back", "face"]} | {
    f"dynamic/frame-{f:03}.png" for f in range(15, 361, 15)
}


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def sample_name(group):
    return f"dynamic/frame-{group['frame']:03}.png" if "frame" in group else group["view"] + ".png"


def inspect(entry, catalog, execution):
    row = dict(
        Id=entry["Id"],
        Name=entry["Name"],
        CardSHA256=entry["Hash"],
        Samples=0,
        MinimumSSIM=None,
        MaximumMeanChannelError255=None,
        NumericPass=False,
        VisualReviewPassed=False,
        RenderScopePassed=False,
        Passed=False,
        ReviewStatus="missing",
        Errors=[],
    )
    try:
        if sha(Path(entry["Card"])) != entry["Hash"]:
            raise ValueError("Source card hash changed")
        if (
            not execution
            or not execution.get("ExecutionPassed")
            or not execution.get("MaterialStatePassed")
            or execution.get("Errors")
        ):
            raise ValueError("Execution or material-state evidence did not pass")
        comparison = read(catalog / entry["Id"] / "comparison.json")
        scope = read(catalog / entry["Id"] / "render-scope.json")
        if (
            scope.get("CapturedAmbientProbe", True)
            or not scope.get("EffectsExecuted")
            or scope.get("UnsupportedEnabledEffects") != []
            or not scope.get("DynamicCaptured")
            or set(scope.get("CapturedViews", [])) != {"front", "side", "back", "face"}
        ):
            raise ValueError("Native capture scope is incomplete or an enabled effect did not execute")
        if comparison.get("renderScope") != scope:
            raise ValueError("Render execution evidence changed after numeric comparison")
        row["RenderScopePassed"] = True
        row["EnabledEffects"] = scope.get("EnabledEffects", [])
        row["Cloth"] = scope.get("Cloth", False)
        row["KnownUnsupportedFeaturesOutsideThisCapture"] = scope.get("PendingImplementation", [])
        if comparison["thresholds"] != dict(ssim=0.99, meanChannelError255=2):
            raise ValueError("Acceptance thresholds changed")
        groups = comparison["views"] + comparison["dynamic"]
        if (
            len(comparison["views"]) != 4
            or len(comparison["dynamic"]) != 24
            or {sample_name(v) for v in groups} != SAMPLES
        ):
            raise ValueError("Four static and 24 dynamic samples are required")
        if any(not group["regions"] for group in groups):
            raise ValueError("A sample has no measured regions")
        regions = [region for group in groups for region in group["regions"].values()]
        if any(
            not math.isfinite(region["ssim"]) or not math.isfinite(region["meanChannelError255"]) for region in regions
        ):
            raise ValueError("Non-finite numeric measurement")
        row["Samples"] = len(groups)
        row["MinimumSSIM"] = min(region["ssim"] for region in regions)
        row["MaximumMeanChannelError255"] = max(region["meanChannelError255"] for region in regions)
        row["NumericPass"] = (
            comparison.get("numericPass") is True
            and not comparison["errors"]
            and all(region["ssim"] >= 0.99 and 0 <= region["meanChannelError255"] <= 2 for region in regions)
        )
        if not row["NumericPass"]:
            row["Errors"].append("Numeric gate failed")
        review = read(catalog / "visual-review" / entry["Id"] / "evidence.json")
        row["ReviewStatus"] = review.get("ReviewStatus", "missing")
        if review.get("CardSHA256") != entry["Hash"] or review.get("Id") != entry["Id"]:
            raise ValueError("Review card identity changed")
        samples = review.get("Samples", [])
        if len(samples) != 28 or {v["Image"] for v in samples} != SAMPLES:
            raise ValueError("Review evidence is incomplete")
        frozen = {v["Name"]: v for v in entry["Evidence"] if v["Source"] == "legacy-preview"}
        for sample in samples:
            name = sample["Image"]
            if (
                sample["ReferenceSHA256"] != frozen[name]["SHA256"]
                or sha(Path(frozen[name]["Path"])) != sample["ReferenceSHA256"]
                or sha(catalog / entry["Id"] / name) != sample["NativeSHA256"]
            ):
                raise ValueError("Reviewed image hash changed: " + name)
        for name in ["overview.png", "local.png"]:
            if not (catalog / "visual-review" / entry["Id"] / name).is_file():
                raise ValueError("Review image missing: " + name)
        extra = review.get("AdditionalLocalReview", {}).get("File")
        if extra and (Path(extra).name != extra or not (catalog / "visual-review" / entry["Id"] / extra).is_file()):
            raise ValueError("Additional local review image is missing or invalid")
        row["VisualReviewPassed"] = (
            row["ReviewStatus"] == "passed" and bool(review.get("ReviewedUtc")) and bool(review.get("ReviewNotes"))
        )
        if not row["VisualReviewPassed"]:
            row["Errors"].append("Observed local visual review has not passed")
        row["Passed"] = row["NumericPass"] and row["VisualReviewPassed"] and not row["Errors"]
        # 原始采集结果不能自行批准；
        # 只有数值检查和人工观察均通过后，才在此确认采集阶段的要求。
        row["ValidatedCaptureRequirements"] = scope.get("PendingValidation", []) if row["Passed"] else []
        row["ClothParityVerified"] = row["Passed"] if row["Cloth"] else None
    except (OSError, ValueError, KeyError, TypeError) as error:
        row["Errors"].append(str(error))
    return row


def aggregate(baseline, catalog):
    execution = read(catalog / "report.json")
    entries = baseline["Cards"]
    ids = [entry["Id"] for entry in entries]
    executed = execution["Cards"]
    executions = {row["Id"]: row for row in executed}
    full = (
        len(ids) == 84
        and len(set(ids)) == 84
        and len(executed) == 84
        and len(executions) == 84
        and set(ids) == set(executions)
        and execution.get("Mode") == "frames"
        and execution.get("Complete") is True
        and execution.get("RunStopped") is False
        and execution.get("NumericPass") is True
    )
    rows = [inspect(entry, catalog, executions.get(entry["Id"])) for entry in entries]
    return dict(
        SchemaVersion=1,
        GeneratedUtc=datetime.now(timezone.utc).isoformat(),
        Scope="Only the fixed 84 cards and their frozen resource configuration; threshold parity, not pixel identity.",
        Thresholds=dict(SSIM=0.99, MeanChannelError255=2),
        FullCatalog=full,
        NumericPass=full and all(row["NumericPass"] for row in rows),
        VisualReviewPassed=full and all(row["VisualReviewPassed"] and not row["Errors"] for row in rows),
        VisualParityVerified=full and all(row["Passed"] for row in rows),
        Cards=rows,
    )


def publish(report, catalog):
    (catalog / "acceptance-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf8")
    fields = [
        "Id",
        "Name",
        "Samples",
        "MinimumSSIM",
        "MaximumMeanChannelError255",
        "RenderScopePassed",
        "NumericPass",
        "ReviewStatus",
        "VisualReviewPassed",
        "Passed",
        "CardSHA256",
        "Errors",
    ]
    with (catalog / "acceptance-report.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(dict(row, Errors="; ".join(row["Errors"])) for row in report["Cards"])
    lines = [
        "# 原生预览逐卡验收",
        "",
        report["Scope"],
        "",
        "固定门槛：各适用区域 SSIM ≥ 0.99，平均通道误差 ≤ 2/255；四视角 + 24 个动态采样，局部复核单独判定。",
        "",
        f"完整目录：{report['FullCatalog']}；数值通过：{report['NumericPass']}；视觉复核通过：{report['VisualReviewPassed']}。",
        "",
        "| 卡 ID | 最低 SSIM | 最大平均误差 /255 | 复核 | 通过 |",
        "|---|---:|---:|---|---|",
    ]
    for row in report["Cards"]:
        score = "—" if row["MinimumSSIM"] is None else f"{row['MinimumSSIM']:.8f}"
        error = "—" if row["MaximumMeanChannelError255"] is None else f"{row['MaximumMeanChannelError255']:.6f}"
        lines.append(
            f"| [{row['Id']}](visual-review/{row['Id']}/overview.png) | {score} | {error} | {row['ReviewStatus']} | {row['Passed']} |"
        )
    (catalog / "acceptance-report.md").write_text("\n".join(lines) + "\n", encoding="utf8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("catalog", type=Path)
    args = parser.parse_args()
    report = aggregate(read(args.baseline), args.catalog)
    publish(report, args.catalog)
    print(json.dumps({key: value for key, value in report.items() if key != "Cards"}, ensure_ascii=False))
    raise SystemExit(0 if report["VisualParityVerified"] else 1)
