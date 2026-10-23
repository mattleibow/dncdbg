# Debugger for the .NET Core Runtime

**DNCDbg** is an acronym for .NET Core (dot net core) Debugger. It implements the [Debug Adapter Protocol](https://microsoft.github.io/debug-adapter-protocol/), allowing you to debug .NET applications running on all versions of the .NET runtime.

Initially, DNCDbg was based on version 3.1.3 of the [NetCoreDbg](https://github.com/Samsung/netcoredbg) source code, but unlike NetCoreDbg, its development is fully hosted on GitHub, making it more collaborative and flexible.

**Project goals:** implement functionality close to that of the MSVS C# debugger (excluding features related to the MSVS IDE and those not supported by DAP), provide complete [Debug Adapter Protocol](https://microsoft.github.io/debug-adapter-protocol/) support, and achieve low memory consumption and high performance.

See the current [Debug Adapter Protocol support status](docs/dap_status.md).

## C# migration

The managed debugger is built with **.NET 10 and C# 14**. The SDK is pinned in
`global.json`; no preview SDK, C++ compiler, or CMake is needed for this build.
It consists of the DAP host (`src/DncDbg.Adapter`), CLR debugging engine
(`src/DncDbg.Engine`), and protocol transport/types (`src/DncDbg.Protocol`).
The managed foundation is adapted from the MIT-licensed dotnet-debugger project;
see [Third-Party Notices](THIRD-PARTY-NOTICES.md).

```sh
dotnet build
dotnet publish src/DncDbg.Adapter -c Release -o bin
./bin/dncdbg --buildinfo
cd test-suite
DNCDBG=../bin/dncdbg bash run_tests.sh
```

Publishing produces the `dncdbg` apphost (`dncdbg.exe` on Windows). Keep its
managed assemblies and native runtime assets together. This framework-dependent
publish requires the .NET 10 runtime; add `-r <rid> --self-contained true` to
include a runtime. The native CLR debugging libraries are supplied through NuGet,
not a separate debugger executable.

Migration is in progress. The original test suite and native sources remain as
the compatibility contract/reference until replacement behavior is validated.
The comparison table below describes the original native implementation, not
measurements or a parity guarantee for the managed debugger.

<div align="center">

<table>
  <thead>
    <tr>
      <th align="left"></th>
      <th align="center">DNCDbg</th>
      <th align="center"><a href="https://github.com/Samsung/netcoredbg">NetCoreDbg</a></th>
      <th align="center"><a href="https://github.com/dotnet/vscode-csharp">VsDbg</a></th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td align="left"><b>License</b></td>
      <td align="center">MIT</td>
      <td align="center">MIT</td>
      <td align="center">Commercial<sup><a href="docs/dbg_compare.md#note-1">1</a></sup></td>
    </tr>
    <tr>
      <td align="left"><b>Implementation</b></td>
      <td align="center">C++</td>
      <td align="center">C++ and C#<sup><a href="docs/dbg_compare.md#note-2">2</a></sup></td>
      <td align="center">C#</td>
    </tr>
    <tr>
      <td align="left"><b>Physical memory footprint<sup><a href="docs/dbg_compare.md#note-3">3</a></sup></b></td>
      <td align="center"><b>11.7M</b></td>
      <td align="center">52.4M</td>
      <td align="center">146.8M</td>
    </tr>
    <tr>
      <td align="left"><b>Cross-platform</b></td>
      <td align="center">✅</td>
      <td align="center">⚠️<sup><a href="docs/dbg_compare.md#note-4">4</a></sup></td>
      <td align="center">⚠️<sup><a href="docs/dbg_compare.md#note-5">5</a></sup></td>
    </tr>
    <tr>
      <td align="left"><b>Human-readable async stack traces</b></td>
      <td align="center">✅</td>
      <td align="center">❌</td>
      <td align="center">✅</td>
    </tr>
    <tr>
      <td align="left"><b>Use <code>ToString()</code> for object variable display</b></td>
      <td align="center">✅</td>
      <td align="center">❌</td>
      <td align="center">✅</td>
    </tr>
    <tr>
      <td align="left"><b><code>DebuggerBrowsable</code> attribute</b></td>
      <td align="center">✅</td>
      <td align="center">⚠️<sup><a href="docs/dbg_compare.md#note-6">6</a></sup></td>
      <td align="center">✅</td>
    </tr>
    <tr>
      <td align="left"><b><code>DebuggerDisplay</code> attribute</b></td>
      <td align="center">✅</td>
      <td align="center">❌</td>
      <td align="center">✅</td>
    </tr>
    <tr>
      <td align="left"><b><code>DebuggerTypeProxy</code> attribute</b></td>
      <td align="center">✅</td>
      <td align="center">❌</td>
      <td align="center">✅</td>
    </tr>
    <tr>
      <td align="left"><a href="docs/evaluation_format_specifiers.md"><b>Evaluation format specifiers</b></a></td>
      <td align="center">✅</td>
      <td align="center">❌</td>
      <td align="center">✅</td>
    </tr>
    <tr>
      <td align="left"><b>Logpoints</b></td>
      <td align="center">✅</td>
      <td align="center">❌</td>
      <td align="center">✅</td>
    </tr>
    <tr>
      <td align="left"><b>Embedded PDB</b></td>
      <td align="center">✅</td>
      <td align="center">❌</td>
      <td align="center">✅</td>
    </tr>
    <tr>
      <td align="left"><b>Embedded sources</b></td>
      <td align="center">✅</td>
      <td align="center">❌</td>
      <td align="center">✅</td>
    </tr>
    <tr>
      <td align="left"><a href="https://github.com/dotnet/sourcelink/blob/main/README.md"><b>Source Link</b></a></td>
      <td align="center">✅</td>
      <td align="center">❌</td>
      <td align="center">✅</td>
    </tr>
    <tr>
      <td align="left"><a href="https://code.visualstudio.com/docs/csharp/debugger-settings#_source-file-map"><b>Source File Map</b></a></td>
      <td align="center">✅</td>
      <td align="center">❌</td>
      <td align="center">✅</td>
    </tr>
    <tr>
      <td align="left"><a href="docs/inputting_text.md"><b>Standard input redirection (STDIN)</b></a></td>
      <td align="center">✅</td>
      <td align="center">❌</td>
      <td align="center">✅</td>
    </tr>
    <tr>
      <td align="left"><b>Jump to Cursor (Set Next Statement)</b></td>
      <td align="center">✅</td>
      <td align="center">❌</td>
      <td align="center">✅</td>
    </tr>
    <tr>
      <td align="left"><b>Single-thread execution and stepping</b></td>
      <td align="center">✅</td>
      <td align="center">❌</td>
      <td align="center">❓</td>
    </tr>
    <tr>
      <td align="left"><b>MI/GDB and CLI protocols</b></td>
      <td align="center">❌</td>
      <td align="center">✅</td>
      <td align="center">❌</td>
    </tr>
    <tr>
      <td align="left"><b>Mixed-mode (interop) debugging</b></td>
      <td align="center">❌</td>
      <td align="center">⚠️<sup><a href="docs/dbg_compare.md#note-7">7</a></sup></td>
      <td align="center">❓</td>
    </tr>
    <tr>
      <td align="left"><b>Hot Reload</b></td>
      <td align="center">❌</td>
      <td align="center">⚠️<sup><a href="docs/dbg_compare.md#note-8">8</a></sup></td>
      <td align="center">❓</td>
    </tr>
    <tr>
      <td align="center" colspan="4">
        <a href="docs/dbg_compare.md">View Full Comparison Table</a>
      </td>
    </tr>
  </tbody>
</table>

</div>

## Development Process

The project uses Trunk-Based Development, which means you can build the current upstream code and be sure you have the latest version with all features and fixes included. Upcoming changes can be found in [CHANGELOG.md](CHANGELOG.md).

## Contributing

Contributions are welcome! Please read our [Contributing Guidelines](CONTRIBUTING.md) to learn how you can report bugs, propose features, or contribute code changes through forks.

## Building from Source Code

Use `dotnet build` for the managed solution. Platform-specific notes below also
retain the legacy native build instructions for behavior comparisons.

- [Windows OS build.](docs/build_windows.md)
- [Linux OS build.](docs/build_linux.md)
- [macOS build.](docs/build_macos.md)
- [Local testing.](test-suite/README.md)

## Usage

With no arguments, `dncdbg` serves DAP on stdin/stdout. Compatibility options:
`--help`, `--version`, `--buildinfo`, `--interpreter=vscode`,
`--logProtocol=<file>`, `--log=<file>`, and `--loglevel=<0..3|DEBUG|INFO|WARNING|ERROR>`.
Protocol and engine logs are separate; engine logs append and log levels are
case-insensitive. Unknown levels fall back to INFO with a warning in the engine
log. `--server[=PORT]` additionally accepts one TCP connection on localhost.
As in the original adapter, an unavailable log file does not prevent debugging.

- [VS Code IDE, Windows OS.](docs/usage_vscode_windows.md)
- [VS Code IDE, Linux and macOS.](docs/usage_vscode_unix.md)
- [Debugger configuration environment variables.](docs/config_env.md)
- [Debugger pseudo-variables.](docs/pseudo_variables.md)
- [Inputting text into the target process.](docs/inputting_text.md)
- [Evaluation format specifiers.](docs/evaluation_format_specifiers.md)
- [Multi-session persistent debug server.](docs/debug_server.md)
