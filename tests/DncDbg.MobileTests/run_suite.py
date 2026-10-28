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

sys.path.insert(0, str(ROOT / "tests/DncDbg.MauiTestApp"))
from validate import start_owned, stop_owned

PIN = json.loads(Path(__file__).with_name("tuple.json").read_text())
FIXTURE = ROOT / "tests/DncDbg.MauiTestApp"
APP = "com.dncdbg.mobiletests"


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def properties(path):
    return {key.strip(): value.strip() for line in path.read_text().splitlines() if "=" in line
            for key, value in [line.split("=", 1)]}


class Suite:
    def __init__(self, args):
        self.args = args
        self.output = args.output.resolve()
        require(self.output.is_relative_to((ROOT / "build").resolve()), "Evidence must be under this checkout's build/.")
        self.state_path = self.output / "state.json"
        self.env = dict(os.environ)
        self.state = {}
        self.number = max((int(p.name.split("-", 1)[0]) for p in self.output.glob("*.log")
                           if p.name.split("-", 1)[0].isdigit()), default=0)
        self.emulator = None
        self.emulator_log = None
        self.broker_process = None

    def save(self):
        self.state_path.write_text(json.dumps(self.state, indent=2))

    def run(self, *command, cwd=ROOT, timeout=180, check=True, input=None, grouped=True):
        self.number += 1
        print("RUN:", " ".join(str(c) for c in command), flush=True)
        process = subprocess.Popen([str(c) for c in command], cwd=cwd, env=self.env, text=True,
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

    def json(self, *command, **kwargs):
        return json.loads(self.run(*command, **kwargs).stdout)

    def preflight(self):
        require(os.name == "posix", "Device orchestration requires POSIX process ownership.")
        require(platform.system() == "Darwin" and platform.machine() == "arm64",
                "The pinned private ABI requires Apple Silicon: iOS simulator or accelerated Android arm64 emulator.")
        self.env = tool_environment(self.args.platform)
        scratch = self.output / "work/scratch"
        scratch.mkdir(parents=True)
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
            require(version.strip() == f"Xcode {PIN['ios']['xcode']}\nBuild version {PIN['ios']['xcodeBuild']}",
                    f"Expected Xcode {PIN['ios']['xcode']} ({PIN['ios']['xcodeBuild']}), got {version!r}.")
            runtimes = self.json("xcrun", "simctl", "list", "runtimes", "-j")["runtimes"]
            require(any(r["identifier"] == PIN["ios"]["runtime"] and r.get("isAvailable") and
                        r["buildversion"] == PIN["ios"]["runtimeBuild"] for r in runtimes),
                    f"Required simulator runtime {PIN['ios']['runtime']} ({PIN['ios']['runtimeBuild']}) unavailable.")
        else:
            sdk = android_sdk()
            self.env["PATH"] = str(sdk / "emulator") + os.pathsep + self.env["PATH"]
            image = properties(sdk / "system-images" / Path(*PIN["android"]["systemImage"].split(";")[1:]) /
                               "source.properties")
            require(image.get("Pkg.Revision") == PIN["android"]["imageRevision"] and
                    image.get("AndroidVersion.ApiLevel") == "37.1" and
                    image.get("SystemImage.TagId") == "google_apis,page_size_16kb" and
                    image.get("SystemImage.Abi") == "arm64-v8a",
                    "Required Android API/image revision/ABI unavailable; never substitute x86_64.")
            emulator_properties = properties(sdk / "emulator/source.properties")
            require(emulator_properties.get("Pkg.Revision") == PIN["android"]["emulatorVersion"],
                    f"Required Android emulator {PIN['android']['emulatorVersion']} unavailable.")
            require(properties(sdk / f"ndk/{PIN['android']['ndk']}/source.properties").get("Pkg.Revision") == PIN["android"]["ndk"],
                    "Pinned Android NDK unavailable.")
            self.run(sdk / "emulator/emulator", "-accel-check")
        self.state["pins"] = PIN
        self.save()

    def boot(self):
        name = "dncdbg-" + uuid.uuid4().hex
        self.state["name"] = name
        self.save()
        if self.args.platform == "ios":
            device = self.run("xcrun", "simctl", "create", name, PIN["ios"]["deviceType"],
                              PIN["ios"]["runtime"]).stdout.strip()
            self.state["device"] = device
            self.save()
            self.run("xcrun", "simctl", "boot", device)
            self.run("xcrun", "simctl", "bootstatus", device, "-b", timeout=240)
        else:
            avd = self.output / "work/avd"
            avd.mkdir()
            self.env["ANDROID_AVD_HOME"] = str(avd)
            sdk = android_sdk()
            manager = sdk / "cmdline-tools/latest/bin/avdmanager"
            self.run(manager, "create", "avd", "--name", name, "--package", PIN["android"]["systemImage"],
                     "--device", PIN["android"]["deviceType"], input="no\n")
            ports = list(range(5554, 5682, 2))
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
            require(self.run(adb_path(), "-s", device, "shell", "getprop", "ro.product.cpu.abi").stdout.strip() == "arm64-v8a",
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
        rid = "iossimulator-arm64" if self.args.platform == "ios" else "android-arm64"
        tfm = "net11.0-" + self.args.platform
        if configuration == "Debug":
            command = [sys.executable, "src/DncDbg.Remote.Target/build.py", rid, "--configuration", "Debug"]
            if faults:
                command.append("--test-faults")
            self.run(*command)
        properties = [f"-p:RuntimeIdentifier={rid}", "-p:EnableExperimentalRemoteDebugging=true",
                      "-p:EnableExperimentalPrivateBootstrap=true", f"-p:EnableExperimentalTestFaults={str(faults).lower()}",
                      "-p:EmbedAssembliesIntoApk=true", "-p:UseMonoRuntime=false"]
        result = self.json("dotnet", "msbuild", ".", f"-p:TargetFramework={tfm}", f"-p:Configuration={configuration}",
                           *properties, "-getProperty:MauiVersion,DefineConstants",
                           "-getItem:NativeReference,AndroidNativeLibrary,PackageReference,ProjectReference", cwd=FIXTURE)
        require(result["Properties"]["MauiVersion"] == PIN["maui"], "MAUI package differs from the pinned workload.")
        if configuration == "Release":
            require(not result["Items"]["NativeReference"] and not result["Items"]["AndroidNativeLibrary"] and
                    "DNCDBG_PRIVATE_BOOTSTRAP" not in result["Properties"]["DefineConstants"] and
                    not any("DevFlow" in i["Identity"] for i in result["Items"]["PackageReference"]) and
                    not any("DncDbg.Remote" in i["Identity"] for i in result["Items"]["ProjectReference"]),
                    "Experimental code must remain absent even with every opt-in supplied to Release.")
        self.run("dotnet", "build", ".", "-t:Rebuild", "-c", configuration, "-f", tfm, *properties,
                 "--nologo", "-v:q", cwd=FIXTURE, timeout=600)
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
                 "--app", app, "--assets", assets, "--output", self.output / name, timeout=900)

    def execute(self):
        self.preflight()
        self.boot()
        self.broker()
        self.build("Release", faults=True)
        app, assets = self.build()
        self.proof("run_device.py", app, assets, "dap")
        try:
            app, assets = self.build(faults=True)
            self.proof("run_faults.py", app, assets, "faults")
        finally:
            # Faults must not leave the ordinary helper/package in a poisoned build state.
            app, assets = self.build()
        self.proof("run_device.py", app, assets, "restored-dap")
        for disconnect in (False, True):
            command = [sys.executable, str(FIXTURE / "validate.py"), "--platform", self.args.platform,
                       "--device", self.state["device"], "--private-bootstrap", "--output",
                       str(self.output / ("direct-disconnect" if disconnect else "direct-continue"))]
            if self.args.platform == "android":
                command += ["--agent-port", "9223", "--allow-fallback"]
            else:
                command += ["--build-developer-dir", str(self.args.xcode_dir.resolve())]
            if disconnect:
                command.append("--private-disconnect")
            self.run(*command, timeout=600)

    def cleanup(self):
        errors = []

        def clean(*command, **kwargs):
            try:
                return self.run(*command, timeout=30, **kwargs)
            except Exception as error:
                errors.append(str(error))

        device = self.state.get("device")
        if self.args.platform == "ios":
            if self.args.xcode_dir:
                self.env["DEVELOPER_DIR"] = str(self.args.xcode_dir.resolve())
            if device:
                devices = clean("xcrun", "simctl", "list", "devices", "-j")
                matches = [d for group in json.loads(devices.stdout)["devices"].values() for d in group
                           if d["udid"] == device] if devices else []
                if matches and matches[0]["name"] != self.state["name"]:
                    errors.append("Simulator identity changed; refusing to touch a different device.")
                elif matches:
                    clean("xcrun", "simctl", "terminate", device, APP, check=False)
                    clean("xcrun", "simctl", "uninstall", device, APP, check=False)
                    clean("xcrun", "simctl", "shutdown", device, check=False)
                    clean("xcrun", "simctl", "delete", device)
                    remaining = clean("xcrun", "simctl", "list", "devices", "-j")
                    if remaining and device in remaining.stdout:
                        errors.append("Owned simulator remains after deletion.")
        elif device:
            # The serial may have been reused after cancellation: check the unique AVD identity first.
            identity = clean(adb_path(), "-s", device, "emu", "avd", "name", check=False)
            if identity and identity.returncode == 0:
                if self.state["name"] not in identity.stdout.splitlines():
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
                    stop_owned(self.emulator)
                except Exception as error:
                    errors.append(str(error))
            elif self.state.get("emulatorPid"):
                pid = self.state["emulatorPid"]
                process = clean("ps", "-p", str(pid), "-o", "args=", check=False)
                if process and self.state["name"] in process.stdout:
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
        shutil.rmtree(self.output / "work", ignore_errors=True)
        self.state["cleanupErrors"] = errors
        self.state["cleanupDone"] = not errors
        self.save()
        require(not errors, "Cleanup failed: " + "; ".join(errors))

    def evidence(self, passed, error):
        destination = self.output / "evidence"
        destination.mkdir(exist_ok=True)
        manifest = {"head": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
                    "dirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip()),
                    "platform": self.args.platform, "pins": PIN, "passed": passed, "error": error,
                    "cleanup": self.state.get("cleanupDone", False),
                    "cleanupErrors": self.state.get("cleanupErrors", []), "files": {}}
        total = 0
        for path in self.output.rglob("*"):
            relative = path.relative_to(self.output)
            if (not path.is_file() or any(p in relative.parts for p in ("evidence", "work", "assets", "tools")) or
                    path.name == "state.json" or path.suffix not in (".json", ".jsonl", ".log", ".png")):
                continue
            size = path.stat().st_size
            total += size
            require(size <= 8 * 1024 * 1024 and total <= 100 * 1024 * 1024,
                    "Mobile evidence exceeds 8 MiB/file or 100 MiB/run; refusing unbounded uploads.")
            data = path.read_bytes()
            require(b'"Token"' not in data and b'"token"' not in data, f"Credential-shaped evidence refused: {relative}")
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            manifest["files"][str(relative)] = hashlib.sha256(data).hexdigest()
        (destination / "manifest.json").write_text(json.dumps(manifest, indent=2))


def contracts(suite):
    for path in ("src/DncDbg.Remote/generate_contract.py", "tests/DncDbg.RemoteTests/test_dap.py",
                 "tests/DncDbg.RemoteTests/test_mobile_processes.py", "tests/DncDbg.RemoteTests/test_mobile_runner.py"):
        suite.run(sys.executable, path, *(["--check"] if "generate_contract" in path else []), timeout=180)
    suite.run(sys.executable, "src/DncDbg.Remote.Target/build.py", "--test-host")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", required=True, choices=["contracts", "ios", "android"])
    parser.add_argument("--output", type=Path)
    parser.add_argument("--xcode-dir", type=Path)
    parser.add_argument("--cleanup", action="store_true", help="Idempotent recovery using this run's ownership journal")
    args = parser.parse_args()
    args.output = args.output or ROOT / "build/mobile-tests" / f"{args.platform}-{uuid.uuid4().hex}"
    suite = Suite(args)
    if args.cleanup:
        if not suite.state_path.exists():
            return 0
        suite.state = json.loads(suite.state_path.read_text())
        require(suite.state["platform"] == args.platform, "Cleanup platform does not match the journal.")
        if not suite.state.get("cleanupDone"):
            try:
                suite.cleanup()
            finally:
                suite.evidence(False, "Recovery after an interrupted mobile run; rerun all gates on a fresh output.")
        return 0
    require(not suite.output.exists(), f"Evidence output already exists; choose a fresh path: {suite.output}")
    suite.output.mkdir(parents=True)
    suite.state = {"platform": args.platform, "cleanupDone": False}
    suite.save()
    passed, error = False, None

    def interrupted(_signal, _frame):
        raise KeyboardInterrupt("Mobile run interrupted")

    signal.signal(signal.SIGTERM, interrupted)
    try:
        if args.platform == "contracts":
            contracts(suite)
        else:
            suite.execute()
        passed = True
    except BaseException as failure:
        error = repr(failure)
        (suite.output / "failure.log").write_text(traceback.format_exc())
        print(error, file=sys.stderr, flush=True)
    finally:
        try:
            if args.platform != "contracts":
                suite.cleanup()
            else:
                suite.state["cleanupDone"] = True
                suite.save()
        except BaseException as failure:
            passed, error = False, f"{error or ''}\nCleanup: {failure!r}"
            print(error, file=sys.stderr)
        try:
            suite.evidence(passed, error)
        except Exception as failure:
            passed = False
            error = f"{error or ''}\nEvidence: {failure!r}"
            destination = suite.output / "evidence"
            destination.mkdir(exist_ok=True)
            (destination / "manifest.json").write_text(json.dumps({
                "passed": False, "error": error, "cleanup": suite.state.get("cleanupDone", False)}, indent=2))
            print(error, file=sys.stderr)
    print(f"{'PASS' if passed else 'FAIL'}: {args.platform}; evidence: {suite.output.relative_to(ROOT)}/evidence")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
