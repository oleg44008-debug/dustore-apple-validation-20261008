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

RUNTIME = "com.apple.CoreSimulator.SimRuntime.iOS-18-5"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--edition", choices=["Free", "Prime"], required=True)
    args = parser.parse_args()
    run = NativeRun("ios-" + args.edition)
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
                if not run.run(device["kind"].lower() + "-install", ["xcrun", "simctl", "install", identifier, str(simulator_app)], 120):
                    continue
                for fixture in ["empty", "library", "transfer", "player"]:
                    name = device["kind"].lower() + "-" + fixture
                    if run.run(name + "-launch", ["xcrun", "simctl", "launch", "--terminate-running-process", identifier,
                            "ru.dustore.launcher.ios", "-dustoreUITest", "-dustoreFixture", fixture, "-dustoreOfflineStore"], 60):
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
        # Keep native failures and named test attachments even when a suite aborts.
        if validation.is_dir():
            import shutil
            for path in validation.iterdir():
                if path.is_file() and path.suffix in {".json", ".log", ".txt"}:
                    shutil.copyfile(path, run.out / path.name)
            for bundle in sorted(validation.glob("*.xcresult")):
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
