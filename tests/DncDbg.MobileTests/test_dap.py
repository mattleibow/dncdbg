#!/usr/bin/env python3
"""Actual stdio DAP with explicitly fake device tools/remote runtime; not mobile runtime proof."""
import json
import os
from pathlib import Path
import queue
import shutil
import socket
import struct
import subprocess
import sys
import threading
import time
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[2]
ADAPTER = ROOT / "src/DncDbg.Dap/bin/Release/net10.0/dncdbg-dap.dll"
IDENTITIES = {
    "Android": [
        "AndroidEmulator", "F741D77EAE68D01DA37161129423DEC3AA9DFBF9", "E2FAE5E1F10FA61CDE54D9CC8E6E4537C3EDCE24B8E0216E078F655B04EACC92",
        "25A4607527A49B76F797C582AFB6D45338D2DB85", "363A647DCADB9D8200F2098B9C4313A6EC38735D970B6F2890D7A9951932A52A",
        "5A52173745A3C9B69D48A6A27B9E045C5DEAC1B4", "7956F691D19540801FC55E91D7F2F7258E26C0107C3F7E2B8AF709850E0AA3DB"],
    "iOS": [
        "iOSSimulator", "6907233237423C4CB8189F481F1CF5DA", "2AD1DCECBA0C66483A774B7CC77345E47C11456320602A48BCA5E4AB40155476",
        "E5520AE2A6593A3A9CBE2DD3B8B60DA7", "A6D01239286F0141DC3D6A90E425C022F96340CA052B41757D2AB26CC84FC0DC",
        "2267F3F5CDC63995B4E0D3B89F52C148", "81CB0104D53425EC22F006C382D706370C4B2A5A99A54FE816866C25ACF06928"],
}


def tool():
    state = Path(os.environ["DNCDBG_FAKE_DEVICE"])
    args = sys.argv[1:]
    with (state / "commands.jsonl").open("a") as log:
        log.write(json.dumps([os.environ["DNCDBG_FAKE_TOOL"], *args]) + "\n")
    if "install" in args:
        (state / "installed").touch()
    if "launch" in args or ("start" in args and "--es" in args):
        endpoint = json.loads((state / "endpoint.json").read_text())
        endpoint["SessionId"] = os.environ.get("SIMCTL_CHILD_DNCDBG_SESSION_ID") or args[-1]
        (state / "endpoint.json").write_text(json.dumps(endpoint))
        (state / "Library/dncdbg-remote.json").write_text(json.dumps(endpoint))
    if "forward" in args and "--remove" not in args:
        print(json.loads((state / "endpoint.json").read_text())["Port"])
    elif "cat" in args:
        print((state / "endpoint.json").read_text())
    elif "get_app_container" in args:
        if (state / "installed").exists():
            print(state)
    elif "list" in args:
        print(json.dumps({"devices": {"test": [{"udid": "test-simulator", "state": "Booted"}]}}))
    elif "resolve-activity" in args:
        print("com.dncdbg.mobiletests/.MainActivity")
    elif "getprop" in args:
        print("1")
    elif "launch" in args:
        print("com.dncdbg.mobiletests: 123")


