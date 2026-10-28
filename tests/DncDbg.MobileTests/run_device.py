#!/usr/bin/env python3
"""Real mobile DAP gate. Deployment/debugger routing belong exclusively to dncdbg-dap; DevFlow is separate."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import queue
import shutil
import subprocess
import struct
import threading
import time

from mobile_tools import adb_path, tool_environment
from test_dap import Dap, ROOT


def copy_debug_assets(source, destination):
    destination.mkdir(parents=True, exist_ok=True)
    for name in ("DncDbg.MauiTestApp", "DncDbg.ReloadFixture"):
        for extension in ("dll", "pdb"):
            shutil.copyfile(source / f"{name}.{extension}", destination / f"{name}.{extension}")
    return destination


def ios_ui_port(registration, pid, session):
    assert registration["platform"] == "iOS", "Broker returned a different platform"
    assert registration["processId"] == pid, "Broker returned a different process"
    assert session and registration["sessionId"] == session, "Broker returned a different run identity"
    assert isinstance(registration["port"], int) and 0 < registration["port"] <= 65535, "Invalid broker UI port"
    return registration["port"]


def verify_ui(port, platform, pid):
    deadline = time.monotonic() + 30
    while True:
        result = subprocess.run(["maui", "devflow", "--agent-port", str(port), "agent", "status"],
                                cwd=ROOT, capture_output=True, text=True, timeout=15)
        if result.returncode == 0:
            status = json.loads(result.stdout)
            assert status["app"]["packageId"] == "com.dncdbg.mobiletests", "DevFlow port belongs to another application"
            assert status["app"]["processId"] == pid, "DevFlow selected a different app process"
            assert status["device"]["platform"].lower() == ("android" if platform == "android" else "ios"), "Wrong DevFlow platform"
            return status
        if time.monotonic() >= deadline:
            raise TimeoutError("The exact fixture DevFlow agent did not become reachable")
        time.sleep(0.1)


class Ui:
    def __init__(self, port, output):
        self.errors = (output / "devflow-stderr.log").open("a")
        self.process = subprocess.Popen(["maui", "devflow", "--agent-port", str(port), "batch"],
                                        cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=self.errors, text=True)
        self.responses = queue.Queue()
        self.log = (output / "devflow.jsonl").open("a")
        threading.Thread(target=self.read, daemon=True).start()

    def read(self):
        for line in self.process.stdout:
            self.responses.put(line)

    def send(self, command):
        self.process.stdin.write(command + "\n")
        self.process.stdin.flush()

    def receive(self):
        line = self.responses.get(timeout=30)
        self.log.write(line)
        self.log.flush()
        result = json.loads(line)
        assert result["exit_code"] == 0, result
        return result

    def command(self, command):
        self.send(command)
        return self.receive()

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.process.stdout.close()
        self.errors.close()
        self.log.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", required=True, choices=["android", "iosSimulator"])
    parser.add_argument("--device", required=True)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--ui-port", type=int, default=9223, help="UI-only fixture port, never the debugger endpoint")
    parser.add_argument("--runtime-identifier", choices=["iossimulator-arm64", "android-arm64", "android-x64"])
    parser.add_argument("--ui-session")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    assets = copy_debug_assets(args.assets, args.output / "assets")
    adb = adb_path() if args.platform == "android" else None
    env = tool_environment(args.platform) | {"DNCDBG_LOG_DIR": str(args.output.resolve())}
    dap = Dap(env, timeout=120)
    ui = None
    ui_port = None
    evidence = {"platform": args.platform, "device": args.device,
                "head": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
                "passed": False}
    checks = []

    def check(value, name):
        assert value, name
        checks.append(name)
        print("PASS:", name, flush=True)

    def device(*command, check=True):
        return subprocess.run([adb, "-s", args.device, *command], text=True, capture_output=True,
                              check=check, timeout=30)

    def stack():
        event = dap.event("stopped")
        frame = dap.request("stackTrace", {"threadId": event["threadId"]})["stackFrames"][0]
        return event, frame

    def native_report():
        if args.platform == "android":
            result = device("exec-out", "run-as", "com.dncdbg.mobiletests", "cat", "files/dncdbg-private-startup.jsonl", check=False)
            return result.stdout
        container = subprocess.run(["xcrun", "simctl", "get_app_container", args.device,
                                    "com.dncdbg.mobiletests", "data"], capture_output=True, text=True, timeout=15)
        path = Path(container.stdout.strip()) / "Library/dncdbg-private-startup.jsonl"
        return path.read_text() if container.returncode == 0 and path.exists() else ""

    source = ROOT / "tests/DncDbg.MauiTestApp/MainPage.xaml.cs"
    line = next(i + 1 for i, text in enumerate(source.read_text().splitlines()) if "// MOBILE_BREAKPOINT:" in text)
    deep_line = next(i + 1 for i, text in enumerate(source.read_text().splitlines()) if "// MOBILE_DEEP_BREAKPOINT:" in text)
    try:
        dap.request("initialize", {"adapterID": "dncdbg", "linesStartAt1": True, "columnsStartAt1": True})
        launch = dap.send("launch", {"mobile": {
            "platform": args.platform, "appPath": str(args.app.resolve()), "applicationId": "com.dncdbg.mobiletests",
            "deviceId": args.device, "assetsPath": str(assets.resolve()),
            "runtimeIdentifier": args.runtime_identifier or ("android-arm64" if args.platform == "android" else "iossimulator-arm64"),
            "replaceExisting": False, "uninstallOnDisconnect": False, "startupTimeoutSeconds": 90}})
        breakpoint = dap.request("setBreakpoints", {"source": {"path": str(source)},
                                                  "breakpoints": [{"line": line}, {"line": deep_line}]})["breakpoints"][0]
        reload_source = ROOT / "tests/DncDbg.ReloadFixture/ReloadScenario.cs"
        reload_line = next(i + 1 for i, text in enumerate(reload_source.read_text().splitlines()) if "// MOBILE_RELOAD_BREAKPOINT:" in text)
        reload_bp = dap.request("setBreakpoints", {"source": {"path": str(reload_source)},
                                                  "breakpoints": [{"line": reload_line}]})["breakpoints"][0]
        check(not reload_bp["verified"], "collectible source breakpoint is pending before any instance loads")
        dap.request("setExceptionBreakpoints", {"filters": []})
        dap.request("configurationDone")
        dap.response(launch)
        changed = dap.event("breakpoint")["breakpoint"]
        check(changed["id"] == breakpoint["id"] and changed["verified"] and changed["source"]["path"] == str(source),
              "DAP breakpoint identity survives asynchronous module binding")
        check(dap.event("breakpoint")["breakpoint"]["line"] == deep_line, "deep inspection breakpoint bound before execution")
        details = dap.request("dotnet/info")
        evidence["provider"] = details.get("remote", details.get("mobile"))
        wait = subprocess.run(["maui", "devflow", "--device", args.device, "wait", "--wait-platform",
                               "Android" if args.platform == "android" else "iOS", "--timeout", "20"],
                              cwd=ROOT, capture_output=True, text=True, timeout=30)
        (args.output / "devflow-wait.json").write_text(wait.stdout + wait.stderr)
        if args.platform == "android":
            # Separate UI-only route. Never reused by the debugger or counted as broker success.
            ui_port = int(device("forward", "--no-rebind", "tcp:0", "tcp:9223").stdout.strip())
            evidence["devflowBroker"] = wait.returncode == 0
        else:
            ui_port = ios_ui_port(json.loads(wait.stdout), details["debuggeeProcessId"], args.ui_session)
            evidence["devflowBroker"] = True
        evidence["uiIdentity"] = verify_ui(ui_port, args.platform, details["debuggeeProcessId"])
        ui = Ui(ui_port, args.output)
        ui.command("MAUI tree --depth 15")
        ui.command("MAUI tap --automationId reset-scenario")
        ui.send("MAUI tap --automationId run-scenario")
        event, frame = stack()
        check(event["reason"] == "breakpoint" and frame["name"].endswith("MainPage.ManagedScenario") and
              frame["source"]["path"] == str(source) and frame["line"] == line, "exact MOBILE_BREAKPOINT through dncdbg-dap")
        check(event.get("hitBreakpointIds") == [breakpoint["id"]], "native binding stop reports stable DAP source breakpoint ID")
        thread = event["threadId"]
        scope = dap.request("scopes", {"frameId": frame["id"]})["scopes"][0]["variablesReference"]
        variables = dap.request("variables", {"variablesReference": scope})["variables"]
        locals_by_name = {v["name"]: v for v in variables}
        check(locals_by_name["input"]["value"] == "1" and locals_by_name["answer"]["value"] == "42",
              "DAP locals/argument 1/42")
        check("mobile" in locals_by_name["message"]["value"] and "🚀" in locals_by_name["message"]["value"],
              "DAP UTF-16 string with surrogate pair")
        array = dap.request("variables", {"variablesReference": locals_by_name["numbers"]["variablesReference"],
                                          "start": 0, "count": 2})["variables"]
        check([v["value"] for v in array] == ["1", "42"], "DAP array children")
        for page in ({}, {"count": 0}):
            all_values = dap.request("variables", {"variablesReference": locals_by_name["numbers"]["variablesReference"]} | page)["variables"]
            check(len(all_values) == 150 and [int(v["value"]) for v in all_values] == [1, 42, *range(2, 150)],
                  "DAP 150-element array, " + ("omitted count" if not page else "count zero"))
        page = dap.request("variables", {"variablesReference": locals_by_name["numbers"]["variablesReference"],
                                         "start": 141, "count": 4})["variables"]
        check([int(v["value"]) for v in page] == list(range(141, 145)), "DAP explicit array page remains bounded")
        fields = dap.request("variables", {"variablesReference": locals_by_name["sample"]["variablesReference"]})["variables"]
        check(any(v["name"] == "Number" and v["value"] == "42" for v in fields), "DAP object fields")
        field_values = {v["name"]: v["value"] for v in fields}
        check(struct.pack("<f", float(field_values["SmallFloat"])) == struct.pack("<f", 1e-10),
              "DAP float 1e-10 round-trips")
        check(float(field_values["SmallDouble"]) == 1e-10 and float(field_values["PreciseDouble"]) == 1.23456789012345,
              "DAP double magnitude and precision round-trip")
        check(float(field_values["NegativeZero"]) == 0 and math.copysign(1, float(field_values["NegativeZero"])) == -1 and
              math.isnan(float(field_values["NotANumber"])) and float(field_values["PositiveInfinity"]) == math.inf and
              float(field_values["NegativeInfinity"]) == -math.inf, "DAP NaN, infinities and negative zero retain semantics")
        for expression, expected in [("answer + 1", "43"), ("numbers[1]", "42"), ("sample.Number", "42")]:
            check(dap.request("evaluate", {"frameId": frame["id"], "expression": expression})["result"] == expected,
                  "safe DAP evaluate " + expression)
        dap.request("evaluate", {"frameId": frame["id"], "expression": "sample.ToString()"}, success=False)
        evidence["breakpoint"] = {"event": event, "frame": frame, "locals": variables, "fields": fields, "array": array}
        dap.request("stepIn", {"threadId": thread})
        step, step_frame = stack()
        check(step["reason"] == "step" and step_frame["name"].endswith("FinishScenario"), "DAP source step-in")
        dap.request("next", {"threadId": step["threadId"]})
        step, _ = stack()
        check(step["reason"] == "step", "DAP source next")
        dap.request("stepOut", {"threadId": step["threadId"]})
        step, _ = stack()
        check(step["reason"] == "step", "DAP source step-out")
        dap.request("scopes", {"frameId": frame["id"]}, success=False)
        dap.request("continue", {"threadId": step["threadId"]})
        ui.receive()
        ui.command('MAUI assert --automationId result-status Text "Result: 42"')
        ui.send("MAUI tap --automationId deep-scenario")
        deep = dap.event("stopped")
        complete = dap.request("stackTrace", {"threadId": deep["threadId"]})
        deep_frames = complete["stackFrames"]
        check(complete["totalFrames"] == len(deep_frames) > 150 and
              sum(f["name"].endswith("DeepScenario") for f in deep_frames) == 150 and
              deep_frames[0]["line"] == deep_line, "real native stack reports all 150 recursive frames and truthful totalFrames")
        check(dap.request("evaluate", {"frameId": deep_frames[128]["id"], "expression": "depth"})["result"] == "128",
              "frame 128 resolves to the actual recursive caller depth 128")
        names = [f["name"] for f in deep_frames]
        for arguments, start, count in [
                ({"levels": 0}, 0, 0), ({"startFrame": 128}, 128, 0),
                ({"startFrame": 128, "levels": 0}, 128, 0),
                ({"startFrame": 128, "levels": 10}, 128, 10),
                ({"startFrame": len(names) + 1}, len(names) + 1, 0)]:
            page = dap.request("stackTrace", {"threadId": deep["threadId"]} | arguments)
            expected = names[start:start + count] if count else names[start:]
            check(page["totalFrames"] == len(names) and [f["name"] for f in page["stackFrames"]] == expected,
                  "real native stack page " + json.dumps(arguments))
            if start == 128:
                check(dap.request("evaluate", {"frameId": page["stackFrames"][0]["id"], "expression": "depth"})["result"] == "128",
                      "paged frame identity resolves to recursive caller depth 128")
        for expression in ("shifted", "shifted[0]", "shifted[5]", "shifted[149]", "shifted[150]"):
            error = dap.request("evaluate", {"frameId": deep_frames[0]["id"], "expression": expression}, success=False)
            check("UnsupportedArrayBounds" in error["error"]["format"], "real lower-bound-five array rejects " + expression)
        evidence["deepStack"] = complete
        dap.request("continue", {"threadId": deep["threadId"]})
        ui.receive()
        ui.command('MAUI assert --automationId result-status Text "Deep result: 55"')
        def loaded_modules():
            return [m for m in dap.request("modules")["modules"] if "DncDbg.ReloadFixture" in m["name"]]

        def run_contexts(count):
            ui.send("MAUI tap --automationId reload-run")
            for _ in range(count):
                hit, frame = stack()
                check(hit["reason"] == "breakpoint" and hit.get("hitBreakpointIds") == [reload_bp["id"]] and
                      frame["source"]["path"] == str(reload_source) and frame["line"] == reload_line,
                      "each collectible context stops at the original DAP source breakpoint")
                check(dap.request("evaluate", {"frameId": frame["id"], "expression": "answer + 1"})["result"] == "43",
                      "collectible context executes the actual 1/42/43 method")
                dap.request("continue", {"threadId": hit["threadId"]})
            ui.receive()
            ui.command(f'MAUI assert --automationId reload-status Text "Reload result: {42 * count} from {count}"')

        def unload_context(remaining):
            ui.command("MAUI tap --automationId reload-unload")
            ui.command(f'MAUI assert --automationId reload-status Text "Contexts: {remaining}; collected: True"')
            deadline = time.monotonic() + 10
            while len(loaded_modules()) != remaining:
                if time.monotonic() >= deadline:
                    raise TimeoutError("Collectible module unload was not reported.")
                time.sleep(0.01)
            check(True, "runtime unload removes exactly one module instance")

        ui.command("MAUI tap --automationId reload-load")
        changed = dap.event("breakpoint")["breakpoint"]
        check(changed["id"] == reload_bp["id"] and changed["verified"], "first collectible load verifies the existing request")
        first = loaded_modules()
        check(len(first) == 1, "first collectible instance appears in native module snapshot")
        run_contexts(1)
        ui.command("MAUI tap --automationId reload-load")
        both = loaded_modules()
        check(len(both) == 2 and len({m["version"] for m in both}) == 1 and len({m["id"] for m in both}) == 2,
              "two simultaneous load contexts share the PE MVID but have distinct native module generations")
        run_contexts(2)
        unload_context(1)
        surviving = loaded_modules()
        check(len(surviving) == 1 and surviving[0]["id"] != first[0]["id"],
              "unloading the first context preserves only the second instance")
        run_contexts(1)
        unload_context(0)
        changed = dap.event("breakpoint")["breakpoint"]
        check(changed["id"] == reload_bp["id"] and not changed["verified"], "last unload truthfully returns the same source request to pending")
        ui.command("MAUI tap --automationId reload-load")
        changed = dap.event("breakpoint")["breakpoint"]
        check(changed["id"] == reload_bp["id"] and changed["verified"], "later reload rebinds without changing DAP identity")
        reloaded = loaded_modules()
        check(len(reloaded) == 1 and reloaded[0]["id"] not in {m["id"] for m in both},
              "reloaded module has a fresh native load generation")
        run_contexts(1)
        unload_context(0)
        changed = dap.event("breakpoint")["breakpoint"]
        check(changed["id"] == reload_bp["id"] and not changed["verified"], "final collectible unload invalidates the last binding")
        module_ids = {m["id"] for m in both + reloaded}
        module_events = [m["body"] for m in dap.history if m.get("event") == "module" and
                         m["body"]["module"]["id"] in module_ids]
        check(len(module_events) == 6 and all(
            sorted(e["reason"] for e in module_events if e["module"]["id"] == id) == ["new", "removed"]
            for id in module_ids), "each real module generation emits exactly one truthful new/removed event pair")
        check(not any(m.get("event") == "stopped" or
                      (m.get("event") == "breakpoint" and m["body"]["breakpoint"]["id"] == reload_bp["id"])
                      for m in dap.history), "no duplicate stops or stale verification callbacks remain after collectible unload")
        evidence["collectibleModules"] = {"first": first, "both": both, "surviving": surviving, "reloaded": reloaded,
                                          "breakpoint": reload_bp, "events": module_events}
        dap.request("setExceptionBreakpoints", {"filters": [], "filterOptions": [
            {"filterId": "all", "condition": "System.ArgumentException"},
            {"filterId": "unhandled", "condition": "System.InvalidOperationException"}]})
        ui.command("MAUI tap --automationId throw-scenario")
        ui.command('MAUI assert --automationId result-status Text "Exception handled"')
        while not dap.messages.empty():
            dap.history.append(dap.messages.get_nowait())
        check(not any(m.get("event") == "stopped" for m in dap.history),
              "combined stage/type filters ignore caught first-chance InvalidOperationException")
        dap.request("setExceptionBreakpoints", {"filters": ["user-unhandled"]}, success=False)
        dap.request("setExceptionBreakpoints", {"filters": [], "filterOptions": [
            {"filterId": "all", "condition": "System.InvalidOperationException"}]})
        ui.send("MAUI tap --automationId throw-scenario")
        observed_exceptions = []
        for _ in range(32):
            exception = dap.event("stopped")
            exception_frames = dap.request("stackTrace", {"threadId": exception["threadId"]})["stackFrames"]
            check(exception["reason"] == "exception", "DAP thrown-exception stop")
            info = dap.request("exceptionInfo", {"threadId": exception["threadId"]})
            observed_exceptions.append(info["exceptionId"])
            evidence["exceptions"] = observed_exceptions
            if info["exceptionId"] == "System.InvalidOperationException" and any(f["name"].endswith("ThrowScenario") for f in exception_frames):
                evidence["exceptionStack"] = exception_frames
                break
            dap.request("continue", {"threadId": exception["threadId"]})
        else:
            raise AssertionError("MOBILE_EXCEPTION was not observed: " + repr(observed_exceptions))
        evidence["exceptions"] = observed_exceptions
        check(info["exceptionId"] == "System.InvalidOperationException", "DAP actual exception type and ThrowScenario frame")
        dap.request("setExceptionBreakpoints", {"filters": []})
        dap.request("continue", {"threadId": exception["threadId"]})
        ui.receive()
        ui.command('MAUI assert --automationId result-status Text "Exception handled"')
        dap.request("setExceptionBreakpoints", {"filters": []})
        dap.request("pause", {"threadId": thread})
        paused = dap.event("stopped")
        check(paused["reason"] == "pause", "DAP pause while polling does not deadlock")
        dap.request("continue", {"threadId": paused["threadId"]})
        check(len(dap.request("threads")["threads"]) > 0 and dap.request("modules")["totalModules"] > 0,
              "DAP real thread/module snapshots")
        modules = dap.request("modules")
        total = modules["totalModules"]
        ids = [module["id"] for module in modules["modules"]]
        pages = [({}, ids), ({"moduleCount": 0}, ids),
                 ({"startModule": 1}, ids[1:]), ({"startModule": 1, "moduleCount": 0}, ids[1:]),
                 ({"startModule": 1, "moduleCount": 2}, ids[1:3]),
                 ({"startModule": total + 1}, [])]
        for arguments, expected in pages:
            page = dap.request("modules", arguments)
            assert page["totalModules"] == total and [module["id"] for module in page["modules"]] == expected, page
        check(len(ids) == total, "module paging: omitted/zero all remaining, explicit counts, nonzero/out-of-range starts and totalModules")
        dap.request("restart")
        check(True, "default replaceExisting=false/uninstallOnDisconnect=false restarts its own installation")
        restored = dap.event("breakpoint")["breakpoint"]
        check(restored["id"] == breakpoint["id"] and restored["verified"], "DAP breakpoint ID remains stable across restart")
        # iOS gets a fresh DevFlow assignment after restart; Android keeps a UI-only mapping to its fixed fallback.
        ui.close()
        ui = None
        wait = subprocess.run(["maui", "devflow", "--device", args.device, "wait", "--wait-platform",
                               "Android" if args.platform == "android" else "iOS", "--timeout", "20"],
                              cwd=ROOT, capture_output=True, text=True, timeout=30)
        restarted_pid = dap.request("dotnet/info")["debuggeeProcessId"]
        if args.platform == "iosSimulator":
            ui_port = ios_ui_port(json.loads(wait.stdout), restarted_pid, args.ui_session)
        verify_ui(ui_port, args.platform, restarted_pid)
        ui = Ui(ui_port, args.output)
        ui.command("MAUI tree --depth 15")
        ui.send("MAUI tap --automationId run-scenario")
        restarted, restarted_frame = stack()
        check(restarted["reason"] == "breakpoint" and restarted_frame["line"] == line,
              "DAP restart reinstalls, reacquires endpoint and restores source breakpoints")
        check(dap.request("evaluate", {"frameId": restarted_frame["id"], "expression": "answer + 1"})["result"] == "43",
              "restart preserves exact 1/42/43 gate")
        dap.request("disconnect", {"terminateDebuggee": False})
        dap.event("terminated")
        ui.receive()
        ui.command('MAUI assert --automationId result-status Text "Result: 42"')
        deadline = time.monotonic() + 10
        report = native_report()
        while '"Phase":"owner-finished"' not in report and time.monotonic() < deadline:
            time.sleep(0.1)
            report = native_report()
        phases = [json.loads(row) for row in report.splitlines()]
        evidence["nativeCleanup"] = phases
        for phase in ("cleanup-detach-return", "terminate-return", "owner-finished"):
            matches = [row for row in phases if row["Phase"] == phase]
            check(len(matches) == 1 and matches[0]["HResult"] == "0x00000000", "native exact-once " + phase)
        image = args.output.resolve() / "detached.png"
        ui.command("MAUI screenshot --output " + json.dumps(str(image)) + " --overwrite")
        evidence["screenshotSha256"] = hashlib.sha256(image.read_bytes()).hexdigest()
        check(True, "DAP disconnect while stopped relinquishes stop and UI remains usable")
        evidence["passed"] = True
    finally:
        try:
            report = native_report()
            if report:
                (args.output / "native-report.jsonl").write_text(report)
        except (OSError, subprocess.SubprocessError):
            pass
        if ui is not None:
            ui.close()
        dap.close()
        (args.output / "adapter-stderr.log").write_text(dap.stderr)
        evidence["checks"] = checks
        evidence["dapMessages"] = dap.transcript
        # Ensure no target or UI route is left by this gate, including on an assertion failure.
        if args.platform == "android":
            device("shell", "am", "force-stop", "com.dncdbg.mobiletests", check=False)
            device("uninstall", "com.dncdbg.mobiletests", check=False)
            if ui_port:
                device("forward", "--remove", f"tcp:{ui_port}", check=False)
        else:
            subprocess.run(["xcrun", "simctl", "terminate", args.device, "com.dncdbg.mobiletests"],
                           capture_output=True, timeout=20)
            subprocess.run(["xcrun", "simctl", "uninstall", args.device, "com.dncdbg.mobiletests"],
                           capture_output=True, timeout=20)
        (args.output / "report.json").write_text(json.dumps(evidence, indent=2))


if __name__ == "__main__":
    main()
