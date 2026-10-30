"""Keep target-native deployment assets and test hooks out of ordinary host artifacts."""
from pathlib import Path
import sys
import zipfile

MARKERS = (b"dncdbg_prepare", b"dncdbg_complete", b"dncdbg_test_stall_owner", b"libdncdbg_target",
           b"dotnet/testCloseTunnel", "dotnet/testCloseTunnel".encode("utf-16-le"), b"FixtureControlServer")


def inspect(name, data):
    lowered = name.lower()
    if any(part in lowered for part in ("dncdbg_target", "bootstrap.cpp", "nativecontracts", "devflow.agent")):
        raise AssertionError(f"Target-native/test-only asset in ordinary host artifacts: {name}")
    if any(marker in data for marker in MARKERS):
        raise AssertionError(f"Target-native import/test hook in ordinary host artifacts: {name}")


def verify(path):
    path = Path(path)
    if not path.exists():
        raise AssertionError(f"Missing artifact: {path}")
    files = list(path.rglob("*")) if path.is_dir() else [path]
    count = 0
    for file in files:
        if not file.is_file():
            continue
        if file.suffix in (".apk", ".nupkg", ".zip"):
            with zipfile.ZipFile(file) as package:
                for entry in package.infolist():
                    if not entry.is_dir():
                        inspect(entry.filename, package.read(entry))
                        count += 1
        else:
            inspect(str(file), file.read_bytes())
            count += 1
    if count == 0:
        raise AssertionError(f"Empty artifact: {path}")
    print(f"PASS: {count} ordinary host artifact entries exclude target-native/test-only code")
    return count


if __name__ == "__main__":
    if not sys.argv[1:]:
        raise SystemExit("Supply at least one published directory, app bundle, APK or package.")
    for argument in sys.argv[1:]:
        verify(argument)
