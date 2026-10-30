# Remote targets and device provisioning

The supported public configuration is `launch.remote`; no feature opt-in is
required. Target ownership, debugger behavior and transport are independent.
Schema: [`remote-launch.schema.json`](remote-launch.schema.json).
Architecture: [remote-architecture.md](remote-architecture.md).

## Support matrix

| Target | Configuration | Automated execution gate | Physical hardware |
|---|---|---|---|
| Linux x64 | `nativeHost`, `commands`, `ssh` | Hosted native host + required real Docker device | Not a hardware claim |
| Linux arm64 | Same contracts/assets | Local Docker/device proof | Not a hardware claim |
| macOS arm64 | `nativeHost` | Local and hosted native host | Host machine |
| Android x64 | `android`, ADB | Hosted Ubuntu 24.04/KVM exact emulator | Emulator only |
| Android arm64 | `android`, ADB | Local exact emulator | USB/paired-TLS route implemented; **not executed on physical hardware** |
| iOS simulator arm64 | `appleSimulator` | Local exact simulator; hosted macOS gate | Simulator only |
| iOS arm64 device | `appleDevice` + structured commands + secure tunnel | Exact SDK runtime images/DBI exports and RID agent build checked | **Not executed; signing, entitlements and real-device debugger behavior require qualification** |

This matrix describes implemented routes and required proofs, not the live state
of a particular commit. Consult exact-head CI/evidence. A capacity cancellation,
missing KVM/image/runtime, cleanup error or failed prerequisite is red, never a
skip. A device transport contract/fake does not count as physical-device execution.

Remote target images are **exact-build pinned**. Current target runtime and mobile
SDK/workload packages include their actual upstream .NET 11 RC1 versions; those
versions are not relabeled or presented as a stable Microsoft runtime release.
The host API/configuration is stable; CoreCLR private DBI startup remains a private,
version-fragile implementation dependency. Mono, NativeAOT, arbitrary framework
versions and unlisted OS/architecture combinations are rejected. Adding another
image requires exact build IDs/hashes/exports plus qualification, not a manifest
override or roll-forward switch.

## Common launch settings

The version-1 target descriptor is platform-neutral:

| Manifest field | Admission contract |
|---|---|
| `SchemaVersion`, `SessionId` | Version 1, exact new 32-hex generation |
| `Profile` | Versioned OS/architecture, exact runtime/DBI/DAC identities and code hashes, agent identity, capabilities |
| `Profile.Agent` | Name, explicit RID, protocol/model versions, build ID and SHA-256 |
| `Process` | Positive actual PID, payload identity and generation ownership |
| `Endpoint` | Ephemeral port, 64-hex credential, matching process/generation |
| `Transport` | Upstream host and encryption requirement |

It is bounded to 128 KiB. Expected profiles come from trusted deployment metadata,
not the observed descriptor itself. Unknown schema, changed identities/capabilities
or stale process/generation fail before debugger operation.

```json
{
  "request": "launch",
  "remote": {
    "assetsPath": "/absolute/matching-user-dlls-and-portable-pdbs",
    "outputPath": "/absolute/owned-debug-session-output",
    "startupTimeoutSeconds": 90,
    "target": { "provider": "nativeHost", "nativeHost": {
      "runtimeDirectory": "/opt/dotnet/shared/Microsoft.NETCore.App/11.0.0-rc.1.26425.128",
      "hostPath": "/opt/device-bundle/dncdbg-target-host",
      "agentPath": "/opt/device-bundle/libdncdbg_target.so",
      "assemblyPath": "/opt/device-bundle/payload/Service.dll",
      "runtimeConfigPath": "/opt/device-bundle/payload/Service.runtimeconfig.json",
      "mode": "application",
      "arguments": ["--device", "sensor-1"],
      "environment": { "DEVICE_CONFIG": "/etc/vendor/sensor.json" }
    }},
    "tunnel": { "provider": "loopback" }
  }
}
```

