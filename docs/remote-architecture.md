# Generic remote managed debugging

The shared debugger is not a collection of separate Android/iOS engines.
`RemoteSession` translates DAP to the generated typed model, `RemoteLifecycle`
owns the remote session, and target/tunnel providers supply only platform
lifecycle and routing. Desktop native hosting is the canonical fast proof;
mobile adds packaging/startup and platform integration proof.

```text
dncdbg-dap (C#, .NET 10)
  RemoteSession                 DAP IDs, source/PDB catalogs, typed operations
    RemoteLifecycle             settle acquisition/cancel/restart/auth/cleanup
      IRemoteTargetProvider     start/install/path/descriptor/stop
      ITunnelProvider           owned host endpoint and transactional disposal
        DirectLoopback | LocalTcpProxy | AdbForwardProvider | future Apple USB
    RemoteDebugClient           the same framed/authenticated generated contract
       -> target C++ agent      the same Bootstrap.cpp / RemoteModel.h
          native owner thread   private DBI / ICorDebug / callback-stop leases
          CoreCLR managed code
```

There is **one execution controller in the target**. The C# DAP host does not
attach the ordinary desktop engine as a second controller. Unsupported remote
requests fail explicitly, rather than falling back to desktop execution.
Normal DNCDbg remains C#; the native code is an explicit Debug experimental
target/provider fixture, absent from ordinary Release adapter artifacts.

## Provider responsibilities

`IRemoteTargetProvider.AcquireAsync(sessionId, token)` performs target startup;
`ReadDescriptorAsync` supplies only the platform-specific descriptor read.
The core polls/parses that descriptor and enforces nonce/credential admission.
The provider's close operation stops/releases only that target.
`ITunnelProvider.OpenAsync(descriptor, token)` returns the
owned host endpoint; it must preserve the target credential/session/process
identity. The coordinator enforces that invariant.

The shared coordinator:

1. Creates a fresh session nonce and bounded acquisition cancellation source.
2. Acquires the target, validates descriptor generation/port/credential shape,
   acquires the owned route, and authenticates `RemoteDebugClient`.
3. Checks provider/model version and exact target runtime/DBI/DAC identity and
   code hashes. Product versions and matching filenames are insufficient.
4. On close, cancels and **settles acquisition before taking the resource
   snapshot**. It disconnects/disposes the client, disposes the owned tunnel and
   closes the target with independent bounded cleanup budgets.
5. On restart or reconnect after transport loss, creates a new target, endpoint,
   credential, client and tunnel generation. DAP integer handles are invalidated
   and never reused; source breakpoint request IDs remain stable.

Providers do not implement shared stepping/inspection/evaluation or transport
authentication:

| Provider | Responsibility |
|---|---|
| `NativeHostProvider` | Public nethost/hostfxr C++ fixture process, scoped descriptor/log discovery and stop/reap |
| `MobileTargetProvider` | Exact simctl/ADB install, launch, app-data paths, markers and owned app cleanup |
| `DirectLoopback` | Local native host / iOS simulator loopback; no tunnel process or forwarding state |
| `LocalTcpProxy` | Real dynamic localhost listener, bidirectional forwarding, partial writes, half-close, bounded buffers and cancellation |
| `AdbForwardProvider` | Android CLI syntax/parsing and transactional daemon mapping ownership |
| future Apple USB | Explicitly unavailable today; no physical-device claim or silent direct fallback |

Android allocates `tcp:0` using a unique staging nonce, then rebinds only that
acquired port to the target's ephemeral listener. Lost allocation/rebind replies
are recovered transactionally by the exact serial/nonce; unrelated mappings are
never reset. This is Android-specific plumbing, not a second debugger model.
ADB command fakes verify syntax, parsing and committed-daemon rollback; the
generic lifecycle and tunnel invariants additionally run against real sockets
and the real target agent.

## Local correctness before mobile CI

Run the [native-host architecture suite](../tests/DncDbg.NativeHostTests/README.md):

```sh
dotnet run --project tests/DncDbg.NativeHostTests -c Release
```

It uses public `get_hostfxr_path`, `hostfxr_initialize_for_runtime_config` and
`hostfxr_get_runtime_delegate`, loading the **same target agent before runtime
initialization** and holding the user payload until native debugger startup.
The host then calls a managed entrypoint and proves return into C++.
`hostfxr` can execute framework initialization internally; the proof does not
mislabel that as “no managed instruction anywhere before attach.”

