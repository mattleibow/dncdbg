#!/usr/bin/env python3
"""A test DAP peer, or a deterministic relay around a real lldb-dap. No production test hooks."""

import argparse
import base64
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time

parser = argparse.ArgumentParser()
parser.add_argument("--mode", default="normal")
parser.add_argument("--lldb")
parser.add_argument("--barrier", type=Path)
parser.add_argument("--version", action="store_true")
parser.add_argument("--target-gate", type=Path)
parser.add_argument("--arch", default="arm64")
options = parser.parse_args()

if options.target_gate:
    while not options.target_gate.exists():
        time.sleep(0.01)
    sys.exit(0)

if options.version:
    if options.mode == "version-reads-stdin":
        sys.stdin.buffer.read(1)
    if options.mode == "no-cli-version":
        sys.exit(1)
    if options.lldb:
        sys.exit(subprocess.run([options.lldb, "--version"]).returncode)
    print("lldb version 21.1.8")
    sys.exit(0)

write_lock = threading.Lock()


def read(stream):
    length = None
    while True:
        header = stream.readline()
        if not header:
            return None
        if header in (b"\r\n", b"\n"):
            break
        if header.lower().startswith(b"content-length:"):
            length = int(header.split(b":", 1)[1])
    if length is None:
        raise RuntimeError("Missing DAP Content-Length")
    body = stream.read(length)
    if len(body) != length:
        raise RuntimeError("Truncated DAP frame")
    return json.loads(body)


def write(stream, message):
    data = json.dumps(message).encode()
    with write_lock:
        stream.write(f"Content-Length: {len(data)}\r\n\r\n".encode() + data)
        stream.flush()


if options.lldb:
    child = subprocess.Popen([options.lldb], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=sys.stderr.buffer)
    held = False
    internal_seq = 2_000_000_000

    def receive():
        while True:
            message = read(child.stdout)
            if message is None:
                os._exit(child.wait(timeout=5))
            if message.get("request_seq") == internal_seq:
                continue
            if options.mode in ("mutation-failure", "mutation-timeout") and message.get("command") in ("evaluate", "writeMemory"):
                if options.barrier and options.barrier.with_suffix(".arm").exists():
                    options.barrier.with_suffix(".arm").unlink()
                    options.barrier.with_suffix(".ready").write_text("native mutation completed")
                    if options.mode == "mutation-timeout":
                        continue
                    message.update(success=False, message="Test relay: native side effect completed before failure")
            if options.mode == "decimal-modules" and message.get("command") == "modules":
                for module in message.get("body", {}).get("modules", []):
                    address = module.get("addressRange", "")
                    if address.startswith("0x"):
                        module["addressRange"] = str(int(address.split("-")[0].strip(), 16))
            if options.mode == "duplicate-native-pc" and message.get("command") == "stackTrace":
                frames = message.get("body", {}).get("stackFrames", [])
                copies = [dict(frame, id=frame["id"] + 100000000) for frame in frames]
                frames.extend(copies)
            write(sys.stdout.buffer, message)

    receiver = threading.Thread(target=receive, daemon=True)
    receiver.start()
    try:
        while True:
            message = read(sys.stdin.buffer)
            if message is None:
                break
            if (options.barrier and not held and options.barrier.with_suffix(".arm").exists()
                    and message.get("command") == "readMemory"):
                held = True
                options.barrier.with_suffix(".ready").write_text(str(message["seq"]))
                if options.mode == "exit-during-read":
                    write(child.stdin, dict(seq=internal_seq, type="request", command="disconnect",
                                            arguments=dict(terminateDebuggee=True)))
                elif options.mode == "lost-server":
                    child.kill()
                    break
                # Keep the read outstanding. Other requests, including continue/disconnect, are still admitted.
                continue
            write(child.stdin, message)
        receiver.join(timeout=2)
    finally:
        if child.poll() is None:
            child.kill()
        child.wait(timeout=5)
    sys.exit(0)

last_reverse_response = None
owned_target = None
selected_frame = 1
launch_count = 0
deferred = None
disconnects = []
module_reads = 0
inspection_transitioned = False


