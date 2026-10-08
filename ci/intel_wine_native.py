#!/usr/bin/env python3
"""Curated native Intel diagnostics; expected failures remain named evidence."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import sys
from native_helpers import NativeRun, ROOT

HELPER_SHA = "9554b42edc364e2a71bc5574823a866d02b3bf5df9d4ff846664db41cccf218d"
NATIVE_FIXTURE_SHA = "01990e4ae6db577f2506966ba849dd55202180f2ef0e33a8170ca53d8b6dd080"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--variants", action="store_true")
    mode.add_argument("--environment-pair", action="store_true")
    mode.add_argument("--native-vulkan", action="store_true")
    mode.add_argument("--native-vulkan-debugger", action="store_true")
    args = parser.parse_args()
    run = NativeRun("intel-wine-native-vulkan-debugger" if args.native_vulkan_debugger else
                    "intel-wine-native-vulkan" if args.native_vulkan else
                    "intel-wine-environment" if args.environment_pair else
                    "intel-wine-phase2" if args.variants else "intel-wine-diagnostic")
    helper = ROOT / "ci/diagnose_intel_wine.py"
    # Runtime binaries and prefixes stay outside NativeRun's curated report tree.
    probes = ROOT / "owned-diagnostics/intel-wine"
    run.details.update({"diagnosticOnly": True, "launcherBuildValidated": False,
                        "generatedCoreWrapperValidated": False,
                        "productionSourceModified": False, "exactCoreModified": False,
                        "graphicsDeviceCreated": False, "gameFpsMeasured": False,
                        "diagnosticVariants": args.variants,
                        "controlledEnvironmentPair": args.environment_pair,
                        "nativeVulkanAbiIsolation": args.native_vulkan or args.native_vulkan_debugger,
                        "ownedNativeDebugger": args.native_vulkan_debugger,
                        "logicalDeviceCreated": False, "surfaceCreated": False,
                        "rendererDisableVariantsShipped": False})
    try:
        if not run.check("actual native Intel host", platform.system() == "Darwin" and platform.machine() == "x86_64"):
            raise RuntimeError("The probe requires an actual Intel Mac host.")
        if not run.check("owner helper byte-identical", hashlib.sha256(helper.read_bytes()).hexdigest() == HELPER_SHA):
            raise RuntimeError("The diagnostic helper differs from the owner's frozen version.")
        if args.native_vulkan or args.native_vulkan_debugger:
            fixture = helper.with_name("native_vulkan_probe.c")
            if not run.check("owner native ABI fixture byte-identical",
                             hashlib.sha256(fixture.read_bytes()).hexdigest() == NATIVE_FIXTURE_SHA):
                raise RuntimeError("The native ABI fixture differs from the owner's frozen version.")
        if not run.run("source-provenance", [sys.executable, "ci/verify_source.py", "--out", str(run.out / "provenance")], 60):
            raise RuntimeError("Committed source provenance failed.")
        run.run("native-taskpolicy-paths", ["/bin/ls", "-l", "/usr/sbin/taskpolicy", "/usr/bin/taskpolicy"], 15, required=False)
        run.run("native-taskpolicy-owned-command", ["/usr/sbin/taskpolicy", "-a", "-l", "0", "-t", "0", "/usr/bin/true"], 15, required=False)
        # Wine process/module tracing may include its child environment. Supply
        # only native identity/locale/temp values, never Actions or GitHub secrets.
        permitted = {"HOME", "USER", "LOGNAME", "LANG", "LC_CTYPE", "TMPDIR",
                     "__CF_USER_TEXT_ENCODING", "SECURITYSESSIONID"}
        native_env = {name: value for name, value in os.environ.items() if name in permitted}
        native_env["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin"
        run.details["tracedChildEnvironment"] = {"allowlistedNames": sorted(native_env),
                                                  "inheritsActionsCredentials": False}
        command = [sys.executable, str(helper), "--out", str(probes)]
        if args.variants:
            command.append("--variants")
        elif args.environment_pair:
            command.append("--environment-pair")
        elif args.native_vulkan:
            command.append("--native-vulkan")
        elif args.native_vulkan_debugger:
            command.append("--native-vulkan-debugger")
        run.run("bounded-owner-Wine-probe", command, 1500, env=native_env)
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
                                      "gstreamerFrameworkPresent": data.get("gstreamerFrameworkPresent"),
                                      "nativeVulkanIsolation": data.get("nativeVulkanIsolation")})
        run.details["diagnostics"] = summaries
        run.check("curated diagnostic report exists", len(summaries) == 1)
    return run.finish("Native Intel pinned Wine bootstrap and taskpolicy diagnostics using owned command fixtures. Stage failures are evidence to classify; a green infrastructure job does not claim the actual launcher Wine workflow passed.")


if __name__ == "__main__":
    sys.exit(main())
