"""Owned POSIX commands shared by remote/native/mobile test entrypoints."""
import os
import ctypes
import errno
import json
import selectors
import signal
import socket
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def process_identity(pid):
    """Public kernel birth identity, never an ancestry or executable-name inference."""
    try:
        if sys.platform == "darwin":
            class BsdInfo(ctypes.Structure):
                _fields_ = [(name, ctypes.c_uint32) for name in (
                    "flags", "status", "xstatus", "pid", "ppid", "uid", "gid", "ruid", "rgid",
                    "svuid", "svgid", "reserved")] + [
                    ("comm", ctypes.c_char * 16), ("name", ctypes.c_char * 32),
                    ("nfiles", ctypes.c_uint32), ("pgid", ctypes.c_uint32), ("jobc", ctypes.c_uint32),
                    ("tdev", ctypes.c_uint32), ("tpgid", ctypes.c_uint32), ("nice", ctypes.c_int32),
                    ("start_seconds", ctypes.c_uint64), ("start_microseconds", ctypes.c_uint64)]
            library = ctypes.CDLL("/usr/lib/libproc.dylib", use_errno=True)
            query = library.proc_pidinfo
            query.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_int]
            query.restype = ctypes.c_int
            info = BsdInfo()
            size = query(pid, 3, 0, ctypes.byref(info), ctypes.sizeof(info))
            if size != ctypes.sizeof(info):
                code = ctypes.get_errno()
                if size == 0 and code in (errno.ESRCH, errno.ENOENT):
                    return None
                raise OSError(code or errno.EIO, "Cannot read the exact proc_pidinfo birth identity.")
            if info.pid != pid:
                raise RuntimeError("Kernel process identity returned a mismatched PID.")
            return {"pid": pid, "ppid": info.ppid, "pgid": info.pgid, "sid": os.getsid(pid),
                    "uid": info.uid, "state": info.status,
                    "startIdentity": f"{info.start_seconds}:{info.start_microseconds}"}
        if sys.platform.startswith("linux"):
            directory = Path("/proc") / str(pid)
            stat = (directory / "stat").read_text()
            fields = stat[stat.rfind(")") + 2:].split()
            uid = next(line.split()[2] for line in (directory / "status").read_text().splitlines()
                       if line.startswith("Uid:"))
            return {"pid": pid, "ppid": int(fields[1]), "pgid": int(fields[2]), "sid": int(fields[3]),
                    "uid": int(uid), "state": fields[0], "startIdentity": fields[19]}
        raise RuntimeError("Exact process birth identities require Linux /proc or macOS public libproc.")
    except (FileNotFoundError, ProcessLookupError):
        return None


