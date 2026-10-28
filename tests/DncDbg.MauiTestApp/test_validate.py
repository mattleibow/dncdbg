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
import time
import unittest

import validate


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
    def test_timeout_removes_parent_child_and_listener_within_five_seconds(self):
        directory = validate.ROOT / "build/validation/process-ownership" / f"{os.getpid()}-{time.time_ns()}"
        directory.mkdir(parents=True)
        record = directory / "pids.json"
        pids = []
        try:
            with self.assertRaises(subprocess.TimeoutExpired):
                validate.run(sys.executable, __file__, "--waiting-parent", str(record), timeout=1)
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


if __name__ == "__main__":
    if sys.argv[1:2] == ["--waiting-child"]:
        waiting_child()
    elif sys.argv[1:2] == ["--waiting-parent"]:
        waiting_parent(sys.argv[2])
    else:
        unittest.main()
