"""Isolated updater regression suite: no production network or systemd mutations."""
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch
import hashlib
import io
import json
import os
import subprocess
import sys
import tarfile
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from gateway_install import Installer, LinuxService, installed_tag, update_lock
from gateway_updater import main
from release_source import (ASSET, BINARY, GitHubSource, Release, UpdateError,
                            download_package, sha256, stage_package)

COMMIT = "a" * 40
NEW_BYTES = b"\x7fELF\x02\x01" + bytes(10) + b"\x03\x00\x3e\x00" + b"new-gateway"


def archive_bytes(entries):
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as archive:
        for name, payload, kind in entries:
            member = tarfile.TarInfo(name)
            member.size = len(payload)
            if kind == "link":
                member.type = tarfile.SYMTYPE
                member.linkname = "../elsewhere"
                member.size = 0
            archive.addfile(member, io.BytesIO(payload))
    return buffer.getvalue()


def valid_package(tag="gateway-v62"):
    identity = {"release": tag, "commit": COMMIT,
                "sha256": hashlib.sha256(NEW_BYTES).hexdigest()}
    return archive_bytes([(BINARY, NEW_BYTES, "file"),
                          ("gateway-build.json", json.dumps(identity).encode(), "file")]), identity


class FakeSource:
    def __init__(self, payload):
        self.payload = payload
        self.release = Release("gateway-v62", 100, 101, len(payload))
        self.calls = []
        self.fail_start = None

    def latest(self):
        return self.release

    def get_release(self, tag):
        if tag != self.release.tag:
            raise UpdateError("Missing test Release")
        return self.release

    def checksum(self, release):
        return hashlib.sha256(self.payload).hexdigest()

    def download_range(self, release, start, end, destination):
        self.calls.append(start)
        destination.write_bytes(self.payload[start:end + 1])
        if start == self.fail_start:
            destination.write_bytes(b"partial")
            raise UpdateError("Test range failed")


class FakeService:
    def __init__(self, target, old_hash):
        self.target = target
        self.running_hash = old_hash
        self.restarts = 0
        self.fail_new = None
        self.old_hash = old_hash
        self.fail_rollback = False

    def restart(self):
        self.restarts += 1
        new_hash = sha256(self.target)
        if self.fail_new == "crash" and new_hash != self.old_hash:
            self.fail_new = None
            raise KeyboardInterrupt()
        if self.fail_new == "restart" and new_hash != self.old_hash:
            self.fail_new = None
            raise UpdateError("Test restart failed")
        if new_hash == self.old_hash and self.fail_rollback:
            raise UpdateError("Test rollback restart failed")
        if self.fail_new != "runtime":
            self.running_hash = new_hash

    def verify(self, expected_hash, identity=None):
        if self.running_hash != expected_hash:
            raise UpdateError("Test runtime mismatch")
        if expected_hash != self.old_hash and self.fail_new in ("auth", "identity"):
            raise UpdateError("Test authorization or identity mismatch")
        if sha256(self.target) != expected_hash:
            raise UpdateError("Test disk mismatch")
        return identity


