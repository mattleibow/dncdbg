#!/usr/bin/env python3
"""Public-tool baseline and mixed-mode integration checks. No Python packages required."""

import argparse
import json
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import threading
import time


ROOT = Path(__file__).resolve().parents[2]
FIXTURE = Path(__file__).resolve().parent / "fixture"
OUT = ROOT / "build" / "validation" / "mixed-mode"
DLL = OUT / "fixture" / "Fixture.dll"


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def line(path, marker):
    return next(i for i, text in enumerate(path.read_text().splitlines(), 1) if marker in text)


class Dap:
    def __init__(self, executable, name, arguments=()):
        self.process = subprocess.Popen(
            [str(executable), *arguments], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.messages = queue.Queue()
        self.saved = []
        self.seq = 0
        self.disconnected = False
        self.log = (OUT / (name + ".jsonl")).open("w")
        self.errors = (OUT / (name + ".stderr")).open("wb")
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()
        self.error_reader = threading.Thread(target=self._errors, daemon=True)
        self.error_reader.start()

    def _errors(self):
        shutil.copyfileobj(self.process.stderr, self.errors)

    def _read(self):
        try:
            while True:
                length = None
                while True:
                    header = self.process.stdout.readline()
                    if not header:
                        raise EOFError("DAP peer closed stdout")
                    if header in (b"\r\n", b"\n"):
                        break
                    if header.lower().startswith(b"content-length:"):
                        length = int(header.split(b":", 1)[1])
                check(length is not None, "Missing Content-Length")
                data = b""
                while len(data) < length:
                    part = self.process.stdout.read(length - len(data))
                    if not part:
                        raise EOFError("Truncated DAP message")
                    data += part
                message = json.loads(data)
                self.log.write(json.dumps(message) + "\n")
                self.log.flush()
                self.messages.put(message)
        except Exception as error:
            self.messages.put(error)

    def send(self, command, arguments=None):
        self.seq += 1
        data = json.dumps(dict(seq=self.seq, type="request", command=command,
                               arguments=arguments or {})).encode()
        self.process.stdin.write(f"Content-Length: {len(data)}\r\n\r\n".encode() + data)
        self.process.stdin.flush()
        return self.seq

    def receive(self, predicate, timeout=30):
        for i, item in enumerate(self.saved):
            if predicate(item):
                return self.saved.pop(i)
        deadline = time.monotonic() + timeout
        while True:
            item = self.messages.get(timeout=max(0, deadline - time.monotonic()))
            if isinstance(item, Exception):
                raise item
            if predicate(item):
                return item
            self.saved.append(item)

    def response(self, seq, success=True, timeout=30):
        response = self.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == seq, timeout)
        check(response.get("success") == success, str(response))
        if response.get("command") == "disconnect" and success:
            self.disconnected = True
        return response.get("body", {})

    def request(self, command, arguments=None, success=True):
        return self.response(self.send(command, arguments), success)

    def event(self, name):
        return self.receive(lambda m: m.get("type") == "event" and m.get("event") == name).get("body", {})

    def close(self):
        if self.process.poll() is None and not self.disconnected:
            try:
                self.response(self.send("disconnect", dict(terminateDebuggee=True)), timeout=5)
            except (OSError, EOFError, queue.Empty, AssertionError) as error:
                print(f"Cleanup disconnect failed for PID {self.process.pid}: {error}", flush=True)
        if self.process.poll() is None:
            if self.disconnected:
                try:
                    self.process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    print(f"DAP peer {self.process.pid} did not exit after disconnect", flush=True)
                    self.process.kill()
            else:
                self.process.kill()
        self.process.wait(timeout=10)
        self.reader.join(timeout=5)
        self.error_reader.join(timeout=5)
        self.log.close()
        self.errors.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def build():
    OUT.mkdir(parents=True, exist_ok=True)
    subprocess.run(["dotnet", "build", str(FIXTURE / "Fixture.csproj"), "-o", str(DLL.parent),
                    "--nologo", "-v", "quiet"], check=True)
    configure = ["cmake", "-S", str(FIXTURE), "-B", str(OUT / "native")]
    configure += ["-A", "x64"] if sys.platform == "win32" else ["-DCMAKE_BUILD_TYPE=Debug"]
    subprocess.run(configure, check=True)
    subprocess.run(["cmake", "--build", str(OUT / "native"), "--config", "Debug"], check=True)
    candidates = [p for p in (OUT / "native").rglob("*mixedfixture*")
                  if p.suffix in (".dylib", ".so", ".dll")]
    check(len(candidates) == 1, f"Expected one native library, got {candidates}")
    shutil.copy2(candidates[0], DLL.parent)
    native_pdb = candidates[0].with_suffix(".pdb")
    if native_pdb.exists():
        shutil.copy2(native_pdb, DLL.parent)
    result = subprocess.run(["dotnet", str(DLL)], capture_output=True, text=True, check=True)
    check(result.stdout.splitlines() == ["BEFORE:19,23", "AFTER:42", "RESULT:42"], result.stdout)
    print("PASS fixture: native_add(19, 23) == 42", flush=True)


