"""Verify the portable-test dependency closure and imports without trusting global metadata."""
import base64
import hashlib
import importlib
import importlib.machinery
import importlib.metadata as metadata
import importlib.util
import json
from pathlib import Path
import re
import sys


def verify_environment(requirements):
    prefix = Path(sys.prefix).resolve()
    if sys.prefix == sys.base_prefix:
        raise RuntimeError("Portable contracts require an isolated virtual environment.")
    entries = re.findall(r"(?m)^([a-z][a-z0-9-]*)==([^\s\\]+)", requirements.read_text())
    if not entries or len({name for name, _ in entries}) != len(entries):
        raise RuntimeError("Invalid or duplicate locked requirements.")
    expected = dict(entries)
    found, records = {}, []
    for distribution in metadata.distributions():
        name = distribution.metadata["Name"].lower().replace("_", "-")
        if name in found:
            raise RuntimeError("Duplicate distribution: " + name)
        location = Path(distribution.locate_file("")).resolve()
        found[name] = distribution
        records.append({"name": name, "version": distribution.version, "location": str(location)})
        if not location.is_relative_to(prefix):
            raise RuntimeError("Distribution outside selected virtual environment: " + name)
    installed = {name: d.version for name, d in found.items() if name not in {"pip", "setuptools", "wheel"}}
    if installed != expected:
        raise RuntimeError(f"Package inventory differs from the locked closure: {installed} != {expected}")
    modules = []

    def check_module(name, package, spec):
        if spec is None or spec.origin is None:
            raise RuntimeError("Missing owned module: " + name)
        path = Path(spec.origin).resolve()
        if not path.is_relative_to(prefix):
            raise RuntimeError("Module outside selected virtual environment: " + name)
        distribution = found[package]
        files = [entry for entry in distribution.files or [] if Path(distribution.locate_file(entry)).resolve() == path]
        if len(files) != 1 or files[0].hash is None or files[0].hash.mode != "sha256":
            raise RuntimeError("Module is absent from its locked distribution checksum inventory: " + name)
        digest = hashlib.sha256(path.read_bytes()).digest()
        if base64.urlsafe_b64encode(digest).rstrip(b"=").decode() != files[0].hash.value:
            raise RuntimeError("Installed module differs from its locked wheel record: " + name)
        modules.append({"name": name, "distribution": package, "file": str(path), "sha256": digest.hex()})
        return path

    for name, package in (("attr", "attrs"), ("attrs", "attrs"), ("jsonschema", "jsonschema"),
                          ("jsonschema_specifications", "jsonschema-specifications"),
                          ("referencing", "referencing"), ("rpds", "rpds-py")):
        path = check_module(name, package, importlib.util.find_spec(name))
        if name == "rpds":
            spec = importlib.machinery.PathFinder.find_spec("rpds.rpds", [str(path.parent)])
            check_module("rpds.rpds", package, spec)
    for entry in modules:
        module = importlib.import_module(entry["name"])
        if Path(module.__file__).resolve() != Path(entry["file"]):
            raise RuntimeError("Imported module differs from the validated origin: " + entry["name"])
    return {"prefix": str(prefix), "python": sys.version, "complete": False,
            "distributions": records, "modules": modules}


if __name__ == "__main__":
    print(json.dumps(verify_environment(Path(__file__).with_name("requirements.txt")), indent=2))
