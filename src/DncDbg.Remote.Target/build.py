#!/usr/bin/env python3
"""Build explicit RID-specific DNCDbg target agent assets."""
import json
import os
import sys
import time

RIDS = ["iossimulator-arm64", "iossimulator-x64", "ios-arm64", "android-arm64", "android-x64",
        "osx-arm64", "osx-x64", "linux-x64", "linux-arm64"]
began = time.monotonic()
sequence = 0
trace_failed = False
trace_context = {"rid": next((value for value in sys.argv[1:] if value in RIDS), None),
                 "configuration": "Debug", "faults": "--test-faults" in sys.argv}
if "--configuration" in sys.argv:
    position = sys.argv.index("--configuration") + 1
    trace_context["configuration"] = (sys.argv[position]
        if position < len(sys.argv) and sys.argv[position] in ("Debug", "Release") else None)


def emit(stage, phase, **fields):
    global sequence, trace_failed
    if trace_failed or sequence >= 32:
        return
    sequence += 1
    try:
        now = time.monotonic()
        line = json.dumps({"sequence": sequence, "stage": stage, "phase": phase, "pid": os.getpid(),
                           "monotonic": now, "elapsed": now - began, **trace_context, **fields}) + "\n"
        if sys.stderr.write(line) != len(line):
            raise OSError("Native stage diagnostic write was incomplete.")
        sys.stderr.flush()
    except Exception:
        trace_failed = True
        try:
            sys.stderr.write("Native stage diagnostics unavailable; build outcome is unchanged.\n")
            sys.stderr.flush()
        except Exception:
            pass


emit("script", "begin")
import argparse
import hashlib
from pathlib import Path
import platform
import re
import subprocess
import urllib.request


def traced(stage, action):
    emit(stage, "begin")
    try:
        result = action()
    except BaseException as error:
        try:
            emit(stage, "error", error=type(error).__name__)
        except BaseException:
            pass
        raise
    emit(stage, "end")
    return result


ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("rid", nargs="?", choices=RIDS)
parser.add_argument("--test-host", action="store_true", help="Run native contract fakes without loading a runtime or DBI")
parser.add_argument("--test-faults", action="store_true", help="Explicit fixture-only watchdog fault injection; excluded from normal helpers")
parser.add_argument("--configuration", choices=["Debug", "Release"], default="Debug")
args = parser.parse_args()
trace_context.update(rid=args.rid, configuration=args.configuration, faults=args.test_faults)
if not args.rid and not args.test_host:
    parser.error("Choose a target RID or --test-host")
if args.test_faults and args.configuration != "Debug":
    parser.error("Fault injection is test-only and cannot be built into normal Release target assets")
out = ROOT / "build/native-private" / ("host-tests" if args.test_host else args.rid)
out.mkdir(parents=True, exist_ok=True)
deps = ROOT / "build/native-private/deps"
deps.mkdir(parents=True, exist_ok=True)
dependency = traced("dependency-config", lambda: json.loads(
    (Path(__file__).parent / "dependencies.json").read_text())["json"])
header = deps / "json.hpp"
trace_context["cacheHit"] = header.exists()
if not header.exists():
    def download():
        with urllib.request.urlopen(dependency["url"], timeout=30) as response:
            header.write_bytes(response.read())
    traced("dependency-download", download)


def verify_header():
    actual = hashlib.sha256(header.read_bytes()).hexdigest()
    if actual != dependency["sha256"]:
        raise SystemExit("Pinned JSON dependency hash mismatch; refusing to compile.")
    return actual


trace_context["validatedHash"] = traced("dependency-verify", verify_header)
common = ["-std=c++17", "-O1", "-g", "-fPIC", "-fvisibility=hidden",
          "-Wall", "-Wextra", "-Werror", "-I", str(deps), str(Path(__file__).parent / "Bootstrap.cpp")]
def build_identity():
    identity = hashlib.sha256()
    for source in sorted(Path(__file__).parent.glob("*")):
        if source.suffix in (".cpp", ".h", ".json"):
            identity.update(source.name.encode())
            identity.update(source.read_bytes())
    identity.update(f"{args.rid}:{args.configuration}:{args.test_faults}".encode())
    return identity.hexdigest()