Select exactly one typed target member. `assetsPath` contains **matching user**
PE DLLs and portable PDBs, plus `dncdbg-agent.json` for application providers.
MVID, CodeView/PDB identity and source checksums are checked. Do not copy entire
framework output trees as the symbol catalog. Authenticated frames remain bounded
to 256 KiB; symbol catalogs are chunked. Source breakpoints preserve their request
identity across module unload/reload and restart; stale value/frame handles fail.

For builds on a different machine/container, `remote.sourceFileMap` explicitly
maps absolute build prefixes to absolute host source directories, for example
`{ "/workspace": "/home/me/sensor-source" }`. Longest prefixes win and path
boundaries are enforced. Both uploaded sequence points and returned stack sources
use the mapped paths; exact PDB/source checksum validation is unchanged.

Startup timeout is 1–300 seconds (default 60), including target acquisition and
symbol configuration. Each host RPC has a 30-second deadline, including its
queue wait; a timed-out partial exchange discards the connection rather than
reusing an uncorrelated stream. Watchdog and cleanup deadlines are separate.
The mobile test facade allows a 20-minute Release build and a
10-minute Debug build with minimal live logs, progress, status JSON and sanitized
binlogs; that does not relax debugger operation/fault deadlines.

`target.nativeHost.environment` and SSH/controller `start.environment` are literal
validated application values. Names must be portable environment identifiers;
NULs and every case-insensitive `DNCDBG_` name are rejected before launch.
The host owns that namespace, including generation and credentials. User values
are applied first, followed by protected internal values; a collision is an
explicit error, never a silently dropped value or credential override.

Do not combine remote with desktop attach/build/env/JIT/source-map/terminal,
`noDebug` or `stopAtEntry` options. Unsupported requests fail explicitly. Safe
remote evaluation supports the generated model's operations; arbitrary managed
func-eval is not implemented.

## SSH target

Provision a dedicated account, known host key and the product target controller
before debugging. Use OpenSSH 9+ (SFTP-based `scp`); do not disable host checking,
enable legacy SCP, use agent forwarding or accept a password prompt.
The fixed controller apphost requires .NET 10 on the device; it independently
launches the exact deployment runtime, which need not be the controller runtime.

Replace the `target` and `tunnel` from the common example:

```json
{
  "target": { "provider": "ssh", "ssh": {
    "host": "sensor.example",
    "port": 22,
    "user": "debugger",
    "identityFile": "/home/me/.ssh/sensor-debug",
    "knownHostsFile": "/home/me/.ssh/sensor-known-hosts",
    "controllerPath": "/opt/vendor/control/dncdbg-target-control",
    "workspaceRoot": "/var/lib/vendor-debug",
    "bundlePath": "/home/me/qualified-linux-arm64-bundle",
    "profilePath": "/home/me/qualified-linux-arm64-bundle/target-profile.json",
    "start": {
      "runtimeDirectory": "{targetDirectory}/bundle/runtime/shared/Microsoft.NETCore.App/11.0.0-rc.1.26425.128",
      "hostPath": "{targetDirectory}/bundle/dncdbg-target-host",
      "agentPath": "{targetDirectory}/bundle/libdncdbg_target.so",
      "assemblyPath": "{targetDirectory}/bundle/payload/Service.dll",
      "runtimeConfigPath": "{targetDirectory}/bundle/payload/Service.runtimeconfig.json"
    }
  }},
  "tunnel": { "provider": "ssh" }
}
```

Controller/workspace paths are safe absolute POSIX paths, not shell fragments.
The workspace must be dedicated and protected from other users. Deployment is
copied into a new generation directory. The trusted local integrity inventory
hash is sent through the encrypted control channel and all deployed files are
verified before process startup. A new ephemeral local listener bridges built-in
`ssh -W` to only the manifest's target loopback port.

## Generic commands, Docker and IoT vendors

