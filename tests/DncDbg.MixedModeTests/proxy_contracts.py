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


def bundle_links_contract(adapter, trailing=True):
    root = OUT / "bundle-links"
    root.mkdir(exist_ok=True)
    executable = root / "adapter"
    license_file = root / "LICENSE.TXT"
    for item in (executable, license_file, root / "bin"):
        if item.is_symlink():
            item.unlink()
    executable.write_text("#!/bin/sh\nexit 0\n")
    executable.chmod(0o755)
    license_file.write_text("LLVM test manifest only; no debugger binaries redistributed.")
    info = subprocess.run([adapter, "--buildinfo"], capture_output=True, text=True, check=True).stdout
    os_name = next(s.split(":", 1)[1].strip() for s in info.splitlines() if "Target OS:" in s)
    arch = next(s.split(":", 1)[1].strip() for s in info.splitlines() if "Target arch:" in s)
    manifest = dict(provider="llvm", licenseExpression="Apache-2.0 WITH LLVM-exception",
                    rid=os_name + "-" + arch, version="0.0.0", executable="adapter", licenseFile="LICENSE.TXT")
    (root / "native-adapter.json").write_text(json.dumps(manifest))
    command = [adapter, "--native-debugger=dap", "--native-debugger-info"]
    if trailing:
        result = subprocess.run([*command, "--native-debugger-path=" + str(root) + os.sep], capture_output=True, text=True)
        check(result.returncode == 0, "Valid bundle with trailing separator was rejected: " + result.stderr)
    outside = OUT / "outside-bundle"
    outside.mkdir(exist_ok=True)
    (outside / "adapter").write_text("#!/bin/sh\nexit 0\n")
    (outside / "adapter").chmod(0o755)
    (outside / "notice").write_text("Outside manifest boundary")
    for key, path, target, relative in (
            ("executable", executable, outside / "adapter", "adapter"),
            ("licenseFile", license_file, outside / "notice", "LICENSE.TXT"),
            ("executable", root / "bin", outside, "bin/adapter")):
        if path.is_file():
            path.unlink()
        try:
            path.symlink_to(target, target_is_directory=target.is_dir())
        except OSError as error:
            check(sys.platform == "win32" and error.winerror == 1314, str(error))
            print("SKIP bundle symlink creation requires host privilege", flush=True)
            return
        try:
            changed = dict(manifest, **{key: relative})
            (root / "native-adapter.json").write_text(json.dumps(changed))
            result = subprocess.run([*command, "--native-debugger-path=" + str(root)], capture_output=True, text=True)
            check(result.returncode == 1 and "inside the bundle" in result.stderr.lower(),
                  "Symlinked " + key + " escaped the bundle: " + result.stdout + result.stderr)
        finally:
            path.unlink()
            if path == executable:
                executable.write_text("#!/bin/sh\nexit 0\n")
                executable.chmod(0o755)
            if path == license_file:
                license_file.write_text("Inside bundle")
    print("PASS bundle canonical path validation covers trailing separators, file links and ancestor links", flush=True)


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
        peer.request("configurationDone")
        response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == first, timeout=35)
        check(response["success"] is False and "startup failed" in response["message"].lower(), str(response))
        peer.request("launch", dict(program="owned-test-target"), success=False)
        check(peer.request("ownershipStatus")["launches"] == 1, "Unknown startup outcome admitted another launch")
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("disconnect")
    with Dap(adapter, "startup-explicit-failure", arguments("dap", "startup-failure")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target"), success=False)
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("disconnect")
    print("PASS unknown startup requires reconciliation; explicit startup failure permits retry", flush=True)


def startup_evidence_contract(adapter):
    with Dap(adapter, "startup-evidence", arguments("dap", "startup-evidence")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        first = peer.send("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.event("stopped")
        peer.request("configurationDone")
        peer.response(first, success=False, timeout=35)
        retry = peer.send("launch", dict(program="second-test-target"))
        response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == retry)
        check(not response["success"], "Startup timeout admitted a second launch despite process/all-stop evidence: " + str(response))
        check(peer.request("ownershipStatus")["launches"] == 1, "Second startup reached the backend")
        peer.request("disconnect")
    print("PASS startup timeout with target evidence preserves admission until reconciliation", flush=True)


def startup_configuration_contract(adapter):
    with Dap(adapter, "startup-configuration", arguments("dap", "startup-config")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        launch = peer.send("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        try:
            response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == launch, timeout=31)
            raise AssertionError("Launch deadline expired before configurationDone: " + str(response))
        except queue.Empty:
            pass
        peer.request("configurationDone")
        peer.response(launch)
        peer.request("disconnect")
    print("PASS startup response deadline begins at configurationDone, not preconfiguration setup", flush=True)


def startup_without_configuration_contract(adapter):
    with Dap(adapter, "startup-no-configuration", arguments("dap", "startup-no-configuration")) as peer:
        caps = peer.request("initialize", dict(adapterID="dncdbg"))
        check(not caps["supportsConfigurationDoneRequest"], "Test backend unexpectedly supports configurationDone")
        request = peer.send("launch", dict(program="owned-test-target"))
        try:
            peer.response(request, success=False, timeout=35)
        except queue.Empty:
            raise AssertionError("A backend without configurationDone has no bounded startup deadline") from None
        peer.request("disconnect")
    print("PASS backend without configurationDone retains a bounded startup response deadline", flush=True)


def disconnect_timeout_contract(adapter):
    with Dap(adapter, "disconnect-timeout", arguments("lldb", "disconnect-timeout")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target", managedInspection=True))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        peer.response(peer.send("disconnect", dict(terminateDebuggee=False)), success=False, timeout=35)
        request = peer.send("dotnet/managedStackTrace", dict(threadId=7))
        response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == request)
        check(not response["success"] and "not stopped" in response["message"].lower(),
              "Unanswered disconnect restored inspection without a fresh stop: " + str(response))
        status = peer.request("ownershipStatus")
        check(status["moduleReads"] == 0 and status["disconnects"] == [False],
              "Unknown disconnect accessed modules/DAC or changed nontermination intent: " + str(status))
        peer.request("disconnect")
    print("PASS unanswered disconnect rejects inspection before module/DAC reads and keeps detach intent", flush=True)


def dead_backend_disconnect_contract(adapter):
    gate = OUT / "dead-backend-target-release"
    gate.unlink(missing_ok=True)
    pid = None
    try:
        with Dap(adapter, "dead-backend-disconnect", arguments("dap", "dead-disconnect", barrier=gate)) as peer:
            peer.request("initialize", dict(adapterID="dncdbg"))
            peer.request("launch", dict(program="owned-test-target"))
            peer.event("initialized")
            pid = peer.event("process")["systemProcessId"]
            peer.request("die")
            peer.event("terminated")
            peer.request("disconnect", dict(terminateDebuggee=False), success=False)
            peer.process.stdin.close()
            peer.disconnected = True
            peer.process.wait(timeout=15)
            check(peer.process.returncode == 0 and integration["alive"](pid), "Dead-backend nonterminating disconnect escalated target ownership")
            gate.write_text("release")
            integration["exited"](pid)
            pid = None
    finally:
        gate.write_text("release")
        if pid is not None and integration["alive"](pid):
            os.kill(pid, getattr(signal, "SIGKILL", signal.SIGTERM))
        gate.unlink(missing_ok=True)
    print("PASS disconnect with a dead backend preserves nontermination and the actual child", flush=True)


def blocked_write_contract(adapter, eof=False):
    gate = OUT / ("blocked-write-eof" if eof else "blocked-write-timeout")
    gate.unlink(missing_ok=True)
    ready = gate.with_suffix(".ready")
    ready.unlink(missing_ok=True)
    with Dap(adapter, gate.name, arguments("dap", "blocked-writes", barrier=gate)) as peer:
        try:
            peer.request("initialize", dict(adapterID="dncdbg"))
            request = peer.send("pipeFill", dict(payload="x" * (8 * 1024 * 1024)))
            wait_file(ready)
            if eof:
                peer.process.stdin.close()
                peer.disconnected = True
                try:
                    peer.process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    raise AssertionError("Frontend EOF did not abort a pipe-filled backend write before worker join") from None
                check(peer.process.returncode == 0, "Blocked-write EOF teardown failed: " + str(peer.process.returncode))
            else:
                try:
                    peer.response(request, success=False, timeout=35)
                except queue.Empty:
                    raise AssertionError("Pipe-filled backend write exceeded the 30-second native request deadline") from None
                peer.process.stdin.close()
                peer.disconnected = True
                peer.process.wait(timeout=15)
                check(peer.process.returncode == 0, "Write-timeout teardown failed")
            check(not integration["alive"](int(ready.read_text())), "Unread backend survived transport abort")
        finally:
            # Release only the test peer so red runs do not strand adapter processes.
            gate.write_text("release")
    gate.unlink(missing_ok=True)
    ready.unlink(missing_ok=True)
    print("PASS blocked backend writes: " + ("frontend EOF aborts before worker join" if eof else "request deadline is bounded"), flush=True)


def inspection_capability_contract(adapter, modes=("no-memory", "malformed-module")):
    for mode in modes:
        with Dap(adapter, "inspection-" + mode, arguments("lldb", mode)) as peer:
            peer.request("initialize", dict(adapterID="dncdbg"))
            peer.request("launch", dict(program="owned-test-target", managedInspection=True))
            peer.event("initialized")
            peer.request("configurationDone")
            peer.event("stopped")
            body = peer.request("stackTrace", dict(threadId=7))
            check(body["stackFrames"][0]["name"] == "native", "Overlay failure lost native stack")
            status = body["dncdbgManagedInspection"]
            check(status["status"] == "unavailable", str(body))
            if mode == "no-memory":
                check("supportsReadMemoryRequest" in status["message"]
                      and peer.request("ownershipStatus")["moduleReads"] == 0, "Unsupported memory inspection reached module/DAC reads: " + str(status))
            peer.request("disconnect")
    print("PASS unsupported memory and malformed managed metadata degrade to explicit native-only stacks", flush=True)


def wide_reference_contract(adapter):
    with Dap(adapter, "wide-backend-references", arguments("dap", "wide-ids")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        frame = peer.request("stackTrace", dict(threadId=7))["stackFrames"][0]
        check(0 < frame["id"] < 1 << 31, "Client frame ID is not a proxy int")
        scope = peer.request("scopes", dict(frameId=frame["id"]))["scopes"][0]
        variable = peer.request("variables", dict(variablesReference=scope["variablesReference"]))["variables"][0]
        peer.request("variables", dict(variablesReference=variable["variablesReference"]))
        check(peer.request("source", dict(sourceReference=frame["source"]["sourceReference"]))["content"] == "native source",
              "64-bit backend source ID was truncated")
        peer.request("evaluate", dict(frameId=frame["id"], expression="value", context="hover"))
        peer.request("disconnect")
    print("PASS 64-bit native frame/variable/source identities stay opaque behind client int IDs", flush=True)


def refused_admission_contract(adapter):
    with Dap(adapter, "refused-admission", arguments("dap", "refused-admission")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        frame = peer.request("stackTrace", dict(threadId=7))["stackFrames"][0]
        scope = peer.request("scopes", dict(frameId=frame["id"]))["scopes"][0]
        for command, args in (("launch", dict(program="duplicate")), ("attach", dict(pid=123)),
                              ("continue", dict(threadId=7)), ("next", dict(threadId=7))):
            peer.request(command, args, success=False)
            peer.request("scopes", dict(frameId=frame["id"]))
            peer.request("variables", dict(variablesReference=scope["variablesReference"]))
        peer.request("disconnect")
    print("PASS rejected startup and refused native control preserve the live stop and references", flush=True)


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
        peer.request("evaluate", dict(frameId=frames[0]["id"], expression="phase + result", context="repl"))
        try:
            message = peer.receive(lambda m: m.get("event") == "invalidated", timeout=2)
        except queue.Empty:
            raise AssertionError("Native evaluation expired managed frame handles without an invalidation notification") from None
        check(set(message["body"]["areas"]) >= {"stacks", "variables"}, str(message))
        peer.request("scopes", dict(frameId=managed["id"]), success=False)
        refreshed = peer.request("stackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
        current = next(f for f in refreshed if "[Managed]" in f["name"] and "ManagedCall" in f["name"])
        peer.request("scopes", dict(frameId=current["id"]))
        peer.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                           breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_CHECKPOINT"))]))
        changed = peer.event("invalidated")
        check(set(changed["areas"]) >= {"stacks", "variables"}, "Breakpoint mutation failed to notify managed-view invalidation")
        peer.request("scopes", dict(frameId=current["id"]), success=False)
        updated = peer.request("stackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
        current = next(f for f in updated if "[Managed]" in f["name"] and "ManagedCall" in f["name"])
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


def native_watch_contract(adapter, lldb, invalidation=False):
    with Dap(adapter, "native-watch-" + str(invalidation), ["--native-debugger=lldb", "--native-debugger-path=" + lldb]) as peer:
        peer.request("initialize", dict(adapterID="dncdbg", supportsInvalidatedEvent=invalidation))
        launch = peer.send("launch", dict(program=shutil.which("dotnet"), args=[str(DLL)],
                                          env=["DNCDBG_MIXED_CHECKPOINTS=1"], managedInspection=True))
        peer.event("initialized")
        peer.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                           breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_CHECKPOINT"))]))
        peer.request("configurationDone")
        peer.response(launch)
        stop = peer.event("stopped")
        frames = peer.request("stackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
        managed = next(f for f in frames if "ManagedCall" in f["name"] and "[Managed]" in f["name"])
        for context in ("watch", "hover"):
            peer.request("evaluate", dict(frameId=frames[0]["id"], expression="phase", context=context))
            peer.request("scopes", dict(frameId=managed["id"]))
        try:
            message = peer.receive(lambda m: m.get("event") == "invalidated", timeout=0.25)
            raise AssertionError("Read-only native watch/hover caused an IDE invalidation refresh loop: " + str(message))
        except queue.Empty:
            pass
        peer.request("disconnect", dict(terminateDebuggee=True))
    print("PASS native watch/hover preserves managed handles without unsolicited invalidation; client=" + str(invalidation), flush=True)


def frameless_evaluation_contract(adapter, mode="selected-frame"):
    with Dap(adapter, "frameless-native-evaluation-" + mode, arguments("dap", mode)) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        frames = peer.request("stackTrace", dict(threadId=7))["stackFrames"]
        peer.request("scopes", dict(frameId=frames[0]["id"]))
        peer.request("selectOtherFrame")
        check(peer.request("evaluate", dict(expression="value", context="repl" if mode == "selected-repl" else "hover"))["result"] == "1",
              "Frame-less evaluate observed internal/backend frame selection instead of client frame")
        peer.request("disconnect")
    print("PASS frame-less evaluation restores the client-selected native frame", flush=True)


def inspection_stop_publication_contract(adapter, command="variables"):
    with Dap(adapter, "inspection-stop-publication-" + command, arguments("dap", "transition-" + command)) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        frame = peer.request("stackTrace", dict(threadId=7))["stackFrames"][0]["id"]
        generation = peer.request("dotnet/info")["stopGeneration"]
        args = dict(frameId=frame)
        if command == "variables":
            scope = peer.request("scopes", args)["scopes"][0]["variablesReference"]
            args = dict(variablesReference=scope)
        request = peer.send(command, args)
        peer.event("continued")
        peer.event("stopped")
        response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == request)
        current = peer.request("dotnet/info")["stopGeneration"]
        check(current > generation, "Test peer failed to advance the native stop")
        check(not response["success"] and "cancel" in response.get("message", "").lower() and not response.get("body", {}).get(command),
              "Old " + command + " response published across stop " + str(generation) + " -> " + str(current)
              + ": success=" + str(response["success"]))
        peer.request("scopes", dict(frameId=frame), success=False)
        current_frame = peer.request("stackTrace", dict(threadId=7))["stackFrames"][0]["id"]
        scope = peer.request("scopes", dict(frameId=current_frame))["scopes"][0]["variablesReference"]
        value = peer.request("variables", dict(variablesReference=scope))["variables"][0]
        check(peer.request("variables", dict(variablesReference=value["variablesReference"]))["variables"][0]["value"] == "42",
              "Fresh stop inspection failed after rejecting the expired response")
        peer.request("disconnect")
    print("PASS native " + command + " publication rejects old values and child handles across a stop transition", flush=True)


def explicit_repl_contract(adapter):
    with Dap(adapter, "explicit-native-repl-selection", arguments("dap", "selected-repl")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        frame = peer.request("stackTrace", dict(threadId=7))["stackFrames"][0]["id"]
        peer.request("scopes", dict(frameId=frame))
        peer.request("selectOtherFrame")
        result = peer.request("evaluate", dict(frameId=frame, expression="`frame info", context="repl"))
        check(result["result"] == "1", "Explicit-frame REPL observed backend frame " + result["result"] + " instead of client frame 1")
        peer.request("disconnect")
    print("PASS explicit native REPL restores actual backend selection before global-state commands", flush=True)


def real_explicit_repl_contract(adapter, lldb):
    with Dap(adapter, "real-explicit-repl-selection", ["--native-debugger=lldb", "--native-debugger-path=" + lldb]) as peer:
        peer.request("initialize", dict(adapterID="dncdbg", supportsInvalidatedEvent=True))
        launch = peer.send("launch", dict(program=shutil.which("dotnet"), args=[str(DLL)],
                                          env=["DNCDBG_MIXED_CHECKPOINTS=1"], managedInspection=True))
        peer.event("initialized")
        peer.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                           breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_BREAK"))]))
        peer.request("configurationDone")
        peer.response(launch)
        stop = peer.event("stopped")
        raw = peer.request("dotnet/nativeStackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
        caller = raw[1]
        check("native_add" in caller["name"], "Fixture did not expose native caller frame #1")
        peer.request("scopes", dict(frameId=caller["id"]))
        enriched = peer.request("stackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
        check(any("[Managed]" in f["name"] for f in enriched), "Real explicit-frame REPL proof did not open a managed snapshot")
        result = peer.request("evaluate", dict(frameId=caller["id"], expression="`frame info", context="repl"))["result"]
        check("frame #1:" in result and "native_add" in result,
              "Real overlay explicit-frame REPL returned another selected frame: " + result)
        peer.request("disconnect", dict(terminateDebuggee=True))
    print("PASS real LLDB explicit caller-frame REPL survives internal overlay register selection", flush=True)


def startup_deadline_gaps_contract(adapter, mode="startup-preinitialized"):
    with Dap(adapter, mode, arguments("dap", mode)) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        launch = peer.send("launch", dict(program="owned-test-target"))
        if mode == "startup-postconfig-retry":
            peer.event("initialized")
            peer.request("configurationDone")
            peer.response(launch, success=False)
            launch = peer.send("launch", dict(program="retry-test-target"))
            peer.event("initialized")
            peer.request("configurationDone")
        try:
            peer.response(launch, success=False, timeout=35)
        except queue.Empty:
            raise AssertionError("No bounded startup deadline for " + mode) from None
        peer.request("disconnect")
    print("PASS bounded startup deadline: " + mode, flush=True)


def startup_failed_evidence_contract(adapter):
    with Dap(adapter, "startup-failed-evidence", arguments("dap", "startup-failed-evidence")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target"), success=False)
        peer.event("stopped")
        request = peer.send("launch", dict(program="second-test-target"))
        response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == request)
        check(not response["success"] and peer.request("ownershipStatus")["launches"] == 1,
              "Explicit failed launch discarded observed process/stopped ownership")
        peer.request("disconnect")
    print("PASS explicit startup failure retains admission after actual target evidence", flush=True)


def disconnect_blocked_startup_contract(adapter):
    gate = OUT / "disconnect-blocked-launch"
    gate.unlink(missing_ok=True)
    gate.with_suffix(".ready").unlink(missing_ok=True)
    with Dap(adapter, gate.name, arguments("dap", "blocked-writes", barrier=gate)) as peer:
        try:
            peer.request("initialize", dict(adapterID="dncdbg"))
            launch = peer.send("launch", dict(program="owned-test-target", payload="x" * (8 * 1024 * 1024)))
            wait_file(gate.with_suffix(".ready"))
            disconnect = peer.send("disconnect", dict(terminateDebuggee=False))
            try:
                peer.response(disconnect, success=False, timeout=5)
            except queue.Empty:
                raise AssertionError("Disconnect did not cancel blocked launch transport ownership") from None
            peer.response(launch, success=False, timeout=5)
            peer.process.stdin.close()
            peer.disconnected = True
            peer.process.wait(timeout=15)
            check(peer.process.returncode == 0, "Blocked-launch disconnect shutdown failed")
        finally:
            gate.write_text("release")
    gate.unlink(missing_ok=True)
    gate.with_suffix(".ready").unlink(missing_ok=True)
    print("PASS disconnect cancels blocked startup writes before queuing its own request", flush=True)


def wide_thread_contract(adapter):
    with Dap(adapter, "native-wide-threads", arguments("dap", "wide-threads")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("configurationDone")
        stop = peer.event("stopped")
        tid = (1 << 40) + 7
        check(stop["threadId"] == tid and peer.request("threads")["threads"][0]["id"] == tid, "Native thread event identity lost")
        frame = peer.request("stackTrace", dict(threadId=tid))["stackFrames"][0]
        peer.request("scopes", dict(frameId=frame["id"]))
        peer.request("disconnect")
    print("PASS native-only 64-bit thread IDs round-trip without CLR API narrowing", flush=True)


def uncertain_mutation_contract(adapter, lldb, mode="mutation-failure", command="evaluate"):
    import base64
    import re
    gate = OUT / (mode + "-" + command)
    for suffix in (".arm", ".ready"):
        gate.with_suffix(suffix).unlink(missing_ok=True)
    with Dap(adapter, gate.name, arguments("lldb", mode, lldb=lldb, barrier=gate)) as peer:
        peer.request("initialize", dict(adapterID="dncdbg", supportsInvalidatedEvent=True))
        launch = peer.send("launch", dict(program=shutil.which("dotnet"), args=[str(DLL)],
                                          env=["DNCDBG_MIXED_CHECKPOINTS=1"], managedInspection=True))
        peer.event("initialized")
        peer.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                           breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_CHECKPOINT"))]))
        peer.request("configurationDone")
        peer.response(launch)
        stop = peer.event("stopped")
        raw = peer.request("dotnet/nativeStackTrace", dict(threadId=stop["threadId"]))["stackFrames"][0]
        if command == "writeMemory":
            address = peer.request("evaluate", dict(frameId=raw["id"], expression="&phase", context="repl"))["result"]
            pointer = re.findall(r"0x[0-9a-fA-F]+", address)[-1]
            args = dict(memoryReference=pointer, data=base64.b64encode((9).to_bytes(4, "little")).decode())
        else:
            args = dict(frameId=raw["id"], expression="phase = 9", context="repl")
        frames = peer.request("stackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
        managed = next(f for f in frames if "[Managed]" in f["name"] and "ManagedCall" in f["name"])
        serial = peer.request("dotnet/info")["managedSnapshot"]
        gate.with_suffix(".arm").write_text("arm")
        peer.response(peer.send(command, args), success=False, timeout=35)
        wait_file(gate.with_suffix(".ready"))
        request = peer.send("scopes", dict(frameId=managed["id"]))
        response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == request)
        check(not response["success"], "Failed-after-side-effect " + command + " retained a cached managed snapshot")
        updated = peer.request("stackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
        check(peer.request("dotnet/info")["managedSnapshot"] > serial, "Unknown native mutation reused the CLR view")
        _, locals = integration["locals_for"](peer, updated[0]["id"])
        check(locals["phase"] == "9", "Test mutation did not actually change native memory: " + str(locals))
        peer.request("disconnect", dict(terminateDebuggee=True))
    for suffix in (".arm", ".ready"):
        gate.with_suffix(suffix).unlink(missing_ok=True)
    print("PASS managed snapshot expires before failed/timed-out native mutation: " + mode + "/" + command, flush=True)


def nonmutating_queries_contract(adapter, lldb):
    with Dap(adapter, "nonmutating-native-queries", ["--native-debugger=lldb", "--native-debugger-path=" + lldb]) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
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
        for command, args in (("completions", dict(frameId=frames[0]["id"], text="pha", column=4)),
                              ("dataBreakpointInfo", dict(frameId=frames[0]["id"], name="phase"))):
            seq = peer.send(command, args)
            response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == seq)
            check("Native mutation" not in response.get("message", ""), command + " was incorrectly treated as mutation")
            peer.request("scopes", dict(frameId=managed["id"]))
        peer.request("disconnect", dict(terminateDebuggee=True))
    print("PASS completion/data-breakpoint queries preserve the live managed view without invalidation support", flush=True)


def client_backpressure_contract(adapter, command="variables", finish="eof"):
    name = "client-backpressure-" + command + "-" + finish
    with (OUT / (name + ".stderr")).open("wb") as errors:
        gate = OUT / name
        gate.unlink(missing_ok=True)
        mode = "client-block-transition" if finish == "module" else "client-block-" + command
        process = subprocess.Popen([adapter, *arguments("dap", mode, barrier=gate)],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors)
        seq, backend, target_pid = 0, None, None
        def send(command, args=None):
            nonlocal seq
            seq += 1
            payload = json.dumps(dict(seq=seq, type="request", command=command, arguments=args or {})).encode()
            process.stdin.write(f"Content-Length: {len(payload)}\r\n\r\n".encode() + payload)
            process.stdin.flush()
            return seq
        def header():
            length = None
            while True:
                text = process.stdout.readline()
                check(text, "Client backpressure adapter exited before publication")
                if text == b"\r\n":
                    return length
                if text.lower().startswith(b"content-length:"):
                    length = int(text.split(b":", 1)[1])
        def request(command, args=None):
            target = send(command, args)
            while True:
                message = json.loads(process.stdout.read(header()))
                if message.get("request_seq") == target:
                    check(message["success"], str(message))
                    return message.get("body", {})
        try:
            request("initialize", dict(adapterID="dncdbg"))
            request("launch", dict(program="owned-test-target"))
            request("configurationDone")
            info = request("dotnet/info")
            backend, target_pid = info["nativeAdapterProcessId"], info["debuggeeProcessId"]
            check(isinstance(target_pid, int), "Backpressure test did not own a real child target")
            frame = request("stackTrace", dict(threadId=7))["stackFrames"][0]["id"]
            args = dict(frameId=frame)
            if command == "variables":
                args = dict(variablesReference=request("scopes", args)["scopes"][0]["variablesReference"])
            target = send(command, args)
            length = header()
            while length <= 1000000:
                message = json.loads(process.stdout.read(length))
                check(command == "reverse" and message.get("type") in ("event", "response"),
                      "Response is not large enough to fill a client pipe: " + str(message))
                length = header()
            check(length > 1000000, "Response is not large enough to fill a client pipe")
            check(process.stdout.read(1) == b"{", "The large response write was not admitted")
            if finish == "eof":
                process.stdin.close()
            elif finish == "disconnect":
                send("disconnect", dict(terminateDebuggee=True))
            elif finish == "module":
                gate.write_text("module invalidation")
            elif finish != "deadline":
                raise AssertionError("Unknown output backpressure mode")
            try:
                process.wait(timeout=36 if command == "reverse" else 15)
            except subprocess.TimeoutExpired:
                raise AssertionError("Client backpressure held state/teardown during " + command + "/" + finish) from None
            check(process.returncode == 0, "Client backpressure shutdown failed: " + str(process.returncode))
            check(not integration["alive"](backend), "Client backpressure stranded the native adapter")
            integration["exited"](target_pid)
        finally:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
            process.stdout.close()
            gate.unlink(missing_ok=True)
            if backend and integration["alive"](backend):
                os.kill(backend, getattr(signal, "SIGKILL", signal.SIGTERM))
            if target_pid and integration["alive"](target_pid):
                os.kill(target_pid, getattr(signal, "SIGKILL", signal.SIGTERM))
    print("PASS full client pipe permits bounded " + command + "/" + finish + " shutdown", flush=True)


def poisoned_disconnect_contract(adapter):
    gate = OUT / "poisoned-backend-disconnect"
    gate.unlink(missing_ok=True)
    gate.with_suffix(".ready").unlink(missing_ok=True)
    with Dap(adapter, gate.name, arguments("dap", "blocked-writes", barrier=gate)) as peer:
        try:
            peer.request("initialize", dict(adapterID="dncdbg"))
            request = peer.send("pipeFill", dict(payload="x" * (8 * 1024 * 1024)))
            wait_file(gate.with_suffix(".ready"))
            peer.response(request, success=False, timeout=35)
            disconnect = peer.send("disconnect", dict(terminateDebuggee=False))
            response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == disconnect)
            check(not response["success"] and "timed out" not in response["message"].lower(),
                  "Poisoned transport was reported as a new request timeout: " + str(response))
            peer.disconnected = True
            peer.process.wait(timeout=15)
            check(peer.process.returncode == 0, "Poisoned disconnect requires frontend EOF to clean up")
        finally:
            gate.write_text("release")
    gate.unlink(missing_ok=True)
    gate.with_suffix(".ready").unlink(missing_ok=True)
    print("PASS poisoned backend disconnect reports transport loss and exits without frontend EOF", flush=True)


def late_startup_evidence_contract(adapter):
    with Dap(adapter, "startup-late-evidence", arguments("dap", "startup-late-evidence")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target"), success=False)
        peer.request("lateEvidence")
        peer.event("stopped")
        peer.request("launch", dict(program="second-test-target"), success=False)
        check(peer.request("ownershipStatus")["launches"] == 1, "Late target evidence failed to reserve startup ownership")
        peer.request("disconnect")
    print("PASS process/stopped after a failed launch re-reserves target ownership", flush=True)


def fresh_configuration_contract(adapter):
    with Dap(adapter, "fresh-configuration", arguments("dap", "fresh-configuration")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        launch = peer.send("launch", dict(program="first-test-target"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.response(launch, success=False)
        launch = peer.send("launch", dict(program="retry-test-target"))
        peer.event("initialized")
        try:
            message = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == launch, timeout=31)
            raise AssertionError("Retry inherited prior configurationDone instead of a fresh setup phase: " + str(message))
        except queue.Empty:
            pass
        peer.request("disconnect")
        peer.response(launch, success=False)
    print("PASS each admitted startup gets a fresh initialized/configuration phase", flush=True)


def hybrid_selection_contract(adapter, lldb, wide=False):
    with Dap(adapter, "hybrid-selection-" + str(wide), ["--native-debugger=lldb", "--native-debugger-path=" + lldb]) as peer:
        peer.request("initialize", dict(adapterID="dncdbg", supportsInvalidatedEvent=True))
        launch = peer.send("launch", dict(program=shutil.which("dotnet"), args=[str(DLL)],
                                          env=["DNCDBG_MIXED_CHECKPOINTS=1"], managedInspection=True))
        peer.event("initialized")
        peer.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                           breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_BREAK"))]))
        peer.request("configurationDone")
        peer.response(launch)
        stop = peer.event("stopped")
        frames = peer.request("stackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
        managed = next(f for f in frames if "[Managed]" in f["name"] and "ManagedCall" in f["name"])
        caller = frames[1]
        peer.request("scopes", dict(frameId=caller["id"]))
        serial = peer.request("dotnet/info")["managedSnapshot"]
        if wide:
            response = peer.request("stackTrace", dict(threadId=(1 << 40) + 7))
            check(response["dncdbgManagedInspection"]["status"] == "unavailable", str(response))
            peer.request("scopes", dict(frameId=managed["id"]))
            check(peer.request("dotnet/info")["managedSnapshot"] == serial, "Wide overlay thread destroyed another thread's snapshot")
        else:
            peer.request("scopes", dict(frameId=managed["id"]))
            result = peer.request("evaluate", dict(expression="`frame info", context="repl"))["result"]
            check("frame #1:" in result and "native_add" in result, "Managed frame replaced remembered native selection: " + result)
        peer.request("disconnect")
    print("PASS " + ("unrepresentable overlay thread preserves existing snapshots" if wide else "hybrid managed frames cannot replace native selection"), flush=True)


def zero_frame_contract(adapter):
    with Dap(adapter, "native-frame-zero", arguments("dap", "zero-frame")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        frames = peer.request("stackTrace", dict(threadId=7))["stackFrames"]
        peer.request("scopes", dict(frameId=frames[1]["id"]))
        peer.request("scopes", dict(frameId=frames[0]["id"]))
        result = peer.request("evaluate", dict(expression="`frame info", context="repl"))["result"]
        check(result == "0", "Native frame zero was not remembered; frame-less REPL restored " + result)
        peer.request("disconnect")
    print("PASS native frame zero replaces prior native selection without changing hybrid ownership", flush=True)


def disconnect_terminal_drain_contract(adapter, mode="queued-terminals"):
    release = OUT / "terminal-drain-release"
    release.unlink(missing_ok=True)
    with Dap(adapter, mode, arguments("dap", mode, barrier=release)) as peer:
        peer.request("initialize", dict(adapterID="dncdbg", supportsRunInTerminalRequest=True))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        peer.request("reverse")
        peer.receive(lambda m: m.get("type") == "request" and m.get("command") == "runInTerminal")
        started = time.monotonic()
        request = peer.send("disconnect", dict(terminateDebuggee=True))
        peer.response(request)
        if mode in ("staggered-terminals", "long-staggered-terminals"):
            peer.event("exited")
            release.write_text("release terminated after exited delivery")
        try:
            peer.process.wait(timeout=12)
        except subprocess.TimeoutExpired:
            raise AssertionError("Successful disconnect did not bound terminal draining during reverse wait") from None
        peer.reader.join(timeout=3)
        messages = [json.loads(line) for line in (OUT / (mode + ".jsonl")).read_text().splitlines()]
        terminals = [m["event"] for m in messages if m.get("event") in ("exited", "terminated")]
        check(terminals == ["exited", "terminated"], "Successful disconnect lost or duplicated terminal events: " + str(terminals))
        disconnect_index = next(i for i, m in enumerate(messages) if m.get("request_seq") == request and m.get("type") == "response")
        terminal_index = next(i for i, m in enumerate(messages) if m.get("event") == "exited")
        check(disconnect_index < terminal_index and time.monotonic() - started < 12,
              "Terminal events were not drained in order after disconnect response")
    release.unlink(missing_ok=True)
    print("PASS successful disconnect drains exited/terminated exactly once after reverse wait: " + mode, flush=True)


def refused_reverse_contract(adapter):
    with Dap(adapter, "refused-detach-reverse", arguments("dap", "reverse-detach-refused")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg", supportsRunInTerminalRequest=True))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("reverse")
        peer.receive(lambda m: m.get("type") == "request" and m.get("command") == "runInTerminal")
        peer.request("disconnect", dict(terminateDebuggee=False), success=False)
        deadline = time.monotonic() + 2
        while True:
            reply = peer.request("reverseStatus").get("response")
            if reply:
                break
            check(time.monotonic() < deadline, "Refused detach orphaned the originating runInTerminal request")
            time.sleep(0.01)
        check(reply["request_seq"] == 500 and reply["success"] is False, "Interrupted reverse did not receive explicit failure: " + str(reply))
        peer.request("reverse")
        reverse = peer.receive(lambda m: m.get("type") == "request" and m.get("command") == "runInTerminal")
        payload = json.dumps(dict(seq=999, type="response", request_seq=reverse["seq"], command="runInTerminal",
                                  success=True, body=dict(processId=123))).encode()
        peer.process.stdin.write(f"Content-Length: {len(payload)}\r\n\r\n".encode() + payload)
        peer.process.stdin.flush()
        deadline = time.monotonic() + 2
        while not peer.request("reverseStatus")["valid"]:
            check(time.monotonic() < deadline, "Refused detach did not reset reverse-request admission")
            time.sleep(0.01)
        peer.request("disconnect", dict(terminateDebuggee=True))
    print("PASS refused detach fails interrupted reverse to origin and permits the next reverse request", flush=True)


def attached_refused_termination_contract(adapter):
    release = OUT / "attached-refusal-target-release"
    release.unlink(missing_ok=True)
    pid = None
    try:
        with Dap(adapter, "attached-refused-termination", arguments("dap", "attach-termination-refused", barrier=release)) as peer:
            peer.request("initialize", dict(adapterID="dncdbg"))
            peer.request("attach", dict(pid=123))
            pid = peer.event("process")["systemProcessId"]
            peer.event("initialized")
            peer.request("disconnect", dict(terminateDebuggee=True), success=False)
            peer.process.stdin.close()
            peer.disconnected = True
            peer.process.wait(timeout=15)
            check(peer.process.returncode == 0 and integration["alive"](pid),
                  "Refused terminating disconnect escalated ownership of an attached target")
            release.write_text("release")
            integration["exited"](pid)
            pid = None
    finally:
        release.write_text("release")
        if pid and integration["alive"](pid):
            os.kill(pid, getattr(signal, "SIGKILL", signal.SIGTERM))
        release.unlink(missing_ok=True)
    print("PASS refused terminating detach preserves non-owned attached target through EOF cleanup", flush=True)


def inspection_backend_eof_contract(adapter, command="stackTrace"):
    with Dap(adapter, "inspection-eof-" + command, arguments("dap", "eof-" + command)) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        args = dict(threadId=7)
        if command in ("scopes", "variables"):
            frame = peer.request("stackTrace", dict(threadId=7))["stackFrames"][0]["id"]
            args = dict(frameId=frame)
            if command == "variables":
                args = dict(variablesReference=peer.request("scopes", args)["scopes"][0]["variablesReference"])
        elif command == "readMemory":
            args = dict(memoryReference="0x1000", count=4)
        request = peer.send(command, args)
        reply = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == request)
        text = reply.get("message", "").lower()
        check(not reply["success"] and any(word in text for word in ("connection", "transport", "unavailable", "closed"))
              and "timed out" not in text and not text.startswith("cancelled"),
              "Inspection backend EOF was misreported as stop/session cancellation: " + str(reply))
        peer.process.stdin.close()
        peer.disconnected = True
        peer.process.wait(timeout=15)
    print("PASS active " + command + " backend EOF reports transport loss rather than stop/session cancellation", flush=True)


def managed_publication_contracts(lldb):
    subprocess.run(["dotnet", "build", "tests/DncDbg.NativeTests", "-c", "Release", "--nologo", "-v", "quiet"], check=True)
    env = dict(os.environ, DNCDBG_TEST_LLDB_DAP=lldb)
    for command in ("scopes", "variables", "evaluate", "dotnet/managedStackTrace", "stackTrace"):
        for transition in ("", ":module"):
            subprocess.run(["dotnet", "run", "--no-build", "--project", "tests/DncDbg.NativeTests", "-c", "Release", "--",
                            sys.executable, str(FAKE), "managed-publication-" + command + transition], env=env, check=True)
    subprocess.run(["dotnet", "run", "--no-build", "--project", "tests/DncDbg.NativeTests", "-c", "Release", "--",
                    sys.executable, str(FAKE), "managed-publication-hybrid-enumeration"], env=env, check=True)


def orphan_target_contract(adapter, reverse=False, attached=False, detached=False):
    mode = "orphan-reverse" if reverse else "orphan-detached" if detached else "orphan-eof"
    gate = OUT / ("orphan-target-" + mode + "-" + str(attached))
    gate.unlink(missing_ok=True)
    pid = None
    engine_log = OUT / (gate.name + ".engine.log")
    with (OUT / (gate.name + ".stderr")).open("wb") as errors:
        process = subprocess.Popen([adapter, *arguments("dap", mode, barrier=gate), "--log=" + str(engine_log)],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors)
        seq, backend = 0, None
        def send(command, args=None):
            nonlocal seq
            seq += 1
            data = json.dumps(dict(seq=seq, type="request", command=command, arguments=args or {})).encode()
            process.stdin.write(f"Content-Length: {len(data)}\r\n\r\n".encode() + data)
            process.stdin.flush()
            return seq
        def header():
            length = None
            while True:
                line = process.stdout.readline()
                check(line, "Adapter closed before orphan test admission")
                if line == b"\r\n":
                    return length
                if line.lower().startswith(b"content-length:"):
                    length = int(line.split(b":", 1)[1])
        def request(command, args=None):
            nonlocal pid
            target = send(command, args)
            while True:
                message = json.loads(process.stdout.read(header()))
                if message.get("event") == "process":
                    pid = message["body"]["systemProcessId"]
                if message.get("request_seq") == target:
                    check(message["success"], str(message))
                    return message.get("body", {})
        try:
            request("initialize", dict(adapterID="dncdbg"))
            request("attach" if attached else "launch", dict(program="owned-test-target", pid=123))
            info = request("dotnet/info")
            backend = info["nativeAdapterProcessId"]
            check(pid and integration["alive"](pid), "No actual child target to prove cleanup")
            if detached:
                request("disconnect", dict(terminateDebuggee=False))
            elif reverse:
                send("reverse")
                length = header()
                check(length > 8000000 and process.stdout.read(1) == b"{", "No admitted partial reverse request")
            else:
                request("die")
                deadline = time.monotonic() + 5
                while integration["alive"](backend):
                    check(time.monotonic() < deadline, "Backend did not exit")
                    time.sleep(0.01)
                process.stdin.close()
            process.wait(timeout=36)
            check(process.returncode == 0 and not integration["alive"](backend), "Proxy/backend cleanup did not finish")
            if attached or detached or sys.platform == "darwin":
                check(integration["alive"](pid), "Independent fallback killed an attached/relinquished target")
                if sys.platform == "darwin" and not attached and not detached:
                    check("kernel-bound process identity" in engine_log.read_text(),
                          "Unavailable independent Unix cleanup was not explicitly diagnosed")
                gate.write_text("release")
                integration["exited"](pid)
            else:
                try:
                    integration["exited"](pid)
                except AssertionError:
                    raise AssertionError("Owned local child survived cleanup after its native adapter exited: " + mode) from None
            pid = None
        finally:
            gate.write_text("release")
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
            process.stdout.close()
            if pid and integration["alive"](pid):
                os.kill(pid, getattr(signal, "SIGKILL", signal.SIGTERM))
            gate.unlink(missing_ok=True)
    print("PASS adapter-exit target ownership: " + mode + (" attached" if attached else "")
          + (" (independent cleanup explicitly unavailable; fail-closed target survival)" if sys.platform == "darwin"
             and not attached and not detached else ""), flush=True)


def slow_terminal_output_contract(adapter, frames=1):
    mode = "three-slow-terminal-outputs" if frames == 3 else "slow-terminal-output"
    with (OUT / (mode + ".stderr")).open("wb") as errors:
        process = subprocess.Popen([adapter, *arguments("dap", mode)],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors)
        seq = 0
        def send(command, args=None):
            nonlocal seq
            seq += 1
            data = json.dumps(dict(seq=seq, type="request", command=command, arguments=args or {})).encode()
            process.stdin.write(f"Content-Length: {len(data)}\r\n\r\n".encode() + data)
            process.stdin.flush()
            return seq
        def read():
            length = None
            while True:
                line = process.stdout.readline()
                if not line:
                    return None
                if line == b"\r\n":
                    break
                if line.lower().startswith(b"content-length:"):
                    length = int(line.split(b":", 1)[1])
            if length > 1000000:
                time.sleep(3)
            body = process.stdout.read(length)
            check(len(body) == length, "Slow terminal output was truncated: " + str(len(body)) + "/" + str(length))
            return json.loads(body)
        def request(command, args=None):
            target = send(command, args)
            while True:
                message = read()
                check(message is not None, "Unexpected adapter exit")
                if message.get("request_seq") == target:
                    check(message["success"], str(message))
                    return
        try:
            request("initialize", dict(adapterID="dncdbg"))
            request("launch", dict(program="owned-test-target"))
            request("configurationDone")
            started = time.monotonic()
            request("disconnect", dict(terminateDebuggee=True))
            messages = []
            while (message := read()) is not None:
                messages.append(message)
            process.wait(timeout=8)
            output = [m["body"]["output"] for m in messages if m.get("event") == "output"]
            check(output == ["x" * (2 * 1024 * 1024)] * frames, "Slow but responsive client did not receive its full valid output events")
            check([m["event"] for m in messages if m.get("event") in ("exited", "terminated")] == ["exited", "terminated"],
                  "Slow output lost/reordered terminal events")
            check(process.returncode == 0 and time.monotonic() - started < 18, "Slow terminal drain did not bound shutdown")
        finally:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
    print("PASS three-second pause per frame preserves " + str(frames) + " complete 2MiB outputs and ordered exit events", flush=True)


def unrelated_target_event_contract(adapter, start_method="attach"):
    gate = OUT / ("external-sibling-" + start_method)
    gate.unlink(missing_ok=True)
    with Dap(adapter, gate.name, arguments("dap", "unrelated-event")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        # This child deliberately starts AFTER adapter initialization; chronology
        # alone is not evidence that this backend launched it.
        sibling = subprocess.Popen([sys.executable, str(FAKE), "--target-gate", str(gate)],
                                   stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            peer.request("launch", dict(program="owned-test-target", reportedPid=sibling.pid,
                                        reportedStartMethod=start_method), success=False)
            peer.process.stdin.close()
            peer.disconnected = True
            peer.process.wait(timeout=15)
            check(peer.process.returncode == 0 and sibling.poll() is None,
                  "Uncorroborated " + start_method + " process event authorized killing an external sibling")
            gate.write_text("release")
            check(sibling.wait(timeout=5) == 0, "Preserved external sibling did not exit normally")
        finally:
            gate.write_text("release")
            if sibling.poll() is None:
                sibling.kill()
                sibling.wait(timeout=5)
            gate.unlink(missing_ok=True)
    print("PASS launch provenance rejects external sibling process event: startMethod=" + start_method, flush=True)


def hybrid_backend_eof_contract(adapter):
    with Dap(adapter, "hybrid-eof-modules", arguments("lldb", "eof-modules")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target", managedInspection=True))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        request = peer.send("stackTrace", dict(threadId=7))
        reply = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == request)
        text = reply.get("message", "").lower()
        check(not reply["success"] and any(word in text for word in ("connection", "transport", "closed"))
              and not text.startswith("cancelled"), "Hybrid acquisition swallowed fatal backend loss: " + str(reply))
        peer.process.stdin.close()
        peer.disconnected = True
        peer.process.wait(timeout=15)
    print("PASS hybrid stack acquisition propagates fatal backend EOF as connection loss", flush=True)


def dac_backend_eof_contract(adapter, lldb, mode="eof-dac-memory"):
    with Dap(adapter, mode, arguments("lldb", mode, lldb=lldb)) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        launch = peer.send("launch", dict(program=shutil.which("dotnet"), args=[str(DLL)],
                                          env=["DNCDBG_MIXED_CHECKPOINTS=1"], managedInspection=True))
        peer.event("initialized")
        peer.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                           breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_CHECKPOINT"))]))
        peer.request("configurationDone")
        peer.response(launch)
        stop = peer.event("stopped")
        request = peer.send("stackTrace", dict(threadId=stop["threadId"]))
        reply = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == request)
        text = reply.get("message", "").lower()
        check(not reply["success"] and any(word in text for word in ("transport", "connection", "unavailable")),
              "DAC COM wrapped backend loss into a generic runtime error: " + str(reply))
        peer.process.stdin.close()
        peer.disconnected = True
        peer.process.wait(timeout=15)
    print("PASS DAC COM backend failure retains transport-unavailable diagnostic: " + mode, flush=True)


def inherited_stdout_contract(adapter, attach=False):
    gate = OUT / ("inherited-stdout-" + str(attach))
    gate.unlink(missing_ok=True)
    pid = None
    try:
        with Dap(adapter, gate.name, arguments("dap", "inherited-stdout", barrier=gate)) as peer:
            peer.request("initialize", dict(adapterID="dncdbg"))
            peer.request("attach" if attach else "launch", dict(program="owned-test-target", pid=123))
            pid = peer.event("process")["systemProcessId"]
            peer.event("initialized")
            peer.request("disconnect", dict(terminateDebuggee=False))
            peer.process.wait(timeout=18)
            check(peer.process.returncode == 0, "Inherited detached target stdout aborted cleanup: exit=" + str(peer.process.returncode))
            check(integration["alive"](pid), "Reader quiescence killed a detached/attached target")
            gate.write_text("release")
            integration["exited"](pid)
            pid = None
    finally:
        gate.write_text("release")
        if pid and integration["alive"](pid):
            os.kill(pid, getattr(signal, "SIGKILL", signal.SIGTERM))
        gate.unlink(missing_ok=True)
    print("PASS inherited target stdout cannot throw or block bounded detached reader cleanup; attach=" + str(attach), flush=True)


def native_only_watch_contract(adapter):
    with Dap(adapter, "native-only-watch-semantics", arguments("lldb")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        peer.request("launch", dict(program="owned-test-target"))
        peer.event("initialized")
        peer.request("configurationDone")
        peer.event("stopped")
        frame = peer.request("stackTrace", dict(threadId=7))["stackFrames"][0]
        result = peer.request("evaluate", dict(frameId=frame["id"], expression="left + right", context="watch"))
        check(result["context"] == "watch", "Read-only overlay policy changed native-only backend watch semantics")
        peer.request("disconnect")
    print("PASS native-only watch expressions retain the backend's unmodified evaluation context", flush=True)


def duplicate_native_pc_contract(adapter, lldb):
    with Dap(adapter, "duplicate-native-pc", arguments("lldb", "duplicate-native-pc", lldb=lldb)) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        launch = peer.send("launch", dict(program=shutil.which("dotnet"), args=[str(DLL)],
                                          env=["DNCDBG_MIXED_CHECKPOINTS=1"], managedInspection=True))
        peer.event("initialized")
        peer.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                           breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_CHECKPOINT"))]))
        peer.request("configurationDone")
        peer.response(launch)
        stop = peer.event("stopped")
        frames = peer.request("stackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
        check(not any("[Managed]" in f["name"] for f in frames), "Ambiguous duplicate native PCs were enriched as unique managed frames")
        peer.request("disconnect")
    print("PASS managed overlay requires native as well as managed PC uniqueness", flush=True)


def managed_launch_scope_contract(adapter):
    with Dap(adapter, "managed-launch-scope", arguments("lldb")) as peer:
        peer.request("initialize", dict(adapterID="dncdbg"))
        for key, value in (("preRunCommands", ["platform select remote-linux"]), ("postRunCommands", ["continue"]),
                           ("stopCommands", ["continue"]), ("exitCommands", ["target create other"]),
                           ("terminateCommands", ["process kill"]), ("targetTriple", "x86_64-unknown-linux"),
                           ("attachCommands", []), ("launchCommands", []), ("initCommands", []), ("platformName", "remote-linux")):
            request = peer.send("launch", dict(program="owned-test-target", managedInspection=True, **{key: value}))
            response = peer.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == request)
            check(not response["success"] and "local" in response["message"].lower(),
                  "Managed launch accepted custom execution vector " + key + ": " + str(response))
        peer.request("disconnect")
    print("PASS managed launch rejects custom command/triple/remote execution vectors", flush=True)


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
    startup_evidence_contract(options.adapter)
    startup_configuration_contract(options.adapter)
    startup_without_configuration_contract(options.adapter)
    disconnect_timeout_contract(options.adapter)
    dead_backend_disconnect_contract(options.adapter)
    blocked_write_contract(options.adapter)
    blocked_write_contract(options.adapter, eof=True)
    inspection_capability_contract(options.adapter)
    wide_reference_contract(options.adapter)
    refused_admission_contract(options.adapter)
    frameless_evaluation_contract(options.adapter)
    frameless_evaluation_contract(options.adapter, mode="selected-repl")
    inspection_stop_publication_contract(options.adapter)
    inspection_stop_publication_contract(options.adapter, command="scopes")
    explicit_repl_contract(options.adapter)
    zero_frame_contract(options.adapter)
    disconnect_terminal_drain_contract(options.adapter)
    disconnect_terminal_drain_contract(options.adapter, mode="late-terminals")
    disconnect_terminal_drain_contract(options.adapter, mode="staggered-terminals")
    disconnect_terminal_drain_contract(options.adapter, mode="long-terminals")
    disconnect_terminal_drain_contract(options.adapter, mode="long-staggered-terminals")
    client_backpressure_contract(options.adapter, command="reverse", finish="deadline")
    orphan_target_contract(options.adapter)
    orphan_target_contract(options.adapter, reverse=True)
    orphan_target_contract(options.adapter, attached=True)
    orphan_target_contract(options.adapter, detached=True)
    slow_terminal_output_contract(options.adapter)
    slow_terminal_output_contract(options.adapter, frames=3)
    unrelated_target_event_contract(options.adapter)
    unrelated_target_event_contract(options.adapter, start_method="launch")
    hybrid_backend_eof_contract(options.adapter)
    inherited_stdout_contract(options.adapter)
    inherited_stdout_contract(options.adapter, attach=True)
    refused_reverse_contract(options.adapter)
    attached_refused_termination_contract(options.adapter)
    for command in ("stackTrace", "scopes", "variables", "readMemory"):
        inspection_backend_eof_contract(options.adapter, command=command)
    for command in ("variables", "scopes"):
        for finish in ("eof", "disconnect"):
            client_backpressure_contract(options.adapter, command=command, finish=finish)
    client_backpressure_contract(options.adapter, finish="module")
    client_backpressure_contract(options.adapter, finish="deadline")
    poisoned_disconnect_contract(options.adapter)
    fresh_configuration_contract(options.adapter)
    late_startup_evidence_contract(options.adapter)
    startup_deadline_gaps_contract(options.adapter)
    startup_deadline_gaps_contract(options.adapter, mode="startup-postconfig-retry")
    startup_failed_evidence_contract(options.adapter)
    disconnect_blocked_startup_contract(options.adapter)
    wide_thread_contract(options.adapter)
    native_only_watch_contract(options.adapter)
    managed_launch_scope_contract(options.adapter)
    unsupported_overlay_controls(options.adapter)
    bundle_contracts(options.adapter)
    bundle_links_contract(options.adapter)
    detached_backend_stall(options.adapter)
    if options.lldb_dap:
        build()
        dac_backend_eof_contract(options.adapter, options.lldb_dap)
        dac_backend_eof_contract(options.adapter, options.lldb_dap, mode="eof-dac-context")
        managed_publication_contracts(options.lldb_dap)
        strict_managed_contract(options.adapter, options.lldb_dap)
        strict_managed_contract(options.adapter, options.lldb_dap, "decimal-modules")
        native_watch_contract(options.adapter, options.lldb_dap)
        native_watch_contract(options.adapter, options.lldb_dap, invalidation=True)
        duplicate_native_pc_contract(options.adapter, options.lldb_dap)
        real_explicit_repl_contract(options.adapter, options.lldb_dap)
        hybrid_selection_contract(options.adapter, options.lldb_dap)
        hybrid_selection_contract(options.adapter, options.lldb_dap, wide=True)
        nonmutating_queries_contract(options.adapter, options.lldb_dap)
        for mode in ("mutation-failure", "mutation-timeout"):
            for command in ("evaluate", "writeMemory"):
                uncertain_mutation_contract(options.adapter, options.lldb_dap, mode=mode, command=command)
        if options.attach_restart_proof:
            real_restart_contract(options.adapter, options.lldb_dap)
            real_attach_contract(options.adapter, options.lldb_dap)
        notification_contract(options.adapter, options.lldb_dap)
        for poison in (False, True):
            invalid_runtime(options.adapter, options.lldb_dap, poison)
        for mode in ("hold", "exit-during-read", "lost-server"):
            interrupted_read(options.adapter, options.lldb_dap, mode)
