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
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[2]
ADAPTER = ROOT / "src/DncDbg.Dap/bin/Release/net10.0/dncdbg-dap.dll"


def tool():
    state = Path(os.environ["DNCDBG_FAKE_DEVICE"])
    args = sys.argv[1:]
    with (state / "commands.jsonl").open("a") as log:
        log.write(json.dumps([Path(sys.argv[0]).name, *args]) + "\n")
    if "forward" in args and "--remove" not in args:
        print(json.loads((state / "endpoint.json").read_text())["Port"])
    elif "cat" in args:
        print((state / "endpoint.json").read_text())
    elif "get_app_container" in args:
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
    def __init__(self, state, platform):
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen()
        self.endpoint = {"Port": self.listener.getsockname()[1], "Token": "a" * 64}
        (state / "endpoint.json").write_text(json.dumps(self.endpoint))
        (state / "Library").mkdir()
        (state / "Library/dncdbg-remote.json").write_text(json.dumps(self.endpoint))
        self.platform = platform
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
                        result = {
                            "Runtime": "explicit test fake", "Platform": self.platform,
                            "ManagedDebugging": True, "Blocker": None, "ModelVersion": 2,
                            "Provider": "contract-test", "Features": [
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
    def __init__(self, env):
        self.process = subprocess.Popen(["dotnet", str(ADAPTER)], cwd=ROOT, env=env,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.messages = queue.Queue()
        self.history = []
        self.sequence = 0
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
        for item in self.history:
            if predicate(item):
                self.history.remove(item)
                return item
        while True:
            item = self.messages.get(timeout=15)
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
        self.process.stderr.close()


class MobileDapTests(unittest.TestCase):
    def setUp(self):
        self.state = ROOT / "build/mobile-dap-tests" / uuid.uuid4().hex
        (self.state / "tools").mkdir(parents=True)
        for name in ("adb", "xcrun"):
            path = self.state / "tools" / name
            path.write_text("#!" + sys.executable + "\nimport runpy\n"
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
        shutil.rmtree(self.state)

    def launch(self, platform):
        self.remote = Remote(self.state, "Android" if platform == "android" else "iOS")
        mobile = {"platform": platform, "appPath": str(self.state / ("fixture.apk" if platform == "android" else "Fixture.app")),
                  "applicationId": "com.dncdbg.mobiletests",
                  "deviceId": "emulator-5556" if platform == "android" else "test-simulator",
                  "runtimeIdentifier": "android-arm64" if platform == "android" else "iossimulator-arm64",
                  "assetsPath": str(ROOT / "tests/RemoteBreakpointApp/bin/Release/net10.0"),
                  "provider": "contract-test", "uninstallOnDisconnect": True}
        # Contract-test providers are permitted only by this host-test opt-in, never by target startup policy.
        self.env["DNCDBG_MOBILE_CONTRACT_TEST"] = "1"
        self.dap.process.stdin.close()
        self.dap.close()
        self.dap = Dap({**self.env, "DNCDBG_MOBILE_CONTRACT_TEST": "1"})
        self.dap.request("initialize", {"adapterID": "dncdbg"})
        launch = self.dap.send("launch", {"mobile": mobile})
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
        self.dap.request("launch", {"mobile": {
            "platform": "android", "appPath": str(self.state / "fixture.apk"), "applicationId": "test",
            "deviceId": "physical-device", "runtimeIdentifier": "android-arm64", "assetsPath": str(self.state)}},
            success=False)
        self.assertFalse((self.state / "commands.jsonl").exists())


if __name__ == "__tool__":
    tool()
elif __name__ == "__main__":
    unittest.main()
