#!/usr/bin/env python3
"""Simulator/emulator-only evidence runner. Exit 2 means the mobile debugging gate is blocked."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import zipfile


ROOT = Path(__file__).resolve().parents[2]
APP = "com.dncdbg.mobiletests"
PROJECT = "tests/DncDbg.MauiTestApp"
EVIDENCE = ROOT / "build/validation/mobile/net11-default"


def run(*args, env=None, check=True, input=None, timeout=180, log=None, cwd=ROOT):
    result = subprocess.run(args, cwd=cwd, env=env, input=input, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout)
    if log:
        (EVIDENCE / log).write_text(result.stdout + result.stderr)
    if check and result.returncode:
        raise RuntimeError(f"{args[0]} failed ({result.returncode}): {result.stderr[-2000:]}")
    return result


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
    args = parser.parse_args()
    if args.platform == "android" and not args.device.startswith("emulator-"):
        parser.error("Physical Android devices are intentionally not supported")
    if args.public_attach_probe and args.platform != "ios":
        parser.error("The host public attach probe only applies to an iOS simulator process, not an emulator PID")
    EVIDENCE = ROOT / f"build/validation/mobile/net11-{args.runtime}"
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    prefix = args.platform
    adb = str(Path(os.environ.get("ANDROID_HOME", str(Path.home() / "Library/Android/sdk"))) / "platform-tools/adb")
    forwards = []
    added_reverse = False
    installed = False
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

        env = os.environ.copy()
        if args.build_developer_dir:
            env["DEVELOPER_DIR"] = args.build_developer_dir
        tfm = f"net11.0-{prefix}"
        rid = "iossimulator-arm64" if prefix == "ios" else "android-arm64"
        properties = [f"-p:RuntimeIdentifier={rid}", "-p:EnableExperimentalRemoteDebugging=true"]
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
        installed = True
        if prefix == "ios":
            run("xcrun", "simctl", "install", args.device, str(output / "DncDbg.MauiTestApp.app"))
            run("xcrun", "simctl", "launch", f"--stdout={EVIDENCE / 'ios-app-stdout.log'}",
                f"--stderr={EVIDENCE / 'ios-app-stderr.log'}", args.device, APP)
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
        capability_result = run("dotnet", "run", "--project", "tests/DncDbg.RemoteTests", "--", "--mobile", str(forwarded_file),
                                log=f"{prefix}-remote-capability.log")
        capabilities = json.loads(next(line for line in capability_result.stdout.splitlines() if line.startswith("{")))
        if not capabilities["Runtime"].startswith(".NET 11."):
            raise RuntimeError("The installed application did not report a .NET 11 runtime")
        expected_kind = "Mono" if inspected["UseMonoRuntime"] == "true" else "CoreClr"
        if not capabilities["Runtime"].endswith(f"({expected_kind})"):
            raise RuntimeError("Runtime declaration differs from the measured SDK configuration")
        public_attach = None
        if args.public_attach_probe:
            probe = run("dotnet", "run", "--project", "tests/DncDbg.RemoteTests", "--", "--attach-probe",
                        str(status["app"]["processId"]), check=False, timeout=60, log="ios-public-attach-probe.log")
            public_attach = json.loads(next(line for line in probe.stdout.splitlines() if line.startswith("{")))
            if probe.returncode not in (0, 2):
                raise RuntimeError("Public attach probe failed without a structured result")
        report = {"platform": prefix, "ui": "passed", "broker_wait": broker_ok,
                  "sdk": sdk_version, "workload_set": workload_version, "runtime_selection": args.runtime,
                  "properties": inspected, "runtime_packs": runtime_packs, "native_assets": native_assets,
                  "capabilities": capabilities, "public_attach_probe": public_attach,
                  "mobile_managed_breakpoint": "blocked", "physical_devices": "not tested"}
        (EVIDENCE / f"{prefix}-report.json").write_text(json.dumps(report, indent=2) + "\n")
        print(json.dumps(report))
        return 2
    finally:
        cleanup_errors = []

        def cleanup(action):
            try:
                action()
            except Exception as error:
                cleanup_errors.append(str(error))

        if installed:
            if prefix == "ios":
                cleanup(lambda: run("xcrun", "simctl", "terminate", args.device, APP, check=False))
                cleanup(lambda: run("xcrun", "simctl", "uninstall", args.device, APP))
            else:
                cleanup(lambda: device("shell", "am", "force-stop", APP))
                cleanup(lambda: device("uninstall", APP))
        for port in forwards:
            cleanup(lambda port=port: device("forward", "--remove", f"tcp:{port}"))
        if added_reverse:
            cleanup(lambda: device("reverse", "--remove", "tcp:19223"))
        for path in [endpoint_file, forwarded_file]:
            cleanup(lambda path=path: path.unlink(missing_ok=True))
        if cleanup_errors:
            raise RuntimeError("Cleanup incomplete: " + "; ".join(cleanup_errors))


if __name__ == "__main__":
    sys.exit(main())
