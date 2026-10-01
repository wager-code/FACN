"""Atomic installation and verified rollback; no production paths are embedded."""
from contextlib import contextmanager
from pathlib import Path
import json
import os
import re
import shutil
import subprocess
import tempfile
import time
import urllib.parse
import uuid

from release_source import UpdateError, release_number, sha256

STATE_FILES = ("installed-gateway-release", "installed-gateway.json")


def atomic_write(path, content, mode=0o600):
    path = Path(path)
    descriptor, name = tempfile.mkstemp(prefix=path.name + ".", dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as output:
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
        os.chmod(name, mode)
        os.replace(name, path)
        if os.name == "posix":
            directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(directory)
            finally:
                os.close(directory)
    finally:
        Path(name).unlink(missing_ok=True)


def replace_binary(source, target):
    target = Path(target)
    descriptor, name = tempfile.mkstemp(prefix=target.name + ".", dir=target.parent)
    try:
        with os.fdopen(descriptor, "wb") as output, open(source, "rb") as input_file:
            shutil.copyfileobj(input_file, output)
            output.flush()
            os.fsync(output.fileno())
        os.chmod(name, 0o755)
        os.replace(name, target)
    finally:
        Path(name).unlink(missing_ok=True)


@contextmanager
def update_lock(state):
    state = Path(state)
    state.mkdir(parents=True, exist_ok=True, mode=0o700)
    with (state / "updater.lock").open("a+b") as lock:
        try:
            if os.name == "posix":
                import fcntl
                fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            else:
                import msvcrt
                lock.seek(0)
                if not lock.read(1):
                    lock.write(b"\0")
                    lock.flush()
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise UpdateError("Another gateway update is running") from error
        try:
            yield
        finally:
            if os.name == "posix":
                fcntl.flock(lock.fileno(), fcntl.LOCK_UN)
            else:
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)


def installed_tag(state):
    marker = Path(state) / STATE_FILES[0]
    if not marker.exists():
        return None
    try:
        value = marker.read_text(encoding="utf-8").strip()
        release_number(value)
        return value
    except (OSError, UnicodeError) as error:
        raise UpdateError("Installed Release marker is invalid") from error


class LinuxService:
    def __init__(self, unit, health_url, auth_url, deadline=75):
        if not re.fullmatch(r"[A-Za-z0-9_.@-]+\.service", unit):
            raise UpdateError("Invalid systemd service name")
        for url in (health_url, auth_url):
            parsed = urllib.parse.urlsplit(url)
            if (parsed.scheme != "http" or parsed.hostname not in ("127.0.0.1", "::1")
                    or parsed.username or parsed.password or parsed.fragment):
                raise UpdateError("Verification endpoints must use loopback HTTP without credentials")
        self.unit = unit
        self.health_url = health_url
        self.auth_url = auth_url
        self.deadline = deadline

    def _systemctl(self, *arguments):
        try:
            result = subprocess.run(["systemctl", *arguments, self.unit],
                                    capture_output=True, timeout=60)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise UpdateError("systemd operation failed or exceeded its deadline") from error
        if result.returncode:
            raise UpdateError("systemd operation failed")
        return result.stdout.decode("ascii").strip()

    def restart(self):
        self._systemctl("restart")

    def _request(self, url):
        try:
            result = subprocess.run(
                ["curl", "--silent", "--max-time", "3", "--connect-timeout", "2",
                 "--noproxy", "*", "--proto", "=http", "--max-filesize", "4096",
                 "--write-out", "\n%{http_code}", url], capture_output=True, timeout=5)
            if result.returncode:
                raise UpdateError("Gateway verification endpoint is unavailable")
            body, status = result.stdout.rsplit(b"\n", 1)
            if len(body) > 4096:
                raise UpdateError("Gateway verification response is too large")
            return int(status), body
        except (OSError, ValueError, subprocess.TimeoutExpired) as error:
            raise UpdateError("Gateway verification endpoint is unavailable") from error

    def _pid(self):
        self._systemctl("is-active", "--quiet")
        value = self._systemctl("show", "--property=MainPID", "--value")
        if not re.fullmatch(r"[1-9][0-9]*", value):
            raise UpdateError("Gateway service has no running MainPID")
        return value

    def verify_once(self, expected_hash, identity=None):
        try:
            pid = self._pid()
            runtime_hash = sha256(Path("/proc") / pid / "exe")
            status, body = self._request(self.health_url)
            health = json.loads(body)
            auth_status, _ = self._request(self.auth_url)
            if (status != 200 or health.get("ok") is not True
                    or health.get("service") != "scfa-publication" or auth_status != 401
                    or runtime_hash != expected_hash or self._pid() != pid):
                raise ValueError()
            if identity and (health.get("version") != identity["release"]
                             or health.get("commit") != identity["commit"]):
                raise ValueError()
            if (isinstance(health.get("version"), str)
                    and re.fullmatch(r"gateway-v[0-9]{1,10}", health["version"])
                    and isinstance(health.get("commit"), str)
                    and re.fullmatch(r"[a-f0-9]{40}", health["commit"])):
                return {"release": health["version"], "commit": health["commit"]}
            return None  # Legacy gateway still requires active/health/auth/runtime checks.
        except (OSError, ValueError, KeyError, TypeError) as error:
            raise UpdateError("Gateway health, authorization or runtime hash did not match") from error

    def verify(self, expected_hash, identity=None):
        end = time.monotonic() + self.deadline
        while True:
            try:
                return self.verify_once(expected_hash, identity)
            except UpdateError:
                if time.monotonic() >= end:
                    raise
                time.sleep(1)


