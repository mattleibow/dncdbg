#!/usr/bin/env python3
"""Real LLDB-backed DAP and read-only CLR integration, without a second execution controller."""

import argparse
import json
import os
from pathlib import Path
import runpy
import shutil
import signal
import subprocess
import sys
import time

base = runpy.run_path(str(Path(__file__).with_name("run.py")))
Dap, check, build, line = (base[n] for n in ("Dap", "check", "build", "line"))
ROOT, OUT, DLL, FIXTURE = (base[n] for n in ("ROOT", "OUT", "DLL", "FIXTURE"))
EXTRA_ENV = {}
EXPECT_UNAVAILABLE = False


def target(extra=()):
    return (shutil.which("dotnet"), [str(DLL), *extra]) if DLL.suffix == ".dll" else (str(DLL), list(extra))


def alive(pid):
    if sys.platform == "win32":
        import ctypes
        from ctypes import wintypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        handle = kernel.OpenProcess(0x100000, False, pid)
        if not handle:
            check(ctypes.get_last_error() == 87, f"OpenProcess failed: {ctypes.get_last_error()}")
            return False
        try:
            status = kernel.WaitForSingleObject(handle, 0)
            check(status in (0, 258), f"WaitForSingleObject failed: {status}")
            return status == 258
        finally:
            check(kernel.CloseHandle(handle), "CloseHandle")
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False


def exited(pid):
    deadline = time.monotonic() + 10
    while alive(pid):
        check(time.monotonic() < deadline, f"Target {pid} survived native adapter shutdown")
        time.sleep(0.01)


def locals_for(session, frame):
    scopes = session.request("scopes", dict(frameId=frame))["scopes"]
    scope = next(s for s in scopes if s["name"] == "Locals")
    variables = session.request("variables", dict(variablesReference=scope["variablesReference"]))["variables"]
    return scope["variablesReference"], {v["name"]: v["value"] for v in variables}


