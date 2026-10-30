#!/usr/bin/env python3
"""Pass through real device commands, withholding a completed acquisition's reply."""
import json
import os
from pathlib import Path
import subprocess
import sys
import time

state = Path(os.environ["DNCDBG_INSTALL_GATE"])
real = os.environ["DNCDBG_INSTALL_GATE_TOOL"]
arguments = sys.argv[1:]
with (state / "commands.jsonl").open("a") as log:
    log.write(json.dumps([Path(real).name, *arguments]) + "\n")
stage = os.environ.get("DNCDBG_FORWARD_GATE_STAGE")
forward = "forward" in arguments and (
    stage == "allocation" and "tcp:0" in arguments or
    stage == "rebind" and arguments[-2].startswith("tcp:") and "--no-rebind" not in arguments)
if forward:
    result = subprocess.run([real, *arguments], capture_output=True, check=False)
    if result.returncode:
        sys.stderr.buffer.write(result.stderr)
        raise SystemExit(result.returncode)
    (state / "forward-blocked").write_text(json.dumps({"arguments": arguments, "result": result.stdout.decode().strip()}))
    deadline = time.monotonic() + 120
    while not (state / "release-forward").exists():
        if time.monotonic() >= deadline:
            raise SystemExit("Forward gate was not released within its absolute deadline.")
        time.sleep(0.01)
    sys.stdout.buffer.write(result.stdout)
    raise SystemExit(0)
if stage or "install" not in arguments:
    os.execv(real, [real, *arguments])
result = subprocess.run([real, *arguments], check=False)
if result.returncode:
    raise SystemExit(result.returncode)
counter = state / "install-count"
number = int(counter.read_text()) + 1 if counter.exists() else 1
counter.write_text(str(number))
if number == int(os.environ["DNCDBG_INSTALL_GATE_NUMBER"]):
    (state / "install-blocked").touch()
    deadline = time.monotonic() + 120
    while not (state / "release-install").exists():
        if time.monotonic() >= deadline:
            raise SystemExit("Install gate was not released within its absolute deadline.")
        time.sleep(0.01)
