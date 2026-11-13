"""校验并发布正式便携包；附件全部上传后才公开，已公开的版本禁止覆盖。"""

import argparse
import hashlib
import json
import os
import re
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

REPOSITORY = "taoyuan11/HS2-mangement"


def version(value):
    if not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", value):
        raise ValueError("Invalid stable version")
    parts = tuple(map(int, value.split(".")))
    if max(parts) > 65535:
        raise ValueError("Version component too large")
    return parts


def validate(directory, tag, expected):
    version(expected)
    if tag != "v" + expected:
        raise ValueError("Tag does not match VERSION")
    manifest = json.loads((directory / "update.json").read_text(encoding="utf-8-sig"))
    name = f"HS2Manager-{expected}-cpp-win-x64.zip"
    archive = directory / name
    with archive.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if manifest != {
        "schemaVersion": 1,
        "version": expected,
        "platform": "win-x64",
        "archive": name,
        "bytes": archive.stat().st_size,
        "sha256": digest,
    }:
        raise ValueError("Release manifest does not match the archive")
    if (directory / (name + ".sha256")).read_text(encoding="utf-8-sig").strip() != f"{digest}  {name}":
        raise ValueError("SHA-256 file does not match")
    return [archive, directory / (name + ".sha256"), directory / "update.json"]


class GitHub:
    def __init__(self, token):
        self.token = token

    def request(self, method, path, body=None, binary=False):
        url = path if path.startswith("https://") else "https://api.github.com" + path
        parsed = urllib.parse.urlsplit(url)
        if parsed.scheme != "https" or parsed.hostname not in {"api.github.com", "uploads.github.com"}:
            raise ValueError("Unexpected GitHub API host")
        headers = {
            "Authorization": "Bearer " + self.token,
            "User-Agent": "HS2Manager-release",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
        }
        if body is not None:
            headers["Content-Type"] = "application/octet-stream" if binary else "application/json"
            body = body if binary else json.dumps(body).encode()
        request = urllib.request.Request(url, data=body, method=method, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=180) as response:
                data = response.read()
                return json.loads(data) if data else None
        except urllib.error.HTTPError as error:
            if method == "GET" and error.code == 404:
                return None
            raise RuntimeError(f"GitHub {method} failed: HTTP {error.code}") from error


def matches(assets, files):
    by_name = {asset["name"]: asset for asset in assets}
    for file in files:
        asset = by_name.get(file.name)
        with file.open("rb") as stream:
            digest = "sha256:" + hashlib.file_digest(stream, "sha256").hexdigest()
        if (
            not asset
            or asset.get("state") != "uploaded"
            or asset.get("size") != file.stat().st_size
            or asset.get("digest") != digest
        ):
            return False
    return True


def publish(api, files, tag, commit):
    base = f"/repos/{REPOSITORY}/releases"
    release = api.request("GET", base + "/tags/" + urllib.parse.quote(tag, safe=""))
    if release and not release["draft"]:
        if release.get("prerelease") or not matches(release["assets"], files):
            raise RuntimeError("Published version differs; publish a new version instead of overwriting")
        return release["html_url"]
    if not release:
        release = api.request(
            "POST",
            base,
            {
                "tag_name": tag,
                "target_commitish": commit,
                "name": tag,
                "draft": True,
                "prerelease": False,
                "generate_release_notes": True,
            },
        )
    for file in files:
        previous = next((asset for asset in release.get("assets", []) if asset["name"] == file.name), None)
        if previous and matches([previous], [file]):
            continue
        if previous:
            api.request("DELETE", base + "/assets/" + str(previous["id"]))
        upload = release["upload_url"].split("{", 1)[0] + "?" + urllib.parse.urlencode({"name": file.name})
        result = api.request("POST", upload, file.read_bytes(), binary=True)
        if not matches([result], [file]):
            raise RuntimeError("Uploaded asset failed verification; release remains a draft")
    checked = api.request("GET", base + "/" + str(release["id"]))
    if not matches(checked["assets"], files):
        raise RuntimeError("Release assets incomplete; release remains a draft")
    latest = api.request("GET", base + "/latest")
    make_latest = latest is None or version(tag[1:]) > version(latest["tag_name"].removeprefix("v"))
    result = api.request(
        "PATCH",
        base + "/" + str(release["id"]),
        {"draft": False, "prerelease": False, "make_latest": "true" if make_latest else "false"},
    )
    return result["html_url"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--commit", required=True)
    args = parser.parse_args()
    expected = (Path(__file__).resolve().parents[2] / "VERSION").read_text().strip()
    files = validate(args.directory, args.tag, expected)
    print(publish(GitHub(os.environ["GH_TOKEN"]), files, args.tag, args.commit))
