#!/usr/bin/env python3
"""Build the single-project MAUI mixed-mode sample for Catalyst and AppKit."""

from pathlib import Path
import platform
import subprocess


ROOT = Path(__file__).resolve().parents[2]
PROJECT = Path(__file__).resolve().parent / "MauiSample" / "DncDbg.MixedMode.Maui.csproj"


def build(framework, rid=None, architecture=None):
    command = [
        "dotnet", "build", str(PROJECT),
        "-f", framework,
        "-p:ValidateXcodeVersion=false",
        "--nologo",
    ]
    if rid:
        command.append("-p:RuntimeIdentifier=" + rid)
    if architecture:
        command.append("-p:MixedModeNativeArch=" + architecture)
    subprocess.run(command, cwd=ROOT, check=True)


if __name__ == "__main__":
    architecture = "arm64" if platform.machine() == "arm64" else "x64"
    build("net10.0-maccatalyst", "maccatalyst-" + architecture)
    build("net10.0-macos", architecture=architecture)
    print("PASS single-project MAUI sample builds for Mac Catalyst and macOS AppKit", flush=True)
