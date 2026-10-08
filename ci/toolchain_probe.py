#!/usr/bin/env python3
"""Record the actual native runner before accepting Apple build evidence."""
import argparse
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import time


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--expected-arch", choices=["arm64", "x86_64"], required=True)
    parser.add_argument("--skip-simulator", action="store_true", help="Mac app-only jobs do not require CoreSimulator initialization.")
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    records = []
    commands = [
        ("os", ["sw_vers"]),
        ("architecture", ["uname", "-m"]),
        ("xcode", ["xcodebuild", "-version"]),
        ("sdks", ["xcodebuild", "-showsdks"]),
        ("dotnet-sdks", ["dotnet", "--list-sdks"]),
        ("dotnet-info", ["dotnet", "--info"]),
    ]
    if not args.skip_simulator:
        commands += [("simulator-runtimes", ["xcrun", "simctl", "list", "runtimes", "-j"]),
                     ("simulator-devices", ["xcrun", "simctl", "list", "devices", "available", "-j"])]
    outputs = {}
    for name, command in commands:
        started = time.monotonic()
        attempts = []
        for attempt in range(2 if name.startswith("simulator-") else 1):
            try:
                result = subprocess.run(command, text=True, capture_output=True, timeout=120 if name.startswith("simulator-") else 60, check=False)
                output = result.stdout + result.stderr
                exit_code = result.returncode
            except (OSError, subprocess.TimeoutExpired) as error:
                output = f"{type(error).__name__}: {error}\n"
                exit_code = -1
            attempts.append({"attempt": attempt + 1, "exitCode": exit_code})
            (args.out / f"{name}-attempt-{attempt + 1}.txt").write_text(output, encoding="utf-8")
            if exit_code == 0:
                break
        (args.out / f"{name}.txt").write_text(output, encoding="utf-8")
        outputs[name] = output
        records.append({"name": name, "command": command, "exitCode": exit_code,
                        "seconds": round(time.monotonic() - started, 3), "report": f"{name}.txt", "attempts": attempts})
    checks = [
        {"name": "native macOS host", "passed": platform.system() == "Darwin"},
        {"name": "expected native architecture", "passed": platform.machine() == args.expected_arch},
        {"name": "Xcode 16.4 selected", "passed": outputs["xcode"].startswith("Xcode 16.4\n")},
        {"name": "iOS 18.5 simulator SDK", "passed": "iphonesimulator18.5" in outputs["sdks"]},
        {"name": "pinned .NET 8 SDK available", "passed": "8.0.424" in outputs["dotnet-sdks"]},
        {"name": "all probe commands succeeded", "passed": all(item["exitCode"] == 0 for item in records)},
    ]
    summary = {
        "schema": 1,
        "kind": "native-toolchain-probe",
        "commit": os.environ.get("GITHUB_SHA"),
        "runner": {"os": platform.system(), "architecture": platform.machine(),
                   "imageOS": os.environ.get("ImageOS"), "imageVersion": os.environ.get("ImageVersion")},
        "developerDir": os.environ.get("DEVELOPER_DIR"),
        "checks": checks,
        "passed": sum(check["passed"] for check in checks),
        "failed": sum(not check["passed"] for check in checks),
        "commands": records,
        "scope": "Toolchain availability only; no application or physical-device validation.",
    }
    (args.out / "toolchain-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"passed": summary["passed"], "failed": summary["failed"],
                      "architecture": summary["runner"]["architecture"]}))
    return 1 if summary["failed"] else 0


if __name__ == "__main__":
    sys.exit(main())
