#!/usr/bin/env python3
"""Portable actual-adapter contracts with fake device tools/runtime; not mobile proof."""
import json
import hashlib
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
import zipfile

from fake_adb import FakeAdbDaemon

ROOT = Path(__file__).resolve().parents[2]
ADAPTER = ROOT / "src/DncDbg.Dap/bin/Release/net10.0/dncdbg-dap.dll"
IDENTITIES = {
    "AndroidX64": [
        "Android", "A3ABA7AECC5E3B02CEF376BA080FEEFD5D3C992F", "CEB01AE9B0CCCE2437C768CB2EE1BE0E719D6B60903F5521AEDFA1C7D16FD736",
        "6182AB42360380FF67C9F829EBCEBEFE500D51D2", "911AEE78E1143BD419228C4D2B768DE7092DEC2586867AF07CB08F274935C3A1",
        "7EB51F456695097C1DCD69996D6929B605F9685A", "FD0CA02BF29CB75A23C7E96F53E1B10CAFB5ED0829A9DD39E3C7AB1C20B8C6E8"],
    "Android": [
        "Android", "F741D77EAE68D01DA37161129423DEC3AA9DFBF9", "E2FAE5E1F10FA61CDE54D9CC8E6E4537C3EDCE24B8E0216E078F655B04EACC92",
        "25A4607527A49B76F797C582AFB6D45338D2DB85", "363A647DCADB9D8200F2098B9C4313A6EC38735D970B6F2890D7A9951932A52A",
        "5A52173745A3C9B69D48A6A27B9E045C5DEAC1B4", "7956F691D19540801FC55E91D7F2F7258E26C0107C3F7E2B8AF709850E0AA3DB"],
    "iOS": [
        "iOSSimulator", "6907233237423C4CB8189F481F1CF5DA", "2AD1DCECBA0C66483A774B7CC77345E47C11456320602A48BCA5E4AB40155476",
        "E5520AE2A6593A3A9CBE2DD3B8B60DA7", "A6D01239286F0141DC3D6A90E425C022F96340CA052B41757D2AB26CC84FC0DC",
        "2267F3F5CDC63995B4E0D3B89F52C148", "81CB0104D53425EC22F006C382D706370C4B2A5A99A54FE816866C25ACF06928"],
}

FEATURES = ["sourceBreakpoints", "threads", "modules", "scopes", "variables", "safeEvaluate",
            "pause", "stepIn", "next", "stepOut", "exceptions"]
AGENT_IMAGE = b"fixture native agent:" + b"b" * 64
LINKED_IMAGE = b"fixture final linked executable:" + AGENT_IMAGE


def fake_agent(rid):
    apple = rid.startswith("ios")
    return {"Name": "dncdbg-target", "Rid": rid, "ProtocolVersion": 1, "ModelVersion": 5,
            "BuildId": "b" * 64, "Sha256": hashlib.sha256(LINKED_IMAGE if apple else AGENT_IMAGE).hexdigest().upper(),
            "ImageKind": "linked-executable" if apple else "native-library"}


def bundle_hash(directory):
    manifest = b""
    for path in sorted(p for p in directory.rglob("*") if p.is_file()):
        data = path.read_bytes()
        manifest += (path.relative_to(directory).as_posix() + "\0" + str(len(data)) + "\0" +
                     hashlib.sha256(data).hexdigest().upper() + "\n").encode()
    return hashlib.sha256(manifest).hexdigest().upper()


def fake_profile(platform):
    identity = dict(zip(["Platform", "RuntimeBuildId", "RuntimeHash", "DbiBuildId", "DbiHash", "DacBuildId", "DacHash"],
                        IDENTITIES[platform]))
    identity |= {"Architecture": "X64" if platform == "AndroidX64" else "Arm64",
                 "RuntimeVersion": "11.0.0-rc.1.26425.128"}
    rid = "android-x64" if platform == "AndroidX64" else "android-arm64" if platform == "Android" else "iossimulator-arm64"
    return {"SchemaVersion": 1, "Os": "ios" if platform == "iOS" else "android",
            "Architecture": identity["Architecture"].lower(), "Runtime": identity, "Agent": fake_agent(rid), "Capabilities": FEATURES}


def remote_launch(options):
    options = dict(options)
    platform = options.pop("platform")
    assets = options.pop("assetsPath")
    output = options.pop("outputPath", str(ROOT / "build/remote-sessions" / uuid.uuid4().hex))
    tunnel = options.pop("tunnel", None)
    timeout = options.pop("startupTimeoutSeconds", 60)
    provider = options.pop("provider", "android" if platform == "android" else "appleSimulator")
    remote = {"assetsPath": assets, "outputPath": output, "startupTimeoutSeconds": timeout,
              "target": {"provider": provider, "android" if platform == "android" else "apple": options}}
    if tunnel is not None:
        remote["tunnel"] = tunnel
    return {"remote": remote}