class Remote:
    def __init__(self, state, platform, model_version=2):
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen()
        self.endpoint = {"Port": self.listener.getsockname()[1], "Token": "a" * 64, "ProcessId": 123}
        (state / "endpoint.json").write_text(json.dumps(self.endpoint))
        (state / "Library").mkdir(exist_ok=True)
        (state / "Library/dncdbg-remote.json").write_text(json.dumps(self.endpoint))
        self.platform = platform
        self.model_version = model_version
        self.operations = []
        self.thread = threading.Thread(target=self.serve, daemon=True)
        self.thread.start()

    def serve(self):
        try:
            with self.listener.accept()[0] as connection:
                stream = connection.makefile("rb")
                pending = []
                generation = 101
                while header := stream.read(8):
                    magic, size = struct.unpack("<II", header)
                    assert magic == 0x31434e44
                    request = json.loads(stream.read(size))
                    op, body = request["Operation"], request["Body"]
                    self.operations.append(op)
                    error, result = None, {}
                    if op == 1:
                        identity = IDENTITIES[self.platform]
                        result = {
                            "Runtime": "explicit test fake", "Platform": self.platform,
                            "ManagedDebugging": True, "Blocker": None, "ModelVersion": self.model_version,
                            "Provider": "private-coreclr", "Bootstrap": "private-dbi-v1 (experimental; runtime-version-fragile)",
                            "SupportedBuild": dict(zip(
                                ["Platform", "RuntimeBuildId", "RuntimeHash", "DbiBuildId", "DbiHash", "DacBuildId", "DacHash"],
                                identity)) | {"Architecture": "Arm64", "RuntimeVersion": "11.0.0-rc.1.26425.128"},
                            "Features": [
                                "sourceBreakpoints", "threads", "modules", "scopes", "variables",
                                "safeEvaluate", "pause", "stepIn", "next", "stepOut", "exceptions"],
                        }
                    elif op == 3:
                        pending = [{"Kind": "stopped", "Generation": generation, "Stop": {
                            "Id": generation, "Reason": "breakpoint", "ThreadId": 7}}]
                    elif op == 11:
                        result, pending = pending, []
                    elif op == 12:
                        result = [{"Id": 7, "Name": "main"}]
                    elif op == 13:
                        result = [{"Id": "module-1", "Name": "RemoteBreakpointApp", "Path": "fixture.dll",
                                   "Mvid": "0" * 32, "Generation": 1}]
                    elif op == 5:
                        result = [{"Handle": generation + 1, "Name": "Program.Main", "Source": None,
                                   "Line": 0, "StopId": generation, "Mvid": "0" * 32,
                                   "MethodToken": 0x06000001, "IlOffset": 0}]
                    elif op == 14:
                        result = [{"Name": "Locals", "Handle": generation + 2, "Count": 4}]
                    elif op == 15:
                        result = [{"Name": "answer", "Value": "42", "Type": "int", "Handle": 0},
                                  {"Name": "message", "Value": '"mobile"', "Type": "string", "Handle": 0},
                                  {"Name": "numbers", "Value": "int[2]", "Type": "int[]",
                                   "Handle": generation + 3, "IndexedVariables": 2}]
                    elif op == 6:
                        if body["Expression"] == "answer + 1":
                            result = {"Value": "43", "Type": "int", "Handle": 0}
                        else:
                            error = "UnsupportedExpression"
                    elif op in (16, 17):
                        generation += 10
                        pending = [{"Kind": "stopped", "Generation": generation, "Stop": {
                            "Id": generation, "Reason": "step" if op == 16 else "pause", "ThreadId": 7}}]
                    response = {**request, "Body": result, "Error": error}
                    payload = json.dumps(response).encode()
                    connection.sendall(struct.pack("<II", 0x31434e44, len(payload)) + payload)
                    if op == 9:
                        return
        except (OSError, ValueError):
            pass

    def close(self):
        self.listener.close()
        self.thread.join(timeout=2)


