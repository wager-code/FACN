#!/usr/bin/env python3
"""Write the identity consumed by the reviewed updater before Release packaging."""
import argparse
import json
from pathlib import Path
import re
from release_source import BINARY, release_number, sha256

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("release")
    parser.add_argument("commit")
    options = parser.parse_args()
    release_number(options.release)
    if not re.fullmatch(r"[a-f0-9]{40}", options.commit):
        parser.error("commit must be a full lowercase Git SHA")
    identity = {"release": options.release, "commit": options.commit,
                "sha256": sha256(options.directory / BINARY)}
    (options.directory / "gateway-build.json").write_text(
        json.dumps(identity, sort_keys=True) + "\n", encoding="utf-8")

if __name__ == "__main__":
    main()
