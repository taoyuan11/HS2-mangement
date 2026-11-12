"""复制验收所需的游戏运行文件到独立目录，不修改正式游戏与用户资料。"""

import argparse
import concurrent.futures
import hashlib
import json
import pathlib
import shutil
import threading


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--game", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--plugin", help="Optional precompiled plugin; Test-GameAssets.ps1 compiles it when omitted")
    parser.add_argument("--reports", nargs="+", required=True)
    parser.add_argument("--reuse-base", action="store_true", help="Reuse the verified owned sandbox's game files")
    args = parser.parse_args()
    game, output = pathlib.Path(args.game).resolve(), pathlib.Path(args.output).resolve()
    if output == game or game in output.parents:
        raise ValueError("验收目录不能位于正式游戏内")
    output.mkdir(parents=True, exist_ok=True)
    marker = output / "agent-sandbox.json"
    if any(output.iterdir()) and not marker.exists():
        raise ValueError("不能接管已有的非验收目录")
    if args.reuse_base:
        previous = json.loads(marker.read_text(encoding="utf-8"))
        if (
            previous.get("purpose") != "hs2-agent-game-validation"
            or pathlib.Path(previous["source"]).resolve() != game
            or not (output / "HoneySelect2.exe").is_file()
        ):
            raise ValueError("只能复用相同游戏来源的已验证验收副本")
    marker.write_text(json.dumps({"source": str(game), "purpose": "hs2-agent-game-validation"}), encoding="utf-8")

    path_gate = threading.Lock()

    def copy(source):
        dest = output / source.relative_to(game)
        # Windows canonical-path resolution can race with parallel parent creation.
        with path_gate:
            resolved = dest.resolve()
            if not resolved.is_relative_to(output):
                raise ValueError(f"验收副本存在指向外部的文件链接：{dest} -> {resolved} (root {output})")
            dest.parent.mkdir(parents=True, exist_ok=True)
        if not dest.exists() or source.stat().st_size != dest.stat().st_size:
            shutil.copy2(source, dest)

    directories = [
        "HoneySelect2_Data",
        "MonoBleedingEdge",
        "DefaultData",
        "BepInEx/core",
        "BepInEx/plugins/HS2_BepisPlugins",
    ]
    directories += [
        "abdata/" + name
        for name in ("chara", "list", "gamedata", "title", "etcetra", "animator", "custom", "prefabs", "lut")
    ]
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        for directory in ([] if args.reuse_base else directories):
            source = game / directory
            if source.is_dir():
                list(pool.map(copy, [path for path in source.rglob("*") if path.is_file()]))
                print("Copied " + directory, flush=True)
        for name in (
            "HoneySelect2.exe",
            "UnityPlayer.dll",
            "UnityCrashHandler64.exe",
            "winhttp.dll",
            "doorstop_config.ini",
        ):
            copy(game / name)
        for source in (game / "abdata").iterdir():
            if source.is_file() and not source.suffix:
                copy(source)
    plugins = output / "BepInEx/plugins"
    plugins.mkdir(parents=True, exist_ok=True)
    if args.plugin and pathlib.Path(args.plugin).resolve() != (plugins / "GameAssetChecks.dll").resolve():
        shutil.copy2(args.plugin, plugins / "GameAssetChecks.dll")
    mods = output / "mods"
    if mods.exists():
        if mods.is_symlink() or not mods.resolve().is_relative_to(output):
            raise ValueError("验收 MOD 目录越界")
        shutil.rmtree(mods)
    items = []
    for report in args.reports:
        state = json.loads(pathlib.Path(report).read_text(encoding="utf-8"))
        built = state.get("hs2_build_asset", {}).get("result", {})
        asset = built.get("asset")
        if not asset:
            asset = next(reversed(built.get("session", {}).get("assets", [])), None)
        kind = asset["kind"] if asset else "texture"
        target = asset.get("spec", {}).get("target", "face") if asset else "face"
        for folder in state["hs2_finalize_card"]["result"]["folders"]:
            folder = pathlib.Path(folder)
            for path in folder.rglob("*"):
                if path.is_file() and path.parts[len(folder.parts)] in ("mods", "BepInEx"):
                    dest = output / path.relative_to(folder)
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    if (
                        not dest.exists()
                        or hashlib.sha256(path.read_bytes()).digest() != hashlib.sha256(dest.read_bytes()).digest()
                    ):
                        shutil.copy2(path, dest)
            card = next((folder / "UserData/chara").rglob("*.png"))
            items.append({"path": str(card), "kind": kind, "slot": 0, "target": target})
    if any(item["kind"] == "texture" for item in items):
        for name in ("HS2API.dll", "HS2_OverlayMods.dll"):
            copy(game / "BepInEx/plugins" / name)
    config = output / "BepInEx/config"
    config.mkdir(parents=True, exist_ok=True)
    (config / "BepInEx.cfg").write_text(
        "[Caching]\nEnableAssemblyCache = false\n\n"
        "[Preloader.Entrypoint]\nAssembly = Assembly-CSharp.dll\nType = TitleScene\nMethod = Start\n\n"
        "[Logging.Disk]\nWriteUnityLog = true\n",
        encoding="utf-8",
    )
    (output / "agent-test-input.json").write_text(
        json.dumps({"items": items}, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    (output / "agent-test-input.tsv").write_text(
        "\n".join(
            item["path"] + "\t" + item["kind"] + "\t" + str(item["slot"]) + "\t" + item["target"] for item in items
        ),
        encoding="utf-8",
    )
    print("Prepared independent game sandbox with " + str(len(items)) + " cards", flush=True)


if __name__ == "__main__":
    main()