def native_inspection(native, stop):
    tid = stop["threadId"]
    threads = native.request("threads")["threads"]
    check(any(t["id"] == tid for t in threads), "Stopped native thread not listed")
    frames = native.request("stackTrace", dict(threadId=tid))["stackFrames"]
    check("native_sum" in frames[0]["name"], str(frames[:3]))
    check(any("native_add" in f["name"] for f in frames), "Missing native caller")
    check(Path(frames[0]["source"]["path"]).resolve() == (FIXTURE / "native.c").resolve(),
          str(frames[0]))
    check(frames[0]["line"] == line(FIXTURE / "native.c", "NATIVE_BREAK"), str(frames[0]))
    scopes = native.request("scopes", dict(frameId=frames[0]["id"]))["scopes"]
    variables = []
    for scope in scopes:
        variables += native.request("variables", dict(variablesReference=scope["variablesReference"]))["variables"]
    values = {v["name"]: v["value"] for v in variables}
    check(values.get("left") == "19" and values.get("right") == "23", str(values))
    print(f"PASS native breakpoint/stack/threads/arguments: {values}", flush=True)
    return tid, frames[0]["id"]


def native_baseline(lldb):
    with Dap(lldb, "native-baseline") as native:
        native.request("initialize", dict(adapterID="lldb", pathFormat="path", linesStartAt1=True, columnsStartAt1=True))
        launch = native.send("launch", dict(program=shutil.which("dotnet"), args=[str(DLL)],
                                             stopOnEntry=False))
        native.event("initialized")
        native.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                             breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_BREAK"))]))
        native.request("configurationDone")
        native.response(launch)
        tid, frame = native_inspection(native, native.event("stopped"))
        native.request("next", dict(threadId=tid))
        stop = native.event("stopped")
        frames = native.request("stackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
        result = native.request("evaluate", dict(frameId=frames[0]["id"], expression="sum", context="watch"))
        check(result["result"] == "42", str(result))
        native.request("continue", dict(threadId=tid))
        check(native.event("exited")["exitCode"] == 0, "Native baseline exit")
        native.request("disconnect")
    print("PASS public LLDB DAP launch baseline and native stepping", flush=True)


def start_managed(managed, preload=False):
    managed.request("initialize", dict(adapterID="dncdbg"))
    launch = managed.send("launch", dict(program=str(DLL), args=["--preload"] if preload else []))
    managed.event("initialized")
    managed.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "Program.cs")),
                                          breakpoints=[dict(line=line(FIXTURE / "Program.cs", marker))
                                                       for marker in ("MANAGED_BEFORE", "MANAGED_AFTER")]))
    managed.request("configurationDone")
    managed.response(launch)
    before = managed.event("stopped")
    frames = managed.request("stackTrace", dict(threadId=before["threadId"]))["stackFrames"]
    check("ManagedCall" in frames[0]["name"], str(frames[0]))
    check(frames[0]["line"] == line(FIXTURE / "Program.cs", "MANAGED_BEFORE"), str(frames[0]))
    left = managed.request("evaluate", dict(frameId=frames[0]["id"], expression="left", context="watch"))
    check(left["result"] == "19", str(left))
    return before


def managed_after(managed):
    after = managed.event("stopped")
    frames = managed.request("stackTrace", dict(threadId=after["threadId"]))["stackFrames"]
    check(frames[0]["line"] == line(FIXTURE / "Program.cs", "MANAGED_AFTER"), str(frames[0]))
    value = managed.request("evaluate", dict(frameId=frames[0]["id"], expression="result", context="watch"))
    check(value["result"] == "42", str(value))
    return after


