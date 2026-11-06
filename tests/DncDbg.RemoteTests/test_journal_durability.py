"""Real process and filesystem contracts for the mobile runner's private journal."""
import json
import io
import os
from pathlib import Path
import selectors
import shutil
import signal
import subprocess
import sys
import unittest
from unittest.mock import patch
import uuid
from argparse import Namespace

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

    def test_actual_runner_retains_the_lease_through_commands_cleanup_and_evidence(self):
        directory = self.output / "active-run"
        script = f"""
import sys
from pathlib import Path
sys.path.insert(0,{str(ROOT / "tests/DncDbg.MobileTests")!r})
sys.path.insert(0,{str(ROOT / "tests/DncDbg.RemoteTests")!r})
import run_suite
def pause(*args):
    print("READY",flush=True)
    sys.stdin.readline()
run_suite.contracts = pause
evidence = run_suite.Suite.evidence
def collect(suite,*args):
    pause()
    return evidence(suite,*args)
run_suite.Suite.evidence = collect
sys.argv = ["run_suite.py","--platform","contracts","--output",sys.argv[1]]
raise SystemExit(run_suite.main())
"""
        runner = self.worker(script, directory)
        command = [sys.executable, str(ROOT / "tests/DncDbg.MobileTests/run_suite.py"),
                   "--platform", "contracts", "--cleanup", "--output", str(directory)]
        try:
            for stage in ("commands", "evidence"):
                with self.subTest(stage=stage):
                    self.ready(runner)
                    before = (directory / "state.json").read_bytes()
                    lock = json.loads((directory / ".journal.lock").read_text())
                    self.assertEqual(lock["pid"], runner.pid)
                    self.assertEqual(lock["owner"]["pid"], runner.pid)
                    self.assertTrue(lock["owner"]["startIdentity"])
                    self.assertEqual(len(lock["source"]["head"]), 40)
                    self.assertEqual(len(lock["runNonce"]), 32)
                    blocked = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=10)
                    self.assertNotEqual(blocked.returncode, 0)
                    self.assertEqual((directory / "state.json").read_bytes(), before)
                    if stage == "evidence":
                        self.assertFalse(json.loads((directory / "evidence/manifest.json").read_text())["passed"])
                    runner.stdin.write("\n")
                    runner.stdin.flush()
            runner.wait(timeout=5)
            self.assertEqual(runner.returncode, 0, runner.stderr.read())
            self.assertTrue(json.loads((directory / "evidence/manifest.json").read_text())["passed"])
        finally:
            self.finish(runner)

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
        self.assertEqual(events, [("fsync",)] * len(run_suite.journal_components(self.output)) + [
            ("mkdir", self.output / "first"), ("fsync",),
            ("mkdir", self.output / "first/second"), ("fsync",),
            ("mkdir", path), ("fsync",)])

    def test_parent_sync_failure_prevents_later_directory_or_command_creation(self):
        path = self.output / "first/second/evidence"
        sync = run_suite.os.fsync
        parent = self.output.stat()

        def fail_parent(fd):
            identity = os.fstat(fd)
            if (identity.st_dev, identity.st_ino) == (parent.st_dev, parent.st_ino):
                raise OSError("parent sync failed")
            return sync(fd)

        with patch("run_suite.os.fsync", side_effect=fail_parent):
            with self.assertRaisesRegex(OSError, "parent sync"):
                run_suite.durable_mkdir(path)
        self.assertTrue((self.output / "first").exists())
        self.assertFalse((self.output / "first/second").exists())
        events = []

        def retry(fd):
            identity = os.fstat(fd)
            if (identity.st_dev, identity.st_ino) == (parent.st_dev, parent.st_ino):
                self.assertFalse((self.output / "first/second").exists())
                events.append("sync-interrupted-creation")
            return sync(fd)

        with patch("run_suite.os.fsync", side_effect=retry):
            run_suite.durable_mkdir(path)
        self.assertEqual(events, ["sync-interrupted-creation"])

    def test_initial_directory_sync_failure_prevents_main_from_persisting_or_launching(self):
        destination = self.output / "must-not-launch"
        suite = run_suite.Suite(Namespace(platform="ios", output=destination, xcode_dir=None))
        sync = run_suite.sync_directory
        previous = signal.getsignal(signal.SIGTERM)

        def unavailable(path):
            if path == self.output:
                raise OSError("parent directory sync failed")
            return sync(path)

        try:
            with patch.object(sys, "argv", ["run_suite.py", "--platform", "ios", "--output", str(destination)]), \
                    patch("run_suite.Suite", return_value=suite), patch.object(suite, "execute") as execute, \
                    patch("run_suite.sync_directory", side_effect=unavailable):
                self.assertEqual(run_suite.main(), 1)
                execute.assert_not_called()
        finally:
            signal.signal(signal.SIGTERM, previous)
        self.assertFalse(suite.state_path.exists())
        self.assertFalse((destination / "evidence/manifest.json").exists())

    def test_unique_atomic_temporary_preserves_mode_and_prior_state_on_partial_failure(self):
        path = self.output / "state.json"
        run_suite.atomic_json(path, {"phase": "pending"})
        path.chmod(0o640)

        def interrupted(value, stream, *args, **kwargs):
            stream.write("{partial")
            raise OSError("write interrupted")

        with patch("run_suite.json.dump", side_effect=interrupted):
            with self.assertRaisesRegex(OSError, "write interrupted"):
                run_suite.atomic_json(path, {"phase": "complete"})
        self.assertEqual(json.loads(path.read_text()), {"phase": "pending"})
        self.assertEqual(path.stat().st_mode & 0o777, 0o640)
        self.assertFalse(list(self.output.glob("*.pending")))
        run_suite.atomic_json(path, {"phase": "complete"})
        self.assertEqual(path.stat().st_mode & 0o777, 0o640)

    def test_release_failure_never_leaves_a_success_manifest_or_terminal_pass(self):
        for after_close, cancellation, timeout in ((False, False, False), (True, False, False),
                                                   (False, True, False), (False, False, True)):
            with self.subTest(after_close=after_close, cancellation=cancellation, timeout=timeout):
                destination = self.output / uuid.uuid4().hex
                suite = run_suite.Suite(Namespace(platform="contracts", output=destination, xcode_dir=None))
                original = KeyboardInterrupt("release cancellation") if cancellation else OSError("release failed")
                primary = TimeoutError("original operation timeout")
                close = run_suite.JournalLease.close
                attempted = []
                leases = []
                terminal = io.StringIO()
                previous = signal.getsignal(signal.SIGTERM)

                def release(lease):
                    leases.append(lease)
                    if not attempted:
                        attempted.append(True)
                        if after_close:
                            close(lease)
                        raise original
                    return close(lease)

                try:
                    with patch.object(sys, "argv", ["run_suite.py", "--platform", "contracts",
                                                   "--output", str(destination)]), \
                            patch("run_suite.Suite", return_value=suite), \
                            patch("run_suite.contracts", side_effect=primary if timeout else None), \
                            patch.object(run_suite.JournalLease, "close", release), \
                            patch("run_suite.sys.stdout", terminal):
                        if cancellation:
                            with self.assertRaises(KeyboardInterrupt) as raised:
                                run_suite.main()
                            self.assertIs(raised.exception, original)
                        else:
                            self.assertEqual(run_suite.main(), 1)
                finally:
                    signal.signal(signal.SIGTERM, previous)
                    for lease in leases:
                        close(lease)
                manifest = json.loads((destination / "evidence/manifest.json").read_text())
                self.assertFalse(manifest["passed"])
                self.assertNotIn("PASS:", terminal.getvalue())
                self.assertIn("release", manifest["error"])
                if timeout:
                    self.assertIn("original operation timeout", manifest["error"])

    def test_released_lease_and_competing_cleanup_never_observe_final_success(self):
        directory = self.output / "release-handoff"
        script = f"""
import sys
from pathlib import Path
sys.path.insert(0,{str(ROOT / "tests/DncDbg.MobileTests")!r})
sys.path.insert(0,{str(ROOT / "tests/DncDbg.RemoteTests")!r})
import run_suite
run_suite.contracts = lambda suite: None
close = run_suite.JournalLease.close
attempted = False
def release(lease):
    global attempted
    if not attempted:
        attempted = True
        close(lease)
        print("READY",flush=True)
        sys.stdin.readline()
        raise OSError("release failed after unlock")
    return close(lease)
run_suite.JournalLease.close = release
sys.argv = ["run_suite.py","--platform","contracts","--output",sys.argv[1]]
raise SystemExit(run_suite.main())
"""
        runner = self.worker(script, directory)
        command = [sys.executable, str(ROOT / "tests/DncDbg.MobileTests/run_suite.py"),
                   "--platform", "contracts", "--cleanup", "--output", str(directory)]
        try:
            self.ready(runner)
            state_before = (directory / "state.json").read_bytes()
            manifest_before = (directory / "evidence/manifest.json").read_bytes()
            cleanup = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=10)
            self.assertFalse(json.loads(state_before)["cleanupDone"])
            self.assertFalse(json.loads(manifest_before)["passed"])
            self.assertNotEqual(cleanup.returncode, 0)
            self.assertEqual((directory / "state.json").read_bytes(), state_before)
            self.assertEqual((directory / "evidence/manifest.json").read_bytes(), manifest_before)
            runner.stdin.write("\n")
            runner.stdin.flush()
            runner.wait(timeout=5)
            self.assertEqual(runner.returncode, 1, runner.stderr.read())
            self.assertFalse(json.loads((directory / "state.json").read_text())["cleanupDone"])
            self.assertFalse(json.loads((directory / "evidence/manifest.json").read_text())["passed"])
        finally:
            self.finish(runner)

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
