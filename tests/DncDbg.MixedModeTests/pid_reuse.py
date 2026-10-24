#!/usr/bin/env python3
"""Force PID reuse only inside a private Linux PID/mount namespace."""
import argparse
import os
from pathlib import Path
import select
import signal
import subprocess
import sys

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--inside", action="store_true")
parser.add_argument("--dotnet", required=True)
parser.add_argument("--tests", required=True)
parser.add_argument("--python", required=True)
parser.add_argument("--peer", required=True)
args = parser.parse_args()

if not args.inside:
    if sys.platform != "linux":
        print("SKIP forced PID reuse: Linux PID namespaces required", flush=True)
        sys.exit(2)
    result = subprocess.run(["sudo", "unshare", "--mount", "--pid", "--fork", "--mount-proc",
                             sys.executable, str(Path(__file__).resolve()), "--inside",
                             "--dotnet", args.dotnet, "--tests", args.tests, "--python", args.python, "--peer", args.peer])
    sys.exit(result.returncode)

# Never write a host ns_last_pid: PID 1 must be this private namespace supervisor.
if os.getpid() != 1:
    raise RuntimeError("Refusing forced reuse outside the private PID namespace")
try:
    fd = os.open("/proc/sys/kernel/ns_last_pid", os.O_WRONLY)
    os.close(fd)
except OSError as error:
    print("SKIP forced PID reuse: private ns_last_pid is unavailable: " + str(error), flush=True)
    sys.exit(2)

worker = subprocess.Popen([args.dotnet, args.tests, args.python, args.peer, "pid-reuse-worker"],
                          stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
def receive():
    if not select.select([worker.stdout], [], [], 15)[0]:
        raise AssertionError("PID-reuse worker did not reach the identity/signal barrier")
    value = worker.stdout.readline().strip()
    if not value:
        raise AssertionError("PID-reuse worker exited before proof completed")
    return value
def spawn():
    pid = os.fork()
    if pid == 0:
        while True:
            signal.pause()
    return pid
def ticks(pid):
    stat = Path(f"/proc/{pid}/stat").read_text()
    return stat[stat.rindex(")") + 2:].split()[19]
def reap(pid):
    if pid is not None:
        try:
            os.kill(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        try:
            os.waitpid(pid, 0)
        except ChildProcessError:
            pass

original = replacement = None
try:
    if receive() != "READY":
        raise AssertionError("Invalid PID-reuse worker readiness")
    equal_tick = False
    for attempt in range(64):
        original = spawn()
        original_tick = ticks(original)
        worker.stdin.write(f"{original} {os.getpid()}\n")
        worker.stdin.flush()
        if receive() != "VALIDATED":
            raise AssertionError("Target identity was not retained before reuse")
        number = original
        reap(original)
        original = None
        with open("/proc/sys/kernel/ns_last_pid", "w") as value:
            value.write(str(number - 1))
        replacement = spawn()
        if replacement != number:
            raise AssertionError(f"Private namespace did not reuse PID {number}: {replacement}")
        equal_tick = ticks(replacement) == original_tick
        worker.stdin.write("RELEASE\n")
        worker.stdin.flush()
        if receive() != "DONE":
            raise AssertionError("PID-reuse worker did not finish retained-identity cleanup")
        exited, status = os.waitpid(replacement, os.WNOHANG)
        if exited:
            replacement = None
            raise AssertionError(f"Replacement PID {number} was killed after validated generation exited; same_tick={equal_tick}, status={status}")
        print(f"PASS forced PID reuse replacement survives: pid={number}, same_tick={equal_tick}", flush=True)
        reap(replacement)
        replacement = None
        if equal_tick:
            break
    if not equal_tick:
        raise AssertionError("Forced PID reuse passed but equal-clock-tick replacement was not exercised")
    worker.stdin.write("EXIT\n")
    worker.stdin.flush()
    if worker.wait(timeout=5) != 0:
        raise AssertionError("PID-reuse worker failed")
    print("PASS forced PID reuse includes equal Linux creation ticks with replacement survival", flush=True)
finally:
    reap(original)
    reap(replacement)
    if worker.poll() is None:
        worker.kill()
        worker.wait(timeout=5)
