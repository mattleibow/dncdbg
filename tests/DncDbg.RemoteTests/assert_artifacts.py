"""Reject the experimental target-native implementation in ordinary desktop/Release output."""
from pathlib import Path
import sys
import zipfile

MARKERS = (b"dncdbg_prepare", b"dncdbg_complete", b"dncdbg_test_stall_owner", b"libdncdbg_target",
           b"dotnet/testCloseTunnel", "dotnet/testCloseTunnel".encode("utf-16-le"))


def inspect(name, data):
    lowered = name.lower()
    if any(part in lowered for part in ("dncdbg_target", "bootstrap.cpp", "nativecontracts", "devflow.agent")):
        raise AssertionError(f"Experimental native/Debug-only asset in ordinary artifacts: {name}")
    if any(marker in data for marker in MARKERS):
        raise AssertionError(f"Experimental native code/import in ordinary artifacts: {name}")


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
    print(f"PASS: {count} ordinary artifact entries exclude experimental native/Debug-only code")
    return count


if __name__ == "__main__":
    if not sys.argv[1:]:
        raise SystemExit("Supply at least one published directory, app bundle, APK or package.")
    for argument in sys.argv[1:]:
        verify(argument)