def tool():
    state = Path(os.environ["DNCDBG_FAKE_DEVICE"])
    args = sys.argv[1:]
    with (state / "commands.jsonl").open("a") as log:
        log.write(json.dumps([os.environ["DNCDBG_FAKE_TOOL"], *args]) + "\n")
    name = os.environ["DNCDBG_FAKE_TOOL"]
    if name == "aapt2":
        print("package: name='com.dncdbg.mobiletests' versionCode='1' versionName='1.0'")
        return
    if name == "java":
        if (state / "bad-signature").exists():
            sys.exit(1)
        print("Signer #1 certificate SHA-256 digest: " + "e" * 64)
        return
    if name == "plutil":
        print({"CFBundleExecutable": "DncDbg.MauiTestApp", "CFBundleIdentifier": "com.dncdbg.mobiletests",
               "CFBundleVersion": "1", "CFBundleShortVersionString": "1.0"}[args[1]])
        return
    if name == "codesign":
        if "-d" in args:
            print("CDHash=" + "f" * 40, file=sys.stderr)
        return
    if "forward" in args and (state / "daemon-ready.json").exists():
        import runpy
        runpy.run_path(str(Path(__file__).with_name("fake_adb.py")), run_name="__client__")
        return
    if "install" in args:
        if (state / "block-install").exists():
            (state / "install-blocked").touch()
            while not (state / "release-install").exists():
                time.sleep(0.01)
        (state / "installed").touch()
        if name == "adb":
            shutil.copyfile(args[-1], state / "installed.apk")
            if (state / "tamper-installed").exists():
                with (state / "installed.apk").open("ab") as package:
                    package.write(b"installed bytes differ despite matching claimed build ID")
        else:
            shutil.copytree(args[-1], state / "Installed.app", dirs_exist_ok=True)
            if (state / "tamper-installed").exists():
                (state / "Installed.app/DncDbg.MauiTestApp").write_bytes(b"installed linked image mismatch")
    if "uninstall" in args:
        (state / "installed").unlink(missing_ok=True)
        (state / "installed.apk").unlink(missing_ok=True)
        shutil.rmtree(state / "Installed.app", ignore_errors=True)
    if "pull" in args:
        shutil.copyfile(state / "installed.apk", args[-1])
        return
    if "tee" in args:
        (state / Path(args[-1]).name).write_text(sys.stdin.read())
    if "launch" in args or ("start" in args and "--es" in args):
        endpoint = json.loads((state / "endpoint.json").read_text())
        endpoint["SessionId"] = os.environ.get("SIMCTL_CHILD_DNCDBG_SESSION_ID") or args[-1]
        endpoint["Endpoint"]["SessionId"] = endpoint["SessionId"]
        endpoint["Process"]["OwnershipId"] = endpoint["SessionId"]
        (state / "endpoint.json").write_text(json.dumps(endpoint))
        (state / "Library/dncdbg-remote.json").write_text(json.dumps(endpoint))
    if "forward" in args and "--remove" not in args:
        print(json.loads((state / "endpoint.json").read_text())["Endpoint"]["Port"])
    elif "cat" in args:
        path = state / ("endpoint.json" if args[-1].endswith("dncdbg-remote.json") else Path(args[-1]).name)
        if path.exists():
            print(path.read_text())
        elif "exec-out" in args:
            print("cat: " + args[-1] + ": No such file or directory")
    elif "path" in args and (state / "installed").exists():
        print("package:/data/app/com.dncdbg.mobiletests/base.apk")
    elif "get_app_container" in args:
        if (state / "installed").exists():
            print(state / "Installed.app" if args[-1] == "app" else state)
    elif "list" in args:
        print(json.dumps({"devices": {"test": [{"udid": "test-simulator", "state": "Booted"}]}}))
    elif "resolve-activity" in args:
        print("com.dncdbg.mobiletests/.MainActivity")
    elif "getprop" in args:
        print("1")
    elif "launch" in args:
        print("com.dncdbg.mobiletests: 123")


class Remote:
    def __init__(self, state, platform, model_version=5, start_error=None):
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen()
        self.endpoint = {"Port": self.listener.getsockname()[1], "Token": "a" * 64, "ProcessId": 123}
        manifest = {"SchemaVersion": 1, "SessionId": "", "Profile": fake_profile(platform),
                    "Process": {"Id": 123, "Payload": "fixture.dll", "OwnershipId": ""},
                    "Endpoint": self.endpoint, "Transport": {"Host": "127.0.0.1", "RequiresEncryption": False}}
        (state / "endpoint.json").write_text(json.dumps(manifest))
        (state / "Library").mkdir(exist_ok=True)
        (state / "Library/dncdbg-remote.json").write_text(json.dumps(manifest))
        self.platform = platform
        self.model_version = model_version
        self.start_error = start_error
        self.modules = [{"Id": "module-1", "Name": "RemoteBreakpointApp", "Path": "fixture.dll",
                         "Mvid": "0" * 32, "Generation": 1}]
        self.operations = []
        self.requests = []
        self.events = queue.Queue()
        self.stack_count = 1
        self.stack_complete = True
        self.stack_omit_last = False
        self.block_operation = None
        self.operation_blocked = threading.Event()
        self.release_operation = threading.Event()
        self.thread = threading.Thread(target=self.serve, daemon=True)
        self.thread.start()

    def serve(self):
        try:
            with self.listener.accept()[0] as connection:
                stream = connection.makefile("rb")
                pending = []
                bound_breakpoints = []
                generation = 101
                while header := stream.read(8):
                    magic, size = struct.unpack("<II", header)
                    assert magic == 0x31434e44
                    request = json.loads(stream.read(size))
                    op, body = request["Operation"], request["Body"]
                    self.operations.append(op)
                    self.requests.append((op, body))
                    if op == self.block_operation:
                        self.operation_blocked.set()
                        self.release_operation.wait(timeout=10)
                    error, result = None, {}
                    if op == 1:
                        identity = IDENTITIES[self.platform]
                        result = {
                            "Runtime": "explicit test fake", "Platform": self.platform,
                            "ManagedDebugging": True, "Blocker": None, "ModelVersion": self.model_version,
                            "Provider": "coreclr", "Bootstrap": "coreclr-dbi-v1",
                            "SupportedBuild": dict(zip(
                                ["Platform", "RuntimeBuildId", "RuntimeHash", "DbiBuildId", "DbiHash", "DacBuildId", "DacHash"],
                                identity)) | {"Architecture": "X64" if self.platform == "AndroidX64" else "Arm64",
                                              "RuntimeVersion": "11.0.0-rc.1.26425.128"},
                            "Features": [
                                "sourceBreakpoints", "threads", "modules", "scopes", "variables",
                                "safeEvaluate", "pause", "stepIn", "next", "stepOut", "exceptions"],
                        }
                    elif op == 2:
                        result = [{"Line": line, "Verified": False, "Source": body["Source"]} for line in body["Lines"]]
                        bound_breakpoints = result
                    elif op == 3:
                        pending = [{"Kind": "breakpoint", "Generation": generation, "Breakpoint": bp | {"Verified": True}}
                                   for bp in bound_breakpoints] + [{"Kind": "stopped", "Generation": generation, "Stop": {
                            "Id": generation, "Reason": "breakpoint", "ThreadId": 7,
                            "HitBreakpoints": [bp | {"Verified": True} for bp in bound_breakpoints]}}]
                        error = self.start_error
                    elif op == 11:
                        result, pending = pending, []
                        while not self.events.empty():
                            result.append(self.events.get_nowait())
                    elif op == 12:
                        result = [{"Id": 7, "Name": "main"}]
                    elif op == 13:
                        result = self.modules
                    elif op == 5:
                        rows = [{"Handle": generation + 1000 + i, "Name": f"Program.Frame{i}", "Source": None,
                                   "Line": 0, "StopId": generation, "Mvid": "0" * 32,
                                   "MethodToken": 0x06000001, "IlOffset": i} for i in range(self.stack_count)]
                        start, count = body.get("Start", 0), body.get("Count", 0)
                        result = {"Frames": rows[start:start + count] if count else rows[start:],
                                  "TotalFrames": len(rows), "Complete": self.stack_complete}
                        if self.stack_omit_last:
                            result["Frames"] = result["Frames"][:-1]
                    elif op == 14:
                        result = [{"Name": "Locals", "Handle": generation + 2, "Count": 4}]
                    elif op == 15:
                        if body["Handle"] == generation + 3:
                            start, count = body.get("Start", 0), body.get("Count", 100)
                            result = [{"Name": f"[{i}]", "Value": str(i), "Type": "int", "Handle": 0}
                                      for i in range(start, min(150, start + count) if count else 150)]
                        else:
                            result = [{"Name": "answer", "Value": "42", "Type": "int", "Handle": 0},
                                  {"Name": "message", "Value": '"mobile"', "Type": "string", "Handle": 0},
                                  {"Name": "numbers", "Value": "int[150]", "Type": "int[]",
                                   "Handle": generation + 3, "IndexedVariables": 150}]
                    elif op == 6:
                        if body["Expression"].startswith("shifted"):
                            error = "UnsupportedArrayBounds"
                        elif body["Expression"] == "answer + 1":
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
        self.release_operation.set()
        self.listener.close()
        self.thread.join(timeout=2)


