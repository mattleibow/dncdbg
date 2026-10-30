"""Publish explicit RID-specific target agent/host/control assets and a content integrity manifest."""
import argparse
import fnmatch
import hashlib
import io
import json
from pathlib import Path
import platform
import shutil
import stat
import struct
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

EXCLUDED_DIRECTORIES = {"bin", "obj", "build", "test", "tests", "testresults", "tmp", "temp",
                        ".git", ".svn", ".vs", ".idea", ".cache", ".pytest_cache", "__pycache__", "node_modules"}
EXCLUDED_FILES = (".env*", "secrets.json", "*.secrets.json", "credentials.json", "*.pfx", "*.p12",
                  "*.key", "*.pem", "*.snk", "id_rsa*", "id_ed25519*", "*.binlog", "*.trx", "*.tmp", "*.temp",
                  "*.user", "*.suo", "*.pyc")
BUNDLE_SIGNATURE = bytes.fromhex("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae")


def safe_relative(path):
    if path.is_absolute() or any(part in ("", ".", "..") or "\\" in part or ":" in part or
                                 any(ord(c) < 32 for c in part) for part in path.parts):
        raise RuntimeError("Payload has an unsafe or nonportable relative path: " + str(path))


def copy_payload(source, destination):
    source = Path(source)
    if source.is_symlink():
        raise RuntimeError("Payload symbolic links are not deployable.")
    if not source.is_dir():
        raise RuntimeError("Declare the complete published-output directory, not an individual DLL.")
    source = source.resolve()
    if destination.resolve().is_relative_to(source):
        raise RuntimeError("Payload destination cannot be inside the published-output directory.")
    files, names, size = [], set(), 0

    def scan(directory):
        nonlocal size
        for path in sorted(directory.iterdir(), key=lambda p: p.name):
            relative = path.relative_to(source)
            safe_relative(relative)
            mode = path.lstat().st_mode
            if stat.S_ISLNK(mode):
                raise RuntimeError("Payload symbolic links are not deployable: " + str(relative))
            if stat.S_ISDIR(mode):
                if path.name.casefold() not in EXCLUDED_DIRECTORIES:
                    scan(path)
                continue
            if not stat.S_ISREG(mode):
                raise RuntimeError("Payload special files are not deployable: " + str(relative))
            if any(fnmatch.fnmatchcase(path.name.casefold(), pattern) for pattern in EXCLUDED_FILES):
                continue
            key = relative.as_posix().casefold()
            if key in names:
                raise RuntimeError("Payload has a case-colliding path: " + str(relative))
            names.add(key)
            size += path.stat().st_size
            if len(names) > 10000 or size > 2 * 1024**3:
                raise RuntimeError("Payload exceeds the target inventory file/size bound.")
            files.append((path, relative))

    scan(source)
    if not files:
        raise RuntimeError("The published-output directory contains no deployable files.")
    destination.mkdir()
    for path, relative in files:
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)


def copy_single_file(source, destination):
    """Only real version-6 .NET bundles, never guessed DLL closures or loose dependencies."""
    if source.is_symlink() or not source.is_file() or source.stat().st_size > 2 * 1024**3:
        raise RuntimeError("Single-file payload requires a regular bounded .NET bundle.")
    siblings = [p for p in source.parent.iterdir() if p != source and p != source.with_suffix(".pdb")]
    if siblings:
        raise RuntimeError("Single-file output has loose dependencies; publish a complete directory instead.")
    data = source.read_bytes()
    marker = data.find(BUNDLE_SIGNATURE)
    try:
        if marker < 8 or data.find(BUNDLE_SIGNATURE, marker + 1) != -1:
            raise ValueError()
        offset, = struct.unpack_from("<Q", data, marker - 8)
        if offset <= marker + len(BUNDLE_SIGNATURE) or offset + 12 > len(data):
            raise ValueError()
        stream = io.BytesIO(data)
        stream.seek(offset)
        major, minor, count = struct.unpack("<III", stream.read(12))
        if major != 6 or minor != 0 or count not in range(1, 10001):
            raise ValueError()

        def text():
            length = 0
            for shift in range(0, 35, 7):
                value = stream.read(1)[0]
                length |= (value & 127) << shift
                if value < 128:
                    if not 0 < length <= 4096:
                        raise ValueError()
                    result = stream.read(length)
                    if len(result) != length:
                        raise ValueError()
                    return result.decode("utf-8")
            raise ValueError()

        text()  # Bundle extraction identity.
        if len(stream.read(40)) != 40:
            raise ValueError()
        names = set()
        kinds = set()
        for _ in range(count):
            position, size, compressed, kind = struct.unpack("<qqqB", stream.read(25))
            name = text()
            safe_relative(Path(name))
            if name.casefold() in names or position < 0 or size < 0 or compressed < 0 or position + (compressed or size) > offset:
                raise ValueError()
            names.add(name.casefold())
            kinds.add(kind)
        if not {1, 3, 4}.issubset(kinds):  # Managed assembly, deps.json and runtimeconfig.json.
            raise ValueError()
    except (ValueError, IndexError, struct.error, UnicodeError) as error:
        raise RuntimeError("Single-file payload is not a valid complete version-6 .NET bundle.") from error
    destination.mkdir()
    shutil.copy2(source, destination / source.name)


def write_inventory(output, rid):
    def digest(path):
        hash = hashlib.sha256()
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                hash.update(block)
        return hash.hexdigest()
    manifest = {"SchemaVersion": 1, "Rid": rid, "RuntimeVersion": LOCK["version"],
                "Files": {p.relative_to(output).as_posix(): digest(p)
                          for p in sorted(output.rglob("*"), key=lambda p: p.relative_to(output).as_posix())
                          if p.is_file() and p != output / "integrity.json"}}
    (output / "integrity.json").write_text(json.dumps(manifest, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=["linux-x64", "linux-arm64", "osx-arm64", "android-arm64", "android-x64", "iossimulator-arm64", "ios-arm64"])
    parser.add_argument("--output", type=Path, required=True)
    runtime = parser.add_mutually_exclusive_group()
    runtime.add_argument("--include-runtime", action="store_true")
    runtime.add_argument("--runtime-directory", type=Path, help="Validate/profile a preprovisioned exact runtime without redeploying it")
    payload = parser.add_mutually_exclusive_group()
    payload.add_argument("--payload-directory", type=Path, help="Complete declared dotnet publish output directory")
    payload.add_argument("--payload-file", type=Path, help="Explicit standalone version-6 .NET single-file bundle")
    args = parser.parse_args()
    if args.rid not in LOCK["archives"] and (args.include_runtime or args.runtime_directory or args.payload_directory or args.payload_file):
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
        if args.payload_directory:
            copy_payload(args.payload_directory, output / "payload")
        elif args.payload_file:
            copy_single_file(args.payload_file, output / "payload")
    write_inventory(output, args.rid)
    print(output)


if __name__ == "__main__":
    main()