class UpdaterTests(unittest.TestCase):
    def setUp(self):
        # Caller/CI controls TMP/TEMP; local runs keep all artifacts on D.
        self.temp = tempfile.TemporaryDirectory(prefix="gateway-updater-test-")
        self.root = Path(self.temp.name)
        self.target = self.root / BINARY
        self.target.write_bytes(b"old-gateway")
        self.state = self.root / "state"
        self.state.mkdir()
        (self.state / "installed-gateway-release").write_text("gateway-v61\n")
        self.old_hash = sha256(self.target)
        self.service = FakeService(self.target, self.old_hash)
        self.events = []
        self.installer = Installer(self.target, self.state, self.service, self.events.append)
        self.payload, self.identity = valid_package()
        self.staged = self.root / "staged"
        self.staged.write_bytes(NEW_BYTES)

    def tearDown(self):
        self.temp.cleanup()

    def test_release_filters_and_duplicate_assets(self):
        data = {"tag_name": "gateway-v62", "draft": False, "prerelease": False,
                "assets": [{"name": ASSET, "id": 1, "size": 10},
                           {"name": ASSET + ".sha256", "id": 2, "size": 110}]}
        self.assertEqual(Release.parse(data).tag, "gateway-v62")
        for field in ("draft", "prerelease"):
            with self.subTest(field=field), self.assertRaises(UpdateError):
                Release.parse(dict(data, **{field: True}))
        with self.assertRaises(UpdateError):
            Release.parse(dict(data, assets=data["assets"] + [data["assets"][0]]))
        with self.assertRaises(UpdateError):
            Release.parse(data, "gateway-v61")

    def test_release_latest_paginates_and_uses_numeric_order(self):
        def release(number):
            return {"tag_name": f"gateway-v{number}", "assets": [
                {"name": ASSET, "id": number, "size": 100},
                {"name": ASSET + ".sha256", "id": number + 100, "size": 110}]}
        source = GitHubSource()
        pages = [json.dumps([release(9)] * 100).encode(),
                 json.dumps([release(62)]).encode()]
        with patch.object(source, "_curl", side_effect=pages) as curl:
            self.assertEqual(source.latest().tag, "gateway-v62")
            self.assertIn("page=2", curl.call_args.args[0])

    def test_range_rejects_full_200_response(self):
        result = subprocess.CompletedProcess([], 0, b"200", b"")
        with patch("release_source.subprocess.run", return_value=result), self.assertRaises(UpdateError):
            GitHubSource().download_range(Release("gateway-v62", 1, 2, 10), 0, 3, self.root / "part")

    def test_failed_range_reuses_completed_parts_and_reports_progress(self):
        source = FakeSource(self.payload)
        source.fail_start = 0
        with self.assertRaises(UpdateError):
            download_package(source, source.release, self.state / "cache", self.events.append)
        first_calls = len(source.calls)
        source.fail_start = None
        path = download_package(source, source.release, self.state / "cache", self.events.append)
        self.assertEqual(sha256(path), source.checksum(source.release))
        self.assertEqual(len(source.calls), first_calls + 1)
        self.assertTrue(any("part4=" in event for event in self.events))
        download_package(source, source.release, self.state / "cache", self.events.append)
        self.assertEqual(len(source.calls), first_calls + 1)
        self.assertTrue(any(event.startswith("CACHE_OK") for event in self.events))

    def test_corrupt_range_does_not_poison_retry(self):
        source = FakeSource(self.payload)
        original = source.download_range
        def corrupt(release, start, end, path):
            original(release, start, end, path)
            if start == 0:
                path.write_bytes(b"x" * (end - start + 1))
        source.download_range = corrupt
        with self.assertRaises(UpdateError):
            download_package(source, source.release, self.state / "cache", lambda _: None)
        source.download_range = original
        result = download_package(source, source.release, self.state / "cache", lambda _: None)
        self.assertEqual(result.read_bytes(), self.payload)

    def test_staging_rejects_paths_links_duplicates_missing_identity_and_hash(self):
        cases = [
            [("../escape", b"bad", "file")],
            [("/absolute", b"bad", "file")],
            [("link", b"", "link")],
            [(BINARY, NEW_BYTES, "file"), (BINARY, NEW_BYTES, "file")],
            [(BINARY, NEW_BYTES, "file")],
            [(BINARY, b"wrong", "file"),
             ("gateway-build.json", json.dumps(self.identity).encode(), "file")],
        ]
        for index, entries in enumerate(cases):
            staging = self.root / f"unsafe{index}"
            staging.mkdir()
            package = self.root / f"unsafe{index}.tar.gz"
            package.write_bytes(archive_bytes(entries))
            with self.subTest(index=index), self.assertRaises(UpdateError):
                stage_package(package, staging, "gateway-v62")
        package = self.root / "valid.tar.gz"
        package.write_bytes(self.payload)
        staging = self.root / "safe"
        staging.mkdir()
        binary, identity = stage_package(package, staging, "gateway-v62")
        self.assertEqual(binary.read_bytes(), NEW_BYTES)
        self.assertEqual(identity, self.identity)
        self.assertFalse((self.root / "escape").exists())

    def test_success_updates_marker_only_after_runtime_verification(self):
        self.installer.install(self.staged, self.identity)
        self.assertEqual(self.target.read_bytes(), NEW_BYTES)
        self.assertEqual(installed_tag(self.state), "gateway-v62")
        self.assertEqual(json.loads((self.state / "installed-gateway.json").read_text()), self.identity)
        self.assertEqual(self.events, ["UPDATE_OK gateway-v62"])
        self.assertFalse(self.installer.journal.exists())
        self.installer.verify_current()

    def test_restart_auth_identity_and_runtime_failures_restore_verified_old_gateway(self):
        for failure in ("restart", "auth", "identity", "runtime"):
            with self.subTest(failure=failure):
                self.service.fail_new = failure
                with self.assertRaises(UpdateError):
                    self.installer.install(self.staged, self.identity)
                self.assertEqual(self.target.read_bytes(), b"old-gateway")
                self.assertEqual(installed_tag(self.state), "gateway-v61")
                self.assertEqual(self.service.running_hash, self.old_hash)
                self.assertFalse((self.state / "installed-gateway.json").exists())
                self.assertEqual(self.events[-2:], ["UPDATE_FAILED", "ROLLBACK_OK"])
                self.service.fail_new = None

    def test_marker_write_failure_rolls_back_binary_and_state(self):
        from gateway_install import atomic_write
        failed = False
        def once(path, content, mode=0o600):
            nonlocal failed
            if Path(path).name == "installed-gateway-release" and not failed:
                failed = True
                raise OSError("test marker write failure")
            return atomic_write(path, content, mode)
        with patch("gateway_install.atomic_write", side_effect=once), self.assertRaises(UpdateError):
            self.installer.install(self.staged, self.identity)
        self.assertEqual(installed_tag(self.state), "gateway-v61")
        self.assertFalse((self.state / "installed-gateway.json").exists())
        self.assertEqual(self.target.read_bytes(), b"old-gateway")

    def test_interrupted_update_blocks_retry_and_can_recover(self):
        self.service.fail_new = "crash"
        with self.assertRaises(KeyboardInterrupt):
            self.installer.install(self.staged, self.identity)
        self.assertTrue(self.installer.journal.exists())
        with self.assertRaises(UpdateError):
            self.installer.install(self.staged, self.identity)
        self.installer.recover()
        self.assertEqual(self.target.read_bytes(), b"old-gateway")
        self.assertEqual(installed_tag(self.state), "gateway-v61")
        self.assertEqual(self.events[-1], "ROLLBACK_OK")

    def test_failed_rollback_keeps_recoverable_journal_and_reports_failure(self):
        self.service.fail_new = "auth"
        self.service.fail_rollback = True
        with self.assertRaises(UpdateError):
            self.installer.install(self.staged, self.identity)
        self.assertEqual(self.events[-2:], ["UPDATE_FAILED", "ROLLBACK_FAILED"])
        self.assertTrue(self.installer.journal.exists())
        self.service.fail_rollback = False
        self.service.fail_new = None
        self.installer.recover()
        self.assertFalse(self.installer.journal.exists())

    def test_lock_prevents_two_installers(self):
        with update_lock(self.state):
            with self.assertRaises(UpdateError):
                with update_lock(self.state):
                    self.fail("second lock acquired")

    def test_downgrade_is_rejected_before_switch(self):
        identity = dict(self.identity, release="gateway-v60")
        with self.assertRaises(UpdateError):
            self.installer.install(self.staged, identity)
        self.assertEqual(self.service.restarts, 0)
        self.assertEqual(self.target.read_bytes(), b"old-gateway")

    def test_running_identity_prevents_downgrade_without_marker(self):
        (self.state / "installed-gateway-release").unlink()
        original_verify = self.service.verify
        def running(expected_hash, identity=None):
            original_verify(expected_hash, identity)
            return {"release": "gateway-v70", "commit": COMMIT}
        self.service.verify = running
        with self.assertRaises(UpdateError):
            self.installer.install(self.staged, self.identity)
        self.assertEqual(self.service.restarts, 0)

    def test_verification_rejects_non_loopback_and_credentials(self):
        for url in ("https://example.com/health", "http://localhost/health",
                    "http://user:password@127.0.0.1/health"):
            with self.subTest(url=url), self.assertRaises(UpdateError):
                LinuxService("test.service", url, "http://127.0.0.1/auth")
        with self.assertRaises(UpdateError):
            LinuxService("test.service;other", "http://127.0.0.1/health", "http://127.0.0.1/auth")

    def test_report_only_never_creates_state_or_calls_service(self):
        missing_state = self.root / "report-state"
        with redirect_stdout(io.StringIO()) as output:
            result = main(["report", "--state", str(missing_state)], FakeSource(self.payload))
        self.assertEqual(result, 0)
        self.assertIn("REPORT_ONLY", output.getvalue())
        self.assertFalse(missing_state.exists())
        self.assertEqual(self.service.restarts, 0)

    def test_check_current_still_verifies_disk_and_runtime(self):
        (self.state / "installed-gateway-release").write_text("gateway-v62\n")
        self.service.running_hash = "0" * 64
        with self.assertRaises(UpdateError):
            main(["check", "--state", str(self.state), "--target", str(self.target),
                  "--service", "test.service", "--health-url", "http://127.0.0.1/health",
                  "--auth-url", "http://127.0.0.1/auth"], FakeSource(self.payload), self.service)
        self.assertEqual(self.service.restarts, 0)

    @unittest.skipUnless(sys.platform == "linux", "Requires Linux /proc executable semantics")
    def test_linux_runtime_hash_checks_running_inode_and_anonymous_auth(self):
        executable = self.root / "running"
        executable.write_bytes(Path("/bin/sleep").read_bytes())
        executable.chmod(0o755)
        process = subprocess.Popen([str(executable), "60"])
        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                if self.path == "/health":
                    self.send_response(200)
                    self.end_headers()
                    self.wfile.write(json.dumps({"ok": True, "service": "scfa-publication",
                        "version": "gateway-v62", "commit": COMMIT}).encode())
                else:
                    self.send_response(401)
                    self.end_headers()
            def log_message(self, *args):
                pass
        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        base = f"http://127.0.0.1:{server.server_port}"
        service = LinuxService("test.service", base + "/health", base + "/auth", deadline=0)
        service._systemctl = lambda *args: str(process.pid) if args[0] == "show" else ""
        try:
            running_hash = sha256(executable)
            identity = {"release": "gateway-v62", "commit": COMMIT}
            self.assertEqual(service.verify(running_hash, identity), identity)
            replacement = self.root / "replacement"
            replacement.write_bytes(b"different-disk-binary")
            os.replace(replacement, executable)
            self.assertEqual(service.verify(running_hash, identity), identity)
            with self.assertRaises(UpdateError):
                service.verify(sha256(executable), identity)
            service.auth_url = base + "/health"
            with self.assertRaises(UpdateError):
                service.verify(running_hash, identity)
        finally:
            process.terminate()
            process.wait(timeout=5)
            server.shutdown()
            server.server_close()
            thread.join(timeout=5)


if __name__ == "__main__":
    unittest.main(verbosity=2)
