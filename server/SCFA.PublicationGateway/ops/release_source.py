"""Public Release selection, bounded downloads, reusable range cache and staging."""
from concurrent.futures import ThreadPoolExecutor, wait
from dataclasses import dataclass
from pathlib import Path
import hashlib
import json
import re
import subprocess
import tarfile

ASSET = "scfa-publication-linux-x64.tar.gz"
BINARY = "SCFA.PublicationGateway"
API = "https://api.github.com/repos/wager-code/FACN"
MAX_PACKAGE = 256 * 1024 * 1024


class UpdateError(Exception):
    """A message safe to show in operator output."""


def release_number(tag):
    if not isinstance(tag, str) or not re.fullmatch(r"gateway-v[0-9]{1,10}", tag):
        raise UpdateError("Invalid gateway Release tag")
    return int(tag[9:])


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


@dataclass(frozen=True)
class Release:
    tag: str
    asset_id: int
    checksum_id: int
    size: int

    @classmethod
    def parse(cls, data, expected=None):
        tag = data.get("tag_name")
        release_number(tag)
        if tag != (expected or tag) or data.get("draft") or data.get("prerelease"):
            raise UpdateError("Release is not the requested published stable tag")
        assets = data.get("assets", [])
        selected = []
        for name in (ASSET, ASSET + ".sha256"):
            matches = [a for a in assets if a.get("name") == name]
            if len(matches) != 1:
                raise UpdateError("Release package or checksum is missing or ambiguous")
            item = matches[0]
            if (type(item.get("id")) is not int or item["id"] < 1 or
                    type(item.get("size")) is not int or item["size"] < 1):
                raise UpdateError("Invalid Release asset metadata")
            selected.append(item)
        package, checksum = selected
        if package["size"] > MAX_PACKAGE or checksum["size"] > 4096:
            raise UpdateError("Release asset exceeds the allowed size")
        return cls(tag, package["id"], checksum["id"], package["size"])


class GitHubSource:
    def _curl(self, url, *, destination=None, byte_range=None, limit=1024 * 1024):
        # Only URLs constructed from the fixed repository and numeric asset IDs
        # are used. Redirects must remain HTTPS; curl diagnostics are suppressed.
        command = ["curl", "--fail", "--silent", "--show-error", "--location",
                   "--http1.1", "--proto", "=https", "--proto-redir", "=https",
                   "--tlsv1.2", "--connect-timeout", "15", "--max-time",
                   "1500" if byte_range else "45", "--speed-time", "120",
                   "--speed-limit", "1024", "--max-filesize", str(limit),
                   "--header", "User-Agent: SCFA-Gateway-Updater",
                   "--header", "Accept: application/octet-stream" if "/assets/" in url
                   else "Accept: application/vnd.github+json"]
        if destination is not None:
            command += ["--output", str(destination), "--write-out", "%{http_code}"]
        if byte_range is not None:
            command += ["--range", f"{byte_range[0]}-{byte_range[1]}"]
        try:
            result = subprocess.run(command + [url], capture_output=True, timeout=1520)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise UpdateError("Release request failed or exceeded its deadline") from error
        if result.returncode:
            raise UpdateError("Release request failed")
        if destination is not None:
            expected = b"206" if byte_range else b"200"
            if result.stdout != expected:
                raise UpdateError("Release server returned an unexpected HTTP status")
        return result.stdout

    def get_release(self, tag):
        release_number(tag)
        try:
            data = json.loads(self._curl(f"{API}/releases/tags/{tag}"))
            return Release.parse(data, tag)
        except (ValueError, AttributeError, TypeError) as error:
            raise UpdateError("Invalid Release response") from error

    def latest(self):
        candidates = []
        for page in range(1, 21):
            try:
                releases = json.loads(self._curl(f"{API}/releases?per_page=100&page={page}"))
                if not isinstance(releases, list):
                    raise ValueError()
            except (ValueError, TypeError) as error:
                raise UpdateError("Invalid Releases response") from error
            for data in releases:
                try:
                    candidates.append(Release.parse(data))
                except (UpdateError, AttributeError, TypeError):
                    continue
            if len(releases) < 100:
                if not candidates:
                    raise UpdateError("No complete stable gateway Release found")
                return max(candidates, key=lambda release: release_number(release.tag))
        raise UpdateError("Release listing exceeded the supported page limit")

    def checksum(self, release):
        try:
            raw = self._curl(f"{API}/releases/assets/{release.checksum_id}", limit=4096)
            match = re.fullmatch(rb"([a-fA-F0-9]{64}) [ *]" +
                                 re.escape(ASSET.encode()) + rb"\r?\n?", raw)
            if not match:
                raise ValueError()
            return match[1].decode().lower()
        except ValueError as error:
            raise UpdateError("Invalid Release checksum") from error

    def download_range(self, release, start, end, destination):
        self._curl(f"{API}/releases/assets/{release.asset_id}", destination=destination,
                   byte_range=(start, end), limit=end - start + 1)


