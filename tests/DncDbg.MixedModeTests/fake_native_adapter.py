#!/usr/bin/env python3
"""A test DAP peer, or a deterministic relay around a real lldb-dap. No production test hooks."""

import argparse
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
options = parser.parse_args()

if options.target_gate:
    while not options.target_gate.exists():
        time.sleep(0.01)
    sys.exit(0)

if options.version:
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


def response(request, body=None, success=True, message=None):
    value = dict(seq=0, type="response", request_seq=request["seq"], command=request["command"], success=success)
    if body is not None:
        value["body"] = body
    if message:
        value["message"] = message
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
    if command == "initialize":
        if options.mode == "malformed":
            sys.stdout.buffer.write(b"Content-Length: 15\r\n\r\n{\"success\": []}")
            sys.stdout.buffer.flush()
            break
        response(request, dict(supportsConfigurationDoneRequest=True, supportsReadMemoryRequest=True,
                               supportsModulesRequest=True, supportsCancelRequest=True,
                               **{"$__lldb_version": "lldb version " + ("99.0.0" if options.mode == "version-mismatch" else "21.1.8")}))
    elif command in ("launch", "attach"):
        if options.mode == "detach-stall":
            owned_target = subprocess.Popen([sys.executable, __file__, "--target-gate", str(options.barrier)],
                                            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            event("process", dict(systemProcessId=owned_target.pid, name="owned-test-target", isLocalProcess=True))
        response(request)
        event("initialized")
    elif command == "configurationDone":
        response(request)
        event("stopped", dict(reason="breakpoint", threadId=7, allThreadsStopped=True))
    elif command == "stackTrace":
        response(request, dict(totalFrames=1, stackFrames=[dict(id=1, name="native", line=3, column=1,
                                                               instructionPointerReference="0x1000",
                                                               source=dict(name="native.c", sourceReference=1))]))
    elif command == "scopes":
        response(request, dict(scopes=[dict(name="Locals", variablesReference=1, expensive=False)]),
                 success=args["frameId"] == 1)
    elif command == "variables":
        check = args["variablesReference"] in (1, 2)
        response(request, dict(variables=[dict(name="value", value="42", variablesReference=2 if args["variablesReference"] == 1 else 0)]),
                 success=check)
    elif command == "source":
        source = args.get("source", {}).get("sourceReference", args.get("sourceReference"))
        response(request, dict(content="native source"), success=source == 1)
    elif command in ("continue", "next"):
        response(request, dict(allThreadsContinued=True))
        event("continued", dict(threadId=7, allThreadsContinued=True))
        event("stopped", dict(reason="step", threadId=7, allThreadsStopped=True))
    elif command == "threads":
        response(request, dict(threads=[dict(id=7, name="Native")]))
    elif command == "evaluate":
        response(request, dict(result="42", variablesReference=1), success=args.get("frameId") == 1)
    elif command == "reverse":
        write(sys.stdout.buffer, dict(seq=500, type="request", command="runInTerminal", arguments=dict(args=["owned-fixture"])))
        response(request)
    elif command == "reverseStatus":
        response(request, dict(valid=last_reverse_response is not None and last_reverse_response["request_seq"] == 500
                               and last_reverse_response.get("success") is True))
    elif command == "disconnect":
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
        response(request, dict(modules=[]))
    elif command == "setBreakpoints":
        response(request, dict(breakpoints=[]))
    else:
        response(request, success=False, message="Unsupported native request: " + command)
