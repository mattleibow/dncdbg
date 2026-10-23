# Building on macOS

Install the stable .NET 10 SDK selected by [`global.json`](../global.json).
Apple Silicon uses arm64; Intel uses x64. Debugger and target architectures
must match. No C++ compiler, Xcode build, or CMake is required.

From the repository root:

```sh
dotnet build
dotnet publish src/DncDbg.Adapter -c Release -o bin
./bin/dncdbg --buildinfo
dotnet run --project tests/DncDbg.SmokeTests -- bin/dncdbg
pwsh ./test-suite/run_tests.ps1
```

The framework-dependent apphost requires .NET 10. For a self-contained Apple
Silicon publish:

```sh
dotnet publish src/DncDbg.Adapter -c Release -r osx-arm64 --self-contained true -o bin/osx-arm64
```

Use `osx-x64` for Intel. Keep all published assemblies, native runtime assets
and licenses together. CLR debugging libraries come from NuGet.

## Debuggee signing

macOS restricts debugging executables without the
`com.apple.security.get-task-allow` entitlement. When possible, the debugger
launches an application's DLL with the signed `dotnet` host instead of its
apphost. Self-contained/single-file targets may need to be signed for debugging
with `/usr/bin/codesign`; the launch diagnostic describes the required
entitlement. Attaching is also subject to macOS debugging permissions.

The test suite defaults to `net10.0` and preserves `DNCDBG_TARGET_FRAMEWORK`.
See [local testing](../test-suite/README.md).
