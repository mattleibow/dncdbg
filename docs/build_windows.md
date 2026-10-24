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

## Experimental mixed-mode launch

The first native-debugging slice is opt-in, **Windows x64 / .NET 10 / internal
console / launch only**. It uses ICorDebug for both managed and native execution
control, and the operating system's DbgHelp for native PDB/source mapping and
stack unwinding. There is no second attached debugger and no new production
NuGet dependency. Existing managed-only launches are unchanged.

Send the normal DAP initialize/launch/configuration sequence, adding:

```json
{ "program": "C:/path/MyApp.dll", "nativeDebugging": true }
```

First stop at a managed source breakpoint after the native DLL has loaded, but
before calling the native function. Then send the DNCDbg extension:

```json
{
  "command": "dotnet/setNativeBreakpoint",
  "arguments": {
    "module": "mylibrary.dll",
    "source": { "path": "C:/path/native.c" },
    "line": 11,
    "oneShot": true
  }
}
```

`module` is an unambiguous loaded DLL name or full path. Its matching native PDB
must be available. The source line must resolve exactly; failure is an error,
not an unverified success. The response contains `id`, `verified`, `source`,
`line`, and `oneShot:true`. Standard DAP `setBreakpoints` remains managed-only.
`nativeDebugging` defaults to false.

Continue normally. Hitting the native breakpoint emits the standard `stopped`
event (`reason:"breakpoint"`, `description:"Native breakpoint"`, the OS thread ID,
and the returned breakpoint ID). Standard `threads` enumerates OS threads at this
stop; `stackTrace` on the stopped thread returns `[Native]` frames with native
source locations, stopping at an unresolved native/JIT boundary. It does **not**
invent a unified managed/native stack. Continue again to reach the next managed
breakpoint, where ordinary managed arguments, locals and expressions are
available.

The native breakpoint is consumed on its first hit: DNCDbg restores the opcode
and the x64 instruction pointer before resuming. This avoids requiring an
unproven persistent-breakpoint rearm/stepping implementation. Set another
explicit one-shot breakpoint at a subsequent managed stop when needed.

### Deliberate limitations

- Native locals, C/C++ expression evaluation, mutation and merged C#/native
  frames are not implemented. Only the stopped native thread's stack is exposed.
- Native or transition stepping is not exposed. All stepping in this opt-in
  session is rejected; use continue and source breakpoints. Single-thread
  continuation is also rejected. A capabilities event disables single-thread
  continuation and restart after the opt-in launch.
- Function evaluation, including explicit calls, constructors and implicit
  debugger-display/property execution, is disabled throughout an opted-in
  session. Managed field/primitive inspection still works.
- Native breakpoint installation requires a managed stop and an **already
  loaded** DLL. There is no automatic binding/rebinding for late library loads,
  unloads, source remapping, or symbol-server setup. Native PDB source lookup
  currently uses DbgHelp's ANSI entry points; Unicode-path parity is unproven.
- `noDebug`, .NET Framework, external/integrated terminals, attach, restart and
  detach are rejected for this slice. Native launch shutdown terminates the
  target; `disconnect` with `terminateDebuggee:false` fails without ending the
  session. Use `terminate` or `disconnect` with `terminateDebuggee:true`.
- Windows arm64/x86, Linux and macOS mixed-mode sessions are not supported by
  this implementation. Managed-only support is unaffected.

The independent raw API baseline also proves **attach** and a **native
instruction step** on Windows x64. Neither automatically establishes an adapter
attach/detach lifecycle or source/transition stepper, so those are not exposed
in production.

### Reproduce the fixture and public API proof

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

dotnet publish src/DncDbg.Dap -c Release -o bin
python tests/DncDbg.MixedModeTests/run.py --windows-engine --adapter bin/dncdbg-dap.exe
```

The raw proof directly consumes the public MIT ClrDebug/DbgShim APIs, portable
managed PDBs, documented native debug callbacks, and Windows DbgHelp. It does not
use `DncDbg.Engine`, so it is independent of the implementation being validated.
In-band native events are processed off the Win32 event thread; out-of-band
events are immediately continued without touching managed state. The attach
fixture waits on an explicit release file, not a race against a sleep.

The DNCDbg DAP harness verifies managed -> native -> managed continuation, exact
source/stack/breakpoint-ID shapes, result `42`, one-shot behavior on a second
native call, rejected unsupported operations, and normal/terminate/disconnect
cleanup. It checks that the target has actually exited, not just that a response
was sent.

The branch-scoped
[public proof workflow](../.github/workflows/mixed-mode-proof.yml) preserves full
logs, DAP transcripts, fixture DLL/PDBs and compiler outputs as an Actions
artifact (14-day retention). It also runs the unchanged managed smoke tests and
focused breakpoint, stack, thread, stepping, attach and disconnect acceptance
tests. No PR is required to run this proof.