def managed_baseline(adapter):
    with Dap(adapter, "managed-only-baseline") as managed:
        before = start_managed(managed)
        managed.request("continue", dict(threadId=before["threadId"]))
        after = managed_after(managed)
        managed.request("continue", dict(threadId=after["threadId"]))
        check(managed.event("exited")["exitCode"] == 0, "Managed baseline exit")
        managed.request("disconnect")
    print("PASS managed breakpoints/stack/arguments/result across P/Invoke", flush=True)


def windows_engine(adapter, finish):
    with Dap(adapter, "windows-engine-" + finish) as session:
        session.request("initialize", dict(adapterID="dncdbg"))
        launch = session.send("launch", dict(program=str(DLL), args=["--preload", "--repeat"], nativeDebugging=True))
        session.event("initialized")
        session.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "Program.cs")),
                                              breakpoints=[dict(line=line(FIXTURE / "Program.cs", marker))
                                                           for marker in ("MANAGED_BEFORE", "MANAGED_AFTER")]))
        session.request("configurationDone")
        session.response(launch)
        capabilities = session.event("capabilities")["capabilities"]
        check(not capabilities["supportsSingleThreadExecutionRequests"] and not capabilities["supportsRestartRequest"],
              str(capabilities))
        before = session.event("stopped")
        pid = session.request("dotnet/info")["debuggeeProcessId"]
        session.request("stepIn", dict(threadId=before["threadId"]), success=False)
        session.request("continue", dict(threadId=before["threadId"], singleThread=True), success=False)
        session.request("dotnet/setNativeBreakpoint", dict(module="mixedfixture.dll",
                                                         source=dict(path=str(FIXTURE / "native.c")),
                                                         line=line(FIXTURE / "native.c", "NATIVE_BREAK")), success=False)
        session.request("dotnet/setNativeBreakpoint", dict(module="not-loaded.dll",
                                                         source=dict(path=str(FIXTURE / "native.c")),
                                                         line=11, oneShot=True), success=False)
        session.request("evaluate", dict(expression="NativeAdd(19, 23)", context="watch"), success=False)
        bp = session.request("dotnet/setNativeBreakpoint", dict(module="mixedfixture.dll",
                                                               source=dict(path=str(FIXTURE / "native.c")),
                                                               line=line(FIXTURE / "native.c", "NATIVE_BREAK"),
                                                               oneShot=True))
        check(bp["verified"] and bp["oneShot"], str(bp))
        session.request("continue", dict(threadId=before["threadId"]))
        stop = session.event("stopped")
        check(stop["description"] == "Native breakpoint", str(stop))
        check(stop["hitBreakpointIds"] == [bp["id"]], str(stop))
        threads = session.request("threads")["threads"]
        check(any(t["id"] == stop["threadId"] for t in threads), "Missing native stopped thread")
        frames = session.request("stackTrace", dict(threadId=stop["threadId"]))["stackFrames"]
        check("native_sum" in frames[0]["name"], str(frames[:3]))
        check(any("native_add" in f["name"] for f in frames), "Missing native caller")
        check(Path(frames[0]["source"]["path"]).resolve() == (FIXTURE / "native.c").resolve(), str(frames[0]))
        check(frames[0]["line"] == line(FIXTURE / "native.c", "NATIVE_BREAK"), str(frames[0]))
        check(frames[0]["endLine"] >= frames[0]["line"], str(frames[0]))
        session.request("scopes", dict(frameId=frames[0]["id"]), success=False)
        session.request("stepIn", dict(threadId=stop["threadId"]), success=False)
        session.request("continue", dict(threadId=stop["threadId"], singleThread=True), success=False)
        session.request("disconnect", dict(terminateDebuggee=False), success=False)
        check(session.request("dotnet/info")["nativeStopped"], "Rejected detach changed session state")
        if finish == "continue":
            session.request("continue", dict(threadId=stop["threadId"]))
            after = managed_after(session)
            session.request("continue", dict(threadId=after["threadId"]))
            check(session.event("exited")["exitCode"] == 0, "Windows engine exit")
            session.request("disconnect")
        elif finish == "terminate":
            session.request("terminate")
            check(session.event("exited")["exitCode"] == -1, "Native termination exit code")
            session.request("disconnect")
        elif finish == "disconnect":
            session.request("disconnect", dict(terminateDebuggee=True))
        else:
            session.process.stdin.close()
        session.process.wait(timeout=15)
        check(session.process.returncode == 0, f"Adapter exit: {session.process.returncode}")
        require_windows_exit(pid)
    print(f"PASS Windows DNCDbg native source stop/stack and {finish} lifecycle", flush=True)


