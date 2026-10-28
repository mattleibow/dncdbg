#!/usr/bin/env python3
"""Simulator/emulator-only evidence runner. Exit 2 means the mobile debugging gate is blocked."""
import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import time
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "DncDbg.MobileTests"))
from mobile_tools import adb_path

ROOT = Path(__file__).resolve().parents[2]
APP = "com.dncdbg.mobiletests"
PROJECT = "tests/DncDbg.MauiTestApp"
EVIDENCE = ROOT / "build/validation/mobile/net11-default"


def start_owned(*args, **kwargs):
    if os.name != "posix":
        raise RuntimeError("The mobile runner requires POSIX process-group ownership")
    return subprocess.Popen(*args, start_new_session=True, **kwargs)


def stop_owned(process):
    def group_exists():
        process.poll()
        groups = subprocess.run(["ps", "-axo", "pgid="], check=True, capture_output=True,
                                text=True, timeout=5).stdout.split()
        return str(process.pid) in groups

    def signal_group(value):
        if value == 0:
            return group_exists()
        try:
            os.killpg(process.pid, value)
            return True
        except ProcessLookupError:
            return False
        except PermissionError:
            # macOS can return EPERM rather than ESRCH for a group that just vanished.
            # Never suppress it while ps still observes any member of the owned group.
            if not group_exists():
                return False
            raise

    # The leader may already have exited while its debugger/CLI child is alive.
    if signal_group(signal.SIGTERM):
        deadline = time.monotonic() + 1
        while time.monotonic() < deadline:
            process.poll()
            if not signal_group(0):
                break
            time.sleep(0.02)
        else:
            signal_group(signal.SIGKILL)
    process.wait(timeout=1)
    deadline = time.monotonic() + 1
    while signal_group(0):
        if time.monotonic() >= deadline:
            raise RuntimeError(f"Owned process group {process.pid} remained after termination")
        time.sleep(0.02)


def run(*args, env=None, check=True, input=None, timeout=180, log=None, cwd=ROOT):
    with start_owned(args, cwd=cwd, env=env, text=True, stdin=subprocess.PIPE if input is not None else None,
                     stdout=subprocess.PIPE, stderr=subprocess.PIPE) as process:
        try:
            stdout, stderr = process.communicate(input, timeout=timeout)
            result = subprocess.CompletedProcess(args, process.returncode, stdout, stderr)
        finally:
            stop_owned(process)
    if log:
        (EVIDENCE / log).write_text(result.stdout + result.stderr)
    if check and result.returncode:
        raise RuntimeError(f"{args[0]} failed ({result.returncode}): {result.stderr[-2000:]}")
    return result


def native_listener_closed(port, forwarded):
    with socket.socket() as verify:
        verify.settimeout(1)
        if verify.connect_ex(("127.0.0.1", port)) != 0:
            return True
        if forwarded:
            # ADB still accepts on the host after the target listener closes,
            # then signals target connection refusal with EOF.
            try:
                return verify.recv(1) == b""
            except socket.timeout:
                return False
        return False


