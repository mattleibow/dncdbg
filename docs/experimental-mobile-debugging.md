# Experimental mobile managed-debugging investigation

For the newer adapter-owned typed mobile DAP surface, configuration, limitations
and validation gates, see [Mobile DAP preview](mobile-preview.md). The historical
proof evidence below does not establish that the newer preview gates passed.

**Not shipping general mobile debugger support. The private bootstrap is an
explicit, runtime-version-fragile experiment, not a supported CoreCLR API.**

The local debugger and its DAP launch/attach paths are unchanged. The portable
solution does not require mobile workloads. The MAUI app is intentionally built
separately.

## What exists

| Layer | Status |
| --- | --- |
| Explicit, bounded, authenticated loopback protocol | Implemented, version 1 |
| Host-side typed client | Implemented; **not** an `ICorDebug` COM proxy |
| Connection-owned handle/callback boundary | Contract and desktop proof |
| Default device startup library | Capability-only probe; execution unavailable |
| Opt-in target-native startup/owner | Private, exact-build-pinned DBI experiment |
| Native typed command backend | Single portable-PDB-bound module / int32 proof; not a COM proxy |
| General mobile engine/DAP integration | **Not implemented** |
| Managed breakpoint, stack, local, expression, continue, disconnect | Proven over TCP on a separate desktop debuggee only |
| iOS simulator / Android emulator | Dedicated real runtime gates; see current private experiment below |
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
The host-side client transports a narrow managed-debugging command
surface; it cannot be substituted for an `ICorDebug` object in the engine.

## Private target-native experiment

The explicit authorization to use private exports supersedes the earlier
public-only constraint, **not** CoreCLR's API-support boundary. Both opt-ins are
required in Debug:

```text
EnableExperimentalRemoteDebugging=true
EnableExperimentalPrivateBootstrap=true
```

`src/DncDbg.Remote.Target` adapts the MIT JaneySprings startup topology with
retained JaneySprings/SharpDbg notices. DBI is loaded **inside the target**, next
to its already loaded CoreCLR. `DllMain` and `CoreCLRCreateCordbObject3` run on an
independently created native pthread. They are private implementation exports;
their presence does not make this a supported runtime integration.

Before any private export executes, the fixture checks:

- Actual virtual-device status, target platform and arm64 architecture. Physical
  iOS/Android, macOS, desktop and other architectures are rejected.
- Exact CoreCLR version `11.0.0-rc.1.26425.128`.
- Runtime, DBI **and DAC** build identities and executable hashes, pinned in
  `PrivateBootstrapPolicy.SupportedBuilds`. Apple uses `LC_UUID`, the
  `IOSSIMULATOR` platform tag and SHA-256 of `__text` (stable across ad-hoc
  signing); Android uses GNU build IDs and SHA-256 of the entire ELF.
- Both required DBI exports, their common loaded-image origin and exact target
  path. A product-version match alone is insufficient. No desktop DBI fallback
  or target-library load in the macOS host is permitted.

The build is refused for Mono/NativeAOT or unsupported RIDs. Identity mismatch,
malformed image, missing asset/export or unsupported device leaves the
capability-only backend active with actionable refusal diagnostics.
Capabilities expose `Bootstrap`, `SupportedBuild` and the deliberately narrow
`Scope`, rather than representing this as ordinary supported mobile debugging.
Release excludes Remote, DevFlow, native helpers and their P/Invokes.

### Execution ownership and proof surface

One target-native thread owns the authenticated/version-1 loopback connection,
public typed COM calls, callback stops, frame leases, continue and detach.
Managed code only performs the preflight, creates a random 256-bit token and
writes the private descriptor **before** debugging starts; it is not needed to
serve a suspended runtime. Callback threads transfer counted references/stops
to the native owner and never drive execution. There is no generic vtable or
machine-word dispatcher and no remote function pointer/address operation.

The proof uses the actual build's PE/portable PDB: matching PDB identity and
source SHA-256, method token, scope-local slots, parameter indexes and sequence
point. The target validates the module MVID before binding an IL breakpoint.
Stack source/line annotations require the actual frame's module, method token
and IL IP to match. Argument/local reads use real `ICorDebug` values; the small
native evaluator supports int32 names and addition of an int32 literal only.
It is **not** general Roslyn evaluation, managed function evaluation or a mobile
replacement for the desktop engine.

