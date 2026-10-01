#!/usr/bin/env python3
"""Reviewed updater-v2 tooling. Report never installs or changes systemd."""
import argparse
from pathlib import Path
import os
import sys
import tempfile

from gateway_install import Installer, LinuxService, installed_tag, update_lock
from release_source import GitHubSource, UpdateError, download_package, release_number, stage_package


def main(argv=None, source=None, service=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("report", "check", "update", "verify", "recover"))
    parser.add_argument("tag", nargs="?")
    parser.add_argument("--state", required=True, type=Path,
                        help="Administrator-owned private state, cache, backups and lock directory")
    parser.add_argument("--target", type=Path, help="Existing gateway executable")
    parser.add_argument("--service", help="Existing systemd service unit")
    parser.add_argument("--health-url", help="Loopback publication-healthz URL")
    parser.add_argument("--auth-url", help="Loopback anonymous admin URL; must return 401")
    parser.add_argument("--allow-downgrade", action="store_true")
    options = parser.parse_args(argv)
    if options.command == "update":
        if not options.tag:
            parser.error("update requires a gateway Release tag")
        release_number(options.tag)
    elif options.tag or options.allow_downgrade:
        parser.error("tag and allow-downgrade only apply to update")
    source = source or GitHubSource()
    if options.command == "report":
        latest = source.latest()
        current = installed_tag(options.state)
        # No directory, cache, lock, state write or service operation in this path.
        newer = current is None or release_number(latest.tag) > release_number(current)
        print(f"REPORT_ONLY installed={current or 'unknown'} latest={latest.tag} update_available={str(newer).lower()}")
        return 0
    if not all((options.target, options.service, options.health_url, options.auth_url)):
        parser.error("service operations require target, service, health-url and auth-url")
    if service is None and (sys.platform != "linux" or os.geteuid() != 0):
        raise UpdateError("Gateway service operations require a Linux administrator")
    # Configured state and target locations must be absolute and administrator-owned.
    if not options.target.is_absolute() or not options.state.is_absolute():
        raise UpdateError("Gateway target and state must be absolute paths")
    if options.state == options.target or options.target in options.state.parents:
        raise UpdateError("Gateway executable and state locations overlap")
    service = service or LinuxService(options.service, options.health_url, options.auth_url)
    with update_lock(options.state):
        installer = Installer(options.target, options.state, service)
        if options.command == "recover":
            installer.recover()
            return 0
        installer.require_clean()
        if options.command == "verify":
            installer.verify_current()
            print("VERIFY_OK")
            return 0
        release = source.get_release(options.tag) if options.command == "update" else source.latest()
        _, runtime_identity = installer.verify_current()
        current = installed_tag(options.state) or (runtime_identity or {}).get("release")
        if options.command == "check" and current and release_number(release.tag) <= release_number(current):
            print("CURRENT_OK " + current)
            return 0
        # Fail before downloading if disk/runtime health or the current marker is inconsistent.
        if current and release_number(release.tag) < release_number(current) and not options.allow_downgrade:
            raise UpdateError("Gateway downgrade requires an explicit allow-downgrade option")
        package = download_package(source, release, options.state / "cache")
        with tempfile.TemporaryDirectory(prefix="staging-", dir=options.state) as staging:
            binary, identity = stage_package(package, staging, release.tag)
            installer.install(binary, identity, options.allow_downgrade)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except UpdateError as error:
        print("ERROR " + str(error), file=sys.stderr)
        sys.exit(1)
    except Exception:
        # Avoid leaking arbitrary network, path or private service diagnostics.
        print("ERROR Gateway updater failed; inspect local state before retrying", file=sys.stderr)
        sys.exit(1)
