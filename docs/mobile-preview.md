# Mobile DAP preview (private, build-pinned)

This is an **experimental preview**, not supported general mobile debugging.
Only the exact .NET 11 RC1 **arm64 iOS simulator and Android emulator** builds
in `PrivateBootstrapPolicy.SupportedBuilds` are admitted. No physical device,
Mono, NativeAOT, arbitrary .NET 11 build, or public remote-COM compatibility is
claimed. The desktop launch path still uses the existing local engine.

## Launch

Run `dncdbg-dap` as a normal stdio DAP adapter. For example:

```json
{
  "name": "DNCDbg Android preview",
  "type": "dncdbg",
  "request": "launch",
  "mobile": {
    "platform": "android",
    "appPath": "/absolute/path/com.example.app-Signed.apk",
    "applicationId": "com.example.app",
    "deviceId": "emulator-5588",
    "runtimeIdentifier": "android-arm64",
    "assetsPath": "/absolute/path/matching-user-assemblies",
    "provider": "private-coreclr",
    "tunnel": { "provider": "adb" },
    "replaceExisting": false,
    "uninstallOnDisconnect": true,
    "startupTimeoutSeconds": 90
  }
}
```

For iOS use `platform: "iosSimulator"`, a built `.app` directory, the exact
simulator UDID, `runtimeIdentifier: "iossimulator-arm64"` and
`tunnel: { "provider": "host" }`. The target must already be booted; the adapter
does not seize a shared device or change global Xcode/workload settings.
The host must have `adb` (Android) or `xcrun` (iOS simulator) available on `PATH`.

`platform`, `appPath`, `applicationId`, `deviceId`, `runtimeIdentifier` and
`assetsPath` are required. The assets directory contains matching user PE DLLs
and portable PDBs, not an entire framework/runtime symbol cache. Module MVID,
CodeView GUID/stamp/age, portable-PDB identity and requested source checksums are
checked. Symbol catalogs are uploaded in bounded chunks; the authenticated
256-KiB frame limit is unchanged. Missing/ambiguous sequence points and changed
source files are errors, never invented verified breakpoints.
Source breakpoints keep their DAP ID/source identity when later module-load
events verify them and when a restart restores them.

The app must opt in to and package the target debugger, DBI and DAC, as the
[MAUI fixture](../tests/DncDbg.MauiTestApp/README.md) does. This does not inject
debugging into an arbitrary store/Release app. Android APKs must include their
managed assemblies (`-p:EmbedAssembliesIntoApk=true`); SDK Fast Deployment's
separate development-host push is not an installable APK. The adapter installs, launches,
acquires a fresh session-bound endpoint/token and owns the debugger route.
`replaceExisting` defaults to false. `uninstallOnDisconnect` defaults to false.
The startup timeout defaults to 60 seconds (allowed range 1–300).

Do not combine `mobile` with desktop launch/build/environment/source-map,
JIT/evaluation, terminal, `noDebug` or `stopAtEntry` options.
Unsupported options/requests are rejected instead of routed
to the desktop engine.

## Transport and lifecycle

- **Android debugger:** first allocate with
  `adb -s SERIAL forward --no-rebind tcp:0 localabstract:dncdbg-UNIQUE_NONCE`,
  then rebind only that acquired host port to `tcp:TARGET`, the target's
  dynamically allocated, loopback-only debugger listener. The unique staging
  destination is an ownership marker, not a device listener. If allocation is
  canceled or errors after the daemon commits but before the CLI returns the
  port, bounded rollback queries `adb forward --list` for that exact
  serial/nonce and removes exactly its local port. Rebinding and normal disposal
  already know that owned port; repeated cleanup does not remove it twice.
  Rollback has an independent 10-second budget, not the canceled startup token.
  No fixed debugger port, `--remove-all`, global reset, or debugger `reverse`.
- **iOS simulator:** direct host loopback networking, without a tunnel process.
- `IMobileTunnel` is the route-ownership boundary for future device transports;
  unimplemented providers and physical-device selections fail explicitly.
- **DevFlow is separate:** its broker `adb reverse tcp:19223 tcp:19223`, agent
  forwarding and explicit Android `9223` fallback are UI-automation plumbing.
  None is created or reused by the DAP debugger lifecycle. A fallback UI pass
  is not a broker-discovery pass.
