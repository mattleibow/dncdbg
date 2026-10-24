# Building on macOS

Install the stable .NET 10 SDK selected by [`global.json`](../global.json).
Apple Silicon uses arm64; Intel uses x64. Debugger and target architectures
must match. No C++ compiler, Xcode build, or CMake is required.

From the repository root:

```sh
dotnet build
dotnet publish src/DncDbg.Dap -c Release -o bin
./bin/dncdbg-dap --buildinfo
dotnet run --project tests/DncDbg.SmokeTests -- bin/dncdbg-dap
pwsh ./test-suite/run_tests.ps1
```

The framework-dependent apphost requires .NET 10. For a self-contained Apple
Silicon publish:

```sh
dotnet publish src/DncDbg.Dap -c Release -r osx-arm64 --self-contained true -o bin/osx-arm64
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

## Mixed-mode public-tool baseline

This optional investigation additionally needs Python 3, CMake, and Xcode's public
C compiler and `lldb-dap`. No Python packages, LLDB Python embedding, internal feeds,
or debugger redistribution are involved:

```sh
dotnet publish src/DncDbg.Dap -c Release -o bin
python3 tests/DncDbg.MixedModeTests/run.py --lldb-dap "$(xcrun --find lldb-dap)"
```

The harness builds an isolated Debug native library using CMake and a managed
console fixture using the repository SDK. All generated files and DAP transcripts
are under `build/validation/mixed-mode/`. The normal baseline checks:

- Uninstrumented P/Invoke returns `42` for arguments `19` and `23`.
- LLDB launches the managed program, hits `native.c`'s labelled source line,
  exposes both C frames, threads and arguments, steps the C assignment, reads
  `sum == 42`, continues through the managed return and exits with code zero.
- DNCDbg alone stops at both managed source lines, exposes the managed stack and
  arguments, and reads `result == 42` after P/Invoke.

The dual-controller probe requires **all** of those checks plus a managed source
stop, an attached LLDB native source stop, and return to a managed source stop:

```sh
python3 tests/DncDbg.MixedModeTests/run.py \
  --lldb-dap "$(xcrun --find lldb-dap)" --probe-coordination
python3 tests/DncDbg.MixedModeTests/run.py \
  --lldb-dap "$(xcrun --find lldb-dap)" --probe-coordination --preload
```

Both probes currently **fail**, returning a nonzero exit status. They do not
convert a timeout into a passing test. On timeout, LLDB pauses the target and
records its native stacks before cleanup. `--preload` loads the library before
the first managed stop, ruling out pending-library breakpoint resolution as the
only obstacle. Neither probe changes runtime-private exception settings.

### Recorded macOS evidence (2026-10-24)

Host: `osx-arm64`, macOS 27, SDK `10.0.401`, target runtime `10.0.12`,
Apple Clang `21.0.0`, Xcode LLDB `2103.0.34.103`.

The normal baseline passed all assertions above. In the dual-controller probe,
LLDB attached successfully at the managed pre-call stop. Continuing ICorDebug
while LLDB still held the OS stop did not return within 30 seconds. Reversing
that order allowed managed execution to resume, but did not reach a reported
native breakpoint. Diagnostic pauses observed `dyld`'s `lldb_image_notifier` /
library-load stack, and CoreCLR's `PAL_DispatchException` /
`PAL_DispatchExceptionWrapper` around debugger traps. With the library preloaded
and its source breakpoint resolved, the sampled stack was:

```text
PAL_DispatchException
PAL_DispatchExceptionWrapper
native_sum
native_sum
```

No successful cross-mode continue/step is claimed. See the
[architecture decision and public source references](dap_status.md#mixed-managednative-debugging).