Native startup is single-use per app launch and accepts one connection. Wrong
tokens/versions are rejected before creating a debugger; sequence replay closes
the connection. Handles and stop IDs are never reused in a session, and all
frames expire on continue/disconnect. The portable lease implementation also
encodes non-reused connection generations. Duplicate continue/release is an
error, not another runtime operation.

Malformed authenticated JSON payloads close the connection immediately with
EOF and run the same exact-once cleanup. They do not consume a sequence and
silently wait for another request. Fail-closed is deliberate: command validation
can already have partially mutated state when JSON conversion throws. Host
contracts require malformed `Release` without `Handles` to finish within one
second, both idle and with an outstanding stop, and verify continue/detach and
every retained reference exactly once.

Handshake/read/write/stop waits are bounded (5/30 seconds; initial host wait
120 seconds). EOF, cancellation and timeout consume an outstanding stop once,
deactivate the breakpoint, release leases, detach, terminate the DBI object and
close sockets. A watchdog monitors native-owner progress: a native call that
cannot be canceled exits **this explicitly opted-in fixture** after 20 seconds
(exit 86); failed cleanup exits it with 87 rather than leaving it suspended.
The initialized DBI module stays loaded until app exit: unloading its private
PAL is not assumed safe. The runner uninstalls the fixture and removes owned
ADB mappings, and checks native `owner-finished`/successful detach before
declaring the runtime gate green.

Native ownership/protocol fakes run with:

```sh
python3 src/DncDbg.Remote.Target/build.py --test-host
```

These load no runtime or DBI, cannot activate the target bootstrap on a host,
and are not mobile breakpoint evidence. They supplement the portable negative
tests and actual desktop target-death/disconnect proof. The build downloads
only the SHA-256-pinned MIT JSON header declared in `dependencies.json`.

Current runtime-gate results and exact red attempts are preserved under ignored
`build/validation/mobile/private-bootstrap/`. The public-only results below are
historical baseline evidence, **not** descriptions of this new private path.
No physical-device result or general mobile-debugger support is claimed.

### Verified private-path execution, 2026-10-28

| Gate | iOS simulator arm64 | Android emulator arm64 |
| --- | --- | --- |
| Default .NET 11 CoreCLR, pinned runtime/DBI/DAC identity/hash preflight | Passed | Passed |
| Target-side private factory, self-attach and managed callbacks | Passed | Passed |
| Real `MOBILE_BREAKPOINT`, `MainPage.ManagedScenario`, source line **35** | Passed | Passed |
| Actual managed argument/local `input=1`, `answer=42` | Passed | Passed |
| Limited int32 expression `answer + 1=43` | Passed | Passed |
| Continue, duplicate-continue/stale-handle rejection, normal UI completion | Passed | Passed |
| Fresh-launch disconnect **while stopped**, normal UI completion without host continue | Passed | Passed |
| DevFlow reset/result assertions and in-process screenshot | Passed | Passed |
| Native stop/frame cleanup, breakpoint deactivation, detach, DBI object termination, closed listener | Passed | Passed |
| DevFlow broker wait | Passed | **Still red**; explicit forwarded 9223 fallback passed |

Continue evidence is in `ios-report.json`, `android-report.json`,
`*-remote-capability.log`, `*-private-ui.jsonl`, `*-dncdbg-private-startup.jsonl`
and `*-continued.png`; stopped-disconnect repetitions are in `disconnect/`.
The runner returned **0 for all four** real private-mode runs. It verifies
matching PE/PDB identity and source checksum before the native session; the top
frame's source/line require the observed target module/token/IL IP match.
Other stack frames without source metadata are explicitly left unmapped rather
than assigned invented source locations.

The portable Release solution build has zero warnings/errors; **89** portable
remote/real desktop checks, **11** native ownership/protocol contract checks
(explicit fakes), and all **96** existing local CLI/protocol smoke checks pass.
Both Release configurations exclude DevFlow/Remote/native references and private
defines even with the experimental properties supplied; an actual iOS Release
build also passes with zero warnings/errors.

Earlier red attempts remain under `red-first-breakpoint/`,
`red-domain-abi/` and `red-function-module-abi/`. The implementation's incorrect
hand-indexed COM calls caused the two native stack/attach crashes; replacing
them with typed public interface declarations fixed those bugs. They are **not**
reported as runtime impossibility or as successful debugging. The DevFlow
mutation-lease issue was resolved with one persistent CLI batch, not by taking
over an unrelated agent.