- A fresh launch nonce must match the target descriptor; a stale startup
  token/descriptor cannot attach to a previous launch. Credentials are never
  emitted as DAP output. Only the matching owned descriptor is deleted.
- `disconnect(terminateDebuggee:false)`/`detach` relinquishes any callback stop
  and DBI leases, closes the route, and leaves the app installed and runnable.
  `terminate` and default launch `disconnect` also stop the owned app; uninstall
  happens only when explicitly selected. EOF/cancellation cleans owned routes.
- `restart` terminates the prior launch, reacquires a new endpoint, restores
  source breakpoints/exception filters and starts a fresh execution owner.
  With the default `replaceExisting:false`/`uninstallOnDisconnect:false`,
  the session retains its installation identity and verifies its per-installation
  marker before redeploying its own app. This does not authorize replacing an
  unrelated installation, including one whose ownership marker was changed.
  Changing the launch configuration during restart is explicitly unsupported.
- Closing cancels and settles launch/redeployment before inspecting and releasing
  installed apps, routes and clients. Cancellation is not reset by restart;
  no later acquisition or process event is permitted after session closure.

## Typed model and feature boundary

Module paging follows DAP: omitted or zero `moduleCount` returns all remaining
modules from `startModule`; a positive count bounds the page. `totalModules`
always describes the full snapshot, including for an empty out-of-range page.

`src/DncDbg.Remote/contract.json` is the interface schema. Run
`python3 src/DncDbg.Remote/generate_contract.py` after editing it; CI checks
generated C# records/client methods and native operation/request guards with
`--check`. The wire stays strictly framed/authenticated protocol v1; mobile DAP
requires **typed model v3**, an exact provider ID and exact pinned build tuple.
Model v3 replaces the lossy exception stage/type lists with typed per-stage
rules; v2 targets are rejected instead of silently misinterpreted.
Capabilities expose runtime/platform, runtime/DBI/DAC identities/hashes, provider,
model version and individual features. Modules carry MVID/load generation;
events/stops/frames carry generation/stop identity. DAP integer references are
never reused across restarts and are invalidated on every resume/new stop.

Implemented mobile requests are source line breakpoints, process/module/thread
events and snapshots, continue, pause, synchronous step-in/next/step-out,
stack/scopes/variables, safe evaluation, exception filters/info, detach,
terminate and restart. Native callbacks only enqueue references/stops; one
target-native execution owner performs all `ICorDebug` execution control.

Inspection reads primitives, bounded UTF-16 strings, zero-based
one-dimensional arrays and instance fields whose exact module/type symbols
are supplied. Arrays/objects use stop-scoped leases and paged children.
Omitted or zero variables `count` requests all remaining children, not a
100-element page. Requests exceeding the 1000-child or framed-payload safety
limit fail explicitly (`VariableLimit`/`ResponseLimit`), never truncate.
Floats/doubles use invariant round-trip precision and retain NaN, infinities
and negative zero.
Evaluation admits local/argument names, field/index paths and checked int32
literal addition/subtraction. It does **not** execute arbitrary managed code.

Exception rules independently associate optional exact CLR type conditions with
first-chance `all` or terminal `unhandled` stages. Conditional and unconditional
rules can be combined without forming a cross-product. Empty filters disable
both stages. `exceptionInfo.breakMode` reports the actual stopping stage.
`user-unhandled` requires Just My Code classification not implemented by this
mobile provider and is explicitly rejected; desktop exception semantics are
unchanged.

Explicit limitations:

- No func-eval, property getters, `ToString`, setters, memory reads/writes,
  conditional/hit-count/log/function breakpoints, targeted step-in, single-thread
  execution, set-next-statement, hot reload or async-aware stepping.
- Per-request DAP cancellation is unsupported and reported as such; disconnect
  cancels deployment or closes the owned debugger session.
- Strings over 4096 UTF-16 units, multidimensional/nonzero-based arrays and
  values without supported representations fail explicitly. Missing type
  symbols display an explicit unavailable value, not invented children.
- Exception stops report actual CLR type; message/property func-eval is
  unavailable. `all`/`unhandled` filters optionally accept exact CLR type names
  through DAP `filterOptions`; wildcard and inheritance matching are not
  implemented. Conditional and unconditional rules may be mixed.
