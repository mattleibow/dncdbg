#!/usr/bin/env python3
"""DAP backend ownership, namespace, discovery and lifecycle contracts."""

import argparse
import json
import os
from pathlib import Path
import queue
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
    older = subprocess.run([adapter, *arguments("lldb", "no-cli-version"), "--native-debugger-info"],
                           capture_output=True, text=True)
    check(older.returncode == 0 and json.loads(older.stdout)["version"] == "21.1.8",
          "A public LLDB DAP adapter without --version must use the initialization version: " + older.stderr)
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
            closed = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == disconnected)
            if not closed["success"]:
                peer.process.stdin.close()
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
                                          env=["DNCDBG_MIXED_CHECKPOINTS=1"],
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


def refused_detach(adapter):
    gate = OUT / "refused-detach.release"
    marker = Path(str(gate) + ".terminated")
    gate.unlink(missing_ok=True)
    marker.unlink(missing_ok=True)
    pid = None
    try:
        with Dap(adapter, "refused-detach", arguments(mode="detach-refused", barrier=gate)) as peer:
            peer.request("initialize", dict(adapterID="dncdbg"))
            peer.request("launch", dict(program="owned-test-target"))
            peer.event("initialized")
            pid = peer.event("process")["systemProcessId"]
            peer.request("configurationDone")
            peer.event("stopped")
            peer.request("disconnect", dict(terminateDebuggee=False), success=False)
            time.sleep(0.05)
            check(not marker.exists() and integration["alive"](pid), "Refused detach escalated to a terminating cleanup")
            peer.request("threads")
            check(integration["alive"](pid), "The live session was not preserved after detach refusal")
            peer.request("disconnect", dict(terminateDebuggee=True))
            peer.process.wait(timeout=15)
            integration["exited"](pid)
            pid = None
        print("PASS refused detach stays non-terminating and preserves the session until explicit termination", flush=True)
    finally:
        if pid and integration["alive"](pid):
            os.kill(pid, getattr(signal, "SIGKILL", signal.SIGTERM))
            integration["exited"](pid)
        gate.unlink(missing_ok=True)
        marker.unlink(missing_ok=True)


def reverse_generation(adapter):
    with Dap(adapter, "reverse-generation", arguments()) as peer:
        peer.request("initialize", dict(adapterID="dncdbg", supportsRunInTerminalRequest=True))
        peer.request("reverse")
        old = peer.receive(lambda m: m.get("type") == "request" and m.get("command") == "runInTerminal")
        peer.request("initialize", dict(adapterID="dncdbg", supportsRunInTerminalRequest=True))
        data = json.dumps(dict(seq=999, type="response", request_seq=old["seq"],
                               command="runInTerminal", success=True, body=dict(processId=123))).encode()
        peer.process.stdin.write(f"Content-Length: {len(data)}\r\n\r\n".encode() + data)
        peer.process.stdin.flush()
        time.sleep(0.05)
        check(not peer.request("reverseStatus")["valid"], "An old reverse response was sent into the replacement native backend")
        peer.request("disconnect")
    print("PASS reverse requests are fenced to their original native backend generation", flush=True)


def event_contracts(adapter):
    with Dap(adapter, "native-event-ownership", arguments()) as peer:
        peer.request("initialize", dict(adapterID="dncdbg", supportsInvalidatedEvent=True))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        peer.request("stackTrace", dict(threadId=7))
        peer.request("continue", dict(threadId=7))
        peer.event("stopped")
        new_frame = peer.request("stackTrace", dict(threadId=7))["stackFrames"][0]["id"]
        check(new_frame != 1, "The test must distinguish raw backend and public frame identities")
        peer.request("invalidate")
        invalidated = peer.event("invalidated")
        check("stackFrameId" not in invalidated and set(invalidated["areas"]) >= {"stacks", "variables"},
              "Backend frame identity leaked through invalidation: " + str(invalidated))
        peer.request("capabilityUpdate")
        update = peer.event("capabilities")
        check(update["capabilities"]["supportsCancelRequest"] is False,
              "Backend capabilities re-enabled unsupported proxy cancellation")
        peer.request("disconnect")
    print("PASS native invalidation ownership and capability-update restrictions", flush=True)


def capability_contract(adapter):
    with Dap(adapter, "capability-ownership", arguments()) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("capabilityUpdate")
        update = peer.event("capabilities")
        check(update["capabilities"]["supportsCancelRequest"] is False, "Backend capability update re-enabled unsupported cancellation")
        peer.request("disconnect")
    print("PASS all backend capability updates preserve proxy restrictions", flush=True)


