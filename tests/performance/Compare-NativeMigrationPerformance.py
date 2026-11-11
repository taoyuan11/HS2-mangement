"""比较顺序执行、分辨率一致的生产路径，不设提升百分比门槛。"""

import argparse
import csv
import json
import math
from pathlib import Path
import statistics


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def median(values):
    finite = [v for v in values if v is not None and math.isfinite(v)]
    return statistics.median(finite) if finite else None


def p95(values):
    ordered = sorted(v for v in values if v is not None and math.isfinite(v))
    return ordered[math.ceil(len(ordered) * 0.95) - 1] if ordered else None


def validate(a, b, acceptance):
    if a.get("Backend") != "unity" or b.get("Backend") != "native" or not a.get("Complete") or not b.get("Complete"):
        raise ValueError("Both complete backend measurements are required")
    if not acceptance.get("VisualParityVerified") or len(acceptance.get("Cards", [])) != 84:
        raise ValueError(
            "The full numeric and observed-visual gate must pass before same-quality performance is reported"
        )
    for key in ["Machine", "OS", "LogicalProcessors", "Samples", "CardSHA256"]:
        if key not in a or a[key] != b.get(key):
            raise ValueError("Measurement inputs or machine differ: " + key)
    kinds = {s["Kind"] for s in a["Samples"]}
    if kinds != {"female", "male", "complex", "cloth"} or len(a["Samples"]) != 4 or len(a["CardSHA256"]) != 4:
        raise ValueError("All four fixed performance samples are required")
    for report in [a, b]:
        method = report["Method"]
        if (method.get("RenderWidth"), method.get("RenderHeight")) != (1280, 720):
            raise ValueError("Performance render size changed")
        if not method.get("NoPerformanceImprovementGate"):
            raise ValueError("Unexpected performance improvement gate")
        for phase, count in [("open-first", 4), ("switch", 8), ("reopen", 4), ("editor-open", 1), ("edit-response", 1)]:
            rows = [r for r in report["Measurements"] if r["Phase"] == phase]
            if len(rows) != count or (count > 1 and {r["Kind"] for r in rows} != kinds):
                raise ValueError("A performance phase or sample is missing: " + phase)
        if len(report["Measurements"]) != 18 or any(r["Viewport"] != [1280, 720] for r in report["Measurements"]):
            raise ValueError("Actual render sizes or phase count differ")
        if len(report["Steady"]) != 4 or {r["Sample"]["Kind"] for r in report["Steady"]} != kinds:
            raise ValueError("Steady-state performance samples are incomplete")
        if any(len(r["Observations"]) < 5 or len(r["FrameIntervalsMs"]) < 15 for r in report["Steady"]):
            raise ValueError("Insufficient steady-state measurement duration")
    camera = lambda r: {(v["Phase"], v["Kind"], v["Iteration"]): v["PreviewCamera"] for v in r["Measurements"]}
    if camera(a) != camera(b):
        raise ValueError("Performance cameras differ")


