# Canonical remote architecture proof

From the checkout:

```sh
dotnet run --project tests/DncDbg.NativeHostTests -c Release
```

This solution-built .NET 10 console project is the **required fast correctness
gate for every target agent, protocol, model, session or tunnel change**. It uses
no MAUI workloads, simulator, emulator or DevFlow. Python orchestrates the existing
stdio DAP driver and owned POSIX processes; actual debugger execution stays in
the **same** `src/DncDbg.Remote.Target/Bootstrap.cpp`, `RemoteModel.h` and generated
contract used on Android/iOS. It does not substitute `EngineBackend`, a managed
agent fake, or the ordinary desktop `DebugEngine`.

Prerequisites: the root SDK, Python 3, a C++17 compiler and macOS arm64 or Linux
x64/arm64. The public SDK's `KnownAppHostPack` selects `nethost.h`, `hostfxr.h`,
`coreclr_delegates.h` and `libnethost.a`; **only the native test fixture** compiles
C++. Normal adapter builds/publishes still need no C++ toolchain.

The target runtime is **11.0.0-rc.1.26425.128**, not whatever runtime is newest.
If absent, the runner installs the official SHA-512-locked archive in
[`runtime-lock.json`](runtime-lock.json) under this checkout's ignored `build/`.
No SDK/workload/global installation is changed. `--runtime-directory PATH` can
select an existing exact runtime. `PrivateBootstrapPolicy.NativeHostBuilds`
separately pins macOS arm64 and Linux x64/arm64 runtime/DBI/DAC identities/code
hashes. The original arm64 mobile tuples are retained separately from the new
hosted Android x64 mobile tuple; no desktop DBI is ever
substituted for a mobile DBI, and no product-version-only fallback is admitted.

## What actually runs

`Host.cpp` loads the target agent **before** `hostfxr_initialize_for_runtime_config`,
uses public nethost/hostfxr to initialize CoreCLR, and starts the same private
`DllMain` / `CoreCLRCreateCordbObject3` agent owner on an independent native
thread. It waits for real DAP configuration/native attach **before loading or
invoking the user managed payload**. `hostfxr` can initialize framework internals;
this is not a claim that no CoreLib code ever executes during runtime startup.
Phase logs prove the preload/runtime/payload ordering. The fixture then calls an
`UnmanagedCallersOnly` managed entrypoint and proves it returns into C++.

The suite launches `dncdbg-dap` with `remote`, not `program`/the desktop engine:

```json
{
  "remote": {
    "hostPath": "/built/test/native-host",
    "agentPath": "/built/test/libdncdbg_target.so",
    "assemblyPath": "/built/test/Payload.dll",
    "runtimeDirectory": "/exact/runtime/shared/Microsoft.NETCore.App/11.0.0-rc.1.26425.128",
    "assetsPath": "/built/test",
    "outputPath": "/this/checkout/build/remote-run",
    "tunnel": { "provider": "localTcpProxy" }
  }
}
```

Use `.dylib` on macOS, or `directLoopback` for a direct route. Unsupported
providers/options fail closed. There is no physical Apple USB provider yet.
`dotnet/info.remote` reports generic capabilities, the non-secret session
generation and route ports; credentials are never DAP output.

## Required gates

- **Direct and real TCP-proxy paths:** native-hosted source breakpoint, stable
  breakpoint IDs, stack/scopes, primitive/string/array/object inspection, safe
  evaluation, all three step operations, actual CLR exception/catch, pause,
  threads/modules, restart, stale handles/old-listener rejection, credential
  rotation, stopped detach, managed completion and native caller return.
- **Still-attached natural completion:** three rounds each of actual exit codes
  0 and 7 over direct and proxy routes (12 fresh targets). No DAP disconnect is
  sent first. Each must produce exactly one `exited` then `terminated`, the
  actual owned process must exit/reap, native cleanup must be exact-once, and
  no transport-failure output or credential/listener may remain. Use
  `--case natural-completion` for this focused real-runtime gate.
- **Real socket contracts:** fragmented/partial I/O, bidirectional half-close,
  complete reverse responses, reconnects, multi-MiB backpressure with bounded
  buffers, target EOF, I/O timeout, connection refusal, listener cancellation,
  delayed publication, collision, repeated disposal and allocation/disposal
  races. These use live sockets, not canned ADB replies.