class Dap:
    def __init__(self, env, timeout=15):
        self.process = subprocess.Popen(["dotnet", str(ADAPTER)], cwd=ROOT, env=env,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.messages = queue.Queue()
        self.history = []
        self.sequence = 0
        self.timeout = timeout
        self.stderr = ""
        threading.Thread(target=self.read, daemon=True).start()

    def read(self):
        while line := self.process.stdout.readline():
            if not line.startswith(b"Content-Length: "):
                continue
            length = int(line.split(b":")[1])
            self.process.stdout.readline()
            self.messages.put(json.loads(self.process.stdout.read(length)))

    def send(self, command, arguments=None):
        self.sequence += 1
        data = json.dumps({"seq": self.sequence, "type": "request", "command": command,
                           "arguments": arguments or {}}).encode()
        self.process.stdin.write(f"Content-Length: {len(data)}\r\n\r\n".encode() + data)
        self.process.stdin.flush()
        return self.sequence

    def wait(self, predicate):
        deadline = time.monotonic() + self.timeout
        for item in self.history:
            if predicate(item):
                self.history.remove(item)
                return item
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("The expected DAP message did not arrive within the absolute request deadline.")
            item = self.messages.get(timeout=remaining)
            if predicate(item):
                return item
            self.history.append(item)

    def response(self, sequence, success=True):
        response = self.wait(lambda m: m.get("type") == "response" and m.get("request_seq") == sequence)
        if response["success"] != success:
            raise AssertionError(response)
        return response.get("body", {})

    def request(self, command, arguments=None, success=True):
        return self.response(self.send(command, arguments), success)

    def event(self, name):
        return self.wait(lambda m: m.get("event") == name).get("body", {})

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.process.stdout.close()
        if not self.process.stderr.closed:
            self.stderr = self.process.stderr.read().decode(errors="replace")
        self.process.stderr.close()


class MobileDapTests(unittest.TestCase):
    def setUp(self):
        self.state = ROOT / "build/mobile-dap-tests" / uuid.uuid4().hex
        (self.state / "tools").mkdir(parents=True)
        for name in ("adb", "xcrun"):
            path = self.state / "tools" / name
            path.write_text("#!" + sys.executable + "\nimport runpy, os\n"
                            "os.environ['DNCDBG_FAKE_TOOL'] = " + repr(name) + "\n"
                            "runpy.run_path(" + repr(str(Path(__file__))) + ", run_name='__tool__')\n")
            path.chmod(0o755)
        (self.state / "fixture.apk").touch()
        (self.state / "Fixture.app").mkdir()
        self.env = {**os.environ, "PATH": str(self.state / "tools") + os.pathsep + os.environ["PATH"],
                    "DNCDBG_FAKE_DEVICE": str(self.state)}
        self.dap = Dap(self.env)
        self.dap.request("initialize", {"adapterID": "dncdbg", "linesStartAt1": True})

    def tearDown(self):
        self.dap.close()
        if hasattr(self, "remote"):
            self.remote.close()
        if hasattr(self, "old_remote"):
            self.old_remote.close()
        shutil.rmtree(self.state)

    def options(self, platform):
        return {"platform": platform, "appPath": str(self.state / ("fixture.apk" if platform == "android" else "Fixture.app")),
                  "applicationId": "com.dncdbg.mobiletests",
                  "deviceId": "emulator-5556" if platform == "android" else "test-simulator",
                  "runtimeIdentifier": "android-arm64" if platform == "android" else "iossimulator-arm64",
                  "assetsPath": str(ROOT / "tests/RemoteBreakpointApp/bin/Release/net10.0"),
                  "uninstallOnDisconnect": True}

    def launch(self, platform):
        self.remote = Remote(self.state, "Android" if platform == "android" else "iOS")
        launch = self.dap.send("launch", {"mobile": self.options(platform)})
        self.dap.request("setExceptionBreakpoints", {"filters": ["all"]})
        self.dap.request("configurationDone")
        self.dap.response(launch)
        self.dap.event("stopped")

    def test_android_full_dap_and_owned_forward(self):
        self.launch("android")
        self.assertEqual(self.dap.request("threads")["threads"][0]["id"], 7)
        self.assertEqual(self.dap.request("modules")["totalModules"], 1)
        frame = self.dap.request("stackTrace", {"threadId": 7})["stackFrames"][0]["id"]
        scope = self.dap.request("scopes", {"frameId": frame})["scopes"][0]["variablesReference"]
        values = self.dap.request("variables", {"variablesReference": scope})["variables"]
        self.assertEqual(values[0]["value"], "42")
        self.assertEqual(values[1]["value"], '"mobile"')
        self.assertEqual(values[2]["indexedVariables"], 2)
        self.assertEqual(self.dap.request("evaluate", {"frameId": frame, "expression": "answer + 1"})["result"], "43")
        self.dap.request("evaluate", {"frameId": frame, "expression": "Dangerous()"}, success=False)
        self.dap.request("next", {"threadId": 7})
        self.assertEqual(self.dap.event("stopped")["reason"], "step")
        self.dap.request("scopes", {"frameId": frame}, success=False)
        self.dap.request("continue", {"threadId": 7})
        self.dap.request("pause", {"threadId": 7})
        self.assertEqual(self.dap.event("stopped")["reason"], "pause")
        self.dap.request("readMemory", {"memoryReference": "0x0", "count": 1}, success=False)
        self.dap.request("cancel", {"requestId": 999}, success=False)
        self.dap.request("disconnect", {"terminateDebuggee": True})
        self.dap.event("terminated")
        self.dap.close()
        commands = [json.loads(x) for x in (self.state / "commands.jsonl").read_text().splitlines()]
        forwards = [c for c in commands if "forward" in c]
        self.assertEqual(forwards, [["adb", "-s", "emulator-5556", "forward", "--no-rebind", "tcp:0",
                                     f"tcp:{self.remote.endpoint['Port']}"],
                                    ["adb", "-s", "emulator-5556", "forward", "--remove",
                                     f"tcp:{self.remote.endpoint['Port']}"]])
        self.assertFalse(any("reverse" in c or "--remove-all" in c for c in commands))
        self.assertTrue(any("uninstall" in c for c in commands))

    def test_ios_host_networking_detach(self):
        self.launch("iosSimulator")
        self.dap.request("disconnect", {"terminateDebuggee": False})
        self.dap.event("terminated")
        self.dap.close()
        commands = [json.loads(x) for x in (self.state / "commands.jsonl").read_text().splitlines()]
        self.assertFalse(any(c[0] == "adb" or "uninstall" in c for c in commands))
        self.assertIn(9, self.remote.operations)

    def test_physical_android_rejected_before_deployment(self):
        options = self.options("android") | {"deviceId": "physical-device"}
        self.dap.request("launch", {"mobile": options}, success=False)
        self.assertFalse((self.state / "commands.jsonl").exists())

    def test_model_mismatch_cleans_owned_device_route(self):
        self.remote = Remote(self.state, "Android", model_version=99)
        self.dap.request("launch", {"mobile": self.options("android")}, success=False)
        self.assertEqual(self.remote.operations, [1, 9])
        commands = [json.loads(x) for x in (self.state / "commands.jsonl").read_text().splitlines()]
        self.assertTrue(any("forward" in c and "--remove" in c for c in commands))
        self.assertTrue(any("uninstall" in c for c in commands))

    def test_official_provider_never_falls_back_to_private(self):
        self.dap.request("launch", {"mobile": self.options("android") | {"provider": "remotemscordbihost"}}, success=False)
        self.assertFalse((self.state / "commands.jsonl").exists())

    def test_unknown_tunnel_never_deploys(self):
        self.dap.request("launch", {"mobile": self.options("android") | {"tunnel": {"provider": "physical-device"}}}, success=False)
        self.assertFalse((self.state / "commands.jsonl").exists())

    def test_desktop_environment_options_are_not_silently_ignored(self):
        self.dap.request("launch", {"mobile": self.options("android"), "env": {"IGNORED": "not-allowed"}}, success=False)
        self.assertFalse((self.state / "commands.jsonl").exists())

    def test_eof_releases_owned_forward(self):
        self.launch("android")
        self.dap.close()
        commands = [json.loads(x) for x in (self.state / "commands.jsonl").read_text().splitlines()]
        self.assertTrue(any("forward" in c and "--remove" in c for c in commands))


if __name__ == "__tool__":
    tool()
elif __name__ == "__main__":
    unittest.main()
