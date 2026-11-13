"""交替运行两个 Release 检查程序，核对编辑结果并汇总耗时和私有内存。"""

import argparse
import hashlib
import json
import statistics
import subprocess
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--before", type=Path, required=True)
    parser.add_argument("--after", type=Path, required=True)
    parser.add_argument("--card", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--runs", type=int, default=5)
    args = parser.parse_args()
    if args.runs < 1:
        parser.error("--runs must be positive")
    args.output.mkdir(parents=True, exist_ok=False)
    executables = {name: path.resolve(strict=True) for name, path in (("before", args.before), ("after", args.after))}
    result = {
        "Configuration": "Release x64",
        "Scope": "Document open and 100 retained face parameter revisions; excludes UI, GPU and serialization time",
        "ExecutableSHA256": {name: hashlib.sha256(path.read_bytes()).hexdigest() for name, path in executables.items()},
        "RunsPerVersion": args.runs,
        "Samples": [],
    }
    for sample_index, card in enumerate(args.card):
        runs = {name: [] for name in executables}
        for iteration in range(args.runs):
            for name, executable in executables.items():
                output = args.output / f"sample-{sample_index}-{name}-{iteration}.json"
                # run 的超时会终止并等待本次子进程，不留下检查服务。
                subprocess.run(
                    [str(executable), str(card.resolve(strict=True)), str(output.resolve())],
                    check=True,
                    timeout=60,
                    stdout=subprocess.DEVNULL,
                    creationflags=subprocess.CREATE_NO_WINDOW,
                )
                runs[name].append(json.loads(output.read_text(encoding="utf-8")))
        for key in ("SourceSHA256", "ResultSHA256"):
            if len({run[key] for values in runs.values() for run in values}) != 1:
                raise RuntimeError(f"Different {key}: {card}")
        result["Samples"].append(
            {
                "Card": str(card),
                "Runs": runs,
                "Median": {
                    name: {
                        key: statistics.median(run[key] for run in values)
                        for key in (
                            "OpenMs",
                            "Edit100Ms",
                            "PrivateBeforeBytes",
                            "PrivateAfterBytes",
                            "HistoryGrowthBytes",
                        )
                    }
                    for name, values in runs.items()
                },
            }
        )
    report = args.output / "summary.json"
    report.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(report)


if __name__ == "__main__":
    main()
