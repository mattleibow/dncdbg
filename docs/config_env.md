# Configuration environment variables

These settings are read by the managed debugger:

## Debugger logging

| Variable | Description |
| --- | --- |
| `DNCDBG_LOG_DIR` | Debugger-process setting: creates a separate DAP trace and engine log per adapter process in this directory. Explicit CLI log paths take precedence. |

## Console relay

| Variable | Description |
| --- | --- |
| `DNCDBG_CONSOLE` | Set to `remoteConsole` in the debugger environment or the launch configuration's `env` to enable the session-owned loopback console relay. |
| `DNCDBG_REMOTECONSOLE_PORT` | Console-relay TCP port (1 through 65535); defaults to 22534. Used with `DNCDBG_CONSOLE=remoteConsole`. |

Set the port in the debugger process environment or override it in the launch
configuration's `env`. Restart applies its saved or updated launch environment
before opening the relay. A session without an override uses the debugger
process value, not the preceding session's override. Invalid values, including
zero, overflow, whitespace, signs and trailing characters, log an error and use
22534 rather than failing configuration parsing. An occupied port still fails
the launch; a subsequent request can retry after that port becomes available.
The former `DNCDBG_REMOTECONSOLEPORT` spelling is no longer interpreted.

## Exception details

| Variable | Description |
| --- | --- |
| `DNCDBG_INNER_EXCEPTION_WALK_LIMIT` | Maximum inner exceptions included in `exceptionInfo` details (0 through 4294967295); defaults to 32. Zero disables inner traversal but preserves the complete outer exception. |

Set the inner-exception limit in the debugger process environment before starting
the adapter, or override it in the `env` dictionary of a launch or attach
configuration. Restart uses its saved configuration, including any updated
request arguments. A new session without an override resets to the process
environment's value, not the preceding session's override.

The limit counts inner exceptions, not the outer node. The final included node
is fully populated; no empty placeholder is appended at the cutoff. Parsing
matches the native complete unsigned-decimal behavior: only ASCII digits are
accepted. Empty values, whitespace, signs, trailing characters and overflow
fall back to 32 with an engine-log diagnostic. In particular, `2suffix` and
`2 ` now use 32 rather than the former numeric-prefix value of 2.

Target environment variables supplied in a DAP launch configuration's `env`
are forwarded to the target process. Console-relay settings are additionally
interpreted by the debugger before completing the launch response.
The inner-exception limit is also interpreted by the debugger. Attach `env`
configures this debugger setting; it does not change an already running
target's environment.

The former native implementation's timeout, pagination, RootHidden-walk, and
PDB-mmap environment knobs are not interpreted by the managed implementation.
Do not rely on them to configure a managed session.

For the acceptance-test scripts only, `DNCDBG` selects the debugger executable
and `DNCDBG_TARGET_FRAMEWORK` overrides the fixture target framework (default
`net10.0`). Neither changes the debugger's own .NET 10 build target.
