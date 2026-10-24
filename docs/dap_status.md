# Debug Adapter Protocol Support Status

This document shows, message by message and field by field, which parts of the [Debug Adapter Protocol](https://microsoft.github.io/debug-adapter-protocol/) are supported by DNCDbg.

> The detailed matrix below is retained as the original native implementation's
> compatibility contract. Passing the existing acceptance suite does not certify
> every field in this matrix or every platform. The managed adapter advertises its
> currently implemented capabilities in the `initialize` response.

## Mixed managed/native debugging

**Decision:** use one ICorDebug interop controller for an **opt-in Windows x64**
launch slice, after its independent public API baseline passed. Preserve the
managed engine's default behavior. Do not advertise cross-platform mixed-mode
parity or add a macOS dual-controller production path: its external baseline
fails. A native-only LLDB stop is not evidence of a working mixed debugger.

The implementation is intentionally bounded to one-shot source breakpoints in
an already loaded native DLL, native stacks, and continue back to managed code.
It uses `DncDbg.Engine` directly, normal `DncDbg.Protocol` framing, and the explicit
`dotnet/setNativeBreakpoint` extension, not a DAP multiplexer.
See [configuration, request shape and limitations](build_windows.md#experimental-mixed-mode-launch).

### Scenarios and acceptance boundary

The first required scenario is managed source breakpoint -> P/Invoke -> native
source breakpoint -> continue -> managed source breakpoint, with arguments
`19, 23`, native sum `42`, and managed return local `42`. The repo-owned fixture
in `tests/DncDbg.MixedModeTests/fixture` includes labelled, non-inlined managed/C
functions, a native caller/callee pair, and an optional library-preload variant.
Managed breakpoints hold execution while the native session attaches, avoiding a
time-based race into P/Invoke.

Additional scenarios need separate proofs: merging native/managed frames on one
thread, enumerating native-only and managed threads without ID collisions,
stepping into/out of P/Invoke and reverse callbacks, native locals/types versus
managed expressions, late library loads, attach to an existing runtime,
detach/restart, and cancellation/termination while either controller is stopped.
None is implied by passing a native-only baseline.

### Public architectures evaluated

| Approach | Public surface and restrictions | Decision |
| --- | --- | --- |
| Direct ICorDebug interop | `SetUnmanagedHandler`, `DebugActiveProcess(pid, true)`, `ICorDebugUnmanagedCallback`, `ICorDebugProcess2.SetUnmanagedBreakpoint`/`ClearUnmanagedBreakpoint`, and native thread contexts. Runtime interop is compiled for **Windows x86, x64 and arm64**, not Unix. An unmanaged callback is initially OS-stopped, not necessarily CLR-synchronized; out-of-band callbacks require special continuation discipline. | Windows-only proof first; do not just flip `win32Attach` in the managed engine. |
| DbgEng / WinDbg | Documented Windows execution control, native symbols/stacks/threads. The public `Microsoft.Debugging.Platform.DbgEng` NuGet package describes redistributable debugger DLLs, headers and libraries, but has Microsoft-specific license terms, not an open-source engine license. A managed wrapper does not relicense its native engine. Two independent Win32 debugger owners cannot simply attach concurrently. | Public option, but no package or debugger binary added. Prefer the runtime's existing interop controller for the first Windows proof. |
| LLDB SB API embedding | Public native execution controller, C/C++ symbols and typed SB APIs. Requires matching native libraries, process/debugserver components, signing/permissions, and potentially a matching Python distribution. It does not itself supply ICorDebug mixed-mode synchronization or C# source support. | More in-process ABI/distribution coupling than a subprocess proof; rejected for the initial slice. |
| LLDB / `lldb-dap` subprocess | Public native debugger and DAP interface; toolchain-supplied or upstream LLVM binaries. `lldb-server`/debugserver provide the native process transport, not managed debugging. | Native-only baseline works on this host. Concurrent ICorDebug/LLDB control fails the macOS transition probe; not enabled in production. Linux is unproven, not inferred from macOS. |
| DAP multiplexer | Can route requests and translate handles/events, but cannot fix OS stop ownership, runtime helper-thread starvation, exception-port conflicts, or synthesize merged stacks. Requires explicit stop ownership, capability intersection, ID lifetimes, and coordinated lifecycle. | Do not implement a protocol facade over a failing engine baseline. |
| ClrMD, SOS, EventPipe | ClrMD/SOS provide process/dump inspection; EventPipe is tracing. These are useful diagnostics, but do not provide native source breakpoint/step execution control. | Not substitutes for a native debugger. No dependency added. |

### macOS blocker

The [reproducible LLDB baseline and failing probes](build_macos.md#mixed-mode-public-tool-baseline)
separate managed-only, native-only, and concurrent behavior. The native-only
baseline exposes C source, arguments, caller/callee frames and stepping across a
native assignment. Its unsymbolized JIT/transition frames are **not** a merged
C#/C call stack.

Current CoreCLR PAL source installs per-thread Mach breakpoint exception handlers
to support the managed debugger. It explicitly warns that suppressing those
handlers makes a managed debugger unusable. Native debugging can instead run with
those handlers suppressed when the runtime detects a native debugger at startup,
but that is not a documented simultaneous managed/native integration contract.
The probe does not alter private runtime settings, exception ports, JIT
instructions, or debugger binaries. Preloading the library still encounters
`PAL_DispatchException` around the native breakpoint, so preloading alone is not
a production fix.

This is a measured blocker for the tested macOS dual-controller architecture,
not a claim that every conceivable mixed-mode implementation is impossible.

### Dependencies, distribution and supported matrix

The fixture and harness are repository-owned MIT-licensed code. The test harness
uses the Python standard library, CMake, a host C compiler, the installed .NET SDK,
and an explicitly supplied `lldb-dap`. Upstream LLVM/LLDB uses
**Apache-2.0 WITH LLVM-exception**. Xcode's toolchain is used in place under Apple's
distribution terms; no Apple or LLVM binary is bundled with DNCDbg. Upstream LLDB
and Xcode LLDB are not assumed interchangeable: Homebrew LLVM `21.1.8` did not
complete its launch handshake on this host, while the Xcode binary did.

The Windows proof uses the same public pinned **MIT** ClrDebug `0.4.2` and
Microsoft.Diagnostics.DbgShim `10.0.745401` packages as the debugger, target-runtime
`mscordbi` supplied by .NET, and the operating system's documented `DbgHelp.dll`
API for source-to-address resolution. No DbgEng, Visual Studio debugger, vsdbg,
internal feed or privately redistributed component is required. The public MSVC
compiler from the Windows Actions image builds the DLL/PDB; its binaries are not
redistributed by this repository.

| Surface | Measured support |
| --- | --- |
| DNCDbg experimental Windows x64 launch | `nativeDebugging:true`, explicit one-shot native source breakpoint, native stack/threads at that stop, continue to managed, terminate. Default managed-only behavior is unchanged. |
| macOS `osx-arm64` / .NET 10 / Xcode LLDB | Separate managed and native launch baselines pass. Concurrent transition probe fails. |
| macOS `osx-x64` | Not validated. |
| Linux `linux-x64` / `linux-arm64` | Fixture is portable; no mixed-mode baseline established. No support claim. |
| Windows `win-x64` / .NET 10 | Public ICorDebug raw launch **and attach** proofs pass. The bounded production slice is launch-only; attach, stepping and native variables are not exposed. |
| Windows x86 / arm64 | Runtime feature gate exists, but these fixture/proof runs do not validate them. |

Debugger/target architecture must match. Runtime DBI/DAC selection and
cross-version compatibility remain the CLR's contracts; native symbols must
match the exact DLL/PDB or shared-library DWARF. No frozen debugger-version
compatibility is inferred from one passing toolchain.

### Windows evidence and next slice

The first complete external launch proof passed at
[run 40076033180](https://github.com/mattleibow/dncdbg/actions/runs/40076033180),
before any production change. Both raw launch and raw attach passed at
[run 40076146350](https://github.com/mattleibow/dncdbg/actions/runs/40076146350).
They established exact managed source points, a DbgHelp-resolved native source
breakpoint, `native_sum -> native_add` stack unwinding, x64 argument registers
`19,23`, a native instruction step, managed return local `42`, and native exit
event code zero.

Early failing runs were useful proof boundaries: an unaligned x64 context
returned `ERROR_NOACCESS`; after alignment, failing to rewind RIP past `INT3`
corrupted the result and failed the `42` assertion. Neither assertion was
weakened. The working baseline restores the opcode and instruction pointer.
The DNCDbg DAP test was also run before implementation and failed with
`Unsupported request 'dotnet/setNativeBreakpoint'`.

Next incremental slice: prove a native breakpoint rearm plus a managed/native
transition stepper on Windows x64, including an unoptimized and optimized DLL,
before replacing the explicit one-shot contract with persistent IDE source
breakpoints. Adapter attach/detach and native locals require their own lifecycle
and PDB type/location proofs. macOS needs a supported exception-ownership
architecture, not a DAP routing workaround.

### Official references checked

- [ICorDebug.DebugActiveProcess](https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-debugactiveprocess-method)
  and [SetUnmanagedHandler](https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-setunmanagedhandler-method).
  The old x86-only remarks on Learn must not be used as the current CoreCLR
  architecture matrix.
- Current runtime source at `47e0d08aa903b476781b746c081d1e2144e1d30c`:
  [platform feature gate](https://github.com/dotnet/runtime/blob/47e0d08aa903b476781b746c081d1e2144e1d30c/src/coreclr/clrdefinitions.cmake#L122-L124),
  [interop eligibility and rejection](https://github.com/dotnet/runtime/blob/47e0d08aa903b476781b746c081d1e2144e1d30c/src/coreclr/debug/di/rsmain.cpp#L1469-L1478),
  [native breakpoint implementation in .NET 10.0.12](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/debug/di/process.cpp#L9077-L9195),
  and [macOS PAL exception ownership](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/pal/src/exception/machexception.cpp#L124-L210).
- [Unmanaged callback synchronization/OOB rules](https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/icordebugunmanagedcallback-debugevent-method).
- [LLDB DAP public distribution and configuration](https://lldb.llvm.org/use/lldbdap.html),
  [LLDB embedding/Python caveats](https://lldb.llvm.org/resources/caveats.html),
  [LLVM license](https://github.com/llvm/llvm-project/blob/llvmorg-21.1.8/lldb/LICENSE.TXT).
- [DbgEng overview](https://learn.microsoft.com/windows-hardware/drivers/debugger/debugger-engine-overview),
  [public DbgEng package](https://www.nuget.org/packages/Microsoft.Debugging.Platform.DbgEng),
  [DbgHelp source-line API](https://learn.microsoft.com/windows/win32/api/dbghelp/nf-dbghelp-symgetlinefromname64).
- [ClrMD's stated inspection scope](https://github.com/microsoft/clrmd),
  [EventPipe tracing scope](https://learn.microsoft.com/dotnet/core/diagnostics/eventpipe).

How to read the blocks below:

- Field names, signatures, and their order are identical to the official DAP specification.
- For **requests**, ✅ marks fields the debugger accepts and uses, ❌ marks fields it ignores.
- For **responses** and **events**, ✅ marks fields the debugger sends to the client.
- For **types**, ✅ marks fields the debugger fills in or reads.
- ❌ marks fields that are not supported.
- 🧩 marks fields that go beyond the DAP specification (for example, following the VS Code C# debugger conventions).
- Messages missing from the detailed sections below are not supported at all; every such message is listed explicitly in the overview.
- ⚪ marks a message that carries no additional fields.

## Notation

```text
✅  Supported
❌  Not supported
🧩  Fields that go beyond the DAP specification
ℹ️  Note
⚪  No additional fields
```

## Navigation

#### Base Protocol

[ProtocolMessage](#protocolmessage), [Request](#request), [Event](#event), [Response](#response), [ErrorResponse](#errorresponse), [Cancel Request](#cancelrequest-cancel)

#### Events

[Initialized Event](#initializedevent-initialized), [Stopped Event](#stoppedevent), [Continued Event](#continuedevent), [Exited Event](#exitedevent), [Terminated Event](#terminatedevent), [Thread Event](#threadevent), [Output Event](#outputevent), [Breakpoint Event](#breakpointevent), [Module Event](#moduleevent), [LoadedSource Event](#loadedsourceevent), [Process Event](#processevent), [Capabilities Event](#capabilitiesevent)

#### Requests

[Initialize Request](#initializerequest-initialize), [Launch Request](#launchrequest-launch), [Attach Request](#attachrequest-attach), [Restart Request](#restartrequest-restart), [Disconnect Request](#disconnectrequest-disconnect), [Terminate Request](#terminaterequest-terminate), [BreakpointLocations Request](#breakpointlocationsrequest-breakpointlocations), [SetBreakpoints Request](#setbreakpointsrequest-setbreakpoints), [SetFunctionBreakpoints Request](#setfunctionbreakpointsrequest-setfunctionbreakpoints), [SetExceptionBreakpoints Request](#setexceptionbreakpointsrequest-setexceptionbreakpoints), [ConfigurationDone Request](#configurationdonerequest-configurationdone), [Continue Request](#continuerequest-continue), [Next Request](#nextrequest-next), [StepIn Request](#stepinrequest-stepin), [StepOut Request](#stepoutrequest-stepout), [Pause Request](#pauserequest-pause), [Goto Request](#gotorequest-goto), [StackTrace Request](#stacktracerequest-stacktrace), [Scopes Request](#scopesrequest-scopes), [Variables Request](#variablesrequest-variables), [SetVariable Request](#setvariablerequest-setvariable), [Source Request](#sourcerequest-source), [Threads Request](#threadsrequest-threads), [Modules Request](#modulesrequest-modules), [LoadedSources Request](#loadedsourcesrequest-loadedsources), [Evaluate Request](#evaluaterequest-evaluate), [SetExpression Request](#setexpressionrequest-setexpression), [GotoTargets Request](#gototargetsrequest-gototargets), [ExceptionInfo Request](#exceptioninforequest-exceptioninfo), [ReadMemory Request](#readmemoryrequest-readmemory), [WriteMemory Request](#writememoryrequest-writememory)

#### Requests, not part of the DAP specification

[Detach Request](#detachrequest-detach)

#### Types

[Capabilities](#capabilities), [Checksum](#checksum), [ExceptionBreakpointsFilter](#exceptionbreakpointsfilter), [Module](#module), [Thread](#thread), [Source](#source), [StackFrame](#stackframe), [Scope](#scope), [Variable](#variable), [SourceBreakpoint](#sourcebreakpoint), [FunctionBreakpoint](#functionbreakpoint), [Breakpoint](#breakpoint), [BreakpointLocation](#breakpointlocation), [GotoTarget](#gototarget), [ExceptionFilterOptions](#exceptionfilteroptions), [ExceptionOptions](#exceptionoptions), [ExceptionDetails](#exceptiondetails), [ExpressionEvaluationOptions](#expressionevaluationoptions)

## Base Protocol

#### ProtocolMessage
```text
✅  seq: number;
✅  type: 'request' | 'response' | 'event' | string;
```
#### Request
```text
✅  command: string;
✅  arguments?: any;
```
#### Event
```text
✅  event: string;
✅  body?: any;
```
#### Response
```text
✅  request_seq: number;
✅  success: boolean;
✅  command: string;
✅  message?: 'cancelled' | 'notStopped' | string;
✅  body?: any;
```
#### ErrorResponse
```text
❌  error?: Message;
```
#### CancelRequest `cancel`
```text
✅  requestId?: number;
❌  progressId?: string;
```
#### CancelResponse
```text
⚪  no additional fields
```

## Events

#### InitializedEvent `initialized`
```text
⚪  no additional fields
```
#### StoppedEvent
```text
✅  reason: 'step' | 'breakpoint' | 'exception' | 'pause'
        | 'entry' | 'function breakpoint' | 'data breakpoint'
        | 'instruction breakpoint' | 'goto' | string;
        ℹ️  note: 'data breakpoint' and 'instruction breakpoint' are never sent
❌  description?: string;
✅  threadId?: number;
❌  preserveFocusHint?: boolean;
✅  text?: string;
✅  allThreadsStopped?: boolean;
✅  hitBreakpointIds?: number[];
```
#### ContinuedEvent
```text
✅  threadId: number;
✅  allThreadsContinued?: boolean;
```
#### ExitedEvent
```text
✅  exitCode: number;
```
#### TerminatedEvent
```text
❌  restart?: any;
```
#### ThreadEvent
```text
✅  reason: 'started' | 'exited' | string;
✅  threadId: number;
```
#### OutputEvent
```text
✅  category?: 'console' | 'important' | 'telemetry' | 'stdout' | 'stderr' | string;
        ℹ️  note: 'important' and 'telemetry' are never sent
✅  output: string;
❌  group?: 'start' | 'startCollapsed' | 'end';
❌  variablesReference?: number;
✅  source?: Source;
✅  line?: number;
✅  column?: number;
❌  data?: any;
❌  locationReference?: number;
```
#### BreakpointEvent
```text
✅  reason: 'changed' | 'new' | 'removed' | string;
✅  breakpoint: Breakpoint;
```
#### ModuleEvent
```text
✅  reason: 'new' | 'changed' | 'removed';
✅  module: Module;
```
#### LoadedSourceEvent
```text
✅  reason: 'new' | 'changed' | 'removed';
✅  source: Source;
```
#### ProcessEvent
```text
✅  name: string;
✅  systemProcessId?: number;
✅  isLocalProcess?: boolean;
✅  startMethod?: 'launch' | 'attach' | 'attachForSuspendedLaunch';
        ℹ️  note: 'attachForSuspendedLaunch' is never sent
✅  pointerSize?: number;
```
#### CapabilitiesEvent
```text
✅  capabilities: Capabilities;
```

## Requests

#### InitializeRequest `initialize`
```text
✅  clientID?: string;
✅  clientName?: string;
✅  adapterID: string;
❌  locale?: string;
✅  linesStartAt1?: boolean;
✅  columnsStartAt1?: boolean;
❌  pathFormat?: 'path' | 'uri' | string;
❌  supportsVariableType?: boolean;
❌  supportsVariablePaging?: boolean;
❌  supportsRunInTerminalRequest?: boolean;
✅  supportsMemoryReferences?: boolean;
❌  supportsProgressReporting?: boolean;
❌  supportsInvalidatedEvent?: boolean;
❌  supportsMemoryEvent?: boolean;
❌  supportsArgsCanBeInterpretedByShell?: boolean;
❌  supportsStartDebuggingRequest?: boolean;
❌  supportsANSIStyling?: boolean;
```
#### InitializeResponse
```text
✅  body?: Capabilities;
```
#### LaunchRequest `launch`
```text
✅  noDebug?: boolean;
❌  __restart?: any;
🧩  cwd?: string;
🧩  env?: { [key: string]: string; };
🧩  sourceFileMap?: { [key: string]: string; };
🧩  program?: string;
🧩  args?: string;
🧩  stopAtEntry?: boolean;
🧩  justMyCode?: boolean;
🧩  enableStepFiltering?: boolean;
🧩  expressionEvaluationOptions?: ExpressionEvaluationOptions;
🧩  console?: 'internalConsole' | 'remoteConsole' | 'externalTerminal';
🧩  suppressJITOptimizations?: boolean;
```
#### LaunchResponse
```text
⚪  no additional fields
```
#### AttachRequest `attach`
```text
❌  __restart?: any;
✅  processId: number;
🧩  env?: { [key: string]: string; };
        ℹ️  note: in attach, 'env' is used for the debug session configuration only
🧩  sourceFileMap?: { [key: string]: string; };
🧩  stopAtEntry?: boolean;
🧩  justMyCode?: boolean;
🧩  enableStepFiltering?: boolean;
🧩  expressionEvaluationOptions?: ExpressionEvaluationOptions;
🧩  suppressJITOptimizations?: boolean;
```
#### AttachResponse
```text
⚪  no additional fields
```
#### RestartRequest `restart`
```text
✅  arguments?: LaunchRequestArguments | AttachRequestArguments;
```
#### RestartResponse
```text
⚪  no additional fields
```
#### DisconnectRequest `disconnect`
```text
❌  restart?: boolean;
✅  terminateDebuggee?: boolean;
❌  suspendDebuggee?: boolean;
```
#### DisconnectResponse
```text
⚪  no additional fields
```
#### TerminateRequest `terminate`
```text
❌  restart?: boolean;
```
#### TerminateResponse
```text
⚪  no additional fields
```
#### BreakpointLocationsRequest `breakpointLocations`
```text
✅  source: Source;
✅  line: number;
✅  column?: number;
✅  endLine?: number;
✅  endColumn?: number;
```
#### BreakpointLocationsResponse
```text
✅  breakpoints: BreakpointLocation[];
```
#### SetBreakpointsRequest `setBreakpoints`
```text
✅  source: Source;
✅  breakpoints?: SourceBreakpoint[];
❌  lines?: number[];
❌  sourceModified?: boolean;
```
#### SetBreakpointsResponse
```text
✅  breakpoints: Breakpoint[];
```
#### SetFunctionBreakpointsRequest `setFunctionBreakpoints`
```text
✅  breakpoints: FunctionBreakpoint[];
```
#### SetFunctionBreakpointsResponse
```text
✅  breakpoints: Breakpoint[];
```
#### SetExceptionBreakpointsRequest `setExceptionBreakpoints`
```text
✅  filters: string[];
✅  filterOptions?: ExceptionFilterOptions[];
❌  exceptionOptions?: ExceptionOptions[];
```
#### SetExceptionBreakpointsResponse
```text
✅  breakpoints?: Breakpoint[];
```
#### ConfigurationDoneRequest `configurationDone`
```text
⚪  no additional fields
```
#### ContinueRequest `continue`
```text
✅  threadId: number;
✅  singleThread?: boolean;
```
#### ContinueResponse
```text
✅  allThreadsContinued?: boolean;
🧩  threadId: number;
```
#### NextRequest `next`
```text
✅  threadId: number;
✅  singleThread?: boolean;
❌  granularity?: SteppingGranularity;
```
#### NextResponse
```text
⚪  no additional fields
```
#### StepInRequest `stepIn`
```text
✅  threadId: number;
✅  singleThread?: boolean;
❌  targetId?: number;
❌  granularity?: SteppingGranularity;
```
#### StepInResponse
```text
⚪  no additional fields
```
#### StepOutRequest `stepOut`
```text
✅  threadId: number;
✅  singleThread?: boolean;
❌  granularity?: SteppingGranularity;
```
#### StepOutResponse
```text
⚪  no additional fields
```
#### PauseRequest `pause`
```text
✅  threadId: number;
```
#### PauseResponse
```text
⚪  no additional fields
```
#### GotoRequest `goto`
```text
✅  threadId: number;
✅  targetId: number;
```
#### GotoResponse
```text
⚪  no additional fields
```
#### StackTraceRequest `stackTrace`
```text
✅  threadId: number;
✅  startFrame?: number;
✅  levels?: number;
❌  format?: StackFrameFormat;
```
#### StackTraceResponse
```text
✅  stackFrames: StackFrame[];
✅  totalFrames?: number;
```
#### ScopesRequest `scopes`
```text
✅  frameId: number;
```
#### ScopesResponse
```text
✅  scopes: Scope[];
```
#### VariablesRequest `variables`
```text
✅  variablesReference: number;
❌  filter?: 'indexed' | 'named';
❌  start?: number;
❌  count?: number;
❌  format?: ValueFormat;
```
#### VariablesResponse
```text
✅  variables: Variable[];
```
#### SetVariableRequest `setVariable`
```text
✅  variablesReference: number;
✅  name: string;
✅  value: string;
❌  format?: ValueFormat;
```
#### SetVariableResponse
```text
✅  value: string;
❌  type?: string;
❌  variablesReference?: number;
❌  namedVariables?: number;
❌  indexedVariables?: number;
❌  memoryReference?: string;
❌  valueLocationReference?: number;
```
#### SourceRequest `source`
```text
✅  source?: Source;
✅  sourceReference: number;
```
#### SourceResponse
```text
✅  content: string;
❌  mimeType?: string;
```
#### ThreadsRequest `threads`
```text
⚪  no additional fields
```
#### ThreadsResponse
```text
✅  threads: Thread[];
```
#### ModulesRequest `modules`
```text
✅  startModule?: number;
✅  moduleCount?: number;
```
#### ModulesResponse
```text
✅  modules: Module[];
✅  totalModules?: number;
```
#### LoadedSourcesRequest `loadedSources`
```text
⚪  no additional fields
```
#### LoadedSourcesResponse
```text
✅  sources: Source[];
```
#### EvaluateRequest `evaluate`
```text
✅  expression: string;
✅  frameId?: number;
❌  line?: number;
❌  column?: number;
❌  source?: Source;
❌  context?: 'watch' | 'repl' | 'hover' | 'clipboard' | 'variables' | string;
❌  format?: ValueFormat;
```
#### EvaluateResponse
```text
✅  result: string;
✅  type?: string;
❌  presentationHint?: VariablePresentationHint;
✅  variablesReference: number;
❌  namedVariables?: number;
❌  indexedVariables?: number;
✅  memoryReference?: string;
❌  valueLocationReference?: number;
```
#### SetExpressionRequest `setExpression`
```text
✅  expression: string;
✅  value: string;
✅  frameId?: number;
❌  format?: ValueFormat;
```
#### SetExpressionResponse
```text
✅  value: string;
❌  type?: string;
❌  presentationHint?: VariablePresentationHint;
❌  variablesReference?: number;
❌  namedVariables?: number;
❌  indexedVariables?: number;
❌  memoryReference?: string;
❌  valueLocationReference?: number;
```
#### GotoTargetsRequest `gotoTargets`
```text
✅  source: Source;
✅  line: number;
✅  column?: number;
```
#### GotoTargetsResponse
```text
✅  targets: GotoTarget[];
```
#### ExceptionInfoRequest `exceptionInfo`
```text
✅  threadId: number;
```
#### ExceptionInfoResponse
```text
✅  exceptionId: string;
✅  description?: string;
✅  breakMode: ExceptionBreakMode;
✅  details?: ExceptionDetails;
```
#### ReadMemoryRequest `readMemory`
```text
✅  memoryReference: string;
✅  offset?: number;
✅  count: number;
```
#### ReadMemoryResponse
```text
✅  address: string;
✅  unreadableBytes?: number;
✅  data?: string;
```
#### WriteMemoryRequest `writeMemory`
```text
✅  memoryReference: string;
✅  offset?: number;
❌  allowPartial?: boolean;
✅  data: string;
```
#### WriteMemoryResponse
```text
❌  offset?: number;
❌  bytesWritten?: number;
```

### Requests, not part of the DAP specification

#### DetachRequest `detach`
```text
⚪  no additional fields
```
#### DetachResponse
```text
⚪  no additional fields
```

## Types

#### Capabilities
```text
✅  supportsConfigurationDoneRequest?: boolean;
✅  supportsFunctionBreakpoints?: boolean;
✅  supportsConditionalBreakpoints?: boolean;
✅  supportsHitConditionalBreakpoints?: boolean;
❌  supportsEvaluateForHovers?: boolean;
✅  exceptionBreakpointFilters?: ExceptionBreakpointsFilter[];
❌  supportsStepBack?: boolean;
✅  supportsSetVariable?: boolean;
❌  supportsRestartFrame?: boolean;
✅  supportsGotoTargetsRequest?: boolean;
❌  supportsStepInTargetsRequest?: boolean;
❌  supportsCompletionsRequest?: boolean;
❌  completionTriggerCharacters?: string[];
❌  supportsModulesRequest?: boolean;
❌  additionalModuleColumns?: ColumnDescriptor[];
❌  supportedChecksumAlgorithms?: ChecksumAlgorithm[];
✅  supportsRestartRequest?: boolean;
        ℹ️  note: VS Code IDE supports this feature, but vsdbg-ui (the Microsoft C# extension)
                  does not. The debugger detects that it runs in VS Code with vsdbg-ui by the
                  '--interpreter=vscode' launch option, which vsdbg-ui silently adds, and
                  reports "false" for this capability.
✅  supportsExceptionOptions?: boolean;
❌  supportsValueFormattingOptions?: boolean;
✅  supportsExceptionInfoRequest?: boolean;
✅  supportTerminateDebuggee?: boolean;
❌  supportSuspendDebuggee?: boolean;
❌  supportsDelayedStackTraceLoading?: boolean;
✅  supportsLoadedSourcesRequest?: boolean;
✅  supportsLogPoints?: boolean;
❌  supportsTerminateThreadsRequest?: boolean;
✅  supportsSetExpression?: boolean;
✅  supportsTerminateRequest?: boolean;
❌  supportsDataBreakpoints?: boolean;
✅  supportsReadMemoryRequest?: boolean;
✅  supportsWriteMemoryRequest?: boolean;
❌  supportsDisassembleRequest?: boolean;
✅  supportsCancelRequest?: boolean;
✅  supportsBreakpointLocationsRequest?: boolean;
❌  supportsClipboardContext?: boolean;
❌  supportsSteppingGranularity?: boolean;
❌  supportsInstructionBreakpoints?: boolean;
✅  supportsExceptionFilterOptions?: boolean;
✅  supportsSingleThreadExecutionRequests?: boolean;
❌  supportsDataBreakpointBytes?: boolean;
❌  breakpointModes?: BreakpointMode[];
❌  supportsANSIStyling?: boolean;
```
#### Checksum
```text
✅  algorithm: ChecksumAlgorithm;
✅  checksum: string;
```
#### ExceptionBreakpointsFilter
```text
✅  filter: string;
✅  label: string;
❌  description?: string;
❌  default?: boolean;
❌  supportsCondition?: boolean;
❌  conditionDescription?: string;
```
#### Module
```text
✅  id: number | string;
✅  name: string;
✅  path?: string;
✅  isOptimized?: boolean;
✅  isUserCode?: boolean;
❌  version?: string;
✅  symbolStatus?: string;
✅  symbolFilePath?: string;
❌  dateTimeStamp?: string;
✅  addressRange?: string;
```
#### Thread
```text
✅  id: number;
✅  name: string;
```
#### Source
```text
✅  name?: string;
✅  path?: string;
✅  sourceReference?: number;
❌  presentationHint?: 'normal' | 'emphasize' | 'deemphasize';
❌  origin?: string;
❌  sources?: Source[];
❌  adapterData?: any;
✅  checksums?: Checksum[];
```
#### StackFrame
```text
✅  id: number;
✅  name: string;
✅  source?: Source;
✅  line: number;
✅  column: number;
✅  endLine?: number;
✅  endColumn?: number;
❌  canRestart?: boolean;
✅  instructionPointerReference?: string;
✅  moduleId?: number | string;
✅  presentationHint?: 'normal' | 'label' | 'subtle';
```
#### Scope
```text
✅  name: string;
❌  presentationHint?: 'arguments' | 'locals' | 'registers' | 'returnValue' | string;
✅  variablesReference: number;
❌  namedVariables?: number;
❌  indexedVariables?: number;
✅  expensive: boolean;
❌  source?: Source;
❌  line?: number;
❌  column?: number;
❌  endLine?: number;
❌  endColumn?: number;
```
#### Variable
```text
✅  name: string;
✅  value: string;
✅  type?: string;
❌  presentationHint?: VariablePresentationHint;
✅  evaluateName?: string;
✅  variablesReference: number;
❌  namedVariables?: number;
❌  indexedVariables?: number;
✅  memoryReference?: string;
❌  declarationLocationReference?: number;
❌  valueLocationReference?: number;
```
#### SourceBreakpoint
```text
✅  line: number;
✅  column?: number;
✅  condition?: string;
✅  hitCondition?: string;
✅  logMessage?: string;
❌  mode?: string;
```
#### FunctionBreakpoint
```text
✅  name: string;
✅  condition?: string;
✅  hitCondition?: string;
```
#### Breakpoint
```text
✅  id?: number;
✅  verified: boolean;
✅  message?: string;
✅  source?: Source;
✅  line?: number;
✅  column?: number;
✅  endLine?: number;
✅  endColumn?: number;
✅  instructionReference?: string;
✅  offset?: number;
❌  reason?: 'pending' | 'failed';
```
#### BreakpointLocation
```text
✅  line: number;
✅  column?: number;
✅  endLine?: number;
✅  endColumn?: number;
```
#### GotoTarget
```text
✅  id: number;
✅  label: string;
✅  line: number;
✅  column?: number;
✅  endLine?: number;
✅  endColumn?: number;
✅  instructionPointerReference?: string;
```
#### ExceptionFilterOptions
```text
✅  filterId: string;
✅  condition?: string;
❌  mode?: string;
```
#### ExceptionOptions
```text
❌  path?: ExceptionPathSegment[];
✅  breakMode: ExceptionBreakMode;
```
#### ExceptionDetails
```text
✅  message?: string;
✅  typeName?: string;
✅  fullTypeName?: string;
✅  evaluateName?: string;
✅  stackTrace?: string;
✅  innerException?: ExceptionDetails[];
🧩  formattedDescription?: string;
🧩  source?: string;
```
#### ExpressionEvaluationOptions
```text
🧩  allowImplicitFuncEval?: boolean;
🧩  allowToString?: boolean;
🧩  showRawValues?: boolean;
```
