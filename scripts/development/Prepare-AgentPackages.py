"""将固定的 ModdingTool 依赖嵌入本机工程，避免旧版 UPM 的联网解析差异。"""

import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess
import tarfile
import tempfile

PACKAGES = {
    "com.domybest.mybox": ("IL-Modding-Tool/MyBox", "f8b702ef4468ebe168067d07d0cbde7247d5cebc"),
    "com.hooah.moddingtool": ("IL-Modding-Tool/Modding-Tool-Unity-Package", "a87812f6e48391b9f65bb23a381e6b77aed8ee7d"),
    "com.hooah.moddingtool.examples": (
        "IL-Modding-Tool/Modding-Tool-Examples",
        "a5ec676e2f064ce825485d6051c645739289b63f",
    ),
    "com.neuecc.unirx": ("HooahPlugins/UniRx", "ded49824f4b1e531d535392425802b2a2a6dcf31"),
    "com.yasirkula.runtimepreviewgenerator": (
        "yasirkula/UnityRuntimePreviewGenerator",
        "87cbffc2d4457860199a5de1fae547315062f41a",
    ),
    "com.unity.postprocessing": (
        "https://cdn.packages.unity.com/tarballs/com.unity.postprocessing/com.unity.postprocessing-2.3.0/74626fc10d7252d3e4b3390eaacdb5f2023088f6.tgz",
        "2.3.0",
    ),
    "com.unity.package-manager-ui": (
        "https://packages.unity.com/com.unity.package-manager-ui/-/com.unity.package-manager-ui-2.0.12.tgz",
        "2.0.12",
    ),
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", required=True)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    project = pathlib.Path(args.project).absolute()
    if str(project).startswith("\\\\"):
        project = pathlib.Path("\\\\?\\UNC\\" + str(project)[2:])
    elif not str(project).startswith("\\\\?\\"):
        project = pathlib.Path("\\\\?\\" + str(project))
    packages = project / "Packages"
    manifest_file = packages / "manifest.json"
    manifest = json.loads(manifest_file.read_text(encoding="utf-8-sig"))
    lock_file = packages / "agent-package-lock.json"
    lock = json.loads(lock_file.read_text(encoding="utf-8")) if lock_file.exists() else {}
    for name, (source, revision) in PACKAGES.items():
        target = packages / name
        expected = "version" if source.startswith("https:") else "revision"
        record = lock.get(name, {})
        if not (target / "package.json").is_file() or record.get(expected) != revision:
            if args.check:
                raise ValueError("本地依赖未准备或版本不匹配：" + name)
            if target.exists():
                raise ValueError("保留已有依赖，请检查版本后处理：" + str(target))
            with tempfile.TemporaryDirectory(prefix="hs2-agent-package-") as temp:
                temp = pathlib.Path(temp)
                archive = temp / "package.tgz"
                url = (
                    source if source.startswith("https:") else f"https://codeload.github.com/{source}/tar.gz/{revision}"
                )
                subprocess.run(
                    [
                        "curl.exe",
                        "--fail",
                        "--location",
                        "--retry",
                        "3",
                        "--retry-all-errors",
                        "--max-time",
                        "300",
                        "--silent",
                        "--show-error",
                        "--output",
                        str(archive),
                        url,
                    ],
                    check=True,
                )
                checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
                if (
                    name == "com.unity.postprocessing"
                    and checksum != "e073d47579d5b2278964853e584829c3c967ab69563a0d163c993cd163b6aaaf"
                ):
                    raise ValueError("后处理包校验失败")
                unpacked = temp / "unpacked"
                with tarfile.open(archive) as bundle:
                    bundle.extractall(unpacked, filter="data")
                roots = [path.parent for path in unpacked.glob("*/package.json")]
                if len(roots) != 1:
                    raise ValueError("依赖包布局不符合预期：" + name)
                metadata = json.loads((roots[0] / "package.json").read_text(encoding="utf-8-sig"))
                if metadata["name"] != name:
                    raise ValueError("依赖包标识不匹配：" + name)
                shutil.copytree(roots[0], target)
                lock[name] = {expected: revision, "sha256": checksum, "source": source}
                lock_file.write_text(json.dumps(lock, indent=2), encoding="utf-8")
        manifest["dependencies"].pop(name, None)
    if not args.check:
        manifest_file.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print("PASS: seven pinned embedded ModdingTool dependencies")


if __name__ == "__main__":
    main()