build_id = traced("build-id", build_identity)
common.insert(0, '-DDNCDBG_AGENT_BUILD_ID="' + build_id + '"')
if args.test_faults:
    common.insert(0, "-DDNCDBG_TEST_FAULTS")
if args.test_host:
    test = out / "native-contracts"
    command = ["c++", *common[:-1], str(ROOT / "tests/DncDbg.RemoteTests/NativeContracts.cpp"),
               "-pthread", "-o", str(test)]
    if sys.platform != "darwin":
        command.append("-ldl")
    traced("native-compile", lambda: subprocess.run(command, cwd=ROOT, check=True))
    traced("native-contracts", lambda: subprocess.run([str(test)], cwd=ROOT, check=True, timeout=30))
    emit("script", "end")
    raise SystemExit(0)
if args.rid in ("osx-arm64", "osx-x64", "linux-x64", "linux-arm64"):
    expected = ("osx-" if platform.system() == "Darwin" else "linux-") + (
        "arm64" if platform.machine() in ("arm64", "aarch64") else "x64")
    if args.rid != expected:
        raise SystemExit(f"Native host RID {args.rid} does not match this host ({expected}).")
    library = out / ("libdncdbg_target.dylib" if platform.system() == "Darwin" else "libdncdbg_target.so")
    command = ["c++", "-DDNCDBG_NATIVE_HOST", *common, "-pthread",
               "-dynamiclib" if platform.system() == "Darwin" else "-shared", "-o", str(library)]
    if platform.system() != "Darwin":
        command.append("-ldl")
elif args.rid in ("iossimulator-arm64", "iossimulator-x64", "ios-arm64"):
    simulator = args.rid.startswith("iossimulator-")
    sdk = traced("sdk-probe", lambda: subprocess.check_output(
        ["xcrun", "--sdk", "iphonesimulator" if simulator else "iphoneos", "--show-sdk-path"], text=True)).strip()
    library = out / "libdncdbg_target.a"
    obj = out / "Bootstrap.o"
    architecture = "x86_64" if args.rid.endswith("-x64") else "arm64"
    command = ["xcrun", "clang++", *common, "-target",
               architecture + "-apple-ios15.0" + ("-simulator" if simulator else ""),
               "-isysroot", sdk, "-c", "-o", str(obj)]
else:
    sdk = os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT")
    if not sdk:
        raise SystemExit("Set ANDROID_HOME to the installed Android SDK.")
    ndk = Path(sdk) / "ndk/29.0.14206865"
    host = {"Darwin": "darwin-x86_64", "Linux": "linux-x86_64"}.get(platform.system())
    if host is None:
        raise SystemExit("Native mobile helpers require macOS or Linux.")
    target = "x86_64-linux-android" if args.rid == "android-x64" else "aarch64-linux-android"
    compiler = ndk / f"toolchains/llvm/prebuilt/{host}/bin/{target}24-clang++"
    library = out / "libdncdbg_target.so"
    command = [str(compiler), *common, "-shared", "-static-libstdc++", "-ldl", "-o", str(library)]
traced("native-compile", lambda: subprocess.run(command, cwd=ROOT, check=True))
if args.rid in ("iossimulator-arm64", "iossimulator-x64", "ios-arm64"):
    traced("archive", lambda: subprocess.run(["xcrun", "ar", "rcs", str(library), str(obj)], check=True))
print(library.relative_to(ROOT))
def manifest():
    model = int(re.search(r"ModelVersion\s*=\s*(\d+)", (Path(__file__).parent / "RemoteContract.g.h").read_text()).group(1))
    (out / "dncdbg-agent.json").write_text(json.dumps({
        "Name": "dncdbg-target", "Rid": args.rid, "ProtocolVersion": 1, "ModelVersion": model,
        "BuildId": build_id, "Sha256": hashlib.sha256(library.read_bytes()).hexdigest(),
    }, indent=2))


traced("manifest-hash", manifest)
emit("script", "end")