def compare(a, b, acceptance):
    validate(a, b, acceptance)
    rows = []

    def add(kind, metric, unit, old, new):
        rows.append(
            dict(
                Sample=kind,
                Metric=metric,
                Unit=unit,
                Unity=old,
                Native=new,
                Delta=None if old is None or new is None else new - old,
                DeltaPercent=None if old is None or new is None or old == 0 else (new - old) / old * 100,
            )
        )

    phases = ["open-first", "switch", "reopen", "editor-open", "edit-response"]
    for phase in phases:
        for kind in ["all"] + [s["Kind"] for s in a["Samples"]]:
            values = lambda r: [
                v["Milliseconds"]
                for v in r["Measurements"]
                if v["Phase"] == phase and (kind == "all" or v["Kind"] == kind)
            ]
            old, new = values(a), values(b)
            if not old:
                continue
            add(kind, phase + "-median", "ms", median(old), median(new))
            add(kind, phase + "-p95", "ms", p95(old), p95(new))
    issues = []
    for old in a["Steady"]:
        kind = old["Sample"]["Kind"]
        new = next(v for v in b["Steady"] if v["Sample"]["Kind"] == kind)
        add(kind, "frame-interval-median", "ms", median(old["FrameIntervalsMs"]), median(new["FrameIntervalsMs"]))
        add(kind, "frame-interval-p95", "ms", p95(old["FrameIntervalsMs"]), p95(new["FrameIntervalsMs"]))

        def metric(report, name):
            observations = report["Observations"]
            if name == "cpu":
                return [v["CpuPercent"] for v in observations]
            if name == "gpu":
                return [v["Gpu"]["GpuPercent"] for v in observations]
            if name == "private":
                return [(v["HostPrivateBytes"] + v["PlayerPrivateBytes"]) / 1048576 for v in observations]
            if name == "working":
                return [(v["HostWorkingBytes"] + v["PlayerWorkingBytes"]) / 1048576 for v in observations]
            return [
                None if v["Gpu"].get("DedicatedBytes") is None else v["Gpu"]["DedicatedBytes"] / 1048576
                for v in observations
            ]

        for name, unit in [("cpu", "%"), ("gpu", "%"), ("private", "MiB"), ("working", "MiB"), ("dedicated", "MiB")]:
            av, bv = metric(old, name), metric(new, name)
            add(kind, name + "-median", unit, median(av), median(bv))
            add(kind, name + "-p95", unit, p95(av), p95(bv))
            if median(av) is None or median(bv) is None:
                issues.append(kind + ": " + name + " counter unavailable")
    return dict(
        SchemaVersion=1,
        SameQualityPrerequisite=True,
        NoPerformanceImprovementGate=True,
        Machine=a["Machine"],
        LogicalProcessors=a["LogicalProcessors"],
        CardSHA256=a["CardSHA256"],
        ReferenceMeasuredUtc=a["MeasuredUtc"],
        NativeMeasuredUtc=b["MeasuredUtc"],
        Method=a["Method"],
        AvailabilityIssues=issues,
        Measurements=rows,
    )


def publish(report, output):
    output.mkdir(parents=True, exist_ok=True)
    (output / "performance-comparison.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf8"
    )
    with (output / "performance-comparison.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(
            stream, fieldnames=["Sample", "Metric", "Unit", "Unity", "Native", "Delta", "DeltaPercent"]
        )
        writer.writeheader()
        writer.writerows(report["Measurements"])
    lines = [
        "# 原生迁移性能差异",
        "",
        "同机、相同四组源卡、1280×720 渲染、顺序独立运行；全量视觉门槛已通过。没有性能提升比例门槛。",
        "",
        "首次加载使用空应用缓存；没有清空操作系统或存储缓存。完成帧间隔包含调度与实际呈现路径。CPU 为应用及其测试拥有的 Player 总占用，按全部逻辑处理器归一化；GPU 为这些进程最繁忙引擎占用。内存合计应用和 Player。",
        "",
        "加载统计跨四组卡；切卡每组两次；编辑使用原版女性卡。各组冷加载只有一次，不能用于稳定性推断。稳态使用实际记录的全部帧和每秒资源采样。差值为原生减参考，负值表示耗时或占用减少。",
        "",
        "| 样本 | 指标 | 单位 | 参考 WinUI | 原生 | 差值 | 变化 % |",
        "|---|---|---|---:|---:|---:|---:|",
    ]

    def fmt(value):
        return "不可用" if value is None else f"{value:.3f}"

    for row in report["Measurements"]:
        lines.append(
            "| "
            + " | ".join(
                [row["Sample"], row["Metric"], row["Unit"]]
                + [fmt(row[k]) for k in ["Unity", "Native", "Delta", "DeltaPercent"]]
            )
            + " |"
        )
    if report["AvailabilityIssues"]:
        lines += ["", "计数器缺项：" + "；".join(report["AvailabilityIssues"])]
    (output / "performance-comparison.md").write_text("\n".join(lines) + "\n", encoding="utf8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference", type=Path)
    parser.add_argument("native", type=Path)
    parser.add_argument("--acceptance", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = compare(read(args.reference), read(args.native), read(args.acceptance))
    publish(report, args.output)
    print(
        json.dumps(
            {key: value for key, value in report.items() if key not in ["Measurements", "CardSHA256", "Method"]},
            ensure_ascii=False,
        )
    )
