"""在独立 Agent 数据目录检查全部新制作类型；始终回收本测试启动的服务。"""

import argparse
import copy
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
    parser.add_argument("--report", required=True)
    parser.add_argument("--sex", choices=["male", "female"], default="male")
    parser.add_argument("--kinds", nargs="+", default=["catalog_texture", "layout", "head"])
    parser.add_argument("--session")
    parser.add_argument("--disable-accessory-slot", type=int, nargs="*", default=[])
    parser.add_argument("--exercise-recovery", action="store_true")
    parser.add_argument("--complete", action="store_true", help="Build remaining enabled targets and test final export")
    parser.add_argument("--enable-test-accessory", action="store_true")
    parser.add_argument("--rebuild-kinds", nargs="*", default=[])
    args = parser.parse_args()
    target = pathlib.Path(args.report).resolve()
    target.parent.mkdir(parents=True, exist_ok=True)
    report = {"checks": [], "game_validation": "not_run"}
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
        assert line is not None, "Agent exited unexpectedly"
        response = json.loads(line)
        assert response["id"] == sequence and "error" not in response, response
        return response["result"]

    def call(name, arguments):
        result = rpc("tools/call", {"name": name, "arguments": arguments})
        assert not result.get("isError"), result
        return result["structuredContent"]

    def wait_job(state, expected="completed"):
        deadline = time.monotonic() + 1200
        while state["status"] in ("queued", "running"):
            if time.monotonic() >= deadline:
                call("hs2_cancel_job", {"job_id": state["job_id"]})
                raise TimeoutError(state["job_id"])
            time.sleep(0.25)
            state = call("hs2_get_job", {"job_id": state["job_id"]})
        assert state["status"] == expected, state
        return state

    def job(name, arguments):
        state = wait_job(call(name, arguments))
        return state["result"]

    try:
        rpc("initialize")
        tools = rpc("tools/list")["tools"]
        assert "hs2_search_assets" not in {t["name"] for t in tools}
        removed = rpc("tools/call", {"name": "hs2_search_assets", "arguments": {}})
        assert removed["isError"] and "ASSET_SEARCH_REMOVED" in removed["content"][0]["text"]
        session = (
            call("hs2_get_session", {"session_id": args.session})
            if args.session
            else call(
                "hs2_begin_card",
                {"sex": args.sex, "reference_image": {"path": str(pathlib.Path(args.image).resolve())}},
            )
        )
        report["session_id"] = session["session_id"]
        values = {"session_id": session["session_id"], "expected_revision": session["revision"]}
        if args.disable_accessory_slot:
            session = call(
                "hs2_apply_changes",
                dict(
                    values,
                    changes=[
                        {"path": f"accessory.parts[{slot}].type", "value": 350} for slot in args.disable_accessory_slot
                    ],
                ),
            )
            values["expected_revision"] = session["revision"]
            report["disabled_accessory_slots"] = args.disable_accessory_slot
        if args.exercise_recovery:
            pending = call("hs2_render", dict(values, sample_time=5))
            call("hs2_cancel_job", {"job_id": pending["job_id"]})
            report["cancelled_job"] = wait_job(pending, "cancelled")
            assert (
                call("hs2_get_session", {"session_id": session["session_id"]})["revision"]
                == values["expected_revision"]
            )
        if args.enable_test_accessory:
            session = call(
                "hs2_apply_changes", dict(values, changes=[{"path": "accessory.parts[0].type", "value": 351}])
            )
            values["expected_revision"] = session["revision"]
        schema = call("hs2_get_schema", {"session_id": session["session_id"]})
        assets = [f for f in schema["fields"] if f["kind"] in (3, 5)]
        assert assets and all(f.get("creation") for f in assets)
        report["targets"] = [f["creation"] for f in assets]
        original = assets[0]
        rejected = rpc(
            "tools/call",
            {
                "name": "hs2_apply_changes",
                "arguments": dict(
                    values, changes=[{"path": original["path"], "value": f"{original['creation']['category']}::0"}]
                ),
            },
        )
        assert rejected.get("isError") and "ASSET_CREATION_REQUIRED" in rejected["content"][0]["text"], rejected
        assert call("hs2_get_session", {"session_id": session["session_id"]})["revision"] == values["expected_revision"]
        seen = set()
        for field in assets:
            creation = field["creation"]
            kind, path = creation["kind"], field["path"]
            category = creation["category"]
            if args.complete:
                pending = job("hs2_validate", values)["creation_issues"]
                if path not in {issue["target_path"] for issue in pending} and not (
                    kind in args.rebuild_kinds and creation["enabled"]
                ):
                    continue
            elif kind not in args.kinds or (kind, category) in seen:
                continue
            seen.add((kind, category))
            spec = {
                "schema_version": 3,
                "kind": kind,
                "target_path": path,
                "asset_key": f"new-type-{kind}-{category}",
                "name": f"Generated {category}",
            }
            if kind == "catalog_texture":
                spec["textures"] = [
                    {"field": name, "path": str(pathlib.Path(args.image).resolve())}
                    for name in creation["required_textures"]
                ]
            elif kind == "layout":
                if category == 8:
                    spec["layout_values"] = {
                        "CenterScale": 4,
                        "AddScale": 2,
                        "CenterX": 0.5,
                        "CenterY": 0.5,
                        "MoveX": 0.25,
                        "MoveY": 0.25,
                    }
                else:
                    spec["layout"] = [0.4, 0.5, 0.3, 0.6]
            elif kind == "head":
                prepared = job("hs2_prepare_asset", dict(values, target_path=path))
                head_path = pathlib.Path(prepared["source_head"])
                head = json.loads(head_path.read_text(encoding="utf-8"))
                for mesh in head["meshes"]:
                    if "head" in mesh["name"].lower():
                        for vertex in mesh["positions"]:
                            vertex[0] *= 1.001
                edited = target.parent / (args.sex + "-head.json")
                edited.write_text(json.dumps(head), encoding="utf-8")
                spec["source_head"] = str(edited)
                if args.exercise_recovery:
                    invalid = copy.deepcopy(head)
                    invalid["meshes"][0]["indices"][0] += 1
                    invalid_path = target.parent / (args.sex + "-head-invalid.json")
                    invalid_path.write_text(json.dumps(invalid), encoding="utf-8")
                    failures = []
                    for _ in range(4):
                        state = wait_job(
                            call("hs2_build_asset", dict(values, spec=dict(spec, source_head=str(invalid_path)))),
                            "failed",
                        )
                        assert "HEAD_TOPOLOGY" in state["error"], state
                        failures.append(state)
                    report["head_topology_rejections"] = failures
                    assert (
                        call("hs2_get_session", {"session_id": session["session_id"]})["revision"]
                        == values["expected_revision"]
                    )
            else:
                fixture = pathlib.Path(__file__).parents[1] / "fixtures/native-assets" / (kind + ".json")
                spec.update(json.loads(fixture.read_text(encoding="utf-8")))
                spec.update(schema_version=3, target_path=path)
                spec.pop("template_asset_id", None)
                spec["asset_key"] = "fixture-" + path
                if kind == "hair":
                    suffix = "_" + str(category)
                    for bone in spec["recipe"]["bones"]:
                        bone["name"] += suffix
                        if bone["parent"].startswith("native_strand_"):
                            bone["parent"] += suffix
                    for operation in spec["recipe"]["operations"]:
                        if "bones" in operation:
                            operation["bones"] = [bone + suffix for bone in operation["bones"]]
                    spec["dynamic_roots"] = [bone + suffix for bone in spec["dynamic_roots"]]
                if kind == "clothes":
                    spec["masks"] = [{"field": "OverBodyMask", "path": str(pathlib.Path(args.image).resolve())}]
            print("Building " + path, flush=True)
            result = job("hs2_build_asset", dict(values, spec=spec))
            report["checks"].append({"path": path, "kind": kind, "result": result})
            target.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            assert result["status"] == "applied", result
            values["expected_revision"] = result["session"]["revision"]
            if args.exercise_recovery and kind == "head":
                # 已生成的头部成为下一轮模板，再次使用旧参考也必须可继续构建。
                prepared = job("hs2_prepare_asset", dict(values, target_path=path))
                head = json.loads(pathlib.Path(prepared["source_head"]).read_text(encoding="utf-8"))
                head["meshes"][0]["positions"][0][0] += 0.001
                edited.write_text(json.dumps(head), encoding="utf-8")
                for _ in range(2):
                    rebuilt = job("hs2_build_asset", dict(values, spec=spec))
                    assert rebuilt["status"] == "applied", rebuilt
                    values["expected_revision"] = rebuilt["session"]["revision"]
                report["head_template_reuse"] = True
            if args.exercise_recovery and path == "face.moleLayout":
                revision = values["expected_revision"]
                replacement = job("hs2_build_asset", dict(values, spec=dict(spec, layout=[0.2, 0.3, 0.4, 0.5])))
                assert replacement["status"] == "applied", replacement
                values["expected_revision"] = replacement["session"]["revision"]
                restored = call("hs2_restore_revision", dict(values, revision=revision))
                values["expected_revision"] = restored["revision"]
                validation = job("hs2_validate", values)
                assert path not in {issue["target_path"] for issue in validation["creation_issues"]}, validation
                report["layout_restore"] = True
        report["validation"] = job("hs2_validate", values)
        if args.complete:
            assert report["validation"]["created_assets"] and not report["validation"]["creation_issues"], report[
                "validation"
            ]
            call("hs2_set_reference_regions", dict(values, regions=[{"view": "front", "rect": [0, 0, 1, 1]}]))
            render = job("hs2_render", values)
            # 仅验证导出协议，固定测试图和程序生成的简单几何不代表人物还原质量。
            call(
                "hs2_review_render",
                dict(
                    values,
                    render_id=render["render_id"],
                    unseen_regions="自动化固定样本，无人物相似度声明",
                    areas=[
                        {
                            "area": area,
                            "status": "matched",
                            "reference": "固定协议测试输入",
                            "observed": "模拟调用方评估，仅验证完整导出链路",
                        }
                        for area in ["face", "hair", "body", "outfit", "accessories", "materials"]
                    ],
                ),
            )
            report["hs2_finalize_card"] = {
                "result": job(
                    "hs2_finalize_card",
                    dict(values, visual_assessment={"passed": True, "notes": "固定协议测试，不代表参考人物相似度验收"}),
                )
            }
            assert report["hs2_finalize_card"]["result"]["status"] == "complete", report["hs2_finalize_card"]
        else:
            assert report["validation"]["creation_issues"], "Existing enabled assets must still block completion"
        report["session"] = call("hs2_get_session", {"session_id": session["session_id"]})
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
        target.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    assert process.returncode == 0 and not report.get("forced_cleanup"), report
    print("PASS: new asset types, native rendering, provenance and process cleanup", flush=True)


if __name__ == "__main__":
    main()