def main():
    global EVIDENCE
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", required=True, choices=["ios", "android"])
    parser.add_argument("--device", required=True, help="An already booted, dedicated simulator UDID or emulator serial")
    parser.add_argument("--runtime", choices=["default", "coreclr", "mono"], default="default",
                        help="Default leaves UseMonoRuntime unset; explicit overrides are separate experiments")
    parser.add_argument("--build-developer-dir", help="Scoped iOS build Xcode selection; simctl remains unchanged")
    parser.add_argument("--agent-port", type=int, help="Explicit Android fallback DevFlow port, normally 9223")
    parser.add_argument("--allow-fallback", action="store_true", help="Collect UI proof even when broker wait fails")
    parser.add_argument("--replace-fixture", action="store_true", help="Permit replacing this fixture on the dedicated target")
    parser.add_argument("--public-attach-probe", action="store_true",
                        help="After UI validation, test the existing public desktop attach API on the owned iOS simulator app")
    parser.add_argument("--startup-probes", action="store_true",
                        help="Record allowlisted startup environment and separate public factory/provider/notification phases")
    parser.add_argument("--diagnostic-trace", action="store_true",
                        help="Test the documented mobile diagnostic-port route using dotnet-dsrouter and dotnet-trace")
    parser.add_argument("--diagnostic-transport", choices=["tcp", "uds"], default="tcp",
                        help="TCP is the supported mobile route; UDS preserves the iOS negative experiment")
    parser.add_argument("--shared-debugger-ipc", action="store_true",
                        help="iOS only: give the owned target and probe the same short project-owned TMPDIR")
    parser.add_argument("--private-bootstrap", action="store_true",
                        help="Opt into exact-build-pinned, version-fragile target-native private DBI startup")
    parser.add_argument("--private-disconnect", action="store_true",
                        help="Drop the authenticated native session while stopped, instead of continuing explicitly")
    parser.add_argument("--output", type=Path, help="Run-owned evidence directory (used by the top-level mobile suite)")
    args = parser.parse_args()
    if args.platform == "android" and not args.device.startswith("emulator-"):
        parser.error("Physical Android devices are intentionally not supported")
    if args.public_attach_probe and args.platform != "ios":
        parser.error("The host public attach probe only applies to an iOS simulator process, not an emulator PID")
    if args.shared_debugger_ipc and args.platform != "ios":
        parser.error("A shared debugger IPC directory applies only to the iOS simulator's host filesystem")
    if args.diagnostic_trace and args.diagnostic_transport == "uds" and args.platform != "ios":
        parser.error("The UDS diagnostic experiment applies only to the iOS simulator")
    if args.private_bootstrap and args.runtime == "mono":
        parser.error("The private bootstrap requires CoreCLR")
    if args.private_disconnect and not args.private_bootstrap:
        parser.error("--private-disconnect requires --private-bootstrap")
    if args.private_bootstrap and (args.public_attach_probe or args.startup_probes):
        parser.error("Private target execution ownership cannot be combined with a host startup/attach controller")
    EVIDENCE = ROOT / ("build/validation/mobile/private-bootstrap" if args.private_bootstrap else f"build/validation/mobile/net11-{args.runtime}")
    if args.private_disconnect:
        EVIDENCE /= "disconnect"
    if args.output:
        EVIDENCE = args.output.resolve()
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    prefix = args.platform
    adb = adb_path() if prefix == "android" else None
    forwards = []
    added_reverse = False
    installed = False
    target_pid = None
    tracer = None
    router = None
    trace_output = None
    router_output = None
    diagnostic_socket = None
    ipc_directory = None
    diagnostic_tcp_port = None
    diagnostic_reverse_added = False
    probe_env = os.environ.copy()
    endpoint_file = EVIDENCE / f"{prefix}-endpoint.json"
    forwarded_file = EVIDENCE / f"{prefix}-forwarded-endpoint.json"

    def device(*command, **kwargs):
        return run(adb, "-s", args.device, *command, **kwargs)

    def add_forward(remote, local=0):
        existing = run(adb, "forward", "--list").stdout.splitlines()
        if local and f"{args.device} tcp:{local} tcp:{remote}" in existing:
            return local
        # --no-rebind prevents overwriting another test's port.
        port = device("forward", "--no-rebind", f"tcp:{local}", f"tcp:{remote}").stdout.strip()
        actual = local or int(port)
        forwards.append(actual)
        return actual

    try:
        if prefix == "ios":
            devices = json.loads(run("xcrun", "simctl", "list", "devices", "booted", "-j").stdout)
            if not any(d["udid"] == args.device for values in devices["devices"].values() for d in values):
                raise RuntimeError("The exact iOS simulator must already be booted")
            previous = run("xcrun", "simctl", "get_app_container", args.device, APP, "app", check=False)
            if previous.returncode == 0 and not args.replace_fixture:
                raise RuntimeError("Fixture already installed; choose a fresh simulator or --replace-fixture")
        else:
            if device("shell", "getprop", "sys.boot_completed").stdout.strip() != "1":
                raise RuntimeError("The exact Android emulator must already be booted")
            previous = device("shell", "pm", "path", APP, check=False)
            if previous.stdout.strip().startswith("package:") and not args.replace_fixture:
                raise RuntimeError("Fixture already installed; choose a fresh emulator or --replace-fixture")
            if "tcp:19223 tcp:19223" not in device("reverse", "--list").stdout:
                device("reverse", "tcp:19223", "tcp:19223")
                added_reverse = True

        run("dotnet", "build", "tests/DncDbg.RemoteTests", "-c", "Release", "--nologo", "-v:q",
            timeout=180, log=f"{prefix}-host-build.log")
        helper = ["dotnet", str(ROOT / "tests/DncDbg.RemoteTests/bin/Release/net10.0/DncDbg.RemoteTests.dll")]
        env = os.environ.copy()
        if args.build_developer_dir:
            env["DEVELOPER_DIR"] = args.build_developer_dir
        tfm = f"net11.0-{prefix}"
        rid = "iossimulator-arm64" if prefix == "ios" else "android-arm64"
        properties = [f"-p:RuntimeIdentifier={rid}", "-p:EnableExperimentalRemoteDebugging=true"]
        if args.private_bootstrap:
            run(sys.executable, "src/DncDbg.Remote.Target/build.py", rid, env=env, log=f"{prefix}-native-build.log")
            properties.append("-p:EnableExperimentalPrivateBootstrap=true")
        if args.diagnostic_trace or args.shared_debugger_ipc:
            parent = ROOT / "build/i"
            parent.mkdir(mode=0o700, parents=True, exist_ok=True)
            ipc_directory = parent / f"{os.getpid():x}"
            ipc_directory.mkdir(mode=0o700)
            environment_options = []
            if args.shared_debugger_ipc:
                probe_env["TMPDIR"] = str(ipc_directory) + "/"
                environment_options.append(f"--setenv=TMPDIR={ipc_directory}/")
            if args.diagnostic_trace:
                diagnostic_socket = ipc_directory / "s"
                if len(str(diagnostic_socket).encode()) >= 108:
                    raise RuntimeError("The diagnostic socket must be shorter than 108 bytes")
                address = str(diagnostic_socket)
                if args.diagnostic_transport == "tcp":
                    with socket.socket() as reserve:
                        reserve.bind(("127.0.0.1", 0))
                        diagnostic_tcp_port = reserve.getsockname()[1]
                    address = f"127.0.0.1:{diagnostic_tcp_port}"
                diagnostic_environment = {
                    "DOTNET_EnableDiagnostics": "1", "DOTNET_EnableDiagnostics_IPC": "1",
                    "DOTNET_DiagnosticPorts": f"{address},nosuspend,connect",
                }
                environment_options += [f"--setenv={name}={value.replace(',', '%2C')}" for name, value in diagnostic_environment.items()]
            if prefix == "ios":
                # The simulator rewrites launch-time TMPDIR. The SDK applies these before CoreCLR initializes.
                properties.append("-p:AppBundleExtraOptions=" + " ".join(environment_options))
            else:
                android_environment = ipc_directory / "android.env"
                android_environment.write_text("".join(f"{name}={value}\n" for name, value in diagnostic_environment.items()))
                properties.append(f"-p:ExperimentalDiagnosticsEnvironmentFile={android_environment}")
        if args.runtime != "default":
            properties.append(f"-p:UseMonoRuntime={'true' if args.runtime == 'mono' else 'false'}")
        if prefix == "android":
            properties.append("-p:EmbedAssembliesIntoApk=true")
        # SDK selection follows the working directory, not the --project argument.
        # Only the fixture uses .NET 11; the remote host tests retain the repository's .NET 10 pin.
        fixture = ROOT / PROJECT
        sdk_version = run("dotnet", "--version", cwd=fixture).stdout.strip()
        workload_version = run("dotnet", "workload", "--version", cwd=fixture).stdout.strip()
        inspected = json.loads(run("dotnet", "msbuild", ".", f"-p:TargetFramework={tfm}", *properties,
                                   "-getProperty:NETCoreSdkVersion,MauiVersion,MauiWorkloadVersion,TargetPlatformVersion,UseMonoRuntime,_AndroidRuntime,_AndroidRuntimePackRuntime",
                                   env=env, cwd=fixture).stdout)["Properties"]
        (EVIDENCE / f"{prefix}-properties.json").write_text(json.dumps(inspected, indent=2) + "\n")
        run("dotnet", "workload", "list", cwd=fixture, log=f"{prefix}-workloads.log")
        run("dotnet", "build", ".", "-f", tfm, *properties, "--nologo", "-v:q",
            env=env, cwd=fixture, timeout=600, log=f"{prefix}-build.log")
        resolved = json.loads(run("dotnet", "msbuild", ".", f"-p:TargetFramework={tfm}", *properties,
                                  "-t:ResolveFrameworkReferences", "-getItem:ResolvedRuntimePack", "-v:q",
                                  env=env, cwd=fixture, log=f"{prefix}-resolved-packs.json").stdout)
        runtime_packs = [{key: item.get(key) for key in ("NuGetPackageId", "NuGetPackageVersion", "RuntimeIdentifier")}
                         for item in resolved["Items"]["ResolvedRuntimePack"]]
        output = ROOT / PROJECT / "bin/Debug" / tfm / rid
        if prefix == "ios":
            native_assets = [path.name for path in (output / "DncDbg.MauiTestApp.app").rglob("*")
                             if path.is_file() and any(name in path.name.lower() for name in ("coreclr", "mscord", "dbgshim", "monosgen"))]
        else:
            with zipfile.ZipFile(output / f"{APP}-Signed.apk") as package:
                native_assets = [name for name in package.namelist()
                                 if any(part in name.lower() for part in ("coreclr", "mscord", "dbgshim", "monosgen"))]
        (EVIDENCE / f"{prefix}-native-assets.json").write_text(json.dumps(native_assets, indent=2) + "\n")
        if args.diagnostic_trace:
            trace_output = (EVIDENCE / f"{prefix}-diagnostic-trace.log").open("w")
            tracer = start_owned(
                ["dotnet-trace", "collect", "--diagnostic-port", str(diagnostic_socket),
                 "--duration", "00:00:00:02", "--profile", "dotnet-sampled-thread-time",
                 "--output", str(EVIDENCE / f"{prefix}-diagnostic.nettrace")],
                cwd=ROOT, stdout=trace_output, stderr=subprocess.STDOUT, text=True)
            ready = time.monotonic() + 10
            while not diagnostic_socket.exists():
                if tracer.poll() is not None or time.monotonic() >= ready:
                    raise RuntimeError("dotnet-trace did not open the project-owned diagnostic socket")
                time.sleep(0.1)
            if diagnostic_tcp_port is not None:
                router_path = shutil.which("dotnet-dsrouter") or str(ROOT / "build/tools/dotnet-dsrouter")
                router_output = (EVIDENCE / f"{prefix}-diagnostic-router.log").open("w")
                router = start_owned(
                    [router_path, "client-server", "-ipcc", str(diagnostic_socket),
                     "-tcps", f"127.0.0.1:{diagnostic_tcp_port}", "-rt", "120", "-v", "debug"],
                    cwd=ROOT, stdout=router_output, stderr=subprocess.STDOUT, text=True)
                ready = time.monotonic() + 10
                while run("lsof", "-nP", "-a", "-p", str(router.pid), f"-iTCP:{diagnostic_tcp_port}", "-sTCP:LISTEN", check=False).returncode:
                    if router.poll() is not None or time.monotonic() >= ready:
                        raise RuntimeError("dotnet-dsrouter did not open its loopback TCP listener")
                    time.sleep(0.1)
                if prefix == "android":
                    device("reverse", "--no-rebind", f"tcp:{diagnostic_tcp_port}", f"tcp:{diagnostic_tcp_port}")
                    diagnostic_reverse_added = True
        installed = True
        if prefix == "ios":
            run("xcrun", "simctl", "terminate", args.device, APP, check=False)
            run("xcrun", "simctl", "install", args.device, str(output / "DncDbg.MauiTestApp.app"))
            launch_env = os.environ.copy()
            (EVIDENCE / "ios-app-stdout.log").write_text("")
            (EVIDENCE / "ios-app-stderr.log").write_text("")
            run("xcrun", "simctl", "launch", f"--stdout={EVIDENCE / 'ios-app-stdout.log'}",
                f"--stderr={EVIDENCE / 'ios-app-stderr.log'}", args.device, APP, env=launch_env)
        else:
            device("shell", "am", "force-stop", APP)
            device("install", "-r", str(output / f"{APP}-Signed.apk"))
            activity = device("shell", "cmd", "package", "resolve-activity", "--brief", APP).stdout.strip().splitlines()[-1]
            device("shell", "am", "start", "-n", activity)

        wait = run("maui", "devflow", "--device", args.device, "wait", "--wait-platform",
                   "iOS" if prefix == "ios" else "Android", "--timeout", "30",
                   check=False, timeout=45, log=f"{prefix}-wait.json")
        try:
            registration = json.loads(wait.stdout)
            broker_ok = registration.get("platform") == ("iOS" if prefix == "ios" else "Android")
        except (json.JSONDecodeError, AttributeError):
            registration, broker_ok = {}, False
        if not broker_ok and not args.allow_fallback:
            raise RuntimeError("DevFlow broker wait failed; see saved output. Explicit fallback is opt-in.")
        port = registration.get("port") if broker_ok else args.agent_port
        if not port:
            raise RuntimeError("Fallback requires an explicit --agent-port")
        if prefix == "android":
            add_forward(port, port)
        cli = ["maui", "devflow", "--agent-port", str(port)]
        # Broker registration can precede the HTTP listener, especially on a cold CoreCLR launch.
        status_deadline = time.monotonic() + 30
        while True:
            status_result = run(*cli, "agent", "status", check=False, log=f"{prefix}-status.json")
            if status_result.returncode == 0:
                status = json.loads(status_result.stdout)
                break
            if time.monotonic() >= status_deadline:
                raise RuntimeError("Registered DevFlow agent did not become reachable within 30 seconds")
            time.sleep(0.25)
        if status["app"]["packageId"] != APP or status["device"]["platform"].lower() != prefix:
            raise RuntimeError("DevFlow selected the wrong application or platform")
        target_pid = status["app"]["processId"]
        run(*cli, "--device", args.device, "diagnose", log=f"{prefix}-diagnose.json")
        commands = "\n".join([
            "MAUI tree --depth 5",
            "MAUI tap --automationId reset-scenario",
            "MAUI tap --automationId run-scenario",
            "MAUI query --automationId result-status",
            "MAUI tap --automationId reset-scenario",
            "MAUI query --automationId counter-status",
            "MAUI tap --automationId run-scenario",
            f"MAUI screenshot --output {EVIDENCE.relative_to(ROOT)}/{prefix}-scenario.png --overwrite",
        ]) + "\n"
        if not args.private_bootstrap:
            batch = run(*cli, "batch", "--delay", "300", input=commands, log=f"{prefix}-ui-batch.jsonl")
            rows = [json.loads(line) for line in batch.stdout.splitlines()]
            if len(rows) != 8 or any(row["exit_code"] != 0 for row in rows):
                raise RuntimeError("DevFlow UI batch failed; inspect saved JSONL")
            if json.loads(rows[3]["output"])[0]["text"] != "Result: 42":
                raise RuntimeError("Managed scenario did not yield 42")
            if json.loads(rows[5]["output"])[0]["text"] != "Count: 0":
                raise RuntimeError("Reset did not yield Count: 0")

        if prefix == "ios":
            container = Path(run("xcrun", "simctl", "get_app_container", args.device, APP, "data").stdout.strip())
            endpoint = json.loads((container / "Library/dncdbg-remote.json").read_text())
        else:
            endpoint = json.loads(device("exec-out", "run-as", APP, "cat", "files/dncdbg-remote.json").stdout)
            endpoint["Port"] = add_forward(endpoint["Port"])
        # Private, short-lived credentials: never include them in logs or source control.
        descriptor_fd = os.open(forwarded_file, os.O_CREAT | os.O_TRUNC | os.O_WRONLY, 0o600)
        os.fchmod(descriptor_fd, 0o600)
        with os.fdopen(descriptor_fd, "w") as file:
            json.dump(endpoint, file)
        proof = ["--mobile", str(forwarded_file)]
        if args.private_bootstrap:
            proof = ["--mobile-proof", str(forwarded_file), str(fixture / "MainPage.xaml.cs"),
                     str(output / "DncDbg.MauiTestApp.dll"), str(output / "DncDbg.MauiTestApp.pdb"),
                     str(port), "disconnect" if args.private_disconnect else "continue"]
        capability_result = run(*helper, *proof,
                                timeout=90, log=f"{prefix}-remote-capability.log")
        native_cleanup = None
        if args.private_bootstrap:
            cleanup_deadline = time.monotonic() + 25
            while True:
                if prefix == "ios":
                    native_log = (container / "Library/dncdbg-private-startup.jsonl").read_text()
                else:
                    native_log = device("exec-out", "run-as", APP, "cat", "files/dncdbg-private-startup.jsonl").stdout
                phases = [json.loads(row) for row in native_log.splitlines()]
                if phases and phases[-1]["Phase"] == "owner-finished":
                    if not any(row["Phase"] == "cleanup-detach-return" and row["HResult"] == "0x00000000" for row in phases):
                        raise RuntimeError("The native owner finished without successful target detach")
                    native_cleanup = "owner finished, callback/frame leases released, listener closed, DBI terminated"
                    if not native_listener_closed(endpoint["Port"], forwarded=prefix == "android"):
                        raise RuntimeError("Native listener remained open after cleanup")
                    break
                if time.monotonic() >= cleanup_deadline:
                    raise RuntimeError("Native owner did not prove bounded cleanup; inspect its preserved phase log")
                time.sleep(0.1)
        capabilities = json.loads(next(line for line in capability_result.stdout.splitlines() if line.startswith("{")))
        if not capabilities["Runtime"].startswith(".NET 11."):
            raise RuntimeError("The installed application did not report a .NET 11 runtime")
        expected_kind = "Mono" if inspected["UseMonoRuntime"] == "true" else "CoreClr"
        if not capabilities["Runtime"].endswith(f"({expected_kind})"):
            raise RuntimeError("Runtime declaration differs from the measured SDK configuration")
        startup = None
        startup_probes = []
        if args.startup_probes:
            if prefix == "ios":
                startup = json.loads((container / "Library/dncdbg-startup.json").read_text())
            else:
                startup = json.loads(device("exec-out", "run-as", APP, "cat", "files/dncdbg-startup.json").stdout)
            (EVIDENCE / f"{prefix}-startup-environment.json").write_text(json.dumps(startup, indent=2) + "\n")
            if prefix == "ios":
                target_libraries = Path(startup["NativeLibraryDirectory"])
                host_libraries = Path(run("which", "dotnet").stdout.strip()).resolve().parent / "shared/Microsoft.NETCore.App" / startup["Runtime"].removeprefix(".NET ")
                for name, libraries in (("default", None), ("target", target_libraries), ("host", host_libraries)):
                    destination = EVIDENCE / f"ios-startup-{name}.json"
                    command = [*helper, "--startup-probe",
                               str(status["app"]["processId"]), str(target_libraries / "libcoreclr.dylib"), str(destination)]
                    if libraries:
                        command += ["--libraries", str(libraries)]
                    result = run(*command, env=probe_env, check=False, timeout=30, log=f"ios-startup-{name}.log")
                    if result.returncode not in (0, 2):
                        raise RuntimeError("Startup probe failed without a structured result")
                    startup_probes.append(json.loads(destination.read_text()))
            else:
                startup_probes.append({"NotRun": "Android PIDs are not host PIDs; no target-native public dbgshim is packaged."})
        diagnostic_trace = None
        if tracer is not None:
            try:
                trace_code = tracer.wait(timeout=15)
                diagnostic_trace = {"Transport": args.diagnostic_transport, "ExitCode": trace_code,
                                    "TraceExists": (EVIDENCE / f"{prefix}-diagnostic.nettrace").exists()}
            except subprocess.TimeoutExpired:
                diagnostic_trace = {"Timeout": True, "Connected": False}
        public_attach = None
        if args.public_attach_probe:
            probe = run(*helper, "--attach-probe",
                        str(status["app"]["processId"]), env=probe_env, check=False, timeout=60, log="ios-public-attach-probe.log")
            public_attach = json.loads(next(line for line in probe.stdout.splitlines() if line.startswith("{")))
            if probe.returncode not in (0, 2):
                raise RuntimeError("Public attach probe failed without a structured result")
        report = {"platform": prefix, "ui": "passed", "broker_wait": broker_ok,
                  "sdk": sdk_version, "workload_set": workload_version, "runtime_selection": args.runtime,
                  "properties": inspected, "runtime_packs": runtime_packs, "native_assets": native_assets,
                  "capabilities": capabilities, "public_attach_probe": public_attach,
                  "native_cleanup": native_cleanup,
                  "startup_environment": startup, "startup_probes": startup_probes, "diagnostic_trace": diagnostic_trace,
                  "mobile_managed_breakpoint": "blocked", "physical_devices": "not tested"}
        if args.private_bootstrap:
            report["private_bootstrap"] = "experimental target-side portable-PDB-bound int32 breakpoint proof"
            report["mobile_managed_breakpoint"] = "passed"
            report["stop_cleanup"] = "disconnect" if args.private_disconnect else "continue"
        (EVIDENCE / f"{prefix}-report.json").write_text(json.dumps(report, indent=2) + "\n")
        print(json.dumps(report))
        return 0 if args.private_bootstrap else 2
    finally:
        cleanup_errors = []

        def cleanup(action):
            try:
                action()
            except Exception as error:
                cleanup_errors.append(str(error))

        if installed:
            if prefix == "android" and target_pid:
                cleanup(lambda: device("logcat", "-d", "--pid", str(target_pid), "-t", "2000", log="android-target-logcat.log"))
            if args.private_bootstrap:
                # Preserve native phase evidence even if DBI crashed the target before DevFlow connected.
                def preserve_native(name):
                    if prefix == "ios":
                        saved_container = run("xcrun", "simctl", "get_app_container", args.device, APP, "data", check=False)
                        if saved_container.returncode == 0:
                            source = Path(saved_container.stdout.strip()) / "Library" / name
                            if source.exists():
                                (EVIDENCE / f"{prefix}-{name}").write_bytes(source.read_bytes())
                    else:
                        saved = device("exec-out", "run-as", APP, "cat", f"files/{name}", check=False)
                        if saved.returncode == 0:
                            (EVIDENCE / f"{prefix}-{name}").write_text(saved.stdout)
                for name in ("dncdbg-private-preflight.json", "dncdbg-private-startup.jsonl"):
                    cleanup(lambda name=name: preserve_native(name))
            if prefix == "ios":
                cleanup(lambda: run("xcrun", "simctl", "terminate", args.device, APP, check=False))
                cleanup(lambda: run("xcrun", "simctl", "uninstall", args.device, APP))
            else:
                cleanup(lambda: device("shell", "am", "force-stop", APP))
                cleanup(lambda: device("uninstall", APP))
        if tracer is not None:
            cleanup(lambda: stop_owned(tracer))
        if router is not None:
            cleanup(lambda: stop_owned(router))
        if trace_output is not None:
            trace_output.close()
        if router_output is not None:
            router_output.close()
        if diagnostic_socket is not None:
            cleanup(lambda: diagnostic_socket.unlink(missing_ok=True))
        if ipc_directory is not None:
            cleanup(lambda: shutil.rmtree(ipc_directory))
        for port in forwards:
            cleanup(lambda port=port: device("forward", "--remove", f"tcp:{port}"))
        if added_reverse:
            cleanup(lambda: device("reverse", "--remove", "tcp:19223"))
        if diagnostic_reverse_added:
            cleanup(lambda: device("reverse", "--remove", f"tcp:{diagnostic_tcp_port}"))
        for path in [endpoint_file, forwarded_file]:
            cleanup(lambda path=path: path.unlink(missing_ok=True))

        def verify_cleanup():
            if installed:
                if prefix == "ios":
                    if run("xcrun", "simctl", "get_app_container", args.device, APP, "app", check=False).returncode == 0:
                        raise RuntimeError("Owned iOS fixture remained installed")
                    if target_pid:
                        try:
                            os.kill(target_pid, 0)
                        except ProcessLookupError:
                            pass
                        else:
                            raise RuntimeError("Owned iOS fixture PID remained alive")
                else:
                    if device("shell", "pidof", APP, check=False).stdout.strip():
                        raise RuntimeError("Owned Android fixture PID remained alive")
                    if device("shell", "pm", "path", APP, check=False).stdout.strip().startswith("package:"):
                        raise RuntimeError("Owned Android fixture remained installed")
            if prefix == "android":
                mappings = run(adb, "forward", "--list").stdout.splitlines()
                if any(row.startswith(f"{args.device} tcp:{port} ") for row in mappings for port in forwards):
                    raise RuntimeError("Owned ADB forward remained")
                reverse = device("reverse", "--list").stdout
                if added_reverse and "tcp:19223 tcp:19223" in reverse:
                    raise RuntimeError("Owned broker reverse remained")
                if diagnostic_reverse_added and f"tcp:{diagnostic_tcp_port} tcp:{diagnostic_tcp_port}" in reverse:
                    raise RuntimeError("Owned diagnostic reverse remained")
            if any(path.exists() for path in (endpoint_file, forwarded_file)):
                raise RuntimeError("Owned endpoint descriptor remained")
            (EVIDENCE / f"{prefix}-cleanup.json").write_text(json.dumps({
                "fixture_uninstalled": installed, "owned_app_pid_gone": True,
                "owned_host_process_groups_gone": True, "owned_adb_mappings_removed": True,
                "endpoint_descriptors_removed": True, "errors": cleanup_errors,
            }, indent=2) + "\n")

        cleanup(verify_cleanup)
        if cleanup_errors:
            raise RuntimeError("Cleanup incomplete: " + "; ".join(cleanup_errors))


if __name__ == "__main__":
    sys.exit(main())