def response(request, body=None, success=True, message=None):
    global inspection_transitioned
    if options.mode == "wide-ids" and body is not None:
        def widen(value):
            if isinstance(value, dict):
                for key, child in value.items():
                    if key in ("variablesReference", "sourceReference") and isinstance(child, int) and child > 0:
                        value[key] += 1 << 40
                    elif isinstance(child, (dict, list)):
                        widen(child)
            elif isinstance(value, list):
                for child in value:
                    widen(child)
        widen(body)
        if request["command"] == "stackTrace":
            for frame in body["stackFrames"]:
                frame["id"] += 1 << 40
    value = dict(seq=0, type="response", request_seq=request["seq"], command=request["command"], success=success)
    if body is not None:
        value["body"] = body
    if message:
        value["message"] = message
    if (not inspection_transitioned and options.mode in ("transition-variables", "transition-scopes")
            and request["command"] == options.mode.removeprefix("transition-")):
        inspection_transitioned = True
        key = "variables" if request["command"] == "variables" else "scopes"
        value["body"][key] *= 24000
        messages = [value,
                    dict(seq=0, type="event", event="continued", body=dict(threadId=7, allThreadsContinued=True)),
                    dict(seq=0, type="event", event="stopped", body=dict(reason="step", threadId=7, allThreadsStopped=True))]
        data = b"".join(f"Content-Length: {len(payload)}\r\n\r\n".encode() + payload
                        for payload in (json.dumps(m).encode() for m in messages))
        with write_lock:
            sys.stdout.buffer.write(data)
            sys.stdout.buffer.flush()
        return
    write(sys.stdout.buffer, value)


def event(name, body=None):
    write(sys.stdout.buffer, dict(seq=0, type="event", event=name, **({"body": body} if body is not None else {})))


