# Mobile proof: one entrypoint

Run the [native-host architecture gate](../DncDbg.NativeHostTests/README.md)
first for shared agent/model/protocol/lifecycle changes. It exercises the same
C++ bootstrap through generic remote DAP and real TCP routing without a MAUI
workload or device. This entrypoint adds packaging/startup/platform-routing and
DevFlow/device-cleanup proof; mobile is a thin provider layer.

From the checkout, **one .NET 10 console command owns the complete run**. It is
part of `DncDbg.slnx`; it does not add mobile workloads to ordinary solution
builds. All low-level Python scripts are internal helpers, not separate required
user invocations. Python is retained for existing DAP framing/fault tests,
POSIX process groups and `simctl`/ADB/emulator CLI orchestration.

```sh
# Portable host contracts only, explicitly NOT real mobile proof:
dotnet run --project tests/DncDbg.MobileTests -c Release -- --platform contracts

# Complete iOS proof; choose the exact Xcode installation on your machine:
dotnet run --project tests/DncDbg.MobileTests -c Release -- \
  --platform ios --xcode-dir /Applications/Xcode_26.6.app/Contents/Developer \
  --output build/validation/mobile-ios-RUN

# Complete Android proof on Apple Silicon with an installed, accelerated SDK:
ANDROID_HOME="$HOME/Library/Android/sdk" \
dotnet run --project tests/DncDbg.MobileTests -c Release -- \
  --platform android --output build/validation/mobile-android-RUN

# Complete hosted Linux x64/KVM proof (separate pinned runtime and image):
dotnet run --project tests/DncDbg.MobileTests -c Release -- \
  --platform android --android-abi x86_64 --output build/validation/mobile-android-x64-RUN
```

Use a **new output directory** for each run. Exit **0** means every requested
gate **and cleanup** passed; **1** means a gate/cleanup failed; **2** is invalid
invocation or a missing prerequisite executable. An unavailable pinned runtime
is a failure, never a skip. On macOS the entrypoint uses `/usr/bin/python3` to
avoid user Python process-group shims; `DNCDBG_PYTHON` can override it.

## Exact prerequisites (no roll-forward/fallback)

[`tuple.json`](tuple.json) pins SDK `11.0.100-rc.1.26425.128`, workload set
`11.0.100-rc.1.26460.1`, MAUI `11.0.0-rc.1.26451.6` and both DevFlow CLI/agent
`0.1.0-preview.12.26421.1`. Install the SDKs with official `actions/setup-dotnet`
or Microsoft's installer. From `tests/DncDbg.MauiTestApp`, install
`maui-ios`/`maui-android` with `dotnet workload install maui-ios` or
`dotnet workload install maui-android`. The fixture's `global.json` already pins
the exact workload set; supplying `--version` there is rejected by the SDK.
Install `Microsoft.Maui.Cli` at the pinned version and put
its `maui` command on PATH. The fixture's `global.json` selects .NET 11; all
portable projects still select the root .NET 10 SDK.
The runner scopes the fixture's restore graph to the selected platform using
`DncDbgMobileTestPlatform`; an Android Linux job never requests iOS packs, and
an iOS job does not require the Android workload. Project references retain
their own target frameworks.

- **iOS:** separately pinned Apple Silicon (`iossimulator-arm64`, the default)
  and Intel (`--ios-architecture x64`, `iossimulator-x64`) tuples, Xcode **26.6 / 17F113**, simulator runtime
  **iOS 26.5 / 23F77**, device type **iPhone 17 Pro**. `DEVELOPER_DIR` is scoped;
  no global `xcode-select` change, runtime download or nearest-device fallback.
  Simulator artifacts are signed ad hoc (`CodesignKey=-`) with the Debug
  entitlements and no provisioning profile; a hosted runner needs no Apple
  developer certificate. Code signing/verification is not disabled. Physical
  device builds do not inherit this override and still require real signing.
- **Local Android arm64:** Apple Silicon/Hypervisor.framework, emulator **37.1.11**, image
  **`system-images;android-37.1;google_apis_ps16k;arm64-v8a` revision 8**, device
  definition `pixel_6`, NDK **29.0.14206865**, Java and SDK CLI tools.
  `ANDROID_HOME` is required. The exact image/NDK may be provisioned using the
  official `sdkmanager`; if its catalog no longer supplies the pinned revision,
  provision a verified retained SDK instead of substituting a newer image.
  The runner invokes `avdmanager`/`emulator` directly and requires acceleration.
  Console ports stay inside the official **5554–5584 even** range; each paired
  ADB port must also be free. No daemon reset or out-of-range port is used.
