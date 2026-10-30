"""Internal immutable official-archive provisioning for the hosted Android tuple."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import stat
import subprocess
import time
import urllib.request
import uuid
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def owned(path):
    path = path.resolve()
    require(path.is_relative_to((ROOT / "build").resolve()), "Provisioning paths must be in this checkout's build/.")
    return path


def load_lock():
    lock = json.loads((HERE / "android-archives.json").read_text())
    pin = json.loads((HERE / "tuple.json").read_text())["androidX64"]
    require(lock["schemaVersion"] == 1 and lock["host"] == "linux-x64", "Unsupported archive lock.")
    packages = {p["id"]: p for p in lock["packages"]}
    require(len(packages) == len(lock["packages"]) == 2 and
            set(packages) == {"emulator", pin["systemImage"]}, "Missing or duplicate tuple archive.")
    require(packages["emulator"]["revision"] == pin["emulatorVersion"] and
            packages[pin["systemImage"]]["revision"] == pin["imageRevision"], "Archive revisions disagree with tuple.json.")
    require(packages["emulator"]["sdkPath"] == "emulator" and
            packages[pin["systemImage"]]["sdkPath"] == pin["systemImage"].replace(";", "/"),
            "Archive SDK paths disagree with the exact package IDs.")
    for package in packages.values():
        require(re.fullmatch(r"[a-f0-9]{64}", package["sha256"]) and package["size"] > 0,
                "Every archive requires an exact SHA-256 and size.")
        require(package["url"].startswith("https://dl.google.com/android/repository/") and
                package["url"].endswith(".zip") and "latest" not in package["url"], "Archive URL must be versioned and official.")
        require(package["properties"]["Pkg.Revision"] == package["revision"], "Package revision metadata disagrees.")
    return lock


def verify_archive(package, path):
    require(path.stat().st_size == package["size"], "Pinned archive size mismatch.")
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while chunk := source.read(4 * 1024 * 1024):
            digest.update(chunk)
    require(digest.hexdigest() == package["sha256"], "Pinned archive checksum mismatch.")
    with zipfile.ZipFile(path) as archive:
        files = archive.infolist()
        require(files and len(files) <= 100000 and sum(f.file_size for f in files) <= 12 * 1024**3,
                "Archive exceeds its bounded extraction contract.")
        for entry in files:
            parts = PurePosixPath(entry.filename)
            require(not parts.is_absolute() and ".." not in parts.parts and "\\" not in entry.filename and
                    parts.parts and parts.parts[0] == package["archiveRoot"] and
                    stat.S_IFMT(entry.external_attr >> 16) in (0, stat.S_IFREG, stat.S_IFDIR),
                    f"Unsafe archive path/type: {entry.filename}")


def package_xml(package):
    common = "http://schemas.android.com/repository/android/common/02"
    xsi = "http://www.w3.org/2001/XMLSchema-instance"
    ET.register_namespace("common", common)
    ET.register_namespace("xsi", xsi)
    root = ET.Element(f"{{{common}}}repository", {
        "xmlns:generic": "http://schemas.android.com/repository/android/generic/02",
        "xmlns:sys-img": "http://schemas.android.com/sdk/android/repo/sys-img2/05"})
    local = ET.SubElement(root, "localPackage", {"path": package["id"], "obsolete": "false"})
    image = package["id"] != "emulator"
    details = ET.SubElement(local, "type-details", {
        f"{{{xsi}}}type": "sys-img:sysImgDetailsType" if image else "generic:genericDetailsType"})
    if image:
        for name, value in (("api-level", "37.1"), ("extension-level", "23"), ("base-extension", "true")):
            ET.SubElement(details, name).text = value
        for identifier, display in (("google_apis", "Google APIs"), ("page_size_16kb", "16 KB Page Size")):
            tag = ET.SubElement(details, "tag")
            ET.SubElement(tag, "id").text = identifier
            ET.SubElement(tag, "display").text = display
        vendor = ET.SubElement(details, "vendor")
        ET.SubElement(vendor, "id").text = "google"
        ET.SubElement(vendor, "display").text = "Google Inc."
        for name, value in (("abi", "x86_64"), ("abis", "x86_64"), ("translatedAbis", "arm64-v8a")):
            ET.SubElement(details, name).text = value
    revision = ET.SubElement(local, "revision")
    for name, value in zip(("major", "minor", "micro"), package["revision"].split(".")):
        ET.SubElement(revision, name).text = value
    ET.SubElement(local, "display-name").text = package["id"]
    return ET.tostring(root, encoding="utf-8", xml_declaration=True)


def install_archive(package, path, sdk, scratch):
    sdk, scratch = owned(sdk), owned(scratch)
    relative = PurePosixPath(package["sdkPath"])
    require(not relative.is_absolute() and ".." not in relative.parts, "Invalid SDK destination.")
    destination = sdk / str(relative)
    require(not destination.exists() and not destination.is_symlink(), "SDK package already exists; use a fresh owned SDK.")
    verify_archive(package, path)
    stage = scratch / uuid.uuid4().hex
    stage.mkdir(parents=True)
    try:
        with zipfile.ZipFile(path) as archive:
            for entry in archive.infolist():
                target = stage / entry.filename
                if entry.is_dir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with archive.open(entry) as source, target.open("xb") as output:
                        shutil.copyfileobj(source, output, 4 * 1024 * 1024)
                    target.chmod((entry.external_attr >> 16) & 0o777 or 0o644)
        extracted = stage / package["archiveRoot"]
        properties = dict(line.split("=", 1) for line in (extracted / "source.properties").read_text().splitlines() if "=" in line)
        for key, value in package["properties"].items():
            require(properties.get(key) == value, f"Pinned source.properties mismatch: {key}")
        (extracted / "package.xml").write_bytes(package_xml(package))
        destination.parent.mkdir(parents=True, exist_ok=True)
        extracted.rename(destination)
        return destination
    finally:
        shutil.rmtree(stage)


def download(package, cache):
    path = cache / package["url"].rsplit("/", 1)[1]
    if not path.exists():
        partial = path.with_suffix(".partial")
        started = time.monotonic()
        with urllib.request.urlopen(package["url"], timeout=120) as source, partial.open("xb") as output:
            received = 0
            while chunk := source.read(4 * 1024 * 1024):
                received += len(chunk)
                require(received <= package["size"] and time.monotonic() - started <= 1800, "Archive download bound exceeded.")
                output.write(chunk)
        verify_archive(package, partial)
        partial.rename(path)
    verify_archive(package, path)
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk-root", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cache", type=Path, default=ROOT / "build/android-archives")
    parser.add_argument("--verify-only", action="store_true")
    parser.add_argument("--discard-archives", action="store_true", help="Remove verified downloads after installation in ephemeral CI")
    args = parser.parse_args()
    require(args.verify_only or platform.system() == "Linux" and platform.machine() == "x86_64",
            "The immutable hosted archives are Linux x64 only.")
    require(args.verify_only or args.sdk_root is not None, "Select an owned SDK root.")
    output, cache = owned(args.output), owned(args.cache)
    output.mkdir(parents=True, exist_ok=True)
    cache.mkdir(parents=True, exist_ok=True)
    report = {"head": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
              "passed": False, "verifyOnly": args.verify_only, "packages": []}
    try:
        for package in load_lock()["packages"]:
            print("VERIFY OFFICIAL ARCHIVE:", package["url"], flush=True)
            archive = download(package, cache)
            if not args.verify_only:
                install_archive(package, archive, args.sdk_root, cache / "extract")
                if args.discard_archives:
                    archive.unlink()
            report["packages"].append({key: package[key] for key in ("id", "revision", "url", "sha256", "size")})
        report["passed"] = True
        return 0
    except Exception as error:
        report["error"] = repr(error)
        raise
    finally:
        (output / "immutable-archives.json").write_text(json.dumps(report, indent=2))


if __name__ == "__main__":
    raise SystemExit(main())
