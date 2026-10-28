#!/usr/bin/env python3
"""Local AppKit mixed-stack proof driven by DNCDbg, LLDB, and DevFlow."""

import argparse
import atexit
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
            agent = next((agent for agent in agents
                          if agent["processId"] == pid and agent["tfm"] == "net10.0-macos"), None)
            if agent:
                return agent
        time.sleep(0.2)
    raise AssertionError(f"DevFlow agent for debuggee {pid} did not connect")


def terminate(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


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
            env=[
                "DOTNET_ReadyToRun=0",
                "DOTNET_TieredCompilation=0",
            ],
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
        agent = wait_for_agent(pid)
        agent_port = str(agent["port"])
        subprocess.run([
            "maui", "devflow", "ui", "query",
            "--agent-port", agent_port,
            "--automationId", "RunMixedMode",
            "--wait-until", "exists",
            "--timeout", "30",
            "--json",
        ], check=True, capture_output=True, text=True)
        batch = subprocess.Popen([
            "maui", "devflow", "batch", "--agent-port", agent_port,
        ], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        atexit.register(terminate, batch)
        batch.stdin.write(
            "ui tap RunMixedMode; "
            "ui tap RunMixedMode; "
            "ui tap RunMixedMode; "
            "ui assert --automationId MixedModeStatus Text 126:3\n")
        batch.stdin.close()

        stop = session.event("stopped")
        stack_result = session.request("stackTrace", dict(threadId=stop["threadId"]))
        frames = stack_result["stackFrames"]
        check(stack_result["dncdbgManagedInspection"]["skippedFrames"] > 0, str(stack_result))
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
        callback_address = callback["dncdbgCodeStartReference"]
        strict = session.request("dotnet/managedStackTrace", dict(threadId=stop["threadId"]))
        strict_callback = next(frame for frame in strict["stackFrames"]
                               if "ManagedCallback" in frame["name"])
        check(strict_callback["dncdbgCodeStartReference"].lower() == callback_address.lower(),
              str(strict_callback))

        managed_breakpoints = session.request("setBreakpoints", dict(
            source=dict(path=str(PROJECT.parent / "MixedModeScenario.cs")),
            breakpoints=[dict(line=line(PROJECT.parent / "MixedModeScenario.cs", "MAUI_MANAGED_CALLBACK_ENTRY"))],
        ))["breakpoints"]
        check(len(managed_breakpoints) == 1 and managed_breakpoints[0]["verified"],
              str(managed_breakpoints))
        managed_breakpoint_id = managed_breakpoints[0]["id"]
        managed_breakpoint_address = managed_breakpoints[0]["instructionReference"]
        session.request("setBreakpoints", dict(
            source=dict(path=str(FIXTURE / "native.c")),
            breakpoints=[],
        ))

        session.request("continue", dict(threadId=stop["threadId"]))
        managed_stop = session.event("stopped")
        check(managed_stop["reason"] == "breakpoint"
              and managed_breakpoint_id in managed_stop.get("hitBreakpointIds", []),
              str(managed_stop))
        managed_stack = session.request("stackTrace", dict(threadId=managed_stop["threadId"]))
        managed_callback = managed_stack["stackFrames"][0]
        check("[Managed]" in managed_callback["name"] and "ManagedCallback" in managed_callback["name"],
              "Managed source breakpoint top frame is not the callback: " + str(managed_stack))
        check(managed_callback["line"] == line(PROJECT.parent / "MixedModeScenario.cs", "MAUI_MANAGED_CALLBACK_ENTRY")
              and managed_callback["instructionPointerReference"].lower() == managed_breakpoint_address.lower()
              and managed_callback["dncdbgCodeStartReference"].lower() == callback_address.lower(),
              str(managed_callback))

        session.request("stepOut", dict(threadId=managed_stop["threadId"]))
        transition_stop = session.event("stopped")
        transition_stack = session.request("stackTrace", dict(threadId=transition_stop["threadId"]))
        native_caller = transition_stack["stackFrames"][0]
        check(transition_stop["reason"] == "step"
              and transition_stop["description"] == "managed/native step out"
              and "hitBreakpointIds" not in transition_stop
              and "native_callback_roundtrip" in native_caller["name"]
              and native_caller["source"]["path"].endswith("native.cpp")
              and native_caller["line"] == line(FIXTURE / "native.cpp", "callback(sum)"),
              json.dumps(dict(stop=transition_stop, stack=transition_stack), indent=2))
        session.request("continue", dict(threadId=transition_stop["threadId"]))
        repeated_stop = session.event("stopped")
        check(repeated_stop["reason"] == "breakpoint"
              and managed_breakpoint_id in repeated_stop.get("hitBreakpointIds", []),
              str(repeated_stop))
        repeated_stack = session.request("stackTrace", dict(threadId=repeated_stop["threadId"]))
        repeated_callback = repeated_stack["stackFrames"][0]
        check("[Managed]" in repeated_callback["name"]
              and "ManagedCallback" in repeated_callback["name"]
              and repeated_callback["line"] == line(
                  PROJECT.parent / "MixedModeScenario.cs", "MAUI_MANAGED_CALLBACK_ENTRY"),
              str(repeated_stack))
        session.request("continue", dict(threadId=repeated_stop["threadId"]))
        try:
            batch.wait(timeout=30)
        except subprocess.TimeoutExpired:
            terminate(batch)
        batch_output = batch.stdout.read()
        batch_error = batch.stderr.read()
        batch_results = [json.loads(result) for result in batch_output.splitlines() if result]
        check(len(batch_results) == 4
              and all(result["exit_code"] == 0 for result in batch_results)
              and batch_results[0]["command"] == "ui tap RunMixedMode"
              and batch_results[1]["command"] == "ui tap RunMixedMode"
              and batch_results[2]["command"] == "ui tap RunMixedMode"
              and batch_results[3]["command"].startswith("ui assert"),
              batch_output + batch_error)
        atexit.unregister(terminate)

        session.request("disconnect", dict(terminateDebuggee=True))
        print("PASS MAUI AppKit managed source breakpoint and managed-to-native StepOut", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adapter", default=str(ROOT / "bin/dncdbg-dap"))
    parser.add_argument("--lldb-dap", default=subprocess.check_output(
        ["xcrun", "--find", "lldb-dap"], text=True).strip())
    options = parser.parse_args()
    main(Path(options.adapter).resolve(), Path(options.lldb_dap).resolve())
