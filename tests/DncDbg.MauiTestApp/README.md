# Mobile experimental fixture

## One public test interface

Use the [mobile console entrypoint](../DncDbg.MobileTests/README.md) for all
current proof:

```sh
dotnet run --project tests/DncDbg.MobileTests -c Release -- \
  --platform ios --xcode-dir /Applications/Xcode_26.6.app/Contents/Developer \
  --output build/validation/mobile-ios-RUN

dotnet run --project tests/DncDbg.MobileTests -c Release -- \
  --platform android --android-abi x86_64 \
  --output build/validation/mobile-android-x64-RUN
```

The original Apple Silicon Android tuple uses `--platform android` without the
x64 option. Both Android tuples require an explicit Android SDK environment.
Exact SDK/workload/DevFlow, Xcode/runtime, emulator/image/ABI and NDK prerequisites
are in the runner guide and `../DncDbg.MobileTests/tuple.json`. Missing or changed
prerequisites fail closed; no physical devices or arbitrary runtime fallback.

The command builds the fixture, creates/boots a uniquely owned dedicated device,
executes all DAP/DevFlow/fault/direct-client suites, restores ordinary artifacts,
and cleans up even on failure. Repeat with the same options plus `--cleanup`
only for recovery of that run's ownership journal. Do not manually launch the
internal `validate.py`, `run_device.py` or `run_faults.py` helpers or manage their
device/broker lifecycle separately.

Shared debugger correctness first runs through the
[MAUI-independent native-host gate](../DncDbg.NativeHostTests/README.md).
Mobile adds packaging, startup, device routing and UI integration, not another
debugger implementation. Portable contracts live under `tests/`, never
`test-suite/Test*`. The [mobile DAP guide](../../docs/mobile-preview.md) describes
the public launch surface and supported inspection.

## Fixture and runtime boundary

This is a standard `dotnet new maui` fixture targeting `net11.0-ios` and
`net11.0-android`, **not general mobile debugger support**. Its local
`global.json` selects SDK `11.0.100-rc.1.26425.128`; portable solution projects
retain the root .NET 10 SDK. .NET 11 defaults to CoreCLR.

`Microsoft.Maui.DevFlow.Agent` and `AddMauiDevFlowAgent()` are Debug-only.
Experimental remote startup additionally requires
`EnableExperimentalRemoteDebugging=true`; private native startup also requires
`EnableExperimentalPrivateBootstrap=true`. The console runner owns these
properties. The target checks exact platform, architecture, runtime/DBI/DAC
build IDs and hashes, compiled model version and required private exports.

The native owner invokes private `DllMain` / `CoreCLRCreateCordbObject3` on its
independent thread. This is a private, version-fragile P/Invoke-started fixture,
not public mobile initialization compatibility, a pre-main constructor,
func-eval, or Visual Studio remote-COM interoperability. It must not be combined
with a host-side execution controller. No desktop DBI substitutes for mobile.
Reference history and limitations remain in
[experimental mobile debugging](../../docs/experimental-mobile-debugging.md)
and [generic remote architecture](../../docs/remote-architecture.md).

## Required actual-runtime assertions

The fixture has stable AutomationIds and source markers for:

- `MOBILE_BREAKPOINT`: actual source/stack, argument `1`, local `42`, safe
  evaluation `43`, strings, object fields, arrays, stepping and UI completion.
- `deep-scenario`: 150 non-inlined recursive frames, complete omitted/zero stack
  pages, `startFrame:128`, bounded pages and truthful totals. A shifted array
  created with lower bound 5 must report `UnsupportedArrayBounds`, not fabricated
  values, for indices 0/5/149/150.
- Arrays with 150 children, round-trip float/double values, special floating
  point values, per-stage/type exception filters, pause, default-policy restart,
  stable breakpoint IDs and stopped detach.
- `reload-load`/`reload-run`/`reload-unload`: identical embedded
  `DncDbg.ReloadFixture` PE bytes load in independent collectible contexts.
  Two simultaneous same-MVID/different-generation modules bind the original
  source request. Unloading one preserves the other; unloading the last returns
  verification to pending; reload hits again with the same DAP ID. Exactly one
  new/removed event pair per generation and no stale/duplicate stops.

DevFlow is a separate UI-only channel. Before querying or mutating UI, helpers
verify exact package, platform and actual DAP target PID. Android 9223 and broker
19223 are never debugger routes. A persistent DevFlow batch owns UI mutations,
reset assertions and screenshots. Debugger forwarding always uses dynamically
allocated, owned endpoints.

## Faults, Release isolation and evidence

The same entrypoint interrupts real completed installs and restart redeployment,
abruptly kills running/stopped targets and verifies endpoint/route cleanup.
Android also withholds CLI replies after real daemon forward allocation/rebind;
rollback must remove exactly the acquired mapping and preserve baseline routes.

Only the explicit Debug native `DNCDBG_TEST_FAULTS` build and managed
`EnableExperimentalTestFaults=true` property enable the watchdog fixture button.
Its stalled owner must actually exit after 20 seconds; DAP cleanup is bounded
to 35 seconds. The hook is not a DAP operation or broader protocol capability.
Normal helpers exclude its export. The runner always rebuilds ordinary helpers
and reruns the full DAP suite after faults.

Release enables none of the experimental paths even with every opt-in supplied.
The runner inspects evaluated constants/references **and actual APK/app bytes**
to reject the native agent, remote startup imports and DevFlow assets. Ordinary
desktop adapter Release artifacts are independently inspected too.

Evidence includes source head/dirty state, hashes, native phases, actual adapter
transcripts, DevFlow JSONL/screenshots and cleanup results under the run's ignored
`build/` folder. Runtime archives, credentials and compiled tools are excluded.
Exit zero requires **all gates and cleanup**; historical evidence and host fakes
cannot replace exact-head mobile CI. Keep PR #11 draft/unmerged pending full CI
and fresh independent review.
