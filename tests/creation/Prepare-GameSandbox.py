"""Stage creation-export dependencies into an existing independent game sandbox."""

import argparse
import hashlib
import json
import pathlib
import shutil
import os


def native_path(path):
    """Keep copied dependency trees readable beyond Windows MAX_PATH."""
    resolved = str(pathlib.Path(path).resolve())
    if os.name == "nt" and not resolved.startswith("\\\\?\\"):
        resolved = "\\\\?\\UNC\\" + resolved[2:] if resolved.startswith("\\\\") else "\\\\?\\" + resolved
    return pathlib.Path(resolved)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sandbox", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    sandbox = native_path(args.sandbox)
    marker = json.loads((sandbox / "agent-sandbox.json").read_text(encoding="utf-8"))
    source = native_path(marker["source"])
    if marker.get("purpose") != "hs2-agent-game-validation" or sandbox == source or source in sandbox.parents:
        raise ValueError("An existing independent, owned game sandbox is required")
    if not (sandbox / "HoneySelect2.exe").is_file():
        raise ValueError("The sandbox has no game executable")
    report = json.loads(native_path(args.report).read_text(encoding="utf-8"))
    copied = []
    for case in report["cases"]:
        generated = case["guid"].lower() + ".zipmod"
        for folder in case["packages"]:
            package = native_path(folder)
            for path in package.rglob("*"):
                relative = path.relative_to(package)
                if not path.is_file() or relative.parts[0] not in ("mods", "BepInEx", "abdata"):
                    continue
                # The service installs generated assets itself, including its ownership journal.
                if path.name.lower() == generated:
                    continue
                target = sandbox / relative
                if not native_path(target).is_relative_to(sandbox):
                    raise ValueError("Sandbox destination escapes through a link")
                target.parent.mkdir(parents=True, exist_ok=True)
                if not target.exists() or hashlib.sha256(path.read_bytes()).digest() != hashlib.sha256(target.read_bytes()).digest():
                    shutil.copy2(path, target)
                    copied.append(str(relative))
    print(json.dumps({"copied_dependencies": copied}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
