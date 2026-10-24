#!/usr/bin/env python3
"""DAP backend ownership, namespace, discovery and lifecycle contracts."""

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
FAKE = Path(__file__).with_name("fake_native_adapter.py").resolve()
integration = runpy.run_path(str(Path(__file__).with_name("native_proxy.py")))


def arguments(kind="dap", mode="normal", lldb=None, barrier=None):
    result = ["--native-debugger=" + kind, "--native-debugger-path=" + sys.executable,
              "--native-debugger-arg=" + str(FAKE), "--native-debugger-arg=--mode=" + mode]
    if lldb:
        result += ["--native-debugger-arg=--lldb=" + str(lldb)]
    if barrier:
        result += ["--native-debugger-arg=--barrier=" + str(barrier)]
    return result


def fake_contracts(adapter):
    with Dap(adapter, "native-proxy-contracts", arguments()) as peer:
        init = peer.request("initialize", dict(adapterID="dncdbg", supportsRunInTerminalRequest=True))
        check(not init["supportsCancelRequest"], "Untranslated native request IDs must not advertise cancellation")
        peer.request("launch", dict(program="test-owned"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        frames = peer.request("stackTrace", dict(threadId=7))["stackFrames"]
        frame = frames[0]["id"]
        source = frames[0]["source"]["sourceReference"]
        scope = peer.request("scopes", dict(frameId=frame))["scopes"][0]["variablesReference"]
        check(len({frame, source, scope}) == 3, "Frame/source/variable namespaces collided")
        variables = peer.request("variables", dict(variablesReference=scope))["variables"]
        child = variables[0]["variablesReference"]
        check(child not in (frame, source, scope), "Child variable identity collided")
        check(peer.request("variables", dict(variablesReference=child))["variables"][0]["value"] == "42", "Native variables not forwarded")
        check(peer.request("source", dict(sourceReference=source))["content"] == "native source", "Native source ID not forwarded")
        peer.request("reverse")
        reverse = peer.receive(lambda m: m.get("type") == "request" and m.get("command") == "runInTerminal")
        data = json.dumps(dict(seq=999, type="response", request_seq=reverse["seq"], command="runInTerminal",
                               success=True, body=dict(processId=123))).encode()
        peer.process.stdin.write(f"Content-Length: {len(data)}\r\n\r\n".encode() + data)
        peer.process.stdin.flush()
        deadline = time.monotonic() + 5
        while not peer.request("reverseStatus")["valid"]:
            check(time.monotonic() < deadline, "Reverse request identity was not routed")
            time.sleep(0.01)
        peer.request("continue", dict(threadId=7))
        peer.event("stopped")
        peer.request("scopes", dict(frameId=frame), success=False)
        peer.request("variables", dict(variablesReference=scope), success=False)
        new_frame = peer.request("stackTrace", dict(threadId=7))["stackFrames"][0]["id"]
        check(new_frame != frame, "Frame IDs were reused across native stops")
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="second-owned"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        peer.request("source", dict(sourceReference=source), success=False)
        peer.request("scopes", dict(frameId=new_frame), success=False)
        peer.request("unknownNativeRequest", success=False)
        peer.request("disconnect")
        peer.process.wait(timeout=10)
        check(peer.process.returncode == 0, "Same-proxy reinitialization failed")
    print("PASS generic DAP backend: identities, reverse requests, stale references, reinitialization", flush=True)

    for mode in ("version-mismatch", "malformed"):
        with Dap(adapter, "native-proxy-" + mode, arguments("lldb" if mode == "version-mismatch" else "dap", mode)) as peer:
            sequence = peer.send("initialize", dict(adapterID="dncdbg"))
            message = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == sequence)
            check(not message["success"], str(message))
            if mode == "version-mismatch":
                check("version mismatch" in message["message"].lower(), str(message))
            disconnected = peer.send("disconnect")
            peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == disconnected)
            peer.process.wait(timeout=15)
    missing = subprocess.run([adapter, "--native-debugger=lldb", "--native-debugger-path=" + str(OUT / "missing-lldb-dap"),
                              "--native-debugger-info"], capture_output=True, text=True)
    check(missing.returncode == 1 and "not found" in missing.stderr.lower(), missing.stderr)
    print("PASS LLDB missing/version mismatch and malformed backend DAP errors", flush=True)


