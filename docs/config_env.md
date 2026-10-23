# Configuration environment variables

These settings are read by the managed debugger:

| Variable | Description |
| --- | --- |
| `DNCDBG_LOG_DIR` | Debugger-process setting: creates a separate DAP trace and engine log per adapter process in this directory. Explicit CLI log paths take precedence. |
| `DNCDBG_CONSOLE` | Set to `remoteConsole` in the debugger environment or the launch configuration's `env` to enable the session-owned loopback console relay. |
| `DNCDBG_REMOTECONSOLEPORT` | Console-relay TCP port; defaults to 4711. Used with `DNCDBG_CONSOLE=remoteConsole`. |

Target environment variables supplied in a DAP launch configuration's `env`
are forwarded to the target process. Console-relay settings are additionally
interpreted by the debugger before completing the launch response.

The former native implementation's timeout, pagination, RootHidden-walk, and
PDB-mmap environment knobs are not interpreted by the managed implementation.
Do not rely on them to configure a managed session.

For the acceptance-test scripts only, `DNCDBG` selects the debugger executable
and `DNCDBG_TARGET_FRAMEWORK` overrides the fixture target framework (default
`net10.0`). Neither changes the debugger's own .NET 10 build target.
