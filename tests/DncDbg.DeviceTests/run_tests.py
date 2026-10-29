"""Real non-mobile Linux device proof through structured commands and pinned-host SSH."""
import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import traceback
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/DncDbg.NativeHostTests"))
from run_tests import Proof, require, wait_for, alive
sys.path.insert(0, str(ROOT / "tests/DncDbg.RemoteTests"))
from process_tools import start_owned, stop_owned


class DeviceProof(Proof):
    def __init__(self, args):
        super().__init__(args)
        nonce = uuid.uuid4().hex
        self.image = "dncdbg-device:" + nonce
        self.container = "dncdbg-device-" + nonce
        self.label = "org.dncdbg.device-tests=" + nonce
        self.shared = self.output / "device-sessions"
        self.shared.mkdir(exist_ok=args.cleanup)
        self.sshd = None
        self.owner = self.output / "device-owner.json"
        if args.cleanup:
            data = json.loads(self.owner.read_text())
            self.image, self.container, self.label = data["image"], data["container"], data["label"]
        else:
            self.owner.write_text(json.dumps({"image": self.image, "container": self.container, "label": self.label}))

    def build(self):
        architecture = self.run("docker", "info", "--format", "{{.OSType}}/{{.Architecture}}").strip()
        self.rid = {"linux/aarch64": "linux-arm64", "linux/arm64": "linux-arm64",
                    "linux/x86_64": "linux-x64", "linux/amd64": "linux-x64"}.get(architecture)
        require(self.rid is not None, "A real supported Linux Docker device is required; no skip or host fake")
        context = self.output / "context"
        context.mkdir()
        for name in ("global.json", "LICENSE", "THIRD-PARTY-NOTICES.md"):
            shutil.copy2(ROOT / name, context / name)
        shutil.copytree(ROOT / "src", context / "src",
                        ignore=shutil.ignore_patterns("bin", "obj", "__pycache__", "build", ".git"))
        shutil.copytree(ROOT / "tests/DncDbg.NativeHostTests/Payload", context / "tests/DncDbg.NativeHostTests/Payload",
                        ignore=shutil.ignore_patterns("bin", "obj"))
        shutil.copytree(ROOT / "tests/RemoteBreakpointApp", context / "tests/RemoteBreakpointApp",
                        ignore=shutil.ignore_patterns("bin", "obj"))
        shutil.copy2(Path(__file__).with_name("Dockerfile"), context / "Dockerfile")
        self.run("docker", "build", "--build-arg", f"DEVICE_UID={os.getuid()}",
                 "--label", self.label, "-t", self.image, context, timeout=900)
        self.run("docker", "run", "-d", "--name", self.container, "--label", self.label,
                 "--cap-add", "SYS_PTRACE", "--security-opt", "seccomp=unconfined",
                 "--publish", "127.0.0.1::22", "--mount", f"type=bind,src={self.shared},dst=/device-sessions", self.image)
        self.bundle = self.output / "bundle"
        self.run("docker", "cp", self.container + ":/opt/source-package", self.bundle)
        self.run("docker", "cp", self.container + ":/opt/dncdbg-os-packages.txt", self.output / "os-packages.log")
        self.profile = self.bundle / "target-profile.json"
        self.payload = self.bundle / "payload"
        self.host = self.bundle / "dncdbg-target-host"
        self.agent = self.bundle / "libdncdbg_target.so"
        self.runtime = Path("/opt/runtime/shared/Microsoft.NETCore.App/11.0.0-rc.1.26425.128")
        keys = self.output / "keys"
        keys.mkdir()
        self.key = keys / "identity"
        self.known = keys / "known-hosts"
        self.run("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", self.key)
        self.run("docker", "exec", self.container, "ssh-keygen", "-q", "-t", "ed25519", "-N", "",
                 "-f", "/etc/ssh/ssh_host_ed25519_key")
        self.run("docker", "cp", str(self.key) + ".pub", self.container + ":/home/debugger/.ssh/authorized_keys")
        self.run("docker", "exec", self.container, "chown", "debugger:debugger", "/home/debugger/.ssh/authorized_keys")
        self.run("docker", "exec", self.container, "chmod", "600", "/home/debugger/.ssh/authorized_keys")
        host_key = self.run("docker", "exec", self.container, "cat", "/etc/ssh/ssh_host_ed25519_key.pub").strip()
        self.ssh_port = int(self.run("docker", "port", self.container, "22/tcp").strip().rsplit(":", 1)[1])
        self.known.write_text(f"[127.0.0.1]:{self.ssh_port} {' '.join(host_key.split()[:2])}\n")
        self.ssh_log = (self.output / "sshd.log").open("w")
        self.sshd = start_owned(["docker", "exec", self.container, "/usr/sbin/sshd", "-D", "-e",
                                "-o", "PasswordAuthentication=no", "-o", "PermitRootLogin=prohibit-password",
                                "-o", "UsePAM=no"], cwd=ROOT, env=self.env, stdout=self.ssh_log, stderr=subprocess.STDOUT)
        probe = ["ssh", "-F", "none", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
                 "-o", "ConnectTimeout=2", "-o", "UserKnownHostsFile=" + str(self.known),
                 "-i", str(self.key), "-p", str(self.ssh_port), "debugger@127.0.0.1", "/bin/true"]
        def ssh_ready():
            if self.sshd.poll() is not None:
                raise RuntimeError("Owned SSH server exited: " + (self.output / "sshd.log").read_text())
            result = subprocess.run(probe, cwd=ROOT, env=self.env, capture_output=True, text=True, timeout=5)
            (self.output / "ssh-readiness.log").write_text(result.stderr)
            if result.returncode and not any(message in result.stderr for message in (
                    "Connection closed", "Connection refused", "Connection reset", "Connection timed out")):
                raise RuntimeError("Authenticated SSH readiness failed: " + result.stderr)
            return result.returncode == 0
        wait_for(ssh_ready, "Owned authenticated SSH endpoint did not become ready", timeout=15)
        require(True, "owned SSH server is responsive with exact pinned host key and client identity")
        self.adapter = self.bin / "release-adapter/dncdbg-dap.dll"
        self.run("dotnet", "publish", ROOT / "src/DncDbg.Dap", "-c", "Release",
                 "--artifacts-path", self.output / "release-compilation", "-o", self.adapter.parent, "--nologo", "-v:q")
        self.run("dotnet", "publish", ROOT / "src/DncDbg.Dap", "-c", "Debug", "-p:DncDbgTestHooks=true",
                 "--artifacts-path", self.output / "compilation", "-o", self.fault_adapter.parent, "--nologo", "-v:q")
        self.run(sys.executable, "tests/DncDbg.RemoteTests/assert_artifacts.py", self.adapter.parent)

    def start_info(self):
        root = "{targetDirectory}/bundle"
        return {"runtimeDirectory": str(self.runtime), "hostPath": root + "/dncdbg-target-host",
                "agentPath": root + "/libdncdbg_target.so", "assemblyPath": root + "/payload/Payload.dll",
                "runtimeConfigPath": root + "/payload/Payload.runtimeconfig.json",
                "mode": "component", "entryPointType": "NativeHostPayload.Entry, Payload"}

    def request(self, operation, profile=False, start=False):
        value = {"Operation": operation, "WorkspaceRoot": "/device-sessions", "SessionId": "{sessionId}"}
        if profile:
            value["Profile"] = json.loads(self.profile.read_text())
        if start:
            value["Start"] = self.start_info()
            value["IntegritySha256"] = "{bundleIntegritySha256}"
        return json.dumps(value) + "\n"

    def control(self, operation, **kwargs):
        return {"executable": "docker", "arguments": ["exec", "--user", "debugger", "-i", self.container, "dotnet",
                "/opt/control/dncdbg-target-control.dll"], "input": self.request(operation, **kwargs), "timeoutSeconds": 60}

    def options(self, directory, provider, gate=None, listen=0):
        common = {"assetsPath": str(self.payload), "outputPath": str(directory / "audit"), "startupTimeoutSeconds": 90}
        common["sourceFileMap"] = {"/workspace": str(ROOT)}
        if provider == "localTcpProxy":
            common["target"] = {"provider": "ssh", "ssh": {
                "host": "127.0.0.1", "port": self.ssh_port, "user": "debugger",
                "identityFile": str(self.key), "knownHostsFile": str(self.known),
                "controllerPath": "/opt/control/dncdbg-target-control", "workspaceRoot": "/device-sessions",
                "bundlePath": str(self.bundle), "profilePath": str(self.profile), "start": self.start_info()}}
            common["tunnel"] = {"provider": "ssh"}
        else:
            common["target"] = {"provider": "commands", "commands": {
                "profilePath": str(self.profile), "bundlePath": str(self.bundle), "targetDirectory": "/device-sessions/{sessionId}",
                "deploy": [self.control("prepare"), {"executable": "docker", "arguments": [
                    "cp", "-a", str(self.bundle) + "/.", self.container + ":/device-sessions/{sessionId}/bundle"], "timeoutSeconds": 60}],
                "start": self.control("start", profile=True, start=True), "readManifest": self.control("manifest"),
                "stop": self.control("stop"), "detach": self.control("detach"),
                "cleanup": self.control("cleanup"), "observe": self.control("observe")}}
            common["tunnel"] = {"provider": "stdio", "security": "local", "command": self.control("relay")}
        return common

    def session(self, info):
        return self.shared / info["sessionId"]

    def application_options(self, directory, provider):
        options = self.options(directory, provider)
        options["assetsPath"] = str(self.payload)
        application = self.start_info() | {
            "assemblyPath": "{targetDirectory}/bundle/payload/RemoteBreakpointApp.dll",
            "runtimeConfigPath": "{targetDirectory}/bundle/payload/RemoteBreakpointApp.runtimeconfig.json",
            "mode": "application", "entryPointType": None,
            "arguments": ["{targetDirectory}/application-result"]}
        if provider == "localTcpProxy":
            options["target"]["ssh"]["start"] = application
        else:
            command = options["target"]["commands"]["start"]
            request = json.loads(command["input"])
            request["Start"] = application
            command["input"] = json.dumps(request) + "\n"
        return options

    def process_alive(self, pid):
        result = subprocess.run(["docker", "exec", self.container, "kill", "-0", str(pid)],
                                cwd=ROOT, env=self.env, capture_output=True, timeout=15)
        return result.returncode == 0

    def crash_target(self, pid):
        self.run("docker", "exec", self.container, "kill", "-KILL", str(pid))

    def emergency_cleanup(self, recovery=False):
        unsettled = []
        if not recovery:
            def observed_exits():
                unsettled[:] = [str(path.parent.relative_to(self.shared)) for path in self.shared.rglob("process.json")
                                if not (path.parent / "exit.json").exists()]
                return not unsettled
            try: wait_for(observed_exits, "owned device worker did not publish actual process exit", timeout=5)
            except TimeoutError: pass
        credentials = list(self.shared.rglob("endpoint.json"))
        for path in self.output.rglob("adapter-process.json"):
            pid = json.loads(path.read_text())["pid"]
            if alive(pid):
                command = subprocess.check_output(["ps", "-p", str(pid), "-o", "command="], text=True)
                if str(path.parent / "remote-session.log") not in command:
                    raise RuntimeError("Refusing to stop a reused or unverified adapter PID")
                os.kill(pid, signal.SIGTERM)
                wait_for(lambda: not alive(pid), "owned device adapter did not exit", timeout=5)
        if self.sshd:
            stop_owned(self.sshd)
            self.ssh_log.close()
        key, expected = self.label.split("=", 1)
        for kind, name in (("container", self.container), ("image", self.image)):
            inspected = subprocess.run(["docker", kind, "inspect", name], cwd=ROOT, env=self.env,
                                       capture_output=True, text=True, timeout=30)
            if inspected.returncode:
                if "No such" not in inspected.stderr:
                    raise RuntimeError("Cannot verify owned Docker resource: " + inspected.stderr)
                continue
            labels = json.loads(inspected.stdout)[0]["Config"].get("Labels") or {}
            if labels.get(key) != expected:
                raise RuntimeError("Refusing to remove an unverified Docker resource")
            subprocess.run(["docker", kind, "rm", "-f", name], cwd=ROOT, env=self.env,
                           capture_output=True, check=True, timeout=30)
        for path in credentials:
            path.unlink(missing_ok=True)
        shutil.rmtree(self.output / "keys", ignore_errors=True)
        results = {str(p.relative_to(self.shared)): p.read_text() for p in self.shared.rglob("result-*")
                   if p.is_file() and p.stat().st_size < 4096}
        (self.output / "device-results.json").write_text(json.dumps(results, indent=2))
        if not recovery:
            require(not unsettled, "no live/unobserved target was hidden by emergency container removal")
            require(not credentials, "normal generic lifecycle left no credential for emergency cleanup to hide")
        return []


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "build/device-tests" / uuid.uuid4().hex)
    parser.add_argument("--runtime-directory", type=Path)
    parser.add_argument("--cleanup", action="store_true")
    parser.add_argument("--require-docker", action="store_true")
    args = parser.parse_args()
    if args.cleanup and not args.output.exists():
        return 0
    proof = DeviceProof(args)
    if args.cleanup:
        proof.emergency_cleanup(recovery=True)
        return 0
    passed, error = False, None
    try:
        proof.build()
        for provider in ("directLoopback", "localTcpProxy"):
            proof.application(provider)
            proof.complete(provider)
            proof.checks[-1] = "commands-stdio-full" if provider == "directLoopback" else "ssh-full"
            for code in (0, 7):
                proof.natural_completion(provider, code, 0)
        for name, options in [
                ("tunnel-dies-running", {}), ("tunnel-dies-stopped", {"stopped": True}),
                ("terminate-running", {"terminate": True}), ("terminate-stopped", {"terminate": True, "stopped": True}),
                ("target-eof-running", {"target_death": True}), ("target-eof-stopped", {"target_death": True, "stopped": True}),
                ("reconnect-after-tunnel-death", {"stopped": True, "reconnect": True})]:
            proof.failure(name, **options)
        passed = True
    except BaseException as failure:
        error = repr(failure)
        (proof.output / "failure.log").write_text(traceback.format_exc())
        print(error, file=sys.stderr, flush=True)
    finally:
        try: proof.emergency_cleanup()
        except Exception as failure: passed, error = False, f"{error or ''}; cleanup: {failure!r}"
        shutil.rmtree(proof.output / "context", ignore_errors=True)
        proof.evidence(passed, error)
    print(f"{'PASS' if passed else 'FAIL'} generic Linux device; evidence: {proof.output}/evidence")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
