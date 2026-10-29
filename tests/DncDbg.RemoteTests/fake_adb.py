#!/usr/bin/env python3
"""Independent fake ADB daemon: forwards survive cancellation/death of its CLI."""
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import threading
import time


class FakeAdbDaemon:
    def __init__(self, state, mode):
        self.state = state
        self.process = subprocess.Popen([sys.executable, __file__, "--daemon", str(state), mode])
        deadline = time.monotonic() + 5
        while not (state / "daemon-ready.json").exists():
            if self.process.poll() is not None or time.monotonic() > deadline:
                raise RuntimeError("Fake ADB daemon failed to start.")
            time.sleep(0.01)
        self.baseline = self.mappings()

    def mappings(self):
        if self.process.poll() is not None:
            return []
        return json.loads((self.state / "daemon-mappings.json").read_text())

    def committed(self):
        startup_deadline = time.monotonic() + 30
        while not (self.state / "forward-cli-started").exists():
            if time.monotonic() > startup_deadline:
                raise TimeoutError("Package admission did not reach the forward CLI.")
            time.sleep(0.01)
        deadline = time.monotonic() + 5
        while not (self.state / "daemon-committed.json").exists():
            if time.monotonic() > deadline:
                raise TimeoutError("The daemon did not commit the forward.")
            time.sleep(0.01)
        return json.loads((self.state / "daemon-committed.json").read_text())

    def close(self):
        if self.process.poll() is None:
            self.process.terminate()
        self.process.wait(timeout=5)


def daemon(state, mode):
    endpoint = json.loads((state / "endpoint.json").read_text())
    endpoint = endpoint["Endpoint"]
    # Even same-device/same-target and other-device mappings are not ours.
    mappings = [["emulator-5556", "tcp:1", f"tcp:{endpoint['Port']}"],
                ["emulator-9998", "tcp:2", f"tcp:{endpoint['Port']}"]]
    lock = threading.Lock()

    def save():
        replacement = state / "daemon-mappings.new"
        replacement.write_text(json.dumps(mappings))
        replacement.replace(state / "daemon-mappings.json")

    def handle(connection):
        with connection:
            request = json.loads(connection.makefile("rb").readline())
            args = request["args"]
            result, error = "", False
            allocation = "tcp:0" in args
            rebind = not allocation and "--list" not in args and "--remove" not in args
            stage = "allocation" if allocation else "rebind"
            with lock:
                if "--list" in args:
                    result = "\n".join(" ".join(row) for row in mappings)
                elif "--remove" in args:
                    local = args[-1]
                    mappings[:] = [row for row in mappings if row[:2] != [args[1], local]]
                elif allocation and mode == "reject-allocation":
                    error = True
                else:
                    local = f"tcp:{endpoint['Port']}" if allocation else args[-2]
                    if rebind:
                        mappings[:] = [row for row in mappings if row[:2] != [args[1], local]]
                    mappings.append([args[1], local, args[-1]])
                    result = str(endpoint["Port"]) if allocation else ""
                    error = mode == "error-" + stage
                    if mode == "malformed-allocation" and allocation:
                        result = "not-a-port"
                save()
                if mode == "hold-" + stage and (allocation or rebind):
                    replacement = state / "daemon-committed.new"
                    replacement.write_text(json.dumps(request | {"port": endpoint["Port"], "stage": stage}))
                    replacement.replace(state / "daemon-committed.json")
            if mode == "hold-" + stage and (allocation or rebind):
                # The daemon has committed, but the client cannot learn the port.
                # Cancellation kills the CLI, not this independently owned daemon.
                while True:
                    if not connection.recv(1):
                        return
            try:
                connection.sendall(json.dumps({"result": result, "error": error}).encode() + b"\n")
            except OSError:
                pass

    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        listener.listen()
        save()
        (state / "daemon-ready.json").write_text(json.dumps({"port": listener.getsockname()[1]}))
        while True:
            connection, _ = listener.accept()
            threading.Thread(target=handle, args=(connection,), daemon=True).start()


def client():
    state = Path(os.environ["DNCDBG_FAKE_DEVICE"])
    child = None
    if "tcp:0" in sys.argv or ("--no-rebind" not in sys.argv and sys.argv[-2].startswith("tcp:")):
        child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    try:
        port = json.loads((state / "daemon-ready.json").read_text())["port"]
        with socket.create_connection(("127.0.0.1", port), timeout=15) as connection:
            connection.sendall(json.dumps({"args": sys.argv[1:], "pid": os.getpid(),
                                           "child": child.pid if child else None}).encode() + b"\n")
            response = json.loads(connection.makefile("rb").readline())
        if response["error"]:
            sys.exit(1)
        print(response["result"])
    finally:
        if child:
            child.terminate()
            child.wait(timeout=5)


if __name__ == "__main__":
    daemon(Path(sys.argv[2]), sys.argv[3])
elif __name__ == "__client__":
    client()
