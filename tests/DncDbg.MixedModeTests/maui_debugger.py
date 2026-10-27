#!/usr/bin/env python3
"""Local AppKit mixed-stack proof driven by DNCDbg, LLDB, and DevFlow."""

import argparse
import json
from pathlib import Path
import runpy
import subprocess
import time


base = runpy.run_path(str(Path(__file__).with_name("run.py")))
Dap, check, line = (base[name] for name in ("Dap", "check", "line"))
ROOT, OUT, FIXTURE = (base[name] for name in ("ROOT", "OUT", "FIXTURE"))
PROJECT = Path(__file__).with_name("MauiSample") / "DncDbg.MixedMode.Maui.csproj"
APP = PROJECT.parent / "bin/Debug/net10.0-macos/osx-arm64/DNCDbg Mixed Mode.app"
PROGRAM = APP / "Contents/MacOS/DncDbg.MixedMode.Maui"


def wait_for_agent(pid):
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        result = subprocess.run(["maui", "devflow", "list", "--json"], capture_output=True, text=True)
        if result.returncode == 0:
            agents = json.loads(result.stdout)
            if any(agent["processId"] == pid and agent["tfm"] == "net10.0-macos" for agent in agents):
                return
        time.sleep(0.2)
    raise AssertionError(f"DevFlow agent for debuggee {pid} did not connect")


def main(adapter, lldb):
    log = OUT / "maui-appkit-mixed.engine.log"
    with Dap(adapter, "maui-appkit-mixed", [
        "--native-debugger=lldb",
        "--native-debugger-path=" + str(lldb),
        "--log=" + str(log),
    ]) as session:
        capabilities = session.request("initialize", dict(
            adapterID="dncdbg",
            pathFormat="path",
            linesStartAt1=True,
            columnsStartAt1=True,
            supportsInvalidatedEvent=True,
        ))
        check(capabilities["dncdbg"]["executionOwner"] == "native-adapter", str(capabilities))

        launch = session.send("launch", dict(
            program=str(PROGRAM),
            cwd=str(APP.parent),
            managedInspection=True,
        ))
        session.event("initialized")
        breakpoints = session.request("setBreakpoints", dict(
            source=dict(path=str(FIXTURE / "native.c")),
            breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_CHECKPOINT"))],
        ))
        check(len(breakpoints["breakpoints"]) == 1, str(breakpoints))
        session.request("configurationDone")
        session.response(launch)

        info = session.request("dotnet/info")
        pid = info["debuggeeProcessId"]
        wait_for_agent(pid)
        subprocess.run([
            "maui", "devflow", "ui", "query",
            "--automationId", "RunMixedMode",
            "--wait-until", "exists",
            "--timeout", "30",
            "--json",
        ], check=True, capture_output=True, text=True)
        tap = subprocess.Popen([
            "maui", "devflow", "ui", "tap", "--automationId", "RunMixedMode", "--json",
        ], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

        stop = session.event("stopped")
        stack_result = session.request("stackTrace", dict(threadId=stop["threadId"]))
        frames = stack_result["stackFrames"]
        check("native_checkpoint" in frames[0]["name"], str(frames[:5]))
        callback = next((frame for frame in frames
                         if "[Managed]" in frame["name"] and "ManagedCallback" in frame["name"]), None)
        check(callback is not None, "No managed callback frame in mixed stack: " + str(frames))
        check(callback["source"]["path"].endswith("MixedModeScenario.cs"), str(callback))
        scopes = session.request("scopes", dict(frameId=callback["id"]))["scopes"]
        variables = []
        for scope in scopes:
            variables += session.request("variables", dict(
                variablesReference=scope["variablesReference"]))["variables"]
        values = {variable["name"]: variable["value"] for variable in variables}
        check(values.get("value") == "42", "Managed callback argument unavailable: " + str(values))
        check(any("native_callback_roundtrip" in frame["name"] for frame in frames), str(frames))

        session.request("continue", dict(threadId=stop["threadId"]))
        tap_output, tap_error = tap.communicate(timeout=10)
        check(tap.returncode == 0, tap_output + tap_error)
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            status = subprocess.run([
                "maui", "devflow", "ui", "query",
                "--automationId", "MixedModeStatus",
                "--fields", "text",
                "--json",
            ], capture_output=True, text=True)
            if status.returncode == 0 and json.loads(status.stdout)[0]["text"] == "126":
                break
            time.sleep(0.2)
        else:
            raise AssertionError("MAUI mixed-mode callback did not complete with 126")

        session.request("disconnect", dict(terminateDebuggee=True))
        print("PASS MAUI AppKit LLDB-owned mixed stack includes active C# callback", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adapter", default=str(ROOT / "bin/dncdbg-dap"))
    parser.add_argument("--lldb-dap", default=subprocess.check_output(
        ["xcrun", "--find", "lldb-dap"], text=True).strip())
    options = parser.parse_args()
    main(Path(options.adapter).resolve(), Path(options.lldb_dap).resolve())