Both direct and actual TCP-proxy paths go through `dncdbg-dap`, not only a
direct client. They prove source breakpoints, stack/scopes/primitive/string/
array/object inspection, safe evaluation, stepping, CLR exceptions, pause,
restart/reconnect, stale-handle/client rejection, stopped detach, termination,
target EOF and exact-once native cleanup. Real network contracts cover
fragmentation, half-close, backpressure, connection refusal/timeouts, listener
allocation/cancellation/collision and disposal races. Debug-only fault gates
cancel a real allocated-but-unpublished listener and kill the real route while
the target is running/stopped.

The same suite runs on normal macOS/Linux CI and inside the **existing required
Docker integration gate**. No mobile SDK/workload/device is necessary. A change
to target bootstrap, generated contract/model or generic lifecycle must run this
gate; mobile CI is not the sole oracle for debugger correctness.

Mobile testing remains a separate [single entrypoint](../tests/DncDbg.MobileTests/README.md).
It proves fixture packaging, Debug-only startup, exact device runtime admission,
ADB/direct routing, DevFlow app/process identity and owned device cleanup.
Existing comprehensive device assertions remain; generic correctness is now
proved headlessly first. DevFlow 9223 (Android) and broker 19223 are **UI-only**,
never debugger routes. iOS UI ports/session metadata are run-scoped to avoid
colliding with another app or ADB forward.

## Exact private ABI and native-code boundary

`PrivateBootstrapPolicy.SupportedBuilds` retains the two original arm64 mobile
tuples and separately admits Android x64 for hosted Linux/KVM proof.
`NativeHostBuilds` separately admits the SHA-locked .NET 11 RC1 desktop
runtime/DBI/DAC tuples for macOS arm64 and Linux x64/arm64. A desktop tuple cannot
pass the iOS/Android identity check. No nearest-version, desktop-DBI-for-mobile,
Mono, NativeAOT or physical-device fallback is allowed.

The private path remains `DllMain` / `CoreCLRCreateCordbObject3`; public hosting
APIs do **not** make this private debugger startup a supported public ABI.
The agent exports its compiled generated model version, and both native host
and mobile startup reject mismatched/old helpers before payload execution.
Native startup also rejects a capability document with a different model version.

Target-native code is necessary for pre-user-managed startup hooks/private ABI
access and an independent execution thread: a managed execution owner cannot
reliably resume itself when all managed threads are suspended. Only explicit
Debug fixtures build/link the C++ agent. Release configuration ignores mobile,
watchdog and remote-test-hook opt-ins; CI inspects the actual desktop publish
output and mobile Release packages. Stock CLR/dbgshim/PDB-reader native
dependencies are still permitted.

## Reference comparison (not protocol compatibility)

- [JaneySprings/clrdbg at bb5e52b7](https://github.com/JaneySprings/clrdbg/tree/bb5e52b7d0fdef874288a46a4a2aa0f1643e37d7/DotNet.Debugging.Remote)
  separates a **C++ target agent**
  ([Target](https://github.com/JaneySprings/clrdbg/tree/bb5e52b7d0fdef874288a46a4a2aa0f1643e37d7/DotNet.Debugging.Remote/Target))
  and a **NativeAOT shared host proxy**
  ([host project](https://github.com/JaneySprings/clrdbg/blob/bb5e52b7d0fdef874288a46a4a2aa0f1643e37d7/DotNet.Debugging.Remote/Host/DotNet.Debugging.Remote.csproj)).
  Android uses ADB Forward, iOS device routing uses an Apple TCP/USB tunnel
  (`TcpTunnel`), and the simulator can use direct networking. Those are transport
  choices, not separate debugger semantics. DNCDbg follows the target/host and
  lease/ownership separation but keeps its host in ordinary C#, with a **closed
  typed operation model**, not Janey's arbitrary COM/vtable proxy generator or
  wire protocol. Prior MIT adaptation attribution remains in
  `THIRD-PARTY-NOTICES.md`; no interoperability claim is made.
- [dotnet/android tracing/deployment](https://github.com/dotnet/android/blob/main/Documentation/guides/tracing.md)
  configures packaging, diagnostic ports and `dotnet-dsrouter`. Those transport
  **EventPipe diagnostics**, not `ICorDebug` execution control.
- Runtime supplies DBI/DAC and the
  [official mobile initialization API guidance](https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/initialize-debugger-mobile).
  Visual Studio's `vsdbg` remote debugger components/proxy protocol are separate
  from Android packaging tooling; DNCDbg does not redistribute proprietary
  `vsdbgremotecoreclrtarget`/`remotemscordbihost` binaries or claim their protocol.
- Public hosting reference:
  [.NET native hosting](https://learn.microsoft.com/dotnet/core/tutorials/netcore-hosting).
