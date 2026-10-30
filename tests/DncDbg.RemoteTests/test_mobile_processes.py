#!/usr/bin/env python3
"""Host-only process ownership regression; no mobile SDK or device is needed."""
import json
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


def waiting_parent(destination):
    child = subprocess.Popen([sys.executable, __file__, "--waiting-child"],
                             stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)

    stopping = False

    def terminate(_signal, _frame):
        nonlocal stopping
        stopping = True

    signal.signal(signal.SIGTERM, terminate)
    record = json.loads(child.stdout.readline())
    record["parent"] = os.getpid()
    Path(destination).write_text(json.dumps(record))
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
                patch.object(validate.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, " 12\n 34\n", "")) as snapshot:
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


if __name__ == "__main__":
    if sys.argv[1:2] == ["--waiting-child"]:
        waiting_child()
    elif sys.argv[1:2] == ["--waiting-parent"]:
        waiting_parent(sys.argv[2])
    else:
        unittest.main()
