#!/usr/bin/env python3
"""Native diagnostic only: pinned Wine, own command fixtures/prefixes, bounded stages.

Does not change the launcher, exact Converter Core, an existing Wine installation,
Gatekeeper, or a player's prefix. No game/D3D/FPS claims. Retains own evidence.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import signal
import subprocess
import time
import urllib.request
import uuid

WINE_URL = "https://github.com/Gcenx/macOS_Wine_builds/releases/download/11.0_1/wine-stable-11.0_1-osx64.tar.xz"
WINE_HASH = "b50dc50ec7f41d58b115a6b685d4d1315ba3c797bd3aa0f49213f2703cb82388"
WINE_BYTES = 185303032
DXVK_URL = "https://github.com/Gcenx/DXVK-macOS/releases/download/v1.10.3-20230507-repack/dxvk-macOS-async-v1.10.3-20230507-repack-builtin.tar.gz"
DXVK_HASH = "810b1e5caf8ce975b784fae866a130ad23fa0ea233b0e5609cbc4a45f3ef6f00"
DXVK_BYTES = 2785833
TOKEN = "DUSTORE-INTEL-DIAGNOSTIC-OK"


def download(url: str, expected: str, size: int, path: Path) -> None:
    digest = hashlib.sha256()
    with urllib.request.urlopen(url, timeout=90) as response, path.open("xb") as stream:
        received = 0
        while data := response.read(1024 * 1024):
            received += len(data)
            if received > size:
                raise ValueError("Pinned archive exceeds its expected size")
            digest.update(data)
            stream.write(data)
    if received != size or digest.hexdigest() != expected:
        raise ValueError("Pinned archive size/SHA-256 mismatch")


class Diagnostic:
    def __init__(self, root: Path) -> None:
        self.root = root
        self.steps: list[dict] = []
        self.server: Path | None = None
        self.environment = os.environ.copy()
        self.environment.update({"PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "WINEDEBUG": "err+all,warn+module,fixme-all",
                                 "WINEDLLOVERRIDES": "mscoree,mshtml="})
        # No host environment is printed; subprocess argument lists contain owned paths only.
        self.environment.pop("WINEPREFIX", None)
        self.environment.pop("DXVK_CONFIG_FILE", None)
        self.environment.pop("DXMT_CONFIG", None)
        self.environment.pop("D3DMETAL_LIBRARY_PATH", None)

    def run(self, name: str, args: list[str], timeout: int, env: dict | None = None, sample: bool = False) -> dict:
        output = self.root / (name + ".log")
        watch = time.monotonic()
        timed_out = False
        sampled = False
        effective = self.environment.copy()
        if env:
            effective.update(env)
        print(json.dumps({"stage": name, "timeoutSeconds": timeout, "command": args}), flush=True)
        with output.open("wb") as stream:
            process = subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=stream, stderr=subprocess.STDOUT,
                                       env=effective, start_new_session=True)
            while process.poll() is None:
                elapsed = time.monotonic() - watch
                if sample and not sampled and elapsed > 20:
                    sampled = True
                    with (self.root / (name + ".sample.log")).open("wb") as stack:
                        try:
                            subprocess.run(["/usr/bin/sample", str(process.pid), "2", "-file", str(self.root / (name + ".sample.txt"))],
                                           stdout=stack, stderr=subprocess.STDOUT, timeout=10)
                        except (subprocess.TimeoutExpired, OSError) as error:
                            stack.write(str(error).encode())
                if elapsed >= timeout:
                    timed_out = True
                    try:
                        os.killpg(process.pid, signal.SIGTERM)
                    except ProcessLookupError:
                        pass
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        try:
                            os.killpg(process.pid, signal.SIGKILL)
                        except ProcessLookupError:
                            pass
                    break
                time.sleep(0.2)
            process.wait(timeout=10)
        record = {"stage": name, "command": args, "processId": process.pid, "exitCode": process.returncode,
                  "timedOut": timed_out, "elapsedSeconds": round(time.monotonic() - watch, 3), "log": output.name,
                  "tokenSeen": TOKEN in output.read_text(errors="replace")}
        self.steps.append(record)
        self.save()
        # Process names only: neither full environment nor token-bearing CI command lines are captured.
        with (self.root / (name + ".processes.txt")).open("wb") as stream:
            subprocess.run(["/bin/ps", "-axo", "pid,ppid,stat,etime,comm"], stdout=stream, stderr=subprocess.STDOUT, timeout=10)
        return record

    def prefix_environment(self, prefix: Path) -> dict:
        prefix.resolve().relative_to(self.root.resolve())
        prefix.mkdir(parents=True, exist_ok=True)
        return {"WINEPREFIX": str(prefix)}

    def stop_prefix(self, name: str, prefix: Path) -> None:
        if self.server is not None:
            self.run(name + "-owned-prefix-stop", [str(self.server), "-k"], 20, self.prefix_environment(prefix))

    def save(self) -> None:
        (self.root / "diagnostic.json").write_text(json.dumps({"schema": 1, "host": {"system": platform.system(),
            "architecture": platform.machine(), "macOS": platform.mac_ver()[0]}, "wineArchiveSha256": WINE_HASH,
            "dxvkArchiveSha256": DXVK_HASH, "scope": "Owned Windows command fixture and exact pinned Wine bootstrap only. No original game or graphics device.",
            "productionSourceModified": False, "exactCoreModified": False, "wrapperIsCoreScriptReproduction": True,
            "gstreamerFrameworkPresent": Path("/Library/Frameworks/GStreamer.framework").exists(),
            "graphicsDeviceCreated": False, "gameFpsMeasured": False, "steps": self.steps}, ensure_ascii=False, indent=2), encoding="utf-8")


def wrapper_script(wine: Path, skip_notification: bool = False, skip_wait: bool = False) -> str:
    # Verbatim bootstrap structure from byte-identical Core CompatibilityPackager;
    # only the owned game/path and explicit runtime hint vary. This is a diagnostic reproduction.
    notification = "" if skip_notification else "  /usr/bin/osascript -e 'display notification \"Первый запуск: Wine готовит окружение игры. Это займёт до пары минут.\" with title \"DustoreX\"' >/dev/null 2>&1 || true\n"
    wait = "" if skip_wait else '  if [ -n "$WINESERVER" ]; then "$WINESERVER" -w || true; fi\n'
    return ('#!/bin/bash\nset -e\nHERE="$(cd -- "$(dirname -- "$0")" && pwd)"\n'
            'WINE="$DUSTOREX_WINE"\nexport WINEPREFIX="$HOME/Library/Application Support/DustoreX/Wine/diagnostic"\n'
            'export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:-mscoree,mshtml=}"\nexport WINEDEBUG="${WINEDEBUG:--all}"\n'
            'mkdir -p -- "$WINEPREFIX"\nif [ ! -f "$WINEPREFIX/system.reg" ]; then\n' + notification +
            '  "$WINE" wineboot --init >/dev/null 2>&1 || true\n'
            '  WINESERVER="$(dirname "$WINE")/wineserver"; [ -x "$WINESERVER" ] || WINESERVER="$(command -v wineserver || true)"\n' + wait +
            'fi\ncd -- "$HERE/game"\nexec "$WINE" WineSmoke.exe "$@"\n')


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--runtime-only", action="store_true")
    args = parser.parse_args()
    if platform.system() != "Darwin":
        parser.error("This diagnostic must execute on an actual macOS host")
    root = Path(args.out).resolve() / ("owned-intel-wine-" + uuid.uuid4().hex)
    root.mkdir(parents=True, exist_ok=False)
    probe = Diagnostic(root)
    probe.run("host", ["/usr/bin/sw_vers"], 10)
    probe.run("cpu-capabilities", ["/usr/sbin/sysctl", "hw.optional.avx1_0", "hw.optional.avx2_0", "hw.model"], 10)
    probe.run("notification", ["/usr/bin/osascript", "-e", 'display notification "Owned DUSTORE runtime diagnostic" with title "DUSTORE CI"'], 15)
    archive = root / "wine.tar.xz"
    download(WINE_URL, WINE_HASH, WINE_BYTES, archive)
    extracted = root / "wine-extracted"
    extracted.mkdir()
    probe.run("extract-pinned-wine", ["/usr/bin/tar", "-xJf", str(archive), "-C", str(extracted)], 120)
    candidates = [path for path in extracted.rglob("wine*") if path.is_file() and path.name in ("wine", "wine64") and path.parent.name == "bin"]
    wine = min(candidates, key=lambda path: len(str(path)))
    wine_home = wine.parent.parent
    probe.server = wine.parent / "wineserver"
    probe.run("wine-native-architecture", ["/usr/bin/file", str(wine)], 10)
    probe.run("wine-native-dependencies", ["/usr/bin/otool", "-L", str(wine)], 10)
    probe.run("wine-version", [str(wine), "--version"], 20)
    raw_prefix = root / "prefix-pure-command"
    try:
        probe.run("pure-wine-first-cmd", [str(wine), "cmd", "/c", "echo", TOKEN], 120, probe.prefix_environment(raw_prefix), sample=True)
    finally:
        probe.stop_prefix("pure-command", raw_prefix)

    dxvk_archive = root / "dxvk.tar.gz"
    download(DXVK_URL, DXVK_HASH, DXVK_BYTES, dxvk_archive)
    dxvk_root = root / "dxvk-extracted"
    dxvk_root.mkdir()
    probe.run("extract-pinned-dxvk", ["/usr/bin/tar", "-xzf", str(dxvk_archive), "-C", str(dxvk_root)], 30)
    for arch, wine_dir in (("x64", "x86_64-windows"), ("x32", "i386-windows")):
        target = wine_home / "lib/wine" / wine_dir
        if not target.is_dir():
            continue
        for name in ("d3d11.dll", "dxgi.dll", "d3d10core.dll", "d3d10.dll", "d3d10_1.dll"):
            source = next((path for path in dxvk_root.rglob(name) if arch in path.parts or wine_dir in path.parts), None)
            if source:
                if (target / name).exists():
                    shutil.copyfile(target / name, target / (name + ".wined3d"))
                shutil.copyfile(source, target / name)
    initialized = root / "prefix-explicit-boot"
    env = probe.prefix_environment(initialized)
    try:
        probe.run("dxvk-wineboot", [str(wine), "wineboot", "--init"], 120, env, sample=True)
        probe.run("dxvk-wineserver-wait", [str(probe.server), "-w"], 90, env, sample=True)
        probe.run("dxvk-initialized-cmd", [str(wine), "cmd", "/c", "echo", TOKEN], 90, env, sample=True)
    finally:
        probe.stop_prefix("explicit-boot", initialized)

    if not args.runtime_only:
        cmd = next(path for path in wine_home.rglob("cmd.exe") if "x86_64-windows" in path.parts)
        for name, skip_notification, skip_wait in (("wrapper-original", False, False), ("wrapper-no-notification", True, False), ("wrapper-no-server-wait", True, True)):
            fixture = root / name
            (fixture / "game").mkdir(parents=True)
            shutil.copyfile(cmd, fixture / "game/WineSmoke.exe")
            script = fixture / "launch"
            script.write_text(wrapper_script(wine, skip_notification, skip_wait), encoding="utf-8")
            home = fixture / "home"
            home.mkdir()
            prefix = home / "Library/Application Support/DustoreX/Wine/diagnostic"
            env = {"HOME": str(home), "DUSTOREX_WINE": str(wine)}
            try:
                probe.run(name, ["/bin/bash", "-x", str(script), "/c", "echo", TOKEN], 120, env, sample=True)
            finally:
                probe.stop_prefix(name, prefix)
    probe.save()
    print(json.dumps({"diagnosticReport": str(root / "diagnostic.json"), "steps": len(probe.steps)}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
