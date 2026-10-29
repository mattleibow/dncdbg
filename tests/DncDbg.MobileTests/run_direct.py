"""Internal direct-client agent compatibility proof, owned by the mobile suite."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid

from mobile_tools import ROOT, adb_path
from process_tools import run_owned
from run_device import verify_ui

APP = "com.dncdbg.mobiletests"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", required=True, choices=["ios", "android"])
    parser.add_argument("--device", required=True)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--disconnect-stopped", action="store_true")
    parser.add_argument("--ui-session", required=True)
    parser.add_argument("--ui-port", type=int, default=9223)
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.output.mkdir(parents=True)
    private = args.output / "assets"
    private.mkdir()
    serial = ["adb", "-s", args.device]
    if args.platform == "android":
        serial[0] = adb_path()
    forwards = []
    reverse = False
    installed = False
    container = None
    generation = uuid.uuid4().hex

    def run(*command, check=True, input=None, timeout=60, log=None):
        return run_owned(*command, check=check, input=input, timeout=timeout, cwd=ROOT, env=os.environ,
                         log=args.output / log if log else None)

    def device(*command, **kwargs):
        return run(*serial, *command, **kwargs)

    def forward(port):
        local = int(device("forward", "--no-rebind", "tcp:0", f"tcp:{port}").stdout.strip())
        forwards.append(local)
        return local

    try:
        installed = True
        if args.platform == "android":
            device("install", "-r", str(args.app))
            device("shell", "am", "force-stop", APP)
            device("shell", "run-as", APP, "mkdir", "-p", "files")
            device("shell", "-T", "run-as", APP, "tee", "files/dncdbg-session-id", input=generation)
            if "tcp:19223 tcp:19223" not in device("reverse", "--list").stdout:
                device("reverse", "tcp:19223", "tcp:19223")
                reverse = True
            activity = device("shell", "cmd", "package", "resolve-activity", "--brief", APP).stdout.splitlines()[-1]
            device("shell", "am", "start", "-W", "-n", activity)
        else:
            run("xcrun", "simctl", "install", args.device, str(args.app))
            container = Path(run("xcrun", "simctl", "get_app_container", args.device, APP, "data").stdout.strip())
            (container / "Library/dncdbg-session-id").write_text(generation)
            run("xcrun", "simctl", "launch", args.device, APP)
        deadline = time.monotonic() + 60
        manifest = None
        while time.monotonic() < deadline:
            try:
                text = (device("exec-out", "run-as", APP, "cat", "files/dncdbg-remote.json", check=False).stdout
                        if args.platform == "android" else (container / "Library/dncdbg-remote.json").read_text())
                candidate = json.loads(text)
                if candidate["SessionId"] == generation:
                    manifest = candidate
                    break
            except (FileNotFoundError, json.JSONDecodeError):
                pass
            time.sleep(0.1)
        assert manifest and manifest["Process"]["OwnershipId"] == generation, "Owned manifest was not published"
        expected = json.loads((args.assets / "dncdbg-agent.json").read_text())
        assert manifest["Profile"]["Agent"] == expected, "Wrong target agent package"
        endpoint = manifest["Endpoint"]
        if args.platform == "android":
            endpoint["Port"] = forward(endpoint["Port"])
        endpoint_path = private / "endpoint.json"
        endpoint_path.write_text(json.dumps(endpoint))
        endpoint_path.chmod(0o600)
        wait = run("maui", "devflow", "--device", args.device, "wait", "--wait-platform",
                   "iOS" if args.platform == "ios" else "Android", "--timeout", "30",
                   check=False, timeout=45, log="devflow-wait.log")
        registration = json.loads(wait.stdout) if wait.returncode == 0 else None
        if registration:
            assert registration["platform"] == ("iOS" if args.platform == "ios" else "Android")
            assert registration["processId"] == manifest["Process"]["Id"]
            assert registration["sessionId"] == args.ui_session
        ui_port = registration["port"] if args.platform == "ios" and registration else args.ui_port
        assert registration or args.platform == "android", "No owned iOS UI registration"
        if args.platform == "android":
            ui_port = forward(ui_port)
        verify_ui(ui_port, args.platform, manifest["Process"]["Id"])
        run("dotnet", "tests/DncDbg.RemoteTests/bin/Release/net10.0/DncDbg.RemoteTests.dll",
            "--mobile-proof", str(endpoint_path), str(ROOT / "tests/DncDbg.MauiTestApp/MainPage.xaml.cs"),
            str(args.assets / "DncDbg.MauiTestApp.dll"), str(args.assets / "DncDbg.MauiTestApp.pdb"),
            str(ui_port), "disconnect" if args.disconnect_stopped else "continue",
            timeout=180, log="direct-proof.log")
        native = (device("exec-out", "run-as", APP, "cat", "files/dncdbg-startup.jsonl").stdout
                  if args.platform == "android" else (container / "Library/dncdbg-startup.jsonl").read_text())
        (args.output / "native-report.jsonl").write_text(native)
        phases = [json.loads(row) for row in native.splitlines()]
        for phase in ("cleanup-detach-return", "terminate-return", "owner-finished"):
            matches = [p for p in phases if p["Phase"] == phase]
            assert len(matches) == 1 and matches[0]["HResult"] == "0x00000000", phase
        (args.output / "report.json").write_text(json.dumps({"passed": True, "generation": generation,
            "mode": "disconnect" if args.disconnect_stopped else "continue"}, indent=2))
    finally:
        if installed:
            if args.platform == "android":
                device("shell", "am", "force-stop", APP, check=False)
                device("uninstall", APP, check=False)
            else:
                run("xcrun", "simctl", "terminate", args.device, APP, check=False)
                run("xcrun", "simctl", "uninstall", args.device, APP, check=False)
        for port in forwards:
            device("forward", "--remove", f"tcp:{port}", check=False)
        if reverse:
            device("reverse", "--remove", "tcp:19223", check=False)
        (private / "endpoint.json").unlink(missing_ok=True)


if __name__ == "__main__":
    main()
