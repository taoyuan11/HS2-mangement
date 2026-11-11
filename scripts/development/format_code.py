"""统一自有源码的排版；第三方源码与冻结测试数据保留原样。"""

import argparse
import json
import re
import subprocess
from pathlib import Path
from xml.dom import Node, minidom

import black
from cmakelang.configuration import Configuration
from cmakelang.format.__main__ import process_file

ROOT = Path(__file__).resolve().parents[2]
CPP_SUFFIXES = {".c", ".cpp", ".h", ".hlsl"}
XML_SUFFIXES = {".xaml", ".xml", ".props", ".targets", ".vcxproj", ".manifest", ".slnx"}


def project_files():
    result = subprocess.run(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
        cwd=ROOT,
        check=True,
        capture_output=True,
    )
    for name in sorted(set(result.stdout.decode("utf-8").split("\0"))):
        if not name:
            continue
        file = ROOT / name
        if any(part.lower() in {"thirdparty", "third_party", "licenses", "fixtures"} for part in file.parts):
            continue
        if file.is_file():
            yield file


def format_cpp(source, file, clang_format):
    def clang(text, filename):
        result = subprocess.run(
            [clang_format, "--style=file", "--assume-filename=" + str(filename)],
            input=text.encode("utf-8"),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=True,
            cwd=ROOT,
        )
        return result.stdout.decode("utf-8")

    # 原生着色器内嵌在原始字符串中，需要单独展开其中的函数和代码块。
    source = re.sub(
        r'R"hlsl\((.*?)\)hlsl"',
        lambda match: 'R"hlsl(\n' + clang(match[1], ROOT / "shader.hlsl").strip() + '\n)hlsl"',
        source,
        flags=re.DOTALL,
    )
    return clang(source, file)


def format_xml(source):
    document = minidom.parseString(source)

    def remove_layout_whitespace(node):
        # 保留实际文本和属性，只移除元素间原有的排版空白。
        for child in list(node.childNodes):
            if child.nodeType == Node.TEXT_NODE and not child.data.strip():
                node.removeChild(child)
            elif child.nodeType == Node.ELEMENT_NODE:
                remove_layout_whitespace(child)

    remove_layout_whitespace(document)
    result = document.toprettyxml(indent="    ", encoding="utf-8").decode("utf-8")
    lines = []
    for line in result.splitlines():
        if len(line) <= 120 or line.lstrip().startswith(("<?", "<!--")):
            lines.append(line)
            continue
        match = re.fullmatch(r"(\s*)<([\w.:-]+)(\s+[^<>]*?)(/?>)", line)
        if not match:
            lines.append(line)
            continue
        indent, tag, attributes, end = match.groups()
        attrs = re.findall(r'[\w.:-]+="[^"]*"', attributes)
        if not attrs:
            lines.append(line)
            continue
        lines.append(indent + "<" + tag)
        lines.extend(indent + "    " + attribute for attribute in attrs[:-1])
        lines.append(indent + "    " + attrs[-1] + end)
    return "\n".join(lines) + "\n"


def format_source(file, source, clang_format, cmake_config):
    if file.suffix in CPP_SUFFIXES or file.name.endswith(".h.in"):
        return format_cpp(source, file, clang_format)
    if file.suffix == ".py":
        mode = black.Mode(line_length=120, target_versions={black.TargetVersion.PY310})
        return black.format_str(source, mode=mode)
    if file.suffix == ".cmake" or file.name == "CMakeLists.txt":
        return process_file(cmake_config, source)[0]
    if file.suffix in XML_SUFFIXES:
        return format_xml(source)
    if file.name == "packages.lock.json":
        return json.dumps(json.loads(source), ensure_ascii=False, indent=4) + "\n"
    if file.suffix == ".sln":
        return source.expandtabs(4).rstrip() + "\n"
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="仅检查，不写入文件")
    parser.add_argument("--clang-format", default="clang-format", help="clang-format 可执行文件")
    arguments = parser.parse_args()
    settings = json.loads((ROOT / ".cmake-format.json").read_text(encoding="utf-8"))
    cmake_config = Configuration(**settings)
    changed = []
    checked = 0
    for file in project_files():
        if file.suffix not in CPP_SUFFIXES | XML_SUFFIXES | {".py", ".cmake", ".sln"} and file.name not in {
            "CMakeLists.txt",
            "packages.lock.json",
            "PbrShader.h.in",
        }:
            continue
        original_bytes = file.read_bytes()
        source = original_bytes.decode("utf-8-sig").replace("\r\n", "\n")
        formatted = format_source(file, source, arguments.clang_format, cmake_config)
        if formatted is None:
            continue
        formatted = "\n".join(line.rstrip() for line in formatted.splitlines()).rstrip() + "\n"
        checked += 1
        desired_bytes = (
            formatted.replace("\n", "\r\n").encode("utf-8") if file.suffix == ".sln" else formatted.encode("utf-8")
        )
        if desired_bytes != original_bytes:
            changed.append(file.relative_to(ROOT).as_posix())
            if not arguments.check:
                with file.open("w", encoding="utf-8", newline="\r\n" if file.suffix == ".sln" else "\n") as output:
                    output.write(formatted)
    if arguments.check and changed:
        print("以下文件需要格式化：\n" + "\n".join(changed))
        return 1
    action = "检查" if arguments.check else "格式化"
    print(f"已{action} {checked} 个文件，需调整 {len(changed)} 个。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