`commands` accepts `profilePath`, optional `bundlePath`, `targetDirectory`,
`deploy` array, optional `start`, required `readManifest`, `stop`, `detach`, `cleanup`,
and `observe`. Owned starts require stop/detach/observation; deployments require cleanup.
Each action is a **structured command**:

```json
{
  "executable": "/opt/vendor/bin/device-control",
  "arguments": ["manifest", "--device", "sensor-1", "--generation", "{sessionId}"],
  "environment": { "VENDOR_CONFIG": "/etc/vendor/devices.json" },
  "timeoutSeconds": 30
}
```

Optional `workingDirectory` and `input` are supported. Arguments/env are passed
literally through process APIs, never a shell. Shell executables/command strings
are rejected. This is trusted executable configuration, **not a sandbox**.
Do not put secrets in argv; use protected environment/input/credential files.
Commands have 1–300 second bounds, bounded output and owned cancellation/reaping.

Templates expand each string, not an assembled command line:
`{sessionId}`, `{sessionDirectory}` (host audit directory), `{targetDirectory}`
(device workspace), `{bundlePath}`, `{profilePath}`, `{profileJson}`,
`{bundleIntegritySha256}`, and `{processId}` after manifest admission. Device
directories are never inferred from host paths.

The product controller reads one JSON request from stdin. Operations are
`prepare`, `start`, `manifest`, `observe`, `stop`, `detach`, `cleanup`, `relay`.
A request carries `Operation`, `WorkspaceRoot`, `SessionId`; start also carries
`Profile`, `Start`, `IntegritySha256`. `observe` returns
`SessionId`, `ProcessId`, `HasExited`, `ExitCode`. A non-exited process has a null
exit code; exited means an actual observed code, never a guessed zero.

For Docker, the executable is `docker`, argv is
`["exec","-i","EXACT_OWNED_CONTAINER","/opt/control/dncdbg-target-control"]`,
and stdin is the controller request. Deployment uses literal `docker cp` argv.
`tunnel.provider: "stdio"` runs the same controller with a `relay` request as its
initial stdin line, then forwards raw binary stdin/stdout with half-close support.
Use `"security": "local"` only for a trusted local process/daemon transport.
The real [device proof](../tests/DncDbg.DeviceTests/README.md) is a complete,
executable command-provider example, not Android-specific pseudocode.

An IoT vendor supplies:

1. A qualified runtime/DBI/DAC and matching explicit RID agent package.
2. The version-1 target profile/integrity inventory from its build pipeline.
3. Product host/controller or equivalent structured generation-aware commands.
4. A trusted SSH, USB or authenticated encrypted managed/stdio route.
5. Actual process observation, exact stop/reap/cleanup, and autonomous deployment
   cleanup after a detached process eventually exits.

No APK/application ID, ADB, simctl, Android paths or emulator lifecycle is required.

## Android

```json
{
  "target": { "provider": "android", "android": {
    "applicationId": "com.vendor.sensor",
    "deviceId": "EXACT_USB_SERIAL",
    "appPath": "/absolute/com.vendor.sensor-Signed.apk",
    "runtimeIdentifier": "android-arm64",
    "connectionSecurity": "usb",
    "replaceExisting": false,
    "uninstallOnDisconnect": false
  }},
  "tunnel": { "provider": "adb" }
}
```

Use the exact running emulator serial and `android-x64` for the hosted x64 tuple.
USB serials do not have to begin with `emulator-`. Network devices require
`connectionSecurity: "pairedTls"` and actual official ADB pairing; plaintext
`adb tcpip` and remote plaintext ADB servers are rejected.

The debug application must package the RID agent, matching agent metadata and
exact CoreCLR/DBI/DAC images, and publish its owned generation-bound manifest.
This does not inject a debugger into an arbitrary store/Release application.
APKs must embed managed assemblies, not depend on SDK Fast Deployment. Installation
replacement is explicit and journaled; unrelated apps/forwardings are preserved.

### Host-trusted final mobile packages

