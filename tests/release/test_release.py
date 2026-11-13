"""发布协议、草稿可见性与重跑规则的离线验证。"""

import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    "release", Path(__file__).resolve().parents[2] / "scripts/release/publish_release.py"
)
release = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release)


class FakeGitHub:
    def __init__(self, existing=None, latest=None, fail_upload=False):
        self.existing = existing
        self.latest = latest
        self.fail_upload = fail_upload
        self.calls = []
        self.value = existing or {
            "id": 1,
            "draft": True,
            "assets": [],
            "upload_url": "https://uploads.github.com/repos/test/releases/1/assets{?name}",
            "html_url": "https://github.com/test/releases/v1.5.0",
        }

    def request(self, method, path, body=None, binary=False):
        self.calls.append((method, path, body))
        if method == "GET" and "/tags/" in path:
            return self.existing
        if method == "GET" and path.endswith("/latest"):
            return self.latest
        if method == "POST" and binary:
            if self.fail_upload:
                raise RuntimeError("upload interrupted")
            name = release.urllib.parse.parse_qs(release.urllib.parse.urlsplit(path).query)["name"][0]
            asset = {
                "id": len(self.value["assets"]) + 1,
                "name": name,
                "size": len(body),
                "state": "uploaded",
                "digest": "sha256:" + hashlib.sha256(body).hexdigest(),
            }
            self.value["assets"].append(asset)
            return asset
        if method == "PATCH":
            self.value.update(body)
        return self.value


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.name = "HS2Manager-1.5.0-cpp-win-x64.zip"
        archive = self.root / self.name
        archive.write_bytes(b"fixture")
        digest = hashlib.sha256(b"fixture").hexdigest()
        (self.root / (self.name + ".sha256")).write_text(f"{digest}  {self.name}\n", encoding="utf-8")
        (self.root / "update.json").write_text(
            json.dumps(
                {
                    "schemaVersion": 1,
                    "version": "1.5.0",
                    "platform": "win-x64",
                    "archive": self.name,
                    "bytes": 7,
                    "sha256": digest,
                }
            ),
            encoding="utf-8",
        )
        self.files = release.validate(self.root, "v1.5.0", "1.5.0")

    def tearDown(self):
        self.temp.cleanup()

    def test_tag_and_integrity(self):
        with self.assertRaises(ValueError):
            release.validate(self.root, "v1.5.1", "1.5.0")
        (self.root / self.name).write_bytes(b"changed")
        with self.assertRaises(ValueError):
            release.validate(self.root, "v1.5.0", "1.5.0")

    def test_numeric_version(self):
        self.assertGreater(release.version("1.10.0"), release.version("1.9.0"))
        for value in ["1.0", "01.2.3", "1.0.0-beta", "65536.0.0"]:
            with self.assertRaises(ValueError):
                release.version(value)

    def test_publish_only_after_verified_uploads(self):
        api = FakeGitHub()
        release.publish(api, self.files, "v1.5.0", "commit")
        self.assertEqual(len(api.value["assets"]), 3)
        self.assertFalse(api.value["draft"])
        self.assertEqual(api.calls[-1][0], "PATCH")
        self.assertEqual(api.calls[-1][2]["make_latest"], "true")

    def test_failure_keeps_draft(self):
        api = FakeGitHub(fail_upload=True)
        with self.assertRaises(RuntimeError):
            release.publish(api, self.files, "v1.5.0", "commit")
        self.assertTrue(api.value["draft"])
        self.assertFalse(any(call[0] == "PATCH" for call in api.calls))

    def test_old_version_does_not_replace_latest(self):
        api = FakeGitHub(latest={"tag_name": "v1.10.0"})
        release.publish(api, self.files, "v1.5.0", "commit")
        self.assertEqual(api.value["make_latest"], "false")

    def test_published_rerun_is_read_only(self):
        api = FakeGitHub()
        release.publish(api, self.files, "v1.5.0", "commit")
        again = FakeGitHub(existing=api.value)
        release.publish(again, self.files, "v1.5.0", "commit")
        self.assertTrue(all(call[0] == "GET" for call in again.calls))
        (self.root / self.name).write_bytes(b"different build")
        with self.assertRaises(RuntimeError):
            release.publish(again, self.files, "v1.5.0", "commit")

    def test_draft_rerun_reuses_verified_assets(self):
        api = FakeGitHub()
        release.publish(api, self.files, "v1.5.0", "commit")
        api.value["draft"] = True
        again = FakeGitHub(existing=api.value)
        release.publish(again, self.files, "v1.5.0", "commit")
        self.assertFalse(any(call[0] == "POST" for call in again.calls))


if __name__ == "__main__":
    unittest.main()
