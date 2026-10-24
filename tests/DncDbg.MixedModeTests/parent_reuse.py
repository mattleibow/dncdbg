#!/usr/bin/env python3
"""Force same-tick intermediate-parent reuse only in a private Linux PID namespace."""
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
        print("SKIP intermediate-parent reuse: Linux namespace required", flush=True)
        sys.exit(77)
    sys.exit(subprocess.run(["sudo", "unshare", "--mount", "--pid", "--fork", "--mount-proc",
                            sys.executable, str(Path(__file__).resolve()), "--inside", "--dotnet", args.dotnet,
                            "--tests", args.tests, "--python", args.python, "--peer", args.peer]).returncode)
if os.getpid() != 1:
    raise RuntimeError("Refusing PID allocation changes outside the private namespace")
try:
    fd = os.open("/proc/sys/kernel/ns_last_pid", os.O_WRONLY)
    os.close(fd)
except OSError as error:
    print("SKIP intermediate-parent reuse: private allocator unavailable: " + str(error), flush=True)
    sys.exit(77)

worker = subprocess.Popen([args.dotnet, args.tests, args.python, args.peer, "parent-reuse-worker"],
                          stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
def receive():
    if not select.select([worker.stdout], [], [], 15)[0]:
        raise AssertionError("Intermediate-parent reuse worker did not reach its barrier")
    text = worker.stdout.readline().strip()
    if not text:
        raise AssertionError("Intermediate-parent reuse worker exited")
    return text
def send(text):
    worker.stdin.write(text + "\n")
    worker.stdin.flush()
def ticks(pid):
    stat = Path(f"/proc/{pid}/stat").read_text()
    return stat[stat.rindex(")") + 2:].split()[19]
def reap(pid):
    if pid:
        try:
            os.kill(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        try:
            os.waitpid(pid, 0)
        except ChildProcessError:
            pass
def unrelated():
    read, write = os.pipe()
    parent = os.fork()
    if parent == 0:
        os.close(read)
        target = os.fork()
        if target == 0:
            os.close(write)
            while True:
                signal.pause()
        os.write(write, str(target).encode())
        os.close(write)
        while True:
            signal.pause()
    os.close(write)
    target = int(os.read(read, 32))
    os.close(read)
    return parent, target

parent = target = None
try:
    if receive() != "READY":
        raise AssertionError("Invalid ancestry proof readiness")
    matched = False
    for attempt in range(128):
        parent, target = unrelated()
        target_tick = ticks(target)
        send(f"{target} {parent}")
        if receive() != "PARENT_READ":
            raise AssertionError("Production ancestry did not read the unrelated target PPID")
        number = parent
        reap(parent)
        parent = None
        # T now belongs to namespace PID1, not the replacing adapter child.
        with open("/proc/sys/kernel/ns_last_pid", "w") as allocator:
            allocator.write(str(number - 1))
        send("REPLACE")
        replacement = receive()
        if replacement != f"REPLACEMENT {number}":
            raise AssertionError("Adapter did not receive the reused intermediate PID: " + replacement)
        matched = ticks(number) == target_tick
        send("CONTINUE")
        result = receive()
        exited, status = os.waitpid(target, os.WNOHANG)
        if exited:
            target = None
            raise AssertionError(f"Unrelated target killed through reused intermediate parent; same_tick={matched}, status={status}")
        if result != "DONE rejected":
            raise AssertionError(f"Reused intermediate ancestry authorized unrelated target; same_tick={matched}, result={result}")
        print(f"PASS intermediate-parent replacement rejected and unrelated target survives: same_tick={matched}", flush=True)
        reap(target)
        target = None
        if matched:
            break
    if not matched:
        raise AssertionError("Intermediate-parent proof never exercised equal target/replacement creation ticks")
    print("PASS same-tick intermediate-parent PID reuse cannot authorize independent target termination", flush=True)
    matched = False
    for attempt in range(128):
        send("ROOT")
        root = receive()
        if not root.startswith("ROOT "):
            raise AssertionError("Native session did not start its adapter root: " + root)
        number = int(root.split()[1])
        original_tick = ticks(number)
        os.kill(number, signal.SIGKILL)
        send("REAP_ROOT")
        if receive() != "ROOT_REAPED":
            raise AssertionError("Original adapter root was not reaped")
        with open("/proc/sys/kernel/ns_last_pid", "w") as allocator:
            allocator.write(str(number - 1))
        parent, target = unrelated()
        if parent != number:
            raise AssertionError(f"Private namespace did not reuse root PID {number}: {parent}")
        matched = original_tick == ticks(parent) == ticks(target)
        send(f"CAPTURE_ROOT {target}")
        result = receive()
        # This target still belongs to the live replacement root, not PID1.
        # Inspect its state; waitpid is valid only after that parent is reaped.
        stat = Path(f"/proc/{target}/stat").read_text()
        if stat[stat.rindex(")") + 2:].split()[0] in ("Z", "X"):
            raise AssertionError(f"Replacement adapter root authorized killing its unrelated target; same_tick={matched}")
        if result != "ROOT_DONE rejected":
            raise AssertionError(f"Replacement root acquired cleanup authority; same_tick={matched}, result={result}")
        print(f"PASS replacement adapter root rejected and unrelated target survives: same_tick={matched}", flush=True)
        reap(parent)
        parent = None
        exited, status = os.waitpid(target, os.WNOHANG)
        if exited:
            target = None
            raise AssertionError(f"Replacement adapter root authorized killing its unrelated target; same_tick={matched}, status={status}")
        reap(target)
        target = None
        if matched:
            break
    if not matched:
        raise AssertionError("Adapter-root proof never exercised equal original/replacement/target creation ticks")
    send("EXIT")
    if worker.wait(timeout=5) != 0:
        raise AssertionError("Intermediate-parent proof worker failed")
    print("PASS startup-retained root rejects same-tick adapter PID reuse", flush=True)
finally:
    reap(parent)
    reap(target)
    if worker.poll() is None:
        worker.kill()
        worker.wait(timeout=5)