def require_windows_exit(pid):
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x100000, False, pid)
    if not handle:
        check(ctypes.get_last_error() == 87, f"OpenProcess({pid}) error {ctypes.get_last_error()}")
        return
    try:
        check(kernel.WaitForSingleObject(handle, 0) == 0, f"Native target {pid} survived session shutdown")
    finally:
        check(kernel.CloseHandle(handle), "CloseHandle")


def rejected_native_configurations(adapter):
    with Dap(adapter, "native-configuration-errors") as session:
        session.request("initialize", dict(adapterID="dncdbg"))
        launch = session.send("launch", dict(program=str(DLL), nativeDebugging=True, noDebug=True))
        response = session.receive(lambda m: m.get("type") == "response" and m.get("request_seq") == launch)
        check(not response["success"], str(response))
        check("native debugging" in response["message"].lower(), str(response))
        check("retired" in response["message"].lower(), str(response))
        session.request("attach", dict(processId=-1, nativeDebugging=True), success=False)
        check(session.request("dotnet/info")["debuggeeProcessId"] == 0, "Rejected config started a process")
        session.request("disconnect")
    print("PASS unsupported native configurations are rejected before process creation", flush=True)


def coordination_probe(adapter, lldb, preload=False):
    with Dap(adapter, "managed-baseline") as managed, Dap(lldb, "coordinated-native-baseline") as native:
        stop = start_managed(managed, preload)
        pid = managed.request("dotnet/info")["debuggeeProcessId"]
        native.request("initialize", dict(adapterID="lldb", pathFormat="path", linesStartAt1=True, columnsStartAt1=True))
        attach = native.send("attach", dict(pid=pid, stopOnEntry=True))
        native.event("initialized")
        native.request("setBreakpoints", dict(source=dict(path=str(FIXTURE / "native.c")),
                                             breakpoints=[dict(line=line(FIXTURE / "native.c", "NATIVE_BREAK"))]))
        native.request("configurationDone")
        native.response(attach)
        initial = native.event("stopped")
        native.request("continue", dict(threadId=initial["threadId"]))
        managed.request("continue", dict(threadId=stop["threadId"]))
        try:
            native_stop = native.receive(lambda m: m.get("event") == "stopped", timeout=5)["body"]
        except queue.Empty:
            native.request("pause")
            paused = native.event("stopped")
            threads = native.request("threads")["threads"]
            for thread in threads:
                stack = native.request("stackTrace", dict(threadId=thread["id"], levels=8))
                print("DIAGNOSTIC", thread,
                      [f["name"] for f in stack["stackFrames"][:4]], flush=True)
            raise AssertionError(f"No native breakpoint; paused: {paused}")
        tid, _ = native_inspection(native, native_stop)
        native.request("continue", dict(threadId=tid))
        after = managed_after(managed)
        native.request("disconnect", dict(terminateDebuggee=False))
        managed.request("continue", dict(threadId=after["threadId"]))
        check(managed.event("exited")["exitCode"] == 0, "Coordinated process exit")
        managed.request("disconnect")
    print("PASS managed breakpoint -> native breakpoint -> managed result (two public DAP engines)", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adapter", type=Path, default=ROOT / "bin" / "dncdbg-dap")
    parser.add_argument("--lldb-dap", type=Path)
    parser.add_argument("--windows-engine", action="store_true",
                        help="Require the experimental Windows DNCDbg engine/DAP slice.")
    parser.add_argument("--reject-native", action="store_true",
                        help="Verify native launch/attach configuration errors without a native session.")
    parser.add_argument("--probe-coordination", action="store_true",
                        help="Require the full dual-debugger scenario; currently FAILS on macOS.")
    parser.add_argument("--preload", action="store_true",
                        help="Load the native library before attaching LLDB in the coordination probe.")
    options = parser.parse_args()
    check(not options.preload or options.probe_coordination, "--preload requires --probe-coordination")
    build()
    if options.reject_native:
        rejected_native_configurations(options.adapter.resolve())
    elif options.windows_engine:
        rejected_native_configurations(options.adapter.resolve())
        for finish in ("continue", "terminate", "disconnect", "eof"):
            windows_engine(options.adapter.resolve(), finish)
    else:
        check(options.lldb_dap is not None, "--lldb-dap is required for LLDB baselines")
        native_baseline(options.lldb_dap)
        managed_baseline(options.adapter.resolve())
        if options.probe_coordination:
            coordination_probe(options.adapter.resolve(), options.lldb_dap, options.preload)
