"""Real process and filesystem contracts for the mobile runner's private journal."""
import json
import os
from pathlib import Path
import selectors
import shutil
import subprocess
import sys
import unittest
from unittest.mock import patch
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/DncDbg.MobileTests"))
import run_suite


@unittest.skipUnless(os.name == "posix", "POSIX journal locking and directory durability")
class JournalDurabilityTests(unittest.TestCase):
    def setUp(self):
        self.output = ROOT / "build/journal-contracts" / uuid.uuid4().hex
        self.output.mkdir(parents=True)

    def tearDown(self):
        shutil.rmtree(self.output)

    def ready(self, process):
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ)
            self.assertTrue(selector.select(5), "Private journal worker did not reach its barrier.")
        self.assertEqual(process.stdout.readline().strip(), "READY")

    def worker(self, script, *args):
        return subprocess.Popen([sys.executable, "-I", "-c", script, *map(str, args)], cwd=ROOT,
                                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

    def finish(self, process):
        if process.poll() is None:
            process.kill()
        process.communicate(timeout=5)

    def test_two_atomic_writers_cannot_fsync_or_rename_each_others_temporary(self):
        script = f"""
import json,os,stat,sys
from pathlib import Path
sys.path.insert(0,{str(ROOT / "tests/DncDbg.MobileTests")!r})
sys.path.insert(0,{str(ROOT / "tests/DncDbg.RemoteTests")!r})
import run_suite
real = os.fsync
paused = False
def fsync(fd):
    global paused
    if not paused and not stat.S_ISDIR(os.fstat(fd).st_mode):
        paused = True
        print("READY",flush=True)
        sys.stdin.readline()
    return real(fd)
run_suite.os.fsync = fsync
run_suite.atomic_json(Path(sys.argv[1]),{{"writer":sys.argv[2]}})
"""
        path = self.output / "state.json"
        first = self.worker(script, path, "first")
        second = None
        try:
            self.ready(first)
            second = self.worker(script, path, "second")
            self.ready(second)
            first.stdin.write("\n")
            first.stdin.flush()
            first.wait(timeout=5)
            self.assertEqual(first.returncode, 0, first.stderr.read())
            self.assertEqual(json.loads(path.read_text())["writer"], "first")
            second.stdin.write("\n")
            second.stdin.flush()
            second.wait(timeout=5)
            self.assertEqual(second.returncode, 0, second.stderr.read())
            self.assertEqual(json.loads(path.read_text())["writer"], "second")
            self.assertFalse(list(self.output.glob("*.pending")))
        finally:
            self.finish(first)
            if second is not None:
                self.finish(second)

    def test_cleanup_cannot_mutate_active_journal_and_os_releases_crashed_owner_lock(self):
        script = f"""
import fcntl,json,os,sys
from pathlib import Path
sys.path.insert(0,{str(ROOT / "tests/DncDbg.MobileTests")!r})
sys.path.insert(0,{str(ROOT / "tests/DncDbg.RemoteTests")!r})
import run_suite
directory = Path(sys.argv[1])
with (directory / ".journal.lock").open("a+") as lock:
    fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
    json.dump({{"pid":os.getpid(),"runNonce":"private-worker","source":{{"head":"a"*40,"dirty":False}}}},lock)
    lock.flush()
    os.fsync(lock.fileno())
    run_suite.atomic_json(directory/"state.json",{{"platform":"ios","cleanupDone":False,
        "source":{{"head":"a"*40,"dirty":False}},"phase":"active-owner"}})
    print("READY",flush=True)
    sys.stdin.readline()
"""
        owner = self.worker(script, self.output)
        command = [sys.executable, str(ROOT / "tests/DncDbg.MobileTests/run_suite.py"),
                   "--platform", "ios", "--cleanup", "--output", str(self.output)]
        try:
            self.ready(owner)
            state = (self.output / "state.json").read_bytes()
            lock = (self.output / ".journal.lock").read_bytes()
            blocked = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=10)
            self.assertNotEqual(blocked.returncode, 0)
            self.assertIn("journal", blocked.stderr.lower())
            self.assertEqual((self.output / "state.json").read_bytes(), state)
            self.assertEqual((self.output / ".journal.lock").read_bytes(), lock)
            self.assertFalse((self.output / "evidence").exists())
            owner.kill()
            owner.wait(timeout=5)
            recovered = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=10)
            self.assertEqual(recovered.returncode, 0, recovered.stderr)
            manifest = json.loads((self.output / "evidence/manifest.json").read_text())
            self.assertEqual(manifest["head"], "a" * 40)
            self.assertTrue(manifest["cleanup"])
            self.assertFalse(manifest["passed"])
        finally:
            self.finish(owner)

    def test_new_directory_components_sync_their_parent_before_the_next_creation(self):
        path = self.output / "first/second/evidence"
        mkdir, sync = Path.mkdir, run_suite.os.fsync
        events = []

        def create(directory, *args, **kwargs):
            events.append(("mkdir", directory))
            return mkdir(directory, *args, **kwargs)

        def fsync(fd):
            self.assertTrue(os.fstat(fd).st_mode & 0o170000 == 0o040000)
            events.append(("fsync",))
            return sync(fd)

        with patch.object(Path, "mkdir", create), patch("run_suite.os.fsync", side_effect=fsync):
            run_suite.durable_mkdir(path)
        self.assertEqual(events, [
            ("mkdir", self.output / "first"), ("fsync",),
            ("mkdir", self.output / "first/second"), ("fsync",),
            ("mkdir", path), ("fsync",)])

    def test_parent_sync_failure_prevents_later_directory_or_command_creation(self):
        path = self.output / "first/second/evidence"
        with patch("run_suite.os.fsync", side_effect=OSError("parent sync failed")):
            with self.assertRaisesRegex(OSError, "parent sync"):
                run_suite.durable_mkdir(path)
        self.assertFalse((self.output / "first/second").exists())

    def test_durable_directory_creation_refuses_symlink_components(self):
        actual = self.output / "actual"
        actual.mkdir()
        link = self.output / "linked"
        link.symlink_to(actual, target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, "symlink|owned"):
            run_suite.durable_mkdir(link / "evidence")
        self.assertFalse((actual / "evidence").exists())


if __name__ == "__main__":
    unittest.main()
