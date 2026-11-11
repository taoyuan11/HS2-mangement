"""从二进制 FBX 的基础层和稀疏差量中比较全部形变法线。

避免读取 SDK 展开数组中未初始化的条目。数量、索引、蒙皮和变换由 NativeFbxCompare
分别检查；本检查独立读取文件中的实际表示。"""

import argparse
import importlib.util
import json
from collections import defaultdict
from pathlib import Path

import numpy as np

spec = importlib.util.spec_from_file_location("binary_fbx", Path(__file__).with_name("Inspect-FbxBinary.py"))
binary = importlib.util.module_from_spec(spec)
spec.loader.exec_module(binary)


def fields(node):
    return {child["name"]: child for child in node["children"]}


def label(node):
    return node["properties"][1].split(b"\0", 1)[0].decode("utf8")


def shapes(path):
    roots = binary.Reader(path).roots()
    objects = next(n for n in roots if n["name"] == "Objects")["children"]
    connections = next(n for n in roots if n["name"] == "Connections")["children"]
    objects = {node["properties"][0]: node for node in objects}
    parents, children = defaultdict(list), defaultdict(list)
    for connection in connections:
        p = connection["properties"]
        if connection["name"] == "C" and p[0] == b"OO":
            parents[p[1]].append(p[2])
            children[p[2]].append(p[1])
    result = {}
    for channel_id, channel in objects.items():
        if channel["name"] != "Deformer" or channel["properties"][2] != b"BlendShapeChannel":
            continue
        deformers = [objects[p] for p in parents[channel_id] if p in objects and objects[p]["name"] == "Deformer"]
        if len(deformers) != 1:
            raise ValueError("A shape channel has ambiguous ownership.")
        meshes = [
            objects[p]
            for p in parents[deformers[0]["properties"][0]]
            if p in objects and objects[p]["name"] == "Geometry"
        ]
        if len(meshes) != 1:
            raise ValueError("A shape deformer has ambiguous mesh ownership.")
        mesh = meshes[0]
        models = [objects[p] for p in parents[mesh["properties"][0]] if p in objects and objects[p]["name"] == "Model"]
        if len(models) != 1:
            raise ValueError("A mesh has ambiguous model ownership.")
        normal_layer = fields(mesh)["LayerElementNormal"]
        normal_fields = fields(normal_layer)
        if normal_fields["MappingInformationType"]["properties"][0] not in (
            b"ByVertice",
            b"ByVertex",
            b"ByControlPoint",
        ):
            raise ValueError("This check requires control-point normals.")
        if normal_fields["ReferenceInformationType"]["properties"][0] != b"Direct":
            raise ValueError("This check requires direct base normal layers.")
        base = normal_fields["Normals"]["properties"][0].reshape(-1, 3)
        for frame, shape_id in enumerate(
            s for s in children[channel_id] if s in objects and objects[s]["name"] == "Geometry"
        ):
            shape = fields(objects[shape_id])
            indices = shape["Indexes"]["properties"][0]
            if len(set(indices.tolist())) != len(indices) or (
                len(indices) and (indices.min() < 0 or indices.max() >= len(base))
            ):
                raise ValueError("Invalid sparse shape normal indices.")
            output = base.copy()
            if "Normals" in shape:
                delta = shape["Normals"]["properties"][0].reshape(-1, 3)
                if len(delta) != len(indices):
                    raise ValueError("Sparse shape normal count differs from its indices.")
                output[indices] += delta
            key = (label(models[0]), label(channel), frame)
            if key in result:
                raise ValueError("Duplicate shape identity.")
            result[key] = output
    return result


def compare(reference, actual, output):
    expected, native = shapes(reference), shapes(actual)
    errors, rows = [], []
    if expected.keys() != native.keys():
        errors.append("The full set of mesh/channel/frame identities differs.")
    for key, a in expected.items():
        if key not in native:
            continue
        b = native[key]
        if a.shape != b.shape:
            errors.append("Shape normal dimensions differ: " + str(key))
            continue
        difference = np.abs(a - b)
        passed = bool(np.isfinite(a).all() and np.isfinite(b).all() and difference.max(initial=0) <= 0.0001)
        rows.append(
            dict(
                Model=key[0],
                Channel=key[1],
                Frame=key[2],
                Vertices=len(a),
                MaximumError=float(difference.max(initial=0)),
                Passed=passed,
            )
        )
    report = dict(
        SchemaVersion=1,
        Method="Base control-point normals plus all sparse file deltas; omitted indices retain base normals.",
        DirectionTolerance=0.0001,
        ExpectedFrames=len(expected),
        ActualFrames=len(native),
        Frames=rows,
        Errors=errors,
        Passed=not errors and bool(rows) and all(r["Passed"] for r in rows),
        VisualParityVerified=False,
    )
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf8")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference", type=Path)
    parser.add_argument("actual", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    result = compare(args.reference, args.actual, args.output)
    print(
        json.dumps(
            dict(
                Passed=result["Passed"],
                Frames=len(result["Frames"]),
                Worst=max((r["MaximumError"] for r in result["Frames"]), default=None),
                Failures=[r for r in result["Frames"] if not r["Passed"]],
            )
        )
    )
    raise SystemExit(0 if result["Passed"] else 1)
