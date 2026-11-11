"""冻结可重定位的原生渲染输入与图像证据，不修改参考数据。"""

import argparse
import hashlib
import json
from pathlib import Path, PureWindowsPath


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest().upper()


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def relocate(value, root):
    if isinstance(value, str):
        # 仅重定位旧检出目录，不按文件名猜测游戏或资源路径。
        old = PureWindowsPath(value)
        parts = old.parts
        if "artifacts" in parts and "HS2-mangement" in parts:
            index = parts.index("HS2-mangement")
            if index + 1 < len(parts) and parts[index + 1] == "artifacts":
                return str(root.joinpath(*parts[index + 1 :]))
        return value
    if isinstance(value, list):
        return [relocate(item, root) for item in value]
    if isinstance(value, dict):
        return {key: relocate(item, root) for key, item in value.items()}
    return value


def prepare(root, catalog_path, output):
    catalog = load(catalog_path)
    if len(catalog) != 84 or len({entry["Id"] for entry in catalog}) != 84:
        raise ValueError("The accepted migration baseline must contain all 84 distinct cards.")
    settings = load(root / "artifacts/editor-baseline/pinned-final/baseline.json")
    pinned = root / "artifacts/editor-baseline/pinned-final/files.jsonl"
    if digest(pinned) != settings["ManifestSHA256"]:
        raise ValueError("Pinned resource manifest has changed.")
    if (
        settings["SSIM"] != 0.99
        or settings["Hz"] != 30
        or settings["Frames"] != 360
        or settings["Samples"] != 24
        or settings["Seed"] != 1979
    ):
        raise ValueError("The accepted visual thresholds/clock have changed.")
    if output.exists():
        raise FileExistsError("Use a new output directory; existing evidence is never overwritten.")
    entries = []
    definitions = []
    for original in catalog:
        entry = relocate(original, root)
        card = Path(entry["Card"])
        if digest(card) != entry["Hash"].upper():
            raise ValueError(f"Card hash changed: {entry['Id']}")
        definition_path = Path(entry["Definition"])
        definition = relocate(load(definition_path), root)
        if definition["CardHash"] != entry["Hash"]:
            raise ValueError(f"Definition belongs to a different card: {entry['Id']}")
        reference = Path(entry["Reference"])
        independent = root / "artifacts/editor-baseline/compare-v20" / entry["Id"] / "independent"
        names = ["fixture.json", "face-region.json", "state.json"]
        names += [
            f"{view}{suffix}"
            for view in ("front", "side", "back", "face")
            for suffix in (".png", "-background.png", "-pose.json")
        ]
        names += [
            f"dynamic/{stem}-{frame:03}.{ext}"
            for frame in range(15, 361, 15)
            for stem, ext in [("frame", "png"), ("pose", "json")]
        ]
        names += ["dynamic/complete.txt"]
        evidence = []
        for source, directory in [("game", reference), ("legacy-preview", independent)]:
            for name in names + (["complete.txt"] if source == "game" else ["run.json"]):
                path = directory / name
                evidence.append({"Source": source, "Name": name, "Path": str(path), "SHA256": digest(path)})
        entry["DefinitionSourceSHA256"] = digest(definition_path)
        entry["Definition"] = str(output / "definitions" / f"{entry['Id']}.json")
        entry["Evidence"] = evidence
        entries.append(entry)
        definitions.append((entry["Id"], definition_path))
    (output / "definitions").mkdir(parents=True)
    for key, definition_path in definitions:
        definition = relocate(load(definition_path), root)
        (output / "definitions" / f"{key}.json").write_text(
            json.dumps(definition, ensure_ascii=False), encoding="utf-8"
        )
    manifest = {
        "SchemaVersion": 1,
        "Cards": entries,
        "Settings": settings,
        "SourceCatalogSHA256": digest(catalog_path),
        "ResourceManifestSHA256": digest(pinned),
        "VisualParityVerified": False,
    }
    (output / "baseline.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False), encoding="utf-8")
    print(
        json.dumps(
            {
                "Cards": len(entries),
                "EvidenceFiles": sum(len(e["Evidence"]) for e in entries),
                "Baseline": str(output / "baseline.json"),
                "VisualParityVerified": False,
            }
        )
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--catalog", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = args.root.resolve()
    prepare(
        root, args.catalog or root / "artifacts/editor-baseline/catalog-v15/uniform-final.json", args.output.resolve()
    )
