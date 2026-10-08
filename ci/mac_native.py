#!/usr/bin/env python3
"""Native Mac launch, installer, service, WebKit, input and offline UI checks."""
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

CORE_HASH = "230cee7f71f6b2858a15ef210e4d6eb0f1f1a9ab368e9ff9512a77693934dfc3"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--edition", choices=["Free", "Prime"], required=True)
    parser.add_argument("--rid", choices=["osx-arm64", "osx-x64"], required=True)
    args = parser.parse_args()
    run = NativeRun(f"mac-{args.edition}-{args.rid}")
    source = ROOT / "launcher-mac"
    core = ROOT / "Dependencies/DustoreX.AutoConverter.Core.dll"
    packages = run.artifact / "packages"
    packages.mkdir(exist_ok=True)
    staging = run.out / "package-build"
    staging.mkdir(exist_ok=True)
    try:
        expected_arch = "arm64" if args.rid == "osx-arm64" else "x86_64"
        if not run.check("actual native runner architecture", platform.system() == "Darwin" and platform.machine() == expected_arch):
            raise RuntimeError("The package architecture must match the actual host.")
        if not run.run("source-provenance", [sys.executable, "ci/verify_source.py", "--out", str(run.out / "provenance")], 60):
            raise RuntimeError("Committed source provenance failed.")
        if not run.run("native-toolchain", [sys.executable, "ci/toolchain_probe.py", "--out", str(run.out / "toolchain"),
                    "--expected-arch", expected_arch, "--skip-simulator"], 180):
            raise RuntimeError("The required native Apple toolchain failed.")
        run.run("packaging-boundary-unit-tests", [sys.executable, "-m", "unittest", "discover", "-s", "packaging/tests", "-v"], 180, source)
        project = source / "DustoreLauncherV.Mac.csproj"
        if not run.run("build-and-sdk-analyzers", ["dotnet", "build", str(project), "-c", "Release", "-p:DustoreEdition=" + args.edition,
                "-p:ConverterCoreAssembly=" + str(core), "-p:RunAnalyzers=true", "-p:EnableNETAnalyzers=true", "-p:AnalysisLevel=latest-recommended"], 300):
            raise RuntimeError("Native managed build failed.")
        if not run.run("native-self-contained-package", [sys.executable, "packaging/build_macos.py", "--rid", args.rid,
                "--edition", args.edition.lower(), "--converter-assembly", str(core), "--output", str(staging)], 600, source):
            raise RuntimeError("Native package/signature generation failed.")
        package_report = json.loads((staging / f"package-{args.rid}.json").read_text(encoding="utf-8"))
        bundle = Path(package_report["bundle"])
        executable = bundle / "Contents/MacOS/DustoreLauncherV.Mac"
        published_core = bundle / "Contents/MacOS/DustoreX.AutoConverter.Core.dll"
        run.check("published converter Core byte-identical", hashlib.sha256(published_core.read_bytes()).hexdigest() == CORE_HASH)
        run.check("package edition/version", package_report["edition"] == args.edition.lower() and package_report["metadata"]["CFBundleShortVersionString"] == "5.4.0")
        run.check("native ad-hoc signing reported", package_report["signing"]["adHoc"] is True and package_report["signing"]["notarized"] is False)
        archive = Path(package_report["archive"])
        installers = list(staging.glob("*.dmg"))
        if not run.check("one native DMG installer", len(installers) == 1):
            raise RuntimeError("The native package did not produce a unique installer.")
        native = run.out / "native-package-checks"
        run.run("native-startup-installer-webkit-services", [sys.executable, "packaging/verify_macos.py", "--archive", str(archive),
                "--rid", args.rid, "--output", str(native), "--normal-launch", "--installer", str(installers[0]), "--ui-smoke"], 900, source)
        offline = run.out / "offline-ui"
        offline.mkdir(exist_ok=True)
        env = os.environ.copy()
        env["DUSTOREV_PROFILE_DIRECTORY"] = str(offline / "profile-owned")
        for key in ["DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64"]:
            env.pop(key, None)
        offline_report = offline / "offline-ui.json"
        if run.run("native-offline-routed-input-accessibility-layout", [str(executable), "--ui-smoke", "--offline-ui-smoke",
                "--smoke-report", str(offline_report), "--smoke-screenshot", str(offline / "offline-library.png")], 300, source, env):
            report = json.loads(offline_report.read_text(encoding="utf-8"))
            run.check("offline UI assertion report", report.get("status") == "Pass", report)
        probe_project = source / "tests/CatalogProbe/CatalogProbe.csproj"
        if probe_project.is_file():
            run.run("2000-entry-viewmodel-overhead", ["dotnet", "run", "--project", str(probe_project), "-c", "Release", "--",
                    str(bundle / "Contents/MacOS/DustoreLauncherV.Mac.dll"), str(run.out / "catalog-overhead.json")], 180)
        for path in staging.iterdir():
            if path.is_file() and path.suffix in {".zip", ".dmg", ".sha256", ".json"}:
                shutil.copyfile(path, packages / path.name)
        run.details["edition"] = args.edition
        run.details["runtimeIdentifier"] = args.rid
        run.details["exactCoreSha256"] = CORE_HASH
        run.details["nativeWineGameFpsVerified"] = False
        run.details["physicalVoiceOverVerified"] = False
    except Exception as error:
        run.check("native validation completed", False, f"{type(error).__name__}: {error}")
    return run.finish("Native macOS 5.4.0 launcher build/analyzers/package/normal LaunchServices/DMG/service/WebKit/offline UI and owned input fixtures on the matching CPU host. Ad-hoc signed, not Developer ID notarized. No original game, arbitrary Wine compatibility, FPS or real VoiceOver claim.")


if __name__ == "__main__":
    sys.exit(main())
