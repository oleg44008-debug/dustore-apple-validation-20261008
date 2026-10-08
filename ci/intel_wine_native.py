#!/usr/bin/env python3
"""Curated native Intel diagnostics; expected failures remain named evidence."""
from __future__ import annotations
import hashlib
import json
from pathlib import Path
import platform
import shutil
import sys
from native_helpers import NativeRun, ROOT

HELPER_SHA = "ffdd614d5082028ad9133c1409a04daae5f2a05be26ae47f52ae07901036b781"


def main() -> int:
    run = NativeRun("intel-wine-diagnostic")
    helper = ROOT / "ci/diagnose_intel_wine.py"
    # Runtime binaries and prefixes stay outside NativeRun's curated report tree.
    probes = ROOT / "owned-diagnostics/intel-wine"
    run.details.update({"diagnosticOnly": True, "launcherBuildValidated": False,
                        "generatedCoreWrapperValidated": False,
                        "productionSourceModified": False, "exactCoreModified": False,
                        "graphicsDeviceCreated": False, "gameFpsMeasured": False})
    try:
        if not run.check("actual native Intel host", platform.system() == "Darwin" and platform.machine() == "x86_64"):
            raise RuntimeError("The probe requires an actual Intel Mac host.")
        if not run.check("owner helper byte-identical", hashlib.sha256(helper.read_bytes()).hexdigest() == HELPER_SHA):
            raise RuntimeError("The diagnostic helper differs from the owner's frozen version.")
        if not run.run("source-provenance", [sys.executable, "ci/verify_source.py", "--out", str(run.out / "provenance")], 60):
            raise RuntimeError("Committed source provenance failed.")
        run.run("native-taskpolicy-paths", ["/bin/ls", "-l", "/usr/sbin/taskpolicy", "/usr/bin/taskpolicy"], 15, required=False)
        run.run("native-taskpolicy-owned-command", ["/usr/sbin/taskpolicy", "-a", "-l", "0", "-t", "0", "/usr/bin/true"], 15, required=False)
        run.run("bounded-owner-Wine-probe", [sys.executable, str(helper), "--out", str(probes)], 1500)
    except Exception as error:
        run.check("native diagnostic infrastructure completed", False, f"{type(error).__name__}: {error}")
    finally:
        summaries = []
        if probes.is_dir():
            for probe in sorted(probes.iterdir()):
                if not probe.is_dir() or not probe.name.startswith("owned-intel-wine-"):
                    continue
                target_root = run.out / "diagnostic" / probe.name
                for source in sorted(probe.iterdir()):
                    # Only top-level evidence created by the owner helper. Never recurse into
                    # Wine archives, runtime extraction, command PE fixtures, or Wine prefixes.
                    if source.is_file() and not source.is_symlink() and source.suffix in {".json", ".log", ".txt"}:
                        target_root.mkdir(parents=True, exist_ok=True)
                        shutil.copyfile(source, target_root / source.name)
                report = probe / "diagnostic.json"
                if report.is_file():
                    data = json.loads(report.read_text(encoding="utf-8"))
                    summaries.append({"report": str((target_root / report.name).relative_to(run.out)),
                                      "helperSha256": HELPER_SHA, "steps": data.get("steps", []),
                                      "gstreamerFrameworkPresent": data.get("gstreamerFrameworkPresent")})
        run.details["diagnostics"] = summaries
        run.check("curated diagnostic report exists", len(summaries) == 1)
    return run.finish("Native Intel pinned Wine bootstrap and taskpolicy diagnostics using owned command fixtures. Stage failures are evidence to classify; a green infrastructure job does not claim the actual launcher Wine workflow passed.")


if __name__ == "__main__":
    sys.exit(main())
