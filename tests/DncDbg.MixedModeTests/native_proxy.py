#!/usr/bin/env python3
"""Real LLDB-backed DAP and read-only CLR integration, without a second execution controller."""

import argparse
import json
from pathlib import Path
import runpy
import shutil
import subprocess

base = runpy.run_path(str(Path(__file__).with_name("run.py")))
Dap, check, build, line = (base[n] for n in ("Dap", "check", "build", "line"))
ROOT, OUT, DLL, FIXTURE = (base[n] for n in ("ROOT", "OUT", "DLL", "FIXTURE"))


def locals_for(session, frame):
    scopes = session.request("scopes", dict(frameId=frame))["scopes"]
    scope = next(s for s in scopes if s["name"] == "Locals")
    variables = session.request("variables", dict(variablesReference=scope["variablesReference"]))["variables"]
    return scope["variablesReference"], {v["name"]: v["value"] for v in variables}


def scenario(adapter, lldb, managed, finish):
    arguments = ["--native-debugger=lldb", "--native-debugger-path=" + str(lldb)]
    with Dap(adapter, "lldb-proxy-" + finish, arguments) as session:
        capabilities = session.request("initialize", dict(adapterID="dncdbg", pathFormat="path",
                                                          linesStartAt1=True, columnsStartAt1=True))
        check(capabilities["dncdbg"]["executionOwner"] == "native-adapter", str(capabilities))
        launch = session.send("launch", dict(program=shutil.which("dotnet"), args=[str(DLL)],
                                             env=dict(DNCDBG_MIXED_CHECKPOINTS="1"),
                                             managedInspection=managed))
        session.event("initialized")
        breakpoints = session.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                                               breakpoints=[dict(line=line(FIXTURE / "native.c", marker))
                                                                            for marker in ("NATIVE_CHECKPOINT", "NATIVE_BREAK")]))
        check(len(breakpoints["breakpoints"]) == 2, str(breakpoints))
        checkpoint_id = breakpoints["breakpoints"][0]["id"]
        if managed:
            session.response(session.send("setBreakpoints", dict(source=dict(path=str(FIXTURE / "Program.cs")),
                                                                 breakpoints=[dict(line=line(FIXTURE / "Program.cs", "MANAGED_BEFORE"))])),
                             success=False)
        session.request("configurationDone")
        session.response(launch)
        stale = None
        previous_snapshot = None
        owner = None
        for visit in range(3):
            stop = session.event("stopped")
            thread = stop["threadId"]
            stack = session.request("stackTrace", dict(threadId=thread))["stackFrames"]
            check("native_checkpoint" in stack[0]["name"] if visit != 1 else "native_sum" in stack[0]["name"], str(stack[:3]))
            if visit != 1:
                check(checkpoint_id in stop["hitBreakpointIds"], "Persistent checkpoint breakpoint identity changed")
            native_ref, native = locals_for(session, stack[0]["id"])
            if visit == 1:
                check(native["left"] == "19" and native["right"] == "23", str(native))
                check(session.request("evaluate", dict(frameId=stack[0]["id"], expression="left + right", context="watch"))["result"] == "42",
                      "Native C expression evaluation was not delegated")
                stack = session.request("stackTrace", dict(threadId=thread))["stackFrames"]
                native_ref, native = locals_for(session, stack[0]["id"])
            else:
                check(native["phase"] == ("1" if visit == 0 else "2") and native["result"] == "42", str(native))
            if managed:
                caller = next(f for f in stack if "[Managed]" in f["name"] and "ManagedCall" in f["name"])
                check(caller["source"]["path"].endswith("Program.cs") and caller["line"] > 0, str(caller))
                raw = session.request("dotnet/nativeStackTrace", dict(threadId=thread))["stackFrames"]
                check(caller["instructionPointerReference"].lower() in
                      {f["instructionPointerReference"].lower() for f in raw}, "Managed/native PCs did not correlate exactly")
                info = session.request("dotnet/info")
                check(info["executionOwner"] == "native-adapter" and not info["managedExecutionControl"], str(info))
                if owner is None:
                    owner = info["nativeAdapterProcessId"]
                check(owner == info["nativeAdapterProcessId"], "Native adapter ownership changed mid-session")
                if previous_snapshot is not None:
                    check(info["managedSnapshot"] > previous_snapshot, "A stale CLR virtual snapshot was reused")
                previous_snapshot = info["managedSnapshot"]
                managed_ref, values = locals_for(session, caller["id"])
                check(native_ref != managed_ref, "Native and managed variables references collided")
                check(values["left"] == "19" and values["right"] == "23", str(values))
                if visit == 2:
                    check(values["result"] == "42", "Managed result was stale: " + str(values))
                check(session.request("evaluate", dict(frameId=caller["id"], expression="left", context="watch"))["result"] == "19",
                      "Managed scalar inspection failed")
                unsupported = session.send("evaluate", dict(frameId=caller["id"], expression="NativeAdd(1, 2)", context="watch"))
                response = session.receive(lambda m: m.get("request_seq") == unsupported and m.get("type") == "response")
                check(not response["success"] and "read-only" in response["message"].lower(), str(response))
                session.request("setVariable", dict(variablesReference=managed_ref, name="left", value="1"), success=False)
                if stale is not None:
                    session.request("scopes", dict(frameId=stale), success=False)
                stale = caller["id"]
            if finish != "continue":
                if finish == "disconnect":
                    session.request("disconnect", dict(terminateDebuggee=True))
                else:
                    session.process.stdin.close()
                session.process.wait(timeout=15)
                check(session.process.returncode == 0, f"Proxy did not shut down cleanly: {session.process.returncode}")
                return
            if visit == 1:
                session.request("next", dict(threadId=thread))
                stepped = session.event("stopped")
                after = session.request("stackTrace", dict(threadId=stepped["threadId"]))["stackFrames"][0]
                _, values = locals_for(session, after["id"])
                check(values["sum"] == "42", str(values))
            session.request("continue", dict(threadId=thread))
        check(session.event("exited")["exitCode"] == 0, "Target exit failed")
        session.request("disconnect")
    print(f"PASS LLDB proxy: managed inspection={managed}, lifecycle={finish}", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adapter", default=str(ROOT / "bin" / "dncdbg-dap"))
    parser.add_argument("--lldb-dap", required=True)
    parser.add_argument("--native-only", action="store_true")
    options = parser.parse_args()
    build()
    info = subprocess.run([options.adapter, "--native-debugger=lldb", "--native-debugger-path=" + options.lldb_dap,
                           "--native-debugger-info"], capture_output=True, text=True)
    check(info.returncode == 0, info.stderr)
    check(json.loads(info.stdout)["path"] == options.lldb_dap, info.stdout)
    for finish in ("continue", "disconnect", "eof"):
        scenario(options.adapter, options.lldb_dap, not options.native_only, finish)
