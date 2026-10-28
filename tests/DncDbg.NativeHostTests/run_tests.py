"""Canonical real native-agent/DAP architecture proof. No MAUI workloads or devices."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import signal
import socket
import subprocess
import sys
import time
import traceback
import uuid

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / "tests/DncDbg.RemoteTests"))
from process_tools import run_owned
from test_dap import Dap
from install_runtime import LOCK, install


def require(condition, name):
    if not condition:
        raise AssertionError(name)
    print("PASS:", name, flush=True)


def wait_for(predicate, name, timeout=10):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(0.02)
    raise TimeoutError(name)


def alive(pid):
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False


def closed(port):
    with socket.socket() as connection:
        connection.settimeout(0.1)
        return connection.connect_ex(("127.0.0.1", port)) != 0


class Proof:
    def __init__(self, args):
        self.args = args
        self.output = args.output.resolve()
        if not self.output.is_relative_to((ROOT / "build").resolve()) or self.output.exists() and not args.cleanup:
            raise RuntimeError("Choose a fresh output directory below this checkout's build/.")
        self.output.mkdir(parents=True, exist_ok=args.cleanup)
        self.rid = ("osx-" if platform.system() == "Darwin" else "linux-") + (
            "arm64" if platform.machine() in ("arm64", "aarch64") else "x64")
        self.number = 0
        self.checks = []
        self.env = dict(os.environ)
        scratch = self.output / "scratch"
        scratch.mkdir(exist_ok=args.cleanup)
        self.env["TMPDIR"] = str(scratch) + "/"
        self.adapter = ROOT / "src/DncDbg.Dap/bin/Release/net10.0/dncdbg-dap.dll"
        self.fault_adapter = self.output / "fault-adapter/dncdbg-dap.dll"
        self.source = HERE / "Payload/Entry.cs"
        self.payload = HERE / "Payload/bin/Debug/net10.0"
        self.bin = self.output / "bin"
        self.bin.mkdir(exist_ok=args.cleanup)
        self.host = self.bin / "native-host"
        self.agent = self.bin / ("libdncdbg_target.dylib" if self.rid.startswith("osx") else "libdncdbg_target.so")

    def run(self, *command, timeout=240):
        self.number += 1
        print("RUN:", " ".join(str(c) for c in command), flush=True)
        return run_owned(*(str(c) for c in command), env=self.env, cwd=ROOT, timeout=timeout,
                         log=self.output / f"{self.number:03d}-{Path(str(command[0])).name}.log").stdout

    def build(self):
        if self.rid not in LOCK["archives"]:
            raise RuntimeError("Native architecture proof requires macOS arm64 or Linux x64/arm64.")
        if self.args.runtime_directory:
            self.runtime = self.args.runtime_directory.resolve()
        else:
            metadata = json.loads(self.run("dotnet", "msbuild", HERE / "Payload/Payload.csproj",
                                          "-getProperty:NetCoreRoot,NETCoreSdkVersion"))
            root = Path(metadata["Properties"]["NetCoreRoot"])
            candidate = root / "shared/Microsoft.NETCore.App" / LOCK["version"]
            self.runtime = candidate if candidate.is_dir() else install(self.rid, ROOT / "build/native-runtime")
        self.run("dotnet", "tests/DncDbg.RemoteTests/bin/Release/net10.0/DncDbg.RemoteTests.dll",
                 "--native-host-capabilities", self.runtime)
        self.run("dotnet", "build", HERE / "Payload/Payload.csproj", "-c", "Debug", "--nologo", "-v:q")
        metadata = json.loads(self.run("dotnet", "msbuild", HERE / "Payload/Payload.csproj",
                                      "-getProperty:NetCoreRoot", "-getItem:KnownAppHostPack"))
        pack = next(item for item in metadata["Items"]["KnownAppHostPack"] if item["TargetFramework"] == "net10.0")
        headers = Path(metadata["Properties"]["NetCoreRoot"]) / "packs" / pack["AppHostPackNamePattern"].replace(
            "**RID**", self.rid) / pack["AppHostPackVersion"] / "runtimes" / self.rid / "native"
        require((headers / "nethost.h").is_file() and (headers / "libnethost.a").is_file(),
                "public nethost/hostfxr SDK headers and static library are available")
        self.run(sys.executable, "src/DncDbg.Remote.Target/build.py", self.rid)
        shutil.copy2(ROOT / "build/native-private" / self.rid / self.agent.name, self.agent)
        require(b"dncdbg_test_stall_owner" not in self.agent.read_bytes(), "ordinary native host agent excludes watchdog fault export")
        command = ["c++", "-std=c++17", "-O0", "-g", "-Wall", "-Wextra", "-Werror",
                   "-I", headers, "-I", ROOT / "build/native-private/deps",
                   HERE / "Host.cpp", headers / "libnethost.a", "-pthread", "-o", self.host]
        command += ["-framework", "Security", "-framework", "CoreFoundation"] if self.rid.startswith("osx") else ["-ldl"]
        self.run(*command)
        self.adapter = self.bin / "release-adapter/dncdbg-dap.dll"
        self.run("dotnet", "publish", ROOT / "src/DncDbg.Dap", "-c", "Release",
                 "-p:EnableExperimentalRemoteTestHooks=true", "--artifacts-path", self.output / "release-compilation",
                 "-o", self.adapter.parent, "--nologo", "-v:q")
        self.run(sys.executable, "tests/DncDbg.RemoteTests/assert_artifacts.py", self.adapter.parent)
        self.run("dotnet", "publish", ROOT / "src/DncDbg.Dap", "-c", "Debug",
                 "-p:EnableExperimentalRemoteTestHooks=true", "--artifacts-path", self.output / "compilation",
                 "-o", self.fault_adapter.parent, "--nologo", "-v:q")
        self.run("dotnet", "tests/DncDbg.RemoteTests/bin/Release/net10.0/DncDbg.RemoteTests.dll", "--tunnel-contracts")
        self.checks.append("real-tcp-provider-contracts")
        self.run("dotnet", "tests/DncDbg.RemoteTests/bin/Release/net10.0/DncDbg.RemoteTests.dll")
        self.run(sys.executable, "src/DncDbg.Remote.Target/build.py", "--test-host")

    def options(self, directory, provider, gate=None, listen=0):
        tunnel = {"provider": provider, "writeChunkBytes": 3 if provider == "localTcpProxy" else 4096, "listenPort": listen}
        if gate:
            tunnel["testGateDirectory"] = str(gate)
        return {"hostPath": str(self.host), "agentPath": str(self.agent), "assemblyPath": str(self.payload / "Payload.dll"),
                "runtimeDirectory": str(self.runtime), "assetsPath": str(self.payload),
                "outputPath": str(directory / "targets"), "tunnel": tunnel, "startupTimeoutSeconds": 30}

    def dap(self, directory, adapter):
        result = Dap(self.env | {"DNCDBG_LOG_DIR": str(directory)}, timeout=30, adapter=adapter,
                     arguments=["--log=" + str(directory / "remote-session.log")])
        (directory / "adapter-process.json").write_text(json.dumps({"pid": result.process.pid}))
        return result

    def start(self, dap, options):
        dap.request("initialize", {"adapterID": "dncdbg"})
        pending = dap.send("launch", {"remote": options})
        line = next(i + 1 for i, row in enumerate(self.source.read_text().splitlines()) if "// NATIVE_HOST_BREAKPOINT" in row)
        breakpoint = dap.request("setBreakpoints", {"source": {"path": str(self.source)}, "breakpoints": [{"line": line}]})["breakpoints"][0]
        dap.request("setExceptionBreakpoints", {"filters": []})
        dap.request("configurationDone")
        dap.response(pending)
        info = dap.request("dotnet/info")
        wait_for(lambda: (Path(info["artifactsPath"]) / "managed-ready").exists(), "managed payload never started")
        require(info["remote"]["provider"] == "private-coreclr" and info["remote"]["modelVersion"] == 5,
                "actual native host advertises the same private provider/generated model")
        return breakpoint, info, line

    def command(self, session, sequence, command):
        staging = session / f"command-{sequence}.staging"
        staging.write_text(command)
        staging.rename(session / f"command-{sequence}")

    def result(self, session, sequence):
        path = session / f"result-{sequence}"
        wait_for(path.exists, "managed payload did not finish its native-hosted command")
        return path.read_text()

    def stack(self, dap):
        event = dap.event("stopped")
        frame = dap.request("stackTrace", {"threadId": event["threadId"]})["stackFrames"][0]
        return event, frame

    def native_cleanup(self, session):
        path = session / "native-report.jsonl"
        wait_for(lambda: path.exists() and '"Phase":"owner-finished"' in path.read_text(), "native owner did not finish")
        phases = [json.loads(row) for row in path.read_text().splitlines()]
        for phase in ("cleanup-detach-return", "terminate-return", "owner-finished"):
            matches = [row for row in phases if row["Phase"] == phase]
            require(len(matches) == 1 and matches[0]["HResult"] == "0x00000000", "native exact-once " + phase)
        require(not (session / "endpoint.json").exists(), "owned native-host endpoint credential removed")

    def complete(self, provider):
        directory = self.output / ("direct" if provider == "directLoopback" else "proxy")
        directory.mkdir()
        dap = self.dap(directory, self.adapter)
        sessions = []
        try:
            breakpoint, info, line = self.start(dap, self.options(directory, provider))
            session = Path(info["artifactsPath"])
            sessions.append(session)
            host = (session / "host.log").read_text()
            require(host.index("agent-loaded-before-runtime") < host.index("hostfxr-initialize") <
                    host.index("native-host-calls-managed-entry"), "C++ host loads the exact agent before runtime and calls the managed payload")
            self.command(session, 1, "scenario")
            stopped, frame = self.stack(dap)
            require(stopped["reason"] == "breakpoint" and frame["line"] == line and frame["source"]["path"] == str(self.source),
                    provider + " real managed source breakpoint through dncdbg-dap")
            require(stopped["hitBreakpointIds"] == [breakpoint["id"]], "stable DAP breakpoint identity from actual native callback")
            scope = dap.request("scopes", {"frameId": frame["id"]})["scopes"][0]["variablesReference"]
            values = dap.request("variables", {"variablesReference": scope})["variables"]
            by_name = {v["name"]: v for v in values}
            require(by_name["input"]["value"] == "1" and by_name["answer"]["value"] == "42", "native-host primitive/argument inspection")
            require("native hosted" in by_name["message"]["value"] and "🚀" in by_name["message"]["value"], "native-host UTF-16 string inspection")
            array = dap.request("variables", {"variablesReference": by_name["numbers"]["variablesReference"]})["variables"]
            require([v["value"] for v in array] == ["1", "42", "150"], "native-host actual array inspection")
            fields = dap.request("variables", {"variablesReference": by_name["sample"]["variablesReference"]})["variables"]
            require(any(v["name"] == "Number" and v["value"] == "42" for v in fields), "native-host actual object fields")
            for expression, expected in (("answer + 1", "43"), ("numbers[1]", "42"), ("sample.Number", "42")):
                require(dap.request("evaluate", {"frameId": frame["id"], "expression": expression})["result"] == expected,
                        "same safe remote evaluator: " + expression)
            dap.request("evaluate", {"frameId": frame["id"], "expression": "sample.ToString()"}, success=False)
            for command in ("stepIn", "next", "stepOut"):
                dap.request(command, {"threadId": stopped["threadId"]})
                stopped, step_frame = self.stack(dap)
                require(stopped["reason"] == "step", provider + " actual managed " + command)
            dap.request("scopes", {"frameId": frame["id"]}, success=False)
            dap.request("continue", {"threadId": stopped["threadId"]})
            require(self.result(session, 1) == "42", "C++-invoked managed payload resumes and completes")
            dap.request("setExceptionBreakpoints", {"filters": [], "filterOptions": [
                {"filterId": "all", "condition": "System.InvalidOperationException"}]})
            self.command(session, 2, "throw")
            stopped, frame = self.stack(dap)
            require(stopped["reason"] == "exception" and dap.request("exceptionInfo", {"threadId": stopped["threadId"]})[
                "exceptionId"] == "System.InvalidOperationException", "native-host real CLR exception")
            dap.request("setExceptionBreakpoints", {"filters": []})
            dap.request("continue", {"threadId": stopped["threadId"]})
            require(self.result(session, 2) == "handled", "native-host exception resumes into actual catch")
            dap.request("pause")
            stopped, _ = self.stack(dap)
            require(stopped["reason"] == "pause", "independent native owner pauses and resumes managed execution")
            dap.request("continue", {"threadId": stopped["threadId"]})
            require(dap.request("modules")["totalModules"] > 0 and dap.request("threads")["threads"], "actual module/thread model")
            old_pid, old_port, old_generation = info["debuggeeProcessId"], info["route"]["hostPort"], info["sessionId"]
            old_credential = json.loads((session / "endpoint.json").read_text())["Token"]
            dap.request("restart")
            info = dap.request("dotnet/info")
            session = Path(info["artifactsPath"])
            sessions.append(session)
            wait_for(lambda: (session / "managed-ready").exists(), "restart did not enter managed payload")
            require(info["sessionId"] != old_generation and info["debuggeeProcessId"] != old_pid and not alive(old_pid),
                    "restart owns a new target/endpoint/tunnel generation")
            require(json.loads((session / "endpoint.json").read_text())["Token"] != old_credential,
                    "restart rotates the real native agent credential rather than reusing a stale client session")
            wait_for(lambda: closed(old_port), "old host route remains after restart")
            dap.request("variables", {"variablesReference": scope}, success=False)
            self.command(session, 1, "scenario")
            stopped, frame = self.stack(dap)
            require(stopped["hitBreakpointIds"] == [breakpoint["id"]] and dap.request("evaluate", {
                "frameId": frame["id"], "expression": "answer + 1"})["result"] == "43",
                "restart rejects stale handles and restores stable source requests on a fresh native agent")
            dap.request("dotnet/testCloseTunnel", success=False)
            dap.request("disconnect", {"terminateDebuggee": False})
            dap.event("terminated")
            require(self.result(session, 1) == "42", "stopped detach relinquishes native callback and managed payload stays usable")
            for owned in sessions:
                self.native_cleanup(owned)
            self.command(session, 2, "exit")
            wait_for(lambda: not alive(info["debuggeeProcessId"]), "detached native host did not exit/reap")
            require("managed-entry-returned-to-native-host" in (session / "host.log").read_text(),
                    "managed entrypoint actually returns into the native caller")
            self.checks.append(provider + "-complete")
        finally:
            dap.close()
            (directory / "dap.json").write_text(json.dumps(dap.transcript, indent=2))
            (directory / "adapter-stderr.log").write_text(dap.stderr)

    def natural_completion(self, provider, exit_code, attempt):
        directory = self.output / f"natural-{provider}-{exit_code}-{attempt}"
        directory.mkdir()
        dap = self.dap(directory, self.adapter)
        try:
            _, info, _ = self.start(dap, self.options(directory, provider))
            session = Path(info["artifactsPath"])
            self.command(session, 1, "scenario")
            stopped, frame = self.stack(dap)
            require(dap.request("evaluate", {"frameId": frame["id"], "expression": "answer + 1"})["result"] == "43",
                    "natural completion is attached to the real native agent, not a detached process")
            dap.request("continue", {"threadId": stopped["threadId"]})
            require(self.result(session, 1) == "42", "attached scenario completes before natural exit")
            self.command(session, 2, "exit" if exit_code == 0 else "exit7")
            dap.timeout = 5
            require(dap.event("exited")["exitCode"] == exit_code, "attached natural completion reports the actual exit code")
            dap.event("terminated")
            wait_for(lambda: not alive(info["debuggeeProcessId"]), "naturally completed native host did not exit/reap")
            self.native_cleanup(session)
            wait_for(lambda: closed(info["route"]["hostPort"]), "natural exit left an owned route")
            time.sleep(0.1)
            events = [m["event"] for m in dap.transcript if m.get("event") in ("exited", "terminated")]
            require(events == ["exited", "terminated"], "natural exit emits exited then terminated exactly once")
            require(not any(m.get("event") == "output" and any(word in m.get("body", {}).get("output", "").lower()
                            for word in ("disconnected", "cleanup failed")) for m in dap.transcript),
                    "natural completion is not reported as transport/debugger failure")
            require("managed-entry-returned-to-native-host" in (session / "host.log").read_text(),
                    "attached completion actually returns through the native host")
            dap.request("scopes", {"frameId": frame["id"]}, success=False)
            self.checks.append(f"natural-{provider}-{exit_code}-{attempt}")
        finally:
            dap.close()
            (directory / "dap.json").write_text(json.dumps(dap.transcript, indent=2))
            (directory / "adapter-stderr.log").write_text(dap.stderr)

    def failure(self, name, stopped=False, delayed=False, collision=False, timeout=False, terminate=False, target_death=False,
                reconnect=False):
        directory = self.output / name
        directory.mkdir()
        dap = self.dap(directory, self.adapter if terminate or target_death else self.fault_adapter)
        reserve = socket.socket()
        reserve.bind(("127.0.0.1", 0))
        reserve.listen()
        unrelated = reserve.getsockname()[1]
        try:
            if delayed or collision or timeout:
                dap.request("initialize", {"adapterID": "dncdbg"})
                gate = directory / "targets/gate" if delayed or timeout else None
                options = self.options(directory, "localTcpProxy", gate=gate, listen=unrelated if collision else 0)
                if timeout:
                    options["startupTimeoutSeconds"] = 1
                pending = dap.send("launch", {"remote": options})
                port = None
                if gate:
                    allocated = gate / "allocated.json"
                    wait_for(allocated.exists, "delayed tunnel never allocated its listener")
                    port = json.loads(allocated.read_text())["port"]
                    require(not closed(port), "real tunnel listener allocated before publication")
                if delayed:
                    dap.request("terminate")
                    dap.event("terminated")
                dap.response(pending, success=False)
                if port:
                    wait_for(lambda: closed(port), "canceled/unpublished listener survived")
                require(not closed(unrelated), "rollback/collision/timeout preserves an unrelated listener")
                for session in (directory / "targets").glob("*/process.json"):
                    wait_for(lambda: not alive(json.loads(session.read_text())["pid"]), "failed acquisition left native host alive")
                    require(not (session.parent / "endpoint.json").exists() and not (session.parent / "endpoint.staging").exists(),
                            "failed acquisition removes published and unpublished native endpoint credentials")
                    host = session.parent / "host.log"
                    require("native-host-calls-managed-entry" not in host.read_text(), "no managed execution after canceled/unpublished acquisition")
            else:
                breakpoint, info, _ = self.start(dap, self.options(directory, "directLoopback" if terminate else "localTcpProxy"))
                session = Path(info["artifactsPath"])
                if stopped:
                    self.command(session, 1, "scenario")
                    event, old_frame = self.stack(dap)
                    require(event["reason"] == "breakpoint", "tunnel-death fixture owns a real outstanding native stop")
                if target_death:
                    os.kill(info["debuggeeProcessId"], signal.SIGKILL)
                else:
                    dap.request("terminate" if terminate else "dotnet/testCloseTunnel")
                dap.event("terminated")
                wait_for(lambda: not alive(info["debuggeeProcessId"]), "tunnel failure did not clean target")
                if not target_death:
                    self.native_cleanup(session)
                else:
                    require(not (session / "endpoint.json").exists(), "abrupt target EOF removes its owned endpoint credential")
                wait_for(lambda: closed(info["route"]["hostPort"]), "dead tunnel listener survived")
                if stopped and not target_death:
                    require(self.result(session, 1) == "42", "tunnel death releases the stopped managed command before native host exit")
                if reconnect:
                    old_generation = info["sessionId"]
                    dap.request("restart")
                    info = dap.request("dotnet/info")
                    session = Path(info["artifactsPath"])
                    wait_for(lambda: (session / "managed-ready").exists(), "reconnect did not start a fresh native target")
                    require(info["sessionId"] != old_generation, "reconnect replaces the dead target/tunnel generation")
                    dap.request("scopes", {"frameId": old_frame["id"]}, success=False)
                    self.command(session, 1, "scenario")
                    event, fresh_frame = self.stack(dap)
                    require(event["hitBreakpointIds"] == [breakpoint["id"]] and fresh_frame["id"] != old_frame["id"],
                            "reconnect restores source identity without reusing stale DAP handles")
                    dap.request("terminate")
                    dap.event("terminated")
                    wait_for(lambda: not alive(info["debuggeeProcessId"]), "reconnected native host did not terminate")
                    self.native_cleanup(session)
            self.checks.append(name)
        finally:
            reserve.close()
            dap.close()
            (directory / "dap.json").write_text(json.dumps(dap.transcript, indent=2))
            (directory / "adapter-stderr.log").write_text(dap.stderr)

    def emergency_cleanup(self, recovery=False):
        leaked = []
        journals = sorted(self.output.rglob("adapter-process.json")) + sorted(self.output.rglob("process.json"))
        for file in journals:
            pid = json.loads(file.read_text())["pid"]
            if not alive(pid):
                continue
            command = subprocess.run(["ps", "-p", str(pid), "-o", "args="], capture_output=True, text=True, timeout=5).stdout
            if str(file.parent) not in command:
                continue
            leaked.append(pid)
            os.kill(pid, signal.SIGTERM)
            try:
                wait_for(lambda: not alive(pid), "owned process did not terminate", timeout=2)
            except TimeoutError:
                os.kill(pid, signal.SIGKILL)
                wait_for(lambda: not alive(pid), "owned process survived bounded SIGKILL", timeout=3)
        leftover_credentials = []
        for file in self.output.rglob("process.json"):
            for name in ("endpoint.json", "endpoint.staging"):
                path = file.parent / name
                if path.exists():
                    leftover_credentials.append(str(path.relative_to(self.output)))
                    path.unlink()
        if not recovery:
            require(not leaked, "no leaked native-host processes (emergency cleanup must not make a failed gate green)")
            require(not leftover_credentials, "no leftover native credentials (recovery must not make a failed gate green)")
        return leaked + leftover_credentials

    def evidence(self, passed, error):
        destination = self.output / "evidence"
        destination.mkdir(exist_ok=True)
        head = self.env.get("DNCDBG_SOURCE_HEAD") or subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        manifest = {"head": head, "rid": self.rid, "runtime": LOCK["version"], "passed": passed,
                    "error": error, "gates": self.checks, "files": {}}
        status = subprocess.run(["git", "status", "--porcelain"], cwd=ROOT, capture_output=True, text=True)
        manifest["dirty"] = bool(status.stdout.strip()) if status.returncode == 0 else self.env.get("DNCDBG_SOURCE_DIRTY") == "true"
        manifest["binaries"] = {
            str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in (self.host, self.agent, self.payload / "Payload.dll", self.payload / "Payload.pdb",
                         self.adapter, self.fault_adapter) if path.is_file()
        }
        manifest["managedResults"] = {}
        for path in sorted(self.output.glob("*/targets/*/result-*")):
            if path.stat().st_size > 4096:
                raise RuntimeError("Managed fixture result exceeds the bounded evidence contract.")
            manifest["managedResults"][str(path.relative_to(self.output))] = path.read_text()
        total = 0
        for path in sorted(self.output.rglob("*")):
            relative = path.relative_to(self.output)
            if not path.is_file() or any(p in relative.parts for p in ("evidence", "compilation", "release-compilation", "fault-adapter", "bin", "scratch")):
                continue
            if path.name.startswith("endpoint") or path.suffix not in (".log", ".json", ".jsonl"):
                continue
            data = path.read_bytes()
            total += len(data)
            if len(data) > 8 * 1024 * 1024 or total > 100 * 1024 * 1024:
                raise RuntimeError("Native-host evidence exceeds the 8 MiB/file or 100 MiB/run bound.")
            if b'"Token"' in data or b'"token"' in data:
                raise RuntimeError("Endpoint credential-shaped native-host evidence refused.")
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            manifest["files"][str(relative)] = hashlib.sha256(data).hexdigest()
        (destination / "manifest.json").write_text(json.dumps(manifest, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "build/native-host" / uuid.uuid4().hex)
    parser.add_argument("--runtime-directory", type=Path)
    parser.add_argument("--cleanup", action="store_true", help="Recover only this run's journaled processes/listeners/credentials")
    parser.add_argument("--case", choices=["all", "natural-completion"], default="all")
    args = parser.parse_args()
    if args.cleanup and not args.output.exists():
        return 0
    proof = Proof(args)
    if args.cleanup:
        recovered = proof.emergency_cleanup(recovery=True)
        if recovered or not (proof.output / "evidence/manifest.json").exists():
            proof.evidence(False, "Interrupted native suite recovered; full proof must be rerun.")
        shutil.rmtree(proof.output / "scratch", ignore_errors=True)
        return 0
    passed, error = False, None
    def interrupted(_signal, _frame):
        raise KeyboardInterrupt("Native-host architecture run interrupted")
    signal.signal(signal.SIGTERM, interrupted)
    try:
        proof.build()
        for provider in ("directLoopback", "localTcpProxy"):
            for exit_code in (0, 7):
                for attempt in range(3):
                    proof.natural_completion(provider, exit_code, attempt)
        if args.case == "all":
            proof.complete("directLoopback")
            proof.complete("localTcpProxy")
            proof.failure("cancel-unpublished-listener", delayed=True)
            proof.failure("unpublished-listener-timeout", timeout=True)
            proof.failure("listener-collision", collision=True)
            proof.failure("tunnel-dies-running")
            proof.failure("tunnel-dies-stopped", stopped=True)
            proof.failure("terminate-running", terminate=True)
            proof.failure("terminate-stopped", terminate=True, stopped=True)
            proof.failure("target-eof-running", target_death=True)
            proof.failure("target-eof-stopped", target_death=True, stopped=True)
            proof.failure("reconnect-after-tunnel-death", stopped=True, reconnect=True)
        passed = True
    except BaseException as failure:
        error = repr(failure)
        (proof.output / "failure.log").write_text(traceback.format_exc())
        print(error, file=sys.stderr, flush=True)
    finally:
        try:
            proof.emergency_cleanup()
        except Exception as failure:
            passed, error = False, f"{error or ''}; cleanup: {failure!r}"
        try:
            proof.evidence(passed, error)
        except Exception as failure:
            passed = False
            destination = proof.output / "evidence"
            destination.mkdir(exist_ok=True)
            (destination / "manifest.json").write_text(json.dumps({
                "passed": False, "error": f"{error or ''}; evidence: {failure!r}"}, indent=2))
        shutil.rmtree(proof.output / "scratch", ignore_errors=True)
    print(f"{'PASS' if passed else 'FAIL'}: native-host architecture; evidence: {proof.output}/evidence")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