class Dap:
    def __init__(self, env, timeout=15, adapter=None, arguments=()):
        self.process = subprocess.Popen(["dotnet", str(adapter or ADAPTER), *arguments], cwd=ROOT, env=env,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.messages = queue.Queue()
        self.history = []
        self.transcript = []
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
            message = json.loads(self.process.stdout.read(length))
            self.transcript.append(message)
            self.messages.put(message)

    def send(self, command, arguments=None):
        self.sequence += 1
        message = {"seq": self.sequence, "type": "request", "command": command, "arguments": arguments or {}}
        self.transcript.append(message)
        data = json.dumps(message).encode()
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
        for name in ("adb", "xcrun", "java", "aapt2", "plutil", "codesign"):
            path = self.state / "tools" / name
            path.write_text("#!" + sys.executable + "\nimport runpy, os\n"
                            "import sys; sys.path.insert(0, " + repr(str(Path(__file__).parent)) + ")\n"
                            "os.environ['DNCDBG_FAKE_TOOL'] = " + repr(name) + "\n"
                            "runpy.run_path(" + repr(str(Path(__file__))) + ", run_name='__tool__')\n")
            path.chmod(0o755)
        tools = self.state / "sdk/build-tools/36.0.0"
        (tools / "lib").mkdir(parents=True)
        (tools / "lib/apksigner.jar").touch()
        shutil.copyfile(self.state / "tools/aapt2", tools / "aapt2")
        (tools / "aapt2").chmod(0o755)
        (self.state / "fixture.apk").touch()
        (self.state / "Fixture.app").mkdir()
        (self.state / "assets").mkdir()
        for extension in ("dll", "pdb"):
            shutil.copyfile(ROOT / "tests/RemoteBreakpointApp/bin/Release/net10.0" / ("RemoteBreakpointApp." + extension),
                            self.state / "assets" / ("RemoteBreakpointApp." + extension))
        self.env = {**os.environ, "PATH": str(self.state / "tools") + os.pathsep + os.environ["PATH"],
                    "DNCDBG_FAKE_DEVICE": str(self.state), "ANDROID_HOME": str(self.state / "sdk"), "JAVA_HOME": ""}
        self.dap = Dap(self.env)
        self.dap.request("initialize", {"adapterID": "dncdbg", "linesStartAt1": True})

    def tearDown(self):
        self.dap.close()
        if hasattr(self, "remote"):
            self.remote.close()
        if hasattr(self, "old_remote"):
            self.old_remote.close()
        if hasattr(self, "daemon"):
            self.daemon.close()
        shutil.rmtree(self.state)

    def options(self, platform):
        return {"platform": platform, "appPath": str(self.state / ("fixture.apk" if platform == "android" else "Fixture.app")),
                  "applicationId": "com.dncdbg.mobiletests",
                  "deviceId": "emulator-5556" if platform == "android" else "test-simulator",
                  "runtimeIdentifier": "android-arm64" if platform == "android" else "iossimulator-arm64",
                  "assetsPath": str(self.state / "assets"),
                  "uninstallOnDisconnect": True}

    def arguments(self, options):
        rid = options["runtimeIdentifier"]
        apple = rid.startswith("ios")
        if apple:
            app = self.state / "Fixture.app"
            (app / "DncDbg.MauiTestApp").write_bytes(LINKED_IMAGE)
            image = "DncDbg.MauiTestApp"
            digest = bundle_hash(app)
        else:
            app = self.state / "fixture.apk"
            image = "lib/" + ("x86_64" if rid == "android-x64" else "arm64-v8a") + "/libdncdbg_target.so"
            with zipfile.ZipFile(app, "w") as package:
                package.writestr(image, AGENT_IMAGE)
            digest = hashlib.sha256(app.read_bytes()).hexdigest().upper()
        (self.state / "assets/dncdbg-agent.json").write_text(json.dumps(fake_agent(options["runtimeIdentifier"])))
        (self.state / "assets/dncdbg-package.json").write_text(json.dumps({
            "SchemaVersion": 1, "Rid": rid, "ApplicationId": "com.dncdbg.mobiletests",
            "VersionCode": "1", "VersionName": "1.0", "SigningIdentity": ("f" * 40 if apple else "e" * 64),
            "ImagePath": image, "PackageSha256": digest, "AndroidBuildToolsVersion": None if apple else "36.0.0"}))
        return remote_launch(options)

    def launch(self, platform):
        self.remote = Remote(self.state, "Android" if platform == "android" else "iOS")
        launch = self.dap.send("launch", self.arguments(self.options(platform)))
        self.dap.request("setExceptionBreakpoints", {"filters": ["all"]})
        self.dap.request("configurationDone")
        self.dap.response(launch)
        self.dap.event("stopped")

    def test_android_same_build_id_with_changed_packaged_agent_is_rejected_before_install(self):
        self.remote = Remote(self.state, "Android")
        arguments = self.arguments(self.options("android"))
        with (self.state / "fixture.apk").open("ab") as package:
            package.write(b"changed native agent bytes with unchanged claimed build ID")
        launch = self.dap.send("launch", arguments)
        self.dap.request("setExceptionBreakpoints", {"filters": []})
        self.dap.request("configurationDone")
        self.dap.response(launch, success=False)
        commands = [json.loads(row) for row in (self.state / "commands.jsonl").read_text().splitlines()] \
            if (self.state / "commands.jsonl").exists() else []
        self.assertFalse(any("install" in command or "start" in command for command in commands))

    def test_apple_changed_final_linked_image_is_rejected_before_install(self):
        self.remote = Remote(self.state, "iOS")
        arguments = self.arguments(self.options("iosSimulator"))
        (self.state / "Fixture.app/DncDbg.MauiTestApp").write_bytes(b"tampered final linked agent image")
        launch = self.dap.send("launch", arguments)
        self.dap.request("setExceptionBreakpoints", {"filters": []})
        self.dap.request("configurationDone")
        self.dap.response(launch, success=False)
        commands = [json.loads(row) for row in (self.state / "commands.jsonl").read_text().splitlines()] \
            if (self.state / "commands.jsonl").exists() else []
        self.assertFalse(any("install" in command or "launch" in command for command in commands))

    def test_android_repacked_same_build_id_cannot_replace_the_trusted_native_hash(self):
        self.remote = Remote(self.state, "Android")
        arguments = self.arguments(self.options("android"))
        with zipfile.ZipFile(self.state / "fixture.apk", "w") as package:
            package.writestr("lib/arm64-v8a/libdncdbg_target.so", b"altered native code:" + b"b" * 64)
        path = self.state / "assets/dncdbg-package.json"
        identity = json.loads(path.read_text())
        identity["PackageSha256"] = hashlib.sha256((self.state / "fixture.apk").read_bytes()).hexdigest().upper()
        path.write_text(json.dumps(identity))
        launch = self.dap.send("launch", arguments)
        self.dap.request("configurationDone")
        self.dap.response(launch, success=False)
        self.assertFalse((self.state / "installed").exists())

    def test_installed_mobile_package_must_match_verified_signature_version_and_image(self):
        for platform in ("android", "iosSimulator"):
            with self.subTest(platform=platform):
                if hasattr(self, "remote"):
                    self.remote.close()
                self.dap.close()
                self.dap = Dap(self.env)
                self.dap.request("initialize", {"adapterID": "dncdbg"})
                (self.state / "tamper-installed").touch()
                self.remote = Remote(self.state, "Android" if platform == "android" else "iOS")
                launch = self.dap.send("launch", self.arguments(self.options(platform)))
                self.dap.request("configurationDone")
                self.dap.response(launch, success=False)
                self.assertFalse((self.state / "installed").exists(), "Failed admission must clean the owned installation.")
                commands = [json.loads(row) for row in (self.state / "commands.jsonl").read_text().splitlines()]
                self.assertTrue(any("install" in command for command in commands))
                self.assertFalse(any("start" in command or "launch" in command for command in commands),
                                 "Installed-image admission must precede target/debugger activation.")
                (self.state / "commands.jsonl").unlink()
                (self.state / "tamper-installed").unlink()

    def test_android_invalid_signature_is_rejected_before_install(self):
        self.remote = Remote(self.state, "Android")
        arguments = self.arguments(self.options("android"))
        (self.state / "bad-signature").touch()
        launch = self.dap.send("launch", arguments)
        self.dap.request("configurationDone")
        self.dap.response(launch, success=False)
        self.assertFalse((self.state / "installed").exists())

    def test_android_trusted_signer_and_version_are_explicit_admission_requirements(self):
        for field, value in (("SigningIdentity", "0" * 64), ("VersionCode", "2")):
            with self.subTest(field=field):
                if hasattr(self, "remote"):
                    self.remote.close()
                self.dap.close()
                self.dap = Dap(self.env)
                self.dap.request("initialize", {"adapterID": "dncdbg"})
                self.remote = Remote(self.state, "Android")
                arguments = self.arguments(self.options("android"))
                path = self.state / "assets/dncdbg-package.json"
                identity = json.loads(path.read_text()) | {field: value}
                path.write_text(json.dumps(identity))
                launch = self.dap.send("launch", arguments)
                self.dap.request("configurationDone")
                self.dap.response(launch, success=False)
                self.assertFalse((self.state / "installed").exists())

    def test_android_full_dap_and_owned_forward(self):
        self.launch("android")
        self.assertEqual(self.dap.request("threads")["threads"][0]["id"], 7)
        self.assertEqual(self.dap.request("modules")["totalModules"], 1)
        frame = self.dap.request("stackTrace", {"threadId": 7})["stackFrames"][0]["id"]
        scope = self.dap.request("scopes", {"frameId": frame})["scopes"][0]["variablesReference"]
        values = self.dap.request("variables", {"variablesReference": scope})["variables"]
        self.assertEqual(values[0]["value"], "42")
        self.assertEqual(values[1]["value"], '"mobile"')
        self.assertEqual(values[2]["indexedVariables"], 150)
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
        self.assertRegex(forwards[0][-1], r"^localabstract:dncdbg-[0-9a-f]{32}$")
        self.assertEqual(forwards, [["adb", "-s", "emulator-5556", "forward", "--no-rebind", "tcp:0", forwards[0][-1]],
                                    ["adb", "-s", "emulator-5556", "forward",
                                     f"tcp:{self.remote.endpoint['Port']}", f"tcp:{self.remote.endpoint['Port']}"],
                                    ["adb", "-s", "emulator-5556", "forward", "--remove",
                                     f"tcp:{self.remote.endpoint['Port']}"]])
        self.assertFalse(any("reverse" in c or "--remove-all" in c for c in commands))
        self.assertTrue(any("uninstall" in c for c in commands))

    def test_android_x64_uses_the_same_mobile_provider(self):
        self.remote = Remote(self.state, "AndroidX64")
        launch = self.dap.send("launch", self.arguments(self.options("android") | {"runtimeIdentifier": "android-x64"}))
        self.dap.request("setExceptionBreakpoints", {"filters": []})
        self.dap.request("configurationDone")
        self.dap.response(launch)
        self.dap.event("stopped")
        self.assertEqual(self.dap.request("threads")["threads"][0]["id"], 7)
        self.dap.request("disconnect", {"terminateDebuggee": True})
        self.dap.event("terminated")

    def test_android_x64_rejects_an_arm64_target_identity(self):
        self.remote = Remote(self.state, "Android")
        launch = self.dap.send("launch", self.arguments(self.options("android") | {"runtimeIdentifier": "android-x64"}))
        self.dap.response(launch, success=False)

    def test_terminate_interrupts_a_blocked_inspection_before_taking_the_state_lock(self):
        self.launch("android")
        frame = self.dap.request("stackTrace", {"threadId": 7})["stackFrames"][0]["id"]
        self.remote.block_operation = 6
        inspection = self.dap.send("evaluate", {"frameId": frame, "expression": "answer + 1"})
        self.assertTrue(self.remote.operation_blocked.wait(timeout=5), "Inspection must actually be blocked in its RPC.")
        self.dap.timeout = 3
        started = time.monotonic()
        try:
            self.dap.request("terminate")
            self.assertLess(time.monotonic() - started, 3, "Control cancellation must not wait for the stalled RPC.")
            self.dap.response(inspection, success=False)
            self.dap.event("terminated")
            self.dap.request("disconnect")
            self.dap.close()
        finally:
            self.remote.release_operation.set()
        events = [m for m in self.dap.transcript if m.get("event") == "terminated"]
        self.assertEqual(len(events), 1)
        commands = [json.loads(row) for row in (self.state / "commands.jsonl").read_text().splitlines()]
        self.assertEqual(sum("uninstall" in row for row in commands), 1, "The target is cleaned exactly once.")
        self.assertEqual(sum("--remove" in row for row in commands), 1, "The tunnel is cleaned exactly once.")

    def test_ios_host_networking_detach(self):
        self.launch("iosSimulator")
        self.dap.request("disconnect", {"terminateDebuggee": False})
        self.dap.event("terminated")
        self.dap.close()
        commands = [json.loads(x) for x in (self.state / "commands.jsonl").read_text().splitlines()]
        self.assertFalse(any(c[0] == "adb" or "uninstall" in c for c in commands))
        self.assertIn(9, self.remote.operations)

    def test_deep_stack_pages_preserve_truthful_totals(self):
        self.launch("android")
        self.remote.stack_count = 151
        for arguments, start, count in [
                ({}, 0, 0), ({"levels": 0}, 0, 0),
                ({"startFrame": 128}, 128, 0), ({"startFrame": 128, "levels": 0}, 128, 0),
                ({"startFrame": 128, "levels": 10}, 128, 10),
                ({"startFrame": 149, "levels": 10}, 149, 10),
                ({"startFrame": 151}, 151, 0), ({"startFrame": 200}, 200, 0)]:
            page = self.dap.request("stackTrace", {"threadId": 7} | arguments)
            end = min(151, start + count) if count else 151
            self.assertEqual(page["totalFrames"], 151)
            self.assertEqual([f["name"] for f in page["stackFrames"]],
                             [f"Program.Frame{i}" for i in range(start, end)])
            self.assertEqual([body for op, body in self.remote.requests if op == 5][-1],
                             {"Id": 101, "Start": start, "Count": count})
        for invalid in ({"startFrame": -1}, {"levels": -1}):
            self.dap.request("stackTrace", {"threadId": 7} | invalid, success=False)

    def test_incomplete_stack_is_never_reported_as_complete(self):
        self.launch("android")
        self.remote.stack_complete = False
        self.dap.request("stackTrace", {"threadId": 7}, success=False)
        self.remote.stack_complete = True
        self.remote.stack_omit_last = True
        self.dap.request("stackTrace", {"threadId": 7}, success=False)

    def test_array_bounds_errors_survive_host_and_dap(self):
        self.launch("android")
        frame = self.dap.request("stackTrace", {"threadId": 7})["stackFrames"][0]["id"]
        for expression in ("shifted", "shifted[0]", "shifted[5]", "shifted[149]", "shifted[150]"):
            error = self.dap.request("evaluate", {"frameId": frame, "expression": expression}, success=False)
            self.assertIn("UnsupportedArrayBounds", error["error"]["format"])

    def test_source_breakpoint_verification_events_keep_dap_identity(self):
        self.remote = Remote(self.state, "Android")
        launch = self.dap.send("launch", self.arguments(self.options("android")))
        source = ROOT / "tests/RemoteBreakpointApp/Program.cs"
        line = next(i + 1 for i, text in enumerate(source.read_text().splitlines()) if "// REMOTE_BREAKPOINT" in text)
        breakpoint = self.dap.request("setBreakpoints", {"source": {"path": str(source)},
                                                       "breakpoints": [{"line": line}]})["breakpoints"][0]
        self.assertGreater(breakpoint["id"], 0)
        self.assertFalse(breakpoint["verified"])
        self.dap.request("configurationDone")
        self.dap.response(launch)
        changed = self.dap.event("breakpoint")["breakpoint"]
        self.assertEqual(changed["id"], breakpoint["id"])
        self.assertEqual(changed["source"]["path"], str(source))
        self.assertTrue(changed["verified"])

    def test_module_reload_and_context_stops_keep_source_breakpoint_identity(self):
        self.remote = Remote(self.state, "Android")
        launch = self.dap.send("launch", self.arguments(self.options("android")))
        source = ROOT / "tests/RemoteBreakpointApp/Program.cs"
        line = next(i + 1 for i, text in enumerate(source.read_text().splitlines()) if "// REMOTE_BREAKPOINT" in text)
        bp = self.dap.request("setBreakpoints", {"source": {"path": str(source)},
                                               "breakpoints": [{"line": line}]})["breakpoints"][0]
        self.dap.request("configurationDone")
        self.dap.response(launch)
        self.assertEqual(self.dap.event("breakpoint")["breakpoint"]["id"], bp["id"])
        self.assertEqual(self.dap.event("stopped").get("hitBreakpointIds"), [bp["id"]])
        initial = self.remote.modules[0]
        for generation in range(2, 8):
            with self.subTest(generation=generation):
                self.dap.request("continue", {"threadId": 7})
                current = initial | {"Id": f"module-{generation}", "Generation": generation}
                self.remote.modules = [current]
                self.remote.events.put({"Kind": "moduleRemoved", "Generation": generation * 10, "Module": initial})
                self.remote.events.put({"Kind": "breakpoint", "Generation": generation * 10 + 1,
                                        "Breakpoint": {"Source": str(source), "Line": line, "Verified": False}})
                self.remote.events.put({"Kind": "module", "Generation": generation * 10 + 2, "Module": current})
                self.remote.events.put({"Kind": "breakpoint", "Generation": generation * 10 + 3,
                                        "Breakpoint": {"Source": str(source), "Line": line, "Verified": True}})
                self.remote.events.put({"Kind": "stopped", "Generation": generation * 10 + 4, "Stop": {
                    "Id": generation * 10 + 4, "Reason": "breakpoint", "ThreadId": 7,
                    "HitBreakpoints": [{"Source": str(source), "Line": line, "Verified": True}]}})
                removed, added = self.dap.event("module"), self.dap.event("module")
                self.assertEqual((removed["reason"], removed["module"]["id"]), ("removed", initial["Id"]))
                self.assertEqual((added["reason"], added["module"]["id"]), ("new", current["Id"]))
                for verified in (False, True):
                    changed = self.dap.event("breakpoint")["breakpoint"]
                    self.assertEqual((changed["id"], changed["verified"], changed["source"]["path"]),
                                     (bp["id"], verified, str(source)))
                self.assertEqual(self.dap.event("stopped").get("hitBreakpointIds"), [bp["id"]])
                self.assertEqual([m["id"] for m in self.dap.request("modules")["modules"]], [current["Id"]])
                initial = current

    def test_removed_source_request_ignores_late_verification_event(self):
        self.test_source_breakpoint_verification_events_keep_dap_identity()
        source = str(ROOT / "tests/RemoteBreakpointApp/Program.cs")
        self.dap.request("setBreakpoints", {"source": {"path": source}, "breakpoints": []})
        line = next(i + 1 for i, text in enumerate(Path(source).read_text().splitlines()) if "// REMOTE_BREAKPOINT" in text)
        self.remote.events.put({"Kind": "breakpoint", "Generation": 999,
                                "Breakpoint": {"Source": source, "Line": line, "Verified": True}})
        self.remote.events.put({"Kind": "module", "Generation": 1000, "Module": self.remote.modules[0]})
        self.dap.event("module")
        self.assertFalse(any(m.get("event") == "breakpoint" for m in self.dap.history))

    def test_plaintext_network_adb_rejected_before_deployment(self):
        options = self.options("android") | {"deviceId": "192.0.2.1:5555"}
        self.dap.request("launch", self.arguments(options), success=False)
        self.assertFalse((self.state / "commands.jsonl").exists())

    def test_usb_android_serial_uses_the_same_qualified_provider(self):
        self.remote = Remote(self.state, "Android")
        pending = self.dap.send("launch", self.arguments(self.options("android") | {"deviceId": "usb-serial-01"}))
        self.dap.request("configurationDone")
        self.dap.response(pending)
        self.dap.event("stopped")
        self.dap.request("disconnect")
        self.dap.event("terminated")

    def test_legacy_mobile_and_opt_in_flags_are_explicit_errors(self):
        self.dap.request("launch", {"mobile": self.options("android")}, success=False)
        arguments = self.arguments(self.options("android"))
        arguments["remote"]["experimental"] = True
        self.dap.request("launch", arguments, success=False)
        self.assertFalse((self.state / "commands.jsonl").exists())

    def test_model_mismatch_cleans_owned_device_route(self):
        self.remote = Remote(self.state, "Android", model_version=99)
        self.dap.request("launch", self.arguments(self.options("android")), success=False)
        self.assertEqual(self.remote.operations, [1, 9])
        commands = [json.loads(x) for x in (self.state / "commands.jsonl").read_text().splitlines()]
        self.assertTrue(any("forward" in c and "--remove" in c for c in commands))
        self.assertTrue(any("uninstall" in c for c in commands))

    def test_native_start_failure_cleans_before_dap_disconnect(self):
        self.remote = Remote(self.state, "Android", start_error="CorDebugCallFailed")
        launch = self.dap.send("launch", self.arguments(self.options("android")))
        self.dap.request("configurationDone", success=False)
        self.dap.response(launch, success=False)
        commands = [json.loads(x) for x in (self.state / "commands.jsonl").read_text().splitlines()]
        self.assertTrue(any("forward" in c and "--remove" in c for c in commands))
        self.assertTrue(any("uninstall" in c for c in commands))

    def test_official_provider_never_falls_back_to_private(self):
        self.dap.request("launch", self.arguments(self.options("android") | {"provider": "remotemscordbihost"}), success=False)
        self.assertFalse((self.state / "commands.jsonl").exists())

    def test_unknown_tunnel_never_deploys(self):
        self.dap.request("launch", self.arguments(self.options("android") | {"tunnel": {"provider": "unknown-route"}}), success=False)
        self.assertFalse((self.state / "commands.jsonl").exists())

    def test_desktop_environment_options_are_not_silently_ignored(self):
        self.dap.request("launch", self.arguments(self.options("android")) | {"env": {"IGNORED": "not-allowed"}}, success=False)
        self.assertFalse((self.state / "commands.jsonl").exists())

    def test_eof_releases_owned_forward(self):
        self.launch("android")
        self.dap.close()
        commands = [json.loads(x) for x in (self.state / "commands.jsonl").read_text().splitlines()]
        self.assertTrue(any("forward" in c and "--remove" in c for c in commands))

    def commands(self):
        return [json.loads(x) for x in (self.state / "commands.jsonl").read_text().splitlines()]

    def wait_for_install(self):
        deadline = time.monotonic() + 5
        while not (self.state / "install-blocked").exists():
            if time.monotonic() > deadline:
                self.fail("Install did not reach the blocking device command.")
            time.sleep(0.01)

    def terminate_during_install(self, platform, restart=False):
        if restart:
            self.launch(platform)
            self.dap.event("process")
            self.old_remote = self.remote
        self.remote = Remote(self.state, "Android" if platform == "android" else "iOS")
        (self.state / "block-install").touch()
        acquisition = self.dap.send("restart") if restart else self.dap.send("launch", self.arguments(self.options(platform)))
        self.wait_for_install()
        command_count = len(self.commands())
        before = time.monotonic()
        self.dap.request("terminate")
        (self.state / "release-install").touch()
        if not restart:
            self.dap.send("configurationDone")
        self.dap.event("terminated")
        self.dap.response(acquisition, success=False)
        self.assertLess(time.monotonic() - before, 5)
        self.dap.close()
        later = self.commands()[command_count:]
        self.assertFalse(any("launch" in c or ("start" in c and "--es" in c) for c in later), later)
        self.assertFalse(any("forward" in c and "--remove" not in c for c in later), later)
        self.assertEqual(self.remote.operations, [])
        self.assertFalse(any(m.get("event") == "process" for m in self.dap.history))
        while not self.dap.messages.empty():
            self.assertNotEqual(self.dap.messages.get().get("event"), "process")

    def test_android_terminate_during_install(self):
        self.terminate_during_install("android")

    def test_ios_terminate_during_install(self):
        self.terminate_during_install("iosSimulator")

    def test_android_terminate_during_restart_redeployment(self):
        self.terminate_during_install("android", restart=True)

    def test_ios_terminate_during_restart_redeployment(self):
        self.terminate_during_install("iosSimulator", restart=True)

    def default_restart(self, platform):
        self.remote = Remote(self.state, "Android" if platform == "android" else "iOS")
        launch = self.dap.send("launch", self.arguments(self.options(platform) | {
            "replaceExisting": False, "uninstallOnDisconnect": False}))
        self.dap.request("configurationDone")
        self.dap.response(launch)
        self.dap.event("stopped")
        self.old_remote = self.remote
        self.remote = Remote(self.state, "Android" if platform == "android" else "iOS")
        self.dap.request("restart")
        self.dap.event("stopped")
        self.dap.request("disconnect")
        self.assertEqual(sum("install" in c for c in self.commands()), 2)
        self.assertFalse(any("uninstall" in c for c in self.commands()))

    def test_android_restart_with_default_installation_policy(self):
        self.default_restart("android")

    def test_ios_restart_with_default_installation_policy(self):
        self.default_restart("iosSimulator")

    def test_unrelated_installation_is_not_replaced(self):
        for platform in ("android", "iosSimulator"):
            with self.subTest(platform=platform):
                (self.state / "installed").touch()
                self.dap.request("launch", self.arguments(self.options(platform)), success=False)
        self.assertFalse(any("install" in c for c in self.commands()))

    def test_restart_rejects_lost_installation_identity_on_both_platforms(self):
        for platform in ("android", "iosSimulator"):
            with self.subTest(platform=platform):
                self.dap.close()
                (self.state / "installed").unlink(missing_ok=True)
                self.dap = Dap(self.env)
                self.dap.request("initialize", {"adapterID": "dncdbg"})
                self.remote = Remote(self.state, "Android" if platform == "android" else "iOS")
                launch = self.dap.send("launch", self.arguments(self.options(platform) | {"uninstallOnDisconnect": False}))
                self.dap.request("configurationDone")
                self.dap.response(launch)
                self.dap.event("stopped")
                marker = self.state / ("dncdbg-installation-id" if platform == "android" else "Library/dncdbg-installation-id")
                marker.write_text("a-different-owner")
                installs = sum("install" in c for c in self.commands())
                self.dap.request("restart", success=False)
                self.assertEqual(sum("install" in c for c in self.commands()), installs)
                self.remote.close()

    def test_variables_omitted_and_zero_count_return_all_150(self):
        self.launch("android")
        frame = self.dap.request("stackTrace", {"threadId": 7})["stackFrames"][0]["id"]
        scope = self.dap.request("scopes", {"frameId": frame})["scopes"][0]["variablesReference"]
        array = self.dap.request("variables", {"variablesReference": scope})["variables"][2]["variablesReference"]
        for page in ({}, {"count": 0}):
            with self.subTest(page=page):
                values = self.dap.request("variables", {"variablesReference": array} | page)["variables"]
                self.assertEqual([v["value"] for v in values], [str(i) for i in range(150)])
        page = self.dap.request("variables", {"variablesReference": array, "start": 141, "count": 4})["variables"]
        self.assertEqual([v["value"] for v in page], ["141", "142", "143", "144"])

    def test_modules_paging_returns_all_remaining_for_omitted_or_zero_count(self):
        self.launch("android")
        self.remote.modules = [self.remote.modules[0] | {"Id": f"module-{i}", "Name": f"Module{i}"}
                               for i in range(151)]
        for arguments, expected in (
                ({}, range(151)), ({"moduleCount": 0}, range(151)),
                ({"moduleCount": 3}, range(3)), ({"startModule": 147}, range(147, 151)),
                ({"startModule": 147, "moduleCount": 0}, range(147, 151)),
                ({"startModule": 147, "moduleCount": 2}, range(147, 149)),
                ({"startModule": 150, "moduleCount": 20}, range(150, 151)),
                ({"startModule": 151}, range(0)), ({"startModule": 200, "moduleCount": 0}, range(0))):
            with self.subTest(arguments=arguments):
                result = self.dap.request("modules", arguments)
                self.assertEqual(result["totalModules"], 151)
                self.assertEqual([m["id"] for m in result["modules"]], [f"module-{i}" for i in expected])
        for arguments in ({"startModule": -1}, {"moduleCount": -1}):
            self.dap.request("modules", arguments, success=False)

    def daemon_launch(self, mode):
        self.remote = Remote(self.state, "Android")
        self.daemon = FakeAdbDaemon(self.state, mode)
        return self.dap.send("launch", self.arguments(self.options("android")))

    def assert_exact_rollback(self):
        self.assertEqual(self.daemon.mappings(), self.daemon.baseline)
        removes = [c for c in self.commands() if "forward" in c and "--remove" in c]
        self.assertEqual(removes, [["adb", "-s", "emulator-5556", "forward", "--remove",
                                    f"tcp:{self.remote.endpoint['Port']}"]])
        self.assertFalse(any("--remove-all" in c or "reverse" in c or "kill-server" in c for c in self.commands()))

    def assert_process_gone(self, pid):
        deadline = time.monotonic() + 3
        while True:
            status = subprocess.run(["ps", "-o", "stat=", "-p", str(pid)], capture_output=True, text=True).stdout.strip()
            if not status or status.startswith("Z"):
                return
            if time.monotonic() >= deadline:
                self.fail(f"Owned process {pid} is still running ({status}).")
            time.sleep(0.01)

    def cancel_committed_forward(self, mode):
        pending = self.daemon_launch(mode)
        committed = self.daemon.committed()
        self.assertEqual(len(self.daemon.mappings()), len(self.daemon.baseline) + 1)
        before = time.monotonic()
        self.dap.request("terminate")
        self.dap.event("terminated")
        self.dap.response(pending, success=False)
        self.assertLess(time.monotonic() - before, 5)
        self.assert_process_gone(committed["pid"])
        self.assert_process_gone(committed["child"])
        self.assertIsNone(self.daemon.process.poll(), "Cancellation must not terminate the shared daemon.")
        self.assert_exact_rollback()
        self.dap.request("disconnect")
        self.dap.close()
        self.assert_exact_rollback()

    def test_android_cancel_after_daemon_commit_before_cli_allocation_reply(self):
        self.cancel_committed_forward("hold-allocation")

    def test_android_cancel_after_daemon_commit_before_cli_rebind_reply(self):
        self.cancel_committed_forward("hold-rebind")

    def failed_forward(self, mode):
        pending = self.daemon_launch(mode)
        self.dap.response(pending, success=False)
        self.assert_exact_rollback()

    def test_android_forward_error_after_commit_rolls_back_exact_mapping(self):
        self.failed_forward("error-allocation")

    def test_android_forward_malformed_reply_rolls_back_exact_mapping(self):
        self.failed_forward("malformed-allocation")

    def test_android_rebind_error_after_commit_rolls_back_exact_mapping(self):
        self.failed_forward("error-rebind")

    def test_android_forward_rejected_before_commit_does_not_remove_unrelated_mapping(self):
        pending = self.daemon_launch("reject-allocation")
        self.dap.response(pending, success=False)
        self.assertEqual(self.daemon.mappings(), self.daemon.baseline)
        self.assertFalse(any("--remove" in c for c in self.commands()))

    def test_android_transaction_normal_and_duplicate_cleanup(self):
        pending = self.daemon_launch("normal")
        self.dap.request("configurationDone")
        self.dap.response(pending)
        self.dap.event("stopped")
        self.assertEqual(len(self.daemon.mappings()), len(self.daemon.baseline) + 1)
        self.dap.request("terminate")
        self.dap.event("terminated")
        self.dap.request("disconnect")
        self.dap.close()
        self.assert_exact_rollback()

    def test_android_daemon_death_terminates_waiting_client_without_hanging_cleanup(self):
        pending = self.daemon_launch("hold-allocation")
        committed = self.daemon.committed()
        before = time.monotonic()
        self.daemon.close()
        self.dap.response(pending, success=False)
        self.dap.close()
        self.assertLess(time.monotonic() - before, 5)
        self.assert_process_gone(committed["pid"])
        self.assert_process_gone(committed["child"])
        self.assertEqual(self.daemon.mappings(), [])

    def test_daemon_committed_marker_publishes_exact_mapping_snapshot(self):
        # Pause the writer *after* either direct or atomic marker publication.
        # A marker reader must already see the committed snapshot at that instant.
        script = """
import sys, time
from pathlib import Path
from fake_adb import daemon
state = Path(sys.argv[1])
write, replace = Path.write_text, Path.replace
def gate():
    while not (state / 'release-publication').exists():
        time.sleep(0.001)
def write_text(path, *args, **kwargs):
    result = write(path, *args, **kwargs)
    if path.name == 'daemon-committed.json':
        gate()
    return result
def replace_file(path, target):
    result = replace(path, target)
    if Path(target).name == 'daemon-committed.json':
        gate()
    return result
Path.write_text, Path.replace = write_text, replace_file
daemon(state, sys.argv[2])
"""
        for stage in ("allocation", "rebind"):
            state = self.state / stage
            state.mkdir()
            (state / "endpoint.json").write_text(json.dumps({"Endpoint": {"Port": 34567}}))
            process = subprocess.Popen([sys.executable, "-c", script, str(state), "hold-" + stage],
                                       cwd=Path(__file__).parent)
            def wait_file(name):
                deadline = time.monotonic() + 5
                while not (state / name).exists():
                    self.assertIsNone(process.poll())
                    self.assertLess(time.monotonic(), deadline, name)
                    time.sleep(0.001)
                return json.loads((state / name).read_text())

            def connect(args):
                port = json.loads((state / "daemon-ready.json").read_text())["port"]
                connection = socket.create_connection(("127.0.0.1", port), timeout=5)
                connection.sendall(json.dumps({"args": ["-s", "emulator-5556", "forward", *args]}).encode() + b"\n")
                return connection

            try:
                wait_file("daemon-ready.json")
                baseline = json.loads((state / "daemon-mappings.json").read_text())
                for iteration in range(20):
                    with self.subTest(stage=stage, iteration=iteration):
                        args = ["--no-rebind", "tcp:0", f"localabstract:round-{iteration}"] if stage == "allocation" else ["tcp:34567", "tcp:34567"]
                        with connect(args):
                            wait_file("daemon-committed.json")
                            snapshot = json.loads((state / "daemon-mappings.json").read_text())
                            (state / "release-publication").touch()
                        with connect(["--remove", "tcp:34567"]) as connection:
                            json.loads(connection.makefile("rb").readline())
                        self.assertEqual(json.loads((state / "daemon-mappings.json").read_text()), baseline)
                        (state / "daemon-committed.json").unlink()
                        (state / "release-publication").unlink()
                        self.assertEqual(snapshot, baseline + [["emulator-5556", "tcp:34567", args[-1]]])
            finally:
                (state / "release-publication").touch()
                process.terminate()
                process.wait(timeout=5)

    def test_exception_filter_conditions_remain_associated_with_stages(self):
        self.launch("android")
        self.dap.request("setExceptionBreakpoints", {"filters": [], "filterOptions": [
            {"filterId": "all", "condition": "System.ArgumentException"},
            {"filterId": "unhandled", "condition": "System.InvalidOperationException"}]})
        body = next(body for op, body in reversed(self.remote.requests) if op == 18)
        self.assertEqual(body, {"Rules": [
            {"Filter": "all", "Type": "System.ArgumentException"},
            {"Filter": "unhandled", "Type": "System.InvalidOperationException"}]})
        self.dap.request("setExceptionBreakpoints", {"filters": ["user-unhandled"]}, success=False)
        self.dap.request("setExceptionBreakpoints", {"filters": ["unhandled"], "filterOptions": [
            {"filterId": "all", "condition": "System.ArgumentException"}]})
        body = next(body for op, body in reversed(self.remote.requests) if op == 18)
        self.assertEqual(body["Rules"][0]["Filter"], "unhandled")
        self.assertIsNone(body["Rules"][0].get("Type"))
        self.assertEqual(body["Rules"][1], {"Filter": "all", "Type": "System.ArgumentException"})


if __name__ == "__tool__":
    tool()
elif __name__ == "__main__":
    unittest.main()
