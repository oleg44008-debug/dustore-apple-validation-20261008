#!/usr/bin/env python3
"""Copy a reviewed Apple source allowlist without importing history or profiles."""
from __future__ import annotations
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import sys

CORE_HASH = "230cee7f71f6b2858a15ef210e4d6eb0f1f1a9ab368e9ff9512a77693934dfc3"
LOGO_HASH = "69cdb26a75f82302b8f476788a705bbdd6c1ed8d74934e3f05cb4df6a41468d4"
PRUNED = {".git", "bin", "obj", "build", "DerivedData", "xcuserdata", "__pycache__", "artifacts", "reports", "publish", "node_modules"}
SECRET_PATTERNS = {
    "GitHub token": re.compile(rb"(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,})"),
    "private key": re.compile(rb"-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----"),
    "AWS access key": re.compile(rb"\bAKIA[A-Z0-9]{16}\b"),
    "Google API key": re.compile(rb"\bAIza[0-9A-Za-z_-]{35}\b"),
    "credential-bearing URL": re.compile(rb"https?://[^\s/'\"]+:[^\s/@'\"]+@"),
}


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def safe_files(root: Path, suffixes: set[str]):
    for directory, folders, files in os.walk(root):
        folders[:] = sorted(name for name in folders if name not in PRUNED and not name.lower().startswith(("bin", "obj", "packaging-work")))
        for name in sorted(files):
            if name == "polish_iteration2.py":
                continue  # One-off source transform, not a needed validation fixture.
            path = Path(directory) / name
            if path.is_symlink():
                raise ValueError(f"A source symlink requires explicit review: {path.relative_to(root)}")
            if path.suffix.lower() in suffixes:
                yield path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sources", type=Path, required=True, help="The isolated, supplied Source directory.")
    parser.add_argument("--platform", choices=["mac", "ios", "both"], required=True)
    parser.add_argument("--mac-freeze")
    parser.add_argument("--ios-freeze")
    parser.add_argument("--validate-only", action="store_true")
    args = parser.parse_args()
    repository = Path(__file__).resolve().parent.parent
    source = args.sources.resolve()
    selected = {"mac", "ios"} if args.platform == "both" else {args.platform}
    if not args.validate_only:
        for platform in selected:
            if not getattr(args, f"{platform}_freeze"):
                parser.error(f"--{platform}-freeze must record the explicit platform-owner freeze.")
    snapshots = {}
    roots = {"mac": source / "DustoreLauncherV-Mac", "ios": source / "DustoreIOS"}

    def include(path: Path, relative: str, platform: str, source_label: str):
        if not path.is_file() or path.is_symlink():
            raise ValueError(f"Required reviewed source is missing or a symlink: {source_label}")
        target = (repository / relative).resolve()
        if not target.is_relative_to(repository) or ".git" in Path(relative).parts:
            raise ValueError("Mirror target escaped its own repository.")
        data = path.read_bytes()
        if len(data) > 5 * 1024 * 1024:
            raise ValueError(f"Oversized source resource requires review: {relative}")
        if path.suffix.lower() not in {".png", ".jpg", ".jpeg", ".webp", ".ico", ".icns"}:
            for rule, pattern in SECRET_PATTERNS.items():
                if pattern.search(data) or (path.suffix.lower() == ".dll" and pattern.search(data.decode("utf-16-le", errors="ignore").encode("utf-8"))):
                    raise ValueError(f"Secret-safety review rejected {relative}: {rule}. Matched content is omitted.")
            if path.suffix.lower() == ".sh" and b"\r" in data:
                raise ValueError(f"Native shell script must have LF line endings in the owner source: {relative}")
        if relative in snapshots:
            raise ValueError(f"Duplicate mirror entry: {relative}")
        snapshots[relative] = (path, data, {"path": relative, "source": source_label,
                                          "platform": platform, "bytes": len(data), "sha256": digest(data)})

    if "mac" in selected:
        root = roots["mac"]
        for name in ["DustoreLauncherV.Mac.csproj", "App.axaml", "App.axaml.cs", "MainWindow.axaml", "MainWindow.axaml.cs", "Program.cs", "StartupDiagnostics.cs", "README.md"]:
            include(root / name, f"launcher-mac/{name}", "mac", f"DustoreLauncherV-Mac/{name}")
        for folder, suffixes in {
            "Controls": {".cs"}, "Services": {".cs"}, "ViewModels": {".cs"},
            "Assets": {".png", ".svg", ".json", ".ico"},
            "packaging": {".py", ".swift", ".entitlements", ".icns"},
            "tests": {".cs", ".csproj", ".py", ".sh", ".json", ".md"},
        }.items():
            for path in safe_files(root / folder, suffixes):
                relative = path.relative_to(root).as_posix()
                include(path, f"launcher-mac/{relative}", "mac", f"DustoreLauncherV-Mac/{relative}")
        logo = snapshots["launcher-mac/Assets/dustore-logo-original.png"][1]
        if digest(logo) != LOGO_HASH:
            raise ValueError("The original Mac brand resource does not match the preserved logo.")
        core = source / "Dependencies" / "DustoreX.AutoConverter.Core.dll"
        include(core, "Dependencies/DustoreX.AutoConverter.Core.dll", "mac", "Dependencies/DustoreX.AutoConverter.Core.dll")
        if digest(snapshots["Dependencies/DustoreX.AutoConverter.Core.dll"][1]) != CORE_HASH:
            raise ValueError("The exact preserved converter Core DLL was changed.")
    if "ios" in selected:
        root = roots["ios"]
        for name in ["project.yml", "README.md", "VALIDATION.md", ".gitignore"]:
            include(root / name, f"ios/{name}", "ios", f"DustoreIOS/{name}")
        for folder in ["App", "Tests", "UITests", "Scripts"]:
            for path in safe_files(root / folder, {".swift", ".plist", ".png", ".json", ".xctestplan", ".sh", ".py", ".md"}):
                relative = path.relative_to(root).as_posix()
                include(path, f"ios/{relative}", "ios", f"DustoreIOS/{relative}")

    # A freeze must be a coherent snapshot, not a mixture of concurrent edits.
    for relative, (path, data, record) in snapshots.items():
        if path.read_bytes() != data:
            raise ValueError(f"Owner source changed during the snapshot: {relative}; request a new freeze.")
    summary = {"files": len(snapshots), "bytes": sum(record[2]["bytes"] for record in snapshots.values()),
               "platforms": sorted(selected), "secretSafety": "allowlist and high-confidence pattern scan passed"}
    if args.validate_only:
        print(json.dumps({"validatedOnly": True, **summary}))
        return 0

    manifest_path = repository / "source-manifest.json"
    previous = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else {"files": [], "freezes": {}}
    remaining = [record for record in previous["files"] if record["platform"] not in selected]
    for record in previous["files"]:
        if record["platform"] in selected and record["path"] not in snapshots:
            stale = (repository / record["path"]).resolve()
            if not stale.is_relative_to(repository) or not record["path"].startswith(("launcher-mac/", "ios/", "Dependencies/")):
                raise ValueError("Stale manifest file escaped its own mirror source directories.")
            stale.unlink(missing_ok=True)
    for relative, (_, data, _) in snapshots.items():
        target = repository / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    freezes = previous.get("freezes", {})
    for platform in selected:
        freezes[platform] = {"owner": "mac_design" if platform == "mac" else "foundation",
                             "milestone": getattr(args, f"{platform}_freeze"),
                             "snapshottedAtUtc": datetime.now(timezone.utc).isoformat()}
    records = sorted(remaining + [value[2] for value in snapshots.values()], key=lambda record: record["path"])
    manifest = {"schema": 1, "kind": "allowlisted-owner-frozen-source", "freezes": freezes,
                "sourceScope": "Only the supplied isolated Prime/Source snapshot; both editions are built with explicit configurations.",
                "exactConverterCoreSha256": CORE_HASH, "originalLogoSha256": LOGO_HASH,
                "excluded": ["external Git history", "credentials", "signing keys/profiles", "user profiles", "original games", "original IPA", "build outputs"],
                "files": records}
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({"mirrored": True, **summary, "manifestSha256": digest(manifest_path.read_bytes())}))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError) as error:
        print(f"Mirror preparation failed: {error}", file=sys.stderr)
        sys.exit(1)
