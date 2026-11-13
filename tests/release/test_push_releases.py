"""使用隔离仓库和本地裸远端验证发布入口，不访问 GitHub 或用户 Git 配置。"""

import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

SOURCE = Path(__file__).resolve().parents[2]


class PushReleaseCases:
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="hs2-release-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo = self.root / "发布 仓库"
        self.remote = self.root / "远端 仓库.git"
        self.outside = self.root / "其他 工作目录"
        self.outside.mkdir()
        self.env = {key: value for key, value in os.environ.items() if not key.upper().startswith("GIT_")}
        self.env.update(
            GIT_CONFIG_NOSYSTEM="1",
            GIT_CONFIG_GLOBAL=os.devnull,
            GIT_TERMINAL_PROMPT="0",
            GIT_ALLOW_PROTOCOL="file",
            GCM_INTERACTIVE="Never",
        )
        self.command(["git", "init", "--bare", "--initial-branch=main", str(self.remote)])
        self.command(["git", "init", "--initial-branch=main", str(self.repo)])
        self.git("config", "user.name", "Release Test")
        self.git("config", "user.email", "release@example.invalid")
        self.git("config", "core.autocrlf", "false")
        self.git("config", "commit.gpgSign", "false")
        self.git("config", "tag.gpgSign", "false")
        for name in ("push-releases.cmd", "scripts/release/Push-Releases.ps1", "scripts/build/Version.ps1"):
            target = self.repo / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(SOURCE / name, target)
        self.write("VERSION", "1.5.0\n")
        self.write(".gitignore", "ignored/\n")
        self.write("keep.txt", "original\n")
        self.write("delete.txt", "delete me\n")
        self.git("add", "-A")
        self.git("commit", "-m", "initial")
        self.git("remote", "add", "origin", str(self.remote))
        self.git("push", "origin", "main")
        self.initial = self.git("rev-parse", "HEAD")

    def command(self, arguments, check=True):
        # 每个子进程同步回收；超时连同其派生的 Git 进程一起终止。
        with subprocess.Popen(
            arguments,
            cwd=self.outside,
            env=self.env,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            creationflags=subprocess.CREATE_NO_WINDOW,
        ) as process:
            try:
                output, _ = process.communicate(timeout=60)
            except subprocess.TimeoutExpired:
                subprocess.run(
                    ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                    capture_output=True,
                    timeout=15,
                    creationflags=subprocess.CREATE_NO_WINDOW,
                    check=False,
                )
                process.kill()
                process.communicate(timeout=15)
                raise
            result = subprocess.CompletedProcess(arguments, process.returncode, output)
        if check:
            self.assertEqual(result.returncode, 0, result.stdout)
        return result

    def git(self, *arguments, directory=None):
        return self.command(["git", "-C", str(directory or self.repo), *arguments]).stdout.strip()

    def write(self, name, content):
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8", newline="\n")
        return path

    def release(self, *arguments, cmd=False):
        if cmd:
            command = self.cmd_line("push-releases.cmd", *arguments)
        else:
            command = [
                self.shell,
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(self.repo / "scripts/release/Push-Releases.ps1"),
                *arguments,
            ]
        return self.command(command, check=False)

    def cmd_line(self, script, *arguments):
        # /s /c 需要一层包裹整个命令的引号，否则多个带空格参数会拆散脚本路径。
        quoted = subprocess.list2cmdline([str(self.repo / script), *arguments])
        return f'cmd.exe /d /s /c "{quoted}"'

    def snapshot(self):
        return (
            (self.repo / "VERSION").read_bytes(),
            self.git("rev-parse", "HEAD"),
            self.git("status", "--porcelain"),
            self.git("diff", "--binary"),
            self.git("diff", "--cached", "--binary"),
            self.git("tag", "--list"),
            self.git("show-ref", directory=self.remote),
        )

    def assert_preflight_rejected(self, *arguments, cmd=False):
        before = self.snapshot()
        result = self.release(*arguments, cmd=cmd)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertEqual(self.snapshot(), before, result.stdout)
        return result

    def test_commits_all_changes_and_pushes_only_main_and_requested_tag(self):
        self.write("keep.txt", "staged\n")
        self.git("add", "keep.txt")
        self.write("keep.txt", "latest\n")
        self.write("新增 文件.txt", "new file\n")
        (self.repo / "delete.txt").unlink()
        self.write("ignored/private.txt", "must stay local\n")
        self.git("tag", "-a", "unrelated", "-m", "unrelated")
        self.git("branch", "feature")
        self.git("config", "push.followTags", "true")
        self.git("config", "remote.origin.mirror", "true")
        result = self.release("v1.5.1")
        self.assertEqual(result.returncode, 0, result.stdout)
        head = self.git("rev-parse", "HEAD")
        self.assertEqual(self.git("rev-parse", "main", directory=self.remote), head)
        self.assertEqual(self.git("rev-parse", "v1.5.1^{}", directory=self.remote), head)
        self.assertEqual(self.git("cat-file", "-t", "v1.5.1"), "tag")
        self.assertEqual(self.git("log", "-1", "--format=%s"), "chore(release): v1.5.1")
        self.assertEqual(self.git("show", "HEAD:VERSION"), "1.5.1")
        self.assertEqual(self.git("show", "HEAD:keep.txt"), "latest")
        self.assertEqual(self.git("show", "HEAD:新增 文件.txt"), "new file")
        tree = self.git("ls-tree", "-r", "--name-only", "HEAD")
        self.assertNotIn("delete.txt", tree)
        self.assertNotIn("ignored", tree)
        self.assertEqual(self.git("status", "--porcelain"), "")
        refs = self.git("for-each-ref", "--format=%(refname)", directory=self.remote).splitlines()
        self.assertEqual(refs, ["refs/heads/main", "refs/tags/v1.5.1"])
        self.assertEqual((self.repo / "VERSION").read_bytes(), b"1.5.1\n")

    def test_clean_same_version_tags_current_commit(self):
        result = self.release("1.5.0")
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.initial)
        self.assertEqual(self.git("rev-parse", "v1.5.0^{}", directory=self.remote), self.initial)

    def test_upper_version_boundary(self):
        result = self.release("65535.65535.65535")
        self.assertEqual(result.returncode, 0, result.stdout)

    def test_invalid_arguments_do_not_mutate(self):
        self.write("keep.txt", "pending work\n")
        for arguments in (
            (),
            ("1.5",),
            ("01.5.1",),
            ("1.5.1-beta",),
            ("65536.0.0",),
            ("999999999999999999999999999.0.0",),
            ("V1.5.1",),
            ("1.5.1\n",),
            ("１.5.1",),
            ("1.5.1", "extra"),
        ):
            with self.subTest(arguments=arguments):
                self.assert_preflight_rejected(*arguments)

    def test_cmd_entrypoint_forwards_arguments_and_exit_code(self):
        self.assert_preflight_rejected(cmd=True)
        self.assert_preflight_rejected("invalid", cmd=True)
        result = self.release("v1.5.1", cmd=True)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(self.git("show", "v1.5.1:VERSION", directory=self.remote), "1.5.1")

    def test_non_main_and_detached_head_rejected(self):
        self.git("checkout", "-b", "feature")
        self.assert_preflight_rejected("1.5.1")
        self.git("checkout", "--detach")
        self.assert_preflight_rejected("1.5.1")

    def test_unfinished_operations_rejected(self):
        for state in ("MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "sequencer"):
            with self.subTest(state=state):
                path = self.repo / ".git" / state
                if state.endswith("HEAD"):
                    path.write_text(self.initial + "\n", encoding="ascii")
                else:
                    path.mkdir()
                try:
                    self.assert_preflight_rejected("1.5.1")
                finally:
                    if path.is_dir():
                        path.rmdir()
                    else:
                        path.unlink()

    def test_unmerged_index_rejected(self):
        self.git("checkout", "-b", "conflict")
        self.write("keep.txt", "other branch\n")
        self.git("commit", "-am", "other branch")
        self.git("checkout", "main")
        self.write("keep.txt", "main branch\n")
        self.git("commit", "-am", "main branch")
        merged = self.command(
            ["git", "-C", str(self.repo), "merge", "conflict"],
            check=False,
        )
        self.assertNotEqual(merged.returncode, 0, merged.stdout)
        self.assertTrue(self.git("ls-files", "--unmerged"))
        # 单独覆盖索引冲突检查，避免仅命中 MERGE_HEAD 的提前退出。
        (self.repo / ".git/MERGE_HEAD").unlink()
        self.assert_preflight_rejected("1.5.1")

    def test_existing_local_and_remote_tags_rejected(self):
        self.git("tag", "v1.5.1")
        self.assert_preflight_rejected("1.5.1")
        self.git("push", "origin", "refs/tags/v1.5.1")
        self.git("tag", "-d", "v1.5.1")
        self.assert_preflight_rejected("1.5.1")

    def test_remote_ahead_and_diverged_rejected(self):
        peer = self.root / "peer"
        self.command(["git", "clone", str(self.remote), str(peer)])
        self.git("config", "user.name", "Peer", directory=peer)
        self.git("config", "user.email", "peer@example.invalid", directory=peer)
        (peer / "remote.txt").write_text("remote", encoding="ascii")
        self.git("add", "-A", directory=peer)
        self.git("commit", "-m", "remote change", directory=peer)
        self.git("push", "origin", "main", directory=peer)
        self.assert_preflight_rejected("1.5.1")
        self.write("local.txt", "local change")
        self.git("add", "-A")
        self.git("commit", "-m", "local change")
        self.assert_preflight_rejected("1.5.1")

    def test_missing_origin_rejected(self):
        self.git("remote", "remove", "origin")
        self.assert_preflight_rejected("1.5.1")

    def test_missing_identity_rejected(self):
        self.git("config", "--unset", "user.email")
        self.git("config", "user.useConfigOnly", "true")
        self.assert_preflight_rejected("1.5.1")

    def test_unavailable_remote_rejected_before_commit(self):
        self.git("remote", "set-url", "origin", str(self.root / "unavailable.git"))
        self.assert_preflight_rejected("1.5.1")

    def test_preflight_uses_push_url(self):
        self.git("remote", "set-url", "--push", "origin", str(self.root / "unavailable.git"))
        self.assert_preflight_rejected("1.5.1")

    def test_multiple_push_urls_rejected(self):
        self.git("config", "--add", "remote.origin.pushurl", str(self.remote))
        self.git("config", "--add", "remote.origin.pushurl", str(self.root / "other.git"))
        self.assert_preflight_rejected("1.5.1")

    def test_remote_without_main_rejected(self):
        self.git("branch", "-m", "main", "other", directory=self.remote)
        self.assert_preflight_rejected("1.5.1")

    def test_build_and_package_scripts_parse_in_both_shells(self):
        paths = (
            "scripts/build/Build-Cpp.ps1",
            "scripts/package/Publish-Cpp.ps1",
            "scripts/package/RuntimeLanguages.ps1",
        )
        for name in paths:
            source = str(SOURCE / name).replace("'", "''")
            command = (
                f"$tokens = $null; $errors = $null; "
                f"$null = [Management.Automation.Language.Parser]::ParseFile('{source}', "
                "[ref]$tokens, [ref]$errors); if ($errors.Count) { throw ($errors.Message -join [Environment]::NewLine) }"
            )
            self.command([self.shell, "-NoProfile", "-NonInteractive", "-Command", command])

    def test_commit_failure_keeps_changes_without_tag_or_push(self):
        self.write(".git/hooks/pre-commit", "#!/bin/sh\nexit 1\n")
        result = self.release("1.5.1")
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.initial)
        self.assertEqual(self.git("rev-parse", "main", directory=self.remote), self.initial)
        self.assertEqual(self.git("tag", "--list"), "")
        self.assertEqual(self.git("show", ":VERSION"), "1.5.1")

    def test_push_rejection_is_atomic_and_recovery_preserves_commit(self):
        hook = self.remote / "hooks/update"
        hook.write_text('#!/bin/sh\ncase "$1" in refs/tags/*) exit 1;; esac\nexit 0\n', encoding="ascii")
        result = self.release("1.5.1")
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn("push --atomic --no-follow-tags origin", result.stdout)
        head = self.git("rev-parse", "HEAD")
        self.assertNotEqual(head, self.initial)
        self.assertEqual(self.git("rev-parse", "v1.5.1^{}"), head)
        self.assertEqual(self.git("rev-parse", "main", directory=self.remote), self.initial)
        self.assertEqual(self.git("tag", "--list", directory=self.remote), "")
        self.assert_preflight_rejected("1.5.1")
        hook.unlink()
        self.git(
            "-c",
            "remote.origin.mirror=false",
            "push",
            "--atomic",
            "--no-follow-tags",
            "origin",
            "refs/heads/main:refs/heads/main",
            "refs/tags/v1.5.1:refs/tags/v1.5.1",
        )
        self.assertEqual(self.git("rev-parse", "main", directory=self.remote), head)
        self.assertEqual(self.git("rev-parse", "v1.5.1^{}", directory=self.remote), head)

    def test_remote_without_atomic_support_does_not_partially_push(self):
        self.git("config", "receive.advertiseAtomic", "false", directory=self.remote)
        result = self.release("1.5.1")
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertEqual(self.git("rev-parse", "main", directory=self.remote), self.initial)
        self.assertEqual(self.git("tag", "--list", directory=self.remote), "")
        self.assertEqual(self.git("rev-parse", "v1.5.1^{}"), self.git("rev-parse", "HEAD"))

    def test_start_command_reaches_relocated_build_and_cleans_launch_file(self):
        for name in ("start.cmd", "scripts/development/Start-CppSource.ps1"):
            target = self.repo / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(SOURCE / name, target)
        # 用受控构建产物验证真实启动链，不启动 GUI 或原生编译服务。
        build = self.write(
            "scripts/build/Build-Cpp.ps1",
            "param([string]$ResultFile)\n"
            "$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))\n"
            "New-Item -ItemType Directory (Split-Path -Parent $ResultFile) -Force | Out-Null\n"
            "@{Executable = (Join-Path $root 'fake-app.ps1')} | ConvertTo-Json | "
            "Set-Content -LiteralPath $ResultFile -Encoding UTF8\n",
        )
        build.write_text(build.read_text(encoding="utf-8"), encoding="utf-8-sig")
        self.write(
            "fake-app.ps1",
            "@{Source = $env:HS2MANAGER_SOURCE_ROOT; Arguments = @($args)} | ConvertTo-Json | "
            "Set-Content -LiteralPath (Join-Path $PSScriptRoot 'launch-result.json') -Encoding UTF8\n"
            "$global:LASTEXITCODE = 0\n",
        )
        self.env["LOCALAPPDATA"] = str(self.root / "local")
        self.command(self.cmd_line("start.cmd", "--data", "D:\\Test Data"))
        result = json.loads((self.repo / "launch-result.json").read_text(encoding="utf-8-sig"))
        self.assertEqual(Path(result["Source"]), self.repo)
        self.assertEqual(result["Arguments"], ["--data", "D:\\Test Data"])
        self.assertEqual(list((self.root / "local").rglob("launch-*.json")), [])


@unittest.skipUnless(os.name == "nt" and shutil.which("powershell.exe"), "需要 Windows PowerShell 5.1")
class WindowsPowerShellTests(PushReleaseCases, unittest.TestCase):
    shell = shutil.which("powershell.exe")


@unittest.skipUnless(os.name == "nt" and shutil.which("pwsh.exe"), "需要 PowerShell 7")
class PowerShell7Tests(PushReleaseCases, unittest.TestCase):
    shell = shutil.which("pwsh.exe")


if __name__ == "__main__":
    unittest.main()