- **Hosted Android x64:** Linux x86_64, accessible **`/dev/kvm`**, emulator
  **37.2.12**, image
  **`system-images;android-37.1;google_apis_ps16k;x86_64` revision 9**,
  device `pixel_6`, NDK **29.0.14206865**, command-line tools **21.0**.
  `--android-abi x86_64` explicitly selects `net11.0-android` / `android-x64`;
  there is no ABI or software-emulation fallback. The workflow provisions with
  official Google archives plus `sdkmanager`, `avdmanager`, `emulator` and ADB.

[`android-archives.json`](android-archives.json) makes tuple-critical emulator
and system-image provisioning **immutable**, not merely checked after a floating
install. It records versioned official URLs, exact byte sizes and SHA-256 values
independently derived from downloads verified against the official catalog's
SHA-1. The internal provisioner checks the whole archive before extraction,
rejects path escapes/symlinks and revision/ABI mismatches, preserves executable
permissions, and registers the exact SDK package metadata. It fails closed if
an archive changes or disappears; it never asks `sdkmanager` for floating
`emulator` or image IDs. CI uses a fresh checkout-owned `build/android-sdk`,
does not overwrite the image's existing SDK, and discards verified download
archives after installing them to bound disk usage. Small URL/hash/revision
reports are uploaded; archives and extracted tools are not.

These OS/tool pins supplement, not replace, the exact **runtime/DBI/DAC
build IDs and code hashes** in `PrivateBootstrapPolicy.SupportedBuilds`. Actual
capability/preflight/DAP requests must pass that policy; filenames or product
versions alone are insufficient. Android x64 was derived from
`Microsoft.NETCore.App.Runtime.android-x64/11.0.0-rc.1.26425.128`:
the three ELF machine IDs are **62**, and the DBI exports both `DllMain` and
`CoreCLRCreateCordbObject3`. Its build IDs/full-file SHA-256 values are pinned
separately from local arm64 and desktop Linux, and the adapter verifies the
requested RID's architecture against the actual target capability.

## What the command owns and proves

1. Validate exact prerequisites, create/boot a uniquely named dedicated
   simulator/AVD (never borrow a supplied or shared device).
2. Build **ordinary Release**, reject a Release test-fault request, inspect its
   evaluated items/constants and actual bundle/APK, and require native helper,
   imports, remote/DevFlow references to be absent.
3. Build the ordinary Debug native helper and the same fixture app. Before
   starting/requiring a DevFlow broker, run independent DAP and both direct-client
   modes with DevFlow registration disabled for that owned launch and a real
   failing `maui` CLI shim reproducing unavailable broker authority. Reset/run/
   committed state come from the app-local authenticated fixture control channel,
   not UI automation. Breakpoints, inspection/evaluation, continue/detach,
   stale handles, resumed app usability and exact cleanup must all pass; any
   attempted broker invocation fails the regression.
4. Require the real broker and exact agent identity for the distinct
   **DevFlow-only** reset/run/assert/screenshot proof. This proof has no debugger
   transport and fails honestly on unavailable broker authority. Then run the
   existing explicitly **combined real DAP + DevFlow suite**, including 1/42/43,
   stepping, inspection, deep stacks/array
   bounds, collectible module generations, exception filters, pause, restart,
   stable IDs and stopped detach/UI screenshot.
5. Build explicit Debug-only native/managed watchdog fault injection and run
   **all** acquisition, redeployment, running/stopped target-death and watchdog
   faults (plus Android committed allocation/rebind cancellation races).
   Positive and fault drivers use the same copied fixture/collectible PE+PDB
   catalog, not thousands of unrelated infrastructure symbol fragments from
   the complete build directory.
6. Always restore the ordinary helper/package; require its fault export absent;
   rerun the full combined real suite. The independent direct-client **continue
   and stopped-disconnect** assertions remain mandatory, not weakened UI fallbacks.
7. Always terminate/uninstall the fixture, remove owned forwards, shut down and
   delete the dedicated device/AVD and scratch data, and stop only a broker this
   run started (not a pre-existing shared broker). Do not reset the ADB daemon.

