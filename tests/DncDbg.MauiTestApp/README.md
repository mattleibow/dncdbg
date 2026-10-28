# Mobile experimental fixture

The adapter-owned launch and extended inspection/stepping/exception/restart
gate is documented in [Mobile DAP preview](../../docs/mobile-preview.md).
The direct-client proofs below remain separate regression gates; their
historical results do not prove the newer DAP surface.

The fresh actual-adapter suites also check default-policy restart, all 150 array
children (omitted/zero count), bounded explicit pages, round-trip floats/doubles,
special floating-point values, and combined stage/type exception filters:

```sh
python3 tests/DncDbg.MobileTests/run_device.py \
  --platform iosSimulator --device YOUR_DEDICATED_SIMULATOR_UDID \
  --app tests/DncDbg.MauiTestApp/bin/Debug/net11.0-ios/iossimulator-arm64/DncDbg.MauiTestApp.app \
  --assets tests/DncDbg.MauiTestApp/bin/Debug/net11.0-ios/iossimulator-arm64 \
  --output build/validation/review-final/complete-ios
```

Use `--platform android`, the exact emulator serial, and its built signed APK
and assets for Android. The runner always terminates/uninstalls its fixture
and removes only its own UI mapping, including on failure.

`tests/DncDbg.MobileTests/run_faults.py` takes the same arguments. It gates a
**real completed install** before the adapter can launch, then terminates during
initial deployment and restart redeployment. It also crashes real running and
stopped targets and verifies DAP/endpoint/forward cleanup. Its watchdog scenario
requires explicit test-only builds:

```sh
python3 src/DncDbg.Remote.Target/build.py iossimulator-arm64 --test-faults
# From this fixture directory, build with the usual private/remote opt-ins plus:
# -p:EnableExperimentalTestFaults=true
```

Repeat with `android-arm64` for Android. Both the native `DNCDBG_TEST_FAULTS`
macro and the managed Debug-only property are required. The extra fixture
button asks the native owner to stall; the actual 20-second watchdog must exit
the app, and the adapter must clean its route/credentials within 35 seconds.
The fault export is absent from ordinary native helpers, and the button/import
are inactive without the managed opt-in. Release excludes both, even when
all experimental properties are supplied. Fault injection is not a DAP operation
and does not broaden the remote protocol.

Standard `dotnet new maui` app, now targeting `net11.0-ios` and
`net11.0-android`. This is a
**experimental debugger test fixture, not general mobile debugger support**. See the
[architecture, reference audit and blockers](../../docs/experimental-mobile-debugging.md).

`Microsoft.Maui.DevFlow.Agent` and `builder.AddMauiDevFlowAgent()` are Debug-only.
There is no central package management in this repository. A fixture-local
`global.json` selects SDK `11.0.100-rc.1.26425.128`; the debugger and portable
tests retain the root's .NET 10 SDK. The remote startup
probe additionally requires `-p:EnableExperimentalRemoteDebugging=true`; it is
absent from Release even if that property is supplied. The UI shows the actual
runtime blocker, with stable AutomationIds for scenario/reset/results.
The `MOBILE_BREAKPOINT` comment marks the real runtime proof's source location.

## Private target-native runtime gate (experimental)

This is a separate, explicit opt-in to a **private, version-fragile** CoreCLR
startup path. It must not be combined with a host startup/attach controller.
It supports only the pinned .NET 11 RC1 arm64 iOS simulator/Android emulator
runtime/DBI/DAC identities and hashes in `PrivateBootstrapPolicy`, not physical
devices. No target iOS DBI is loaded in macOS and no desktop DBI is substituted.

```sh
python3 tests/DncDbg.MauiTestApp/validate.py --platform ios \
  --device YOUR_DEDICATED_SIMULATOR_UDID \
  --build-developer-dir /Applications/Xcode-26.6.0.app/Contents/Developer \
  --private-bootstrap

python3 tests/DncDbg.MauiTestApp/validate.py --platform android \
  --device YOUR_DEDICATED_EMULATOR_SERIAL --agent-port 9223 --allow-fallback \
  --private-bootstrap
```

