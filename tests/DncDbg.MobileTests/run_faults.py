#!/usr/bin/env python3
"""Real-device DAP acquisition races, abrupt process death and opt-in native watchdog."""
import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time

from run_device import Ui
from test_dap import Dap, ROOT

APP = "com.dncdbg.mobiletests"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", required=True, choices=["android", "iosSimulator"])
    parser.add_argument("--device", required=True)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    adb = str(Path.home() / "Library/Android/sdk/platform-tools/adb")
    xcrun = shutil.which("xcrun")
    base_env = {**os.environ, "PATH": str(Path(adb).parent) + os.pathsep + os.environ["PATH"]}
    options = {
        "platform": args.platform, "deviceId": args.device, "applicationId": APP,
        "appPath": str(args.app.resolve()), "assetsPath": str(args.assets.resolve()),
        "runtimeIdentifier": "android-arm64" if args.platform == "android" else "iossimulator-arm64",
        "startupTimeoutSeconds": 90, "replaceExisting": False, "uninstallOnDisconnect": False,
    }

    def command(*arguments, check=True):
        return subprocess.run(arguments, text=True, capture_output=True, check=check, timeout=45)

    def device(*arguments, check=True):
        return command(adb, "-s", args.device, *arguments, check=check)

    def forwards():
        return {row for row in command(adb, "forward", "--list").stdout.splitlines()
                if row.startswith(args.device + " ")} if args.platform == "android" else set()

    def container():
        return Path(command(xcrun, "simctl", "get_app_container", args.device, APP, "data").stdout.strip())

    def cleanup():
        if args.platform == "android":
            device("shell", "am", "force-stop", APP, check=False)
            device("uninstall", APP, check=False)
        else:
            command(xcrun, "simctl", "terminate", args.device, APP, check=False)
            command(xcrun, "simctl", "uninstall", args.device, APP, check=False)

    def launch(dap, uninstall=False, start=True):
        pending = dap.send("launch", {"mobile": options | {"uninstallOnDisconnect": uninstall}})
        if start:
            dap.request("setExceptionBreakpoints", {"filters": []})
            dap.request("configurationDone")
            dap.response(pending)
        return pending

    reports = []
    for scenario in ("terminate-install", "terminate-redeploy", "crash-running", "crash-stopped", "watchdog"):
        output = args.output / scenario
        output.mkdir(exist_ok=True)
        checks = []
        report = {"scenario": scenario, "platform": args.platform, "passed": False, "checks": checks,
                  "head": command("git", "rev-parse", "HEAD").stdout.strip()}
        baseline = forwards()
        env = {**base_env, "DNCDBG_LOG_DIR": str(output)}
        dap, ui, ui_port = None, None, None

        def verify(condition, name):
            assert condition, name
            checks.append(name)
            print("PASS:", scenario, name, flush=True)

        try:
            if scenario.startswith("terminate-"):
                tools = output / "tools"
                tools.mkdir(exist_ok=True)
                tool = tools / ("adb" if args.platform == "android" else "xcrun")
                tool.write_text("#!" + sys.executable + "\nimport runpy\nrunpy.run_path(" +
                                repr(str(Path(__file__).with_name("device_gate.py").resolve())) + ", run_name='__main__')\n")
                tool.chmod(0o755)
                env |= {"PATH": str(tools) + os.pathsep + base_env["PATH"], "DNCDBG_INSTALL_GATE": str(output),
                        "DNCDBG_INSTALL_GATE_TOOL": adb if args.platform == "android" else xcrun,
                        "DNCDBG_INSTALL_GATE_NUMBER": "2" if scenario == "terminate-redeploy" else "1"}
            dap = Dap(env, timeout=120)
            dap.request("initialize", {"adapterID": "dncdbg"})
            if scenario.startswith("terminate-"):
                if scenario == "terminate-redeploy":
                    launch(dap, uninstall=True)
                    dap.event("process")
                    pending = dap.send("restart")
                else:
                    pending = launch(dap, uninstall=True, start=False)
                deadline = time.monotonic() + 90
                while not (output / "install-blocked").exists():
                    if time.monotonic() >= deadline:
                        raise TimeoutError("The real install did not reach its barrier.")
                    time.sleep(0.02)
                before = len((output / "commands.jsonl").read_text().splitlines())
                started = time.monotonic()
                dap.request("terminate")
                (output / "release-install").touch()
                dap.event("terminated")
                dap.response(pending, success=False)
                verify(time.monotonic() - started < 15, "termination settles deployment within 15 seconds")
                dap.close()
                later = [json.loads(row) for row in (output / "commands.jsonl").read_text().splitlines()[before:]]
                verify(not any("launch" in row or ("start" in row and "--es" in row) for row in later),
                       "no app launch after interrupted real installation")
                verify(not any("forward" in row and "--remove" not in row for row in later),
                       "no debugger forward/client acquisition after closure")
                while not dap.messages.empty():
                    dap.history.append(dap.messages.get_nowait())
                verify(not any(m.get("event") == "process" for m in dap.history), "no late DAP process event")
            else:
                pending = launch(dap, start=False)
                source = ROOT / "tests/DncDbg.MauiTestApp/MainPage.xaml.cs"
                if scenario == "crash-stopped":
                    line = next(i + 1 for i, row in enumerate(source.read_text().splitlines()) if "// MOBILE_BREAKPOINT:" in row)
                    dap.request("setBreakpoints", {"source": {"path": str(source)}, "breakpoints": [{"line": line}]})
                dap.request("setExceptionBreakpoints", {"filters": []})
                dap.request("configurationDone")
                dap.response(pending)
                pid = dap.event("process")["systemProcessId"]
                wait = command("maui", "devflow", "--device", args.device, "wait", "--wait-platform",
                               "Android" if args.platform == "android" else "iOS", "--timeout", "20", check=False)
                (output / "devflow-wait.json").write_text(wait.stdout + wait.stderr)
                if args.platform == "android":
                    ui_port = int(device("forward", "--no-rebind", "tcp:0", "tcp:9223").stdout)
                else:
                    ui_port = json.loads(wait.stdout)["port"]
                ui = Ui(ui_port, output)
                ui.command("MAUI tree --depth 15")
                if scenario == "crash-stopped":
                    ui.send("MAUI tap --automationId run-scenario")
                    stopped = dap.event("stopped")
                    verify(stopped["reason"] == "breakpoint", "crash fixture owns an outstanding native callback stop")
                started = time.monotonic()
                if scenario == "watchdog":
                    ui.command("MAUI tap --automationId watchdog-scenario")
                elif args.platform == "android":
                    device("shell", "run-as", APP, "kill", "-ABRT", str(pid))
                else:
                    os.kill(pid, signal.SIGABRT)
                dap.event("terminated")
                verify(time.monotonic() - started < 35, "abrupt target death releases DAP within 35 seconds")
                if args.platform == "android":
                    verify(not device("shell", "pidof", APP, check=False).stdout.strip(), "owned app process is gone")
                    descriptor = device("exec-out", "run-as", APP, "cat", "files/dncdbg-remote.json", check=False)
                    text = device("exec-out", "run-as", APP, "cat", "files/dncdbg-private-startup.jsonl", check=False).stdout
                    verify(not descriptor.stdout.strip(), "owned endpoint token/descriptor removed")
                else:
                    try:
                        os.kill(pid, 0)
                    except ProcessLookupError:
                        pass
                    else:
                        raise AssertionError("Owned simulator process remains alive.")
                    verify(not (container() / "Library/dncdbg-remote.json").exists(), "owned endpoint token/descriptor removed")
                    text = (container() / "Library/dncdbg-private-startup.jsonl").read_text()
                (output / "native-report.jsonl").write_text(text)
                if scenario == "watchdog":
                    phases = [json.loads(row)["Phase"] for row in text.splitlines()]
                    verify("test-owner-stall" in phases and "native-call-or-cleanup-timeout" in phases,
                           "real native watchdog exits the stalled target (86), not a host timeout")
                if ui_port and args.platform == "android":
                    device("forward", "--remove", f"tcp:{ui_port}")
                    ui_port = None
            verify(forwards() == baseline, "all owned debugger/UI mappings removed without touching other mappings")
            report["passed"] = True
        except Exception as error:
            report["error"] = repr(error)
            raise
        finally:
            if ui:
                ui.close()
            if dap:
                dap.close()
                (output / "adapter-stderr.log").write_text(dap.stderr)
                report["dapMessages"] = dap.history
            cleanup()
            if ui_port and args.platform == "android":
                device("forward", "--remove", f"tcp:{ui_port}", check=False)
            report["remainingForwards"] = sorted(forwards() - baseline)
            (output / "report.json").write_text(json.dumps(report, indent=2))
            reports.append(report)
            (args.output / "report.json").write_text(json.dumps(reports, indent=2))


if __name__ == "__main__":
    main()
