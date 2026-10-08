#!/usr/bin/env python3
"""Verify the committed allowlisted snapshot and persist its CI provenance."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    manifest_path = root / "source-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    checks = []
    for record in manifest["files"]:
        path = (root / record["path"]).resolve()
        safe = path.is_relative_to(root) and record["path"].startswith(("launcher-mac/", "ios/", "Dependencies/")) and not path.is_symlink()
        data = path.read_bytes() if safe and path.is_file() else b""
        checks.append({"name": record["path"], "passed": safe and len(data) == record["bytes"] and hashlib.sha256(data).hexdigest() == record["sha256"]})
    manifest_hash = hashlib.sha256(manifest_path.read_bytes()).hexdigest()
    summary = {"schema": 1, "kind": "committed-source-verification", "commit": os.environ.get("GITHUB_SHA"),
               "manifestSha256": manifest_hash, "freezes": manifest["freezes"], "checks": checks,
               "passed": sum(check["passed"] for check in checks), "failed": sum(not check["passed"] for check in checks)}
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "source-manifest.json").write_bytes(manifest_path.read_bytes())
    (args.out / "source-verification.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: summary[key] for key in ["passed", "failed", "manifestSha256"]}))
    return 1 if summary["failed"] else 0


if __name__ == "__main__":
    sys.exit(main())
