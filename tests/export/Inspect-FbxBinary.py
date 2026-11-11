"""直接检查二进制 FBX 几何数组，不调用导入器或引擎。"""

import argparse
import json
import struct
import zlib
from pathlib import Path

import numpy as np


class Reader:
    def __init__(self, path):
        self.data = Path(path).read_bytes()
        if not self.data.startswith(b"Kaydara FBX Binary  \x00\x1a\x00"):
            raise ValueError("A binary FBX is required.")
        self.position = 27
        self.wide = struct.unpack_from("<I", self.data, 23)[0] >= 7500

    def take(self, count):
        data = self.data[self.position : self.position + count]
        if len(data) != count:
            raise ValueError("Truncated FBX value.")
        self.position += count
        return data

    def scalar(self, fmt):
        return struct.unpack("<" + fmt, self.take(struct.calcsize("<" + fmt)))[0]

    def prop(self):
        kind = self.take(1).decode()
        scalars = dict(Y="h", C="?", I="i", F="f", D="d", L="q")
        if kind in scalars:
            return self.scalar(scalars[kind])
        if kind in "SR":
            return self.take(self.scalar("I"))
        if kind in "fdlib":
            count, encoding, size = self.scalar("I"), self.scalar("I"), self.scalar("I")
            data = self.take(size)
            if encoding:
                data = zlib.decompress(data)
            array = np.frombuffer(data, dtype=dict(f="<f4", d="<f8", l="<i8", i="<i4", b="u1")[kind])
            if len(array) != count:
                raise ValueError("FBX array count differs from its payload.")
            return array
        raise ValueError("Unsupported FBX property: " + kind)

    def node(self):
        width = "Q" if self.wide else "I"
        end, count, size = self.scalar(width), self.scalar(width), self.scalar(width)
        length = self.scalar("B")
        if not end:
            return None
        name = self.take(length).decode()
        properties = [self.prop() for _ in range(count)]
        children = []
        while self.position < end - (25 if self.wide else 13):
            child = self.node()
            if child:
                children.append(child)
        self.position = end
        return dict(name=name, properties=properties, children=children)

    def roots(self):
        roots = []
        while True:
            node = self.node()
            if not node:
                return roots
            roots.append(node)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("file", type=Path)
    parser.add_argument("--name", required=True)
    parser.add_argument("--index", type=int, default=146)
    args = parser.parse_args()
    objects = next(n for n in Reader(args.file).roots() if n["name"] == "Objects")["children"]
    for node in objects:
        if node["name"] != "Geometry" or args.name.encode() not in node["properties"][1]:
            continue
        fields = {child["name"]: child["properties"] for child in node["children"]}
        print(str(node["properties"]))
        print({k: len(v[0]) if isinstance(v[0], np.ndarray) else str(v) for k, v in fields.items()})
        if "Indexes" in fields and "Normals" in fields:
            indices, normals = fields["Indexes"][0], fields["Normals"][0].reshape(-1, 3)
            selected = np.flatnonzero(indices == args.index)
            print(json.dumps(dict(index=args.index, selected=selected.tolist(), normals=normals[selected].tolist())))
