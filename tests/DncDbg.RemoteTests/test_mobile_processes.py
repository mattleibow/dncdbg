#!/usr/bin/env python3
"""Host-only process ownership regression; no mobile SDK or device is needed."""
import json
import errno
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import threading
import time
import unittest
from unittest.mock import Mock, patch

import process_tools as validate


def alive(pid):
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False


def waiting_child():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        listener.listen()
        print(json.dumps({"child": os.getpid(), "port": listener.getsockname()[1]}), flush=True)
        while True:
            signal.pause()


def waiting_parent(destination, ignore_term=False):
    child = subprocess.Popen([sys.executable, __file__, "--waiting-child"],
                             stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)

    stopping = False

    def terminate(_signal, _frame):
        nonlocal stopping
        if not ignore_term:
            stopping = True

    signal.signal(signal.SIGTERM, terminate)
    record = json.loads(child.stdout.readline())
    record["parent"] = os.getpid()
    Path(destination).write_text(json.dumps(record))
    print("owned helper ready", flush=True)
    if ignore_term:
        signal.signal(signal.SIGCHLD, lambda *_: child.poll())
        while True:
            signal.pause()
    while not stopping and child.poll() is None:
        time.sleep(0.02)
    if child.poll() is None:
        child.terminate()
    child.wait(timeout=1)


@unittest.skipUnless(os.name == "posix", "The simulator/emulator runner requires POSIX process groups")
class ProcessOwnershipTests(unittest.TestCase):
    def test_disappeared_group_eperm_requires_independent_absence_check(self):
        process = Mock(pid=123456)
        with patch.object(validate.os, "killpg", side_effect=PermissionError(1, "Operation not permitted")), \
                patch.object(validate.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "", "")) as snapshot:
            validate.stop_owned(process)
        snapshot.assert_called_with(["ps", "-g", str(process.pid), "-o", "pgid="],
                                    check=False, capture_output=True, text=True, timeout=5)
        self.assertEqual(snapshot.call_count, 2)
        process.wait.assert_called_once_with(timeout=1)

    def test_exact_group_probe_accepts_only_verified_empty_group_not_a_probe_error(self):
        process = Mock(pid=123456)
        with patch.object(validate.os, "killpg", side_effect=PermissionError(1, "Operation not permitted")), \
                patch.object(validate.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, "", "")):
            validate.stop_owned(process)
        with patch.object(validate.os, "killpg", side_effect=PermissionError(1, "Operation not permitted")), \
                patch.object(validate.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, "", "probe failed")):
            with self.assertRaises(RuntimeError):
                validate.stop_owned(process)

    def test_group_probe_rejects_malformed_or_other_group_rows(self):
        for output in ("not-a-process-group\n", "999999\n", "123456\n999999\n"):
            with self.subTest(output=output):
                process = Mock(pid=123456)
                with patch.object(validate.os, "killpg", side_effect=PermissionError(1, "Operation not permitted")), \
                        patch.object(validate.subprocess, "run",
                                     return_value=subprocess.CompletedProcess([], 0, output, "")):
                    with self.assertRaisesRegex(RuntimeError, "group"):
                        validate.stop_owned(process)

    def test_live_group_eperm_is_not_ignored(self):
        process = Mock(pid=123456)
        with patch.object(validate.os, "killpg", side_effect=PermissionError(1, "Operation not permitted")), \
                patch.object(validate.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "123456\n", "")):
            with self.assertRaises(PermissionError):
                validate.stop_owned(process)

    def test_group_liveness_uses_kernel_probe_without_global_process_scan(self):
        process = Mock(pid=123456)
        with patch.object(validate.os, "killpg", side_effect=[
                None, None, ProcessLookupError(), ProcessLookupError()]) as send, \
                patch.object(validate.subprocess, "run",
                             side_effect=subprocess.TimeoutExpired(["ps"], 5)) as snapshot:
            validate.stop_owned(process)
        self.assertEqual(send.call_args_list, [
            unittest.mock.call(123456, signal.SIGTERM), unittest.mock.call(123456, 0),
            unittest.mock.call(123456, 0), unittest.mock.call(123456, 0)])
        snapshot.assert_not_called()

    def test_timeout_removes_parent_child_and_listener_within_five_seconds(self):
        directory = validate.ROOT / "build/validation/process-ownership" / f"{os.getpid()}-{time.time_ns()}"
        directory.mkdir(parents=True)
        record = directory / "pids.json"
        pids = []
        try:
            with self.assertRaises(subprocess.TimeoutExpired):
                validate.run_owned(sys.executable, __file__, "--waiting-parent", str(record), timeout=1)
            self.assertTrue(record.exists(), "Helper must spawn its waiting child before timing out")
            owned = json.loads(record.read_text())
            pids = [owned["parent"], owned["child"]]
            deadline = time.monotonic() + 5
            while any(alive(pid) for pid in pids) and time.monotonic() < deadline:
                time.sleep(0.02)
            self.assertFalse([pid for pid in pids if alive(pid)], "Timed-out probe left an owned PID alive")
            with socket.socket() as connection:
                self.assertNotEqual(connection.connect_ex(("127.0.0.1", owned["port"])), 0,
                                    "Timed-out probe left its child listener alive")
        finally:
            if record.exists():
                owned = json.loads(record.read_text())
                pids = [owned["parent"], owned["child"]]
            for pid in pids:
                if alive(pid):
                    os.kill(pid, signal.SIGKILL)
            shutil.rmtree(directory)