`cleanup.json` records uninstalled fixture apps, removed endpoint descriptors,
owned ADB mappings removed, the dedicated simulator shut down/deleted and the
read-only emulator shut down. The existing broker remains unchanged with zero
agents. Both apps' managed scenarios completed normally before uninstall.
Independent review and full CI remain merge gates; these results alone are not
permission to merge or a claim that the public host startup API now works.

## Pinned .NET 11 toolchain and public-only baseline

The fixture now targets **`net11.0-android;net11.0-ios`**. Its own `global.json`
selects an already installed SDK; the repository root and debugger remain on
SDK **10.0.401**. No shared workloads, global Xcode selection or SDK installation
were modified. The tested toolchain is **RC1**, not a claim about a newer RC/GA:

| Component | Validated version |
| --- | --- |
| Fixture SDK | `11.0.100-rc.1.26425.128` |
| Installed workload set | `11.0.100-rc.1.26460.1` |
| MAUI SDK/packages | `11.0.0-rc.1.26451.6` |
| iOS SDK/ref/platform runtime packs | `26.5.12194-net11-rc.1` (`net11.0_26.5`) |
| Android SDK/ref/platform runtime packs | `37.0.0-rc.1.2257` |
| CoreCLR runtime packs on both targets | `11.0.0-rc.1.26425.128` |
| Scoped iOS build Xcode | Installed Xcode 26.6; selected Xcode 27 remains unchanged |
| DevFlow Agent/CLI | `0.1.0-preview.12.26421.1` |

### Defaults measured without runtime overrides

| Target | Public `UseMonoRuntime` value | Effective SDK runtime | Running app |
| --- | --- | --- | --- |
| iOS simulator arm64 | `false` | CoreCLR | `.NET 11.0.0-rc.1.26425.128 (CoreClr)` |
| Android arm64 emulator | Empty/unset | CoreCLR (`_AndroidRuntime` and `_AndroidRuntimePackRuntime`) | `.NET 11.0.0-rc.1.26425.128 (CoreClr)` |

**CoreCLR is the measured default on both tested targets.** An unset Android
property is not evidence for Mono. The Android SDK chooses MonoVM when the
public `UseMonoRuntime=true` switch is supplied, otherwise CoreCLR. Explicit
`false` also selects CoreCLR. On iOS, explicit `true` changes the evaluated
configuration to Mono and `false` selects CoreCLR. Override matrix entries were
evaluated; the device executions reported here use **no runtime override**.
The `_Android*` properties are recorded as SDK diagnostic evidence, not used as
runtime APIs or application configuration inputs.

The resolved CoreCLR packs are
`Microsoft.NETCore.App.Runtime.iossimulator-arm64` and
`Microsoft.NETCore.App.Runtime.android-arm64`, both at
`11.0.0-rc.1.26425.128`. They are accompanied by:

- `Microsoft.iOS.Runtime.ios.net11.0_26.5` and
  `Microsoft.iOS.Runtime.iossimulator-arm64.net11.0_26.5`,
  both `26.5.12194-net11-rc.1`.
- `Microsoft.Android.Runtime.37.android` and
  `Microsoft.Android.Runtime.CoreCLR.37.android-arm64`,
  both `37.0.0-rc.1.2257`.

