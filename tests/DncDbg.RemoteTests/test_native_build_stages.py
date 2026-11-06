"""Observational native build-stage contracts; no SDK, network or compiler required."""
import hashlib
import io
import json
import os
from pathlib import Path
import runpy
import selectors
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "src/DncDbg.Remote.Target/build.py"


class FlushedEvents(io.StringIO):
    def __init__(self):
        super().__init__()
        self.visible = ""

    def flush(self):
        self.visible = self.getvalue()

    def events(self):
        return [json.loads(line) for line in self.visible.splitlines() if line.startswith("{")]


class NativeBuildStages(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="native-stages.", dir=ROOT / "build"))
        self.source = self.root / "src/DncDbg.Remote.Target"
        self.source.mkdir(parents=True)
        for name in ("build.py", "Bootstrap.cpp", "RemoteContract.g.h"):
            (self.source / name).write_bytes((SOURCE.parent / name).read_bytes())
        self.header = b"fixture pinned header"
        (self.source / "dependencies.json").write_text(json.dumps({
            "json": {"url": "https://invalid.example/credential-must-not-appear",
                     "sha256": hashlib.sha256(self.header).hexdigest()}}))
        self.deps = self.root / "build/native-private/deps"
        self.deps.mkdir(parents=True)
        (self.deps / "json.hpp").write_bytes(self.header)

    def tearDown(self):
        shutil.rmtree(self.root)

    def execute(self, stream, failure=None, cache=True, observe=True):
        calls = []

        def entered(stage):
            if not observe:
                return
            self.assertTrue(any(event["stage"] == stage and event["phase"] == "begin"
                                for event in stream.events()), f"Unflushed {stage} entry before blocking tool")

        def query(command, **kwargs):
            entered("sdk-probe")
            calls.append(command)
            return "/fixture-sdk\n"

        def run(command, **kwargs):
            stage = "archive" if command[:2] == ["xcrun", "ar"] else "native-compile"
            entered(stage)
            calls.append(command)
            if failure is not None:
                raise failure
            output = Path(command[3] if stage == "archive" else command[command.index("-o") + 1])
            output.write_bytes(b"fixture native output")
            return subprocess.CompletedProcess(command, 0)

        def download(*args, **kwargs):
            entered("dependency-download")
            self.assertEqual(kwargs["timeout"], 30)
            return io.BytesIO(self.header)

        if not cache:
            (self.deps / "json.hpp").unlink()
        with patch.object(sys, "argv", [str(self.source / "build.py"), "iossimulator-x64",
                                       "--configuration", "Debug"]), patch("sys.stderr", stream), \
                patch("subprocess.check_output", side_effect=query), patch("subprocess.run", side_effect=run), \
                patch("urllib.request.urlopen", side_effect=download) as request:
            runpy.run_path(str(self.source / "build.py"), run_name="__main__")
        self.assertEqual(request.call_count, 0 if cache else 1)
        return calls

    def test_stage_entries_are_flushed_before_tools_and_identity_inputs_are_unchanged(self):
        stream = FlushedEvents()
        calls = self.execute(stream)
        events = stream.events()
        self.assertLessEqual(len(events), 32)
        self.assertEqual([event["sequence"] for event in events], list(range(1, len(events) + 1)))
        self.assertNotIn("credential-must-not-appear", stream.getvalue())
        compile_command = next(command for command in calls if command[:2] == ["xcrun", "clang++"])
        for flag in ("-O1", "-g", "-fPIC", "-fvisibility=hidden", "-Werror"):
            self.assertIn(flag, compile_command)
        self.assertIn("x86_64-apple-ios15.0-simulator", compile_command)
        identity = hashlib.sha256()
        for path in sorted(self.source.iterdir()):
            if path.suffix in (".cpp", ".h", ".json"):
                identity.update(path.name.encode())
                identity.update(path.read_bytes())
        identity.update(b"iossimulator-x64:Debug:False")
        result = json.loads((self.root / "build/native-private/iossimulator-x64/dncdbg-agent.json").read_text())
        self.assertEqual(result["BuildId"], identity.hexdigest())

    def test_failed_tool_retains_the_exact_original_exception(self):
        original = RuntimeError("original compiler failure")
        with self.assertRaises(RuntimeError) as raised:
            self.execute(FlushedEvents(), failure=original)
        self.assertIs(raised.exception, original)

    def test_cache_miss_download_is_visible_before_the_unchanged_bounded_request(self):
        self.execute(FlushedEvents(), cache=False)

    def test_hash_failure_retains_the_original_rejection_before_any_tool(self):
        (self.deps / "json.hpp").write_bytes(b"wrong pinned content")
        stream = FlushedEvents()
        with self.assertRaisesRegex(SystemExit, "Pinned JSON dependency hash mismatch"):
            self.execute(stream)
        self.assertTrue(any(event["stage"] == "dependency-verify" and event["phase"] == "error"
                            for event in stream.events()))

    def test_diagnostic_writer_failure_cannot_mask_tool_failure_or_cancellation(self):
        class Broken(io.StringIO):
            def write(self, text):
                raise OSError("diagnostic disk full")

        for original in (RuntimeError("exact tool failure"), KeyboardInterrupt("exact cancellation")):
            with self.subTest(kind=type(original).__name__):
                with self.assertRaises(type(original)) as raised:
                    self.execute(Broken(), failure=original, observe=False)
                self.assertIs(raised.exception, original)

    def test_real_stderr_pipe_exposes_sdk_entry_before_barrier_release(self):
        worker = f"""
import runpy,subprocess,sys
from pathlib import Path
from unittest.mock import patch
def sdk(*args,**kwargs):
    print("READY",flush=True)
    sys.stdin.readline()
    return "/fixture-sdk"
def run(command,**kwargs):
    output=Path(command[3] if command[:2]==["xcrun","ar"] else command[command.index("-o")+1])
    output.write_bytes(b"fixture native output")
    return subprocess.CompletedProcess(command,0)
sys.argv=[{str(self.source / "build.py")!r},"iossimulator-x64","--configuration","Debug"]
with patch("subprocess.check_output",sdk),patch("subprocess.run",run):
    runpy.run_path(sys.argv[0],run_name="__main__")
"""
        process = subprocess.Popen([sys.executable, "-I", "-c", worker], stdin=subprocess.PIPE,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            with selectors.DefaultSelector() as selector:
                selector.register(process.stdout, selectors.EVENT_READ)
                self.assertTrue(selector.select(5), "SDK barrier was not reached")
            self.assertEqual(process.stdout.readline().strip(), "READY")
            observed = []
            data = b""
            deadline = time.monotonic() + 5
            os.set_blocking(process.stderr.fileno(), False)
            with selectors.DefaultSelector() as selector:
                selector.register(process.stderr, selectors.EVENT_READ)
                while not any(event["stage"] == "sdk-probe" and event["phase"] == "begin" for event in observed):
                    self.assertTrue(selector.select(max(0, deadline - time.monotonic())), "Missing flushed SDK entry")
                    part = os.read(process.stderr.fileno(), 65536)
                    self.assertTrue(part, "Native stage stream closed before SDK entry")
                    data += part
                    while b"\n" in data:
                        line, data = data.split(b"\n", 1)
                        observed.append(json.loads(line))
            os.set_blocking(process.stderr.fileno(), True)
            self.assertIsNone(process.poll())
            output, error = process.communicate("\n", timeout=5)
            self.assertEqual(process.returncode, 0, error)
            self.assertIn("libdncdbg_target.a", output)
            observed += [json.loads(line) for line in error.splitlines() if line.startswith("{")]
            self.assertLessEqual(len(observed), 32)
        finally:
            if process.poll() is None:
                process.kill()
            process.communicate(timeout=5)

    def test_broken_diagnostic_pipe_cannot_change_success_at_interpreter_shutdown(self):
        worker = f"""
import runpy,subprocess,sys
from pathlib import Path
from unittest.mock import patch
def run(command,**kwargs):
    output=Path(command[3] if command[:2]==["xcrun","ar"] else command[command.index("-o")+1])
    output.write_bytes(b"fixture native output")
    return subprocess.CompletedProcess(command,0)
sys.argv=[{str(self.source / "build.py")!r},"iossimulator-x64","--configuration","Debug"]
with patch("subprocess.check_output",return_value="/fixture-sdk"),patch("subprocess.run",run):
    runpy.run_path(sys.argv[0],run_name="__main__")
"""
        read, write = os.pipe()
        os.close(read)
        try:
            with subprocess.Popen([sys.executable, "-I", "-c", worker], stdin=subprocess.DEVNULL,
                                  stdout=subprocess.PIPE, stderr=write, text=True) as process:
                os.close(write)
                write = None
                output, _ = process.communicate(timeout=5)
                self.assertEqual(process.returncode, 0)
                self.assertIn("libdncdbg_target.a", output)
        finally:
            if write is not None:
                os.close(write)


if __name__ == "__main__":
    unittest.main()
