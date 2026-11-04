"""Bounded, app-free diagnostics for the exact hosted Intel CoreSimulator tuple."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import platform
import plistlib
import re
import signal
import subprocess
import threading
import time
import uuid

XCODE = "/Applications/Xcode_26.6.app/Contents/Developer"
XCODE_VERSION = "Xcode 26.6\nBuild version 17F113"
RUNTIME = "com.apple.CoreSimulator.SimRuntime.iOS-26-5"
RUNTIME_BUILD = "23F77"
DEVICE_TYPE = "com.apple.CoreSimulator.SimDeviceType.iPhone-17-Pro"
RID = "iossimulator-x64"
BOOT_SECONDS = 240
CLEANUP_SECONDS = 30
DIAGNOSTIC_SECONDS = 180
RAW_DIAGNOSE_BYTES = 512 * 1024 * 1024
PR20_SHA = "499dcda91fa8b933b43c7ecb6f928834d099c1bc"
ENV_KEYS = ("DEVELOPER_DIR", "HOME", "CFFIXED_USER_HOME", "TMPDIR", "PATH", "LANG", "LC_ALL", "LC_CTYPE",
            "SIMULATOR_DEVICE_SET_PATH", "DOTNET_CLI_USE_MSBUILD_SERVER", "MSBUILDUSESERVER",
            "RUNNER_OS", "RUNNER_ARCH", "RUNNER_ENVIRONMENT", "ImageOS", "ImageVersion",
            "GITHUB_SHA", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "GITHUB_JOB")
LOG_PREDICATE = ('process CONTAINS[c] "Simulator" OR process == "launchd" OR process == "WindowServer" '
                 'OR process == "SpringBoard" OR process == "backboardd" '
                 'OR process CONTAINS[c] "Migrat" OR process == "ReportCrash" '
                 'OR subsystem CONTAINS[c] "CoreSimulator"')
TEXT_SUFFIXES = {".log", ".txt", ".json", ".ips", ".crash", ".plist"}


def utc():
    return datetime.now(timezone.utc).isoformat()


def require(value, message):
    if not value:
        raise RuntimeError(message)


def exact_runtime(inventory):
    matches = [r for r in inventory["runtimes"] if r["identifier"] == RUNTIME]
    require(len(matches) == 1, "Exactly one installed pinned iOS runtime is required.")
    runtime = matches[0]
    require(runtime.get("isAvailable") and runtime.get("version") == "26.5"
            and runtime.get("buildversion") == RUNTIME_BUILD
            and "x86_64" in runtime.get("supportedArchitectures", [])
            and any(d["identifier"] == DEVICE_TYPE for d in runtime.get("supportedDeviceTypes", [])),
            "Pinned runtime/build/x86_64/device support mismatch; no fallback.")
    return runtime


class Redactor:
    def __init__(self, environment):
        sensitive = ("TOKEN", "SECRET", "PASSWORD", "CREDENTIAL", "API_KEY", "ACCESS_KEY",
                     "PRIVATE_KEY", "AUTHORIZATION")
        self.secrets = sorted({v for k, v in environment.items() if len(v) >= 6
                               and any(part in k.upper() for part in sensitive)}, key=len, reverse=True)
        self.home = environment.get("HOME")

    def __call__(self, text):
        for value in self.secrets:
            text = text.replace(value, "<REDACTED>")
        if self.home:
            text = text.replace(self.home, "<HOME>")
        text = re.sub(r"(?i)(authorization[\"']?\s*[:=]\s*[\"']?)(?:bearer\s+)?[^\s,\"'}]+",
                      r"\1<REDACTED>", text)
        text = re.sub(r"\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]+)\b", "<REDACTED>", text)
        text = re.sub(r"(?i)(https?://[^\s?\"']+)\?[^\s\"']+", r"\1?<REDACTED>", text)
        text = re.sub(r"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "<EMAIL>", text)
        text = re.sub(r"(?i)((?:token|password|secret|credential|api[_-]?key)[\"']?\s*[:=]\s*[\"']?)"
                      r"[^\s,\"'}]+", r"\1<REDACTED>", text)
        return text


class Evidence:
    def __init__(self, path, redactor):
        self.path = path
        self.path.mkdir()
        self.redactor = redactor
        self.limit = 8 * 1024 * 1024
        self.total_limit = 100 * 1024 * 1024
        self.omissions = []

    def write(self, name, text):
        require(Path(name).name == name, "Evidence filenames must not escape the allowlisted directory.")
        target = self.path / name
        used = sum(f.stat().st_size for f in self.path.iterdir() if f != target)
        budget = max(0, min(self.limit, self.total_limit - used))
        data = self.redactor(text).encode("utf-8")
        if len(data) > budget:
            self.omissions.append({"file": name, "bytes": len(data), "keptBytes": budget, "reason": "size limit"})
        target.write_bytes(data[:budget])

    def report(self, name, value):
        self.write(name, json.dumps(value, indent=2, default=str))


class Probe:
    def __init__(self, output, mode, environment=None):
        self.output = Path(output).resolve()
        require(not self.output.exists(), "Use a new output directory; stale journals/devices are never reused.")
        self.output.mkdir(parents=True)
        self.native = dict(os.environ if environment is None else environment)
        require(mode in ("native", "harness"), "Unknown invocation mode.")
        self.mode = mode
        self.host_verified = False
        self.env = self.native | {"DEVELOPER_DIR": XCODE}
        if mode == "harness":
            self.env |= {"DOTNET_CLI_USE_MSBUILD_SERVER": "false", "MSBUILDUSESERVER": "0"}
        self.device_set = Path(self.native["HOME"]) / "Library/Developer/CoreSimulator/Devices"
        self.redactor = Redactor(self.native)
        self.evidence = Evidence(self.output / "evidence", self.redactor)
        self.private = self.output / "private"
        self.private.mkdir(mode=0o700)
        self.state = {"startedUtc": utc(), "mode": mode, "source": None, "passed": False,
                      "systemAppReady": False, "cleanupDone": False, "diagnosticsComplete": False,
                      "pins": {"xcodePath": XCODE, "xcodeVersion": XCODE_VERSION, "runtime": RUNTIME,
                               "runtimeBuild": RUNTIME_BUILD, "deviceType": DEVICE_TYPE, "rid": RID},
                      "bootBudgetSeconds": BOOT_SECONDS, "cleanupBudgetSeconds": CLEANUP_SECONDS}

    def save(self):
        (self.output / "state.json").write_text(json.dumps(self.state, indent=2))

    def run(self, label, command, timeout=30, check=True, environment=None, payload_path=None):
        started, stamp = time.monotonic(), utc()
        raw = self.private / (label + ".raw")
        timed_out = False
        oversized = False
        payload_oversized = False
        with raw.open("wb") as stream:
            process = subprocess.Popen(command, env=self.env if environment is None else environment,
                                       stdin=subprocess.DEVNULL, stdout=stream, stderr=subprocess.STDOUT,
                                       start_new_session=self.mode == "harness")
            try:
                while process.poll() is None:
                    remaining = timeout - (time.monotonic() - started)
                    oversized = raw.stat().st_size > self.evidence.limit
                    if payload_path is not None and payload_path.exists():
                        payload_oversized = sum(p.stat().st_size for p in payload_path.rglob("*")
                                                if p.is_file() and not p.is_symlink()) > RAW_DIAGNOSE_BYTES
                    if remaining <= 0 or oversized or payload_oversized:
                        timed_out = remaining <= 0
                        if self.mode == "harness":
                            os.killpg(process.pid, signal.SIGKILL)
                        else:
                            process.kill()
                        break
                    try:
                        process.wait(timeout=min(0.2, remaining))
                    except subprocess.TimeoutExpired:
                        pass
            finally:
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=1)
        with raw.open("rb") as stream:
            text = stream.read(self.evidence.limit).decode("utf-8", errors="replace")
        raw.unlink()
        status = {"command": command, "startedUtc": stamp, "finishedUtc": utc(),
                  "seconds": time.monotonic() - started, "timeoutSeconds": timeout,
                  "timedOut": timed_out, "outputLimitExceeded": oversized, "exitCode": process.returncode,
                  "rawDiagnoseLimitExceeded": payload_oversized,
                  "passed": process.returncode == 0 and not timed_out and not oversized and not payload_oversized}
        self.evidence.write(label + ".log", text)
        self.evidence.report(label + ".status.json", status)
        print(f"{label}: exit={process.returncode}, seconds={status['seconds']:.3f}, "
              f"timeout={timed_out}, outputLimit={oversized}", flush=True)
        if timed_out:
            raise TimeoutError(f"{label} exceeded {timeout:.3f}s; partial output preserved.")
        if oversized or payload_oversized:
            raise RuntimeError(f"{label} exceeded the bounded output limit; partial output preserved.")
        if check:
            require(process.returncode == 0, f"{label} exited {process.returncode}; see bounded evidence.")
        return subprocess.CompletedProcess(command, process.returncode, text, "")

    def host_snapshot(self, prefix):
        for label, command in (
                ("process-tree", ["ps", "-axo", "pid,ppid,pgid,stat,etime,pcpu,pmem,comm"]),
                ("disk", ["df", "-h"]),
                ("memory", ["vm_stat"]),
                ("hardware", ["sysctl", "hw.model", "hw.ncpu", "hw.memsize", "vm.swapusage"]),
                ("coresimulator-launchd", ["launchctl", "print",
                                          f"gui/{os.getuid()}/com.apple.CoreSimulator.CoreSimulatorService"]),
                ("windowserver-launchd", ["launchctl", "print", "system/com.apple.WindowServer"])):
            try:
                self.run(prefix + "-" + label, command, timeout=10, check=False)
            except (TimeoutError, RuntimeError) as error:
                self.state.setdefault("diagnosticErrors", []).append(str(error))

    def preflight(self):
        require(platform.system() == "Darwin" and platform.machine() == "x86_64",
                "This probe requires actual Intel macOS; local ARM evidence is not hosted Intel proof.")
        require(self.native.get("GITHUB_ACTIONS") == "true"
                and self.native.get("RUNNER_ENVIRONMENT") == "github-hosted",
                "Host diagnostics/provisioning are restricted to this fresh GitHub-hosted VM.")
        require(not self.native.get("SIMULATOR_DEVICE_SET_PATH"),
                "An overridden device set is not the exact native-runner baseline.")
        self.host_verified = True
        self.state["source"] = self.run("source", ["git", "rev-parse", "HEAD"]).stdout.strip()
        self.state["dirty"] = bool(self.run("source-status", ["git", "status", "--porcelain"]).stdout.strip())
        require(not self.state["dirty"], "Hosted source must be an exact clean commit.")
        harness = self.native | {"DEVELOPER_DIR": XCODE, "DOTNET_CLI_USE_MSBUILD_SERVER": "false",
                                 "MSBUILDUSESERVER": "0"}
        native = self.native | {"DEVELOPER_DIR": XCODE}
        self.evidence.report("environment.json", {
            "runner": {k: self.native.get(k) for k in ENV_KEYS},
            "pinnedNative": {k: native.get(k) for k in ENV_KEYS},
            "pr20CommandEquivalent": {k: harness.get(k) for k in ENV_KEYS},
            "differences": {k: {"native": native.get(k), "harness": harness.get(k)}
                            for k in ENV_KEYS if native.get(k) != harness.get(k)},
            "deviceSetPath": str(self.device_set), "pr20Source": PR20_SHA,
            "scope": "Command-only PR20 environment; no SDK/workload/MAUI installation. "
                     "Tool-install PATH/cache differences from preserved PR20 are not asserted identical.",
            "newSession": self.mode == "harness", "interpreter": platform.python_version()})
        require(self.run("xcode-version", ["xcodebuild", "-version"]).stdout.strip() == XCODE_VERSION,
                "Exact Xcode version/build required.")
        self.run("xcode-selection", ["xcode-select", "-p"], check=False, environment=self.native)
        first = self.run("first-launch-status", ["xcodebuild", "-checkFirstLaunchStatus"], check=False)
        self.state["firstLaunchExitCode"] = first.returncode
        self.run("xcode-help", ["xcodebuild", "-help"], check=False)
        self.run("license-preferences", ["defaults", "read", "/Library/Preferences/com.apple.dt.Xcode"],
                 check=False)
        self.run("coresimulator-package", ["pkgutil", "--pkg-info=com.apple.pkg.CoreSimulator"], check=False)
        self.host_snapshot("before")
        runtimes = {}
        for name, env in (("native", native), ("harness", harness)):
            version = self.run(name + "-tool", ["xcrun", "--find", "simctl"], environment=env)
            inventory = self.run(name + "-runtimes", ["xcrun", "simctl", "list", "runtimes", "-j"],
                                 timeout=180, environment=env)
            runtimes[name] = exact_runtime(json.loads(inventory.stdout))
            self.state[name + "Simctl"] = version.stdout.strip()
        self.evidence.report("runtime-comparison.json", {"native": runtimes["native"],
                                                        "harness": runtimes["harness"],
                                                        "equal": runtimes["native"] == runtimes["harness"]})
        self.run("device-types", ["xcrun", "simctl", "list", "devicetypes", "-j"])
        self.run("devices-before", ["xcrun", "simctl", "list", "devices", "-j"])
        self.run("diagnose-help", ["xcrun", "simctl", "help", "diagnose"])
        self.run("bootstatus-help", ["xcrun", "simctl", "help", "bootstatus"])
        self.copy_text("framework", Path(XCODE) / "Library/PrivateFrameworks/CoreSimulator.framework",
                       maximum_files=20)
        self.copy_text("service-framework", Path("/Library/Developer/PrivateFrameworks/CoreSimulator.framework"),
                       maximum_files=20)
        self.copy_text("profile", Path("/Library/Developer/CoreSimulator/Profiles/DeviceTypes/"
                                      "iPhone 17 Pro.simdevicetype"), maximum_files=20)
        self.copy_text("xcode-profile", Path(XCODE) / "Platforms/iPhoneSimulator.platform/Library/"
                       "Developer/CoreSimulator/Profiles/DeviceTypes/iPhone 17 Pro.simdevicetype",
                       maximum_files=20)
        self.copy_text("user-core-simulator", Path(self.native["HOME"]) / "Library/Preferences",
                       maximum_files=20, pattern="*CoreSimulator*.plist")
        self.copy_text("user-cache", Path(self.native["HOME"]) / "Library/Caches/com.apple.CoreSimulator",
                       maximum_files=20)
        self.save()

    def owned_plist(self):
        path = self.device_set / self.state["device"] / "device.plist"
        require(not path.is_symlink() and path.resolve().is_relative_to(self.device_set.resolve()),
                "Owned simulator plist escaped its device set.")
        value = plistlib.loads(path.read_bytes())
        require(value.get("UDID", "").upper() == self.state["device"].upper()
                and value.get("name") == self.state["name"] and value.get("runtime") == RUNTIME
                and value.get("deviceType") == DEVICE_TYPE, "Owned simulator identity changed; refusing mutation.")
        return value

    def observe(self, stop):
        rows = []
        previous = None
        while not stop.is_set():
            try:
                value = self.owned_plist()
                current = {"state": value.get("state"), "plist": value}
            except (OSError, ValueError, RuntimeError) as error:
                current = {"readError": str(error)}
            if current != previous:
                rows.append({"observedUtc": utc(), "monotonicSeconds": time.monotonic(), **current})
                self.evidence.report("device-transitions.json", rows)
                previous = current
            stop.wait(1)

    def boot(self):
        require("device" not in self.state, "A device has already been created; stale reuse is forbidden.")
        self.state["name"] = "dncdbg-intel-" + uuid.uuid4().hex
        self.save()
        device = self.run("create", ["xcrun", "simctl", "create", self.state["name"], DEVICE_TYPE, RUNTIME],
                          timeout=180).stdout.strip()
        require(str(uuid.UUID(device)).upper() == device.upper(), "simctl create did not return one UUID.")
        self.state["device"] = device
        self.save()
        self.evidence.report("created-device.json", self.owned_plist())
        stop = threading.Event()
        observer = threading.Thread(target=self.observe, args=(stop,), daemon=True)
        observer.start()
        started = time.monotonic()
        self.state["bootStartedUtc"] = utc()
        try:
            self.run("boot", ["xcrun", "simctl", "boot", device], timeout=BOOT_SECONDS)
            remaining = BOOT_SECONDS - (time.monotonic() - started)
            require(remaining > 0, "simctl boot consumed the complete 240s readiness budget.")
            self.run("bootstatus", ["xcrun", "simctl", "bootstatus", device, "-b"], timeout=remaining)
            booted = self.owned_plist()
            require(booted.get("state") == 3, "bootstatus exited but owned device is not independently Booted.")
            require(time.monotonic() - started <= BOOT_SECONDS, "Readiness observation exceeded 240s.")
            self.evidence.report("booted-device.json", booted)
            self.state["systemAppReady"] = True
        finally:
            self.state["bootSeconds"] = time.monotonic() - started
            self.state["bootFinishedUtc"] = utc()
            stop.set()
            observer.join(timeout=2)
            require(not observer.is_alive(), "State observer did not finish inside its bounded join.")
            self.save()

    def copy_text(self, prefix, root, maximum_files=80, pattern="**/*", deadline=None):
        records = []
        if not root.exists():
            self.evidence.report(prefix + "-inventory.json", {"root": str(root), "missing": True})
            return
        for path in root.glob(pattern):
            if deadline is not None and time.monotonic() >= deadline:
                records.append({"reason": "diagnostic deadline reached"})
                break
            if path.is_symlink() or not path.is_file() or path.suffix not in TEXT_SUFFIXES:
                continue
            if len(records) >= maximum_files:
                records.append({"reason": "file count limit", "maximumFiles": maximum_files})
                break
            relative = str(path.relative_to(root))
            record = {"file": relative, "bytes": path.stat().st_size, "mtimeNs": path.stat().st_mtime_ns}
            if path.stat().st_size > self.evidence.limit:
                record["omitted"] = "file size limit"
            else:
                try:
                    data = path.read_bytes()
                    text = (json.dumps(plistlib.loads(data), indent=2, default=str) if path.suffix == ".plist"
                            else data.decode("utf-8"))
                    name = prefix + "-" + hashlib.sha256(relative.encode()).hexdigest()[:12] + path.suffix
                    self.evidence.write(name, text)
                    record["evidenceFile"] = name
                except (OSError, UnicodeError, ValueError) as error:
                    record["omitted"] = str(error)
            records.append(record)
        self.evidence.report(prefix + "-inventory.json", {"root": str(root), "files": records})

    def diagnostics(self):
        started = time.monotonic()
        deadline = started + DIAGNOSTIC_SECONDS
        errors = []
        self.state["diagnosticsStartedUtc"] = utc()
        self.host_snapshot("after")
        device = self.state.get("device")
        if device:
            self.copy_text("device-logs", self.device_set / device / "data/Library/Logs", deadline=deadline)
            self.copy_text("device-migration", self.device_set / device / "data/Library/DataMigration",
                           deadline=deadline)
        for prefix, root, pattern in (
                ("core-service-logs", Path(self.native["HOME"]) / "Library/Logs/CoreSimulator", "**/*"),
                ("user-crashes", Path(self.native["HOME"]) / "Library/Logs/DiagnosticReports", "**/*"),
                ("system-crashes", Path("/Library/Logs/DiagnosticReports"), "**/*")):
            self.copy_text(prefix, root, pattern=pattern, deadline=deadline)
        commands = [("system-log", ["/usr/bin/log", "show", "--last", "10m", "--style", "ndjson",
                                    "--info", "--debug", "--predicate", LOG_PREDICATE], 30)]
        if device:
            commands += [
                ("device-launchd", ["xcrun", "simctl", "spawn", device, "launchctl", "print", "system"], 10),
                ("diagnose", ["xcrun", "simctl", "diagnose", "-b", "--timeout=60", "--no-archive",
                              "--output=" + str(self.private / "diagnose"), "--udid=" + device], 75)]
        for label, command, limit in commands:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                errors.append(label + ": diagnostic aggregate deadline reached")
                break
            try:
                result = self.run(label, command, timeout=min(limit, remaining), check=False,
                                  payload_path=self.private / "diagnose" if label == "diagnose" else None)
                if result.returncode:
                    errors.append(f"{label}: exit {result.returncode}")
            except (OSError, TimeoutError, RuntimeError) as error:
                errors.append(str(error))
        self.copy_text("diagnose-text", self.private / "diagnose", maximum_files=160, deadline=deadline)
        self.state["diagnosticsSeconds"] = time.monotonic() - started
        self.state["diagnosticsFinishedUtc"] = utc()
        self.state.setdefault("diagnosticErrors", []).extend(errors)
        self.state["diagnosticsComplete"] = not self.state.get("diagnosticErrors") and not self.evidence.omissions
        self.save()

    def cleanup(self):
        if self.state["cleanupDone"]:
            return
        if "device" not in self.state:
            self.state["cleanupDone"] = True
            self.save()
            return
        started = time.monotonic()
        deadline = started + CLEANUP_SECONDS

        def clean(label, command, check=True):
            remaining = deadline - time.monotonic()
            require(remaining > 0, "Owned cleanup exhausted the unchanged 30s aggregate budget.")
            return self.run(label, command, timeout=remaining, check=check)

        try:
            self.owned_plist()
            result = clean("cleanup-inventory", ["xcrun", "simctl", "list", "devices", "-j"])
            matches = [(runtime, d) for runtime, group in json.loads(result.stdout)["devices"].items()
                       for d in group if d["udid"].upper() == self.state["device"].upper()]
            require(len(matches) == 1 and matches[0][0] == RUNTIME
                    and matches[0][1]["name"] == self.state["name"]
                    and matches[0][1].get("deviceTypeIdentifier") == DEVICE_TYPE,
                    "Owned simulator inventory identity mismatch; refusing mutation.")
            if matches[0][1]["state"] != "Shutdown":
                clean("cleanup-shutdown", ["xcrun", "simctl", "shutdown", self.state["device"]])
            clean("cleanup-delete", ["xcrun", "simctl", "delete", self.state["device"]])
            result = clean("cleanup-verify", ["xcrun", "simctl", "list", "devices", "-j"])
            require(not any(d["udid"].upper() == self.state["device"].upper()
                            for group in json.loads(result.stdout)["devices"].values() for d in group),
                    "Owned device remains in CoreSimulator inventory.")
            require(not (self.device_set / self.state["device"]).exists(), "Owned device directory remains.")
            self.state["cleanupDone"] = time.monotonic() <= deadline
            require(self.state["cleanupDone"], "Owned cleanup exceeded 30s.")
        finally:
            self.state["cleanupSeconds"] = time.monotonic() - started
            self.save()

    def execute(self):
        try:
            self.preflight()
            self.boot()
        except Exception as error:
            self.state["error"] = str(error)
        finally:
            try:
                if self.host_verified:
                    self.diagnostics()
            except Exception as error:
                self.state.setdefault("diagnosticErrors", []).append(str(error))
            try:
                self.cleanup()
            except Exception as error:
                self.state["cleanupError"] = str(error)
            self.state["passed"] = self.state["systemAppReady"] and self.state["cleanupDone"] \
                and not self.state.get("error") and not self.state.get("cleanupError")
            self.state["finishedUtc"] = utc()
            self.save()
            self.evidence.report("summary.json", self.state)
            self.evidence.report("manifest.json", {
                "source": self.state["source"], "files": {
                    p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in self.evidence.path.iterdir()
                    if p.name != "manifest.json"}, "omissions": self.evidence.omissions,
                "fileLimitBytes": self.evidence.limit, "runLimitBytes": self.evidence.total_limit})
        return 0 if self.state["passed"] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("native", "harness"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    require(args.output.resolve().is_relative_to((root / "build").resolve()),
            "Probe output must be under this checkout's build directory.")
    return Probe(args.output, args.mode).execute()


if __name__ == "__main__":
    raise SystemExit(main())
