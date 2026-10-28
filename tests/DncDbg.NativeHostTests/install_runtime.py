"""Install only the SHA-512-locked official runtime beneath this checkout's build/; no MAUI workload."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import tarfile
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
LOCK = json.loads(Path(__file__).with_name("runtime-lock.json").read_text())


def install(rid, destination):
    destination = destination.resolve()
    if not destination.is_relative_to((ROOT / "build").resolve()):
        raise RuntimeError("Runtime installation must be below this checkout's build/.")
    package = LOCK["archives"][rid]
    archive = ROOT / "build/runtime-downloads" / Path(package["url"]).name
    archive.parent.mkdir(parents=True, exist_ok=True)
    if not archive.exists():
        with urllib.request.urlopen(package["url"], timeout=60) as response, archive.open("xb") as target:
            shutil.copyfileobj(response, target)
    if hashlib.sha512(archive.read_bytes()).hexdigest() != package["sha512"]:
        raise RuntimeError("Pinned official runtime archive hash mismatch; refusing extraction.")
    marker = destination / "dncdbg-runtime-lock.json"
    if destination.exists():
        if marker.exists() and json.loads(marker.read_text()) == package:
            return destination / "shared/Microsoft.NETCore.App" / LOCK["version"]
        raise RuntimeError("Choose a fresh runtime destination; never overwrite another installation.")
    destination.mkdir(parents=True)
    with tarfile.open(archive, "r:gz") as source:
        for member in source.getmembers():
            path = (destination / member.name).resolve()
            if not path.is_relative_to(destination) or not (member.isdir() or member.isfile()):
                raise RuntimeError("Unexpected archive traversal/link/special file.")
            if member.isdir():
                path.mkdir(parents=True, exist_ok=True)
            else:
                path.parent.mkdir(parents=True, exist_ok=True)
                with source.extractfile(member) as content, path.open("xb") as output:
                    shutil.copyfileobj(content, output)
                os.chmod(path, member.mode & 0o777)
    marker.write_text(json.dumps(package, indent=2))
    return destination / "shared/Microsoft.NETCore.App" / LOCK["version"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=list(LOCK["archives"]))
    parser.add_argument("--destination", type=Path, default=ROOT / "build/native-runtime")
    args = parser.parse_args()
    rid = args.rid or ("osx-" if platform.system() == "Darwin" else "linux-") + (
        "arm64" if platform.machine() in ("arm64", "aarch64") else "x64")
    print(install(rid, args.destination))
