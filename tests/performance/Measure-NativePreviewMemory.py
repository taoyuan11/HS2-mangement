"""Run the real-card renderer and sample Windows private/working-set memory (no dependencies)."""

import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
from pathlib import Path
import subprocess
import time


class Counters(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
        (name, ctypes.c_size_t)
        for name in (
            "PeakWorkingSetSize",
            "WorkingSetSize",
            "QuotaPeakPagedPoolUsage",
            "QuotaPagedPoolUsage",
            "QuotaPeakNonPagedPoolUsage",
            "QuotaNonPagedPoolUsage",
            "PagefileUsage",
            "PeakPagefileUsage",
            "PrivateUsage",
        )
    ]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("exe", "game", "data", "tpk", "card", "output", "physics"):
        parser.add_argument(name, type=Path)
    parser.add_argument("--comparison-card", type=Path)
    parser.add_argument("--timeout", type=float, default=180)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
    command = [str(getattr(args, name)) for name in ("exe", "game", "data", "tpk", "card", "output", "physics")]
    if args.comparison_card:
        command.append(str(args.comparison_card))
    peak_private = peak_working = samples = 0
    handle = None
    started = time.perf_counter()
    with (args.output / "process.log").open("w", encoding="utf-8") as log:
        process = subprocess.Popen(
            command, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW
        )
        try:
            handle = kernel.OpenProcess(0x410, False, process.pid)
            if not handle:
                raise ctypes.WinError(ctypes.get_last_error())
            while True:
                counters = Counters()
                counters.cb = ctypes.sizeof(counters)
                if psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
                    peak_private = max(peak_private, counters.PrivateUsage, counters.PeakPagefileUsage)
                    peak_working = max(peak_working, counters.PeakWorkingSetSize)
                    samples += 1
                elif process.poll() is None:
                    raise ctypes.WinError(ctypes.get_last_error())
                if process.poll() is not None:
                    break
                if time.perf_counter() - started > args.timeout:
                    raise TimeoutError(f"Renderer exceeded {args.timeout} seconds")
                time.sleep(0.02)
        finally:
            if process.poll() is None:
                process.kill()
            process.wait()
            if handle:
                kernel.CloseHandle(handle)
    result = {
        "Command": command,
        "ExitCode": process.returncode,
        "ExecutableSHA256": hashlib.sha256(args.exe.read_bytes()).hexdigest(),
        "Seconds": time.perf_counter() - started,
        "Samples": samples,
        "PeakPrivateBytes": peak_private,
        "PeakWorkingSetBytes": peak_working,
        "Frames": {
            str(p.relative_to(args.output)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(args.output.rglob("frame-*.png"))
        },
        "Loading": {
            str(p.relative_to(args.output)): json.loads(p.read_text(encoding="utf-8-sig"))
            for p in sorted(args.output.rglob("loading-performance.json"))
        },
    }
    (args.output / "memory-result.json").write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({k: v for k, v in result.items() if k not in ("Command", "Frames", "Loading")}, indent=2))
    if process.returncode:
        raise SystemExit(process.returncode)


if __name__ == "__main__":
    main()
