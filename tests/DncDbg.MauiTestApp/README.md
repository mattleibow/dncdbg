# Mobile experimental fixture

Standard `dotnet new maui` app, now targeting `net11.0-ios` and
`net11.0-android`. This is a
**debugger test fixture, not a working mobile debugger**. See the
[architecture, reference audit and blockers](../../docs/experimental-mobile-debugging.md).

`Microsoft.Maui.DevFlow.Agent` and `builder.AddMauiDevFlowAgent()` are Debug-only.
There is no central package management in this repository. A fixture-local
`global.json` selects SDK `11.0.100-rc.1.26425.128`; the debugger and portable
tests retain the root's .NET 10 SDK. The remote startup
probe additionally requires `-p:EnableExperimentalRemoteDebugging=true`; it is
absent from Release even if that property is supplied. The UI shows the actual
runtime blocker, with stable AutomationIds for scenario/reset/results.
The `MOBILE_BREAKPOINT` comment marks the intended **future** debugger test site.

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

**Exit 2 is intentional:** UI and capability-probe validation can pass, but
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

### Completing the actual debugger gate

Once a public native startup/backend is available, the mobile test must:

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
