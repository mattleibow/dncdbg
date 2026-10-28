"""Shared platform-tool lookup for the internal mobile helpers."""
import os
from pathlib import Path
import shutil
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/DncDbg.RemoteTests"))


def android_sdk():
    configured = os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT")
    if not configured:
        raise RuntimeError("Set ANDROID_HOME to the installed Android SDK; no machine-specific fallback is used.")
    return Path(configured).resolve()


def adb_path():
    path = android_sdk() / "platform-tools/adb"
    if not path.is_file():
        raise RuntimeError(f"ADB is unavailable: {path}")
    return str(path)


def tool_environment(platform):
    env = dict(os.environ)
    if platform == "android":
        env["PATH"] = str(Path(adb_path()).parent) + os.pathsep + env["PATH"]
    if not shutil.which("maui", path=env["PATH"]):
        raise RuntimeError("The pinned Microsoft.Maui.Cli must be on PATH.")
    return env