class ListenerOwnershipTests(unittest.TestCase):
    def test_direct_closed_listener(self):
        with socket.socket() as reserve:
            reserve.bind(("127.0.0.1", 0))
            port = reserve.getsockname()[1]
        self.assertTrue(validate.native_listener_closed(port, forwarded=False))

    def test_direct_open_listener(self):
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            listener.listen()
            self.assertFalse(validate.native_listener_closed(listener.getsockname()[1], forwarded=False))

    def test_adb_forward_accepts_then_eof_when_target_listener_is_closed(self):
        self.check_forward(closed=True)

    def test_adb_forward_accepts_but_remains_connected_when_target_listener_is_open(self):
        self.check_forward(closed=False)

    def check_forward(self, closed):
        done = threading.Event()
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            listener.listen()
            listener.settimeout(3)

            def forward():
                with listener.accept()[0]:
                    if not closed:
                        done.wait(timeout=3)

            proxy = threading.Thread(target=forward)
            proxy.start()
            try:
                self.assertEqual(validate.native_listener_closed(listener.getsockname()[1], forwarded=True), closed)
            finally:
                done.set()
                proxy.join(timeout=3)
                self.assertFalse(proxy.is_alive())


@unittest.skipUnless(os.name == "posix", "POSIX exact process identity")
class GroupIdentityTests(unittest.TestCase):
    def ownership(self):
        process = Mock(pid=123456, returncode=None)
        process.poll.return_value = None
        recovery = validate.OwnedProcessRecovery(process, None, time.monotonic() + 5)
        owner = validate.ProcessGroupOwnership(recovery)
        identity = {"pid": process.pid, "ppid": 123, "pgid": process.pid, "sid": process.pid,
                    "uid": os.geteuid(), "startIdentity": "original-birth", "state": "running"}
        owner.original = identity
        owner.capture_state = "verified"
        owner.members[process.pid] = identity
        return owner, identity

    def test_public_kernel_identity_matches_current_exact_process(self):
        identity = validate.process_identity(os.getpid())
        self.assertEqual(identity["pid"], os.getpid())
        self.assertEqual(identity["pgid"], os.getpgid(0))
        self.assertEqual(identity["sid"], os.getsid(0))
        self.assertEqual(identity["uid"], os.geteuid())
        self.assertTrue(identity["startIdentity"])
        self.assertTrue(validate.ProcessGroupOwnership.same(identity, validate.process_identity(os.getpid())))

    def test_failed_initial_capture_never_adopts_a_later_reused_pid(self):
        for reaped in (False, True):
            with self.subTest(reaped=reaped):
                owner, foreign = self.ownership()
                owner.original = None
                owner.members.clear()
                owner.capture_state = "not-applicable"
                with patch.object(validate, "process_identity", return_value=None):
                    owner.capture()
                owner.recovery.process.returncode = 0 if reaped else None
                with patch.object(validate, "process_identity", return_value=foreign), \
                        patch.object(validate.subprocess, "run",
                                     return_value=subprocess.CompletedProcess([], 0, "123456\n", "")), \
                        patch.object(validate.os, "killpg") as send, patch.object(validate.os, "kill") as direct:
                    owner.capture()
                    with self.assertRaises(RuntimeError):
                        owner.send(signal.SIGTERM, time.monotonic() + 1)
                self.assertIsNone(owner.original)
                send.assert_not_called()
                direct.assert_not_called()

    def test_initial_capture_requires_the_retained_child_still_alive_after_both_identity_reads(self):
        for exited, changed in ((True, False), (False, True)):
            with self.subTest(exited=exited, changed=changed):
                owner, identity = self.ownership()
                owner.original = None
                owner.members.clear()
                owner.capture_state = "not-applicable"
                owner.recovery.process.returncode = None
                owner.recovery.process.poll.return_value = 0 if exited else None
                later = dict(identity, startIdentity="different-birth") if changed else identity
                with patch.object(validate, "process_identity", side_effect=[identity, later]), \
                        patch.object(validate.os, "killpg") as send:
                    owner.capture()
                self.assertEqual(owner.capture_state, "unverified")
                self.assertIsNone(owner.original)
                send.assert_not_called()

    def test_changed_birth_session_or_uid_never_authorizes_a_signal(self):
        for field in ("startIdentity", "sid", "uid"):
            with self.subTest(field=field):
                owner, identity = self.ownership()
                changed = dict(identity, **{field: "different" if field == "startIdentity" else identity[field] + 1})
                with patch.object(validate, "process_identity", return_value=changed), \
                        patch.object(validate.subprocess, "run",
                                     return_value=subprocess.CompletedProcess([], 0, "123456\n", "")), \
                        patch.object(validate.os, "killpg") as send:
                    with self.assertRaisesRegex(RuntimeError, "identity|session|UID"):
                        owner.send(signal.SIGTERM, time.monotonic() + 1)
                send.assert_not_called()

    def test_scoped_group_probe_failure_or_mismatched_rows_cannot_authorize_absence(self):
        for result in (subprocess.CompletedProcess([], 2, "", "probe failed"),
                       subprocess.CompletedProcess([], 1, "123456\n", ""),
                       subprocess.CompletedProcess([], 0, "not-a-pid\n", ""),
                       subprocess.CompletedProcess([], 0, "999999\n", "")):
            with self.subTest(result=result):
                owner, identity = self.ownership()
                wrong = dict(identity, pid=999999, pgid=999999)
                with patch.object(validate, "process_identity",
                                  side_effect=lambda pid: identity if pid == 123456 else wrong), \
                        patch.object(validate.subprocess, "run", return_value=result), \
                        patch.object(validate.os, "killpg") as send:
                    with self.assertRaises(RuntimeError):
                        owner.exists(time.monotonic() + 1)
                send.assert_not_called()

    def test_signal_zero_eperm_accepts_only_the_following_authoritative_empty_group(self):
        owner, identity = self.ownership()
        with patch.object(validate, "process_identity", return_value=identity), \
                patch.object(validate.subprocess, "run", side_effect=[
                    subprocess.CompletedProcess([], 0, "123456\n", ""),
                    subprocess.CompletedProcess([], 1, "", "")]), \
                patch.object(validate.os, "killpg", side_effect=PermissionError(errno.EPERM, "denied")) as send:
            self.assertFalse(owner.exists(time.monotonic() + 1))
        send.assert_called_once_with(123456, 0)
        self.assertEqual(owner.signals[0]["errno"], errno.EPERM)
        self.assertEqual(owner.signals[0]["after"], [])

    def test_live_eperm_is_not_absence_and_nonzero_signal_is_not_retried(self):
        owner, identity = self.ownership()
        with patch.object(validate, "process_identity", return_value=identity), \
                patch.object(validate.subprocess, "run",
                             return_value=subprocess.CompletedProcess([], 0, "123456\n", "")), \
                patch.object(validate.os, "killpg", side_effect=PermissionError(errno.EPERM, "denied")) as send:
            for _ in range(2):
                with self.assertRaises(PermissionError):
                    owner.send(signal.SIGTERM, time.monotonic() + 1)
        self.assertEqual(sum(call.args[1] == signal.SIGTERM for call in send.call_args_list), 1)
        self.assertTrue(all(attempt["after"] for attempt in owner.signals))

    def test_reaped_leader_does_not_authorize_an_unjournaled_group_member(self):
        owner, identity = self.ownership()
        child = dict(identity, pid=123457, ppid=1, startIdentity="child-birth")
        with patch.object(validate, "process_identity", side_effect=lambda pid: child if pid == child["pid"] else None), \
                patch.object(validate.subprocess, "run",
                             return_value=subprocess.CompletedProcess([], 0, "123457\n", "")), \
                patch.object(validate.os, "killpg") as send:
            with self.assertRaisesRegex(RuntimeError, "journaled"):
                owner.send(signal.SIGKILL, time.monotonic() + 1)
        send.assert_not_called()

    def test_exact_journaled_child_remains_authorized_after_leader_reaping(self):
        owner, identity = self.ownership()
        child = dict(identity, pid=123457, ppid=1, startIdentity="child-birth")
        owner.members[child["pid"]] = child
        with patch.object(validate, "process_identity", side_effect=lambda pid: child if pid == child["pid"] else None), \
                patch.object(validate.subprocess, "run", side_effect=[
                    subprocess.CompletedProcess([], 0, "123457\n", ""),
                    subprocess.CompletedProcess([], 1, "", "")]), \
                patch.object(validate.os, "killpg") as send:
            self.assertFalse(owner.send(signal.SIGKILL, time.monotonic() + 1))
        send.assert_called_once_with(123456, signal.SIGKILL)


if __name__ == "__main__":
    if sys.argv[1:2] == ["--waiting-child"]:
        waiting_child()
    elif sys.argv[1:2] == ["--waiting-parent"]:
        waiting_parent(sys.argv[2])
    else:
        unittest.main()