- **Through the same DAP/native-agent path:** cancellation after proxy listener
  allocation but before publication, startup timeout, occupied host port,
  tunnel death while running/stopped, explicit termination while running/stopped,
  abrupt target EOF and restart/reconnect into a fresh generation.
  Stopped tunnel death must release the real callback and allow the managed
  command to finish; detach/DBI termination/owner completion are exact-once.
  A killed target cannot emit a cleanup callback: EOF cases assert process,
  credential and route removal without inventing target cleanup success.
- Existing framing/authentication/lease contracts and native ownership fakes
  remain additional negatives, not substitutes for these runtime tests.

Ordinary **Release** adapter binaries run the positive direct/proxy proofs.
Both publishes use run-owned, separate intermediate/build directories so a
native-host run cannot clean an adapter being used by acceptance tests.
Only a separate **Debug** adapter built with
`EnableExperimentalRemoteTestHooks=true` admits the acquisition barrier and
`dotnet/testCloseTunnel` fault operation. Ordinary Release artifacts are scanned
to prove those hooks and the C++ agent are absent. The target agent itself is the
ordinary helper without native watchdog fault injection.

## Ownership and evidence

`RemoteSession` translates DAP/model data and depends only on a generic lifecycle
factory. `RemoteLifecycle` owns acquisition settlement, cancellation deadlines,
descriptor nonce/credential validation, authenticated `RemoteDebugClient`,
route ownership and cleanup. `IRemoteTargetProvider` implements target-specific
acquire/start/stop and descriptor discovery; `ITunnelProvider` implements the
owned host route. Mobile command/path wrappers contain no shared model logic.

- `DirectLoopback`: iOS simulator/local target, no forward process.
- `LocalTcpProxy`: actual dynamic loopback listener and bidirectional TCP proxy,
  bounded buffers and transactional unpublished-listener cleanup.
- `AdbForwardProvider`: Android command syntax, dynamic `tcp:0` allocation,
  nonce recovery after lost replies, rebind and exact mapping removal. Its
  platform-specific contracts remain under `tests/DncDbg.RemoteTests`.
- Apple USB/physical-device transport remains a future, explicitly unavailable
  provider. No silent direct-loopback fallback.

Restart uses fresh target/tunnel/client/credential generations; DAP integer
handles are never reused. Reconnect after transport loss is an explicit fresh
runtime generation, not unsafe hot reattachment to the single-use private DBI.

The native embedding caller uses the target agent's `dncdbg_complete` after
managed return instead of deleting the descriptor and abruptly canceling/exiting.
The owner publishes a typed completion and settles its DBI/callback cleanup
before the native process returns. `NativeHostProvider` captures the observed
OS exit code before disposing its process, and shared lifecycle/session code
uses that observation for normal DAP exit semantics. This is product-provider
behavior, not a synthetic test-only `exited` event. Rebuild host and agent
together; an older helper without the completion export is refused.

Each run gets a fresh `build/` output folder. Exit 0 requires **all proof and
cleanup**; missing tuples/tools, failed assertions or leaked owned resources are
nonzero failures, never skips. `adapter-process.json` and target `process.json`
journal only owned PIDs and scoped paths for recovery. Repeat the same entrypoint
with `--output SAME_PATH --cleanup` after interruption. Never reuse another
run's journal. An emergency kill cannot make the original proof green.

Evidence includes full DAP transcripts, native phase logs, managed results,
exact source head/dirty state and binary/file SHA-256 hashes. Credentials,
runtime archives, build trees and compiled tools are excluded. Limits:
8 MiB/file, 100 MiB/run, 15-minute CI jobs and seven-day uploaded artifacts.
Native C++ phases are actual runtime evidence; host fakes are labeled separately.

[`remote.yml`](../../.github/workflows/remote.yml) makes macOS and Linux proofs
independent required PR checks. The existing required Docker job additionally
builds an official SDK container and runs **this same suite** in its native Linux
architecture with no runtime network access; it does not replace the original
Docker desktop-debugger test. Containers/images are uniquely named/labeled and
removed on every exit. Mobile CI separately proves packaging, private startup,
ADB/direct routing, DevFlow identity/UI and device cleanup; it is not the only
place common debugger correctness can be established.