def download_package(source, release, cache_root, progress=print):
    expected_hash = source.checksum(release)
    if not re.fullmatch(r"[a-f0-9]{64}", expected_hash):
        raise UpdateError("Invalid package SHA-256")
    cache = Path(cache_root) / f"{release.tag}-{release.asset_id}-{expected_hash}"
    cache.mkdir(parents=True, exist_ok=True, mode=0o700)
    package = cache / ASSET
    if (package.is_file() and package.stat().st_size == release.size
            and sha256(package) == expected_hash):
        progress(f"CACHE_OK {release.size}/{release.size}")
        return package
    package.unlink(missing_ok=True)
    width = (release.size + 3) // 4
    ranges = [(i * width, min((i + 1) * width, release.size) - 1)
              for i in range(4) if i * width < release.size]
    ready = set()
    pending = []
    for index, (start, end) in enumerate(ranges):
        part = cache / f"part.{index}"
        receipt = cache / f"part.{index}.sha256"
        try:
            valid = (part.is_file() and part.stat().st_size == end - start + 1
                     and receipt.read_text(encoding="ascii") == sha256(part))
        except (OSError, UnicodeError):
            valid = False
        if valid:
            ready.add(index)
        else:
            part.unlink(missing_ok=True)
            receipt.unlink(missing_ok=True)
            (cache / f"part.{index}.pending").unlink(missing_ok=True)
            pending.append(index)

    def fetch(index):
        start, end = ranges[index]
        temporary = cache / f"part.{index}.pending"
        source.download_range(release, start, end, temporary)
        if not temporary.is_file() or temporary.stat().st_size != end - start + 1:
            raise UpdateError("Release range is incomplete")
        digest = sha256(temporary)
        temporary.replace(cache / f"part.{index}")
        (cache / f"part.{index}.sha256").write_text(digest, encoding="ascii")

    with ThreadPoolExecutor(max_workers=4) as pool:
        futures = [pool.submit(fetch, index) for index in pending]
        while True:
            counts = []
            for index, (start, end) in enumerate(ranges):
                total = end - start + 1
                path = cache / f"part.{index}"
                if not path.is_file():
                    path = cache / f"part.{index}.pending"
                try:
                    count = min(total, path.stat().st_size)
                except FileNotFoundError:
                    count = 0
                counts.append(count)
            progress(f"DOWNLOAD {sum(counts)}/{release.size} " +
                     " ".join(f"part{i + 1}={count}/{end - start + 1}"
                              for i, (count, (start, end)) in enumerate(zip(counts, ranges))))
            if not futures or all(future.done() for future in futures):
                break
            wait(futures, timeout=1)
        for future in futures:
            future.result()
    temporary = cache / (ASSET + ".pending")
    with temporary.open("wb") as output:
        for index in range(len(ranges)):
            with (cache / f"part.{index}").open("rb") as part:
                for chunk in iter(lambda: part.read(1024 * 1024), b""):
                    output.write(chunk)
    if temporary.stat().st_size != release.size or sha256(temporary) != expected_hash:
        # Wrong cached bytes must not permanently poison subsequent retries.
        for index in range(len(ranges)):
            (cache / f"part.{index}").unlink(missing_ok=True)
            (cache / f"part.{index}.sha256").unlink(missing_ok=True)
        temporary.unlink(missing_ok=True)
        raise UpdateError("Release package size or SHA-256 did not match")
    temporary.replace(package)
    progress(f"PACKAGE_OK {release.size}/{release.size}")
    return package


def stage_package(package, staging, tag):
    staging = Path(staging)
    selected = {}
    expanded = 0
    try:
        with tarfile.open(package, "r:gz") as archive:
            for count, member in enumerate(archive):
                if count >= 1024:
                    raise UpdateError("Release archive contains too many entries")
                name = member.name
                while name.startswith("./"):
                    name = name[2:]
                if name in ("", ".") and member.isdir():
                    continue
                pieces = name.split("/")
                if (name.startswith("/") or "\\" in name or ":" in name
                        or any(piece in ("", "..", ".") for piece in pieces)
                        or not (member.isfile() or member.isdir())):
                    raise UpdateError("Unsafe Release archive entry")
                expanded += member.size
                if member.size < 0 or expanded > MAX_PACKAGE:
                    raise UpdateError("Release archive exceeds the expanded size limit")
                if name in (BINARY, "gateway-build.json"):
                    if not member.isfile() or name in selected:
                        raise UpdateError("Release executable or identity is ambiguous")
                    if name == "gateway-build.json" and member.size > 4096:
                        raise UpdateError("Release identity exceeds the size limit")
                    path = staging / name
                    with archive.extractfile(member) as source, path.open("xb") as target:
                        for chunk in iter(lambda: source.read(1024 * 1024), b""):
                            target.write(chunk)
                    selected[name] = path
    except (tarfile.TarError, EOFError, OSError) as error:
        raise UpdateError("Release archive could not be staged") from error
    if set(selected) != {BINARY, "gateway-build.json"}:
        raise UpdateError("Release lacks an executable or compiled build identity")
    try:
        identity = json.loads(selected["gateway-build.json"].read_text(encoding="utf-8"))
        if (identity["release"] != tag
                or not re.fullmatch(r"[a-f0-9]{40}", identity["commit"])
                or not re.fullmatch(r"[a-f0-9]{64}", identity["sha256"])
                or identity["sha256"] != sha256(selected[BINARY])):
            raise ValueError()
        with selected[BINARY].open("rb") as binary:
            header = binary.read(20)
        if (len(header) != 20 or header[:6] != b"\x7fELF\x02\x01"
                or int.from_bytes(header[16:18], "little") not in (2, 3)
                or int.from_bytes(header[18:20], "little") != 62):
            raise ValueError()
    except (ValueError, KeyError, TypeError) as error:
        raise UpdateError("Release identity, executable hash or Linux architecture is invalid") from error
    return selected[BINARY], {key: identity[key] for key in ("release", "commit", "sha256")}
