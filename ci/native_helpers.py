"""Bounded native commands and a curated artifact boundary, never an env dump."""
from __future__ import annotations
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import signal
import subprocess
import time

ROOT = Path(__file__).resolve().parent.parent


class NativeRun:
    def __init__(self, name: str):
        self.name = name
        self.out = ROOT / "reports" / name
        self.artifact = ROOT / "artifacts" / name
        self.out.mkdir(parents=True, exist_ok=True)
        self.artifact.mkdir(parents=True, exist_ok=True)
        self.commands = []
        self.checks = []
        self.details = {}

    def check(self, name: str, passed: bool, detail=None):
        self.checks.append({"name": name, "passed": bool(passed), "detail": detail})
        return passed

    def run(self, name: str, command: list[str], timeout=300, cwd=None, env=None):
        log = self.out / (name + ".log")
        started = time.monotonic()
        exit_code = -1
        timed_out = False
        with log.open("w", encoding="utf-8") as output:
            output.write(json.dumps({"command": command}) + "\n")
            output.flush()
            try:
                process = subprocess.Popen(command, cwd=cwd or ROOT, env=env,
                                           stdout=output, stderr=subprocess.STDOUT,
                                           text=True, start_new_session=True)
                try:
                    exit_code = process.wait(timeout=timeout)
                except subprocess.TimeoutExpired:
                    timed_out = True
                    os.killpg(process.pid, signal.SIGTERM)
                    try:
                        exit_code = process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL)
                        exit_code = process.wait(timeout=5)
            except OSError as error:
                output.write(f"{type(error).__name__}: {error}\n")
        record = {"name": name, "exitCode": exit_code, "timedOut": timed_out,
                  "seconds": round(time.monotonic() - started, 3), "log": log.name}
        self.commands.append(record)
        self.check(name, exit_code == 0 and not timed_out, record)
        print(json.dumps(record), flush=True)
        return exit_code == 0 and not timed_out

    def finish(self, scope: str):
        result = {"schema": 1, "kind": "native-apple-validation", "name": self.name,
                  "commit": os.environ.get("GITHUB_SHA"),
                  "runner": {"os": platform.system(), "architecture": platform.machine(),
                             "imageOS": os.environ.get("ImageOS"), "imageVersion": os.environ.get("ImageVersion")},
                  "scope": scope, "checks": self.checks,
                  "passed": sum(item["passed"] for item in self.checks),
                  "failed": sum(not item["passed"] for item in self.checks),
                  "commands": self.commands, **self.details}
        (self.out / "native-summary.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        evidence = self.artifact / "evidence"
        evidence.mkdir(exist_ok=True)
        # Copy only diagnostic formats, excluding fixture/user profiles, apps,
        # tool binaries, generated build trees and CoreSimulator data.
        for directory, folders, files in os.walk(self.out):
            folders[:] = [folder for folder in folders if not folder.startswith(("profile", "Derived", "Device", "extracted-", "installed-", "installer-mount-", "packaging-work-"))
                          and not folder.endswith((".app", ".xcresult"))
                          and folder not in {"foreign-working-directory", "Payload", "data", "managed", "cache"}]
            for name in files:
                source = Path(directory) / name
                if source.suffix.lower() in {".json", ".log", ".txt", ".png", ".jpg", ".jpeg"}:
                    target = evidence / source.relative_to(self.out)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(source, target)
        files = [{"path": path.relative_to(self.artifact).as_posix(), "bytes": path.stat().st_size,
                  "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                 for path in sorted(self.artifact.rglob("*")) if path.is_file()]
        (self.artifact / "artifact-manifest.json").write_text(json.dumps({"commit": os.environ.get("GITHUB_SHA"), "files": files}, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"passed": result["passed"], "failed": result["failed"], "artifact": str(self.artifact)}), flush=True)
        return 1 if result["failed"] else 0