def scenario(adapter, lldb, managed, finish):
    arguments = ["--native-debugger=lldb", "--native-debugger-path=" + str(lldb),
                 "--log=" + str(OUT / ("proxy-" + finish + ".engine.log"))]
    with Dap(adapter, "lldb-proxy-" + finish, arguments) as session:
        capabilities = session.request("initialize", dict(adapterID="dncdbg", pathFormat="path",
                                                          linesStartAt1=True, columnsStartAt1=True,
                                                          supportsInvalidatedEvent=True))
        check(capabilities["dncdbg"]["executionOwner"] == "native-adapter", str(capabilities))
        program, arguments_to_target = target()
        launch = session.send("launch", dict(program=program, args=arguments_to_target,
                                             env=dict(DNCDBG_MIXED_CHECKPOINTS="1", **EXTRA_ENV),
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
            pid = session.request("dotnet/info")["debuggeeProcessId"]
            check(isinstance(pid, int), "Native backend did not report its actual target PID")
            stack_result = session.request("stackTrace", dict(threadId=thread))
            stack = stack_result["stackFrames"]
            if EXPECT_UNAVAILABLE:
                check(stack_result["dncdbgManagedInspection"]["status"] == "unavailable", str(stack_result))
                check(any(reason in stack_result["dncdbgManagedInspection"]["message"].lower()
                          for reason in ("metadata", "single-file")), str(stack_result))
                session.request("dotnet/managedStackTrace", dict(threadId=thread), success=False)
            check("native_checkpoint" in stack[0]["name"] if visit != 1 else "native_sum" in stack[0]["name"], str(stack[:3]))
            if visit != 1:
                check(checkpoint_id in stop["hitBreakpointIds"], "Persistent checkpoint breakpoint identity changed")
            native_ref, native = locals_for(session, stack[0]["id"])
            if visit == 1:
                check(native["left"] == "19" and native["right"] == "23", str(native))
                check(session.request("evaluate", dict(frameId=stack[0]["id"], expression="left + right", context="watch"))["result"] == "42",
                      "Native C expression evaluation was not delegated")
                if managed and not EXPECT_UNAVAILABLE:
                    invalidated = session.event("invalidated")
                    check(set(invalidated["areas"]) >= {"stacks", "variables"},
                          "Native evaluation expired managed references without notifying the client")
                stack = session.request("stackTrace", dict(threadId=thread))["stackFrames"]
                native_ref, native = locals_for(session, stack[0]["id"])
            else:
                check(native["phase"] == ("1" if visit == 0 else "2") and native["result"] == "42", str(native))
            if managed and not EXPECT_UNAVAILABLE:
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
                exited(pid)
                print(f"PASS LLDB proxy: managed inspection={managed}, lifecycle={finish}, actual target exit", flush=True)
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
        session.process.wait(timeout=15)
        exited(pid)
    print(f"PASS LLDB proxy: managed inspection={managed}, lifecycle={finish}", flush=True)


def detach_scenario(adapter, lldb, managed):
    gate = OUT / "native-detach-release"
    ready = Path(str(gate) + ".ready")
    gate.unlink(missing_ok=True)
    ready.unlink(missing_ok=True)
    detached_pid = None
    try:
        with Dap(adapter, "lldb-proxy-detach", ["--native-debugger=lldb", "--native-debugger-path=" + lldb]) as peer:
            peer.request("initialize", dict(adapterID="dncdbg", pathFormat="path", linesStartAt1=True, columnsStartAt1=True))
            program, arguments_to_target = target(["--exit-gate", str(gate)])
            launch = peer.send("launch", dict(program=program, args=arguments_to_target,
                                              env=dict(DNCDBG_MIXED_CHECKPOINTS="1", **EXTRA_ENV), managedInspection=managed,
                                              preRunCommands=[
                                                  'settings set target.output-path "' + str(OUT / "detached.stdout").replace("\\", "/") + '"',
                                                  'settings set target.error-path "' + str(OUT / "detached.stderr").replace("\\", "/") + '"',
                                              ]))
            peer.event("initialized")
            peer.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                               breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_CHECKPOINT"))]))
            peer.request("configurationDone")
            peer.response(launch)
            stop = peer.event("stopped")
            stack = peer.request("stackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
            if managed and not EXPECT_UNAVAILABLE:
                check(any("[Managed]" in f["name"] for f in stack), "No managed snapshot before detach")
            pid = peer.request("dotnet/info")["debuggeeProcessId"]
            peer.request("disconnect", dict(terminateDebuggee=False))
            detached_pid = pid
            peer.process.wait(timeout=15)
            check(peer.process.returncode == 0, "Proxy failed to detach")
            deadline = time.monotonic() + 10
            while not ready.exists():
                check(time.monotonic() < deadline and alive(pid), "Detached target did not survive and reach its exit gate")
                time.sleep(0.01)
            check(alive(pid), "Detach killed the target")
            gate.write_text("release")
            exited(pid)
            detached_pid = None
        print("PASS LLDB detach: inspected target survives, completes both transitions, then exits on release", flush=True)
    finally:
        if detached_pid is not None and alive(detached_pid):
            os.kill(detached_pid, getattr(signal, "SIGKILL", signal.SIGTERM))
            exited(detached_pid)
        gate.unlink(missing_ok=True)
        ready.unlink(missing_ok=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adapter", default=str(ROOT / "bin" / "dncdbg-dap"))
    parser.add_argument("--lldb-dap", required=True)
    parser.add_argument("--native-only", action="store_true")
    parser.add_argument("--fixture", type=Path)
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--tiered-jit", choices=("0", "1"))
    parser.add_argument("--expect-managed-unavailable", action="store_true")
    options = parser.parse_args()
    if not options.skip_build:
        build()
    if options.fixture:
        DLL = options.fixture.resolve()
    if options.tiered_jit is not None:
        EXTRA_ENV["DOTNET_TieredCompilation"] = options.tiered_jit
    EXPECT_UNAVAILABLE = options.expect_managed_unavailable
    info = subprocess.run([options.adapter, "--native-debugger=lldb", "--native-debugger-path=" + options.lldb_dap,
                           "--native-debugger-info"], capture_output=True, text=True)
    check(info.returncode == 0, info.stderr)
    check(json.loads(info.stdout)["path"] == options.lldb_dap, info.stdout)
    for finish in ("continue", "disconnect", "eof"):
        scenario(options.adapter, options.lldb_dap, not options.native_only, finish)
    detach_scenario(options.adapter, options.lldb_dap, not options.native_only)
