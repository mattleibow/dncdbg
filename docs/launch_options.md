# Launch options

`dncdbg-dap` accepts options for DAP transport, logging and build information:

```sh
dncdbg-dap [options]
```

Options are processed left to right. Unknown options report an error to stderr
and exit unsuccessfully. Informational options print to stdout and exit
immediately; options after them are not processed.

| Option | Behavior |
| --- | --- |
| `--help`, `-h` | Print usage and exit. |
| `--version` | Print the current version and exit. |
| `--buildinfo` | Print managed build information and exit. |
| `--server[=PORT]` | Serve one DAP connection on `127.0.0.1` instead of stdin/stdout. |
| `--log=<file>` | Append engine diagnostics to a file. |
| `--logProtocol=<file>` | Write incoming and outgoing protocol messages to a separate file, replacing its previous contents. |
| `--loglevel=<number or name>` | Set the minimum engine logging level. |
| `--interpreter=vscode` | Accept the interpreter option supplied by VS Code; the adapter always speaks DAP. |

## Protocol transport

Without `--server`, protocol messages use stdin/stdout. Bare `--server` selects
port `4711` if no explicit port has been selected. The last `--server=PORT`
wins, and a later bare `--server` preserves it:

```sh
dncdbg-dap --server=5000 --server
```

Ports `1` through `65535` are valid. The managed extension `--server=0` requests
an unused OS-selected port, reported on stderr. All ports use loopback only.
The adapter accepts one client, not a succession of separate TCP connections.
See [protocol server mode](debug_server.md#protocol-server-mode---server) for
framing, sequential debug sessions, shutdown and failure behavior.

## Logging

```sh
dncdbg-dap --server=5000 --log=/tmp/engine.log --logProtocol=/tmp/protocol.log --loglevel=WARNING
```

| Value | Level |
| --- | --- |
| `0`, `DEBUG` | Debug |
| `1`, `INFO` | Info |
| `2`, `WARNING` | Warning |
| `3`, `ERROR` | Error |

Names are case-insensitive; the default is `INFO`. An unknown level falls back
to `INFO` with a warning in the engine log. `DEBUG` is accepted in all builds,
but debug-level engine messages are emitted only in Debug builds. The engine
threshold does not filter the separate protocol log.

An empty log path disables that log. An unavailable log file does not prevent
debugging. Protocol logging applies to both stdio and TCP. For environment-based
tracing, see [configuration environment variables](config_env.md).

## Version, build information and help

`--version` currently prints `DNCDbg version 1.3.0`. `--buildinfo` also reports
the managed build configuration and date, target OS/architecture, `.NET 10 / C# 14`,
VCS revision and license notices. These are managed assembly metadata, not native
C++ compiler flags or vendored library versions.

`--help` begins with `Usage: dncdbg-dap [options]` and lists the options above.
It describes `--server[=PORT]` as serving one DAP connection on localhost with
default port `4711`.

`--interpreter=vscode` does not disable supported managed capabilities:
`supportsRestartRequest` remains advertised. Other interpreter names are rejected.
See [DAP status](dap_status.md) for the capability contract.
