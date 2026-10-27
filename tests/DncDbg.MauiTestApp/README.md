# Mobile experimental fixture

Standard `dotnet new maui` app, restricted to iOS and Android. This is a
**debugger test fixture, not a working mobile debugger**. See the
[architecture, reference audit and blockers](../../docs/experimental-mobile-debugging.md).

`Microsoft.Maui.DevFlow.Agent` and `builder.AddMauiDevFlowAgent()` are Debug-only.
There is no central package management in this repository. The remote startup
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

On the validated machine the active workload requires Xcode 26.6 for builds,
while Xcode 27 controls the simulator. No global selection is changed:

```sh
python3 tests/DncDbg.MauiTestApp/validate.py --platform ios \
  --device YOUR_DEDICATED_SIMULATOR_UDID \
  --build-developer-dir /Applications/Xcode-26.6.0.app/Contents/Developer

ANDROID_HOME="$HOME/Library/Android/sdk" \
python3 tests/DncDbg.MauiTestApp/validate.py --platform android \
  --device emulator-5586 --coreclr \
  --agent-port 9223 --allow-fallback
```

The runner builds with public MSBuild options, deploys, executes `maui devflow
diagnose`, `wait`, `agent status`, `ui tree`, AutomationId-driven scenario/reset
assertions and a screenshot. One CLI batch avoids preview.12's cross-invocation
mutation-lease contention. Android uses `EmbedAssembliesIntoApk=true`, required
for direct `adb install` without fast-deploy assembly synchronization.

**Exit 2 is intentional:** UI and capability-probe validation can pass, but
the managed-mobile-breakpoint release gate is still blocked. Failure before
that report is a validation failure, not an expected skip. The
`--allow-fallback` flag only permits collecting UI evidence after an unsuccessful
broker wait; it does not mark that wait successful or turn the release gate green.

Evidence and screenshots are written under ignored `build/validation/mobile/`.
Endpoint tokens are created with owner-only permissions and deleted in cleanup.
Do not post/check in descriptors or their contents.

### Observed DevFlow Android limitation

On the tested read-only Pixel_API_37 arm64 emulator, CoreCLR starts the DevFlow
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
