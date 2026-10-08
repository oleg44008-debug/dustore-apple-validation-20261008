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
import threading
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
NATIVE_ENVIRONMENT_KEYS = {"HOME", "USER", "LOGNAME", "LANG", "LC_CTYPE", "LC_ALL", "TMPDIR",
                           "__CF_USER_TEXT_ENCODING", "SECURITYSESSIONID"}


def environment_bytes(environment: dict[str, str]) -> int:
    """Size only. Never write inherited values or credential-bearing names."""
    return sum(len((key + "=" + value).encode("utf-8")) + 1 for key, value in environment.items())


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
        self.mode = "bootstrap"
        self.wrapper_reproduced = False
        self.dxvk_connected = False
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
                    self.sample_owned_wine_children(name, process.pid)
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
                  "tokenSeen": TOKEN in output.read_text(errors="replace"),
                  "environmentVariableCount": len(effective), "environmentByteCount": environment_bytes(effective),
                  "benignPaddingBytes": len(effective.get("DUSTORE_TEST_PADDING", "")),
                  "inheritsActionsEnvironment": any(key.startswith(("GITHUB_", "ACTIONS_", "RUNNER_")) for key in effective)}
        self.steps.append(record)
        self.save()
        # Process names only: neither full environment nor token-bearing CI command lines are captured.
        with (self.root / (name + ".processes.txt")).open("wb") as stream:
            subprocess.run(["/bin/ps", "-axo", "pid,ppid,stat,etime,comm"], stdout=stream, stderr=subprocess.STDOUT, timeout=10)
        return record

    def sample_owned_wine_children(self, name: str, parent: int) -> None:
        # Detached Wine servers/bootstraps can be reparented to launchd. Verify
        # their private environment contains this probe's unique root before
        # sampling. Never write that environment or full CI command line.
        snapshot = subprocess.run(["/bin/ps", "-axo", "pid=,comm="], capture_output=True, text=True, timeout=10).stdout
        sampled = 0
        for line in snapshot.splitlines():
            fields = line.strip().split(maxsplit=1)
            if len(fields) != 2 or not fields[0].isdigit():
                continue
            pid, command = int(fields[0]), fields[1]
            if pid == parent or not ("wine" in command.lower() or command.lower().startswith("c:\\windows")):
                continue
            private = subprocess.run(["/bin/ps", "eww", "-p", str(pid), "-o", "command="], capture_output=True, text=True, timeout=10).stdout
            if str(self.root) not in private:
                continue
            with (self.root / f"{name}.child-{pid}.sample.log").open("wb") as log:
                try:
                    subprocess.run(["/usr/bin/sample", str(pid), "2", "-file", str(self.root / f"{name}.child-{pid}.sample.txt")],
                                   stdout=log, stderr=subprocess.STDOUT, timeout=10)
                except (subprocess.TimeoutExpired, OSError) as error:
                    log.write(str(error).encode())
            sampled += 1
            if sampled >= 6:
                break

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
            "mode": self.mode, "productionSourceModified": False, "exactCoreModified": False,
            "wrapperIsCoreScriptReproduction": self.wrapper_reproduced, "dxvkConnected": self.dxvk_connected,
            "gstreamerFrameworkPresent": Path("/Library/Frameworks/GStreamer.framework").exists(),
            "inheritedSyncEnvironment": {key: self.environment.get(key) for key in ("WINEMSYNC", "WINEESYNC", "WINEFSYNC")},
            "baseEnvironmentVariableCount": len(self.environment), "baseEnvironmentByteCount": environment_bytes(self.environment),
            "graphicsDeviceCreated": False, "gameFpsMeasured": False, "steps": self.steps}, ensure_ascii=False, indent=2), encoding="utf-8")