- Breakpoint changes after attach require a stopped target. Stepping depends
  on the pinned runtime; unsupported runtime calls return errors, not successful
  no-ops. Lack of iOS func-eval does not disable read-only mobile debugging.
- The target's exact process exit code is not available from this provider;
  termination reports that limitation instead of fabricating exit code zero.

## Providers and reference audit

The private provider explicitly uses `DllMain` and `CoreCLRCreateCordbObject3`.
Exact platform/architecture/version, runtime/DBI/DAC build IDs and code hashes,
and required private exports are validated before activation. Product version
or matching filenames alone are insufficient. There is no desktop-DBI fallback.

`IMobileDebuggerProvider` separates provider validation from deployment and
transport. An official provider can replace the private provider when its
components/API/licensing permit it; selecting one now reports unavailability,
without quietly activating the private implementation.

Verified references (2026-10-28):

- [dotnet/runtime #136350](https://github.com/dotnet/runtime/issues/136350):
  remote transport belongs to the debugger, not CoreCLR.
- [dotnet/docs #56485](https://github.com/dotnet/docs/issues/56485) and
  [official mobile initialization guidance](https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/initialize-debugger-mobile):
  .NET 11 mobile CoreCLR uses `ICorDebug`; the documented target-profiler
  startup sequence uses `CoreCLRCreateCordbObjectEx`. This pinned private
  provider does not claim to implement that public initialization path.
  Visual Studio's `vsdbgremotecoreclrtarget`/`remotemscordbihost` proxy protocol
  is not a public protocol, and redistributing those binaries is not authorized
  by this project.
- [JaneySprings/clrdbg at bb5e52b7](https://github.com/JaneySprings/clrdbg/tree/bb5e52b7d0fdef874288a46a4a2aa0f1643e37d7/DotNet.Debugging.Remote):
  behavior/reference only, with the prior startup adaptation's MIT attribution
  retained in `THIRD-PARTY-NOTICES.md` and `src/licenses/`. Its target/host split,
  callbacks and lease concerns are useful references. Its arbitrary vtable
  machine-word invocation/proxy generator/protocol is **not** incorporated.
  This implementation generates a closed typed model and uses explicit COM
  signatures from the exact runtime IDL, not a generic slot-call ABI.
- [dotnet/android tracing/deployment](https://github.com/dotnet/android/blob/main/Documentation/guides/tracing.md):
  `DiagnosticConfiguration`/`DOTNET_DiagnosticPorts` and `dotnet-dsrouter`
  provide EventPipe diagnostic routing. They are **not ICorDebug execution
  control** and cannot prove breakpoints, stepping or evaluation.

## Reproduction and merge gates

Host-only contracts (explicit fake device tools/runtime, not mobile proof):

```sh
dotnet build DncDbg.slnx -c Release
python3 src/DncDbg.Remote/generate_contract.py --check
python3 tests/DncDbg.MobileTests/test_dap.py
python3 src/DncDbg.Remote.Target/build.py --test-host
```

Real devices use `tests/DncDbg.MobileTests/run_device.py --help`: it launches
the actual adapter, leaves deployment/debugger forwarding to the adapter,
and exercises the exact `MOBILE_BREAKPOINT` 1/42/43 gate plus strings, array and
object children, safe paths, all three step requests, exceptions, pause,
restart and stopped detach. Its independent DevFlow UI lane asserts completion
and captures a screenshot. Reports stay under ignored `build/validation/`.
The fixture now includes 150 array elements and precision-sensitive/special
floating-point fields. The suite requires both unpaged array forms, bounded
explicit paging, per-stage exception conditions and restart under the default
replacement/uninstall policies on each platform.

`tests/DncDbg.MobileTests/run_faults.py` additionally gates real completed
installs to interrupt initial deployment and restart redeployment, kills running
and stopped targets, and verifies that the explicitly opted-in native watchdog
exits a stalled target. See the [fixture guide](../tests/DncDbg.MauiTestApp/README.md)
for the separate Debug-only native/managed fault-injection build flags. Normal
helpers have no fault export; Release enables none of these experimental paths.
Each fault scenario must leave no owned debugger/UI forward, app process or
endpoint credential.
The original direct-client native proofs remain separate regression gates.

**Keep PR #11 draft.** Full local/CI, current-main integration, both real mobile
gates and full independent review are required before merge. No automatic
merge is authorized. Historical proof results in the architecture document
must not be treated as evidence for newly added preview features.
