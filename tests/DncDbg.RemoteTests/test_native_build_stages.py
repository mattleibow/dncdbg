"""Observational native build-stage contracts; no SDK, network or compiler required."""
import hashlib
import io
import json
from pathlib import Path
import runpy
import shutil
import subprocess
import sys
import tempfile
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

    def execute(self, stream, failure=None, cache=True):
        calls = []

        def entered(stage):
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


if __name__ == "__main__":
    unittest.main()
