#!/usr/bin/env python3
"""Native iPhone/iPad validation and clearly unsigned device packaging."""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import plistlib
import subprocess
import sys
import time
import uuid
from native_helpers import NativeRun, ROOT
from ios_simulator_environment import resolve_runtime, REFERENCES

RUNTIME = "com.apple.CoreSimulator.SimRuntime.iOS-18-5"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--edition", choices=["Free", "Prime"], required=True)
    args = parser.parse_args()
    run = NativeRun("ios-" + args.edition)
    run_started = time.time()
    source = ROOT / "ios"
    created = []
    validation = source / "Validation"
    try:
        if not run.run("source-provenance", [sys.executable, "ci/verify_source.py", "--out", str(run.out / "provenance")], 60):
            raise RuntimeError("Committed source provenance failed.")
        if not run.run("native-toolchain", [sys.executable, "ci/toolchain_probe.py", "--out", str(run.out / "toolchain"), "--expected-arch", "arm64"], 600):
            raise RuntimeError("The required native Apple toolchain failed.")
        result = subprocess.run(["xcrun", "simctl", "list", "devices", "available", "-j"], capture_output=True, text=True, check=True, timeout=150)
        devices = json.loads(result.stdout)["devices"].get(RUNTIME, [])
        phone = next((item for item in devices if item["name"] == "iPhone 16 Pro" and item.get("isAvailable")), None)
        tablet = next((item for item in devices if item["name"].startswith("iPad") and item.get("isAvailable")), None)
        if not run.check("iOS 18.5 iPhone and iPad device types available", bool(phone and tablet)):
            raise RuntimeError("The requested runtime/device pair was not installed.")
        owned = []
        for kind, template in [("iPhone", phone), ("iPad", tablet)]:
            name = f"DUSTORE-{args.edition}-{kind}-{uuid.uuid4().hex[:8]}"
            result = subprocess.run(["xcrun", "simctl", "create", name, template["deviceTypeIdentifier"], RUNTIME],
                                    capture_output=True, text=True, check=True, timeout=150)
            udid = result.stdout.strip()
            uuid.UUID(udid)
            created.append(udid)
            owned.append({"kind": kind, "name": name, "deviceType": template["deviceTypeIdentifier"], "udid": udid, "runtime": RUNTIME})
        run.details["ownedSimulators"] = owned
        env = os.environ.copy()
        env["DUSTORE_CI_EDITION"] = args.edition
        env["DUSTORE_SIMULATOR_UDID"] = owned[0]["udid"]
        env["DUSTORE_DEVICE_BUILD"] = "1"
        runtime = resolve_runtime()
        run.details["simulatorRuntimeWorkaround"] = {"primarySources": REFERENCES, "runtime": runtime,
                "deploymentTargetChanged": False, "deviceBuildChanged": False}
        env["DUSTORE_CI_SIMULATOR_DYLD_FALLBACK"] = runtime["fallbackLibraryPath"]
        env["DUSTORE_CI_SCHEME_ENV_HELPER"] = str(ROOT / "ci/ios_simulator_environment.py")
        env["SIMCTL_CHILD_DYLD_FALLBACK_LIBRARY_PATH"] = runtime["fallbackLibraryPath"]
        env["SIMCTL_CHILD_DUSTORE_CI_SIMULATOR_DYLD_FALLBACK"] = runtime["fallbackLibraryPath"]
        phone_id = owned[0]["udid"]
        # A real launch catches dynamic-loader failures before an entire XCTest
        # suite waits for six absent application processes. XCTest still runs.
        for label, command, timeout in [
            ("preflight-phone-boot", ["xcrun", "simctl", "boot", phone_id], 150),
            ("preflight-phone-ready", ["xcrun", "simctl", "bootstatus", phone_id, "-b"], 240),
            ("preflight-project-generate", ["xcodegen", "generate"], 120),
            ("preflight-simulator-environment", [sys.executable, str(ROOT / "ci/ios_simulator_environment.py"),
                "--project", str(source / "DustoreX.xcodeproj"), "--report", str(run.out / "preflight-simulator-environment.json")], 180),
            ("preflight-native-debug-build", ["xcodebuild", "-project", "DustoreX.xcodeproj", "-scheme", "DustoreX-" + args.edition,
                "-configuration", args.edition + "Debug", "-destination", "id=" + phone_id,
                "-derivedDataPath", "Validation/Derived-" + args.edition + "Debug", "CODE_SIGNING_ALLOWED=NO", "build"], 300),
        ]:
            if not run.run(label, command, timeout, source, env):
                raise RuntimeError("Native iOS launch preflight failed: " + label)
        simulator_app = validation / ("Derived-" + args.edition + "Debug") / "Build/Products" / (args.edition + "Debug-iphonesimulator") / "DustoreX.app"
        run.run("preflight-simulator-app-load-commands", ["otool", "-L", str(simulator_app / "DustoreX")], 60)
        run.run("preflight-simulator-signing-diagnostic", ["codesign", "-dvv", str(simulator_app)], 60, required=False)
        if not run.run("preflight-owned-app-install", ["xcrun", "simctl", "install", phone_id, str(simulator_app)], 120):
            raise RuntimeError("The owned native debug app did not install.")
        launched = run.run("preflight-owned-app-launch", ["xcrun", "simctl", "launch", "--terminate-running-process",
                "--stdout=" + str(run.out / "preflight-app-stdout.txt"), "--stderr=" + str(run.out / "preflight-app-stderr.txt"),
                phone_id, "ru.dustore.launcher.ios", "-dustoreUITest", "-dustoreFixture", "empty", "-dustoreOfflineStore"], 60, source, env)
        time.sleep(3)
        run.run("preflight-native-screen", ["xcrun", "simctl", "io", phone_id, "screenshot", str(run.out / "preflight-native-screen.png")], 60)
        run.run("preflight-owned-app-system-log", ["xcrun", "simctl", "spawn", phone_id, "log", "show", "--last", "3m",
                "--style", "compact", "--predicate", 'process == "DustoreX" OR eventMessage CONTAINS[c] "ru.dustore.launcher.ios"'], 150, required=False)
        result = subprocess.run(["xcrun", "simctl", "spawn", phone_id, "launchctl", "list"], capture_output=True, text=True, timeout=60)
        services = [line for line in result.stdout.splitlines() if "ru.dustore.launcher.ios" in line]
        alive = result.returncode == 0 and any(line.split()[0].isdigit() and int(line.split()[0]) > 0 for line in services)
        run.check("native app remains alive after direct launch", launched and alive, {"services": services, "exitCode": result.returncode})
        if not launched or not alive:
            raise RuntimeError("The app failed native launch before XCTest; inspect preflight stderr/system log.")
        run.run("preflight-owned-app-stop", ["xcrun", "simctl", "terminate", phone_id, "ru.dustore.launcher.ios"], 60)
        phone_pass = run.run("iphone-build-analyze-unit-ui", ["bash", "Scripts/validate-apple.sh"], 2100, source, env)
        if phone_pass:
            run.run("unsigned-device-package", ["bash", "Scripts/package-unsigned-device.sh", args.edition], 120, source, env)
            tablet_id = owned[1]["udid"]
            if run.run("ipad-boot", ["xcrun", "simctl", "boot", tablet_id], 150):
                if run.run("ipad-boot-ready", ["xcrun", "simctl", "bootstatus", tablet_id, "-b"], 240):
                    tablet_result = validation / f"{args.edition}-iPad-Tests.xcresult"
                    run.run("ipad-native-ui-tests", ["xcodebuild", "-project", "DustoreX.xcodeproj", "-scheme", "DustoreX-" + args.edition,
                            "-configuration", args.edition + "Debug", "-destination", "id=" + tablet_id,
                            "-derivedDataPath", "Validation/Derived-" + args.edition + "Debug",
                            "-resultBundlePath", str(tablet_result), "-parallel-testing-enabled", "NO",
                            "-only-testing:DustoreXUITests", "CODE_SIGNING_ALLOWED=NO", "test"], 900, source, env)

        simulator_app = validation / ("Derived-" + args.edition + "Debug") / "Build/Products" / (args.edition + "Debug-iphonesimulator") / "DustoreX.app"
        if phone_pass and simulator_app.is_dir():
            for device in owned:
                identifier = device["udid"]
                if not run.run(device["kind"].lower() + "-install", ["xcrun", "simctl", "install", identifier, str(simulator_app)], 120, env=env):
                    continue
                for fixture in ["empty", "library", "transfer", "player"]:
                    name = device["kind"].lower() + "-" + fixture
                    if run.run(name + "-launch", ["xcrun", "simctl", "launch", "--terminate-running-process", identifier,
                            "ru.dustore.launcher.ios", "-dustoreUITest", "-dustoreFixture", fixture, "-dustoreOfflineStore"], 60, env=env):
                        # XCTest screenshots remain the primary interaction evidence;
                        # these supplementary whole-simulator frames document launch.
                        time.sleep(3)
                        run.run(name + "-screen", ["xcrun", "simctl", "io", identifier, "screenshot", str(run.out / (name + ".png"))], 60)
            packages = run.artifact / "packages"
            packages.mkdir(exist_ok=True)
            run.run("simulator-package", ["ditto", "-c", "-k", "--keepParent", str(simulator_app),
                    str(packages / f"DustoreX-{args.edition}-1.2.0-iOS18.5-SIMULATOR.app.zip")], 120)
        device_app = validation / ("Device-" + args.edition + "Release") / "Build/Products" / (args.edition + "Release-iphoneos") / "DustoreX.app"
        if device_app.is_dir():
            metadata = plistlib.loads((device_app / "Info.plist").read_bytes())
            run.details["unsignedDeviceApp"] = {"version": metadata.get("CFBundleShortVersionString"), "build": metadata.get("CFBundleVersion"),
                    "platforms": metadata.get("CFBundleSupportedPlatforms"), "bundleIdentifier": metadata.get("CFBundleIdentifier"),
                    "signedForPhysicalDevice": False, "physicallyInstalled": False}
            run.check("device package version/build/platform", metadata.get("CFBundleShortVersionString") == "1.2.0" and metadata.get("CFBundleVersion") == "3" and metadata.get("CFBundleSupportedPlatforms") == ["iPhoneOS"])
            run.check("no provisioning profile included", not (device_app / "embedded.mobileprovision").exists())
        package_dir = validation / "Packages" / args.edition
        if package_dir.is_dir():
            import shutil
            destination = run.artifact / "packages"
            destination.mkdir(exist_ok=True)
            for path in package_dir.iterdir():
                if path.is_file() and path.suffix in {".ipa", ".txt"}:
                    shutil.copyfile(path, destination / path.name)
    except Exception as error:
        run.check("native validation completed", False, f"{type(error).__name__}: {error}")
    finally:
        # Retain this app's newly created crash diagnostics only, never a host
        # environment dump or unrelated application's reports.
        import shutil
        crash_roots = [Path.home() / "Library/Logs/DiagnosticReports"]
        crash_roots += [Path.home() / "Library/Developer/CoreSimulator/Devices" / identifier / "data/Library/Logs/CrashReporter"
                        for identifier in created]
        for crash_root in crash_roots:
            if not crash_root.is_dir():
                continue
            for crash in sorted(crash_root.glob("DustoreX*")):
                if crash.is_file() and crash.suffix in {".ips", ".crash"} and crash.stat().st_mtime >= run_started and crash.stat().st_size <= 5 * 1024 * 1024:
                    destination = run.out / "owned-app-crashes" / crash.name
                    destination.parent.mkdir(exist_ok=True)
                    shutil.copyfile(crash, destination)
        # Keep native failures and named test attachments even when a suite aborts.
        if validation.is_dir():
            import shutil
            for path in validation.iterdir():
                if path.is_file() and path.suffix in {".json", ".log", ".txt"}:
                    shutil.copyfile(path, run.out / path.name)
            for bundle in sorted(validation.glob("*.xcresult")):
                if run.run(bundle.stem + "-test-summary", ["xcrun", "xcresulttool", "get", "test-results", "summary", "--path", str(bundle)], 120, required=False):
                    log = (run.out / (bundle.stem + "-test-summary.log")).read_text(encoding="utf-8")
                    try:
                        summary = json.loads(log.split("\n", 1)[1])
                        (run.out / (bundle.stem + "-test-summary.json")).write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
                    except (ValueError, IndexError):
                        pass  # Preserve raw native stdout even if a tool adds non-JSON notices.
                attachments = run.out / (bundle.stem + "-attachments")
                attachments.mkdir(exist_ok=True)
                run.run(bundle.stem + "-export-attachments", ["xcrun", "xcresulttool", "export", "attachments", "--path", str(bundle), "--output-path", str(attachments)], 120)
                result_dir = run.artifact / "test-results"
                result_dir.mkdir(exist_ok=True)
                run.run(bundle.stem + "-result-package", ["ditto", "-c", "-k", "--keepParent", str(bundle), str(result_dir / (bundle.name + ".zip"))], 120)
        for identifier in reversed(created):
            # Exact IDs created by this process only. Never reset all simulators.
            try:
                subprocess.run(["xcrun", "simctl", "shutdown", identifier], capture_output=True, timeout=60, check=False)
                result = subprocess.run(["xcrun", "simctl", "delete", identifier], capture_output=True, timeout=60, check=False)
                run.check("owned simulator deleted " + identifier, result.returncode == 0)
            except (OSError, subprocess.SubprocessError) as error:
                run.check("owned simulator deleted " + identifier, False, type(error).__name__)
    return run.finish("Xcode 16.4 native build/analyze/unit/UI checks on owned iOS 18.5 iPhone/iPad simulators. Device SDK output is explicitly unsigned. No real phone, physical controller or unrelated game was tested.")


if __name__ == "__main__":
    sys.exit(main())
