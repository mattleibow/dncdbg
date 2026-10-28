#!/usr/bin/env python3
"""Pass through real device commands, pausing one completed install for race tests."""
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
if "install" not in arguments:
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
