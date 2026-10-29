# Remote debugging architecture

Remote debugging has one execution controller and no platform-specific DAP engine:

```text
dncdbg-dap (.NET 10, stdio DAP)
  RemoteSession                 DAP handles, symbols, generated typed operations
    RemoteLifecycle             acquisition, admission, cancellation, restart, exit
      IRemoteTargetProvider     deployment, process, manifest, observation, cleanup
      ITunnelProvider           owned loopback route and transport trust
    RemoteDebugClient           authenticated bounded model/protocol
       -> target-native agent   Bootstrap.cpp / generated RemoteModel.h
          native owner thread   exact DBI/DAC, ICorDebug, callback-stop leases
          CoreCLR application
```

`DncDbg.Remote` contains neutral manifests, commands, SSH, transport and ownership
contracts. It has no dependency on Android, Apple, MAUI, ClrDebug or the local
engine. `DncDbg.Remote.Android` and `DncDbg.Remote.Apple` are separate thin
host-side packages. `RemoteSession` and `RemoteLifecycle` contain no mobile paths,
ADB, installation or simulator logic. The ordinary desktop engine is not attached
as a second execution controller.

## Composition

| Target | Responsibility | Route |
|---|---|---|
| Local native host | Start public nethost/hostfxr product host and observe its actual process | Direct loopback or owned TCP proxy |
| Structured commands | Optional deploy/start, manifest read, process observation, stop and cleanup | Structured stdio command or managed loopback endpoint |
| SSH | Fixed target controller, SFTP copy, exact identity and host key | Built-in encrypted `ssh -W` |
| Android | SDK install/start/app-data ownership only | Transactional ADB forwarding |
| Apple simulator | simctl install/start/app-data ownership only | Simulator loopback |
| Physical device/vendor | Structured vendor controller and provisioned runtime/agent | USB helper or authenticated encrypted managed/command tunnel |

Target and tunnel providers never implement stepping, inspection or evaluation.
They expose a trusted `TargetProfile` and a version-1 `TargetManifest`. The shared
lifecycle validates OS, architecture, exact runtime/DBI/DAC build IDs and code
hashes, agent RID/build/content hash, protocol/model, capabilities, payload/PID,
generation and transport requirements before authentication. Admission of unknown
private ABI images is impossible through a self-declared vendor manifest.

The separate `DncDbg.TargetHost` executable loads the agent before initializing
the runtime. It supports a normal managed application's `Main` through public
`hostfxr_initialize_for_dotnet_command_line` / `hostfxr_run_app`, as well as an
explicitly named unmanaged-callable managed component. Payload execution waits
for debugger configuration. The tests execute both paths; component fixtures
provide additional long-lived commands and fault coverage.

## Ownership and cancellation

Every launch creates a new 32-hex generation and random credential. Target,
tunnel and DAP handles belong to that generation. Restart/reconnect acquires new
providers/routes/credentials; old stack/value/module handles are invalidated and
never reused. Source breakpoint request IDs remain stable.

Acquisition records action intent before invoking a structured command. The
owner-only journal retains executable/argv, environment **names**, settlement and
owned command PIDs, never environment values, stdin or bearer tokens. Command
arguments are not a place for secrets.

Terminate cancels the control/lifetime path **before** waiting for the state lock
used by synchronous inspection. Acquisition settles before cleanup snapshots are
taken. The client, route, target stop and deployment cleanup settle through
idempotent gates with independent bounded cleanup budgets. A failure is an error,
not a successful detach or hidden emergency cleanup.

Close intent is monotonic. A natural-exit settlement or explicit detach releases
the debugger/route once but does not cache away ownership of later termination.
Terminate cancels concurrent exit observation, serializes with any in-flight
provider detach, and upgrades to owned stop/deployment cleanup exactly once.
Observer failure/cancellation, early exit notification and conflicting actual
exit codes force that stronger cleanup with an independent budget. Already
settled deployment cleanup is not repeated and a completed transport is never
disposed twice.

`dncdbg-target-control` owns a detached device worker and the actual `Process`
object, not a guessed guest PID. It validates the deployed inventory, records the
process before acknowledging start, observes its real exit code, and kills/reaps
that exact child if publication or diagnostic capture fails. After the application
exits it erases credentials and its bundle, including after adapter detach. It
retains nonsecret ownership/exit/log/result records for recovery. An explicit
stop request is generation-bound. Recovery never signals a recycled PID.

An owned command provider must define `stop` and `observe`; deployment must define
`cleanup`. A custom vendor controller must preserve these semantics, including
autonomous cleanup when a detached application eventually exits. Merely launching
a background shell process does not satisfy the ownership contract.

## Completion is not transport failure

