"""用独立 Agent 配置验证参考裁剪、原生对照图及视觉评估门禁；退出时回收本测试进程。"""

import argparse
import json
import pathlib
import queue
import subprocess
import threading
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--executable", required=True)
    parser.add_argument("--config", required=True)
    parser.add_argument("--image", required=True)
    parser.add_argument("--regions", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument("--session")
    parser.add_argument("--assessment")
    parser.add_argument("--probe", action="store_true")
    args = parser.parse_args()
    report = {"game_validation": "not_run"}
    process = subprocess.Popen(
        [args.executable, "--config", args.config],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        creationflags=subprocess.CREATE_NO_WINDOW,
    )
    report["pid"] = process.pid
    lines = queue.Queue()

    def read():
        for line in process.stdout:
            lines.put(line)
        lines.put(None)

    reader = threading.Thread(target=read, daemon=True)
    reader.start()
    sequence = 0

    def rpc(method, params=None):
        nonlocal sequence
        sequence += 1
        process.stdin.write(
            json.dumps({"jsonrpc": "2.0", "id": sequence, "method": method, "params": params or {}}) + "\n"
        )
        process.stdin.flush()
        line = lines.get(timeout=60)
        if line is None:
            raise RuntimeError("Agent unexpectedly exited")
        response = json.loads(line)
        assert response["id"] == sequence and "error" not in response, response
        return response["result"]

    def call(name, arguments):
        response = rpc("tools/call", {"name": name, "arguments": arguments})
        if response.get("isError"):
            raise RuntimeError(response["content"][0]["text"])
        result = response["structuredContent"]
        if name == "hs2_get_job" and result.get("status") == "completed":
            report.setdefault("inline_images", {})[result["tool"]] = sum(
                item["type"] == "image" for item in response["content"]
            )
        return result

    def job(name, arguments):
        state = call(name, arguments)
        deadline = time.monotonic() + 900
        print("Started " + name + ": " + state["job_id"], flush=True)
        while state["status"] in ("queued", "running"):
            if time.monotonic() > deadline:
                call("hs2_cancel_job", {"job_id": state["job_id"]})
                raise TimeoutError(name)
            time.sleep(0.25)
            state = call("hs2_get_job", {"job_id": state["job_id"]})
        assert state["status"] == "completed", state
        print("Completed " + name, flush=True)
        return state["result"]

    try:
        rpc("initialize")
        assert len(rpc("tools/list")["tools"]) == 17
        if args.session:
            session = call("hs2_get_session", {"session_id": args.session})
        else:
            session = call(
                "hs2_begin_card",
                {"sex": "female", "reference_image": {"path": str(pathlib.Path(args.image).resolve())}},
            )
        values = {"session_id": session["session_id"], "expected_revision": session["revision"]}
        report["session_id"] = session["session_id"]
        if not args.assessment:
            regions = json.loads(pathlib.Path(args.regions).read_text(encoding="utf-8"))
            report["references"] = call("hs2_set_reference_regions", dict(values, regions=regions))
            report["render"] = job("hs2_render", values)
            assert len(report["render"]["images"]) == 5
            assert len(report["render"]["comparison_images"]) == len(regions)
            assert report["render"]["background"] == "neutral"
            assert report["inline_images"]["hs2_render"] == len(regions)
            if args.probe:
                schema = call("hs2_get_schema", {"session_id": session["session_id"]})
                field = next(f for f in schema["fields"] if f["kind"] == 0 and f["category"] == "face")
                probe = job("hs2_probe_fields", dict(values, paths=[field["path"]], delta=0.03))
                report["probe"] = probe
                assert len(probe["images"]) == 1 and probe["images"][0]["columns"] == ["face", "face_side"]
                assert report["inline_images"]["hs2_probe_fields"] == 1
                current = call("hs2_get_session", {"session_id": session["session_id"]})
                assert current["revision"] == session["revision"]
                assert current["iterations"] == session["iterations"]
                assert current["last_render"]["render_id"] == report["render"]["render_id"]
        else:
            assessment = json.loads(pathlib.Path(args.assessment).read_text(encoding="utf-8"))
            assessment.update(values)
            assessment["render_id"] = session["last_render"]["render_id"]
            report["review"] = call("hs2_review_render", assessment)
            assert report["review"]["status"] == "needs_work", "此检查针对已观察到差异的草稿"
        report["finalization"] = job(
            "hs2_finalize_card",
            dict(values, visual_assessment={"passed": True, "notes": "检查不能绕过缺失或待修的逐项评估"}),
        )
        assert report["finalization"]["reason"] == "VISUAL_REVIEW_REQUIRED"
        report["passed"] = True
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=45)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=15)
            report["forced_cleanup"] = True
        reader.join(timeout=2)
        report["exit_code"] = process.returncode
        target = pathlib.Path(args.report)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    assert process.returncode == 0 and not report.get("forced_cleanup"), report
    print("PASS: reference comparison, current-revision review, draft retention, process cleanup", flush=True)


if __name__ == "__main__":
    main()
