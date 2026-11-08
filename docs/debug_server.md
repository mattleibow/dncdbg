# Multi-session Persistent Debug Server

DNCDbg supports the multi-session persistent debug server mode. This means you can run several debug sessions one after another without restarting the debugger for each session.

## Example workflow

- Start the debugger.

**First debug session**

- Start the initialization sequence with the [Initialize Request](dap_status.md#initializerequest-initialize).
- ... perform the initialization you need ...
- Finish the initialization sequence with the [ConfigurationDone Request](dap_status.md#configurationdonerequest-configurationdone).
- Start the debug session with the [Launch Request](dap_status.md#launchrequest-launch) or the [Attach Request](dap_status.md#attachrequest-attach).
- ... debug the application ...
- Restart the debug session with the [Restart Request](dap_status.md#restartrequest-restart).
- ... debug the application ...
- Finish the debug session with the [Terminate Request](dap_status.md#terminaterequest-terminate) or the [Detach Request](dap_status.md#detachrequest-detach).
  ***Note: the [Detach Request](dap_status.md#detachrequest-detach) is not part of the DAP specification.***

**Second debug session**

- Start the initialization sequence with the [Initialize Request](dap_status.md#initializerequest-initialize).
- ...
- Finish the debug session with the [Terminate Request](dap_status.md#terminaterequest-terminate) or the [Detach Request](dap_status.md#detachrequest-detach).

**Last debug session**

- Start the initialization sequence with the [Initialize Request](dap_status.md#initializerequest-initialize).
- ...
- Finish the debug session and close the debugger with the [Disconnect Request](dap_status.md#disconnectrequest-disconnect).

**OR**

- Start the initialization sequence with the [Initialize Request](dap_status.md#initializerequest-initialize).
- ...
- Finish the debug session with the [Terminate Request](dap_status.md#terminaterequest-terminate) or the [Detach Request](dap_status.md#detachrequest-detach).
- ...
- Close the debugger with the [Disconnect Request](dap_status.md#disconnectrequest-disconnect).

## Protocol server mode (`--server`)

With no options, `dncdbg-dap` exchanges DAP messages over stdin/stdout.
`--server` instead listens on IPv4 loopback (`127.0.0.1`) and exchanges messages
over one accepted TCP connection:

```sh
dncdbg-dap --server=5000
```

Bare `--server` selects port `4711` unless an earlier `--server=PORT` already
selected a port. The last explicit port wins; `--server=5000 --server` still uses
`5000`. Ports `1` through `65535` are accepted. As a managed extension,
`--server=0` asks the OS for an unused port; the actual endpoint is printed to
stderr when the listener starts. Signs, whitespace, suffixes and out-of-range
ports are rejected.

The listener starts at adapter launch. It accepts exactly one client and then
closes the listening socket, so additional clients cannot start another session.
Sequential debug sessions can still share the accepted connection, as described
above. DAP retains the same `Content-Length` headers and UTF-8 JSON payloads as
stdio; stdout is not used for protocol messages. See [DAP status](dap_status.md)
for supported requests.

A DAP `disconnect` request or client EOF shuts down the adapter and cleans up an
owned target. Invalid or occupied ports report an error and exit unsuccessfully,
without starting a target. Protocol and engine logging work over TCP too.
See [launch options](launch_options.md) for logging, version and build information.
