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

`platform`, `appPath`, `applicationId`, `deviceId`, `runtimeIdentifier` and
`assetsPath` are required. The assets directory contains matching user PE DLLs
and portable PDBs, not an entire framework/runtime symbol cache. Module MVID,
CodeView GUID/stamp/age, portable-PDB identity and requested source checksums are
checked. Symbol catalogs are uploaded in bounded chunks; the authenticated
256-KiB frame limit is unchanged. Missing/ambiguous sequence points and changed
source files are errors, never invented verified breakpoints.

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

- **Android debugger:** `adb -s SERIAL forward --no-rebind tcp:0 tcp:TARGET`
  allocates a host port dynamically for the target's dynamically allocated,
  loopback-only debugger listener. Cleanup removes only that owned local port.
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
  Changing the launch configuration during restart is explicitly unsupported.

## Typed model and feature boundary

`src/DncDbg.Remote/contract.json` is the interface schema. Run
`python3 src/DncDbg.Remote/generate_contract.py` after editing it; CI checks
generated C# records/client methods and native operation/request guards with
`--check`. The wire stays strictly framed/authenticated protocol v1; mobile DAP
requires **typed model v2**, an exact provider ID and exact pinned build tuple.
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
Evaluation admits local/argument names, field/index paths and checked int32
literal addition/subtraction. It does **not** execute arbitrary managed code.

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
  through DAP `filterOptions`; wildcard, inheritance and mixed
  conditional/unconditional filters are not implemented.
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
The original direct-client native proofs remain separate regression gates.

**Keep PR #11 draft.** Full local/CI, current-main integration, both real mobile
gates and full independent review are required before merge. No automatic
merge is authorized. Historical proof results in the architecture document
must not be treated as evidence for newly added preview features.