class ProcessGroupOwnership:
    """Authorize only the retained original session/group and journaled kernel identities."""

    def __init__(self, recovery):
        self.recovery = recovery
        self.original = None
        self.capture_state = "not-applicable"
        self.members = {}
        self.observations = []
        self.signals = []
        self.uid, self.euid = os.getuid(), os.geteuid()
        self.created = time.monotonic()

    @staticmethod
    def same(left, right):
        return all(left[key] == right[key] for key in ("pid", "pgid", "sid", "uid", "startIdentity"))

    def capture(self):
        if self.capture_state != "not-applicable":
            return
        self.capture_state = "unverified"
        if self.recovery.process is None or self.recovery.process.returncode is not None:
            return
        identity = process_identity(self.recovery.process.pid)
        if identity is not None and identity["state"] not in ("Z", 5):
            if identity["pgid"] != identity["pid"] or identity["sid"] != identity["pid"] or identity["uid"] != self.euid:
                raise RuntimeError("Launched process does not own the expected exact group/session/UID.")
            self.original = identity
            self.members[identity["pid"]] = identity
            self.capture_state = "verified"

    def observe(self, deadline, retry=True):
        process = self.recovery.process
        polled = process.poll()
        group = process.pid
        remaining = deadline - time.monotonic() if deadline is not None else 2
        if remaining <= 0:
            raise subprocess.TimeoutExpired(["ps", "-g", str(group)], 0)
        argv = ["ps", "-g", str(group), "-o", "pid="]
        probe = subprocess.run(argv, check=False, capture_output=True, text=True, timeout=min(2, remaining))
        snapshot = {"at": time.monotonic(), "argv": argv, "returnCode": probe.returncode,
                    "stderr": probe.stderr[:1000], "popenPollResult": polled,
                    "popenReturnCode": process.returncode, "pidOutput": probe.stdout[:4000], "rows": []}
        if len(self.observations) >= 128:
            raise RuntimeError("Owned group observation bound exhausted.")
        self.observations.append(snapshot)
        fields = probe.stdout.split()
        if (probe.returncode not in (0, 1) or probe.stderr.strip() or
                probe.returncode == 1 and fields or any(not value.isdecimal() or int(value) <= 0 for value in fields) or
                len(fields) > 1024 or len(fields) != len(set(fields))):
            raise RuntimeError("Cannot verify the exact owned process group observation.")
        if fields and self.capture_state != "verified":
            raise RuntimeError("Original process birth capture is unverified; refusing every group signal.")
        for value in fields:
            identity = process_identity(int(value))
            if identity is None:
                if retry:
                    return self.observe(deadline, retry=False)
                raise RuntimeError("Owned group identity changed during observation; absence is unverified.")
            snapshot["rows"].append(identity)
        rows = snapshot["rows"]
        if not rows:
            return rows
        if self.original is None:
            raise RuntimeError("A live group has no retained original process identity.")
        leader = process_identity(group)
        if leader is not None and not self.same(leader, self.original):
            raise RuntimeError("Original process birth/session/UID identity changed; refusing a signal.")
        if any(row["pgid"] != group or row["sid"] != self.original["sid"] or row["uid"] != self.original["uid"]
               for row in rows):
            raise RuntimeError("Scoped group observation contains a different group/session/UID.")
        if not any(row["pid"] in self.members and self.same(row, self.members[row["pid"]]) for row in rows):
            raise RuntimeError("No exact original or journaled member identity authorizes the group.")
        self.members.update((row["pid"], row) for row in rows)
        return rows

    def send(self, value, deadline):
        group = self.recovery.process.pid
        if value and any(attempt["signal"] == int(value) for attempt in self.signals):
            return self.exists(deadline)
        before = self.observe(deadline)
        if not before:
            return False
        attempt = {"at": time.monotonic(), "signal": int(value), "before": before,
                   "after": None, "errno": None, "returnCodeBefore": self.recovery.process.returncode}
        self.signals.append(attempt)
        try:
            os.killpg(group, value)
        except ProcessLookupError:
            attempt["errno"], attempt["after"] = errno.ESRCH, []
            return False
        except PermissionError as error:
            attempt["errno"] = error.errno
            attempt["after"] = self.observe(deadline)
            if not attempt["after"]:
                return False
            raise
        except OSError as error:
            attempt["errno"] = error.errno
            raise
        attempt["after"] = self.observe(deadline)
        return bool(attempt["after"])

    def exists(self, deadline):
        return self.send(0, deadline)

    def report(self):
        process = self.recovery.process
        return {"platform": sys.platform, "ownerUid": self.uid, "ownerEuid": self.euid,
                "identityCapture": self.capture_state,
                "ownerCreatedMonotonic": self.created, "originalIdentity": self.original,
                "memberIdentities": list(self.members.values()), "groupObservations": self.observations,
                "signalAttempts": self.signals, "popenReturnCode": process.returncode if process else None,
                "operationDeadlineMonotonic": self.recovery.deadline,
                "recoveryDeadlineMonotonic": self.recovery.recovery_deadline}


