# DNCDbg — a C# debugger for .NET

**DNCDbg** implements the [Debug Adapter Protocol](https://microsoft.github.io/debug-adapter-protocol/)
for .NET applications. Use it from VS Code or another DAP client.

The implementation is **C# 14 on .NET 10**, with nullable reference types and
warnings as errors. It consists of three SDK-style projects:

- `src/DncDbg.Dap`: `dncdbg-dap` executable host, DAP requests, configuration and process lifecycle.
- `src/DncDbg.Engine`: CLR debugging, symbols, stepping, evaluation and inspection.
- `src/DncDbg.Protocol`: DAP framing and protocol types.

The `dncdbg` command name is reserved for a future human-oriented debugger CLI.
IDEs and other DAP clients launch `dncdbg-dap`.

The engine uses ClrDebug and the public CLR debugging APIs directly. It does not
wrap another debugger executable. Native CLR debugging libraries and the Windows
PDB reader remain runtime dependencies supplied through NuGet and the target
runtime; no C++ compiler, CMake build, or vendored native source is required.

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
contains 77 tests. Validation on one platform is not proof of parity on Windows,
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

- [DAP compatibility contract](docs/dap_status.md)
- [VS Code on Windows](docs/usage_vscode_windows.md)
- [VS Code on Linux and macOS](docs/usage_vscode_unix.md)
- [Configuration environment variables](docs/config_env.md)
- [Pseudo-variables](docs/pseudo_variables.md)
- [Inputting text into the target](docs/inputting_text.md)
- [Evaluation format specifiers](docs/evaluation_format_specifiers.md)
- [Persistent debug server](docs/debug_server.md)
- [Exception conditions](docs/exception_conditions.md)

## History and contributing

DNCDbg originally derived from NetCoreDbg 3.1.3. Its former C++ implementation
and vendored dependencies remain available in Git history, including
[checkpoint f19fe33a](https://github.com/mattleibow/dncdbg/tree/f19fe33a).
The [historical native comparison](docs/dbg_compare.md) is not a performance
measurement of the managed debugger.

The managed foundation is adapted from the MIT-licensed dotnet-debugger project.
See [Third-Party Notices](THIRD-PARTY-NOTICES.md) and the repository [LICENSE](LICENSE).
Contributions are welcome; see [Contributing](CONTRIBUTING.md).