def bundle_contracts(adapter):
    bundle = OUT / "native-bundle-fixture"
    bundle.mkdir(exist_ok=True)
    manifest = dict(provider="llvm", licenseExpression="Apache-2.0 WITH LLVM-exception",
                    rid="wrong-rid", version="0.0.0", executable="bin/lldb-dap", licenseFile="LICENSE.TXT")
    (bundle / "native-adapter.json").write_text(json.dumps(manifest))
    command = [adapter, "--native-debugger=lldb", "--native-debugger-path=" + str(bundle), "--native-debugger-info"]
    result = subprocess.run(command, capture_output=True, text=True)
    check(result.returncode == 1 and "rid" in result.stderr.lower(), result.stderr)
    manifest["provider"] = "apple"
    (bundle / "native-adapter.json").write_text(json.dumps(manifest))
    result = subprocess.run(command, capture_output=True, text=True)
    check(result.returncode == 1 and "do not bundle xcode" in result.stderr.lower(), result.stderr)
    actual = subprocess.run([adapter, "--buildinfo"], capture_output=True, text=True, check=True).stdout
    os_name = next(s.split(":", 1)[1].strip() for s in actual.splitlines() if "Target OS:" in s)
    arch = next(s.split(":", 1)[1].strip() for s in actual.splitlines() if "Target arch:" in s)
    manifest.update(provider="llvm", rid=os_name + "-" + arch, executable="../escape")
    (bundle / "native-adapter.json").write_text(json.dumps(manifest))
    result = subprocess.run(command, capture_output=True, text=True)
    check(result.returncode == 1 and "inside the bundle" in result.stderr.lower(), result.stderr)
    manifest["executable"] = "bin/lldb-dap"
    (bundle / "native-adapter.json").write_text(json.dumps(manifest))
    result = subprocess.run(command, capture_output=True, text=True)
    check(result.returncode == 1 and "license file is missing" in result.stderr.lower(), result.stderr)
    print("PASS upstream bundle RID, path boundary, license declaration and no-Xcode-repackaging contracts", flush=True)


def invalid_runtime(adapter, lldb, poison):
    directory = OUT / ("poisoned-runtime" if poison else "missing-runtime")
    if poison:
        directory.mkdir(exist_ok=True)
        extension = ".dll" if sys.platform == "win32" else ".dylib" if sys.platform == "darwin" else ".so"
        prefix = "" if sys.platform == "win32" else "lib"
        original = next(p for p in DLL.parent.iterdir() if "mixedfixture" in p.name and p.suffix == extension)
        for name in ("coreclr", "mscordbi", "mscordaccore"):
            shutil.copy2(original, directory / (prefix + name + extension))
    with Dap(adapter, "native-runtime-poison" if poison else "native-runtime-missing",
             ["--native-debugger=lldb", "--native-debugger-path=" + lldb]) as peer:
        peer.request("initialize", dict(adapterID="dncdbg", pathFormat="path", linesStartAt1=True, columnsStartAt1=True))
        launch = peer.send("launch", dict(program=shutil.which("dotnet"), args=[str(DLL)],
                                          env=dict(DNCDBG_MIXED_CHECKPOINTS="1"),
                                          managedInspection=dict(runtimeDirectory=str(directory))))
        peer.event("initialized")
        peer.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                           breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_CHECKPOINT"))]))
        peer.request("configurationDone")
        peer.response(launch)
        stop = peer.event("stopped")
        stack = peer.request("stackTrace", dict(threadId=stop["threadId"]))
        check(stack["dncdbgManagedInspection"]["status"] == "unavailable", str(stack))
        check(not any("[Managed]" in f["name"] for f in stack["stackFrames"]), "Mismatched runtime produced managed data")
        sequence = peer.send("dotnet/managedStackTrace", dict(threadId=stop["threadId"]))
        response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == sequence)
        check(not response["success"], str(response))
        check("runtime" in response["message"].lower() or "library" in response["message"].lower()
              or "CORDBG" in response["message"], str(response))
        peer.request("disconnect", dict(terminateDebuggee=True))
    print("PASS managed runtime identity rejection: " + ("poisoned/mismatched DLLs" if poison else "missing directory"), flush=True)


