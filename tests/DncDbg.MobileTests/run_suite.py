"""Internal POSIX device orchestration. Public entrypoint: dotnet run --project tests/DncDbg.MobileTests."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import random
import shutil
import signal
import socket
import subprocess
import sys
import time
import traceback
import uuid

from mobile_tools import ROOT, adb_path, android_sdk, tool_environment

from process_tools import (OwnedProcessRecovery, OwnedProcessSettlementError, ProcessGroupOwnership,
                           original_cancellation, run_owned, start_owned, stop_owned)

PIN = json.loads(Path(__file__).with_name("tuple.json").read_text())
FIXTURE = ROOT / "tests/DncDbg.MauiTestApp"
APP = "com.dncdbg.mobiletests"
ANDROID_CONSOLE_PORTS = tuple(range(5554, 5586, 2))
RELEASE_BUILD_TIMEOUT = 20 * 60
DEBUG_BUILD_TIMEOUT = 10 * 60
IOS_BOOT_TIMEOUT = 10 * 60
IOS_CLEANUP_TIMEOUT = 60


def build_environment(environment):
    sensitive = ("TOKEN", "SECRET", "PASSWORD", "CREDENTIAL", "API_KEY", "ACCESS_KEY", "PRIVATE_KEY", "AUTHORIZATION")
    return {key: value for key, value in environment.items()
            if not any(part in key.upper() for part in sensitive)}

def sdk_environment(environment):
    clean = {key: value for key, value in environment.items()
             if not key.upper().startswith("PYTHON") and key.upper() not in {"__PYVENV_LAUNCHER__", "DNCDBG_PYTHON"}}
    return clean | {"DOTNET_CLI_USE_MSBUILD_SERVER": "false", "MSBUILDUSESERVER": "0",
                    "PYTHONNOUSERSITE": "1", "PYTHONUTF8": "1"}


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def properties(path):
    return {key.strip(): value.strip() for line in path.read_text().splitlines() if "=" in line
            for key, value in [line.split("=", 1)]}

def atomic_json(path, value):
    pending = path.with_name(path.name + ".pending")
    pending.write_text(json.dumps(value, indent=2))
    pending.replace(path)


class Suite:
    def __init__(self, args):
        self.args = args
        self.output = args.output.resolve()
        require(self.output.is_relative_to((ROOT / "build").resolve()), "Evidence must be under this checkout's build/.")
        self.state_path = self.output / "state.json"
        self.env = sdk_environment(os.environ)
        self.state = {}
        self.number = max((int(p.name.split("-", 1)[0]) for p in self.output.glob("*.log")
                           if p.name.split("-", 1)[0].isdigit()), default=0)
        self.emulator = None
        self.emulator_log = None
        self.broker_process = None
        self.process_recoveries = []
        self.recovery_fallback = None
        self.run_nonce = uuid.uuid4().hex
        self.source = None
        self.evidence_core = None
        self.android = PIN["androidX64" if getattr(args, "android_abi", "arm64-v8a") == "x86_64" else "android"]
        self.ios = PIN["iosX64" if getattr(args, "ios_architecture", "arm64") == "x64" else "ios"]

    def owned_recoveries(self):
        yield from self.process_recoveries
        if (self.recovery_fallback is not None and self.recovery_fallback.process is not None
                and self.recovery_fallback not in self.process_recoveries):
            yield self.recovery_fallback

    def save(self):
        atomic_json(self.state_path, self.state)

    def source_record(self):
        if self.source is None:
            self.source = {
                "head": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
                "dirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip())}
        return self.source

    @staticmethod
    def simulator_uuid(value):
        require(isinstance(value, str) and str(uuid.UUID(value)).casefold() == value.casefold(),
                "Simulator create did not return one canonical UDID.")
        return value

    def simulator_candidate(self, listing, intent):
        require(intent["name"] == self.state["name"] and intent["name"].startswith("dncdbg") and
                uuid.UUID(intent["name"][6:]).hex == intent["name"][6:] and
                intent["runtime"] == self.ios["runtime"] and intent["deviceType"] == self.ios["deviceType"] and
                len(intent["sourceHead"]) == 40 and len(intent["runNonce"]) == 32,
                "Simulator creation intent identity is invalid.")
        entries = [(runtime, entry) for runtime, group in listing["devices"].items() for entry in group]
        known = self.state.get("simulatorIdentity")
        require(known is None or all(entry["name"] == intent["name"] for _, entry in entries
                                    if entry["udid"].casefold() == known["udid"].casefold()),
                "Registered simulator identity changed its unique name.")
        candidates = [(runtime, entry) for runtime, entry in entries if entry["name"] == intent["name"]]
        require(len(candidates) <= 1, "Simulator creation intent matches multiple devices; refusing mutations.")
        if not candidates:
            return None
        runtime, entry = candidates[0]
        require(runtime == intent["runtime"] and entry.get("deviceTypeIdentifier") == intent["deviceType"],
                "Simulator creation intent runtime or device type mismatch.")
        self.simulator_uuid(entry["udid"])
        require(known is None or known["udid"].casefold() == entry["udid"].casefold(),
                "Simulator creation identity changed after registration.")
        return {"udid": entry["udid"], "name": entry["name"], "runtime": runtime,
                "deviceType": entry["deviceTypeIdentifier"]}

    def register_simulator(self, identity, resolution):
        self.state["device"] = identity["udid"]
        self.state["simulatorIdentity"] = identity
        self.save()
        self.state["simulatorCreate"].update(pending=False, resolution=resolution)
        self.save()

    def recovery_state(self):
        return [{
            "pid": r.process.pid, "processGroup": r.process.pid,
            "leaderReaped": r.leader_reaped, "groupAbsent": r.group_absent, "drainComplete": r.drain_complete,
            "isBuild": getattr(r, "is_build", False),
            "originalIdentity": r.group_ownership.original if r.group_ownership is not None else None,
            "identityCapture": r.group_ownership.capture_state if r.group_ownership is not None else "not-applicable",
            "fingerprint": getattr(r, "command_intent", {}).get("fingerprint"),
        } for r in self.owned_recoveries()]

    def prepare_command(self, owner, command):
        executable = Path(str(command[0])).name
        simctl = len(command) > 2 and executable == "xcrun" and command[1] == "simctl"
        operation = command[2] if simctl and command[2] in (
            "create", "boot", "bootstatus", "list", "terminate", "uninstall", "shutdown", "delete") else "command"
        owner.command_intent = {"nonce": uuid.uuid4().hex, "provider": "simctl" if simctl else executable,
                                "operation": operation,
                                "fingerprint": hashlib.sha256(json.dumps([str(c) for c in command]).encode()).hexdigest(),
                                "state": "launch-pending", "pid": None, "identityCapture": "not-applicable", "identity": None}
        owner.command_intent["sourceHead"] = self.source_record()["head"]
        owner.command_intent["runNonce"] = self.run_nonce
        self.state["commandIntent"] = owner.command_intent
        self.state["commandRecoveryRequired"] = True
        self.state["cleanupDone"] = False
        if getattr(owner, "is_build", False):
            self.state["buildGroupSettled"] = False
        self.save()

    def command_published(self, owner):
        ownership = owner.group_ownership
        owner.command_intent.update(pid=owner.process.pid, identityCapture=ownership.capture_state,
                                    identity=ownership.original,
                                    state="owned" if ownership.capture_state == "verified" else "unverified")
        self.state["processRecovery"] = self.recovery_state()
        self.save()
        self.process_recoveries.append(owner)
        require(ownership.capture_state == "verified", "Original command birth identity is unverified.")

    def command_settled(self, owner):
        intent = getattr(owner, "command_intent", None)
        if intent is not None:
            intent["state"] = "settled" if owner.complete else "unverified"
            self.state["commandIntent"] = intent
        self.state["processRecovery"] = self.recovery_state()
        self.state["commandRecoveryRequired"] = any(not r.complete for r in self.owned_recoveries())
        self.save()

    def unverified_command_journal(self):
        return self.state.get("commandRecoveryRequired", False) or any(
            r.get("identityCapture") == "unverified" or not all(r.get(key, False) for key in
                ("leaderReaped", "groupAbsent", "drainComplete")) for r in self.state.get("processRecovery", []))

    def run(self, *command, cwd=ROOT, timeout=180, check=True, input=None, grouped=True,
            progress=False, progress_interval=30, environment=None, deadline=None):
        require(not any(not r.complete for r in self.owned_recoveries()) and
                not (self.unverified_command_journal() and not any(self.owned_recoveries())),
                "Unverified owned command recovery forbids another ordinary command.")
        if progress:
            require(grouped, "Build diagnostics require owned process groups.")
            return self.run_progress(command, cwd, timeout, check, input, progress_interval, environment)
        if grouped and deadline is None:
            return self.run_progress(command, cwd, timeout, check, input, None, environment)
        self.number += 1
        print("RUN:", " ".join(str(c) for c in command), flush=True)
        if deadline is not None:
            require(grouped, "Absolute deadlines require owned process groups.")
            log = self.output / f"{self.number:03d}-{Path(str(command[0])).name}.log"
            owner = OwnedProcessRecovery(None, log, deadline)
            owner.group_ownership = ProcessGroupOwnership(owner)
            self.recovery_fallback = owner
            try:
                self.prepare_command(owner, command)
                return run_owned(*(str(c) for c in command), cwd=cwd, env=self.env if environment is None else environment,
                                 timeout=timeout, check=check, input=input, log=log, deadline=deadline,
                                 owner_callback=self.command_published, recovery_owner=owner)
            except OwnedProcessSettlementError as error:
                first_error, first_cause = error, error.__cause__
                try:
                    try:
                        self.state["processRecovery"] = self.recovery_state()
                        self.save()
                    except BaseException as recording_failure:
                        first_error.recovery.record_failure = recording_failure
                        try:
                            first_error.recovery.record_errors.append(type(recording_failure).__name__)
                        except BaseException:
                            pass
                finally:
                    raise first_error from first_cause
            except subprocess.TimeoutExpired as error:
                raise TimeoutError(f"{command[0]} exceeded its aggregate deadline; partial output: {log.relative_to(ROOT)}") from error
            finally:
                pending = sys.exc_info()[1]
                try:
                    self.command_settled(owner)
                except BaseException as error:
                    owner.record_failure = error
                    try:
                        owner.record_errors.append(type(error).__name__)
                    except BaseException:
                        pass
                    if pending is None:
                        raise
        process = subprocess.Popen([str(c) for c in command], cwd=cwd,
                                   env=self.env if environment is None else environment, text=True,
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   start_new_session=grouped)
        timed_out = False
        try:
            text, _ = process.communicate(input, timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            if grouped:
                stop_owned(process)
            else:
                process.kill()
            text, _ = process.communicate(timeout=5)
        finally:
            if grouped:
                stop_owned(process)
            else:
                process.wait(timeout=5)
        log = self.output / f"{self.number:03d}-{Path(str(command[0])).name}.log"
        log.write_text(text)
        if timed_out:
            raise TimeoutError(f"{command[0]} exceeded {timeout}s; partial output: {log.relative_to(ROOT)}")
        if check and process.returncode:
            raise RuntimeError(f"{command[0]} exited {process.returncode}; see {log.relative_to(ROOT)}\n{text[-4000:]}")
        return subprocess.CompletedProcess(command, process.returncode, text, "")

    def run_progress(self, command, cwd, timeout, check, input, interval, environment):
        self.number += 1
        log = self.output / f"{self.number:03d}-{Path(str(command[0])).name}.log"
        started = time.monotonic()
        timed_out = False
        owner = OwnedProcessRecovery(None, log, started + timeout)
        owner.file_output = True
        owner.is_build = interval is not None
        owner.group_ownership = ProcessGroupOwnership(owner)
        self.recovery_fallback = owner
        print("RUN:", " ".join(str(c) for c in command), flush=True)
        process = None
        try:
            self.prepare_command(owner, command)
            with log.open("wb") as stream:
                try:
                    command_environment = self.env if environment is None else environment
                    process = start_owned([str(c) for c in command], owner=owner, cwd=cwd,
                                          env=build_environment(command_environment) if interval is not None else command_environment,
                                          text=False, stdin=subprocess.PIPE, stdout=stream, stderr=subprocess.STDOUT)
                    self.command_published(owner)
                    owner.await_exec(command, owner.deadline)
                    first = True
                    while True:
                        remaining = owner.deadline - time.monotonic()
                        if remaining <= 0:
                            timed_out = True
                            raise TimeoutError(f"{command[0]} exceeded {timeout}s; partial output: {log.relative_to(ROOT)}")
                        try:
                            process.communicate(
                                input.encode() if first and isinstance(input, str) else input if first else None,
                                timeout=min(interval, remaining) if interval is not None else remaining)
                            break
                        except subprocess.TimeoutExpired as error:
                            first = False
                            if time.monotonic() >= owner.deadline:
                                timed_out = True
                                raise TimeoutError(
                                    f"{command[0]} exceeded {timeout}s; partial output: {log.relative_to(ROOT)}") from error
                            if interval is not None:
                                lines = log.read_text(errors="replace").splitlines()
                                print(f"BUILD still running: {time.monotonic() - started:.1f}/{timeout}s; "
                                      f"last output: {lines[-1] if lines else '<none yet>'}; log: {log.relative_to(ROOT)}", flush=True)
                    if time.monotonic() > owner.deadline:
                        timed_out = True
                        raise TimeoutError(f"{command[0]} exceeded {timeout}s; partial output: {log.relative_to(ROOT)}")
                    if check and process.returncode:
                        raise RuntimeError(f"{command[0]} exited {process.returncode}; see {log.relative_to(ROOT)}\n"
                                           + log.read_text(errors="replace")[-4000:])
                except subprocess.TimeoutExpired as error:
                    timed_out = True
                    primary = TimeoutError(f"{command[0]} exceeded {timeout}s; partial output: {log.relative_to(ROOT)}")
                    primary.__cause__ = error
                    owner.operation_error = primary
                    owner.preserve_error(primary)
                except BaseException as error:
                    owner.operation_error = error
                    owner.preserve_error(error)
                finally:
                    process = owner.process
                    if process is not None:
                        try:
                            if owner.primary_error is None and not owner.settle(owner.deadline):
                                owner.preserve_error(RuntimeError("Command completed but its owned group did not settle."))
                            if isinstance(owner.primary_error, subprocess.TimeoutExpired):
                                timed_out = True
                                primary = TimeoutError(
                                    f"{command[0]} exceeded {timeout}s; partial output: {log.relative_to(ROOT)}")
                                primary.__cause__ = owner.primary_error
                                owner.preserve_error(primary)
                            if not owner.complete:
                                owner.finish()
                        except BaseException as error:
                            if owner.primary_error is None:
                                owner.operation_error = error
                            owner.preserve_error(error)
                        finally:
                            try:
                                owner.safe_record()
                            finally:
                                if owner.complete:
                                    for pipe in (process.stdin, process.stdout, process.stderr):
                                        if pipe is not None:
                                            pipe.close()
        except BaseException as error:
            owner.preserve_error(error)
        finally:
            if owner.process is not None:
                try:
                    builds = [r for r in self.owned_recoveries() if getattr(r, "is_build", False)]
                    if builds:
                        self.state["buildGroupSettled"] = all(r.complete for r in builds)
                    else:
                        self.state.setdefault("buildGroupSettled", True)
                    log.with_suffix(".status.json").write_text(json.dumps({
                        "command": Path(str(command[0])).name, "argumentCount": len(command) - 1,
                        "seconds": time.monotonic() - started,
                        "timeoutSeconds": timeout, "timedOut": timed_out, "exitCode": owner.process.returncode,
                        "primaryError": type(owner.primary_error).__name__ if owner.primary_error else None,
                        "buildGroupSettled": owner.complete, "recoveryErrors": owner.errors,
                        "leaderReaped": owner.leader_reaped, "groupAbsent": owner.group_absent,
                        "drainComplete": owner.drain_complete,
                        **owner.group_ownership.report(),
                    }, indent=2))
                    self.command_settled(owner)
                except BaseException as error:
                    owner.record_failure = error
                    owner.preserve_error(error)
                    try:
                        owner.record_errors.append(type(error).__name__)
                    except BaseException:
                        pass
            else:
                try:
                    if owner.is_build:
                        self.state["buildGroupSettled"] = all(
                            r.complete for r in self.owned_recoveries() if getattr(r, "is_build", False))
                    self.command_settled(owner)
                except BaseException as error:
                    owner.preserve_error(error)
        if owner.primary_error is not None:
            raise owner.primary_error
        if owner.process is None or not owner.complete:
            raise owner.failure_exception
        return subprocess.CompletedProcess(command, owner.process.returncode, log.read_text(errors="backslashreplace"), "")

    def json(self, *command, **kwargs):
        return json.loads(self.run(*command, **kwargs).stdout)

    def preflight(self):
        require(os.name == "posix", "Device orchestration requires POSIX process ownership.")
        system, architecture = (("Darwin", self.ios["hostArchitecture"]) if self.args.platform == "ios" else
                                (self.android["hostSystem"], self.android["hostArchitecture"]))
        require(platform.system() == system and platform.machine() == architecture,
                f"The selected pinned tuple requires {system}/{architecture}; never substitute another ABI.")
        self.env = sdk_environment(tool_environment(self.args.platform))
        scratch = self.output / "work/scratch"
        scratch.mkdir(parents=True)
        # CoreSimulator is a per-user service; preserve its native temporary environment.
        if self.args.platform != "ios":
            self.env["TMPDIR"] = str(scratch) + "/"
        sdk = self.run("dotnet", "--version", cwd=FIXTURE).stdout.strip()
        require(sdk == PIN["sdk"], f"Expected SDK {PIN['sdk']}, got {sdk}.")
        workload = self.run("dotnet", "workload", "--version", cwd=FIXTURE).stdout.strip()
        require(workload == PIN["workload"], f"Expected workload set {PIN['workload']}, got {workload}.")
        require(self.run("maui", "--version").stdout.strip().split("+")[0] == PIN["devflow"],
                "Microsoft.Maui.Cli version differs from the pinned fixture agent.")
        if self.args.platform == "ios":
            require(self.args.xcode_dir is not None, "Select the pinned Xcode explicitly with --xcode-dir.")
            self.env["DEVELOPER_DIR"] = str(self.args.xcode_dir.resolve())
            version = self.run("xcodebuild", "-version").stdout
            require(version.strip() == f"Xcode {self.ios['xcode']}\nBuild version {self.ios['xcodeBuild']}",
                    f"Expected Xcode {self.ios['xcode']} ({self.ios['xcodeBuild']}), got {version!r}.")
            runtimes = self.json("xcrun", "simctl", "list", "runtimes", "-j")["runtimes"]
            require(any(r["identifier"] == self.ios["runtime"] and r.get("isAvailable") and
                        r["buildversion"] == self.ios["runtimeBuild"] for r in runtimes),
                    f"Required simulator runtime {self.ios['runtime']} ({self.ios['runtimeBuild']}) unavailable.")
        else:
            sdk = android_sdk()
            self.env["PATH"] = str(sdk / "emulator") + os.pathsep + self.env["PATH"]
            image = properties(sdk / "system-images" / Path(*self.android["systemImage"].split(";")[1:]) /
                               "source.properties")
            require(image.get("Pkg.Revision") == self.android["imageRevision"] and
                    image.get("AndroidVersion.ApiLevel") == self.android["apiLevel"] and
                    image.get("SystemImage.TagId") == self.android["tag"] and
                    image.get("SystemImage.Abi") == self.android["abi"],
                    "Required Android API/image revision/ABI unavailable; never substitute another tuple.")
            emulator_properties = properties(sdk / "emulator/source.properties")
            require(emulator_properties.get("Pkg.Revision") == self.android["emulatorVersion"],
                    f"Required Android emulator {self.android['emulatorVersion']} unavailable.")
            require(properties(sdk / f"ndk/{self.android['ndk']}/source.properties").get("Pkg.Revision") == self.android["ndk"],
                    "Pinned Android NDK unavailable.")
            tools = sdk / "build-tools" / self.android["buildTools"]
            require(properties(tools / "source.properties").get("Pkg.Revision") == self.android["buildTools"] and
                    (tools / "aapt2").is_file() and (tools / "lib/apksigner.jar").is_file(),
                    "Pinned official Android package identity/signature tools are unavailable.")
            if system == "Linux":
                require(os.access("/dev/kvm", os.R_OK | os.W_OK), "Hosted Android requires accessible /dev/kvm; no software fallback.")
            self.run(sdk / "emulator/emulator", "-accel-check")
            (self.output / "android-prerequisites.json").write_text(json.dumps({
                "runtimeIdentifier": self.android["rid"], "hostSystem": system, "hostArchitecture": architecture,
                "image": image, "emulator": emulator_properties,
                "ndk": properties(sdk / f"ndk/{self.android['ndk']}/source.properties"),
            }, indent=2))
        self.state["pins"] = PIN
        self.save()

    def boot(self):
        require(self.args.platform != "ios" or not self.state.get("simulatorCreate", {}).get("pending", False),
                "A pending simulator creation must be resolved before another create.")
        name = "dncdbg" + uuid.uuid4().hex
        self.state["name"] = name
        with socket.socket() as reserve:
            reserve.bind(("127.0.0.1", 0))
            self.state["uiPort"] = reserve.getsockname()[1] if self.args.platform == "ios" else 9223
        self.save()
        if self.args.platform == "ios":
            started = time.monotonic()
            device = None
            ready = False

            def remaining():
                budget = IOS_BOOT_TIMEOUT - (time.monotonic() - started)
                if budget <= 0:
                    raise TimeoutError(f"iOS provisioning exceeded {IOS_BOOT_TIMEOUT}s.")
                return budget

            try:
                deadline = started + IOS_BOOT_TIMEOUT
                self.state["simulatorCreate"] = {
                    "pending": True, "name": name, "runtime": self.ios["runtime"], "deviceType": self.ios["deviceType"],
                    "sourceHead": self.source_record()["head"], "runNonce": self.run_nonce}
                self.state["simulatorJournalClean"] = self.state["resourceJournalClean"] = False
                self.state.pop("device", None)
                self.save()
                device = self.run("xcrun", "simctl", "create", name, self.ios["deviceType"], self.ios["runtime"],
                                  timeout=min(180, remaining()), deadline=deadline).stdout.strip()
                remaining()
                self.simulator_uuid(device)
                listing = self.json("xcrun", "simctl", "list", "devices", "-j", timeout=remaining(), deadline=deadline)
                identity = self.simulator_candidate(listing, self.state["simulatorCreate"])
                require(identity is not None and identity["udid"].casefold() == device.casefold(),
                        "Created simulator reply does not match the exact owned inventory.")
                self.register_simulator(identity, "create-reply-and-inventory")
                device = identity["udid"]
                self.run("xcrun", "simctl", "boot", device, timeout=remaining(), deadline=deadline)
                self.run("xcrun", "simctl", "bootstatus", device, "-b", timeout=remaining(), deadline=deadline)
                devices = self.json("xcrun", "simctl", "list", "devices", "-j", timeout=remaining(), deadline=deadline)
                remaining()
                matches = [d for d in devices["devices"].get(self.ios["runtime"], [])
                           if d["udid"] == device and d["name"] == name and d["state"] == "Booted"]
                require(len(matches) == 1, "Exact owned simulator must independently report Booted in the pinned runtime.")
                ready = True
            finally:
                (self.output / "ios-provisioning.json").write_text(json.dumps({
                    "device": device, "name": name, "runtime": self.ios["runtime"], "ready": ready,
                    "timeoutSeconds": IOS_BOOT_TIMEOUT, "seconds": time.monotonic() - started, "complete": False,
                }, indent=2))
        else:
            avd = self.output / "work/avd"
            avd.mkdir()
            self.env["ANDROID_AVD_HOME"] = str(avd)
            sdk = android_sdk()
            manager = sdk / f"cmdline-tools/{self.android.get('commandLineTools', 'latest')}/bin/avdmanager"
            self.run(manager, "create", "avd", "--name", name, "--package", self.android["systemImage"],
                     "--device", self.android["deviceType"], input="no\n")
            ports = list(ANDROID_CONSOLE_PORTS)
            random.shuffle(ports)
            for port in ports:
                try:
                    with socket.socket() as console, socket.socket() as adb:
                        console.bind(("127.0.0.1", port))
                        adb.bind(("127.0.0.1", port + 1))
                    break
                except OSError:
                    continue
            else:
                raise RuntimeError("No unused emulator console/ADB port pair.")
            device = f"emulator-{port}"
            require(device not in self.run(adb_path(), "devices").stdout, "Selected emulator serial is already in use.")
            self.state["device"] = device
            self.emulator_log = (self.output / "emulator.log").open("w")
            self.emulator = start_owned([str(sdk / "emulator/emulator"), "-avd", name, "-port", str(port),
                                         "-no-window", "-no-audio", "-no-snapshot", "-no-boot-anim",
                                         "-gpu", "swiftshader", "-accel", "on"],
                                        env=self.env, cwd=ROOT, stdout=self.emulator_log, stderr=subprocess.STDOUT)
            self.state["emulatorPid"] = self.emulator.pid
            self.save()
            deadline = time.monotonic() + 240
            while time.monotonic() < deadline:
                require(self.emulator.poll() is None, "Owned emulator exited before boot; see emulator.log.")
                if self.run(adb_path(), "-s", device, "shell", "getprop", "sys.boot_completed",
                            check=False, timeout=15).stdout.strip() == "1":
                    break
                time.sleep(2)
            else:
                raise TimeoutError("Dedicated emulator did not boot in 240 seconds.")
            require(self.run(adb_path(), "-s", device, "shell", "getprop", "ro.product.cpu.abi").stdout.strip() == self.android["abi"],
                    "Emulator booted the wrong ABI.")
            require(self.run(adb_path(), "-s", device, "shell", "getprop", "ro.kernel.qemu").stdout.strip() == "1",
                    "Refusing a physical Android device.")

    def broker(self):
        status = self.json("maui", "devflow", "broker", "status")
        if not status["running"]:
            self.broker_process = start_owned(["maui", "devflow", "broker", "start"], cwd=ROOT, env=self.env,
                                              text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            self.state["brokerGroup"] = self.broker_process.pid
            self.save()
            text, _ = self.broker_process.communicate(timeout=120)
            (self.output / "broker-start.log").write_text(text)
            require(self.broker_process.returncode == 0, "DevFlow broker start failed; see broker-start.log.")
            status = self.json("maui", "devflow", "broker", "status")
            require(status["running"], "DevFlow broker did not start.")
            self.state["brokerPid"] = self.run("lsof", "-nP", "-tiTCP:19223", "-sTCP:LISTEN").stdout.strip()
            self.save()
        # Android's 9223 fallback is explicitly UI-only. No reverse/discovery claim is needed.

    def build(self, configuration="Debug", faults=False):
        rid = self.ios["rid"] if self.args.platform == "ios" else self.android["rid"]
        tfm = "net11.0-" + self.args.platform
        if configuration == "Debug":
            command = [sys.executable, "src/DncDbg.Remote.Target/build.py", rid, "--configuration", "Debug"]
            if faults:
                command.append("--test-faults")
            self.run(*command)
        properties = [f"-p:DncDbgMobileTestPlatform={self.args.platform}", f"-p:RuntimeIdentifier={rid}",
                      f"-p:DncDbgTestFaults={str(faults).lower()}",
                      "-p:EmbedAssembliesIntoApk=true", "-p:UseMonoRuntime=false"]
        properties += [f"-p:MauiDevFlowPort={self.state['uiPort']}", f"-p:MauiDevFlowSessionId={self.state['name']}"]
        if self.args.platform == "android":
            properties.append(f"-p:AndroidSdkBuildToolsVersion={self.android['buildTools']}")
        result = self.json("dotnet", "msbuild", ".", f"-p:TargetFramework={tfm}", f"-p:Configuration={configuration}",
                           *properties, "-getProperty:MauiVersion,DefineConstants,AndroidSdkBuildToolsVersion",
                           "-getItem:NativeReference,AndroidNativeLibrary,PackageReference,ProjectReference", cwd=FIXTURE)
        require(result["Properties"]["MauiVersion"] == PIN["maui"], "MAUI package differs from the pinned workload.")
        if self.args.platform == "android":
            require(result["Properties"].get("AndroidSdkBuildToolsVersion") == self.android["buildTools"],
                    "SDK build/sealing tools differ from the provisioned exact Android tuple.")
        if configuration == "Release":
            require(not result["Items"]["NativeReference"] and not result["Items"]["AndroidNativeLibrary"] and
                    "DNCDBG_TEST_FAULTS" not in result["Properties"]["DefineConstants"] and
                    not any("DevFlow" in i["Identity"] for i in result["Items"]["PackageReference"]) and
                    not any("DncDbg.Remote" in i["Identity"] for i in result["Items"]["ProjectReference"]),
                    "Target-native/fault assets must remain absent from an application Release without its explicit target package.")
        binlog = self.output / f"{self.number + 1:03d}-{configuration}-{faults}.binlog"
        self.run("dotnet", "build", ".", "-t:Rebuild", "-c", configuration, "-f", tfm, *properties,
                 "--nologo", "-v:minimal", f"-bl:{binlog};ProjectImports=None", cwd=FIXTURE,
                 timeout=RELEASE_BUILD_TIMEOUT if configuration == "Release" else DEBUG_BUILD_TIMEOUT,
                 progress=True, environment=build_environment(self.env))
        output = FIXTURE / f"bin/{configuration}/{tfm}/{rid}"
        app = output / ("DncDbg.MauiTestApp.app" if self.args.platform == "ios" else f"{APP}-Signed.apk")
        require(app.exists(), f"Fixture build produced no installable artifact: {app}")
        if configuration == "Release":
            self.run(sys.executable, "tests/DncDbg.RemoteTests/assert_artifacts.py", app)
        else:
            library = ROOT / f"build/native-private/{rid}" / ("libdncdbg_target.a" if self.args.platform == "ios" else "libdncdbg_target.so")
            has_fault = b"dncdbg_test_stall_owner" in library.read_bytes()
            require(has_fault == faults, "Native fault export must match the explicit Debug-only build mode.")
        return app, output

    def proof(self, helper, app, assets, name):
        self.run(sys.executable, str(Path(__file__).with_name(helper)), "--platform",
                 "iosSimulator" if self.args.platform == "ios" else "android", "--device", self.state["device"],
                 "--app", app, "--assets", assets, "--output", self.output / name,
                 "--ui-port", str(self.state["uiPort"]),
                 "--ui-session", self.state["name"],
                 "--runtime-identifier", self.ios["rid"] if self.args.platform == "ios" else self.android["rid"],
                 timeout=900)

    def execute(self):
        prefix = ["tuple-admission", "device-boot"]
        if self.args.platform == "ios":
            prefix += ["debug-build", "broker-independent-dap", "release-isolation"]
        else:
            prefix += ["release-isolation", "debug-build", "broker-independent-dap"]
        self.state["requiredPhases"] = prefix + [
            "direct-continue", "direct-disconnect", "broker-admission", "devflow-only",
            "dap", "fault-build", "faults", "ordinary-debug-restored", "restored-dap"]
        self.state["completedPhases"] = []
        self.state["phasesComplete"] = False
        self.save()
        self.preflight()
        self.phase_completed("tuple-admission")
        self.boot()
        self.phase_completed("device-boot")
        if self.args.platform == "ios":
            app, assets = self.build()
            self.phase_completed("debug-build")
            self.fixture_proof(app, assets, "dap", "debugger-dap-broker-unavailable")
            self.phase_completed("broker-independent-dap")
            before = self.debug_identity(app, assets)
            identity_path = self.output / "debug-release-integrity.json"
            identity_path.write_text(json.dumps({"before": before, "after": None, "unchanged": False}, indent=2))
            self.build("Release", faults=True)
            after = self.debug_identity(app, assets)
            identity_path.write_text(json.dumps({"before": before, "after": after, "unchanged": before == after}, indent=2))
            require(before == after, "Debug artifacts changed during Release isolation build.")
            self.phase_completed("release-isolation")
            self.independent_client_proofs(app, assets)
        else:
            self.build("Release", faults=True)
            self.phase_completed("release-isolation")
            app, assets = self.build()
            self.phase_completed("debug-build")
            self.independent_proofs(app, assets)
        self.broker()
        self.phase_completed("broker-admission")
        self.fixture_proof(app, assets, "ui", "devflow-only")
        self.phase_completed("devflow-only")
        self.proof("run_device.py", app, assets, "dap")
        self.phase_completed("dap")
        try:
            app, assets = self.build(faults=True)
            self.phase_completed("fault-build")
            self.proof("run_faults.py", app, assets, "faults")
            self.phase_completed("faults")
        finally:
            # Faults must not leave the ordinary helper/package in a poisoned build state.
            if original_cancellation(sys.exc_info()[1]) is None and not any(not r.complete for r in self.owned_recoveries()):
                app, assets = self.build()
        self.phase_completed("ordinary-debug-restored")
        self.proof("run_device.py", app, assets, "restored-dap")
        self.phase_completed("restored-dap")
        require(self.state["completedPhases"] == self.state["requiredPhases"],
                "The full mobile suite did not complete every required phase in order.")
        self.state["phasesComplete"] = True
        self.save()

    def phase_completed(self, name):
        self.state.setdefault("completedPhases", []).append(name)
        self.save()

    def debug_identity(self, app, assets):
        files = [("app/" + path.relative_to(app).as_posix(), path) for path in app.rglob("*") if path.is_file()]
        for name in ("DncDbg.MauiTestApp.dll", "DncDbg.MauiTestApp.pdb", "DncDbg.ReloadFixture.dll",
                     "DncDbg.ReloadFixture.pdb", "dncdbg-agent.json", "dncdbg-package.json"):
            files.append(("assets/" + name, assets / name))
        hashes = {}
        for name, path in sorted(files):
            root = app if name.startswith("app/") else assets
            require(path.resolve().is_relative_to(root.resolve()), "Debug artifact escapes its owned build output.")
            digest = hashlib.sha256()
            with path.open("rb") as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(block)
            hashes[name] = digest.hexdigest()
        require(any(name.startswith("app/") for name in hashes), "Debug app identity inventory is empty.")
        return hashes

    def fixture_proof(self, app, assets, proof, name, disconnect=False):
        command = [sys.executable, str(Path(__file__).with_name("run_direct.py")), "--platform", self.args.platform,
                   "--device", self.state["device"], "--app", str(app), "--assets", str(assets), "--output",
                   str(self.output / name), "--proof", proof, "--ui-session", self.state["name"],
                   "--ui-port", str(self.state["uiPort"]), "--runtime-identifier",
                   self.ios["rid"] if self.args.platform == "ios" else self.android["rid"]]
        if disconnect:
            command.append("--disconnect-stopped")
        self.run(*command, timeout=600)

    def independent_proofs(self, app, assets):
        self.fixture_proof(app, assets, "dap", "debugger-dap-broker-unavailable")
        self.phase_completed("broker-independent-dap")
        self.independent_client_proofs(app, assets)

    def independent_client_proofs(self, app, assets):
        self.fixture_proof(app, assets, "client", "direct-continue")
        self.phase_completed("direct-continue")
        self.fixture_proof(app, assets, "client", "direct-disconnect", disconnect=True)
        self.phase_completed("direct-disconnect")

    def cleanup(self):
        errors = []
        cancellation = None
        ios_deadline = time.monotonic() + IOS_CLEANUP_TIMEOUT if self.args.platform == "ios" else None
        unverified_command = self.unverified_command_journal() and not any(self.owned_recoveries())
        if unverified_command:
            errors.append("Unverified owned command launch/recovery journal from an unavailable original owner.")
        if not self.process_recoveries and self.recovery_fallback is None and any(not all(r.get(key, False) for key in
                ("leaderReaped", "groupAbsent", "drainComplete")) for r in self.state.get("processRecovery", [])):
            errors.append("Unverified process recovery journal from an unavailable original owner; refusing completion.")
        def finish_recoveries():
            nonlocal cancellation
            for recovery in self.owned_recoveries():
                try:
                    if not recovery.finish():
                        message = f"Owned process/group {recovery.process.pid} failure recovery incomplete."
                        if message not in errors:
                            errors.append(message)
                except BaseException as failure:
                    recovery.preserve_error(failure)
                    errors.append(repr(failure))
                errors.extend(message for message in recovery.record_errors if message not in errors)
                cancellation = cancellation or original_cancellation(recovery.primary_error)
            if cancellation is not None:
                message = "Resource cleanup interrupted; journaled resources remain unverified."
                if message not in errors:
                    errors.append(message)

        finish_recoveries()

        def clean(*command, **kwargs):
            nonlocal cancellation
            if unverified_command or cancellation is not None or any(not r.complete for r in self.owned_recoveries()):
                return None
            try:
                timeout = 30
                if ios_deadline is not None and command[:2] == ("xcrun", "simctl"):
                    timeout = ios_deadline - time.monotonic()
                    if timeout <= 0:
                        raise TimeoutError(f"Owned simulator cleanup exceeded {IOS_CLEANUP_TIMEOUT}s.")
                budget = {"deadline": ios_deadline} if ios_deadline is not None and command[:2] == ("xcrun", "simctl") else {}
                result = self.run(*command, timeout=timeout, **budget, **kwargs)
                if ios_deadline is not None and command[:2] == ("xcrun", "simctl") and time.monotonic() > ios_deadline:
                    raise TimeoutError(f"Owned simulator cleanup exceeded {IOS_CLEANUP_TIMEOUT}s.")
                return result
            except BaseException as error:
                cancellation = original_cancellation(error)
                errors.append(str(error))

        device = self.state.get("device")
        if self.args.platform == "ios":
            if self.args.xcode_dir:
                self.env["DEVELOPER_DIR"] = str(self.args.xcode_dir.resolve())
            intent = self.state.get("simulatorCreate")
            if intent and intent.get("pending"):
                try:
                    listing = clean("xcrun", "simctl", "list", "devices", "-j")
                    require(listing is not None, "Pending simulator creation inventory is unavailable.")
                    identity = self.simulator_candidate(json.loads(listing.stdout), intent)
                    if identity is None:
                        intent.update(pending=False, resolution="absence-verified")
                        self.save()
                    else:
                        self.register_simulator(identity, "exact-inventory")
                    device = self.state.get("device")
                except BaseException as failure:
                    cancellation = cancellation or original_cancellation(failure)
                    errors.append("Pending simulator creation remains unverified: " + str(failure))
                    device = None
            if device:
                devices = clean("xcrun", "simctl", "list", "devices", "-j")
                matches = [d for group in json.loads(devices.stdout)["devices"].values() for d in group
                           if d["udid"] == device] if devices else []
                if matches and matches[0]["name"] != self.state["name"]:
                    errors.append("Simulator identity changed; refusing to touch a different device.")
                elif matches:
                    verified = True
                    if self.state.get("simulatorIdentity"):
                        try:
                            require(self.simulator_candidate(json.loads(devices.stdout), self.state["simulatorCreate"])
                                    == self.state["simulatorIdentity"], "Simulator runtime/type identity changed.")
                        except BaseException as failure:
                            cancellation = cancellation or original_cancellation(failure)
                            errors.append(str(failure))
                            verified = False
                    if verified:
                        clean("xcrun", "simctl", "terminate", device, APP, check=False)
                        clean("xcrun", "simctl", "uninstall", device, APP, check=False)
                        clean("xcrun", "simctl", "shutdown", device, check=False)
                        clean("xcrun", "simctl", "delete", device)
                        remaining = clean("xcrun", "simctl", "list", "devices", "-j")
                        if remaining and device in remaining.stdout:
                            errors.append("Owned simulator remains after deletion.")
        elif device:
            def owned_emulator_pid():
                pid = self.state.get("emulatorPid")
                if not pid:
                    return False
                process = clean("ps", "-ww", "-p", str(pid), "-o", "args=", check=False)
                arguments = process.stdout.split() if process else []
                pairs = set(zip(arguments, arguments[1:]))
                return ("-avd", self.state["name"]) in pairs and ("-port", device.rsplit("-", 1)[-1]) in pairs

            # The serial may have been reused after cancellation: check the unique AVD identity first.
            identity = clean(adb_path(), "-s", device, "emu", "avd", "name", check=False)
            pid_only = False
            if identity and identity.returncode == 0:
                if not identity.stdout.strip():
                    pid_only = owned_emulator_pid()
                    if not pid_only:
                        errors.append("Cannot verify the emulator console or exact journaled AVD/port/PID; refusing serial mutations.")
                elif self.state["name"] not in identity.stdout.splitlines():
                    errors.append("Emulator identity changed; refusing to touch a different AVD.")
                else:
                    clean(adb_path(), "-s", device, "shell", "am", "force-stop", APP, check=False)
                    clean(adb_path(), "-s", device, "uninstall", APP, check=False)
                    mappings = clean(adb_path(), "forward", "--list")
                    if mappings:
                        for row in mappings.stdout.splitlines():
                            parts = row.split()
                            if parts and parts[0] == device:
                                clean(adb_path(), "-s", device, "forward", "--remove", parts[1])
                    clean(adb_path(), "-s", device, "emu", "kill")
            if self.emulator:
                try:
                    stop_owned(self.emulator, grace_period=25, settle_timeout=5)
                except Exception as error:
                    errors.append(str(error))
            elif self.state.get("emulatorPid"):
                pid = self.state["emulatorPid"]
                if owned_emulator_pid():
                    os.kill(pid, signal.SIGTERM)
                    for attempt in range(100):
                        try:
                            os.kill(pid, 0)
                        except ProcessLookupError:
                            break
                        if attempt == 49:
                            os.kill(pid, signal.SIGKILL)
                        time.sleep(0.1)
                    else:
                        errors.append("Owned emulator PID remained after bounded SIGTERM/SIGKILL recovery.")
            if pid_only:
                deadline = time.monotonic() + 10
                while True:
                    devices = clean(adb_path(), "devices")
                    if not devices or not any(row.split()[:1] == [device] for row in devices.stdout.splitlines()):
                        break
                    if time.monotonic() >= deadline:
                        errors.append("Journaled emulator serial remains after its exact owned PID was stopped.")
                        break
                    time.sleep(0.1)
            mappings = clean(adb_path(), "forward", "--list")
            if mappings and any(row.startswith(device + " ") for row in mappings.stdout.splitlines()):
                errors.append("Owned emulator debugger/UI forwards remained after cleanup.")
        if self.emulator_log:
            self.emulator_log.close()
        if self.state.get("brokerPid") or self.state.get("brokerGroup"):
            listener = clean("lsof", "-nP", "-tiTCP:19223", "-sTCP:LISTEN", check=False)
            owned = listener and listener.stdout.strip() == self.state.get("brokerPid")
            if listener and listener.stdout.strip().isdigit() and self.state.get("brokerGroup"):
                try:
                    owned = owned or os.getpgid(int(listener.stdout.strip())) == self.state["brokerGroup"]
                except ProcessLookupError:
                    pass
            if owned:
                deadline = time.monotonic() + 15
                status = clean("maui", "devflow", "broker", "status")
                while status and json.loads(status.stdout).get("agents", 0) and time.monotonic() < deadline:
                    time.sleep(1)
                    status = clean("maui", "devflow", "broker", "status")
                if status and json.loads(status.stdout).get("agents", 0) == 0:
                    clean("maui", "devflow", "broker", "stop")
                else:
                    errors.append("Owned broker still has agents; refusing to stop another investigation's UI session.")
            if self.broker_process and not errors:
                try:
                    stop_owned(self.broker_process)
                except Exception as error:
                    errors.append(str(error))
            if owned and not errors:
                deadline = time.monotonic() + 10
                while time.monotonic() < deadline:
                    remaining = clean("lsof", "-nP", "-tiTCP:19223", "-sTCP:LISTEN", check=False)
                    if not remaining or remaining.stdout.strip() != listener.stdout.strip():
                        break
                    time.sleep(0.1)
                else:
                    errors.append("Owned DevFlow broker listener remained after shutdown.")
        if cancellation is None:
            shutil.rmtree(self.output / "work", ignore_errors=True)
        finish_recoveries()
        if self.process_recoveries or self.recovery_fallback is not None:
            self.state["processRecovery"] = self.recovery_state()
            self.state["commandRecoveryRequired"] = any(not r.complete for r in self.owned_recoveries())
        self.state["cleanupErrors"] = errors
        builds = [r for r in self.owned_recoveries() if getattr(r, "is_build", False)]
        if builds:
            self.state["buildGroupSettled"] = all(r.complete for r in builds)
        else:
            self.state.setdefault("buildGroupSettled", not any(r.get("isBuild") and not all(r.get(key, False) for key in
                ("leaderReaped", "groupAbsent", "drainComplete")) for r in self.state.get("processRecovery", [])))
        resource_clean = not errors
        if not self.state["buildGroupSettled"]:
            errors.append("Build process-group settlement remains unverified.")
        if self.args.platform == "ios":
            self.state["simulatorJournalClean"] = resource_clean
        self.state["resourceJournalClean"] = resource_clean
        self.state["cleanupDone"] = resource_clean and self.state["buildGroupSettled"]
        try:
            self.save()
        except BaseException as write_error:
            errors.append(f"state.json: {write_error}")
            cancellation = cancellation or original_cancellation(write_error)
            self.state["cleanupDone"] = self.state["resourceJournalClean"] = False
        if cancellation is not None:
            raise cancellation
        require(not errors, "Cleanup failed: " + "; ".join(errors))

    def prepare_evidence(self, error):
        manifest = {**self.source_record(),
                    "platform": self.args.platform, "pins": PIN,
                    "passed": False, "error": error or "Evidence collection is incomplete.",
                    "phases": {"required": self.state.get("requiredPhases", []),
                               "completed": self.state.get("completedPhases", []),
                               "complete": self.state.get("phasesComplete", False)},
                    "cleanup": self.state.get("cleanupDone", False),
                    "buildGroupSettled": self.state.get("buildGroupSettled", True),
                    "simulatorJournalClean": self.state.get("simulatorJournalClean", False),
                    "resourceJournalClean": self.state.get("resourceJournalClean", self.state.get("cleanupDone", False)),
                    "cleanupErrors": self.state.get("cleanupErrors", []),
                    "processRecovery": self.state.get("processRecovery", []),
                    "commandRecoveryRequired": self.state.get("commandRecoveryRequired", False),
                    "recoveriesUnavailable": bool(self.state.get("commandRecoveryRequired") and not any(self.owned_recoveries())),
                    "simulatorCreate": self.state.get("simulatorCreate"),
                    "evidenceErrors": self.state.get("evidenceErrors", []), "files": {}}
        if self.args.platform in ("ios", "android"):
            manifest["runtimeIdentifier"] = self.ios["rid"] if self.args.platform == "ios" else self.android["rid"]
        self.evidence_core = manifest
        return manifest

    def persist_evidence_core(self):
        destination = self.output / "evidence"
        destination.mkdir(exist_ok=True)
        atomic_json(destination / "manifest.json", self.evidence_core)

    def evidence(self, passed, error):
        manifest = self.prepare_evidence(error)
        self.persist_evidence_core()
        destination = self.output / "evidence"
        total = 0
        for path in self.output.rglob("*"):
            relative = path.relative_to(self.output)
            if (not path.is_file() or any(p in relative.parts for p in ("evidence", "work", "assets", "tools")) or
                    path.name == "state.json" or path.suffix not in (".json", ".jsonl", ".log", ".png", ".binlog")):
                continue
            size = path.stat().st_size
            total += size
            require(size <= 8 * 1024 * 1024 and total <= 100 * 1024 * 1024,
                    "Mobile evidence exceeds 8 MiB/file or 100 MiB/run; refusing unbounded uploads.")
            data = path.read_bytes()
            if path.suffix != ".binlog":
                require(b'"Token"' not in data and b'"token"' not in data, f"Credential-shaped evidence refused: {relative}")
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            manifest["files"][str(relative)] = hashlib.sha256(data).hexdigest()
        manifest["error"] = error
        manifest["passed"] = (passed and self.state.get("cleanupDone", False) and
                              self.state.get("buildGroupSettled", True) and
                              not self.state.get("commandRecoveryRequired", False) and
                              not self.state.get("simulatorCreate", {}).get("pending", False) and
                              (self.args.platform == "contracts" or self.state.get("phasesComplete", False)))
        self.persist_evidence_core()


def contracts(suite):
    suite.run(sys.executable, "-m", "pip", "list", "--format=json")
    suite.run(sys.executable, "tests/DncDbg.RemoteTests/python_environment.py")
    suite.run("dotnet", "tests/DncDbg.RemoteTests/bin/Release/net10.0/DncDbg.RemoteTests.dll", "--fixture-control-contracts")
    for path in ("src/DncDbg.Remote/generate_contract.py", "tests/DncDbg.RemoteTests/test_dap.py",
                 "tests/DncDbg.RemoteTests/test_mobile_processes.py", "tests/DncDbg.RemoteTests/test_mobile_runner.py",
                 "tests/DncDbg.RemoteTests/test_android_provisioning.py"):
        options = ["--check"] if "generate_contract" in path else ["--parallel"] if path.endswith("test_dap.py") else []
        suite.run(sys.executable, path, *options, timeout=180)
    suite.run(sys.executable, "src/DncDbg.Remote.Target/build.py", "--test-host")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", required=True, choices=["contracts", "ios", "android"])
    parser.add_argument("--output", type=Path)
    parser.add_argument("--xcode-dir", type=Path)
    parser.add_argument("--ios-architecture", choices=["arm64", "x64"], default="arm64",
                        help="Select the separately pinned simulator tuple; no host-architecture fallback")
    parser.add_argument("--android-abi", choices=["arm64-v8a", "x86_64"], default="arm64-v8a",
                        help="Explicit separate local Apple Silicon or hosted Linux/KVM tuple; no automatic substitution")
    parser.add_argument("--cleanup", action="store_true", help="Idempotent recovery using this run's ownership journal")
    args = parser.parse_args()
    args.output = args.output or ROOT / "build/mobile-tests" / f"{args.platform}-{uuid.uuid4().hex}"
    suite = Suite(args)
    passed, primary, cancellation = False, None, None

    def remember(failure):
        nonlocal primary, cancellation
        if isinstance(primary, (TimeoutError, subprocess.TimeoutExpired)):
            return
        if cancellation is None:
            cancellation = original_cancellation(failure)
        if primary is None:
            primary = failure

    def settle(recovery):
        if recovery is None or recovery.process is None:
            return
        if cancellation is None and original_cancellation(recovery.primary_error) is not None:
            remember(recovery.primary_error)
        try:
            if not recovery.complete and not recovery.finish():
                remember(recovery.failure_exception)
        except BaseException as failure:
            remember(failure)
        finally:
            if original_cancellation(recovery.primary_error) is not None:
                remember(recovery.primary_error)

    def interrupted(_signal, _frame):
        raise KeyboardInterrupt("Mobile run interrupted")

    signal.signal(signal.SIGTERM, interrupted)
    try:
        if args.cleanup:
            if not suite.state_path.exists():
                return 0
            suite.state = json.loads(suite.state_path.read_text())
            require(suite.state["platform"] == args.platform, "Cleanup platform does not match the journal.")
            if (suite.state.get("cleanupDone") and suite.state.get("buildGroupSettled", True) and
                    not suite.state.get("commandRecoveryRequired", False) and
                    not suite.state.get("simulatorCreate", {}).get("pending", False) and
                    all(r.get("identityCapture") != "unverified" and all(r.get(key, False) for key in
                        ("leaderReaped", "groupAbsent", "drainComplete")) for r in suite.state.get("processRecovery", []))):
                return 0
            try:
                suite.cleanup()
            except BaseException as failure:
                remember(failure)
        else:
            require(not suite.output.exists(), f"Evidence output already exists; choose a fresh path: {suite.output}")
            suite.output.mkdir(parents=True)
            suite.state = {"platform": args.platform, "cleanupDone": False}
            suite.save()
            try:
                if args.platform == "contracts":
                    contracts(suite)
                else:
                    suite.execute()
                    require(suite.state.get("phasesComplete", False),
                            "The full mobile suite did not complete every required phase.")
                passed = True
            except BaseException as failure:
                remember(failure)
            finally:
                try:
                    if args.platform != "contracts":
                        suite.cleanup()
                    else:
                        suite.state["cleanupDone"] = True
                        suite.save()
                except BaseException as failure:
                    remember(failure)

        if (passed or args.cleanup) and not suite.state.get("cleanupDone", False):
            remember(RuntimeError("Owned cleanup remains unverified after the required mobile phases."))
        # All rich reporting/bookkeeping is inside the outer non-escaping perimeter.
        error = repr(primary) if primary is not None else None
        if args.cleanup and error is None:
            error = "Recovery after an interrupted mobile run; rerun all gates on a fresh output."
        suite.prepare_evidence(error)
        try:
            suite.persist_evidence_core()
            if primary is not None:
                (suite.output / "failure.log").write_text(
                    "".join(traceback.format_exception(type(primary), primary, primary.__traceback__)))
                print(error, file=sys.stderr, flush=True)
            suite.evidence(passed and primary is None, error)
        except BaseException as failure:
            remember(failure)
            passed = False
            error = f"{error or ''}\nEvidence: {failure!r}"
            suite.state.setdefault("evidenceErrors", []).append(repr(failure))
            try:
                suite.evidence_core.update(passed=False, error=error,
                                           evidenceErrors=suite.state["evidenceErrors"])
                suite.persist_evidence_core()
            except BaseException as write_error:
                remember(write_error)
                suite.state.setdefault("evidenceErrors", []).append(repr(write_error))
                print(f"Evidence failure: {write_error}", file=sys.stderr)
            print(error, file=sys.stderr)
        print(f"{'PASS' if passed and primary is None else 'FAIL'}: {args.platform}; "
              f"evidence: {suite.output.relative_to(ROOT)}/evidence")
    except BaseException as failure:
        remember(failure)
        try:
            print(repr(primary), file=sys.stderr, flush=True)
        except BaseException as reporting_failure:
            remember(reporting_failure)
    finally:
        try:
            # Retention is established before launch; settle this slot before any iterator/report allocation.
            settle(suite.recovery_fallback)
            for recovery in suite.process_recoveries:
                settle(recovery)
        finally:
            if cancellation is not None:
                raise cancellation
    return 0 if primary is None and (args.cleanup or passed) else 1


if __name__ == "__main__":
    raise SystemExit(main())
