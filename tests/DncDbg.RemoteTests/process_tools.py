"""Owned POSIX commands shared by remote/native/mobile test entrypoints."""
import os
import signal
import socket
import subprocess
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def start_owned(*args, **kwargs):
    if os.name != "posix":
        raise RuntimeError("The remote integration runner requires POSIX process-group ownership")
    return subprocess.Popen(*args, start_new_session=True, **kwargs)


def stop_owned(process, grace_period=1, settle_timeout=1):
    def group_exists():
        process.poll()
        try:
            os.killpg(process.pid, 0)
            return True
        except ProcessLookupError:
            return False
        except PermissionError:
            # Some macOS/Python combinations report EPERM for vanished groups.
            # Query only the owned group, not every process on a loaded runner.
            probe = subprocess.run(["ps", "-g", str(process.pid), "-o", "pgid="], check=False,
                                   capture_output=True, text=True, timeout=5)
            if probe.returncode not in (0, 1) or probe.stderr.strip() or probe.returncode == 1 and probe.stdout.strip():
                raise RuntimeError("Cannot independently verify the owned process group: " + probe.stderr.strip())
            return str(process.pid) in probe.stdout.split()

    def signal_group(value):
        if value == 0:
            return group_exists()
        try:
            os.killpg(process.pid, value)
            return True
        except ProcessLookupError:
            return False
        except PermissionError:
            # macOS may report EPERM for a vanished group; independently verify absence.
            if not group_exists():
                return False
            raise

    if signal_group(signal.SIGTERM):
        deadline = time.monotonic() + grace_period
        while time.monotonic() < deadline:
            process.poll()
            if not signal_group(0):
                break
            time.sleep(0.02)
        else:
            signal_group(signal.SIGKILL)
    process.wait(timeout=1)
    deadline = time.monotonic() + settle_timeout
    while signal_group(0):
        if time.monotonic() >= deadline:
            raise RuntimeError(f"Owned process group {process.pid} remained after termination")
        time.sleep(0.02)


def native_listener_closed(port, forwarded):
    with socket.socket() as verify:
        verify.settimeout(1)
        if verify.connect_ex(("127.0.0.1", port)) != 0:
            return True
        if forwarded:
            try:
                return verify.recv(1) == b""
            except socket.timeout:
                return False
        return False


def run_owned(*args, env=None, check=True, input=None, timeout=180, log=None, cwd=ROOT):
    with start_owned(args, cwd=cwd, env=env, text=True, stdin=subprocess.PIPE if input is not None else None,
                     stdout=subprocess.PIPE, stderr=subprocess.PIPE) as process:
        try:
            stdout, stderr = process.communicate(input, timeout=timeout)
            result = subprocess.CompletedProcess(args, process.returncode, stdout, stderr)
        except subprocess.TimeoutExpired as error:
            stop_owned(process)
            stdout, stderr = process.communicate(timeout=5)
            if log:
                Path(log).write_text(stdout + stderr)
            error.output, error.stderr = stdout, stderr
            raise
        finally:
            stop_owned(process)
    if log:
        Path(log).write_text(result.stdout + result.stderr)
    if check and result.returncode:
        raise RuntimeError(f"{args[0]} failed ({result.returncode}): {(result.stdout + result.stderr)[-4000:]}")
    return result
