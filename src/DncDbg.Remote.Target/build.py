#!/usr/bin/env python3
"""Build only the explicitly pinned arm64 simulator/emulator experimental helper."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("rid", nargs="?", choices=["iossimulator-arm64", "android-arm64"])
parser.add_argument("--test-host", action="store_true", help="Run native contract fakes without loading a runtime or DBI")
parser.add_argument("--test-faults", action="store_true", help="Explicit fixture-only watchdog fault injection; excluded from normal helpers")
parser.add_argument("--configuration", choices=["Debug", "Release"], default="Debug")
args = parser.parse_args()
if not args.rid and not args.test_host:
    parser.error("Choose a target RID or --test-host")
if args.rid and args.configuration != "Debug":
    parser.error("The experimental target-native provider is Debug-fixture-only, never a Release artifact")
out = ROOT / "build/native-private" / ("host-tests" if args.test_host else args.rid)
out.mkdir(parents=True, exist_ok=True)
deps = ROOT / "build/native-private/deps"
deps.mkdir(parents=True, exist_ok=True)
dependency = json.loads((Path(__file__).parent / "dependencies.json").read_text())["json"]
header = deps / "json.hpp"
if not header.exists():
    with urllib.request.urlopen(dependency["url"], timeout=30) as response:
        header.write_bytes(response.read())
if hashlib.sha256(header.read_bytes()).hexdigest() != dependency["sha256"]:
    raise SystemExit("Pinned JSON dependency hash mismatch; refusing to compile.")
common = ["-std=c++17", "-O1", "-g", "-fPIC", "-fvisibility=hidden",
          "-Wall", "-Wextra", "-Werror", "-I", str(deps), str(Path(__file__).parent / "Bootstrap.cpp")]
if args.test_faults:
    common.insert(0, "-DDNCDBG_TEST_FAULTS")
if args.test_host:
    import sys
    test = out / "native-contracts"
    command = ["c++", *common[:-1], str(ROOT / "tests/DncDbg.RemoteTests/NativeContracts.cpp"),
               "-pthread", "-o", str(test)]
    if sys.platform != "darwin":
        command.append("-ldl")
    subprocess.run(command, cwd=ROOT, check=True)
    subprocess.run([str(test)], cwd=ROOT, check=True, timeout=30)
    raise SystemExit(0)
if args.rid == "iossimulator-arm64":
    sdk = subprocess.check_output(["xcrun", "--sdk", "iphonesimulator", "--show-sdk-path"], text=True).strip()
    library = out / "libdncdbg_target.a"
    obj = out / "Bootstrap.o"
    command = ["xcrun", "clang++", *common, "-target", "arm64-apple-ios15.0-simulator",
               "-isysroot", sdk, "-c", "-o", str(obj)]
else:
    sdk = os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT")
    if not sdk:
        raise SystemExit("Set ANDROID_HOME to the installed Android SDK.")
    ndk = Path(sdk) / "ndk/29.0.14206865"
    host = {"Darwin": "darwin-x86_64", "Linux": "linux-x86_64"}.get(platform.system())
    if host is None:
        raise SystemExit("Native mobile helpers require macOS or Linux.")
    compiler = ndk / f"toolchains/llvm/prebuilt/{host}/bin/aarch64-linux-android24-clang++"
    library = out / "libdncdbg_target.so"
    command = [str(compiler), *common, "-shared", "-static-libstdc++", "-ldl", "-o", str(library)]
subprocess.run(command, cwd=ROOT, check=True)
if args.rid == "iossimulator-arm64":
    subprocess.run(["xcrun", "ar", "rcs", str(library), str(obj)], check=True)
print(library.relative_to(ROOT))