The Android fixture minimum is now **API 24**, required by the .NET 11 Android
pack (`XA4216` rejects the template's previous API 21 minimum). The iOS app has
a `MauiUISceneDelegate` and scene manifest, verified in the built bundle, for
its iOS 27 simulator run. Broker registration can precede DevFlow HTTP-listener
readiness; the runner now gates on actual reachable `agent status`, not an
arbitrary launch delay.

### What changed, and what remains blocked

The old iOS **`NETSDK1082` runtime-pack blocker is resolved for this .NET 11
tuple**. Both apps build, run their UI scenario/reset assertions and screenshots,
and authenticate real remote capability probes reporting CoreCLR 11. iOS
DevFlow broker wait succeeds. Android preview.12 broker wait still times out;
explicit forwarded fallback port 9223 works and is recorded separately.

CoreCLR/DBI availability does **not** by itself establish the missing native
debugger startup implementation. Both bundles contain CoreCLR, DBI and DAC;
neither contains `dbgshim`. A fresh audit of the published
`Microsoft.Diagnostics.DbgShim` package **10.0.750501** found only Linux, macOS
and Windows RID dependencies, not Android/iOS. The local debugger's existing
dependency remains `10.0.745401`; it was not silently upgraded.

Additional, real public-API probes used the existing `DebugEngine.Attach`
and its public dbgshim runtime-startup registration against an **owned
CoreCLR 11 iOS simulator app**. An early-startup probe, repeated with the
latest published macOS-arm64 dbgshim asset `10.0.750501` in an isolated helper,
failed with:

```text
Cannot attach to process <pid>: The .NET runtime could not be debugged:
the runtime reported the error CORDBG_E_DEBUG_COMPONENT_MISSING.
```

Before the IPC configuration fix described below, the runner's attach after UI validation, with the debugger's existing
`10.0.745401` dependency, instead timed out with `the runtime did not answer`;
the default diagnostic IPC endpoint was also unreachable. Both attempts failed.
The supplementary helper did not change the debugger's dependency manifest.

These are bounded host-side attach probes, not an in-process device startup
provider or a mobile breakpoint test. They do not establish a universal
impossibility or identify the exact missing native component. They do establish
that changing the fixture's TFM/default runtime has **not produced a working
public startup path in the tested configuration**. There is still no validated
native execution/transport owner; the managed capability agent continues to
reject execution with `RuntimeUnavailable`. No private DBI startup exports or runtime layout assumptions were introduced
in that public-only baseline.

Current evidence is under ignored `build/validation/mobile/net11-default/`,
including `*-properties.json`, `*-runtime-matrix.json`, `*-resolved-packs.json`,
`*-native-assets.json`, screenshots, UI batches, real capability logs,
`ios-public-attach-probe.log`, `ios-public-attach-latest-shim-probe.log`, and
`dbgshim-package-audit.json`.
The validation runner deliberately returns 2 while the mobile breakpoint gate
is blocked. The portable .NET 10 Release build, 46 remote/desktop checks and
96 existing local smoke checks were rerun successfully after the retarget.
The .NET 11 iOS Debug/Release and Android Debug builds succeeded with zero
warnings/errors. Release reference evaluation excludes both DevFlow and Remote
even when the experimental property is supplied.
Earlier .NET 10 evidence remains below for history only.

## Public startup phase investigation

The startup probe now tests individual public operations rather than treating a
failed `DebugEngine.Attach` as a single opaque error:

1. Allowlisted target startup environment, working/native/temp directories.
2. Public `DiagnosticsClient.GetProcessEnvironment` reachability (the returned
   environment is discarded, never logged).
3. `EnumerateCLRs` and `CreateVersionStringFromModule`.
4. `CreateDebuggingInterfaceFromVersionEx`.
5. `CreateDebuggingInterfaceFromVersion3` with a generated-COM
   `ICLRDebuggingLibraryProvider3`.
6. `RegisterForRuntimeStartup`, callback delivery, and deterministic unregister.

Each phase is appended to JSONL before/after native calls, so a failed helper
still identifies its last operation. Helpers have an external deadline. Native
library-load probes run in separate processes and call no private export.

### Confirmed iOS DBI failure

Runtime enumeration returns **`S_OK`, one CLR** and creation of its opaque
version string succeeds. Discovery is not the blocker.

The packaged `libmscordbi.dylib` is arm64 **`IOSSIMULATOR`**, not `MACOS`.
A separate public `NativeLibrary.Load` test in the macOS host fails with:

```text
incompatible platform (have 'iOS-simulator', need 'macOS')
```

The corresponding public dbgshim factory returns
`CORDBG_E_DEBUG_COMPONENT_MISSING`; the published dbgshim source maps failure
to load DBI to that HRESULT. Supplying the exact packaged DBI/DAC through the
public version-3 provider does not change the result.

The provider records and validates its public runtime build-identity request:
the iOS runtime UUID is `6907233237423C4CB8189F481F1CF5DA`; the same-version
desktop runtime is `6BB5037327BB336CB48F3CAE461D3F9D`. The latter is rejected.
A product-version match is not permission to assume compatible native layouts.
The UUID reader uses public Mach-O `LC_UUID` metadata, not CLR structure offsets.
Synthetic metadata tests cover accepted identity, mismatch and malformed input.

The official `dotnet-symbol --debugging` lookup also retrieved DBI/DAC indexed by
the iOS runtime identity. Those files are still tagged **IOSSIMULATOR**. No Mach-O
platform tags, native identities, runtime binaries or loader checks were patched.

### Confirmed callback timeout cause and public configuration fix

The runtime and host initially use different temp/IPC directories.
`RegisterForRuntimeStartup` returns `S_OK` and a token, but the post-UI callback
does not arrive. The published dbgshim PAL tests debugger-transport readiness in
its selected IPC directory before enumerating modules for the callback.

Setting `SIMCTL_CHILD_TMPDIR` was **not effective**: the target startup snapshot
still showed its simulator-container directory. The working configuration is the
documented SDK `AppBundleExtraOptions --setenv=TMPDIR=...`, applied **before**
CoreCLR initializes, with the helper launched using the same project-owned
directory. The target snapshot confirms the value and the callback now arrives,
returning `CORDBG_E_DEBUG_COMPONENT_MISSING` rather than timing out. Its
registration token is unregistered in both the red and configured cases.

This fixes notification configuration, not the independent DBI ABI failure.
It is opt-in and does not change the normal debugger or global environment.

### Diagnostics is a separate public transport

The default PID-based diagnostics endpoint and a direct UDS diagnostic-port
experiment did not connect. Long generated endpoint paths and the separate
mobile namespace must not be confused with absence of the CLR.

The documented mobile **TCP** route succeeded on both the iOS simulator and
Android emulator:

```text
DOTNET_DiagnosticPorts=127.0.0.1:PORT,nosuspend,connect
    -> dotnet-dsrouter client-server
    -> short project-owned IPC socket
    -> dotnet-trace collect
```

Actual EventPipe traces were collected with exit code zero on both. This demonstrates
diagnostic connectivity only; EventPipe is **not** an `ICorDebug` execution
controller and cannot substantiate the breakpoint gate. The runner supports
the public TCP route for the emulator through an individually owned ADB
reverse and Debug-only `AndroidEnvironment` file. It never treats an Android
PID as a host PID.

### Reference and official-source boundaries

The pinned JaneySprings agent loads target-ABI DBI in the target process and
directly calls `DllMain` and `CoreCLRCreateCordbObject3`. Its native thread also
avoids suspending a managed execution owner. Those observations explain why
its topology differs; they do not make those exports public APIs. The public-only baseline did not call them. The separately opted-in private
experiment above now does, but does not adopt generic vtable invocation.

The inspected published dbgshim source is pinned to
`dotnet/diagnostics@ad724c4b85e7ae775e42f393c6e95a7557a57954`.
The runtime pack identifies
`dotnet/dotnet@3551975be08744f0418857c5bed8ab1545c5dd47`; its
`src/runtime/docs/workflow/building/coreclr/android.md` explicitly states that
managed debugging in that CoreCLR Android workflow is not supported. The APK
contains target DBI/DAC but no target-native public dbgshim. A target-native
public bootstrap or verified host-compatible cross-debugging payload remains
missing; it is not replaced with a desktop library of the same version.

Exact red/configured phase logs, allowlisted snapshots, loader results, public
symbol lookup output and TCP trace evidence are under
`build/validation/mobile/startup-research/` and
`build/validation/mobile/net11-default/`. Neither platform's managed breakpoint
gate is green. No PR or merge is authorized by diagnostic trace success.

The expanded portable/desktop suite passes **52 checks**, and the existing local
smoke suite still passes **96**. The final configured iOS `DebugEngine.Attach`
now reports the delivered component-load error instead of `the runtime did not
answer`. Android UI/capability probes and the TCP trace pass; no host attach
attempt is made with an emulator PID. DevFlow's Android broker-wait/fallback
limitation is unchanged and remains separately reported.

## Historical .NET 10 baseline (superseded for the current fixture)

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

At this historical stage we did not replace these missing pieces with private
DBI exports. The new explicitly authorized experiment uses those exports with
exact build validation, not copied CLR structure offsets or simulated stops.

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
- The original audit was research only. The new native bootstrap adapts its
  target-side startup topology and retains both applicable MIT notices in
  `src/licenses/JaneySprings-clrdbg.LICENSE`. Generated proxies, generic invocation,
  wire definitions and assets were not incorporated.
- Its useful separation is native target startup, object handles, host proxies,
  and callback ownership. These are sound concerns to address explicitly.
- Its bootstrap explicitly invokes DBI's `DllMain` and
  `CoreCLRCreateCordbObject3` exports. Exported implementation entry points are
  not made supported public APIs by being visible or linked in source comments.
  The new explicit private opt-in calls them and labels the boundary accordingly.
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

### Historical .NET 10 execution record, 2026-10-28

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
