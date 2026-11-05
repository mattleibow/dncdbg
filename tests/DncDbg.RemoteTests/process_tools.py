"""Owned POSIX commands shared by remote/native/mobile test entrypoints."""
import os
import json
import signal
import socket
import subprocess
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class OwnedProcessSettlementError(RuntimeError):
    """Failed command with a retained, separately bounded recovery owner."""

    def __init__(self, recovery):
        self.recovery = recovery
        super().__init__(f"Owned process/group {recovery.process.pid} required failure settlement; "
                         f"leaderReaped={recovery.leader_reaped}, groupAbsent={recovery.group_absent}, "
                         f"drainComplete={recovery.drain_complete}; see {recovery.log}.")


def original_cancellation(error):
    while error is not None:
        if not isinstance(error, Exception):
            return error
        error = error.__cause__
    return None


class OwnedProcessRecovery:
    """Own only the leader/original group; external resources need their own journal."""

    def __init__(self, process, log, deadline):
        self.process, self.log, self.deadline = process, log, deadline
        self.recovery_deadline = None
        self.leader_reaped = self.group_absent = self.drain_complete = False
        self.stdout = self.stderr = b""
        self.errors = []
        self.record_errors = []
        self.primary_error = None

    @property
    def complete(self):
        return self.leader_reaped and self.group_absent and self.drain_complete

    @property
    def stdout_text(self):
        return self.stdout.decode("utf-8", errors="backslashreplace")

    @property
    def stderr_text(self):
        return self.stderr.decode("utf-8", errors="backslashreplace")

    def capture(self, stdout, stderr):
        def merge(previous, current):
            current = current.encode() if isinstance(current, str) else current or b""
            return current if current.startswith(previous) else previous if previous.startswith(current) else previous + current
        self.stdout, self.stderr = merge(self.stdout, stdout), merge(self.stderr, stderr)

    def preserve_error(self, error):
        current = self.primary_error
        if (current is None or
                isinstance(current, subprocess.TimeoutExpired) and not isinstance(error, subprocess.TimeoutExpired) or
                original_cancellation(error) is not None and original_cancellation(current) is None):
            self.primary_error = error

    def settle(self, deadline):
        try:
            if not self.group_absent:
                available = deadline - time.monotonic()
                if available <= 0:
                    raise subprocess.TimeoutExpired(self.process.args, 0)
                stop_owned(self.process, grace_period=min(0.1, available / 4),
                           settle_timeout=max(0, available / 2), deadline=deadline)
                self.group_absent = True
            available = deadline - time.monotonic()
            if available <= 0:
                raise subprocess.TimeoutExpired(self.process.args, 0)
            self.capture(*self.process.communicate(timeout=available))
            self.drain_complete = True
        except subprocess.TimeoutExpired as error:
            self.capture(error.output, error.stderr)
            self.preserve_error(error)
            self.errors.append(type(error).__name__)
        except BaseException as error:
            self.preserve_error(error)
            self.errors.append(type(error).__name__)
        finally:
            self.leader_reaped = self.process.poll() is not None
        return self.complete

    def finish(self):
        if self.complete:
            return True
        if self.recovery_deadline is None:
            self.recovery_deadline = time.monotonic() + 5
        self.settle(self.recovery_deadline)
        if self.complete or time.monotonic() >= self.recovery_deadline:
            for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
                if stream is not None:
                    stream.close()
        self.record()
        return self.complete

    def record(self):
        if self.log:
            report = {
                "pid": self.process.pid, "processGroup": self.process.pid,
                "ownershipScope": "leader-and-original-process-group",
                "leaderReaped": self.leader_reaped, "groupAbsent": self.group_absent,
                "drainComplete": self.drain_complete, "resourceJournalClean": False,
                "returnCode": self.process.returncode, "recoveryComplete": self.complete,
                "primaryError": type(self.primary_error).__name__ if self.primary_error else None,
                "deadlineExceeded": time.monotonic() > self.deadline,
                "failureRecoveryBudgetSeconds": 5, "recoveryErrors": self.errors,
                "recordErrors": self.record_errors,
                "partialStdout": self.stdout_text[-4000:], "partialStderr": self.stderr_text[-4000:],
                "stdoutBytes": len(self.stdout), "stderrBytes": len(self.stderr),
            }
            for path, is_report in ((Path(self.log), False), (Path(self.log).with_suffix(".settlement.json"), True)):
                try:
                    report["primaryError"] = type(self.primary_error).__name__ if self.primary_error else None
                    content = json.dumps(report, indent=2) if is_report else self.stdout_text + self.stderr_text
                    path.write_text(content)
                except OSError as error:
                    self.record_errors.append(f"{path.name}: {error}")
                    if self.primary_error is None:
                        self.primary_error = error