class NativeVulkanDiagnostic(Diagnostic):
    """Independent QA mode. Never inherits Actions/auth or modifies a runtime."""

    LOG_LIMIT = 64 * 1024 * 1024

    def __init__(self, root: Path) -> None:
        super().__init__(root)
        self.mode = "nativeVulkanIsolation"
        self.environment = {key: value for key, value in os.environ.items() if key in NATIVE_ENVIRONMENT_KEYS}
        self.environment.update({"PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "WINEDEBUG": "-all",
                                 "WINEDLLOVERRIDES": "mscoree,mshtml="})
        self.details: dict = {"expectedHost": {"architecture": "x86_64", "macOS": "15.7.9", "model": "Macmini6,2"},
            "helperSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "timeBoundsSeconds": {"archiveDownload": 240, "archiveConnect": 20,
                "extraction": 150, "compilation": 45, "wineCommand": 60, "nativeAbi": 20,
                "pidOwnership": 3, "samplePerPid": 5, "ownedPrefixStop": 15, "ownedCleanupVerification": 35},
            "logLimitPerStageBytes": self.LOG_LIMIT, "nativeLibraries": [], "ownedProcessCaptures": [],
            "nativeControls": [], "errors": [], "logicalDeviceCreated": False, "surfaceCreated": False,
            "productionRuntimeModified": False, "icdOverrideApplied": False}
        self.capture_lock = threading.Lock()
        self.active_logs: list[tuple] = []
        self.save()

    def save(self) -> None:
        super().save()
        report = self.root / "diagnostic.json"
        data = json.loads(report.read_text(encoding="utf-8"))
        data["scope"] = "QA-only native Vulkan instance/physical-device enumeration and pinned Wine command bootstrap. No logical device, surface, original game, or FPS measurement."
        data["nativeVulkanIsolation"] = self.details
        report.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")

    def owned_path(self, path: Path) -> Path:
        resolved = path.resolve()
        resolved.relative_to(self.root.resolve())
        return resolved

    def quiet_command(self, args: list[str], timeout: int = 3) -> subprocess.CompletedProcess:
        return subprocess.run(args, stdin=subprocess.DEVNULL, capture_output=True, text=True,
                              env=self.environment, timeout=timeout)

    def owned_processes(self, deadline: float | None = None) -> list[dict]:
        """No environment reads: detached Wine PIDs must map this unique runtime."""
        snapshot = self.quiet_command(["/bin/ps", "-axo", "pid=,ppid=,comm="])
        results = []
        for line in snapshot.stdout.splitlines():
            if deadline is not None and time.monotonic() + 6 >= deadline:
                break
            fields = line.strip().split(maxsplit=2)
            if len(fields) != 3 or not fields[0].isdigit() or not fields[1].isdigit():
                continue
            pid, parent, command = int(fields[0]), int(fields[1]), fields[2]
            lower = command.lower()
            if not ("wine" in lower or lower.startswith("c:\\windows")):
                continue
            try:
                opened = self.quiet_command(["/usr/sbin/lsof", "-a", "-p", str(pid), "-Fn"])
                images = []
                for row in opened.stdout.splitlines():
                    if not row.startswith("n") or not Path(row[1:]).is_absolute():
                        continue
                    try:
                        path = self.owned_path(Path(row[1:]))
                    except (ValueError, OSError):
                        continue
                    if path.is_file():
                        images.append(str(path))
                if not any("wine-extracted" in Path(image).parts for image in images):
                    continue
                identity = self.quiet_command(["/bin/ps", "-p", str(pid), "-o", "lstart=,comm="]).stdout.strip()
                # Keep only verified owned names, PIDs and mapped private files.
                results.append({"pid": pid, "ppid": parent, "command": command,
                    "identity": identity, "ownership": "mapped-file-in-unique-runtime",
                    "ownedMappedFiles": sorted(set(images))})
                if len(results) >= 8:
                    break
            except (subprocess.TimeoutExpired, OSError):
                continue
        return results

    def capture_detached(self, name: str, parent: int, stage_deadline: float) -> None:
        result = {"stage": name, "requestedAtSeconds": 12, "processes": [], "errors": []}
        try:
            for entry in self.owned_processes(stage_deadline - 6):
                if time.monotonic() + 6 >= stage_deadline:
                    result["errors"].append("Capture budget exhausted before sampling all owned PIDs")
                    break
                pid = entry["pid"]
                # Recheck start time/name to reject a reused PID before attachment.
                identity = self.quiet_command(["/bin/ps", "-p", str(pid), "-o", "lstart=,comm="]).stdout.strip()
                if not identity or identity != entry["identity"]:
                    result["errors"].append(f"PID {pid} exited or changed identity before capture")
                    continue
                sample = self.root / f"{name}.owned-{pid}.sample.txt"
                try:
                    record = self.quiet_command(["/usr/bin/sample", str(pid), "2", "-file", str(sample)], timeout=5)
                    (self.root / f"{name}.owned-{pid}.sample.log").write_text(
                        record.stdout + record.stderr, encoding="utf-8")
                    entry["sample"] = sample.name
                    entry["sampleExitCode"] = record.returncode
                    entry["sampleExists"] = sample.is_file()
                    entry["initialCommand"] = pid == parent
                except (subprocess.TimeoutExpired, OSError) as error:
                    entry["sampleError"] = f"{type(error).__name__}: {error}"
                result["processes"].append(entry)
        except (subprocess.TimeoutExpired, OSError) as error:
            result["errors"].append(f"{type(error).__name__}: {error}")
        (self.root / f"{name}.owned-processes.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        with self.capture_lock:
            self.details["ownedProcessCaptures"].append(result)

    def run_bounded(self, name: str, args: list[str], timeout: int, env: dict | None = None,
                    capture_detached: bool = False, sample_native: bool = False) -> dict:
        output = self.root / (name + ".log")
        effective = self.environment.copy()
        effective.update(env or {})
        started = time.monotonic()
        deadline = started + timeout
        stop_reason = None
        state = {"bytesObserved": 0, "bytesWritten": 0, "logLimitReached": False, "readerError": None}
        collector = None
        native_sampled = False
        print(json.dumps({"stage": name, "timeoutSeconds": timeout, "command": args}), flush=True)
        process = subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT, env=effective, start_new_session=True)

        def drain() -> None:
            try:
                with output.open("wb") as stream:
                    assert process.stdout is not None
                    while block := process.stdout.read(65536):
                        state["bytesObserved"] += len(block)
                        available = self.LOG_LIMIT - state["bytesWritten"]
                        if available > 0:
                            chunk = block[:available]
                            stream.write(chunk)
                            state["bytesWritten"] += len(chunk)
                        if len(block) > available:
                            state["logLimitReached"] = True
            except Exception as error:
                state["readerError"] = f"{type(error).__name__}: {error}"

        reader = threading.Thread(target=drain, daemon=True)
        reader.start()
        while process.poll() is None:
            now = time.monotonic()
            if capture_detached and collector is None and now - started >= 12:
                collector = threading.Thread(target=self.capture_detached,
                    args=(name, process.pid, deadline), daemon=True)
                collector.start()
            if sample_native and not native_sampled and now - started >= 4:
                native_sampled = True
                sample = self.root / (name + ".sample.txt")
                try:
                    data = self.quiet_command(["/usr/bin/sample", str(process.pid), "2", "-file", str(sample)], timeout=5)
                    (self.root / (name + ".sample.log")).write_text(data.stdout + data.stderr, encoding="utf-8")
                except (subprocess.TimeoutExpired, OSError) as error:
                    (self.root / (name + ".sample.log")).write_text(f"{type(error).__name__}: {error}", encoding="utf-8")
            if now >= deadline or state["logLimitReached"] or state["readerError"]:
                stop_reason = "log-limit" if state["logLimitReached"] else "log-reader-error" if state["readerError"] else "timeout"
                try:
                    os.killpg(process.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
                try:
                    process.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    try:
                        os.killpg(process.pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                break
            time.sleep(0.1)
        process.wait(timeout=5)
        if collector is not None:
            collector.join(timeout=max(0, deadline - time.monotonic()) + 4)
        reader.join(timeout=4)
        if reader.is_alive() and process.stdout is not None:
            # Wine children inherit the log pipe. Prefix cleanup below releases
            # them; never wait indefinitely for a detached child's stdout.
            state["readerStillOpen"] = True
        text = output.read_text(errors="replace") if output.exists() else ""
        record = {"stage": name, "command": args, "processId": process.pid,
            "exitCode": process.returncode, "timedOut": stop_reason == "timeout", "stopReason": stop_reason,
            "timeoutSeconds": timeout, "elapsedSeconds": round(time.monotonic() - started, 3), "log": output.name,
            "tokenSeen": TOKEN in text, "environmentVariableCount": len(effective),
            "environmentByteCount": environment_bytes(effective),
            "inheritsActionsEnvironment": any(key.startswith(("GITHUB_", "ACTIONS_", "RUNNER_")) for key in effective),
            "logCapture": dict(state)}
        self.steps.append(record)
        if reader.is_alive():
            self.active_logs.append((reader, state, record))
        self.save()
        return record

    def stop_prefix(self, name: str, prefix: Path) -> None:
        if self.server is None:
            return
        record = self.run_bounded(name + "-owned-prefix-stop", [str(self.server), "-k"], 15,
                                  {**self.prefix_environment(prefix), "WINEDEBUG": "-all"})
        cleanup = {"stage": name, "stop": record, "remainingOwnedProcesses": [], "errors": []}
        deadline = time.monotonic() + 35
        try:
            time.sleep(0.3)
            remaining = self.owned_processes(deadline - 8)
            for entry in remaining:
                # Kill no global names/groups. A surviving process must still
                # map this exact private runtime and match its start identity.
                identity = self.quiet_command(["/bin/ps", "-p", str(entry["pid"]), "-o", "lstart=,comm="]).stdout.strip()
                if not identity or identity != entry["identity"]:
                    continue
                os.kill(entry["pid"], signal.SIGTERM)
                entry["ownedFallbackSignal"] = "SIGTERM"
                cleanup["remainingOwnedProcesses"].append(entry)
            if remaining:
                time.sleep(0.3)
                for entry in self.owned_processes(deadline):
                    if time.monotonic() + 3 >= deadline:
                        cleanup["errors"].append("Owned cleanup verification budget exhausted")
                        break
                    identity = self.quiet_command(["/bin/ps", "-p", str(entry["pid"]), "-o", "lstart=,comm="]).stdout.strip()
                    if identity and identity == entry["identity"]:
                        os.kill(entry["pid"], signal.SIGKILL)
                        cleanup["remainingOwnedProcesses"].append({**entry, "ownedFallbackSignal": "SIGKILL"})
        except (subprocess.TimeoutExpired, OSError) as error:
            cleanup["errors"].append(f"{type(error).__name__}: {error}")
        (self.root / (name + ".cleanup.json")).write_text(json.dumps(cleanup, indent=2), encoding="utf-8")
        self.finish_logs()

    def finish_logs(self) -> None:
        pending = []
        for reader, state, record in self.active_logs:
            reader.join(timeout=2)
            record["logCapture"] = {**state, "readerStillOpen": reader.is_alive()}
            if reader.is_alive():
                pending.append((reader, state, record))
        self.active_logs = pending

    def collect_owned_crashes(self, record: dict, binary: Path) -> None:
        # System reports may appear asynchronously. Match BOTH the exact owned
        # executable path and PID; do not copy unrelated/user crash reports.
        reports = Path.home() / "Library/Logs/DiagnosticReports"
        copied = []
        deadline = time.monotonic() + 5
        if record["exitCode"] is not None and record["exitCode"] < 0 and reports.is_dir():
            while time.monotonic() < deadline and not copied:
                for path in reports.glob("native_vulkan_probe*.ips"):
                    if path.stat().st_size > 16 * 1024 * 1024 or time.time() - path.stat().st_mtime > 90:
                        continue
                    text = path.read_text(errors="replace")
                    try:
                        _, _, body = text.partition("\n")
                        report = json.loads(body)
                    except json.JSONDecodeError:
                        continue
                    if report.get("pid") != record["processId"] or report.get("procPath") != str(binary):
                        continue
                    target = self.root / (record["stage"] + ".owned-crash.ips.txt")
                    target.write_text(text, encoding="utf-8")
                    copied.append(target.name)
                if not copied:
                    time.sleep(0.25)
        record["ownedCrashReports"] = copied

    def seal(self) -> None:
        self.finish_logs()
        if self.active_logs:
            self.details["errors"].append("Owned log reader still open after prefix cleanup; raw capture incomplete")
        self.save()
        entries = []
        for path in sorted(self.root.iterdir()):
            if path.is_file() and not path.is_symlink() and path.suffix in {".json", ".log", ".txt"} and path.name != "evidence-sha256.json":
                entries.append({"path": path.name, "bytes": path.stat().st_size,
                                "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
        (self.root / "evidence-sha256.json").write_text(json.dumps({"schema": 1, "files": entries}, indent=2), encoding="utf-8")


def native_vulkan_main(out: str) -> int:
    if platform.machine() != "x86_64":
        raise RuntimeError("Native Vulkan isolation requires actual Intel x86_64")
    root = Path(out).resolve() / ("owned-intel-wine-" + uuid.uuid4().hex)
    root.mkdir(parents=True, exist_ok=False)
    probe = NativeVulkanDiagnostic(root)
    try:
        if platform.mac_ver()[0] != "15.7.9":
            raise RuntimeError("Host macOS differs from the compared Intel 15.7.9 evidence; do not silently compare versions")
        probe.run_bounded("host", ["/usr/bin/sw_vers"], 10)
        cpu = probe.run_bounded("cpu-capabilities", ["/usr/sbin/sysctl", "hw.optional.avx1_0", "hw.optional.avx2_0", "hw.model"], 10)
        cpu_text = (root / cpu["log"]).read_text(errors="replace")
        probe.details["hostModelMatchesPriorEvidence"] = "hw.model: Macmini6,2" in cpu_text
        if not probe.details["hostModelMatchesPriorEvidence"]:
            raise RuntimeError("Host model differs from the compared Intel evidence; a different Metal host is not a paired control")
        probe.run_bounded("metal-host-displays", ["/usr/sbin/system_profiler", "SPDisplaysDataType", "-json"], 20)
        archive = root / "wine.tar.xz"
        fetched = probe.run_bounded("download-pinned-wine", ["/usr/bin/curl", "-q", "--fail", "--location", "--silent", "--show-error",
            "--connect-timeout", "20", "--max-time", "240", "--max-filesize", str(WINE_BYTES), "--output", str(archive), WINE_URL], 240)
        if fetched["exitCode"] or fetched["stopReason"] or not archive.is_file() or archive.stat().st_size != WINE_BYTES:
            raise RuntimeError("Bounded pinned Wine download failed or has a different size")
        if hashlib.sha256(archive.read_bytes()).hexdigest() != WINE_HASH:
            raise RuntimeError("Pinned Wine archive SHA-256 mismatch")
        probe.details["archiveVerified"] = {"bytes": archive.stat().st_size, "sha256": WINE_HASH}
        extracted = root / "wine-extracted"
        extracted.mkdir()
        extracted_step = probe.run_bounded("extract-pinned-wine", ["/usr/bin/tar", "-xJf", str(archive), "-C", str(extracted)], 150)
        if extracted_step["exitCode"] or extracted_step["stopReason"]:
            raise RuntimeError("Pinned archive extraction did not finish")
        candidates = [path for path in extracted.rglob("wine*") if path.is_file() and path.name in ("wine", "wine64") and path.parent.name == "bin"]
        wine = probe.owned_path(min(candidates, key=lambda path: len(str(path))))
        probe.server = wine.parent / "wineserver"
        probe.owned_path(probe.server)
        probe.run_bounded("wine-native-architecture", ["/usr/bin/file", str(wine)], 10)
        version = probe.run_bounded("wine-version", [str(wine), "--version"], 15)
        if version["exitCode"]:
            raise RuntimeError("Pinned Wine version check failed")
        source = Path(__file__).with_name("native_vulkan_probe.c")
        source_bytes = source.read_bytes()
        probe.details["nativeFixtureSourceSha256"] = hashlib.sha256(source_bytes).hexdigest()
        (root / "native-probe-source.txt").write_bytes(source_bytes)
        owned_source = root / "native_vulkan_probe.c"
        owned_source.write_bytes(source_bytes)
        binary = root / "native_vulkan_probe"
        compile_step = probe.run_bounded("compile-native-vulkan-fixture", ["/usr/bin/xcrun", "clang", "-arch", "x86_64", "-std=c11",
            "-Wall", "-Wextra", "-Werror", "-O0", "-g", str(owned_source), "-o", str(binary)], 45)
        if compile_step["exitCode"] or compile_step["stopReason"]:
            raise RuntimeError("Native ABI fixture compilation failed")
        probe.details["nativeFixtureBinarySha256"] = hashlib.sha256(binary.read_bytes()).hexdigest()
        prefix = root / "prefix-native-vulkan-seh"
        debug = "-all,err+all,warn+all,trace+seh,trace+vulkan,trace+explorer,trace+wineboot,trace+setupapi,trace+sync,trace+server,+timestamp,+pid"
        try:
            probe.run_bounded("wine-seh-bootstrap", [str(wine), "cmd", "/c", "echo", TOKEN], 60,
                {**probe.prefix_environment(prefix), "WINEDEBUG": debug}, capture_detached=True)
        finally:
            probe.stop_prefix("wine-seh-bootstrap", prefix)
        mapped = {Path(path).resolve() for capture in probe.details["ownedProcessCaptures"]
                  for process in capture["processes"] for path in process["ownedMappedFiles"]}
        inputs = []
        for basename, role in (("libvulkan.1.dylib", "vulkan-loader"), ("libMoltenVK.dylib", "moltenvk-direct")):
            values = list(extracted.rglob(basename))
            # Prefer the exact mapped image over a duplicate bundled dependency.
            values.sort(key=lambda path: (path.resolve() not in mapped, len(str(path))))
            if not values:
                probe.details["errors"].append(f"Pinned runtime did not contain {basename}")
                continue
            library = probe.owned_path(values[0])
            inputs.append((library, role))
        for library, role in inputs:
            metadata = {"path": str(library), "role": role, "sha256": hashlib.sha256(library.read_bytes()).hexdigest(),
                        "matchedWineMappedImage": library in mapped}
            probe.details["nativeLibraries"].append(metadata)
            probe.run_bounded(role + "-architecture", ["/usr/bin/file", str(library)], 10)
            probe.run_bounded(role + "-uuid", ["/usr/bin/xcrun", "dwarfdump", "--uuid", str(library)], 10)
            probe.run_bounded(role + "-dependencies", ["/usr/bin/otool", "-L", str(library)], 10)
            record = probe.run_bounded("native-" + role, [str(binary), str(library), str(root)], 20, sample_native=True)
            probe.collect_owned_crashes(record, binary)
            events = []
            for line in (root / record["log"]).read_text(errors="replace").splitlines():
                if line.startswith("DUSTORE_NATIVE_VULKAN "):
                    try:
                        events.append(json.loads(line.partition(" ")[2]))
                    except json.JSONDecodeError:
                        probe.details["errors"].append(f"Malformed fixture event in {record['log']}")
            probe.details["nativeControls"].append({"library": metadata, "run": record, "events": events})
        probe.details["classification"] = "Raw native and Wine results require comparison; an infrastructure pass is not proof the launcher game/graphics path works."
    except Exception as error:
        probe.details["errors"].append(f"{type(error).__name__}: {error}")
    finally:
        probe.seal()
        print(json.dumps({"diagnosticReport": str(root / "diagnostic.json"), "mode": probe.mode,
                          "helperErrors": probe.details["errors"]}, indent=2))
    return 1 if probe.details["errors"] else 0


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
    parser.add_argument("--variants", action="store_true", help="Short runtime phase-2 probes; no wrapper or DXVK mutation")
    parser.add_argument("--environment-pair", action="store_true", help="Minimal child environment versus own benign padding; no host-value dump")
    parser.add_argument("--native-vulkan", action="store_true", help="QA-only native Vulkan ABI versus detached Wine SEH/boot capture; no device/surface/game")
    args = parser.parse_args()
    if args.environment_pair and (args.variants or args.runtime_only):
        parser.error("--environment-pair must be used independently of other diagnostic modes")
    if args.native_vulkan and (args.environment_pair or args.variants or args.runtime_only):
        parser.error("--native-vulkan must be used independently of other diagnostic modes")
    if platform.system() != "Darwin":
        parser.error("This diagnostic must execute on an actual macOS host")
    if args.native_vulkan:
        return native_vulkan_main(args.out)
    root = Path(args.out).resolve() / ("owned-intel-wine-" + uuid.uuid4().hex)
    root.mkdir(parents=True, exist_ok=False)
    probe = Diagnostic(root)
    probe.mode = "environmentPair" if args.environment_pair else "rawVariants" if args.variants else "bootstrap"
    if args.environment_pair:
        # Only native identity/locale/temp values survive. Controlled padding is
        # the only changed independent variable; no host secrets enter Wine trace.
        probe.environment = {key: value for key, value in os.environ.items() if key in NATIVE_ENVIRONMENT_KEYS}
        probe.environment.update({"PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "WINEDEBUG": "err+all,warn+all,trace+process,trace+module",
                                  "WINEDLLOVERRIDES": "mscoree,mshtml="})
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
    if args.environment_pair:
        # WineHQ bug 60185 is an unconfirmed lead, not evidence of our cause:
        # https://list.winehq.org/hyperkitty/list/wine-bugs@list.winehq.org/thread/6KTCKCQL6EJQ7KA4QK25I3ABBP4LEXWE/
        # Repeat a small control after padding to separate this factor from host
        # load or previous Wine prefix state. Every stage owns a fresh prefix.
        for name, padding in (("minimal-small-before", 0), ("minimal-padding-5000", 5000),
                              ("minimal-padding-16000", 16000), ("minimal-small-after", 0)):
            prefix = root / ("prefix-" + name)
            env = probe.prefix_environment(prefix)
            if padding:
                env["DUSTORE_TEST_PADDING"] = "A" * padding
            try:
                probe.run(name, [str(wine), "cmd", "/c", "echo", TOKEN], 60, env, sample=True)
            finally:
                probe.stop_prefix(name, prefix)
        probe.save()
        print(json.dumps({"diagnosticReport": str(root / "diagnostic.json"), "steps": len(probe.steps)}, indent=2))
        return 0
    if args.variants:
        # A disabled renderer is diagnostic evidence only. Never ship that
        # environment or use it to label the production GPU path as verified.
        variants = [
            ("baseline-traced", {}),
            ("sync-disabled", {"WINEMSYNC": "0", "WINEESYNC": "0", "WINEFSYNC": "0"}),
            ("menu-builder-disabled", {"WINEDLLOVERRIDES": "winemenubuilder.exe=d;mscoree,mshtml="}),
            ("vulkan-disabled-diagnostic-only", {"WINEDLLOVERRIDES": "winevulkan=d;mscoree,mshtml="}),
            ("mac-driver-disabled-diagnostic-only", {"WINEDLLOVERRIDES": "winemac.drv=d;mscoree,mshtml="}),
        ]
        for name, extra in variants:
            prefix = root / ("prefix-" + name)
            env = probe.prefix_environment(prefix)
            env.update({"WINEDEBUG": "err+all,warn+all,trace+process,trace+module"})
            env.update(extra)
            try:
                probe.run(name, [str(wine), "cmd", "/c", "echo", TOKEN], 60, env, sample=True)
            finally:
                probe.stop_prefix(name, prefix)
        probe.save()
        print(json.dumps({"diagnosticReport": str(root / "diagnostic.json"), "steps": len(probe.steps)}, indent=2))
        return 0
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
    probe.dxvk_connected = True
    env = probe.prefix_environment(initialized)
    try:
        probe.run("dxvk-wineboot", [str(wine), "wineboot", "--init"], 120, env, sample=True)
        probe.run("dxvk-wineserver-wait", [str(probe.server), "-w"], 90, env, sample=True)
        probe.run("dxvk-initialized-cmd", [str(wine), "cmd", "/c", "echo", TOKEN], 90, env, sample=True)
    finally:
        probe.stop_prefix("explicit-boot", initialized)

    if not args.runtime_only:
        probe.wrapper_reproduced = True
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
