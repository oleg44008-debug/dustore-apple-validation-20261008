#!/usr/bin/env python3
"""Format the supplied brand PNG on an opaque App Store icon using native CG."""
from __future__ import annotations
import hashlib
import json
from pathlib import Path
import platform
import shutil
import struct
import sys
from native_helpers import NativeRun, ROOT

HELPER_SHA = "1bc905c45174e2a92f33cf96065fac64d660412fa27f2891db36400c9c636f01"
LOGO_SHA = "69cdb26a75f82302b8f476788a705bbdd6c1ed8d74934e3f05cb4df6a41468d4"


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    run = NativeRun("ios-app-icon")
    helper = ROOT / "ci/generate_app_icon.swift"
    logo = ROOT / "ios/App/Assets.xcassets/BrandMark.imageset/dustore-logo-original.png"
    fixture = ROOT / "owned-assets/ios-app-icon"
    run.details.update({"sourceManifestModified": False, "productionSourceModified": False,
                        "originalBrandMarkModified": False, "logoResampled": False,
                        "assetScope": "Original 856x636 supplied logo centered at native resolution on an opaque 1024x1024 sRGB canvas; no redesigned branding."})
    try:
        if not run.check("actual native Mac host", platform.system() == "Darwin"):
            raise RuntimeError("CoreGraphics icon generation requires a real Mac.")
        if not run.check("owner compositor byte-identical", sha(helper) == HELPER_SHA):
            raise RuntimeError("The compositor differs from the platform owner's reviewed helper.")
        if not run.check("original brand PNG byte-identical", sha(logo) == LOGO_SHA):
            raise RuntimeError("The supplied original branding resource changed.")
        if not run.run("source-provenance", [sys.executable, "ci/verify_source.py", "--out", str(run.out / "provenance")], 60):
            raise RuntimeError("Committed source provenance failed.")
        if not run.run("actual-Xcode-toolchain", ["/usr/bin/xcodebuild", "-version"], 30):
            raise RuntimeError("Xcode toolchain unavailable.")
        target_logo = fixture / "App/Assets.xcassets/BrandMark.imageset/dustore-logo-original.png"
        target_logo.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(logo, target_logo)
        if not run.run("native-opaque-icon-compositor", ["/usr/bin/xcrun", "swift", str(helper), str(fixture)], 120):
            raise RuntimeError("The native compositor did not finish successfully.")
        icon = fixture / "App/Assets.xcassets/AppIcon.appiconset/dustore-app-icon.png"
        raw = icon.read_bytes()
        if len(raw) < 33 or raw[:8] != b"\x89PNG\r\n\x1a\n" or raw[12:16] != b"IHDR":
            raise RuntimeError("The compositor did not create a valid PNG.")
        width, height, depth, color, _, _, _ = struct.unpack(">IIBBBBB", raw[16:29])
        run.check("App Store icon exact 1024x1024 dimensions", (width, height) == (1024, 1024))
        run.check("opaque RGB PNG without an alpha channel", color == 2 and depth == 8)
        run.check("original brand PNG preserved after compositor", sha(logo) == LOGO_SHA and sha(target_logo) == LOGO_SHA)
        output = run.out / "dustore-app-icon.png"
        shutil.copyfile(icon, output)
        provenance = {"helperSha256": HELPER_SHA, "originalLogoSha256": LOGO_SHA,
                      "outputSha256": sha(output), "outputBytes": output.stat().st_size,
                      "width": width, "height": height, "pngBitDepth": depth, "pngColorType": color,
                      "backgroundSrgb": [23, 23, 31], "logoRectangle": [84, 194, 856, 636],
                      "logoResampled": False, "nativeCoreGraphics": True}
        run.details["assetProvenance"] = provenance
        (run.out / "asset-provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    except Exception as error:
        run.check("native asset formatting completed", False, f"{type(error).__name__}: {error}")
    return run.finish("Native CoreGraphics formatting and verification of an opaque AppIcon from the exact original brand PNG. This job does not claim an iOS application build or runtime test passed.")


if __name__ == "__main__":
    sys.exit(main())