def start_owned(*args, **kwargs):
    if os.name != "posix":
        raise RuntimeError("The remote integration runner requires POSIX process-group ownership")
    return subprocess.Popen(*args, start_new_session=True, **kwargs)


def stop_owned(process, grace_period=1, settle_timeout=1, deadline=None):
    def remaining(limit):
        return limit if deadline is None else min(limit, max(0, deadline - time.monotonic()))

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
                                   capture_output=True, text=True, timeout=remaining(5))
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
        grace_deadline = time.monotonic() + remaining(grace_period)
        while time.monotonic() < grace_deadline:
            process.poll()
            if not signal_group(0):
                break
            time.sleep(min(0.02, remaining(0.02)))
        else:
            signal_group(signal.SIGKILL)
    process.wait(timeout=remaining(1))
    settle_deadline = time.monotonic() + remaining(settle_timeout)
    while signal_group(0):
        if time.monotonic() >= settle_deadline:
            raise RuntimeError(f"Owned process group {process.pid} remained after termination")
        time.sleep(min(0.02, remaining(0.02)))


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


def run_owned(*args, env=None, check=True, input=None, timeout=180, log=None, cwd=ROOT, deadline=None, owner_callback=None):
    if deadline is not None:
        # Reserve settlement time inside the caller's budget, not after its expiry.
        available = max(0, deadline - time.monotonic())
        if not available:
            raise subprocess.TimeoutExpired(args, timeout)
        reserve = min(1, available / 4)
        if any(arg in ("--daemon", "--daemonize", "--background") for arg in args):
            raise ValueError("Daemonizing commands require a separate stable resource journal, not group ownership.")
        process = start_owned(args, cwd=cwd, env=env, text=False,
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        owner = OwnedProcessRecovery(process, log, deadline)
        if owner_callback is not None:
            owner_callback(owner)
        try:
            try:
                owner.capture(*process.communicate(input.encode() if isinstance(input, str) else input,
                    timeout=min(timeout, max(0, deadline - time.monotonic() - reserve))))
            except BaseException as error:
                owner.preserve_error(error)
                if isinstance(error, subprocess.TimeoutExpired):
                    owner.capture(error.output, error.stderr)
            if not owner.settle(deadline):
                if owner.primary_error is None:
                    owner.primary_error = subprocess.TimeoutExpired(args, timeout)
                owner.finish()
                raise OwnedProcessSettlementError(owner) from owner.primary_error
        finally:
            if owner.complete:
                for stream in (process.stdin, process.stdout, process.stderr):
                    if stream is not None:
                        stream.close()
            owner.record()
        if time.monotonic() > deadline:
            if owner.primary_error is None:
                owner.primary_error = subprocess.TimeoutExpired(args, timeout)
            raise OwnedProcessSettlementError(owner) from owner.primary_error
        if owner.primary_error is not None:
            if owner.record_errors:
                raise OwnedProcessSettlementError(owner) from owner.primary_error
            if isinstance(owner.primary_error, subprocess.TimeoutExpired):
                owner.primary_error.output, owner.primary_error.stderr = owner.stdout_text, owner.stderr_text
            raise owner.primary_error
        result = subprocess.CompletedProcess(args, process.returncode, owner.stdout_text, owner.stderr_text)
        if check and result.returncode:
            raise RuntimeError(f"{args[0]} failed ({result.returncode}): {(result.stdout + result.stderr)[-4000:]}")
        return result
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
