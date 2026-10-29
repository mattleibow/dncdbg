"""Internal independent debugger and explicit broker-required UI proofs for the same mobile app."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import socket
import struct
import sys
import time
import uuid

from mobile_tools import ROOT, adb_path
from process_tools import run_owned
from run_device import verify_ui, ios_ui_port, Ui, copy_debug_assets
from test_dap import Dap, remote_launch

APP = "com.dncdbg.mobiletests"
LEASE_ERROR = "The DevFlow broker lease authority is temporarily unavailable."


def unavailable_broker_environment(output):
    directory = output / "unavailable-broker"
    directory.mkdir()
    trace = directory / "invoked.log"
    executable = directory / "maui"
    executable.write_text(f"#!{sys.executable}\nimport json,os,sys\n"
                          "with open(os.environ['DNCDBG_BROKER_FAILURE_LOG'],'a') as log: log.write('invoked\\n')\n"
                          f"print(json.dumps({{'exit_code': 1, 'output': {LEASE_ERROR!r}, 'retryable': False}}),flush=True)\n"
                          "sys.exit(1)\n")
    executable.chmod(0o700)
    return dict(os.environ) | {"PATH": str(directory) + os.pathsep + os.environ["PATH"],
                               "DNCDBG_BROKER_FAILURE_LOG": str(trace)}, trace


class FixtureClient:
    def __init__(self, endpoint):
        self.endpoint = endpoint

    def command(self, operation):
        endpoint = self.endpoint
        request = json.dumps({"Token": endpoint["Token"], "SessionId": endpoint["SessionId"],
                              "Command": operation}).encode()
        with socket.create_connection(("127.0.0.1", endpoint["Port"]), timeout=30) as peer:
            peer.sendall(struct.pack("<I", len(request)) + request)
            stream = peer.makefile("rb")
            header = stream.read(4)
            assert len(header) == 4, "Fixture control closed without a response"
            length = struct.unpack("<I", header)[0]
            assert 0 < length <= 4096, "Unbounded fixture control response"
            response = json.loads(stream.read(length))
        assert response["Success"] and response["State"] is not None, response.get("Error")
        return response["State"]


class FixtureApp:
    def __init__(self, args):
        self.args = args
        self.serial = [adb_path(), "-s", args.device] if args.platform == "android" else None
        self.forwards = []
        self.reverse = False
        self.installed = False
        self.container = None

    def run(self, *command, check=True, input=None, timeout=60, log=None, env=None):
        return run_owned(*command, check=check, input=input, timeout=timeout, cwd=ROOT, env=env or os.environ,
                         log=self.args.output / log if log else None)

    def device(self, *command, **kwargs):
        return self.run(*self.serial, *command, **kwargs)

    def write(self, name, value):
        if self.serial:
            self.device("shell", "-T", "run-as", APP, "tee", "files/" + name, input=value)
        else:
            (self.container / "Library" / name).write_text(value)

    def read(self, name):
        if self.serial:
            return self.device("shell", "-T", "run-as", APP, "cat", "files/" + name, check=False).stdout
        return (self.container / "Library" / name).read_text()

    def prepare(self, debugger_only):
        self.installed = True
        if self.serial:
            self.device("install", "-r", str(self.args.app))
            self.device("shell", "am", "force-stop", APP)
            self.device("shell", "run-as", APP, "mkdir", "-p", "files")
        else:
            self.run("xcrun", "simctl", "install", self.args.device, str(self.args.app))
            self.container = Path(self.run("xcrun", "simctl", "get_app_container", self.args.device, APP, "data").stdout.strip())
        if debugger_only:
            self.write("dncdbg-fixture-mode", "debugger")
        elif self.serial and "tcp:19223 tcp:19223" not in self.device("reverse", "--list").stdout:
            self.device("reverse", "tcp:19223", "tcp:19223")
            self.reverse = True

    def launch(self, generation):
        self.write("dncdbg-session-id", generation)
        if self.serial:
            activity = self.device("shell", "cmd", "package", "resolve-activity", "--brief", APP).stdout.splitlines()[-1]
            self.device("shell", "am", "start", "-W", "-n", activity)
        else:
            self.run("xcrun", "simctl", "launch", self.args.device, APP)

    def descriptor(self, name, generation, pid=None):
        if not self.serial:
            self.container = Path(self.run("xcrun", "simctl", "get_app_container", self.args.device, APP, "data").stdout.strip())
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            try:
                candidate = json.loads(self.read(name))
                if candidate["SessionId"] == generation and (pid is None or candidate["ProcessId"] == pid):
                    return candidate
            except (FileNotFoundError, json.JSONDecodeError):
                pass
            time.sleep(0.1)
        raise TimeoutError("The exact generation did not publish " + name)

    def forward(self, endpoint):
        result = dict(endpoint)
        if self.serial:
            port = int(self.device("forward", "--no-rebind", "tcp:0", f"tcp:{endpoint['Port']}").stdout.strip())
            self.forwards.append(port)
            result["Port"] = port
        return result

    def native_cleanup(self):
        deadline = time.monotonic() + 10
        native = self.read("dncdbg-startup.jsonl")
        while '"Phase":"owner-finished"' not in native and time.monotonic() < deadline:
            time.sleep(0.1)
            native = self.read("dncdbg-startup.jsonl")
        (self.args.output / "native-report.jsonl").write_text(native)
        phases = [json.loads(row) for row in native.splitlines()]
        for phase in ("cleanup-detach-return", "terminate-return", "owner-finished"):
            matches = [p for p in phases if p["Phase"] == phase]
            assert len(matches) == 1 and matches[0]["HResult"] == "0x00000000", phase

    def close(self):
        failures = []
        def cleanup(*command, allow_failure=False):
            try: self.run(*command, check=not allow_failure)
            except Exception as error: failures.append(str(error))
        if self.installed:
            if self.serial:
                cleanup(*self.serial, "shell", "am", "force-stop", APP)
                cleanup(*self.serial, "uninstall", APP)
                result = self.device("shell", "pm", "path", APP, check=False)
                if "package:" in result.stdout: failures.append("Owned fixture remains installed")
            else:
                cleanup("xcrun", "simctl", "terminate", self.args.device, APP, allow_failure=True)
                cleanup("xcrun", "simctl", "uninstall", self.args.device, APP)
                if self.container and self.container.exists(): failures.append("Owned app data remains after uninstall")
        for port in self.forwards:
            cleanup(*self.serial, "forward", "--remove", f"tcp:{port}")
        if self.serial:
            mappings = self.device("forward", "--list").stdout
            if any(f" tcp:{port} " in " " + mappings.replace("\n", " ") + " " for port in self.forwards):
                failures.append("Owned debugger/control forwarding remains")
        if self.reverse: cleanup(*self.serial, "reverse", "--remove", "tcp:19223")
        for name in ("endpoint.json", "fixture.json"):
            (self.args.output / "assets" / name).unlink(missing_ok=True)
        if failures: raise RuntimeError("Independent fixture cleanup failed: " + "; ".join(failures))


def state(count, result):
    return {"Count": count, "Counter": f"Count: {count}", "Result": "Result: " + result}


def dap_proof(app, args, assets, environment):
    source = ROOT / "tests/DncDbg.MauiTestApp/MainPage.xaml.cs"
    line = next(i + 1 for i, row in enumerate(source.read_text().splitlines()) if "// MOBILE_BREAKPOINT:" in row)
    dap = Dap(environment, timeout=120)
    pool = ThreadPoolExecutor(max_workers=1)
    try:
        dap.request("initialize", {"adapterID": "dncdbg"})
        launch = dap.send("launch", remote_launch({
            "platform": "android" if app.serial else "iosSimulator", "deviceId": args.device,
            "applicationId": APP, "appPath": str(args.app), "assetsPath": str(assets),
            "runtimeIdentifier": args.runtime_identifier, "replaceExisting": True, "uninstallOnDisconnect": False,
            "startupTimeoutSeconds": 90, "outputPath": str(args.output / "targets")}))
        breakpoint = dap.request("setBreakpoints", {"source": {"path": str(source)}, "breakpoints": [{"line": line}]})["breakpoints"][0]
        dap.request("configurationDone")
        dap.response(launch)
        info = dap.request("dotnet/info")
        endpoint = app.forward(app.descriptor("dncdbg-fixture-control.json", info["sessionId"], info["debuggeeProcessId"]))
        assert endpoint["BrokerRequired"] is False, "Independent DAP fixture must not register DevFlow"
        control = FixtureClient(endpoint)
        assert control.command("reset") == state(0, "not run")
        running = pool.submit(control.command, "run")
        stopped = dap.event("stopped")
        assert stopped["reason"] == "breakpoint" and stopped["hitBreakpointIds"] == [breakpoint["id"]]
        frame = dap.request("stackTrace", {"threadId": stopped["threadId"]})["stackFrames"][0]
        assert frame["line"] == line and frame["source"]["path"] == str(source)
        scope = dap.request("scopes", {"frameId": frame["id"]})["scopes"][0]["variablesReference"]
        values = {v["name"]: v for v in dap.request("variables", {"variablesReference": scope})["variables"]}
        assert values["input"]["value"] == "1" and values["answer"]["value"] == "42"
        assert dap.request("evaluate", {"frameId": frame["id"], "expression": "answer + 1"})["result"] == "43"
        dap.request("continue", {"threadId": stopped["threadId"]})
        assert running.result(timeout=30) == state(1, "42")
        dap.request("scopes", {"frameId": frame["id"]}, success=False)
        dap.request("disconnect", {"terminateDebuggee": False})
        dap.event("terminated")
        assert control.command("reset") == state(0, "not run")
        assert control.command("run") == state(1, "42")
        app.native_cleanup()
        return info["sessionId"]
    finally:
        dap.close()
        pool.shutdown(wait=True, cancel_futures=True)
        (args.output / "dap.json").write_text(json.dumps(dap.transcript, indent=2))


def ui_proof(app, args, manifest):
    broker = json.loads(app.run("maui", "devflow", "broker", "status").stdout)
    assert broker["running"], "DevFlow-only proof requires the real broker"
    if app.serial:
        port = app.forward({"Port": args.ui_port})["Port"]
    else:
        registration = json.loads(app.run("maui", "devflow", "--device", args.device, "wait", "--wait-platform",
                                         "iOS", "--timeout", "30", timeout=45, log="devflow-wait.log").stdout)
        port = ios_ui_port(registration, manifest["Process"]["Id"], args.ui_session)
    verify_ui(port, args.platform, manifest["Process"]["Id"])
    ui = Ui(port, args.output)
    try:
        for command in ("MAUI tree --depth 15", "MAUI tap --automationId reset-scenario",
                        'MAUI assert --automationId counter-status Text "Count: 0"',
                        "MAUI tap --automationId run-scenario", 'MAUI assert --automationId result-status Text "Result: 42"',
                        "MAUI tap --automationId reset-scenario", 'MAUI assert --automationId counter-status Text "Count: 0"',
                        "MAUI tap --automationId run-scenario", 'MAUI assert --automationId result-status Text "Result: 42"'):
            ui.command(command)
        ui.command("MAUI screenshot --output " + json.dumps(str(args.output / "ui.png")) + " --overwrite")
    finally:
        ui.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", required=True, choices=["ios", "android"])
    parser.add_argument("--device", required=True)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--proof", choices=["client", "dap", "ui"], default="client")
    parser.add_argument("--disconnect-stopped", action="store_true")
    parser.add_argument("--ui-session")
    parser.add_argument("--ui-port", type=int)
    parser.add_argument("--runtime-identifier", choices=["iossimulator-arm64", "android-arm64", "android-x64"])
    args = parser.parse_args()
    if args.proof == "ui" and (not args.ui_session or args.ui_port is None or not 0 < args.ui_port <= 65535):
        parser.error("The DevFlow-only proof requires its explicit run identity and configured agent port.")
    args.app, args.output = args.app.resolve(), args.output.resolve()
    args.output.mkdir(parents=True)
    if args.runtime_identifier is None:
        args.runtime_identifier = "iossimulator-arm64" if args.platform == "ios" else "android-arm64"
    assets = copy_debug_assets(args.assets, args.output / "assets")
    app = FixtureApp(args)
    environment, trace = unavailable_broker_environment(args.output) if args.proof != "ui" else (None, None)
    if environment is not None and app.serial:
        environment["PATH"] = str(Path(adb_path()).parent) + os.pathsep + environment["PATH"]
    generation = uuid.uuid4().hex
    completed, cleaned, failure = False, False, None
    try:
        app.prepare(debugger_only=args.proof != "ui")
        if args.proof == "dap":
            generation = dap_proof(app, args, assets, environment)
        else:
            app.launch(generation)
            manifest = app.descriptor("dncdbg-remote.json", generation)
            assert manifest["Process"]["OwnershipId"] == generation
            assert manifest["Profile"]["Agent"] == json.loads((assets / "dncdbg-agent.json").read_text())
            if args.proof == "ui":
                ui_proof(app, args, manifest)
            else:
                endpoint = app.forward(manifest["Endpoint"])
                fixture = app.forward(app.descriptor("dncdbg-fixture-control.json", generation, manifest["Process"]["Id"]))
                assert fixture["BrokerRequired"] is False, "Independent direct fixture must not register DevFlow"
                for name, value in (("endpoint.json", endpoint), ("fixture.json", fixture)):
                    path = assets / name
                    path.write_text(json.dumps(value))
                    path.chmod(0o600)
                app.run("dotnet", "tests/DncDbg.RemoteTests/bin/Release/net10.0/DncDbg.RemoteTests.dll",
                        "--mobile-proof", str(assets / "endpoint.json"), str(ROOT / "tests/DncDbg.MauiTestApp/MainPage.xaml.cs"),
                        str(assets / "DncDbg.MauiTestApp.dll"), str(assets / "DncDbg.MauiTestApp.pdb"),
                        str(assets / "fixture.json"), "disconnect" if args.disconnect_stopped else "continue",
                        timeout=180, log="direct-proof.log", env=environment)
                app.native_cleanup()
        if trace is not None: assert not trace.exists(), "Independent debugger proof invoked the unavailable broker"
        completed = True
    except BaseException as error:
        failure = repr(error)
        raise
    finally:
        try:
            app.close()
            cleaned = True
        except BaseException as error:
            failure = (failure or "") + "; cleanup: " + repr(error)
            raise
        finally:
            (args.output / "report.json").write_text(json.dumps({"passed": completed and cleaned and failure is None,
                "generation": generation, "proof": args.proof, "brokerRequired": args.proof == "ui",
                "brokerUnavailable": args.proof != "ui", "cleanup": cleaned, "error": failure}))


if __name__ == "__main__":
    main()
