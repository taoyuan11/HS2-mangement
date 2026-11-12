"""通过真实 stdio 进程检查 MCP；只修改配置指定的独立 Agent 数据与输出目录。"""

import argparse
import json
import pathlib
import queue
import subprocess
import threading
import time
import zipfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--executable", required=True)
    parser.add_argument("--config", required=True)
    parser.add_argument("--image", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument("--render", action="store_true")
    parser.add_argument("--finalize", action="store_true")
    parser.add_argument("--overlay", action="store_true")
    parser.add_argument("--overlay-target", choices=("face", "body", "clothes"), default="face")
    parser.add_argument("--prepare", action="store_true")
    parser.add_argument("--build-fixture", choices=("accessory", "hair", "clothes"))
    args = parser.parse_args()
    process = subprocess.Popen(
        [args.executable, "--config", args.config],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        creationflags=subprocess.CREATE_NO_WINDOW,
    )
    lines = queue.Queue()

    def read():
        for line in process.stdout:
            lines.put(line)
        lines.put(None)

    reader = threading.Thread(target=read, daemon=True)
    reader.start()
    request_id = 0
    report = {"pid": process.pid, "checks": [], "game_validation": "not_run"}

    def rpc(method, params=None):
        nonlocal request_id
        request_id += 1
        process.stdin.write(
            json.dumps({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params or {}}) + "\n"
        )
        process.stdin.flush()
        line = lines.get(timeout=60)
        if line is None:
            raise RuntimeError("MCP process exited: " + process.stderr.read())
        response = json.loads(line)
        assert response["id"] == request_id and "error" not in response, response
        return response["result"]

    def call(tool, values):
        result = rpc("tools/call", {"name": tool, "arguments": values})
        if result.get("isError"):
            raise RuntimeError(result["content"][0]["text"])
        return result["structuredContent"]

    def job(tool, values):
        state = call(tool, values)
        started = time.monotonic()
        print(f"Started {tool}: {state['job_id']}", flush=True)
        while state["status"] in ("queued", "running"):
            if time.monotonic() - started > 900:
                call("hs2_cancel_job", {"job_id": state["job_id"]})
                raise TimeoutError(tool)
            time.sleep(0.2)
            state = call("hs2_get_job", {"job_id": state["job_id"]})
        report[tool] = state
        assert state["status"] == "completed", state
        print(f"Completed {tool} in {time.monotonic() - started:.1f}s", flush=True)
        return state["result"]

    try:
        assert (
            rpc(
                "initialize",
                {
                    "protocolVersion": "2025-11-25",
                    "capabilities": {},
                    "clientInfo": {"name": "hs2-agent-check", "version": "1"},
                },
            )["protocolVersion"]
            == "2025-11-25"
        )
        assert len(rpc("tools/list")["tools"]) == 15
        rpc("ping")
        report["capabilities"] = call("hs2_capabilities", {})
        for sex in ("male", "female"):
            if not report["capabilities"]["base_cards"][sex]["valid"]:
                continue
            session = call(
                "hs2_begin_card",
                {
                    "sex": sex,
                    "name": "Agent协议检查",
                    "reference_image": {"path": str(pathlib.Path(args.image).resolve())},
                },
            )
            schema = call("hs2_get_schema", {"session_id": session["session_id"]})
            fields = [field for field in schema["fields"] if field["kind"] == 0 and field["category"] == "face"]
            assert fields
            field = fields[0]
            value = (field["minimum"] + field["maximum"]) / 2
            edited = call(
                "hs2_apply_changes",
                {
                    "session_id": session["session_id"],
                    "expected_revision": 0,
                    "changes": [{"path": field["path"], "value": value}],
                },
            )
            assert edited["revision"] == 1
            stale = rpc(
                "tools/call",
                {
                    "name": "hs2_restore_revision",
                    "arguments": {"session_id": session["session_id"], "expected_revision": 0, "revision": 0},
                },
            )
            assert stale["isError"] and "REVISION_CONFLICT" in stale["content"][0]["text"]
            restored = call(
                "hs2_restore_revision", {"session_id": session["session_id"], "expected_revision": 1, "revision": 0}
            )
            assert restored["revision"] == 2
            current_revision = 2
            report["checks"].append(
                {"sex": sex, "session_id": session["session_id"], "fields": len(schema["fields"]), "revision": 2}
            )
            if (args.prepare or args.build_fixture) and sex == "male":
                prepared = job("hs2_prepare_asset", {"session_id": session["session_id"], "expected_revision": 2})
                assert pathlib.Path(prepared["reference_model"]).is_file()
                assert pathlib.Path(prepared["blend"]).is_file()
                assert prepared["images"] and prepared["bones"]
                if args.build_fixture:
                    kind = args.build_fixture
                    revision = 2
                    if kind == "accessory":
                        edited = call(
                            "hs2_apply_changes",
                            {
                                "session_id": session["session_id"],
                                "expected_revision": revision,
                                "changes": [{"path": "accessory.parts[0].type", "value": 351}],
                            },
                        )
                        revision = edited["revision"]
                    target = {
                        "accessory": "accessory.parts[0].id",
                        "hair": "hair.parts[0].id",
                        "clothes": "clothes.parts[0].id",
                    }[kind]
                    built = job(
                        "hs2_build_asset",
                        {
                            "session_id": session["session_id"],
                            "expected_revision": revision,
                            "spec": {
                                "kind": kind,
                                "asset_key": "protocol-mesh-" + kind,
                                "name": "Agent构建测试",
                                "target_path": target,
                                "parent": "N_Head",
                                "clothing_name": "ct_clothesTop",
                                "source_script": str(
                                    pathlib.Path(__file__).with_name("BlenderAssetFixture.py").resolve()
                                ),
                                "template_material": "Assets/Modding Tool Runtime/Base Files/default.mat",
                                "shader": "hooh/ClotheReplicaOpaque",
                                "position": [0.25, 0, 0] if kind == "accessory" else [0, 0, 0],
                                "rotation": [0, 0, 15] if kind == "accessory" else [0, 0, 0],
                                "scale": [1.1, 1.1, 1.1] if kind == "accessory" else [1, 1, 1],
                            },
                        },
                    )
                    assert built["status"] == "applied", built
                    with zipfile.ZipFile(built["asset"]["path"]) as archive:
                        names = archive.namelist()
                        for name in names:
                            if not name.endswith(".csv"):
                                continue
                            csv_lines = archive.read(name).decode("utf-8-sig").splitlines()
                            assert len(csv_lines) >= 5 and csv_lines[0].isdigit() and csv_lines[1].isdigit(), csv_lines
                            header, row = csv_lines[3].split(","), csv_lines[4].split(",")
                            assert len(header) == len(row) and '"' not in row[header.index("MainAB")], row
                            bundle = row[header.index("MainAB")]
                            assert "abdata/" + bundle in names, (bundle, names)
                    current_revision = built["session"]["revision"]
            if args.render and sex == "male":
                values = {"session_id": session["session_id"], "expected_revision": current_revision}
                job("hs2_search_assets", {"session_id": session["session_id"], "category": 300, "limit": 2})
                result = job("hs2_render", dict(values, views=["front", "side", "back", "face"]))
                assert len(result["images"]) == 4
                job("hs2_validate", values)
                if args.overlay:
                    built = job(
                        "hs2_build_asset",
                        dict(
                            values,
                            spec={
                                "kind": "texture",
                                "asset_key": "protocol-test-overlay",
                                "target": args.overlay_target,
                                "clothing_name": "ct_clothesTop",
                                "source_image": str(pathlib.Path(args.image).resolve()),
                            },
                        ),
                    )
                    assert built["status"] == "applied", built
                    values["expected_revision"] = built["session"]["revision"]
                    job("hs2_render", dict(values, views=["front", "face"]))
                    job("hs2_validate", values)
                if args.finalize:
                    final = job(
                        "hs2_finalize_card",
                        dict(
                            values,
                            visual_assessment={
                                "passed": True,
                                "notes": "固定基准卡的保存管线测试；不代表参考图片相似度验收。",
                            },
                        ),
                    )
                    assert final["status"] == "complete", final
                    for folder in final["folders"]:
                        assert (pathlib.Path(folder) / "agent-report.json").is_file()
                        assert list((pathlib.Path(folder) / "UserData/chara/male").glob("*.png"))
        report["passed"] = True
        if args.render:
            pending = call("hs2_render", dict(values, sample_time=5))
            report["cancelled_job"] = call("hs2_cancel_job", {"job_id": pending["job_id"]})["job_id"]
    finally:
        try:
            process.stdin.close()
        except OSError:
            pass
        try:
            process.wait(timeout=45)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=15)
            report["forced_cleanup"] = True
        reader.join(timeout=2)
        report["exit_code"] = process.returncode
        report_path = pathlib.Path(args.report)
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    assert process.returncode == 0 and not report.get("forced_cleanup"), report
    print("PASS: MCP stdio, tools, image input, real card schemas, revisions, cleanup", flush=True)


if __name__ == "__main__":
    main()