class OwnedProcessSettlementError(RuntimeError):
    """Failed command with a retained, separately bounded recovery owner."""

    def __init__(self, recovery):
        self.recovery = recovery
        super().__init__("Owned command required failure settlement; inspect its retained recovery owner.")


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
        self.record_failure = None
        self.primary_error = None
        self.operation_error = None
        self.failure_exception = OwnedProcessSettlementError(self)
        self.exec_error_read = self.exec_error_write = None
        self.group_ownership = None
        self.file_output = False

    @property
    def complete(self):
        return ((self.process is None or self.leader_reaped and self.group_absent and self.drain_complete)
                and self.exec_error_read is None and self.exec_error_write is None and
                (self.process is None or self.group_ownership is None or self.group_ownership.capture_state == "verified"))

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
        if self.operation_error is not None:
            self.primary_error = self.operation_error
            return
        current = self.primary_error
        if (current is None or
                isinstance(current, subprocess.TimeoutExpired) and not isinstance(error, subprocess.TimeoutExpired) or
                original_cancellation(error) is not None and original_cancellation(current) is None):
            self.primary_error = error

    def close_exec_error_pipe(self, read=True):
        previous = signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGINT, signal.SIGTERM})
        try:
            for name in (("exec_error_read", "exec_error_write") if read else ("exec_error_write",)):
                fd = getattr(self, name)
                if fd is not None:
                    try:
                        os.close(fd)
                    finally:
                        setattr(self, name, None)
        finally:
            signal.pthread_sigmask(signal.SIG_SETMASK, previous)

    def await_exec(self, command, deadline):
        data = bytearray()
        ready = None
        try:
            ready = selectors.DefaultSelector()
            ready.register(self.exec_error_read, selectors.EVENT_READ)
            while True:
                available = deadline - time.monotonic()
                if available <= 0 or not ready.select(max(0, available)):
                    raise subprocess.TimeoutExpired(command, max(0, available))
                part = os.read(self.exec_error_read, 257 - len(data))
                if not part:
                    break
                data.extend(part)
                if len(data) > 256:
                    raise RuntimeError("Owned exec-error record exceeds its bounded size.")
            if data:
                try:
                    record = json.loads(data)
                except (ValueError, UnicodeError) as error:
                    raise RuntimeError("Malformed owned exec-error record.") from error
                if (not isinstance(record, dict) or set(record) != {"version", "errno"}
                        or type(record["version"]) is not int or record["version"] != 1
                        or type(record["errno"]) is not int or not 0 < record["errno"] < 4096):
                    raise RuntimeError("Invalid owned exec-error record.")
                raise OSError(record["errno"], os.strerror(record["errno"]), command[0])
        except BaseException as error:
            self.preserve_error(error)
            raise
        finally:
            try:
                if ready is not None:
                    ready.close()
            finally:
                self.close_exec_error_pipe()

    def settle(self, deadline):
        try:
            if not self.group_absent:
                available = deadline - time.monotonic()
                if available <= 0:
                    raise subprocess.TimeoutExpired(self.process.args, 0)
                arguments = {"ownership": self.group_ownership} if self.group_ownership is not None else {}
                stop_owned(self.process, grace_period=min(0.1, available / 4),
                           settle_timeout=max(0, available / 2), deadline=deadline, **arguments)
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
            try:
                self.close_exec_error_pipe()
            except BaseException as error:
                self.preserve_error(error)
                self.errors.append(type(error).__name__)
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
        self.safe_record()
        return self.complete

    def safe_record(self):
        try:
            self.record()
        except BaseException as error:
            self.record_failure = error
            self.preserve_error(error)
            try:
                self.record_errors.append(type(error).__name__)
            except BaseException:
                pass  # Exhausted bookkeeping must not discard the retained owner/primary failure.

    def record(self):
        if self.log and self.process is not None:
            if self.file_output:
                self.capture(Path(self.log).read_bytes(), b"")
            report = {
                "pid": self.process.pid, "processGroup": self.process.pid,
                "ownershipScope": "leader-and-original-process-group",
                "leaderReaped": self.leader_reaped, "groupAbsent": self.group_absent,
                "drainComplete": self.drain_complete, "resourceJournalClean": False,
                "returnCode": self.process.returncode, "recoveryComplete": self.complete,
                "primaryError": type(self.primary_error).__name__ if self.primary_error else None,
                "operationError": type(self.operation_error).__name__ if self.operation_error else None,
                "deadlineExceeded": time.monotonic() > self.deadline,
                "failureRecoveryBudgetSeconds": 5, "recoveryErrors": self.errors,
                "recordErrors": self.record_errors,
                "partialStdout": self.stdout_text[-4000:], "partialStderr": self.stderr_text[-4000:],
                "stdoutBytes": len(self.stdout), "stderrBytes": len(self.stderr),
            }
            if self.group_ownership is not None:
                report.update(self.group_ownership.report())
            for path, is_report in ((Path(self.log), False), (Path(self.log).with_suffix(".settlement.json"), True)):
                if self.file_output and not is_report:
                    continue
                try:
                    report["primaryError"] = type(self.primary_error).__name__ if self.primary_error else None
                    content = json.dumps(report, indent=2) if is_report else self.stdout_text + self.stderr_text
                    path.write_text(content)
                except BaseException as error:
                    self.record_errors.append(f"{path.name}: {error}")
                    self.preserve_error(error)


def start_owned(*args, owner=None, **kwargs):
    if os.name != "posix":
        raise RuntimeError("The remote integration runner requires POSIX process-group ownership")
    if owner is None:
        return subprocess.Popen(*args, start_new_session=True, **kwargs)
    if owner.group_ownership is None:
        owner.group_ownership = ProcessGroupOwnership(owner)
    if not kwargs.get("close_fds", True):
        raise ValueError("Owned/deadline launches reject close_fds=False; use explicit pass_fds.")
    previous = signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGINT, signal.SIGTERM})
    try:
        passed = tuple(sorted(set(kwargs.pop("pass_fds", ()))))
        for fd in passed:
            os.fstat(fd)
        owner.exec_error_read, owner.exec_error_write = os.pipe()
        dispositions = []
        for name in ("SIGPIPE", "SIGXFZ", "SIGXFSZ"):
            if hasattr(signal, name):
                number = getattr(signal, name)
                value = signal.SIG_DFL if kwargs.get("restore_signals", True) or signal.getsignal(number) != signal.SIG_IGN else signal.SIG_IGN
                dispositions.append(f"{int(number)}:{int(value)}")
        command = [sys.executable, "-I", str(Path(__file__).resolve()), "--restore-signal-mask",
                   ",".join(str(int(value)) for value in previous), "--exec-error-fd", str(owner.exec_error_write),
                   "--signal-dispositions", ",".join(dispositions), "--", *(os.fsdecode(value) for value in args[0])]
        process = subprocess.Popen(command, start_new_session=True, pass_fds=(*passed, owner.exec_error_write), **kwargs)
        owner.process = process
        if owner.group_ownership is not None:
            owner.group_ownership.capture()
        return process
    finally:
        try:
            owner.close_exec_error_pipe(read=owner.process is None)
        finally:
            signal.pthread_sigmask(signal.SIG_SETMASK, previous)


