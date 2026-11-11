"""将 C++ 渲染器与冻结的人物定义和采集画面进行比较。

默认使用采集姿态以单独检查渲染；--physics 则按原静态夹具的时间推进 C++ 动画与布料。
两种模式均不覆盖生产人物卡准备流程或动态播放矩阵。"""

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import time

import numpy as np
from PIL import Image

spec = importlib.util.spec_from_file_location(
    "visual", Path(__file__).resolve().parents[1] / "unity" / "Compare-UnityVisual.py"
)
visual = importlib.util.module_from_spec(spec)
spec.loader.exec_module(visual)


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def pixels(path):
    with Image.open(path) as source:
        if source.size != (1280, 720):
            raise ValueError(f"Unexpected frame size: {path}")
        return np.asarray(source.convert("RGB"))


def compare(reference, actual, report_directory=None):
    report_directory = report_directory or actual
    report_directory.mkdir(parents=True, exist_ok=True)
    scope = load(actual / "scope.json")
    report = {
        "Reference": str(reference),
        "Actual": str(actual),
        "Views": [],
        "Errors": [],
        "Scope": scope["Scope"],
        "RuntimeSimulationExercised": scope.get("RuntimeSimulationExercised", False),
        "ProductionPreparationValidated": False,
        "RuntimeSimulationValidated": False,
        "VisualReviewPassed": False,
        "NumericPass": False,
    }
    region = load(reference / "face-region.json")
    for view in ("front", "side", "back", "face"):
        try:
            a, b, ba, bb = [
                pixels(p)
                for p in (
                    reference / f"{view}.png",
                    actual / f"{view}.png",
                    reference / f"{view}-background.png",
                    actual / f"{view}-background.png",
                )
            ]
            regions, mask, error = visual.measures(a, b, ba, bb, region if view == "face" else None)
            report["Views"].append({"View": view, "Regions": regions, "WholeFrameMeanError255": float(error.mean())})
            if not all(x["numericPass"] for x in regions.values()):
                Image.fromarray(np.clip(error * 8, 0, 255).astype("uint8")).save(
                    report_directory / f"{view}-difference-x8.png"
                )
            overview = Image.new("RGB", (1280, 360))
            overview.paste(Image.fromarray(a).resize((640, 360)), (0, 0))
            overview.paste(Image.fromarray(b).resize((640, 360)), (640, 0))
            overview.save(report_directory / f"{view}-comparison.jpg", quality=92)
        except (OSError, ValueError, KeyError) as error:
            report["Errors"].append(f"{view}: {error}")
    report["NumericPass"] = (
        not report["Errors"]
        and len(report["Views"]) == 4
        and all(r["numericPass"] for v in report["Views"] for r in v["Regions"].values())
    )
    (report_directory / "comparison.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--game", type=Path, required=True)
    parser.add_argument("--tpk", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--pose-root", type=Path)
    parser.add_argument(
        "--physics",
        type=Path,
        help="Advance the C++ runtime and cloth using the fixture timing instead of loading frozen poses.",
    )
    parser.add_argument(
        "--reference-source",
        choices=("legacy-preview", "game"),
        default="legacy-preview",
        help="Choose the frozen evidence source explicitly; legacy-preview is the application being migrated.",
    )
    parser.add_argument(
        "--comparison-output",
        type=Path,
        help="Store comparison reports separately without replacing previous evidence.",
    )
    parser.add_argument("--ids", nargs="*")
    parser.add_argument("--compare-only", action="store_true")
    args = parser.parse_args()
    executable_hash = hashlib.sha256(args.exe.read_bytes()).hexdigest().upper()
    args.output.mkdir(parents=True, exist_ok=True)
    reports = args.comparison_output or args.output
    reports.mkdir(parents=True, exist_ok=True)
    cards = load(args.baseline)["Cards"]
    if args.ids:
        cards = [c for c in cards if c["Id"] in args.ids]
        if {c["Id"] for c in cards} != set(args.ids):
            raise ValueError("Unknown requested card ID.")
    results = []
    started = time.monotonic()
    for card in cards:
        output = args.output / card["Id"]
        output.mkdir(exist_ok=True)
        reference = Path(
            next(
                e["Path"] for e in card["Evidence"] if e["Source"] == args.reference_source and e["Name"] == "front.png"
            )
        ).parent
        pose = args.pose_root / card["Id"] if args.pose_root else reference
        print(f"START {card['Id']} {card['Name']}", flush=True)
        if not args.compare_only:
            command = [
                str(args.exe),
                str(args.game),
                str(args.tpk),
                card["Definition"],
                str(pose),
                str(output),
                "all",
                "48",
            ]
            if args.physics:
                command.append(str(args.physics))
            with (output / "process.log").open("w", encoding="utf-8") as log:
                process = subprocess.Popen(
                    command,
                    stdout=log,
                    stderr=subprocess.STDOUT,
                    creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
                )
                print(f"PID {process.pid}", flush=True)
                try:
                    code = process.wait(timeout=900)
                except BaseException:
                    process.kill()
                    process.wait()
                    raise
            if code:
                results.append(
                    {
                        "Id": card["Id"],
                        "NumericPass": False,
                        "ProcessExitCode": code,
                        "Errors": ["Rendering process failed; see process.log."],
                    }
                )
                print(f"FAIL {card['Id']} exit={code}", flush=True)
                continue
        report = compare(reference, output, reports / card["Id"])
        report["ReferenceSource"] = args.reference_source
        report["Id"] = card["Id"]
        report["Name"] = card["Name"]
        report["Sex"] = card["Sex"]
        report["PoseSource"] = str(pose)
        report["Definition"] = card["Definition"]
        results.append(report)
        regions = [r for v in report["Views"] for r in v["Regions"].values()]
        print(
            json.dumps(
                {
                    "Id": card["Id"],
                    "NumericPass": report["NumericPass"],
                    "MinSSIM": min((r["ssim"] for r in regions), default=0),
                    "MaxMAE255": max((r["meanChannelError255"] for r in regions), default=255),
                    "Errors": report["Errors"],
                },
                ensure_ascii=False,
            ),
            flush=True,
        )
        (reports / "progress.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    aggregate = {
        "Scope": (
            "C++ runtime and GPU rendering with frozen definitions"
            if args.physics
            else "Frozen-pose C++ GPU rendering comparison"
        ),
        "Count": len(results),
        "Executable": str(args.exe.resolve()),
        "ExecutableSHA256": executable_hash,
        "ReferenceSource": args.reference_source,
        "NumericPass": len(results) == len(cards) and all(x["NumericPass"] for x in results),
        "VisualReviewPassed": False,
        "ProductionPreparationValidated": False,
        "RuntimeSimulationExercised": args.physics is not None,
        "RuntimeSimulationValidated": False,
        "Seconds": time.monotonic() - started,
        "Cards": results,
    }
    (reports / "report.json").write_text(json.dumps(aggregate, ensure_ascii=False, indent=2), encoding="utf-8")
    return 0 if aggregate["NumericPass"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
