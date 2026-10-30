# Building on Windows

Install the stable .NET 10 SDK selected by [`global.json`](../global.json).
Visual Studio's C++ toolchain and CMake are not required. Use a debugger whose
architecture matches the target process.

From the repository root in PowerShell:

```powershell
dotnet build
dotnet publish src/DncDbg.Dap -c Release -o bin
.\bin\dncdbg-dap.exe --buildinfo
dotnet run --project tests/DncDbg.SmokeTests -- bin/dncdbg-dap.exe
pwsh .\test-suite\run_tests.ps1
```

The framework-dependent apphost requires .NET 10. For a self-contained x64
publish:

```powershell
dotnet publish src/DncDbg.Dap -c Release -r win-x64 --self-contained true -o bin/win-x64
```

Use `win-arm64` for ARM64. Keep all published assemblies, native runtime assets
and licenses together. NuGet supplies the CLR debugging libraries and native
Windows PDB reader. Attaching to another user's or elevated process is subject
to Windows process permissions.

The test suite defaults to `net10.0`; `$env:DNCDBG_TARGET_FRAMEWORK` overrides
the fixture target when its SDK/runtime is installed.
See [local testing](../test-suite/README.md).

## Native debugger backends

Use `--native-debugger=lldb` to find an installed public LLVM DAP adapter, or
`--native-debugger=dap --native-debugger-path=...` to select another public DAP
backend. The native debugger owns execution, native PDBs/locals/stepping and
module lifetimes. An optional read-only CoreCLR view is not managed execution
control. See [native debugger architecture and configuration](native_debuggers.md).

```powershell
.\bin\dncdbg-dap.exe --native-debugger=lldb --native-debugger-info
python tests/DncDbg.MixedModeTests/native_proxy.py `
  --adapter bin/dncdbg-dap.exe --lldb-dap "C:/Program Files/LLVM/bin/lldb-dap.exe"
```

## Retired direct-controller research

The previous `nativeDebugging:true` / `dotnet/setNativeBreakpoint` production
experiment is removed and rejects requests explicitly. It is not a supported
Windows debugger. Its counted-stop, queued-hit and unload/reuse risks are not
carried into a custom replacement; native execution is delegated to an existing
engine instead. The raw public API baseline below is retained as research
evidence, not production acceptance.

### Reproduce the raw fixture and public API proof

For these optional tests, additionally install CMake, Python 3, and the public
MSVC C++ Build Tools (available on `windows-2025` GitHub-hosted runners). All build
outputs stay under `build/validation/mixed-mode`:

```powershell
dotnet build tests/DncDbg.MixedModeTests/fixture/Fixture.csproj `
  -o build/validation/mixed-mode/fixture
cmake -S tests/DncDbg.MixedModeTests/fixture `
  -B build/validation/mixed-mode/native -A x64
cmake --build build/validation/mixed-mode/native --config Debug
Copy-Item build/validation/mixed-mode/native/Debug/mixedfixture.* `
  build/validation/mixed-mode/fixture/

dotnet run --project tests/DncDbg.MixedModeTests/WindowsProof -- `
  build/validation/mixed-mode/fixture/Fixture.dll tests/DncDbg.MixedModeTests/fixture
dotnet run --project tests/DncDbg.MixedModeTests/WindowsProof -- `
  build/validation/mixed-mode/fixture/Fixture.dll tests/DncDbg.MixedModeTests/fixture --attach
dotnet run --project tests/DncDbg.MixedModeTests/WindowsProof -- `
  build/validation/mixed-mode/fixture/Fixture.dll tests/DncDbg.MixedModeTests/fixture --terminate-native

```

The raw proof directly consumes the public MIT ClrDebug/DbgShim APIs, portable
managed PDBs, documented native debug callbacks, and Windows DbgHelp. It does not
use `DncDbg.Engine`, so it is independent of the implementation being validated.
In-band native events are processed off the Win32 event thread; out-of-band
events are immediately continued without touching managed state. The attach
fixture waits on an explicit release file, not a race against a sleep.

The [native backend workflow](../.github/workflows/mixed-mode-proof.yml) exercises
the active proxy and unchanged managed regressions. Historical run links and
the old direct-controller proof boundary remain in [DAP status](dap_status.md#mixed-managednative-debugging).