def version_stdin_contract(adapter):
    with Dap(adapter, "version-stdin", arguments("lldb", "version-reads-stdin")) as peer:
        initialized = peer.send("initialize", dict(adapterID="dncdbg"))
        try:
            response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == initialized, timeout=3)
        except queue.Empty:
            raise AssertionError("The CLI version probe inherited and consumed the frontend DAP initialize stream") from None
        check(response["success"], str(response))
        peer.request("disconnect")
    print("PASS version discovery cannot read the frontend's DAP stdin", flush=True)


def selected_frame_contract(adapter):
    with Dap(adapter, "selected-frame-variables", arguments(mode="selected-frame")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        frames = peer.request("stackTrace", dict(threadId=7))["stackFrames"]
        first = peer.request("scopes", dict(frameId=frames[0]["id"]))["scopes"][0]["variablesReference"]
        second = peer.request("scopes", dict(frameId=frames[1]["id"]))["scopes"][0]["variablesReference"]
        check(first != second, "Backend static locals references aliased two different frame owners")
        check(peer.request("variables", dict(variablesReference=first))["variables"][0]["value"] == "1",
              "The backend's selected frame corrupted a prior frame's locals")
        check(peer.request("variables", dict(variablesReference=second))["variables"][0]["value"] == "2",
              "Frame-specific native variable selection was not restored")
        peer.request("disconnect")
    print("PASS native variables retain their owning frame despite backend global selection", flush=True)


def refused_control_contract(adapter):
    with Dap(adapter, "refused-control", arguments("lldb", "refused-control")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg", supportsInvalidatedEvent=True))
        peer.request("launch", dict(program="owned-test-target", managedInspection=True))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        for after in (False, True):
            if after:
                peer.request("continue", dict(threadId=7), success=False)
            request = peer.send("dotnet/managedStackTrace", dict(threadId=7))
            error = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == request)
            check(not error["success"] and "all-threads-stopped" in error["message"],
                  "A refused control request fabricated an all-stop managed snapshot: " + str(error))
        peer.request("disconnect")
    print("PASS refused native controls preserve the prior partial-stop/all-stop state", flush=True)


def uncertain_control_contract(adapter):
    with Dap(adapter, "uncertain-control", arguments("lldb", "timeout-control")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target", managedInspection=True))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        peer.response(peer.send("continue", dict(threadId=7)), success=False, timeout=35)
        request = peer.send("dotnet/managedStackTrace", dict(threadId=7))
        response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == request)
        check(not response["success"] and "not stopped" in response["message"].lower(),
              "A timed-out control has unknown execution state, not a proven refusal: " + str(response))
        peer.request("disconnect")
    print("PASS uncertain control timeout fails closed instead of inventing stopped state", flush=True)


def rejected_restart_preserves_stop(adapter):
    with Dap(adapter, "rejected-restart-stop", arguments()) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        frame = peer.request("stackTrace", dict(threadId=7))["stackFrames"][0]["id"]
        peer.request("restart", success=False)
        peer.request("scopes", dict(frameId=frame))
        peer.request("disconnect")
    print("PASS unsupported restart preserves the unchanged stop and frame handles", flush=True)


def reverse_timeout_contract(adapter):
    with Dap(adapter, "reverse-timeout", arguments("dap")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("reverse")
        peer.receive(lambda m: m.get("type") == "request" and m.get("command") == "runInTerminal")
        time.sleep(31)
        response = peer.request("reverseStatus").get("response")
        check(response and response["success"] is False and response["request_seq"] == 500
              and "timed out" in response["message"].lower(), "Reverse timeout left the originating backend unanswered: " + str(response))
        peer.request("disconnect")
    print("PASS unanswered reverse requests fail explicitly to their originating backend", flush=True)


def startup_timeout_contract(adapter):
    with Dap(adapter, "startup-timeout", arguments("dap", "startup-timeout")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        first = peer.send("launch", dict(program="owned-test-target"))
        response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == first, timeout=35)
        check(response["success"] is False and "startup failed" in response["message"].lower(), str(response))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("disconnect")
    print("PASS failed native startup clears admission state and permits retry", flush=True)


def unsupported_overlay_controls(adapter):
    with Dap(adapter, "unsupported-overlay-controls", arguments("lldb")) as peer:
        caps = peer.request("initialize", dict(adapterID="dncdbg"))
        check(not caps.get("supportsRestartRequest", False), "Unproven proxy restart must not be advertised")
        for command, args in (("attach", dict(pid=123, managedInspection=True)), ("restart", {})):
            request = peer.send(command, args)
            response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == request)
            check(response["success"] is False and "not supported" in response["message"].lower()
                  and command in response["message"].lower(), "Unproven control was not explicitly rejected: " + str(response))
        peer.request("disconnect")
    print("PASS unproven managed attach and proxy restart fail explicitly", flush=True)


def notification_contract(adapter, lldb):
    with Dap(adapter, "managed-invalidation", ["--native-debugger=lldb", "--native-debugger-path=" + lldb]) as peer:
        peer.request("initialize", dict(adapterID="dncdbg", pathFormat="path", linesStartAt1=True,
                                        columnsStartAt1=True, supportsInvalidatedEvent=True))
        launch = peer.send("launch", dict(program=shutil.which("dotnet"), args=[str(DLL)],
                                          env=["DNCDBG_MIXED_CHECKPOINTS=1"], managedInspection=True))
        peer.event("initialized")
        peer.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                           breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_CHECKPOINT"))]))
        peer.request("configurationDone")
        peer.response(launch)
        stop = peer.event("stopped")
        frames = peer.request("stackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
        managed = next(f for f in frames if "[Managed]" in f["name"] and "ManagedCall" in f["name"])
        peer.request("evaluate", dict(frameId=frames[0]["id"], expression="phase + result", context="watch"))
        try:
            message = peer.receive(lambda m: m.get("event") == "invalidated", timeout=2)
        except queue.Empty:
            raise AssertionError("Native evaluation expired managed frame handles without an invalidation notification") from None
        check(set(message["body"]["areas"]) >= {"stacks", "variables"}, str(message))
        peer.request("scopes", dict(frameId=managed["id"]), success=False)
        refreshed = peer.request("stackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
        current = next(f for f in refreshed if "[Managed]" in f["name"] and "ManagedCall" in f["name"])
        peer.request("scopes", dict(frameId=current["id"]))
        peer.request("disconnect", dict(terminateDebuggee=True))
    print("PASS native mutation invalidation is visible and refreshes managed frame ownership", flush=True)


def strict_managed_contract(adapter, lldb, mode=None):
    backend = arguments("lldb", mode, lldb=lldb) if mode else ["--native-debugger=lldb", "--native-debugger-path=" + lldb]
    with Dap(adapter, "strict-managed-identities" + ("-" + mode if mode else ""), backend) as peer:
        peer.request("initialize", dict(adapterID="dncdbg", pathFormat="path", linesStartAt1=True,
                                        columnsStartAt1=True, supportsInvalidatedEvent=True))
        launch = peer.send("launch", dict(program=shutil.which("dotnet"), args=[str(DLL)],
                                          env=["DNCDBG_MIXED_CHECKPOINTS=1"], managedInspection=True))
        peer.event("initialized")
        peer.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                           breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_CHECKPOINT"))]))
        peer.request("configurationDone")
        peer.response(launch)
        stop = peer.event("stopped")
        peer.request("stackTrace", dict(threadId=stop["threadId"]))
        frames = peer.request("dotnet/managedStackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
        caller = next(f.get("frame", f) for f in frames if "ManagedCall" in f.get("frame", f)["name"])
        _, values = integration["locals_for"](peer, caller["id"])
        check(values.get("left") == "19" and values.get("right") == "23",
              "Strict managed stack leaked inspection IDs into the native frame namespace: " + str(values))
        peer.request("disconnect", dict(terminateDebuggee=True))
    print("PASS strict managed stack returns owned frame references usable by scopes/variables" + (" (" + mode + ")" if mode else ""), flush=True)


def real_restart_contract(adapter, lldb):
    with Dap(adapter, "real-native-restart", ["--native-debugger=lldb", "--native-debugger-path=" + lldb]) as peer:
        caps = peer.request("initialize", dict(adapterID="dncdbg", pathFormat="path", linesStartAt1=True,
                                               columnsStartAt1=True, supportsInvalidatedEvent=True))
        check(caps.get("supportsRestartRequest"), "Selected LLDB does not advertise restart")
        launch = peer.send("launch", dict(program=shutil.which("dotnet"), args=[str(DLL)],
                                          env=["DNCDBG_MIXED_CHECKPOINTS=1"], managedInspection=True))
        peer.event("initialized")
        peer.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                           breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_CHECKPOINT"))]))
        peer.request("configurationDone")
        peer.response(launch)
        stopped = peer.event("stopped")
        before = peer.request("stackTrace", dict(threadId=stopped["threadId"]))["stackFrames"]
        old = next(f["id"] for f in before if "[Managed]" in f["name"] and "ManagedCall" in f["name"])
        pid = peer.request("dotnet/info")["debuggeeProcessId"]
        peer.request("restart", {})
        stopped = peer.event("stopped")
        after = peer.request("stackTrace", dict(threadId=stopped["threadId"]))["stackFrames"]
        check(any("[Managed]" in f["name"] and "ManagedCall" in f["name"] for f in after), "Restart lost the read-only overlay")
        peer.request("scopes", dict(frameId=old), success=False)
        integration["exited"](pid)
        peer.request("disconnect", dict(terminateDebuggee=True))
    print("PASS real LLDB restart refreshes target, snapshot and client references", flush=True)


def real_attach_contract(adapter, lldb):
    release = OUT / "native-attach.release"
    release.unlink(missing_ok=True)
    env = dict(os.environ, DNCDBG_MIXED_CHECKPOINTS="1")
    with (OUT / "native-attach-target.log").open("w") as output:
        child = subprocess.Popen([shutil.which("dotnet"), str(DLL), "--preload", "--wait-file", str(release)],
                                 stdout=output, stderr=output, env=env)
        try:
            deadline = time.monotonic() + 10
            log = OUT / "native-attach-target.log"
            while "WAIT:" not in log.read_text():
                check(child.poll() is None and time.monotonic() < deadline, "Attach fixture did not initialize")
                time.sleep(0.01)
            with Dap(adapter, "real-native-attach", ["--native-debugger=lldb", "--native-debugger-path=" + lldb]) as peer:
                peer.request("initialize", dict(adapterID="dncdbg", pathFormat="path", linesStartAt1=True,
                                                columnsStartAt1=True, supportsInvalidatedEvent=True))
                attached = peer.send("attach", dict(pid=child.pid, stopOnEntry=True, managedInspection=True))
                peer.event("initialized")
                peer.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                                   breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_CHECKPOINT"))]))
                peer.request("configurationDone")
                peer.response(attached)
                stopped = peer.event("stopped")
                release.write_text("release")
                peer.request("continue", dict(threadId=stopped["threadId"]))
                stopped = peer.receive(lambda m: m.get("event") == "stopped", timeout=5)["body"]
                frames = peer.request("stackTrace", dict(threadId=stopped["threadId"]))["stackFrames"]
                check(any("[Managed]" in f["name"] and "ManagedCall" in f["name"] for f in frames), "Attach could not inspect the managed caller")
                peer.request("disconnect", dict(terminateDebuggee=True))
            child.wait(timeout=10)
        finally:
            if child.poll() is None:
                child.kill()
                child.wait(timeout=5)
            release.unlink(missing_ok=True)
    print("PASS real LLDB attach and sole-owner CoreCLR inspection", flush=True)


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
                                            env=["DNCDBG_MIXED_CHECKPOINTS=1"], managedInspection=True))
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
        if not response["success"]:
            peer.process.stdin.close()
        peer.process.wait(timeout=15)
    barrier.with_suffix(".arm").unlink(missing_ok=True)
    barrier.with_suffix(".ready").unlink(missing_ok=True)
    print(f"PASS interrupted managed read: {mode}", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adapter", required=True)
    parser.add_argument("--lldb-dap")
    parser.add_argument("--attach-restart-proof", action="store_true",
                        help="Run the still-unproven real attach/restart gates; failures are not skipped")
    options = parser.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    fake_contracts(options.adapter)
    refused_detach(options.adapter)
    reverse_generation(options.adapter)
    event_contracts(options.adapter)
    capability_contract(options.adapter)
    version_stdin_contract(options.adapter)
    selected_frame_contract(options.adapter)
    refused_control_contract(options.adapter)
    uncertain_control_contract(options.adapter)
    rejected_restart_preserves_stop(options.adapter)
    reverse_timeout_contract(options.adapter)
    startup_timeout_contract(options.adapter)
    unsupported_overlay_controls(options.adapter)
    bundle_contracts(options.adapter)
    detached_backend_stall(options.adapter)
    if options.lldb_dap:
        build()
        strict_managed_contract(options.adapter, options.lldb_dap)
        strict_managed_contract(options.adapter, options.lldb_dap, "decimal-modules")
        if options.attach_restart_proof:
            real_restart_contract(options.adapter, options.lldb_dap)
            real_attach_contract(options.adapter, options.lldb_dap)
        notification_contract(options.adapter, options.lldb_dap)
        for poison in (False, True):
            invalid_runtime(options.adapter, options.lldb_dap, poison)
        for mode in ("hold", "exit-during-read", "lost-server"):
            interrupted_read(options.adapter, options.lldb_dap, mode)