class Installer:
    def __init__(self, target, state, service, emit=print):
        self.target = Path(target)
        self.state = Path(state)
        self.service = service
        self.emit = emit
        self.journal = self.state / "pending-update.json"

    def require_clean(self):
        if self.journal.exists():
            raise UpdateError("An interrupted update requires the recover command")

    def verify_current(self):
        if not self.target.is_file() or self.target.is_symlink():
            raise UpdateError("Existing gateway executable is missing or unsafe")
        digest = sha256(self.target)
        identity = None
        record = self.state / STATE_FILES[1]
        if record.is_file():
            try:
                identity = json.loads(record.read_text(encoding="utf-8"))
                if (identity["sha256"] != digest
                        or identity["release"] != installed_tag(self.state)):
                    raise ValueError()
            except (ValueError, KeyError, TypeError) as error:
                raise UpdateError("Installed build identity does not match disk or marker") from error
        old_identity = self.service.verify(digest, identity)
        marker = installed_tag(self.state)
        if old_identity and marker and marker != old_identity["release"]:
            raise UpdateError("Installed marker does not match the running Release")
        return digest, old_identity

    def _rollback(self, journal):
        backup = self.state / "backups" / journal["backup"]
        # The journal may only select a backup from this state directory.
        if (Path(journal["backup"]).name != journal["backup"] or backup.is_symlink()
                or not backup.is_file() or sha256(backup) != journal["old_hash"]):
            raise UpdateError("Rollback backup is invalid")
        replace_binary(backup, self.target)
        self.service.restart()
        self.service.verify(journal["old_hash"], journal["old_identity"])
        if sha256(self.target) != journal["old_hash"]:
            raise UpdateError("Rollback executable changed during verification")
        for name, previous in journal["previous_state"].items():
            if name not in STATE_FILES:
                raise UpdateError("Rollback state is invalid")
            if previous is None:
                (self.state / name).unlink(missing_ok=True)
            else:
                atomic_write(self.state / name, previous.encode("utf-8"))
        self.journal.unlink()
        self.emit("ROLLBACK_OK")

    def recover(self):
        if not self.journal.is_file() or self.journal.is_symlink():
            raise UpdateError("No interrupted gateway update to recover")
        try:
            self._rollback(json.loads(self.journal.read_text(encoding="utf-8")))
        except Exception as error:
            self.emit("ROLLBACK_FAILED")
            raise UpdateError("Interrupted update recovery failed; preserve the backup and journal") from error

    def install(self, staged, identity, allow_downgrade=False):
        self.require_clean()
        old_hash, old_identity = self.verify_current()
        tag = identity["release"]
        current = installed_tag(self.state) or (old_identity or {}).get("release")
        if current and release_number(tag) < release_number(current) and not allow_downgrade:
            raise UpdateError("Gateway downgrade requires an explicit allow-downgrade option")
        if sha256(staged) != identity["sha256"]:
            raise UpdateError("Staged executable changed after package verification")
        backups = self.state / "backups"
        backups.mkdir(mode=0o700, exist_ok=True)
        backup = backups / ("gateway-" + uuid.uuid4().hex)
        shutil.copy2(self.target, backup)
        os.chmod(backup, 0o700)
        with backup.open("r+b") as backup_file:
            os.fsync(backup_file.fileno())
        if sha256(backup) != old_hash:
            raise UpdateError("Gateway backup hash did not match")
        journal = {"backup": backup.name, "old_hash": old_hash, "old_identity": old_identity,
                   "previous_state": {name: (self.state / name).read_text(encoding="utf-8")
                                      if (self.state / name).is_file() else None
                                      for name in STATE_FILES}}
        atomic_write(self.journal, json.dumps(journal).encode("utf-8"))
        try:
            replace_binary(staged, self.target)
            self.service.restart()
            self.service.verify(identity["sha256"], identity)
            if sha256(self.target) != identity["sha256"]:
                raise UpdateError("Installed executable changed during verification")
            atomic_write(self.state / STATE_FILES[1], json.dumps(identity).encode("utf-8"))
            atomic_write(self.state / STATE_FILES[0], (tag + "\n").encode("utf-8"))
            self.journal.unlink()
        except Exception as error:
            self.emit("UPDATE_FAILED")
            try:
                self._rollback(journal)
            except Exception as rollback_error:
                self.emit("ROLLBACK_FAILED")
                raise UpdateError("Update and rollback failed; preserve the backup and journal") from rollback_error
            raise UpdateError("Update failed; previous gateway was restored and verified") from error
        self.emit("UPDATE_OK " + tag)