Debugger forwarding is **dynamic** (`tcp:0` plus the target's ephemeral port).
Android **9223 is explicitly DevFlow UI-only**; broker reverse 19223 is also
UI-only. The Android suite records broker failure separately and requires the
explicit-port UI assertions/screenshots; it never relabels fallback as discovery
success. No debugger fixed port, global forward removal or device wipe.
The iOS UI port is assigned by the broker (the requested build port is not a
runtime guarantee); the broker's platform, process ID and run-scoped build
session must all match before its route is admitted. Before querying or
mutating any UI, both DAP/fault helpers verify exact package, platform and native
process ID. A reachable unrelated agent/ADB port is an error, never a fallback
UI target.
The Debug-only fixture control listener is separate from debugger execution and
DevFlow: three fixed operations (`reset`, `run`, `snapshot`) dispatch the existing
scenario handlers on the app's main thread. Its dynamic loopback endpoint has its
own 256-bit credential bound to the exact debugger PID/generation, bounded frames,
owner-only descriptor and idempotent teardown. It cannot evaluate arbitrary code
or control debugger stops. The native agent remains the only execution controller.
The important AutomationIds and same target app are unchanged. Ordinary Release
apps and desktop packages exclude the fixture control implementation.
Owned POSIX group liveness uses a kernel signal-zero probe; only ambiguous
macOS `EPERM` requires a targeted owned-process-group query. An ordinary completed CLI
command must not depend on scanning every process on a loaded hosted runner.
SDK processes explicitly disable the persistent MSBuild server
(`DOTNET_CLI_USE_MSBUILD_SERVER=false`, `MSBUILDUSESERVER=0`). Structured
`-getProperty/-getItem` output must remain exact JSON; the runner does not strip
or ignore a server-fallback diagnostic to turn an unhealthy evaluation green.

`state.json` journals ownership for recovery. The workflow repeats cleanup with
the **same command/options plus `--cleanup`** in an `always()` step after timeout/
cancellation. Keep this journal until cleanup succeeds. Never reuse another
run's journal. Device shutdown is no longer the caller's manual responsibility.
If the official emulator console returns an empty reply, cleanup must verify
the journaled PID's exact unique AVD name **and** console port before stopping
only that owned process. It performs no package/forward mutations through an
unverified serial, requires that serial to disappear, and removes only the
run-owned AVD directory. A nonempty different identity or unrelated PID remains
a hard failure; an empty reply never authorizes touching another device.

## Evidence and CI requirements

The run writes an allowlisted `evidence/` folder: exact checked-out head SHA
(the mobile workflow explicitly checks out the PR head), dirty flag, pins,
per-file SHA-256 manifest, native/adapter/DevFlow logs, full DAP reports,
screenshots and cleanup result. Credentials, tools, compiled assets and AVD data
are excluded; credential-shaped logs fail closed. Limits: **8 MiB/file,
100 MiB/run**, 50-minute CI jobs, three-minute recovery, seven-day artifacts.
Failed gates preserve evidence, not successful-looking partial results.

Build budgets are separate from debugger/RPC and fault deadlines. The first
hosted iOS attempt exceeded 600 seconds in a cold Release build with no useful
quiet output. Release packaging now has a **20-minute build-only bound**;
Debug builds retain 10 minutes, and all debugger operation/fault timeouts and
assertions are unchanged. Builds use minimal verbosity written to a live log,
30-second last-output/elapsed progress, a duration/timeout/exit status JSON and
a binary log with project imports disabled. Secret-bearing environment names
are removed from build children before binary logging. These diagnostics are
subject to the same 8 MiB/file and 100 MiB/run evidence bounds. A build timeout
remains a red gate, never a successful skip.

[`mobile.yml`](../../.github/workflows/mobile.yml) runs unconditionally for
`pull_request` targeting `main` and pushes to `main` (no path filters or
`continue-on-error`):

- **`Mobile iOS simulator (pinned)`** uses hosted **`macos-26-intel`** and explicitly
  selects `--ios-architecture x64`. It installs the exact
  SDK/workload and requires the pinned Xcode/runtime already listed by the
  [official runner image](https://github.com/actions/runner-images/blob/main/images/macos/macos-26-arm64-Readme.md).
  Image rotation/removal is a red infrastructure gate, never a silent skip.
  The x64 CoreCLR/DBI/DAC UUIDs, code hashes and required exports are independent
  of the retained ARM64 profile. Post-link sealing measures the final signed
  executable with its linked x64 agent, not the intermediate static archive.
  The complete debugger, direct app-control, strict DevFlow UI and fault/cleanup
  sequence is identical for both tuples. Selecting the wrong host architecture
  fails before SDK/device provisioning; there is no silent ABI fallback.
  Apple host tools retain the runner's native per-user temporary environment
  for CoreSimulator coordination. The selected simulator, work/evidence paths
  and ownership journal remain run-owned; no global simulator reset is used.
  The separately bounded iOS cold-boot provisioning budget is 600 seconds
  across boot, bootstatus and an independent exact-UDID/name/runtime `Booted`
  inventory check. Hosted app-free measurements reached 348-414 seconds before
  any target build; this is not a debugger-operation timeout. Successful
  bootstatus alone, wrong ownership, or a zero exit after the deadline fails.
  `ios-provisioning.json` records the duration/budget and readiness with
  `complete:false`; only the full app/debugger/UI/fault suite qualifies iOS.
  Owned simulator cleanup has one aggregate 60-second `simctl` budget, based
  on observed 30.23-second inventory latency. It never authorizes other devices
  or shared service resets. Android boot/cleanup and all app, debugger, RPC,
  fault, build and assertion deadlines remain unchanged; no retries are added.
- **`Mobile Android emulator (pinned)`** uses hosted **`ubuntu-24.04`**,
  explicitly enables/checks KVM and runs the **entire same** unified suite with
  the separate x64 pins. It does not merely cross-build an APK, run host fakes,
  or pass an arm64 image to x64 KVM. Immutable archive/hash drift fails provisioning;
  emulator boot failure or missing KVM fails the job.
  Before installing the isolated pinned SDK, the job records disk usage and
  reclaims only the unused preinstalled SDK's `system-images`, `ndk` and
  `emulator` directories, guarded to the exact GitHub-hosted SDK root.
  Command-line/platform tools and the run's selected SDK are never removed.
  This avoids keeping two large image/NDK installations; archive pins and
  checksum/size/property validation are unchanged.

[.NET 11 Android supports `android-x64` CoreCLR](https://github.com/dotnet/android/blob/main/Documentation/docs-mobile/building-apps/build-properties.md).
[Android requires matching host/image architecture for acceleration](https://developer.android.com/studio/run/emulator-acceleration);
the new Linux/image/agent/runtime tuple satisfies that without changing local
arm64 admission. No permanently queued self-hosted job remains.
Hosted capacity cancellation is infrastructure red, not a successful skip.
Generic desktop CI/fakes and historical local evidence **do not make mobile green**.

The portable DAP contracts use two independent process batches on both host
architectures. Every discovered case runs once, either failure makes the command
fail, and children remain in the owned process group for timeout cleanup.
The existing 180-second aggregate budget and individual protocol/fault deadlines
are unchanged; no mobile device proof is replaced by these portable contracts.
Desktop CI selects Python 3.13.15 explicitly through the existing `DNCDBG_PYTHON`
facade override rather than using Xcode's bundled interpreter for every fake
device-tool process. This does not change application SDK/runtime pins.
The portable suite validates complete launch configurations against the public
Draft 2020-12 schema using the fully version/hash-locked test-only closure in
`tests/DncDbg.RemoteTests/requirements.txt`. Binary hashes cover CPython 3.13 on
Linux glibc x64/arm64 and macOS x64/arm64; Windows CI does not run the POSIX
portable suite. The suite records and verifies its exact installed package
inventory, excluding only bootstrap installer tools. For local contracts, use
CPython 3.13 in an isolated environment and select that interpreter, for example:

```sh
python3 -m venv build/contract-python
build/contract-python/bin/python -m pip install --require-hashes --no-deps --only-binary=:all: -r tests/DncDbg.RemoteTests/requirements.txt
DNCDBG_PYTHON="$PWD/build/contract-python/bin/python" dotnet run --project tests/DncDbg.MobileTests -c Release -- --platform contracts
```

The test facade removes ambient Python overrides before starting the selected
interpreter, disables user-site imports, and does not propagate the selector
to unrelated child processes. SDK/worker environments repeat that cleanup.
Dependency verification rejects duplicate distribution names and paths outside
the venv, checks module files against their installed wheel records before
importing them, and records the actual JSON-schema/RPDS import provenance.
Foreign `PYTHONPATH` packages are failures, never accepted as locked metadata.

Fixture device/package commands load the small `fake_mobile_tools.py` CLI,
not the complete unittest driver and its fake-server dependencies on each
invocation. Command transcripts, package mutation negatives and delay barriers
are unchanged; real device proofs never use these fake tools.

Require **both named mobile jobs** in branch protection alongside all existing
Debugger Windows/Linux/macOS, Docker and VS Code checks. Workflow triggers create
checks but do not themselves configure repository branch protection. Never merge
on generic CI alone; exact-head mobile CI and fresh independent review are
mandatory. This PR remains draft/unmerged.

## Why native code exists

The host DNCDbg adapter is C# 14/.NET 10. Explicit **RID-specific target
deployment packages** and Debug mobile fixtures compile/link `src/DncDbg.Remote.Target`. Low-level
pre-managed startup hooks and private DBI/`ICorDebug` initialization require
target-native ABI access independent of managed execution. This fixture requests
activation through a P/Invoke (it does not claim a native pre-main constructor);
the owner then runs on an independent native thread while managed threads
(including the UI) can be suspended. A managed owner cannot reliably resume
itself. This is not a return to a C++
desktop debugger, a native parity fixture, or authorized use of proprietary
Visual Studio proxies. Ordinary Release adapter/local desktop publish artifacts
are inspected in CI; existing stock CLR/dbgshim/PDB-reader native dependencies
are still allowed.
