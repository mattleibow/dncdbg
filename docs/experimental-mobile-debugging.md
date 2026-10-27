# Experimental mobile managed-debugging investigation

**Not shipping mobile debugger support. No mobile managed breakpoint has been
proven. Do not merge this experiment as a completed mobile feature.**

The local debugger and its DAP launch/attach paths are unchanged. The portable
solution does not require mobile workloads. The MAUI app is intentionally built
separately.

## What exists

| Layer | Status |
| --- | --- |
| Explicit, bounded, authenticated loopback protocol | Implemented, version 1 |
| Host-side typed client | Implemented; **not** an `ICorDebug` COM proxy |
| Connection-owned handle/callback boundary | Contract and desktop proof |
| Device startup library | Managed capability probe, explicitly opted in |
| Native device-side execution owner | **Missing; requires a public startup provider** |
| Mobile `ICorDebug` proxy/backend integration | **Blocked, not implemented** |
| Managed breakpoint, stack, local, expression, continue, disconnect | Proven over TCP on a separate desktop debuggee only |
| iOS simulator / Android emulator | MAUI UI and real device-agent handshake; debugging capability rejected |
| Physical iOS / Android devices | **Not tested or supported by this experiment** |

`src/DncDbg.Remote` has no dependency on the local engine, DBI, DAC, ClrDebug,
MAUI, or private runtime exports. `RemoteAgent(enableExperimental: true)` is
required to start the listener. It defaults to a backend which advertises
`ManagedDebugging: false` and rejects execution requests with
`RuntimeUnavailable`. A successful handshake, DevFlow screenshot, or functioning
counter does not change that capability.

`tests/DncDbg.RemoteTests/EngineBackend.cs` is a **desktop test adapter**, not a
mobile implementation. It wraps the existing engine's public API and therefore
uses its real, public `ICorDebug` implementation against a separate process.
The host-side client currently transports a narrow managed-debugging command
surface; it cannot be substituted for an `ICorDebug` object in the engine.

## Public-API and runtime blockers

Observed with SDK **10.0.401**, MAUI workload/packages **10.0.20**, runtime
**10.0.12**, and DevFlow **0.1.0-preview.12.26421.1**:

1. **iOS CoreCLR runtime pack:** building the fixture for
   `net10.0-ios / iossimulator-arm64 / UseMonoRuntime=false` fails with
   `NETSDK1082`: no `Microsoft.NETCore.App` runtime pack for
   `iossimulator-arm64`. The normal build uses Mono. Mono's soft debugger is
   not an `ICorDebug` provider. Implementing its wire protocol would be a
   separate backend, not evidence that the requested CoreCLR architecture works.
2. **Apple build toolchain:** installed .NET iOS **26.5.10318** rejects the
   selected Xcode **27.0** and requires **26.6**. Validation used the already
   installed `/Applications/Xcode-26.6.0.app/Contents/Developer`, scoped to
   `dotnet build` through `DEVELOPER_DIR`, without changing global Xcode or
   installing workloads. The resulting app is linked against SDK **26.5** and
   ran on an **iOS 27.0** simulator controlled by Xcode 27's `simctl`. This is
   not proof of building against SDK 27 or of a scene-lifecycle migration.
3. **Android:** `UseMonoRuntime=false` does build using Android pack
   **36.1.69**, with warning `XA1040` (experimental CoreCLR). The APK contains
   `libcoreclr.so`, `libmscordbi.so` and `libmscordaccore.so`, but not an
   Android-native `dbgshim`. Merely having DBI/DAC is not a public startup
   contract. No public in-process startup provider has been integrated or
   validated. `Microsoft.Diagnostics.DbgShim` used by the desktop engine does
   not supply Android/iOS native assets.
4. **In-process execution ownership:** the managed startup probe must not
   become its own runtime's execution controller. Suspending managed threads
   would also suspend that controller. A real device backend needs an
   independently owned native execution/transport thread, correctly typed
   public interfaces, matching runtime assets, and proven shutdown behavior.
   The profiler startup API alone does not provide an `ICorDebug` instance.

We do not replace these missing pieces with undocumented DBI exports, copied
runtime layout offsets, handcrafted runtime internals, or a simulated breakpoint.

Useful public contracts:

