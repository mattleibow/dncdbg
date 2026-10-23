# Building on Windows

Install the stable .NET 10 SDK selected by [`global.json`](../global.json).
Visual Studio's C++ toolchain and CMake are not required. Use a debugger whose
architecture matches the target process.

From the repository root in PowerShell:

```powershell
dotnet build
dotnet publish src/DncDbg.Adapter -c Release -o bin
.\bin\dncdbg.exe --buildinfo
dotnet run --project tests/DncDbg.SmokeTests -- bin/dncdbg.exe
$env:DNCDBG = (Resolve-Path .\bin\dncdbg.exe).Path
cd test-suite
.\run_tests.ps1
```

The framework-dependent apphost requires .NET 10. For a self-contained x64
publish:

```powershell
dotnet publish src/DncDbg.Adapter -c Release -r win-x64 --self-contained true -o bin/win-x64
```

Use `win-arm64` for ARM64. Keep all published assemblies, native runtime assets
and licenses together. NuGet supplies the CLR debugging libraries and native
Windows PDB reader. Attaching to another user's or elevated process is subject
to Windows process permissions.

The test suite defaults to `net10.0`; `$env:DNCDBG_TARGET_FRAMEWORK` overrides
the fixture target when its SDK/runtime is installed.
See [local testing](../test-suite/README.md).
