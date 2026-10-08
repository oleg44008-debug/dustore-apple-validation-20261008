#!/usr/bin/env python3
"""Apply Apple's iOS 18.5 simulator-only WebKit fallback to generated schemes."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
RUNTIME = "com.apple.CoreSimulator.SimRuntime.iOS-18-5"
REFERENCES = ["https://developer.apple.com/forums/thread/785964",
              "https://bugs.webkit.org/show_bug.cgi?id=293831"]


def resolve_runtime():
    response = subprocess.run(["xcrun", "simctl", "list", "runtimes", "-j"], capture_output=True,
                              text=True, check=True, timeout=150)
    runtime = next((r for r in json.loads(response.stdout)["runtimes"]
                    if r.get("identifier") == RUNTIME and r.get("isAvailable")), None)
    if not runtime or not runtime.get("bundlePath"):
        raise RuntimeError("The owned iOS 18.5 runtime path is unavailable.")
    bundle = Path(runtime["bundlePath"])
    fallback = bundle / "Contents/Resources/RuntimeRoot/System/Cryptexes/OS/usr/lib/swift"
    library = fallback / "libswiftWebKit.dylib"
    if not library.is_file():
        raise RuntimeError("Apple's documented simulator WebKit fallback library is missing.")
    return {"identifier": RUNTIME, "version": runtime.get("version"), "build": runtime.get("buildversion"),
            "bundlePath": str(bundle), "fallbackLibraryPath": str(fallback),
            "library": str(library), "librarySha256": hashlib.sha256(library.read_bytes()).hexdigest()}


def inject_environment(project: Path, runtime: dict):
    project = project.resolve()
    if not project.is_relative_to(ROOT) or project.suffix != ".xcodeproj":
        raise ValueError("Only this CI mirror's generated Xcode project may be changed.")
    variables = {"DYLD_FALLBACK_LIBRARY_PATH": runtime["fallbackLibraryPath"],
                 "DUSTORE_CI_SIMULATOR_DYLD_FALLBACK": runtime["fallbackLibraryPath"]}
    changed = []
    for scheme in sorted((project / "xcshareddata/xcschemes").glob("*.xcscheme")):
        data = scheme.read_bytes()
        tree = ET.fromstring(data)
        for action_name in ["LaunchAction", "TestAction"]:
            action = tree.find(action_name)
            if action is None:
                raise ValueError(f"Generated scheme has no {action_name}: {scheme.name}")
            # Explicit TestAction values reach hosted tests. UI tests explicitly
            # forward the custom variable into XCUIApplication.launchEnvironment.
            if action_name == "TestAction":
                action.set("shouldUseLaunchSchemeArgsEnv", "NO")
            environment = action.find("EnvironmentVariables")
            if environment is None:
                environment = ET.SubElement(action, "EnvironmentVariables")
            for key, value in variables.items():
                entry = next((e for e in environment.findall("EnvironmentVariable") if e.get("key") == key), None)
                if entry is None:
                    entry = ET.SubElement(environment, "EnvironmentVariable")
                entry.attrib.update({"key": key, "value": value, "isEnabled": "YES"})
        output = ET.tostring(tree, encoding="utf-8", xml_declaration=True) + b"\n"
        temporary = scheme.with_suffix(".ci-tmp")
        temporary.write_bytes(output)
        temporary.replace(scheme)
        changed.append({"scheme": scheme.name, "beforeSha256": hashlib.sha256(data).hexdigest(),
                        "afterSha256": hashlib.sha256(output).hexdigest(), "actions": ["LaunchAction", "TestAction"]})
    if len(changed) != 2:
        raise ValueError("Both explicit Free/Prime generated schemes must receive the simulator environment.")
    return changed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    report_path = args.report.resolve()
    if not report_path.is_relative_to(ROOT):
        raise ValueError("The workaround report must remain in the owned CI mirror.")
    runtime = resolve_runtime()
    result = {"kind": "apple-documented-simulator-only-webkit-workaround", "primarySources": REFERENCES,
              "runtime": runtime, "deviceBuildChanged": False, "deploymentTargetChanged": False,
              "schemes": inject_environment(args.project, runtime) if args.project else []}
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"runtime": RUNTIME, "verifiedLibrary": runtime["library"], "schemes": len(result["schemes"])}))


if __name__ == "__main__":
    main()