The runner builds the target-native helper, performs exact-build/platform/export
preflight and uses its **native-owned** authenticated versioned connection.
It binds the actual PE/portable-PDB sequence point at `MOBILE_BREAKPOINT`,
checks stack/source/line, reads `input=1` and `answer=42` through `ICorDebug`,
evaluates `answer + 1=43` with the limited int32 evaluator, continues and checks
normal UI completion. One persistent DevFlow batch owns all UI mutations,
reset assertions and the final screenshot, avoiding preview.12 lease conflicts.
Add `--private-disconnect` on a fresh fixture launch to drop the connection
while stopped and prove that the same scenario completes without a host continue.

**Exit 0 requires the real proof and successful native detach/cleanup.** Private
mode evidence, native phase logs, UI JSONL and screenshots are under ignored
`build/validation/mobile/private-bootstrap/`; failed runs preserve their exact
last native phase before uninstall. The caller still owns simulator/emulator
shutdown. Any red gate forbids opening a completion PR. Independent review and
full CI are required before merge even if both mobile gates pass.

The bootstrap runs `DllMain` / `CoreCLRCreateCordbObject3` target-side, never on a
managed execution owner. A stuck native call exits **this opted-in app** after
20 seconds; failed cleanup exits it rather than leaving a suspended runtime.
This intentionally narrow single-module/int32 backend is not full mobile
engine/DAP integration. MIT attribution is retained in
[`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md).

Portable/native negative checks (neither is mobile proof):

```sh
dotnet run --project tests/DncDbg.RemoteTests -c Release
python3 src/DncDbg.Remote.Target/build.py --test-host
python3 tests/DncDbg.MauiTestApp/test_validate.py
```

The POSIX mobile runner builds the host helper once, then invokes its DLL
directly (no `dotnet run` launcher). Every command and the diagnostic helpers
own a fresh process group; timeout, error and normal completion terminate any
remaining descendants, with bounded SIGTERM/SIGKILL escalation. The host-only
regression spawns a waiting parent/child and TCP listener, forces a timeout and
requires both PIDs and the listener gone within five seconds.
Start the shared DevFlow broker before the runner (`maui devflow broker start`).
A broker auto-started by a short-lived probe belongs to that probe's group and
is intentionally cleaned up with its other descendants. An existing broker
outside those groups is not terminated.

Android listener cleanup additionally requires EOF through the ADB forward:
ADB keeps accepting host TCP connections after the device listener closes, so
host `connect()` alone cannot establish that the native listener is still open.
After verification the runner removes only its owned forward.

Authenticated native JSON payload errors fail closed: the owner closes the
connection immediately (EOF, not an uncorrelated or missing reply), consumes an
outstanding stop once and cleans/detaches. A payload may have partially changed
state before validation throws, so it is not safe to continue the session.
Native contract tests require EOF or a correlated `MalformedPayload` within one
second, both idle and stopped, with exact-once cleanup.

## Portable protocol and actual desktop breakpoint test

From the repository root:

```sh
dotnet run --project tests/DncDbg.RemoteTests -- \
  --desktop tests/RemoteBreakpointApp/bin/Debug/net10.0/RemoteBreakpointApp.dll \
  tests/RemoteBreakpointApp/Program.cs build/validation/remote
```

This performs real breakpoint/stack/local/evaluation/continue and abrupt
disconnect tests against a separately launched desktop process. Contract-only
fakes are not used to substantiate those tests.

## Simulator / emulator UI and startup-probe validation

Use **dedicated, already booted** targets, never another investigation's device.
The runner refuses physical Android serials and checks exact simulator identity.
It installs and, in `finally`, terminates/uninstalls only this fixture and removes
only ADB mappings it added. It does not own the supplied device, so the caller
must shut down/delete any device it created, even if the runner fails.
`--replace-fixture` explicitly permits replacing a previous installation on that
dedicated target.

The installed workload set is `11.0.100-rc.1.26460.1`, with MAUI
`11.0.0-rc.1.26451.6`, iOS `26.5.12194-net11-rc.1`, and Android
`37.0.0-rc.1.2257`. It was already complete; no shared workload installation or
global SDK change was necessary. The iOS workload requires Xcode 26.6 for builds,
while Xcode 27 controls the simulator. No global selection is changed. The runner
builds from the fixture directory so the correct SDK is actually selected:

```sh
python3 tests/DncDbg.MauiTestApp/validate.py --platform ios \
  --device YOUR_DEDICATED_SIMULATOR_UDID \
  --build-developer-dir /Applications/Xcode-26.6.0.app/Contents/Developer \
  --public-attach-probe

ANDROID_HOME="$HOME/Library/Android/sdk" \
python3 tests/DncDbg.MauiTestApp/validate.py --platform android \
  --device emulator-5586 \
  --agent-port 9223 --allow-fallback
```

By default the runner does **not** set `UseMonoRuntime`. In this exact RC1 tuple,
iOS evaluates `UseMonoRuntime=false`; Android leaves it unset but selects
CoreCLR. Both running apps report CoreCLR `11.0.0-rc.1.26425.128`. Explicit
`--runtime mono` / `--runtime coreclr` overrides are separate experiments, saved
to separate evidence folders. Never treat an empty Android `UseMonoRuntime`
value as Mono. The Android minimum is API 24, as required by the .NET 11 pack.

The runner builds with public MSBuild options, records SDK/runtime-pack evidence,
deploys, executes `maui devflow diagnose`, `wait`, `agent status`, `ui tree`,
AutomationId-driven scenario/reset
assertions and a screenshot. One CLI batch avoids preview.12's cross-invocation
mutation-lease contention. Android uses `EmbedAssembliesIntoApk=true`, required
for direct `adb install` without fast-deploy assembly synchronization. It polls actual
agent reachability because broker registration can precede the HTTP listener.

**Without `--private-bootstrap`, exit 2 is intentional:** UI and capability-probe validation can pass, but
the managed-mobile-breakpoint release gate is still blocked. Failure before
that report is a validation failure, not an expected skip. The
`--allow-fallback` flag only permits collecting UI evidence after an unsuccessful
broker wait; it does not mark that wait successful or turn the release gate green.

Current default-runtime evidence and screenshots are written under ignored
`build/validation/mobile/net11-default/`. Previous .NET 10 results remain in the
parent folder as historical evidence, not the current fixture's runtime result.
Endpoint tokens are created with owner-only permissions and deleted in cleanup.
Do not post/check in descriptors or their contents.

### Public attach probe and remaining debugger gap

The .NET 11 iOS CoreCLR runtime pack now exists and the previous .NET 10
`NETSDK1082` failure no longer applies. The optional `--public-attach-probe`
executes the existing debugger's public `DebugEngine.Attach` against the exact
owned simulator process after UI validation. With the existing dbgshim dependency
it records `the runtime did not answer`. A separate early-startup probe with the
latest published macOS-arm64 dbgshim (`10.0.750501`) records
`CORDBG_E_DEBUG_COMPONENT_MISSING`; the default diagnostic IPC endpoint is also
unreachable. Neither has proven a working public startup path or mobile
breakpoint. The actual
device capability agent still reports unavailable execution rather than
pretending this attach succeeded. See the architecture audit for precise scope.

### Observed DevFlow Android limitation

On the tested read-only Pixel_API_37 arm64 emulator, default .NET 11 CoreCLR
starts the DevFlow
agent on fallback port **9223**, and explicit ADB-forwarded `agent status`, trees,
taps, queries and screenshots work. However, preview.12 **broker registration /
`wait` times out**, despite an explicit `adb -s SERIAL reverse tcp:19223 tcp:19223`.
`diagnose` reports that reverse as missing even though `adb reverse --list`
contains it. This discrepancy is recorded, not silently counted as a pass.
The broker was left running and unchanged; no global forwarding reset was used.

## Phase-specific public startup tests

The Debug fixture additionally writes `dncdbg-startup.json` in its private app
data directory. It contains only an allowlist of diagnostics/profiler environment
variables and public runtime/directory information, never the remote token or a
full environment dump. App shutdown/uninstall removes it.

For detailed red/configured evidence:

```sh
# Install only missing tools into the ignored worktree directory.
dotnet tool install --tool-path build/tools dotnet-dsrouter
# dotnet-trace must also be on PATH; use its current
# dotnet-sampled-thread-time profile, not the collect-linux cpu-sampling profile.

python3 tests/DncDbg.MauiTestApp/validate.py --platform ios \
  --device YOUR_OWNED_SIMULATOR_UDID \
  --build-developer-dir /Applications/Xcode-26.6.0.app/Contents/Developer \
  --startup-probes --shared-debugger-ipc --diagnostic-trace \
  --public-attach-probe

python3 tests/DncDbg.MauiTestApp/validate.py --platform android \
  --device YOUR_OWNED_EMULATOR_SERIAL --agent-port 9223 --allow-fallback \
  --startup-probes --diagnostic-trace
```

`--shared-debugger-ipc` uses the documented SDK
`AppBundleExtraOptions --setenv=TMPDIR=...` and the same environment for the
probe. `SIMCTL_CHILD_TMPDIR` alone was not honored. This fixes callback
notification timeout, not the target DBI's incompatible iOS-simulator/macOS ABI.
The directories are newly created beneath the worktree and removed in cleanup;
no system temp directory is created, scanned or used for scratch artifacts.

`--diagnostic-trace` configures the public mobile TCP diagnostic port before
runtime startup, routes it through the official `dotnet-dsrouter` CLI to a short
project-owned IPC socket, and collects a bounded EventPipe trace. Android uses a
Debug-only `AndroidEnvironment` item and a single owned ADB reverse. The iOS
negative UDS experiment is retained as `--diagnostic-transport uds`. Tracer,
router, socket and forward cleanup is tied to this run.

`--startup-probes` calls the documented dbgshim factories, records exact HRESULTs
and callback delivery, and exercises `ICLRDebuggingLibraryProvider3` against
target and desktop candidates. The provider requires the exact public Mach-O
runtime UUID: it refuses to substitute desktop DBI/DAC merely because the product
version is the same. Android PIDs are never passed to a host attach call.

The lower-level diagnostic commands are available independently:

```sh
dotnet run --project tests/DncDbg.RemoteTests -- \
  --load-probe PATH_TO_PACKAGED_DBI

dotnet run --project tests/DncDbg.RemoteTests -- \
  --startup-probe OWNED_HOST_VISIBLE_PID PATH_TO_LOADED_CORECLR report.json \
  --libraries PATH_TO_EXPLICIT_DEBUG_COMPONENTS
```

Use the simulator's host-visible PID only. The loader probe returns 2 on failure;
the startup phase probe returns 0 only for an initialized factory, otherwise 2.
Neither constitutes the separate managed-breakpoint/evaluate/continue gate.
JSONL records identify the last operation even if a native call fails. See the
architecture audit for red results and why no mobile debugger support is claimed.

### Completing the actual debugger gate

The private runtime gate above implements this narrow proof. A future general
mobile backend must continue to prove:

1. Establish a real `ICorDebug` session and receive real runtime callbacks.
2. Set a source breakpoint at `MOBILE_BREAKPOINT` before the scenario runs.
3. Use DevFlow's `run-scenario` AutomationId to invoke it.
4. Assert a managed breakpoint stop; inspect `input == 1`, `answer == 42`;
   evaluate `answer + 1 == 43`.
5. Continue and assert `result-status == "Result: 42"`.
6. Repeat with disconnect/cancellation while stopped; prove no suspended app,
   native references, callbacks, listeners, forwarded ports or helper processes
   remain. Test physical devices separately before making any device claim.

An application callback or counter assertion must never substitute for step 4.
