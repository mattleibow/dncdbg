# DNCDbg — a C# debugger for .NET

**DNCDbg** implements the [Debug Adapter Protocol](https://microsoft.github.io/debug-adapter-protocol/)
for .NET applications. Use it from VS Code or another DAP client.

The implementation is **C# 14 on .NET 10**, with nullable reference types and
warnings as errors. Its three core SDK-style projects are:

- `src/DncDbg.Dap`: `dncdbg-dap` executable host, DAP requests, configuration and process lifecycle.
- `src/DncDbg.Engine`: CLR debugging, symbols, stepping, evaluation and inspection.
- `src/DncDbg.Protocol`: DAP framing and protocol types.

The `dncdbg` command name is reserved for a future human-oriented debugger CLI.
IDEs and other DAP clients launch `dncdbg-dap`.

The engine uses ClrDebug and the public CLR debugging APIs directly. It does not
wrap another debugger executable. Native CLR debugging libraries and the Windows
PDB reader remain runtime dependencies supplied through NuGet and the target
runtime; no C++ compiler, CMake build, or vendored native source is required.
The separate, opt-in **Debug mobile fixture** is the sole exception: its private
target provider uses native startup/ICorDebug code, never shipped in ordinary
desktop adapter artifacts. Normal DNCDbg remains C#.

## Build and publish

Install the stable SDK selected by [`global.json`](global.json), then run:

```sh
dotnet build
dotnet publish src/DncDbg.Dap -c Release -o bin
./bin/dncdbg-dap --buildinfo
```

Publishing produces the `dncdbg-dap` apphost (`dncdbg-dap.exe` on Windows). Keep its
assemblies, native runtime assets, and license files together. This
framework-dependent publish requires the .NET 10 runtime. For a self-contained
deployment, specify a matching RID, for example:

```sh
dotnet publish src/DncDbg.Dap -c Release -r linux-x64 --self-contained true -o bin/linux-x64
```

Debugger and target process architectures must match. See platform-specific notes:

- [Windows](docs/build_windows.md)
- [Linux](docs/build_linux.md)
- [macOS](docs/build_macos.md)

## Validation

The original test projects, Runner, ScriptContext, fixtures and assertions are
the compatibility contract. From the repository root after publishing:

```sh
dotnet run --project tests/DncDbg.SmokeTests -- bin/dncdbg-dap
pwsh ./test-suite/run_tests.ps1
```

The runner discovers `Test*/Test*.csproj` projects automatically and includes
Windows-only projects when running on Windows. The macOS arm64 acceptance matrix
contains 81 tests. Validation on one platform is not proof of parity on Windows,
Linux, or every DAP field. Tests default to `net10.0`; the existing
`DNCDBG_TARGET_FRAMEWORK` override is preserved.
See [local testing](test-suite/README.md) for focused cases and lifecycle regressions.

The VS Code integration test launches an isolated Extension Development Host,
registers `dncdbg-dap` as a debugger, stops in a real workspace source file, and
verifies that VS Code reuses the file editor instead of opening a virtual copy:

```sh
npm ci --prefix tests/DncDbg.VsCodeTests
npm test --prefix tests/DncDbg.VsCodeTests
```

See [VS Code integration testing](tests/DncDbg.VsCodeTests/README.md).

The Docker integration test publishes for the Linux daemon's native architecture
and exercises a real stdio DAP session with adapter and target in one container:

```sh
dotnet run --project tests/DncDbg.DockerTests -- --require-docker
```

See [Docker and WSL debugging](docs/docker_wsl.md) for prerequisites, cleanup,
source mapping and host-client transport configuration. Linux CI requires Docker;
without `--require-docker`, local runs skip when Docker is unavailable.

### Experimental mobile DAP preview

The opt-in [`mobile` launch configuration](docs/mobile-preview.md) adds
adapter-owned deployment, scoped transport, a generated typed debugger model
and a private CoreCLR provider for **exactly pinned .NET 11 RC1 arm64 iOS
simulator/Android emulator** builds. It is not general supported mobile
debugging and makes no physical-device claim. The separately built
[MAUI fixture](tests/DncDbg.MauiTestApp/README.md) remains on a local .NET 11
SDK pin; the debugger and ordinary desktop launch remain on .NET 10.
Use the [single mobile test entrypoint](tests/DncDbg.MobileTests/README.md):

```sh
dotnet run --project tests/DncDbg.MobileTests -c Release -- --platform contracts
# Real iOS/Android runs use the same command with --platform ios/android; see prerequisites.
```

Portable remote/contract projects belong in `tests/` and `DncDbg.slnx`, like
SmokeTests and DockerTests. Mobile orchestration is not an upstream native
parity project and is deliberately outside `test-suite/Test*` auto-discovery.
The separate `Mobile proof` PR workflow requires actual pinned runtime suites;
generic desktop CI and fake contracts do not prove mobile debugging.
Runtime capabilities explicitly exclude func-eval and other unsupported
operations. See the [preview guide](docs/mobile-preview.md) for configuration,
limitations and current merge gates, and the [historical proof](docs/experimental-mobile-debugging.md)
for earlier direct-client evidence. DevFlow UI automation is a separate proof.

## Usage

With no arguments, `dncdbg-dap` serves DAP on stdin/stdout. Options:
`--help`, `--version`, `--buildinfo`, `--interpreter=vscode`,
`--logProtocol=<file>`, `--log=<file>`, and
`--loglevel=<0..3|DEBUG|INFO|WARNING|ERROR>`.

Protocol and engine logs are separate; engine logs append and log levels are
case-insensitive. Unknown levels fall back to INFO with a warning in the engine
log. An unavailable log file does not prevent debugging. `--server[=PORT]`
accepts one TCP connection on localhost; repeated debug sessions can share that
connection. For Unix PAL compatibility, empty target arguments and arguments
containing spaces that end in a backslash are omitted.

The managed debugger includes launch/attach, restart, repeated sessions, noDebug
launches, redirected I/O, source/function/conditional breakpoints and logpoints,
sync/async stepping, expression evaluation and mutation, memory read/write,
embedded PDBs/sources, SourceLink, goto and single-thread execution.
`configurationDone` holds initial startup. Source and variable references retain
their appropriate session/stop lifetimes.

Evaluator binding uses portable-PDB import/alias scopes, hoisted-local lifetimes,
closed generic receiver arguments and captured primary-constructor parameters.
Static-initialization observation uses CLR breakpoints rather than runtime-private
MethodTable layouts. With implicit function evaluation disabled, unobserved
initialization is not forced or assumed complete.

Shutdown cancels and drains admitted startup/evaluation work before disposal.
Debugger input writes are bounded and cancellable outside the engine lock;
detach relinquishes ownership and disarms the Unix launch watchdog. Adapter
events are fenced to their originating engine generation. Detach also removes
user and internal breakpoints, including static-initializer watches, before
releasing the process.

### Getting started

- [VS Code on Windows](docs/usage_vscode_windows.md) - configure local debugging on Windows.
- [VS Code on Linux and macOS](docs/usage_vscode_unix.md) - configure local debugging on Unix-like systems.
- [Docker and WSL](docs/docker_wsl.md) - run the adapter beside the Linux target and transport DAP over stdio.
- [DAP compatibility contract](docs/dap_status.md) - supported protocol messages and fields.

### Debugger configuration

- [Configuration environment variables](docs/config_env.md) - configure the debugger process or a session.
- [Persistent debug server](docs/debug_server.md) - run sequential sessions on one connection.

### Debugging features

- [Pseudo-variables](docs/pseudo_variables.md) - inspect the current exception, process and thread IDs.
- [Evaluation format specifiers](docs/evaluation_format_specifiers.md) - control expression display and evaluation.
- [Exception conditions](docs/exception_conditions.md) - select exception types and prefixes.
- [Hit count breakpoints](docs/hit_count_breakpoints.md) - stop or log after selected counts.
- [Inputting text into the target](docs/inputting_text.md) - send standard input to a launched target.

## History and contributing

DNCDbg originally derived from NetCoreDbg 3.1.3. Its former C++ implementation
and vendored dependencies remain available in Git history, including
[checkpoint f19fe33a](https://github.com/mattleibow/dncdbg/tree/f19fe33a).
The [historical native comparison](docs/dbg_compare.md) is not a performance
measurement of the managed debugger.

The managed foundation is adapted from the MIT-licensed dotnet-debugger project.
See [Third-Party Notices](THIRD-PARTY-NOTICES.md) and the repository [LICENSE](LICENSE).
Contributions are welcome; see [Contributing](CONTRIBUTING.md).