def stop_owned(process, grace_period=1, settle_timeout=1, deadline=None, ownership=None):
    def remaining(limit):
        return limit if deadline is None else min(limit, max(0, deadline - time.monotonic()))

    def group_exists():
        if ownership is not None:
            return ownership.exists(deadline)
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
            rows = probe.stdout.split()
            if any(not row.isdecimal() or int(row) != process.pid for row in rows):
                raise RuntimeError("Owned process group probe returned malformed or mismatched group rows.")
            return bool(rows)

    def signal_group(value):
        if ownership is not None:
            return ownership.send(value, deadline)
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


def run_owned(*args, env=None, check=True, input=None, timeout=180, log=None, cwd=ROOT, deadline=None,
              owner_callback=None, recovery_owner=None):
    if deadline is not None:
        # Reserve settlement time inside the caller's budget, not after its expiry.
        available = max(0, deadline - time.monotonic())
        if not available:
            raise subprocess.TimeoutExpired(args, timeout)
        reserve = min(1, available / 4)
        if any(arg in ("--daemon", "--daemonize", "--background") for arg in args):
            raise ValueError("Daemonizing commands require a separate stable resource journal, not group ownership.")
        owner = recovery_owner if recovery_owner is not None else OwnedProcessRecovery(None, log, deadline)
        if owner.process is not None:
            raise ValueError("An owned command cannot reuse an existing process owner.")
        callback_complete = owner_callback is None
        try:
            try:
                start_owned(args, owner=owner, cwd=cwd, env=env, text=False,
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
                process = owner.process
                if owner_callback is not None:
                    owner_callback(owner)
                    callback_complete = True
                owner.await_exec(args, deadline - reserve)
                owner.capture(*process.communicate(input.encode() if isinstance(input, str) else input,
                    timeout=min(timeout, max(0, deadline - time.monotonic() - reserve))))
            except BaseException as error:
                owner.preserve_error(error)
                if isinstance(error, subprocess.TimeoutExpired):
                    owner.capture(error.output, error.stderr)
            if owner.process is None:
                raise owner.primary_error
            if not owner.settle(deadline):
                if owner.primary_error is None:
                    owner.primary_error = subprocess.TimeoutExpired(args, timeout)
                owner.finish()
                raise owner.failure_exception from owner.primary_error
            if not callback_complete:
                raise owner.failure_exception from owner.primary_error
        finally:
            process = owner.process
            if process is not None and owner.complete:
                for stream in (process.stdin, process.stdout, process.stderr):
                    if stream is not None:
                        stream.close()
            owner.safe_record()
        if time.monotonic() > deadline:
            if owner.primary_error is None:
                owner.primary_error = subprocess.TimeoutExpired(args, timeout)
            raise owner.failure_exception from owner.primary_error
        if owner.primary_error is not None:
            if owner.record_errors:
                raise owner.failure_exception from owner.primary_error
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


if __name__ == "__main__":
    if (len(sys.argv) < 9 or sys.argv[1] != "--restore-signal-mask" or sys.argv[3] != "--exec-error-fd"
            or sys.argv[5] != "--signal-dispositions" or sys.argv[7] != "--"):
        raise SystemExit("Internal owned-command exec trampoline.")
    error_fd = int(sys.argv[4])
    try:
        os.set_inheritable(error_fd, False)
        for item in sys.argv[6].split(","):
            number, disposition = item.split(":")
            signal.signal(int(number), int(disposition))
        signal.pthread_sigmask(signal.SIG_SETMASK, {int(value) for value in sys.argv[2].split(",") if value})
        os.execvpe(sys.argv[8], sys.argv[8:], os.environ)
    except OSError as error:
        try:
            message = json.dumps({"version": 1, "errno": error.errno}).encode()
            while message:
                message = message[os.write(error_fd, message):]
        finally:
            os._exit(255)
