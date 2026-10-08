#!/usr/bin/env python3
"""Validate artifact paths and file hashes before extracting owned CI evidence."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import stat
import sys
import zipfile


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--require-manifest", action="store_true")
    args = parser.parse_args()
    destination = args.out.resolve()
    with zipfile.ZipFile(args.archive) as archive:
        entries = archive.infolist()
        total = sum(entry.file_size for entry in entries)
        if total > 2 * 1024 * 1024 * 1024:
            raise ValueError("The artifact expansion exceeds the reviewed 2 GiB boundary.")
        planned = []
        seen = set()
        for entry in entries:
            path = PurePosixPath(entry.filename.replace("\\", "/"))
            if path.is_absolute() or any(part in {"..", ".git"} or ":" in part for part in path.parts):
                raise ValueError("An artifact entry escaped its own diagnostic directory.")
            key = path.as_posix().casefold()
            if key in seen or stat.S_ISLNK(entry.external_attr >> 16):
                raise ValueError("Duplicate or symlink artifact entry was rejected.")
            seen.add(key)
            target = (destination / path).resolve()
            if not target.is_relative_to(destination):
                raise ValueError("An artifact path escaped its resolved diagnostic directory.")
            planned.append((entry, target))
        manifest = json.loads(archive.read("artifact-manifest.json")) if "artifact-manifest.json" in archive.namelist() else None
        if args.require_manifest and manifest is None:
            raise ValueError("Native artifact is missing its committed-file/evidence manifest.")
        expected = {record["path"]: record for record in manifest["files"]} if manifest else {}
        verified = 0
        for entry, target in planned:
            if entry.is_dir():
                continue
            data = archive.read(entry)
            if manifest and entry.filename != "artifact-manifest.json":
                record = expected.get(entry.filename)
                if not record or len(data) != record["bytes"] or hashlib.sha256(data).hexdigest() != record["sha256"]:
                    raise ValueError(f"Artifact manifest mismatch: {entry.filename}")
                verified += 1
            if target.exists() and target.read_bytes() != data:
                raise ValueError(f"Refusing to overwrite different existing diagnostic evidence: {entry.filename}")
        if manifest and verified != len(expected):
            raise ValueError("The archive omitted manifest-declared evidence files.")
        for entry, target in planned:
            if entry.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                if not target.exists():
                    with archive.open(entry) as source, target.open("wb") as output:
                        import shutil
                        shutil.copyfileobj(source, output)
    print(json.dumps({"archive": str(args.archive), "out": str(destination), "filesVerified": verified,
                      "expandedBytes": total, "commit": manifest.get("commit") if manifest else None}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
