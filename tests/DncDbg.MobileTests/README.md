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

- **iOS:** Apple Silicon, Xcode **26.6 / 17F113**, simulator runtime
  **iOS 26.5 / 23F77**, device type **iPhone 17 Pro**. `DEVELOPER_DIR` is scoped;
  no global `xcode-select` change, runtime download or nearest-device fallback.
- **Local Android arm64:** Apple Silicon/Hypervisor.framework, emulator **37.1.11**, image
  **`system-images;android-37.1;google_apis_ps16k;arm64-v8a` revision 8**, device
  definition `pixel_6`, NDK **29.0.14206865**, Java and SDK CLI tools.
  `ANDROID_HOME` is required. The exact image/NDK may be provisioned using the
  official `sdkmanager`; if its catalog no longer supplies the pinned revision,
  provision a verified retained SDK instead of substituting a newer image.
  The runner invokes `avdmanager`/`emulator` directly and requires acceleration.
- **Hosted Android x64:** Linux x86_64, accessible **`/dev/kvm`**, emulator
  **37.2.12**, image
  **`system-images;android-37.1;google_apis_ps16k;x86_64` revision 9**,
  device `pixel_6`, NDK **29.0.14206865**, command-line tools **21.0**.
  `--android-abi x86_64` explicitly selects `net11.0-android` / `android-x64`;
  there is no ABI or software-emulation fallback. The workflow provisions with
  official `sdkmanager`, `avdmanager`, `emulator` and ADB only.

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
2. Build **Release with all experimental/fault opt-ins supplied**, inspect its
   evaluated items/constants and actual bundle/APK, and require native helper,
   imports, remote/DevFlow references to be absent.
3. Build the ordinary Debug native helper and fixture, run the **full real DAP +
   DevFlow suite**, including 1/42/43, stepping, inspection, deep stacks/array
   bounds, collectible module generations, exception filters, pause, restart,
   stable IDs and stopped detach/UI screenshot.
4. Build explicit Debug-only native/managed watchdog fault injection and run
   **all** acquisition, redeployment, running/stopped target-death and watchdog
   faults (plus Android committed allocation/rebind cancellation races).
5. Always restore the ordinary helper/package; require its fault export absent;
   rerun the full real suite. Retain the original direct-client **continue and
   stopped-disconnect** proofs on each platform as additional regressions.
6. Always terminate/uninstall the fixture, remove owned forwards, shut down and
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
Owned POSIX group liveness uses a kernel signal-zero probe; only ambiguous
macOS `EPERM` requires a global process snapshot. An ordinary completed CLI
command must not depend on scanning every process on a loaded hosted runner.

`state.json` journals ownership for recovery. The workflow repeats cleanup with
the **same command/options plus `--cleanup`** in an `always()` step after timeout/
cancellation. Keep this journal until cleanup succeeds. Never reuse another
run's journal. Device shutdown is no longer the caller's manual responsibility.

## Evidence and CI requirements

The run writes an allowlisted `evidence/` folder: exact checked-out head SHA
(the mobile workflow explicitly checks out the PR head), dirty flag, pins,
per-file SHA-256 manifest, native/adapter/DevFlow logs, full DAP reports,
screenshots and cleanup result. Credentials, tools, compiled assets and AVD data
are excluded; credential-shaped logs fail closed. Limits: **8 MiB/file,
100 MiB/run**, 50-minute CI jobs, three-minute recovery, seven-day artifacts.
Failed gates preserve evidence, not successful-looking partial results.

[`mobile.yml`](../../.github/workflows/mobile.yml) runs unconditionally for
`pull_request` targeting `main` and pushes to `main` (no path filters or
`continue-on-error`):

- **`Mobile iOS simulator (pinned)`** uses hosted **`macos-26`**, installs the exact
  SDK/workload and requires the pinned Xcode/runtime already listed by the
  [official runner image](https://github.com/actions/runner-images/blob/main/images/macos/macos-26-arm64-Readme.md).
  Image rotation/removal is a red infrastructure gate, never a silent skip.
- **`Mobile Android emulator (pinned)`** uses hosted **`ubuntu-24.04`**,
  explicitly enables/checks KVM and runs the **entire same** unified suite with
  the separate x64 pins. It does not merely cross-build an APK, run host fakes,
  or pass an arm64 image to x64 KVM. Catalog revision drift fails preflight;
  emulator boot failure or missing KVM fails the job.

[.NET 11 Android supports `android-x64` CoreCLR](https://github.com/dotnet/android/blob/main/Documentation/docs-mobile/building-apps/build-properties.md).
[Android requires matching host/image architecture for acceleration](https://developer.android.com/studio/run/emulator-acceleration);
the new Linux/image/agent/runtime tuple satisfies that without changing local
arm64 admission. No permanently queued self-hosted job remains.
Hosted capacity cancellation is infrastructure red, not a successful skip.
Generic desktop CI/fakes and historical local evidence **do not make mobile green**.

Require **both named mobile jobs** in branch protection alongside all existing
Debugger Windows/Linux/macOS, Docker and VS Code checks. Workflow triggers create
checks but do not themselves configure repository branch protection. Never merge
on generic CI alone; exact-head mobile CI and fresh independent review are
mandatory. This PR remains draft/unmerged.

## Why native code exists

Normal DNCDbg is C# 14/.NET 10. Only the opted-in **Debug mobile/native-host
experimental fixtures** compile/link `src/DncDbg.Remote.Target`. Low-level
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
