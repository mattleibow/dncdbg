"""Publish explicit RID-specific target agent/host/control assets and a content integrity manifest."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/DncDbg.TargetHost"))
from runtime_assets import LOCK, install


def run(*arguments):
    subprocess.run([str(x) for x in arguments], cwd=ROOT, check=True)


def copy_notices(output):
    shutil.copy2(ROOT / "LICENSE", output / "LICENSE")
    shutil.copytree(ROOT / "src/licenses", output / "licenses")
    (output / "THIRD-PARTY-NOTICES.md").write_text(
        (ROOT / "THIRD-PARTY-NOTICES.md").read_text().replace("(src/licenses/", "(licenses/"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=["linux-x64", "linux-arm64", "osx-arm64", "android-arm64", "android-x64", "iossimulator-arm64", "ios-arm64"])
    parser.add_argument("--output", type=Path, required=True)
    runtime = parser.add_mutually_exclusive_group()
    runtime.add_argument("--include-runtime", action="store_true")
    runtime.add_argument("--runtime-directory", type=Path, help="Validate/profile a preprovisioned exact runtime without redeploying it")
    parser.add_argument("--payload", type=Path)
    args = parser.parse_args()
    if args.rid not in LOCK["archives"] and (args.include_runtime or args.runtime_directory or args.payload):
        parser.error("Mobile RID publishing produces agent assets; provision application/runtime/payload through its platform SDK.")
    output = args.output.resolve()
    if not output.is_relative_to((ROOT / "build").resolve()) or output.exists():
        raise RuntimeError("Choose a fresh explicit target deployment directory below build/.")
    output.mkdir(parents=True)
    copy_notices(output)
    run(sys.executable, ROOT / "src/DncDbg.Remote.Target/build.py", args.rid, "--configuration", "Release")
    source = ROOT / "build/native-private" / args.rid
    for path in source.iterdir():
        if path.is_file() and path.name in ("libdncdbg_target.so", "libdncdbg_target.dylib",
                                           "libdncdbg_target.a", "dncdbg-agent.json"):
            shutil.copy2(path, output / path.name)
    if args.rid in LOCK["archives"]:
        actual = ("osx-" if platform.system() == "Darwin" else "linux-") + (
            "arm64" if platform.machine() in ("arm64", "aarch64") else "x64")
        if actual != args.rid:
            raise RuntimeError("Build host assets on the matching OS/architecture or in a matching build container.")
        metadata = json.loads(subprocess.check_output(["dotnet", "msbuild",
            str(ROOT / "src/DncDbg.TargetControl/DncDbg.TargetControl.csproj"),
            "-getProperty:NetCoreRoot", "-getItem:KnownAppHostPack"], cwd=ROOT, text=True))
        pack = next(x for x in metadata["Items"]["KnownAppHostPack"] if x["TargetFramework"] == "net10.0")
        headers = Path(metadata["Properties"]["NetCoreRoot"]) / "packs" / pack["AppHostPackNamePattern"].replace(
            "**RID**", args.rid) / pack["AppHostPackVersion"] / "runtimes" / args.rid / "native"
        command = ["c++", "-std=c++17", "-O2", "-Wall", "-Wextra", "-Werror",
            "-I", headers, "-I", ROOT / "build/native-private/deps",
            ROOT / "src/DncDbg.TargetHost/main.cpp", headers / "libnethost.a", "-pthread", "-o", output / "dncdbg-target-host"]
        command += ["-framework", "Security", "-framework", "CoreFoundation"] if args.rid.startswith("osx") else ["-ldl"]
        run(*command)
        run("dotnet", "publish", ROOT / "src/DncDbg.TargetControl", "-c", "Release",
            "--artifacts-path", output / "compilation", "-o", output / "control", "--nologo", "-v:q")
        shutil.rmtree(output / "compilation")
        runtime = install(args.rid, output / "runtime") if args.include_runtime else args.runtime_directory
        if runtime is not None:
            profile = subprocess.check_output(["dotnet", str(output / "control/dncdbg-target-control.dll"),
                "profile", str(runtime), str(output / "dncdbg-agent.json")], cwd=ROOT, text=True)
            (output / "target-profile.json").write_text(profile)
        if args.payload:
            payload = args.payload.resolve()
            destination = output / "payload"
            destination.mkdir()
            for path in payload.parent.iterdir():
                if path.is_file() and path.suffix in (".dll", ".pdb", ".json"):
                    shutil.copy2(path, destination / path.name)
    manifest = {"SchemaVersion": 1, "Rid": args.rid, "RuntimeVersion": LOCK["version"],
                "Files": {str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest()
                          for p in sorted(output.rglob("*")) if p.is_file()}}
    (output / "integrity.json").write_text(json.dumps(manifest, indent=2))
    print(output)


if __name__ == "__main__":
    main()