- [ICorDebug](https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-interface)
- [CreateDebuggingInterfaceFromVersionEx](https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/createdebugginginterfacefromversionex-function)
- [ICorProfilerCallback.Initialize](https://learn.microsoft.com/dotnet/framework/unmanaged-api/profiling/icorprofilercallback-initialize-method)
- [Mono debugging](https://github.com/dotnet/runtime/blob/main/docs/workflow/debugging/libraries/debugging-vscode.md)

## Reference audit and attribution

Audited **JaneySprings/clrdbg** at
[`bb5e52b7d0fdef874288a46a4a2aa0f1643e37d7`](https://github.com/JaneySprings/clrdbg/tree/bb5e52b7d0fdef874288a46a4a2aa0f1643e37d7),
particularly `DotNet.Debugging.Remote`, its agent/protocol/handle sources,
host proxies, and `docs/remote`.

- Its root [MIT license](https://github.com/JaneySprings/clrdbg/blob/bb5e52b7d0fdef874288a46a4a2aa0f1643e37d7/LICENSE)
  identifies **Copyright (c) 2026 Nikita Romanov**.
- Its [attribution](https://github.com/JaneySprings/clrdbg/blob/bb5e52b7d0fdef874288a46a4a2aa0f1643e37d7/ATTRIBUTION.md)
  credits **SharpDbg by MattParkerDev**, commit
  `e3f2298746c619b16a7d683d39f1fe01aa5ef8b8`, under MIT,
  **Copyright (c) 2026 Matthew Parker**.
- The reference is research, **not incorporated source**. No C++, generated
  proxies, COM stubs, wire definitions, assets or build scripts were copied.
  Future incorporation must retain both applicable MIT notices.
- Its useful separation is native target startup, object handles, host proxies,
  and callback ownership. These are sound concerns to address explicitly.
- Its bootstrap explicitly invokes DBI's `DllMain` and
  `CoreCLRCreateCordbObject3` exports. Exported implementation entry points are
  not made supported public APIs by being visible or linked in source comments.
  This experiment does not call them.
- Its generic invocation path calls arbitrary interface slots through a
  machine-word function signature. Published COM layouts do not establish that
  one variadic/word ABI correctly marshals every interface signature. Our
  protocol admits only typed, enumerated operations and no function pointers,
  vtable indices, remote addresses, or arbitrary memory reads.
- The reference logs a version mismatch rather than rejecting it. Version 1 of
  this experiment rejects it before creating any runtime backend.
- The reference's disconnect path retries continue a fixed number of times.
  Our backend contract instead requires exact ownership of callback stops and
  deterministic release/detach on disconnect. The desktop test actually drops
  its connection while stopped and verifies that the target finishes.

This audit is not a security certification or verification of the reference's
claimed platform coverage.

## Protocol version 1

The connection is local development only: bind/connect **IPv4 loopback**.
iOS simulators share host networking; Android uses an explicitly scoped
`adb -s SERIAL forward`. No LAN listening, physical-device tunnel, TLS,
multi-user deployment, or untrusted-network support is provided.

- Frame: 4-byte little-endian magic `DNC1`, 4-byte little-endian signed JSON
  length, then UTF-8 JSON. Length must be 1..262144 bytes.
- Envelope: `Version`, `Sequence`, `Operation`, `Body`, optional stable `Error`
  code. Enum values are explicit protocol identifiers, not runtime COM slots.
- First request: version 1, sequence 1, `Hello`, a cryptographically random
  256-bit per-startup token. Wrong versions/tokens do not create a backend.
  Tokens are not logged. Debug fixture descriptors live in the app's private
  sandbox and must be removed after validation.
- Subsequent requests increase sequence by exactly one. Responses must match
  version, sequence **and operation**. One request is in flight at a time.
  A canceled/partial stream cannot be reused.
- Handshake deadline: 5 seconds. Each idle/read/backend operation: 30 seconds.
  One connection owns one backend; listeners/backends are closed on disposal.
- Enumerated commands: breakpoints, start, wait for a stopped callback, stack,
  expression evaluation with engine function calls disabled, continue, handle release, disconnect.
  Unsupported operations do not fall through to native invocation.
- A stopped callback owns exactly one continue. Callback IDs and frame handles
  are connection-local. Frames expire on continue; explicit release consumes
  one handout. Numeric IDs are never reused within a table. Stale/double releases
  are errors. The serialized execution owner, not callback threads, accesses the
  lease table; it is bounded at 4096 entries.
- Backends must clean up in `DisposeAsync`, even after malformed input,
  authentication failure, cancellation, timeout, or EOF. A stuck native call
  which ignores cancellation is not made safe by this transport; a future
  native provider must prove that behavior independently.

## Reproduction and evidence

See [`tests/DncDbg.MauiTestApp/README.md`](../tests/DncDbg.MauiTestApp/README.md).
Evidence belongs in ignored `build/validation/mobile`, not in source control.
Never check in private endpoint descriptors/tokens.

### Local execution record, 2026-10-28

| Check | Result |
| --- | --- |
| Portable solution, Release | Build succeeded, zero warnings/errors |
| Remote protocol/ownership + real desktop breakpoint tests | 46 checks passed |
| Existing local CLI/protocol smoke suite | 96 checks passed |
| iOS Mono Debug, simulator arm64 | Build, DevFlow wait/status/tree, scenario/reset assertions, screenshot passed |
| iOS Release, experimental property supplied | Actual build passed; DevFlow/remote references excluded |
| Android CoreCLR Debug, arm64 / Pixel_API_37 | Build passed with expected `XA1040`; explicit-port status/tree, scenario/reset, screenshot passed |
| Android DevFlow broker wait | Timed out; fallback port 9223 validated, not counted as broker success |
| Both real mobile agents | Authenticated/versioned handshake; explicit runtime kind; execution rejected with `RuntimeUnavailable` |
| Mobile managed breakpoint | **Blocked, not run** |
| Cleanup | Fixture apps uninstalled; owned read-only emulator stopped; newly created simulator shut down/deleted; ADB forwards gone; endpoint descriptors deleted; existing broker left running with zero agents |

The portable test suite distinguishes protocol contract fakes from the real
desktop breakpoint proof. CI runs both without installing MAUI. A mobile
release gate must additionally prove an actual managed breakpoint at
`MOBILE_BREAKPOINT`, inspect `input == 1` / `answer == 42`, evaluate
`answer + 1 == 43`, continue, and verify both normal and interrupted cleanup.
That gate remains **red** on this branch. No PR should be opened or merged as
completed mobile debugger support until it passes.