def detached_backend_stall(adapter):
    gate = OUT / "detached-backend-stall.release"
    gate.unlink(missing_ok=True)
    target = None
    try:
        with Dap(adapter, "native-detach-stall", arguments(mode="detach-stall", barrier=gate)) as peer:
            peer.request("initialize", dict(adapterID="dncdbg"))
            peer.request("launch", dict(program="owned-test-target"))
            peer.event("initialized")
            target = peer.event("process")["systemProcessId"]
            peer.request("configurationDone")
            peer.event("stopped")
            peer.request("disconnect", dict(terminateDebuggee=False))
            peer.process.wait(timeout=15)
            check(integration["alive"](target), "Killing a stuck detached adapter also killed its relinquished target")
            gate.write_text("release")
            integration["exited"](target)
            target = None
        print("PASS stuck adapter detach preserves its relinquished target", flush=True)
    finally:
        if target and integration["alive"](target):
            os.kill(target, getattr(signal, "SIGKILL", signal.SIGTERM))
            integration["exited"](target)
        gate.unlink(missing_ok=True)


def wait_file(path):
    deadline = time.monotonic() + 10
    while not path.exists():
        check(time.monotonic() < deadline, "The deterministic native read barrier was not reached")
        time.sleep(0.01)


def interrupted_read(adapter, lldb, mode):
    barrier = OUT / ("read-barrier-" + mode)
    for suffix in (".arm", ".ready"):
        barrier.with_suffix(suffix).unlink(missing_ok=True)
    with Dap(adapter, "native-read-" + mode, arguments("lldb", mode, lldb, barrier)) as peer:
        peer.request("initialize", dict(adapterID="dncdbg", pathFormat="path", linesStartAt1=True, columnsStartAt1=True))
        launched = peer.send("launch", dict(program=shutil.which("dotnet"), args=[str(DLL)],
                                            env=dict(DNCDBG_MIXED_CHECKPOINTS="1"), managedInspection=True))
        peer.event("initialized")
        peer.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                           breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_CHECKPOINT")),
                                                        dict(line=line(FIXTURE / "native.c", "NATIVE_BREAK"))]))
        peer.request("configurationDone")
        peer.response(launched)
        stop = peer.event("stopped")
        barrier.with_suffix(".arm").write_text("arm")
        read = peer.send("stackTrace", dict(threadId=stop["threadId"]))
        wait_file(barrier.with_suffix(".ready"))
        if mode == "hold":
            continued = peer.send("continue", dict(threadId=stop["threadId"]))
            peer.response(read, success=False)
            peer.response(continued)
            next_stop = peer.event("stopped")
            frames = peer.request("stackTrace", dict(threadId=next_stop["threadId"]))["stackFrames"]
            check("native_sum" in frames[0]["name"], "One native continue did not reach the next breakpoint")
        else:
            peer.response(read, success=False)
            peer.event("terminated")
        disconnected = peer.send("disconnect", dict(terminateDebuggee=True))
        response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == disconnected)
        check(isinstance(response["success"], bool), "Shutdown did not complete")
        peer.process.wait(timeout=15)
    barrier.with_suffix(".arm").unlink(missing_ok=True)
    barrier.with_suffix(".ready").unlink(missing_ok=True)
    print(f"PASS interrupted managed read: {mode}", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adapter", required=True)
    parser.add_argument("--lldb-dap")
    options = parser.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    fake_contracts(options.adapter)
    bundle_contracts(options.adapter)
    detached_backend_stall(options.adapter)
    if options.lldb_dap:
        build()
        for poison in (False, True):
            invalid_runtime(options.adapter, options.lldb_dap, poison)
        for mode in ("hold", "exit-during-read", "lost-server"):
            interrupted_read(options.adapter, options.lldb_dap, mode)
