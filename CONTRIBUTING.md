# Contributing to DNCDbg

Thank you for your interest in contributing to DNCDbg! Filing issues for problems you encounter is a great way to help, and code contributions are always welcome.

## Reporting Issues

Bug reports, feature proposals, and general feedback are all welcome. Please feel free to open an issue to start a discussion.

## How to Contribute Code Changes

Use the stable .NET 10 SDK selected by `global.json`; the implementation uses
C# 14, nullable reference types, and warnings as errors. No C++ toolchain or
CMake build is needed.

```sh
dotnet build
dotnet run --project tests/DncDbg.SmokeTests -- src/DncDbg.Adapter/bin/Debug/net10.0/dncdbg
```

Run the relevant unchanged acceptance tests using the commands in
[test-suite/README.md](test-suite/README.md). Preserve existing Runner,
ScriptContext, fixture and assertion behavior. Keep CLR interop in the engine,
using ClrDebug/public debugging APIs rather than runtime-private object layouts.

### Submitting changes

Direct Pull Requests are disabled for this repository. To propose changes, please follow these steps:

1. **Fork the repository** to your own GitHub account.
2. **Make your changes** on a dedicated branch in your fork.
3. **Open an issue** in this repository describing the changes you have made, and include a link to your fork or the specific commits.

### Review Process

If your proposed changes are **valuable and aligned with the project's goals**, they will be manually cherry-picked into the upstream repository. Full credit for your authorship will be preserved in the commit history.