while True:
    request = read(sys.stdin.buffer)
    if request is None:
        break
    if request["type"] == "response":
        last_reverse_response = request
        continue
    command, args = request["command"], request.get("arguments", {})
    if options.mode == "wide-ids":
        for key in ("frameId", "variablesReference", "sourceReference"):
            if args.get(key, 0) > 1 << 40:
                args[key] -= 1 << 40
        if args.get("source", {}).get("sourceReference", 0) > 1 << 40:
            args["source"]["sourceReference"] -= 1 << 40
    if command == "initialize":
        if options.mode == "malformed":
            sys.stdout.buffer.write(b"Content-Length: 15\r\n\r\n{\"success\": []}")
            sys.stdout.buffer.flush()
            break
        response(request, dict(supportsConfigurationDoneRequest=options.mode != "startup-no-configuration", supportsReadMemoryRequest=options.mode != "no-memory",
                               supportsModulesRequest=True, supportsCancelRequest=True,
                               **{"$__lldb_version": "lldb version " + ("99.0.0" if options.mode == "version-mismatch" else "21.1.8")}))
        if options.mode == "blocked-writes":
            # Admit a header and one payload byte, then stop draining the pipe.
            # This proves the proxy worker entered the write before frontend EOF.
            while sys.stdin.buffer.readline() not in (b"\r\n", b"\n"):
                pass
            sys.stdin.buffer.read(1)
            options.barrier.with_suffix(".ready").write_text(str(os.getpid()))
            while not options.barrier.exists():
                time.sleep(0.01)
    elif command in ("launch", "attach"):
        launch_count += 1
        if options.mode == "startup-preinitialized":
            continue
        if options.mode == "startup-postconfig-retry":
            deferred = request
            event("initialized")
            continue
        if options.mode == "fresh-configuration":
            if launch_count == 1:
                deferred = request
                event("initialized")
            else:
                event("initialized")
            continue
        if options.mode in ("startup-timeout", "startup-no-configuration") and launch_count == 1:
            continue
        if options.mode == "startup-config" and launch_count == 1:
            deferred = request
            event("initialized")
            continue
        if options.mode == "startup-evidence" and launch_count == 1:
            event("process", dict(systemProcessId=os.getpid(), name="test-target-evidence", isLocalProcess=True))
            event("initialized")
            event("stopped", dict(reason="breakpoint", threadId=7, allThreadsStopped=True))
            continue
        if options.mode == "startup-failed-evidence" and launch_count == 1:
            event("process", dict(systemProcessId=os.getpid(), name="test-target-evidence", isLocalProcess=True))
            event("stopped", dict(reason="breakpoint", threadId=7, allThreadsStopped=True))
            response(request, success=False, message="Startup failed after target evidence")
            continue
        if options.mode == "startup-late-evidence" and launch_count == 1:
            response(request, success=False, message="Explicit startup rejection")
            continue
        if options.mode == "startup-failure" and launch_count == 1:
            response(request, success=False, message="Explicit startup rejection")
            continue
        if options.mode in ("detach-stall", "detach-refused", "dead-disconnect"):
            owned_target = subprocess.Popen([sys.executable, __file__, "--target-gate", str(options.barrier)],
                                            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            event("process", dict(systemProcessId=owned_target.pid, name="owned-test-target", isLocalProcess=True))
        response(request)
        event("initialized")
    elif command == "configurationDone":
        response(request)
        if options.mode == "startup-config":
            response(deferred)
        if options.mode == "startup-postconfig-retry" and launch_count == 1:
            response(deferred, success=False, message="Explicit failure after configuration")
            continue
        if options.mode == "fresh-configuration" and launch_count == 1:
            response(deferred, success=False, message="First configured launch rejected")
            continue
        event("stopped", dict(reason="breakpoint", threadId=(1 << 40) + 7 if options.mode == "wide-threads" else 7,
                              allThreadsStopped=options.mode != "refused-control"))
    elif command == "stackTrace":
        if options.mode == "wide-threads" and args["threadId"] != (1 << 40) + 7:
            response(request, success=False, message="Native thread identity truncated")
            continue
        response(request, dict(totalFrames=2, stackFrames=[dict(id=1, name="native", line=3, column=1,
                                                               instructionPointerReference="0x1000",
                                                               source=dict(name="native.c", sourceReference=1)),
                                                          dict(id=2, name="native-caller", line=4, column=1,
                                                               instructionPointerReference="0x2000",
                                                               source=dict(name="native.c", sourceReference=1))]))
    elif command == "scopes":
        if options.mode == "client-block-scopes":
            response(request, dict(scopes=[dict(name="Locals", variablesReference=1, expensive=False)] * 160000))
            continue
        selected_frame = args["frameId"]
        response(request, dict(scopes=[dict(name="Registers" if options.mode.startswith("register-") else "Locals",
                                           variablesReference=3 if options.mode.startswith("register-") else 1, expensive=False)]),
                 success=args["frameId"] in (1, 2))
    elif command == "variables":
        if options.mode == "client-block-variables":
            response(request, dict(variables=[dict(name="old-value", value="x" * 80, variablesReference=2)] * 80000))
            continue
        if options.mode.startswith("register-"):
            if args["variablesReference"] == 3:
                response(request, dict(variables=[dict(name="General Purpose Registers", value="", variablesReference=4)]))
            else:
                if options.arch == "arm64":
                    registers = {**{"x" + str(i): "0x" + format(i, "x") for i in range(31)},
                                 "x29": "0x2929", "x30": "0x3030", "sp": "0x1000", "pc": "0x2000", "cpsr": "0x20001000"}
                else:
                    registers = {**{n: "0x1000" for n in ("rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rsp", "rbp", "rip")},
                                 **{"r" + str(i): "0x1000" for i in range(8, 16)}, "eflags": "0x202 [ IF ]"}
                    if options.mode == "register-optional-mxcsr":
                        registers.update({"xmm" + str(i): "0x0" for i in range(16)})
                response(request, dict(variables=[dict(name=n, value=v, variablesReference=0) for n, v in registers.items()]))
            continue
        check = args["variablesReference"] in (1, 2)
        response(request, dict(variables=[dict(name="value", value=str(selected_frame) if options.mode == "selected-frame" else "42",
                                              variablesReference=2 if args["variablesReference"] == 1 else 0)]),
                 success=check)
    elif command == "source":
        source = args.get("source", {}).get("sourceReference", args.get("sourceReference"))
        response(request, dict(content="native source"), success=source == 1)
    elif command in ("continue", "next"):
        if options.mode == "timeout-control":
            continue
        if options.mode in ("refused-control", "refused-admission"):
            response(request, success=False, message="Test backend refused continuation")
            continue
        response(request, dict(allThreadsContinued=True))
        event("continued", dict(threadId=7, allThreadsContinued=True))
        event("stopped", dict(reason="step", threadId=7, allThreadsStopped=True))
    elif command == "threads":
        response(request, dict(threads=[dict(id=(1 << 40) + 7 if options.mode == "wide-threads" else 7, name="Native")]))
    elif command == "evaluate":
        if options.mode == "selected-repl":
            response(request, dict(result=str(selected_frame), variablesReference=0))
            continue
        if options.mode == "selected-frame" and "frameId" not in args:
            response(request, dict(result=str(selected_frame), variablesReference=0))
            continue
        response(request, dict(result=str(args.get("frameId", selected_frame)) if options.mode == "selected-frame" else "42",
                               variablesReference=1, context=args.get("context")),
                 success=args.get("frameId", selected_frame) in (1, 2))
    elif command == "reverse":
        write(sys.stdout.buffer, dict(seq=500, type="request", command="runInTerminal", arguments=dict(args=["owned-fixture"])))
        response(request)
    elif command == "reverseStatus":
        response(request, dict(valid=last_reverse_response is not None and last_reverse_response["request_seq"] == 500
                               and last_reverse_response.get("success") is True, response=last_reverse_response))
    elif command == "deferred":
        deferred = request
    elif command == "timeout":
        continue
    elif command == "releaseDeferred":
        response(deferred, dict(released=True))
        response(request)
    elif command == "invalidate":
        response(request)
        event("invalidated", dict(areas=["variables"], threadId=7, stackFrameId=1))
    elif command == "capabilityUpdate":
        response(request)
        event("capabilities", dict(capabilities=dict(supportsCancelRequest=True)))
    elif command == "disconnect":
        disconnects.append(args.get("terminateDebuggee"))
        if options.mode == "disconnect-timeout" and len(disconnects) == 1:
            continue
        if options.mode == "disconnect-timeout" and args.get("terminateDebuggee") is not False:
            response(request, success=False, message="Unknown detach escalated into termination")
            continue
        if options.mode == "detach-refused" and args.get("terminateDebuggee") is False:
            response(request, success=False, message="Test backend refused detach")
            continue
        if options.mode == "detach-refused" and args.get("terminateDebuggee") is True:
            Path(str(options.barrier) + ".terminated").write_text("terminating request")
        response(request)
        event("terminated")
        if options.mode == "detach-stall" and args.get("terminateDebuggee") is False:
            while True:
                time.sleep(1)
        if owned_target and owned_target.poll() is None:
            owned_target.kill()
            owned_target.wait(timeout=5)
        break
    elif command == "modules":
        module_reads += 1
        response(request, dict(modules=[dict(name="libcoreclr.dylib", addressRange=123)]
                               if options.mode == "malformed-module" else []))
    elif command == "ownershipStatus":
        response(request, dict(launches=launch_count, disconnects=disconnects, moduleReads=module_reads))
    elif command == "selectOtherFrame":
        selected_frame = 2
        response(request)
    elif command == "lateEvidence":
        event("process", dict(systemProcessId=os.getpid(), name="late-test-target", isLocalProcess=True))
        event("stopped", dict(reason="breakpoint", threadId=7, allThreadsStopped=True))
        response(request)
    elif command == "die":
        response(request)
        os._exit(0)
    elif command == "readMemory" and options.mode == "partial-memory":
        address = int(args["memoryReference"], 16)
        if address == 0x1000:
            response(request, dict(address="0x1000", data=base64.b64encode(bytes(range(256)) * 16).decode()))
        else:
            response(request, success=False, message="Unmapped memory")
    elif command == "setBreakpoints":
        response(request, dict(breakpoints=[]))
    else:
        response(request, success=False, message="Unsupported native request: " + command)