Attached natural completion is distinct from EOF, detach and terminate. The
native embedding host publishes `dncdbg_complete(exitCode)` while its native owner
is alive. The owner publishes the typed exit event and settles DBI cleanup.
The lifecycle disposes the route and observes the provider's actual OS exit code,
then DAP emits `exited`, followed by `terminated`, exactly once. Abrupt target EOF
does not manufacture exit code zero; unknown mobile exit codes remain unknown.

Debuggee detach releases the callback stop and debugger owner, leaving the
application usable. The provider does not kill a detached application or remove a
bundle it is still executing. The target worker removes deployment assets only
after process exit.

## Transport trust

The authenticated protocol is **not encryption**. Raw TCP is loopback-only.
Non-loopback target access requires a derived authenticated SSH route or an
explicitly configured secure external transport terminating on local loopback.
There is no arbitrary plaintext host/port option. An externally managed transport
is a trusted provisioning assertion, not a TLS implementation in DNCDbg.

SSH uses explicit identity and known-host files, strict host-key checking,
noninteractive authentication, disabled ambient config/agent forwarding, modern
SFTP `scp`, and a fixed validated absolute controller executable. JSON is sent
through stdin; no caller-generated remote shell command is constructed. `ssh -W`
opens only the exact target loopback port. Its local listener/child process,
half-closes, cancellation and reaping use the common stdio tunnel.

ADB allocation first reserves a dynamic daemon mapping with a unique staging
nonce and rebinds only that acquired mapping. Lost replies are recovered using
the exact device/nonce; unrelated mappings are never reset. Physical network ADB
requires explicit paired TLS, not `adb tcpip`.

## Validation and packaging

### Desktop transport detach and file-handle ownership

The desktop transport fixture must prove normal application I/O **after**
debugger ownership has been relinquished, not race I/O against the call that
resumes the callback. Its old shape returned from the source breakpoint, opened
the result file immediately and slept 150 ms while native detach unwound.
Windows reproduced `IOException: The handle is invalid` during flush/disposal.
Waiting for target exit before reading the result did not order file creation
against debugger teardown.

The fixture now uses a deterministic loopback ownership fence established
**before** the source breakpoint. It opens, writes and flushes its own retained
file before the callback stop. After transport EOF, the backend calls production
`Detach` and completes `DebugEngine.Dispose`, then acknowledges the fence. Until
that acknowledgement the target must remain alive and neither its result nor
staging file may exist. The target then:

1. Writes/flushes the retained pre-detach file handle again.
2. Opens a new correctly owned file, writes `42`, flushes to disk and disposes it.
3. Atomically renames that closed staging file to the result and exits normally.

The original exact `disconnect relinquishes callback stop (result="42")`
assertion remains, strengthened with retained-handle contents, normal Windows
exit code, no pending file and exact process/socket cleanup. There are no timing
sleeps, retries, ignored I/O exceptions or reduced assertions. The focused
`--detach-io` probe runs three independent fail-fast cases; the Windows workflow
also executes the full desktop transport proof.

Inspection of production launch ownership found only parent-side process/thread
handles and local pipe-client copies being closed; inherited child references
are separate. The process watcher already retains its own OS handle. The test
backend and probe now also open their observation handles while the target is
alive, rather than asking for an exit code from an ID-only `Process` object after
the process has vanished. No production handle-inheritance workaround is used.
The first Windows probe preserved the target's retained handle and produced both
correct files with OS exit code zero; it separately exposed that lazy observer
handle bug, which is covered by retained observation handles.

The native Windows runtime debugging channel itself owns remote-process handles
during teardown (see the pinned runtime's
[`LocalEventChannel::Delete`](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/debug/di/localeventchannel.cpp)).
The proof now establishes a causal ownership boundary instead of assuming that
callback resumption means all native debugger cleanup has already finished.
The retained/pre-existing and new/post-teardown handle checks distinguish this
fixture ordering defect from production closing unrelated application handles.

The normal solution requires no mobile workload. Normal CI runs neutral contracts,
the existing desktop proof/smoke/acceptance suite, and artifact-isolation checks.
The remote workflow requires the same real native host on Linux/macOS and a real
Linux x64 Docker device through both structured commands and SSH. Local Docker
arm64 runs the same proof. Mobile adds real pinned Android/iOS deployment,
DevFlow identity/interaction, fault and cleanup proofs, not a replacement engine.

The upstream `test-suite` executable/assertion sources remain unchanged.
Release adapters and managed provider packages cannot contain target-native
agents or test hooks. Target agents/hosts/controllers are explicit RID assets
published separately with an integrity inventory. Fault injection is compiled
only into internal Debug test artifacts and rejected for Release.

See [configuration, provisioning, support and migration](remote-devices.md).