`assetsPath` must contain the **post-link** `dncdbg-agent.json` and
`dncdbg-package.json`, produced from the final signed SDK installable output.
Trust/distribute those host-side files separately from the device and package;
editing an app's embedded metadata or reporting a build ID cannot replace this
authority. The fixture seals Debug output automatically after the SDK build.
For vendor builds, run the product tool explicitly after final linking/signing:

```bash
dotnet run --project src/DncDbg.TargetControl -c Release -- \
  seal-mobile android-arm64 /absolute/Sensor-Signed.apk \
  /trusted/native-build/dncdbg-agent.json /trusted/final-assets 37.0.0
dotnet run --project src/DncDbg.TargetControl -c Release -- \
  seal-mobile ios-arm64 /absolute/Sensor.app \
  /trusted/native-build/dncdbg-agent.json /trusted/final-assets ""
dotnet run --project src/DncDbg.TargetControl -c Release -- \
  verify-mobile /absolute/Sensor.app /trusted/final-assets/dncdbg-agent.json
```

Use the exact Android build-tools version selected by your SDK; it is recorded
and required for verification. Official `aapt2` and Java `apksigner.jar verify`
measure application/version and validate the signing certificate. Before install,
the provider checks the whole APK digest and the exact ABI-specific
`lib/{arm64-v8a,x86_64}/libdncdbg_target.so` bytes against the trusted native
build digest. Before launch it pulls the package-manager-resolved installed base
APK and repeats **all** checks: its application, signature, version, native entry
and complete bytes must match. Split APKs are not admitted by this monolithic
package route. Actual loaded library/APK-entry bytes are measured again in the
target manifest before debugger admission.

On Apple, the agent is statically linked: an intermediate `.a` digest cannot
identify the executed code. `ImageKind: "linked-executable"` means SHA-256 over
**every byte of the final linked, signed executable**, covering its agent code,
data, load commands and embedded CodeDirectory—not a self-reported archive hash.
The host additionally checks a deterministic whole-bundle digest, plist
application/version, CodeDirectory identity and `codesign --verify --deep --strict`,
both before install and against the simulator's installed app bundle. The target
resolves the actual agent function with `dladdr`, requires that image to be its
principal executable and measures that final file. Host admission compares that
measurement with the separately trusted post-link identity before debugger
activation. Simulator ad-hoc signing is not a claim of vendor certificate
authentication; the externally trusted digests are authoritative. Physical builds
retain their normal developer/provisioning/signing requirements.

Re-seal after **any** relink, strip, re-sign or package change. Missing final
metadata, same-build-ID/different-byte images and installed mismatches fail
closed before launch/admission. Exact runtime/DBI/DAC hashes/exports remain
independently required. Generic physical-device command providers must run this
same verification before deployment, verify the installed image through their
trusted device route, and supply the resulting profile; a command tunnel does
not waive package verification or hardware qualification.

The qualified fixture explicitly selects Android build-tools **37.0.0**, matching
the hosted SDK provisioning and both emulator tuples. Preflight requires its
official `aapt2` and `apksigner.jar` before boot/build. It does not silently use the
SDK's different default version or discover the newest installed tools.

## Apple simulator and physical device

Simulator target:

```json
{
  "target": { "provider": "appleSimulator", "apple": {
    "applicationId": "com.vendor.sensor",
    "deviceId": "EXACT_BOOTED_SIMULATOR_UDID",
    "appPath": "/absolute/Sensor.app",
    "runtimeIdentifier": "iossimulator-arm64"
  }},
  "tunnel": { "provider": "loopback" }
}
```

Physical Apple targets use `provider: "appleDevice"` with a `commands` member.
The vendor's signed development application embeds the `ios-arm64` target agent,
exact CoreCLR/DBI/DAC and generation-bound manifest. Supply structured commands
for official/vendor install/launch/descriptor/observation/stop/cleanup, and a
provisioned USB helper or authenticated encrypted network tunnel. DNCDbg does not
pretend simctl controls a physical phone, invent a stable Apple USB CLI, bypass
signing, or silently use an unencrypted network port.

