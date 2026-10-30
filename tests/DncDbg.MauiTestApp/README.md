# Mobile target fixture

Use the [single mobile console entrypoint](../DncDbg.MobileTests/README.md):

```sh
dotnet run --project tests/DncDbg.MobileTests -c Release -- \
  --platform ios --xcode-dir /Applications/Xcode_26.6.app/Contents/Developer \
  --output build/validation/mobile-ios-UNIQUE

ANDROID_HOME="$HOME/Library/Android/sdk" \
dotnet run --project tests/DncDbg.MobileTests -c Release -- \
  --platform android --output build/validation/mobile-android-UNIQUE

# Hosted Linux x64/KVM uses the separate exact tuple:
dotnet run --project tests/DncDbg.MobileTests -c Release -- \
  --platform android --android-abi x86_64 \
  --output build/validation/mobile-android-x64-UNIQUE
```

The facade owns builds, dedicated simulator/emulator lifecycle, DAP/DevFlow,
fault/direct-client proofs, ordinary restoration and exact cleanup. Low-level
scripts are internal; do not invoke them as separate public test commands.
The portable solution requires no mobile workload.

The fixture uses the exact upstream .NET 11 RC1 SDK/workload/MAUI and DevFlow
versions in `../DncDbg.MobileTests/tuple.json`. Those actual dependency versions
are not renamed or represented as a stable upstream release. .NET 11 CoreCLR
is required; Mono/NativeAOT and unknown private ABI builds fail closed.

Debug always includes `TargetAgentStartup`, the explicit RID agent/metadata and
DevFlow. There is no remote feature opt-in. The startup path validates actual
runtime/DBI/DAC identities/code hashes, model, agent build ID and required
private exports, then publishes the common generation-owned target manifest.
Missing deployment generation is an error. The owner is an independent native
thread so it can resume stopped managed/UI threads.

Internal `DncDbgTestFaults=true` adds the fixture watchdog operation only in Debug.
Release rejects test faults and includes neither target agent/startup imports nor
DevFlow. Separate normal desktop artifacts also exclude target-native binaries
and test hooks; host-side supported remote providers are allowed.

The full suite checks real source breakpoints and 1/42/43, UTF-16 values, arrays/
objects/deep stacks, safe evaluation, steps, exceptions, pause, collectible module
generations, restart, stale handles, stopped detach/UI usability, fault cleanup
and matching DevFlow identity. A screenshot or handshake alone is not debugger
proof. Shared correctness also runs through the workload-independent native-host
and generic Linux device suites.

Debugger-only launches of this same app use an owned, single-use fixture mode
marker. They do not register a DevFlow agent and use the Debug-only authenticated
loopback `reset`/`run`/`snapshot` control channel to dispatch the existing scenario
handlers. This is fixture state control, not a second debugger or a UI-automation
fallback. The DAP/native direct proofs deliberately make the host broker CLI
unavailable and must still prove 1/42/43, continue/detach and exact cleanup.
The separate DevFlow-only and explicitly combined DAP/DevFlow proofs retain the
same AutomationIds and require a real broker/agent; their UI failures remain red.

Physical Android USB/paired-TLS and generic externally managed Apple routes use
the same profile/manifest/protocol. Their RID assets can be built, but this
fixture's automated facade executes simulators/emulators only. No physical
hardware has been executed or claimed validated. Physical Apple signing,
entitlements and actual debugger behavior require qualification.

See [public configuration/support](../../docs/remote-devices.md) and
[generic ownership architecture](../../docs/remote-architecture.md).
