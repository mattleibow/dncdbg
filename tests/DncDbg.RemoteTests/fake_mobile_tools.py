"""Fixture-only device/package CLI behavior, without importing the DAP test driver."""
import json
import os
from pathlib import Path
import shutil
import sys
import time


def tool():
    state = Path(os.environ["DNCDBG_FAKE_DEVICE"])
    args = sys.argv[1:]
    with (state / "commands.jsonl").open("a") as log:
        log.write(json.dumps([os.environ["DNCDBG_FAKE_TOOL"], *args]) + "\n")
    name = os.environ["DNCDBG_FAKE_TOOL"]
    if name == "aapt2":
        print("package: name='com.dncdbg.mobiletests' versionCode='1' versionName='1.0'")
        return
    if name == "java":
        if (state / "bad-signature").exists():
            sys.exit(1)
        print("Signer #1 certificate SHA-256 digest: " + "e" * 64)
        return
    if name == "plutil":
        print({"CFBundleExecutable": "DncDbg.MauiTestApp", "CFBundleIdentifier": "com.dncdbg.mobiletests",
               "CFBundleVersion": "1", "CFBundleShortVersionString": "1.0"}[args[1]])
        return
    if name == "codesign":
        if "-d" in args:
            print("CDHash=" + "f" * 40, file=sys.stderr)
        return
    if "forward" in args and (state / "daemon-ready.json").exists():
        if "tcp:0" in args:
            (state / "forward-cli-started").touch()
        import runpy
        runpy.run_path(str(Path(__file__).with_name("fake_adb.py")), run_name="__client__")
        return
    if "install" in args:
        if (state / "block-install").exists():
            (state / "install-blocked").touch()
            while not (state / "release-install").exists():
                time.sleep(0.01)
        (state / "installed").touch()
        if name == "adb":
            shutil.copyfile(args[-1], state / "installed.apk")
            if (state / "tamper-installed").exists():
                with (state / "installed.apk").open("ab") as package:
                    package.write(b"installed bytes differ despite matching claimed build ID")
        else:
            shutil.copytree(args[-1], state / "Installed.app", dirs_exist_ok=True)
            if (state / "tamper-installed").exists():
                (state / "Installed.app/DncDbg.MauiTestApp").write_bytes(b"installed linked image mismatch")
    if "uninstall" in args:
        (state / "installed").unlink(missing_ok=True)
        (state / "installed.apk").unlink(missing_ok=True)
        shutil.rmtree(state / "Installed.app", ignore_errors=True)
    if "pull" in args:
        shutil.copyfile(state / "installed.apk", args[-1])
        return
    if "tee" in args:
        (state / Path(args[-1]).name).write_text(sys.stdin.read())
    if "launch" in args or ("start" in args and "--es" in args):
        endpoint = json.loads((state / "endpoint.json").read_text())
        endpoint["SessionId"] = os.environ.get("SIMCTL_CHILD_DNCDBG_SESSION_ID") or args[-1]
        endpoint["Endpoint"]["SessionId"] = endpoint["SessionId"]
        endpoint["Process"]["OwnershipId"] = endpoint["SessionId"]
        (state / "endpoint.json").write_text(json.dumps(endpoint))
        if (state / "delay-descriptor").exists():
            (state / "launch-awaiting-descriptor").touch()
        else:
            (state / "Library/dncdbg-remote.json").write_text(json.dumps(endpoint))
    if "forward" in args and "--remove" not in args:
        print(json.loads((state / "endpoint.json").read_text())["Endpoint"]["Port"])
    elif "cat" in args:
        path = state / ("endpoint.json" if args[-1].endswith("dncdbg-remote.json") else Path(args[-1]).name)
        if path.exists():
            print(path.read_text())
        elif "exec-out" in args:
            print("cat: " + args[-1] + ": No such file or directory")
    elif "path" in args and (state / "installed").exists():
        print("package:/data/app/com.dncdbg.mobiletests/base.apk")
    elif "get_app_container" in args:
        if (state / "installed").exists():
            print(state / "Installed.app" if args[-1] == "app" else state)
    elif "list" in args:
        print(json.dumps({"devices": {"test": [{"udid": "test-simulator", "state": "Booted"}]}}))
    elif "resolve-activity" in args:
        print("com.dncdbg.mobiletests/.MainActivity")
    elif "getprop" in args:
        print("1")
    elif "launch" in args:
        print("com.dncdbg.mobiletests: 123")


if __name__ == "__main__":
    tool()