`tunnel: { "provider": "managed", "host": "127.0.0.1", "port": 12345,
"security": "externallyManagedEncrypted" }` consumes an **already secured** external tunnel.
For USB-only local helpers use `security: "local"`. DNCDbg does not own/kill a
managed external helper. To give it ownership, use a structured `stdio` command
whose lifetime is the route. Non-loopback host endpoints are always rejected.

The pinned physical iOS pack contains separate Mach-O platform-2 CoreCLR/DBI/DAC
images; their UUID/code hashes and required `DllMain`/`CoreCLRCreateCordbObject3`
exports were checked. The `ios-arm64` native archive builds with the iPhoneOS SDK.
**No physical device or signing/provisioning profile was available for execution.**
Permissions/entitlements and native debugging must be qualified on real hardware
before claiming that hardware supported. Simulator execution cannot prove it.

## Publishing

```sh
# Managed host contracts/providers; no mobile workloads or target-native binaries:
dotnet pack src/DncDbg.Remote -c Release -o build/packages
dotnet pack src/DncDbg.Remote.Android -c Release -o build/packages
dotnet pack src/DncDbg.Remote.Apple -c Release -o build/packages

# Explicit deployment bundle on its matching OS/architecture:
python3 src/DncDbg.Remote.Target/publish.py --rid linux-arm64 \
  --include-runtime --payload-directory /absolute/published \
  --output build/target-linux-arm64-UNIQUE

# Explicit physical Apple build asset (not hardware validation):
python3 src/DncDbg.Remote.Target/publish.py --rid ios-arm64 \
  --output build/target-ios-arm64-UNIQUE
```

Native bundles contain the agent/metadata, product host, target controller,
optional checksum-locked runtime and matching payload files, target profile and
per-file SHA-256 `integrity.json`. Mobile packages contain only the explicit RID
agent/metadata. Build Linux assets in a matching Linux container/host. Do not mix
agent RIDs or copy target packages into an ordinary desktop adapter artifact.
Fault-enabled Release targets are rejected.

`--payload-directory` declares the complete `dotnet publish` output: native
`runtimes/*` assets, satellite resource assemblies, content subdirectories and
executable permissions are preserved recursively and inventoried deterministically.
Build/test/temp/cache directories and secret/key files are excluded; keep required
credentials outside the published closure and provision them through protected
configuration. Symbolic links, special files, traversal/nonportable names and
case-colliding paths fail closed. The old `--payload DLL` heuristic is rejected.
`--payload-file` is only for a validated standalone .NET version-6 bundle with no
loose dependencies; vendor command launchers can execute that apphost. The built-in
native host expects a managed assembly and its directory closure, not a bundled
apphost.

For a device with its qualified runtime already provisioned, replace
`--include-runtime` with `--runtime-directory /absolute/exact/runtime-directory`.
The publisher validates that runtime and emits its trusted profile without
copying it into every per-session deployment. Target startup still checks the
actual installed images; this is explicit provisioning, not a runtime fallback.

## Migration and failures

The legacy `mobile` DAP property, `enableExperimental`, old provider names and
`EnableExperimental*` MSBuild properties are rejected with explicit errors. Move
settings into `remote.target` and `remote.tunnel`; remove obsolete flags rather
than renaming their boolean values. There is no fallback to the desktop engine.
The old mobile diagnostic scripts are not public commands: use the one
[`DncDbg.MobileTests`](../tests/DncDbg.MobileTests/README.md) entrypoint.

On failure inspect owner journals, target startup errors, actual runtime/agent
identity, strict SSH diagnostics and exact-head evidence. Do not substitute a
nearby runtime/image, disable host checking, assume EOF means success, reset a
shared daemon or signal an unverified PID. Recovery acts only on a verified owned
generation/container/process; emergency cleanup never makes a failed gate green.
