"""Prospective original-group membership contracts; no debugger or device required."""
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from argparse import Namespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/DncDbg.MobileTests"))
from run_suite import Suite
import process_tools
import test_mobile_processes


@unittest.skipUnless(os.name == "posix", "POSIX exact original-group membership")
class MemberAnchoringTests(unittest.TestCase):
    def ownership(self):
        group, leader = test_mobile_processes.GroupIdentityTests.ownership(self)
        group.fingerprint = "a" * 64
        group.anchors_complete = True
        return group, leader

    def test_one_known_member_never_authorizes_new_members_after_leader_exit(self):
        group, leader = self.ownership()
        known = leader | {"pid": 123457, "startIdentity": "anchored"}
        unknown = leader | {"pid": 123458, "startIdentity": "not-anchored"}
        group.members[known["pid"]] = known
        identities = {leader["pid"]: None, known["pid"]: known, unknown["pid"]: unknown}
        with patch("process_tools.process_identity", side_effect=lambda pid: identities[pid]), \
                patch("process_tools.subprocess.run", return_value=subprocess.CompletedProcess(
                    ["ps"], 0, "123457\n123458\n", "")), patch("process_tools.os.killpg") as send:
            with self.assertRaises(RuntimeError):
                group.send(signal.SIGTERM, time.monotonic() + 1)
            send.assert_not_called()
        self.assertNotIn(unknown["pid"], group.members)

    def test_live_members_are_persisted_before_they_can_authorize_signals(self):
        group, leader = self.ownership()
        child = leader | {"pid": 123457, "startIdentity": "compiler"}
        saved = []

        def persist(proof):
            self.assertFalse(group.anchors_complete)
            self.assertNotIn(child["pid"], group.members)
            self.assertEqual({row["pid"] for row in proof["members"]}, {leader["pid"], child["pid"]})
            saved.append(json.loads(json.dumps(proof)))

        with patch("process_tools.process_identity", side_effect=lambda pid: leader if pid == leader["pid"] else child), \
                patch("process_tools.subprocess.run", return_value=subprocess.CompletedProcess(
                    ["ps"], 0, "123456\n123457\n", "")):
            self.assertTrue(group.capture_members(time.monotonic() + 1, persist))
        self.assertTrue(group.anchors_complete)
        self.assertTrue(saved)
        self.assertEqual(group.members[child["pid"]]["startIdentity"], "compiler")
        self.assertEqual(len(group.members[child["pid"]]["fingerprint"]), 64)

    def test_exited_leader_cannot_publish_new_member_anchors(self):
        group, leader = self.ownership()
        group.recovery.process.poll.return_value = 0
        with patch("process_tools.process_identity") as identity, patch("process_tools.os.killpg") as send:
            self.assertFalse(group.capture_members(time.monotonic() + 1, lambda proof: self.fail("No publication")))
        identity.assert_not_called()
        send.assert_not_called()

    def test_sampling_failure_is_sticky_and_never_authorizes_a_signal(self):
        for kind in ("probe", "persist", "uid", "sid", "pgid", "reuse"):
            with self.subTest(kind=kind):
                group, leader = self.ownership()
                child = leader | {"pid": 123457, "startIdentity": "compiler"}
                later = dict(child)
                if kind in ("uid", "sid", "pgid"):
                    child[kind] += 1
                if kind == "reuse":
                    later["startIdentity"] = "foreign-reused-birth"
                reads = []

                def identity(pid):
                    if pid == leader["pid"]:
                        return leader
                    reads.append(pid)
                    return later if kind == "reuse" and len(reads) > 1 else child

                def persist(proof):
                    if kind == "persist":
                        raise OSError("journal sync failed")

                result = subprocess.CompletedProcess(["ps"], 2 if kind == "probe" else 0,
                                                     "" if kind == "probe" else "123456\n123457\n",
                                                     "probe failed" if kind == "probe" else "")
                with patch("process_tools.process_identity", side_effect=identity), \
                        patch("process_tools.subprocess.run", return_value=result), \
                        patch("process_tools.os.killpg") as send:
                    with self.assertRaises((RuntimeError, OSError)):
                        group.capture_members(time.monotonic() + 1, persist)
                    self.assertFalse(group.anchors_complete)
                    with self.assertRaises(RuntimeError):
                        group.send(signal.SIGTERM, time.monotonic() + 1)
                send.assert_not_called()
                self.assertNotIn(child["pid"], group.members)

    def test_transient_exited_members_are_not_cleanup_targets(self):
        group, leader = self.ownership()
        child = leader | {"pid": 123457, "startIdentity": "transient"}
        reads = []

        def identity(pid):
            if pid == leader["pid"]:
                return leader
            reads.append(pid)
            return child if len(reads) == 1 else None

        with patch("process_tools.process_identity", side_effect=identity), \
                patch("process_tools.subprocess.run", return_value=subprocess.CompletedProcess(
                    ["ps"], 0, "123456\n123457\n", "")):
            self.assertTrue(group.capture_members(time.monotonic() + 1, lambda proof: None))
        self.assertTrue(group.anchors_complete)
        self.assertNotIn(child["pid"], group.members)

    def test_anchor_overflow_remains_incomplete_without_any_signal(self):
        group, leader = self.ownership()
        group.members.update((pid, leader | {"pid": pid, "startIdentity": str(pid)})
                             for pid in range(1000, 2023))
        child = leader | {"pid": 123457, "startIdentity": "exceeds-bound"}
        with patch("process_tools.process_identity", side_effect=lambda pid: leader if pid == leader["pid"] else child), \
                patch("process_tools.subprocess.run", return_value=subprocess.CompletedProcess(
                    ["ps"], 0, "123456\n123457\n", "")), patch("process_tools.os.killpg") as send:
            with self.assertRaises(RuntimeError):
                group.capture_members(time.monotonic() + 1, lambda proof: self.fail("No overflowing publication"))
            self.assertFalse(group.anchors_complete)
            with self.assertRaises(RuntimeError):
                group.send(signal.SIGTERM, time.monotonic() + 1)
        send.assert_not_called()

    def test_leader_exit_during_durable_write_cannot_publish_member_authority(self):
        group, leader = self.ownership()
        child = leader | {"pid": 123457, "startIdentity": "compiler"}

        def persist(proof):
            self.assertFalse(group.anchors_complete)
            group.recovery.process.poll.return_value = 0

        with patch("process_tools.process_identity", side_effect=lambda pid: leader if pid == leader["pid"] else child), \
                patch("process_tools.subprocess.run", return_value=subprocess.CompletedProcess(
                    ["ps"], 0, "123456\n123457\n", "")), patch("process_tools.os.killpg") as send:
            with self.assertRaisesRegex(RuntimeError, "leader"):
                group.capture_members(time.monotonic() + 1, persist)
        self.assertFalse(group.anchors_complete)
        self.assertNotIn(child["pid"], group.members)
        send.assert_not_called()

    def test_real_compiler_like_child_is_owned_after_original_leader_exit(self):
        directory = Path(tempfile.mkdtemp(prefix="member-anchor.", dir=ROOT / "build"))
        record, release = directory / "child.json", directory / "release.fifo"
        os.mkfifo(release)
        suite = Suite(Namespace(platform="contracts", output=directory, xcode_dir=None))
        suite.state = {"platform": "contracts", "source": {"head": "a" * 40, "dirty": False}}
        known = []
        child = ("import json,os,socket,time;from pathlib import Path;"
                 "s=socket.socket();s.bind(('127.0.0.1',0));s.listen();"
                 f"Path({str(record)!r}).write_text(json.dumps({{'pid':os.getpid(),'port':s.getsockname()[1]}}));"
                 "time.sleep(30)")
        parent = ("import subprocess,sys;"
                  f"p=subprocess.Popen([sys.executable,'-c',{child!r}]);"
                  f"open({str(release)!r}).read();raise SystemExit(0)")

        def progress(*args, **kwargs):
            if not str(args[0]).startswith("BUILD still running:") or known or not record.exists():
                return
            data = json.loads(record.read_text())
            identity = process_tools.process_identity(data["pid"])
            self.assertIsNotNone(identity)
            self.assertEqual(identity, process_tools.process_identity(data["pid"]))
            self.assertIsNone(suite.recovery_fallback.process.poll())
            known.append(identity)
            with release.open("w") as stream:
                stream.write("exit")

        try:
            with patch("run_suite.print", side_effect=progress, create=True):
                result = suite.run(sys.executable, "-c", parent, timeout=3, progress=True, progress_interval=0.05)
            self.assertEqual(result.returncode, 0)
            self.assertTrue(suite.recovery_fallback.complete)
            self.assertIn(known[0]["pid"], suite.recovery_fallback.group_ownership.members)
            intent = json.loads(suite.state_path.read_text())["commandIntent"]
            self.assertTrue(intent["memberCaptureComplete"])
            self.assertIn(known[0]["pid"], {row["pid"] for row in intent["memberAnchors"]})
        finally:
            if known:
                current = process_tools.process_identity(known[0]["pid"])
                if current is not None and current["startIdentity"] == known[0]["startIdentity"]:
                    os.kill(current["pid"], signal.SIGTERM)
            owner = suite.recovery_fallback
            if owner is not None and owner.process is not None:
                owner.process.wait(timeout=5)
            shutil.rmtree(directory)


if __name__ == "__main__":
    unittest.main()
