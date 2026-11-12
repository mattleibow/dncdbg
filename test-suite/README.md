# How to launch tests locally

`Test*/Test*.csproj` is the upstream-compatible native/managed parity contract.
Do not add device orchestration or private-runtime mobile tests to that
auto-discovered set. Portable remote contracts live in
[`tests/DncDbg.RemoteTests`](../tests/DncDbg.RemoteTests/README.md), built by
`DncDbg.slnx`; the [mobile entrypoint](../tests/DncDbg.MobileTests/README.md)
owns separate simulator/emulator DAP, DevFlow and fault proof. Neither replaces
or relaxes this parity suite.
The [native-host architecture gate](../tests/DncDbg.NativeHostTests/README.md)
uses the real shared C++ target agent through generic remote DAP providers on
macOS/Linux/Docker. It is also under `tests/`, never an auto-discovered parity
`Test*` project.

Use PowerShell 7 (`pwsh`) on Windows, Linux, and macOS. From the repository root:

```powershell
# Run every test supported by the current operating system.
pwsh ./test-suite/run_tests.ps1

# Run selected tests.
pwsh ./test-suite/run_tests.ps1 TestBreakpoint TestEvaluate

# Use a debugger apphost from another location.
$env:DNCDBG = "/path/to/dncdbg"
pwsh ./test-suite/run_tests.ps1 TestBreakpoint
```

The runner discovers directories named `Test*` that contain a matching
`Test*.csproj`; no registration is required. Projects targeting a Windows-specific
framework or enabling Windows Forms are included automatically on Windows and
skipped on other operating systems.

The target framework can be overridden with the `DNCDBG_TARGET_FRAMEWORK` environment variable (for example, `net8.0`); by default, the tests target `net10.0`.

For the managed debugger, build or publish from the repository root first:

```sh
dotnet publish src/DncDbg.Dap -c Release -o bin
pwsh ./test-suite/run_tests.ps1 TestBreakpoint TestEvaluate TestStackTrace TestMemoryReferences TestReadWriteMemory
```

For the focused stepping parity set against the published Release host:

```sh
pwsh ./test-suite/run_tests.ps1 TestStepping TestStepping_NoJMCNoFilter TestAsyncStepping TestAsyncStepping_NoJMC
```

The debugger's build configuration is independent of each test fixture's
configuration; the existing scripts continue to select the appropriate fixture.

For the upstream TCP server fixtures:

```sh
pwsh ./test-suite/run_tests.ps1 TestServerDefaultPort TestServerCustomPort
```

Only these two fixtures select TCP automatically: the runner launches the adapter
with bare `--server` for port 4711 and `--server=22111` for the custom port. All
other tests keep stdio. The PowerShell entrypoint is shared across operating
systems, replacing both upstream script routes. Its `Runner --server <port>`
option connects to IPv4 loopback, with a five-second startup deadline, 50 ms
connection-refused retries and TCP `NoDelay`. It reuses the stdio DAP framing and
continuous queued reader, retaining back-to-back events across every restart on
the same connection.

Both `Program.cs` fixtures and project files are unchanged from upstream
`c2a3846694fed03c5f0d29dda4f978e1bcbce510`: initialization, source/function
breakpoints, restart while stopped, restart after exit, exact disabled implicit
evaluation errors, and restart with launch arguments enabling `TestEval()` as
`5`/`int`, followed by debugger exit. This is test/harness coverage, not a
production server implementation change. The preserved native reference is
built from `6bae41f9aa579dd2556ede7094e430e53eb64423` (binary SHA-256
`a7a6eca33690f0c22c7d16530f727f56fd5f9512a8adca32d37d94a209a988ca`);
its production source/vendor trees are identical at `c2a3846`. This is a
source-equivalent binary, not a binary rebuilt or relabeled as `c2a3846`.
Use a fresh short private inherited `TMPDIR` for each native/managed parity gate.

The evaluator workflow builds its current-native reference from exact
`b5432b4ef39980463178b0db404f1b315c805599`, retaining the TCP server support
covered by `c2a3846`, the `38365ab7` request hardening and the `be345423`
function-breakpoint name validation, and adding the `b5432b4e` exception option
JSON tolerance required by `TestExceptionOptionTolerance`. The earlier current
reference rejects non-string optional conditions and cannot pass this new fixture.
Its historical
`fc643dfc` native and pre-fix `96b6d67f` managed references remain unchanged.
The preserved-binary native 2/2, managed 2/2 and stdio restart 1/1 results are
local preparation evidence, not final pull-request qualification. Final CI must
run the complete current-native and managed suites, including both TCP fixtures.

Run the additional cross-platform CLI/protocol smoke checks from the repository root:

```sh
dotnet run --project tests/DncDbg.SmokeTests -- bin/dncdbg-dap
```

These checks do not modify the existing Runner, ScriptContext, fixtures, or
assertions. Their diagnostic logs are written under `build/validation/cli-smoke`.

The raw DAP checks run real adapter processes. Malformed cancel arguments and
typed evaluation/mutation/memory arguments must produce failed responses with
the original request sequence and command, a diagnostic, then allow valid
exception-breakpoint configuration and clean disconnect. Run the same shared
matrix against either the managed adapter or a native reference:

```sh
dotnet run --project tests/DncDbg.SmokeTests -- --protocol /path/to/debugger
```

Outgoing framing has its own shared black-box selector:

```sh
dotnet run --project tests/DncDbg.SmokeTests -c Release -- --outgoing-wire /path/to/debugger
dotnet run --project tests/DncDbg.SmokeTests -c Release -- --outgoing functional
dotnet run --project tests/DncDbg.SmokeTests -c Release -- --outgoing allocation
```

The shared selector checks exact UTF-8 byte framing, Unicode/escaped command
round-trips, an outgoing response over 64 KiB followed by small responses,
concurrent request-worker/cancel responses, contiguous outgoing sequences,
and exact protocol-log/wire correspondence. It runs in normal smoke and Docker;
the evaluator also runs it against its existing immutable current-native pin.
Managed checks preserve the previous JSON shape/escaping, outgoing depth over
64, trace-before-write/flush ordering, concurrent events and registered async
requests, reentrant serializer/trace sends, and clean recovery after real
serializer, trace, header, payload and flush exceptions. A partial failed stream
write cannot be undone: recovery resets the test stream before inspecting the
next frame, not the production transport.

The allocation selector warms serializer caches and the connection with 2,048
sends, then measures current-thread allocated bytes across 1,024 prebuilt 8 KiB
event bodies with tracing disabled and `Stream.Null`. Its 1,024-byte/send ceiling
rejects a fresh payload allocation while allowing message/runtime bookkeeping.
Eight repeated 128 KiB sends must each allocate at least their payload size,
demonstrating that oversized storage is not reused; small sends must subsequently
return below the same allocation ceiling. This measures allocations, not elapsed
time or the exact private buffer capacity, and makes no native-allocation claim.
All managed outgoing checks run in desktop smoke, Docker, and evaluator smoke.
The same oversized-allocation probe also covers repeated serializer, header,
payload and flush failures before checking the next clean frame.

The managed implementation mirrors upstream `0d38ab3c` using public .NET APIs:
an `ArrayBufferWriter<byte>` reuses outgoing capacity under the existing write
lock, a per-send disposable `Utf8JsonWriter` preserves serializer ownership,
and a stack header formats the actual UTF-8 byte length. The `finally` block
resets the written count or drops a buffer whose **capacity**, not payload length,
exceeds 64 KiB, including failures. Reentrant sends use separate temporary
storage so existing body-getter/trace behavior cannot corrupt the outer message.
The 64 KiB capacity comparison is an implementation invariant; public allocation
probes demonstrate oversized release but do not inspect the private capacity or
claim an exact payload-size retention cutoff.

Test-first local preparation on macOS arm64 used SDK 10.0.404/runtime 10.0.15.
The exact native `b5432b4e` archive (911 tracked blobs verified) passed the same
56-frame wire selector before production changed. Actual archived managed main
`9d150bb5` also passed wire and all 315 functional checks; its warmed allocation
was **8,816 bytes/send**, honestly failing the 1,024-byte ceiling. Buffer reuse
reduced that to **552 bytes/send**, including after oversized messages, with
functional and failure-release checks green. This is local preparation evidence,
not full native/Linux/Windows or final pull-request qualification. The evaluator's
current-native pin is deliberately unchanged for this allocation-only port.

`--protocol-recovery /path/to/dncdbg-dap` separately preserves managed-only
behavior: optional/no-op cancel arguments, recovery from invalid/overflowing or
over-cap Content-Length headers, non-JSON bodies, and malformed JSON with a
recoverable sequence. It sends only short headers/bodies, never large allocation
probes. Both groups also run in normal smoke; native's malformed-header
disconnect policy is intentionally not imposed on the managed adapter.

The evaluator explicitly runs the 20 shared checks against its exact current-native
adapter after building the smoke assembly and before managed green checks, saving
`native-protocol.log` in the official evidence artifact. The canonical `Test*`
suite alone does not run this selector. Managed smoke separately includes the
same 20 checks and 12 managed-only recovery cases. Local preparation proved
native `38365ab7` green, unchanged managed `dab33c9f` red on all ten malformed
cancel cases, then the minimal fix green. The updated main `94f11ef5` has identical
managed production sources to `dab33c9f`; its own fresh red proof and the rebased
fix use SDK 10.0.404/runtime 10.0.15, not the original preparation's SDK
10.0.403/runtime 10.0.14. These local results are not final CI qualification:
the evaluator uses its pinned SDK 10.0.401/runtime 10.0.14 and must retain all
historical, managed-red, repeated-green and full-suite gates.

The smoke runner also exercises startup/unregistration barriers, rejection of
late startup callbacks, cancellation of a delayed external launcher, a 1 MiB
stdin write to a non-reading target followed by disconnect, detached-child
survival beyond disposal timeouts, and pause during function evaluation followed
by a heartbeat after exactly one continue. On Unix they also alternate real
managed/noDebug and native launcher helpers to ensure a single reaping owner:
`System.Diagnostics.Process` reaps its children, while native-created helpers
use native waiting. Twelve alternating pairs also require exact target stdout
and stderr, without parent-shell watchdog termination notifications. They also arm static-initializer and
user breakpoints, detach, verify internal breakpoint cleanup, dispose the
debugger, and let the detached target complete its watched initializer.
Run these safety checks alone with:

```sh
dotnet run --project tests/DncDbg.SmokeTests -- --safety bin/dncdbg-dap
```

The smoke runner also checks evaluation callback ownership without launching a
target: notification abort fallback, terminal completion with queued callbacks,
and rejection of late or unrelated notifications. Run just these deterministic
contracts with `dotnet run --project tests/DncDbg.SmokeTests -- --evaluation`.

Resume-handle ownership checks require exactly one close after successful or
failed resume, preserve the original resume HRESULT when cleanup also fails,
and report a close failure after successful resume. They also cover exception
cleanup. Run them with
`dotnet run --project tests/DncDbg.SmokeTests -- --resume-handles`.
The shared operation is used by both Unix launch paths, including the terminal
helper; Windows thread-handle launch semantics are unchanged.

The self-hosted safety fixtures are separate from the unchanged acceptance
suite. A surviving detached fixture is explicitly cleaned up by the smoke runner.

## Docker integration

The separate Docker test preserves the existing acceptance runner and fixtures:

```sh
dotnet run --project tests/DncDbg.DockerTests -- --require-docker
```

It publishes daemon-native Linux artifacts and checks launch, source breakpoints,
container-local source mapping/retrieval, locals/evaluation and clean exit over
`docker exec -i` stdio. CI requires Docker; omitting `--require-docker` allows a
local availability skip. See [Docker and WSL debugging](../docs/docker_wsl.md)
for prerequisites, deadlines, isolated cleanup and coverage limitations.

## Focused native migration contracts

Run the same tests against either adapter without changing the debuggee or assertions:

```powershell
pwsh ./test-suite/run_tests.ps1 TestHitConditionSyntax TestContinueResponse TestUnhandledExceptionCondition TestExceptionConditionMatching TestExceptionOptionTolerance TestUnhandledExceptionFilter
$env:DNCDBG = "/absolute/path/to/another/debugger"
pwsh ./test-suite/run_tests.ps1 TestHitConditionSyntax TestContinueResponse TestUnhandledExceptionCondition TestExceptionConditionMatching TestExceptionOptionTolerance TestUnhandledExceptionFilter
```

These tests preserve observable native behavior, not assumptions about DAP syntax:

| Test | Exact contract |
| --- | --- |
| `TestEmbeddedSources` | Retrieve exact compressed/uncompressed embedded file text while stopped, including twenty raw JSON source requests with nested/top-level positive references and null/number/Boolean/array/object optional `path`/`name` values. Two name-only requests cover absent/empty paths. The same incoming-source boundary is exercised by `setBreakpoints`, `gotoTargets` and `breakpointLocations`: valid paths with malformed names and reciprocal valid names with malformed paths must return the exact breakpoint/target location. Path/name precedence, malformed source objects/references, required expression/line/condition validation, correlated failed responses, same-command recovery and exact source text remain covered. Non-string optional-field tolerance is native `38365ab7`; name-only fallback predates that revision. |
| `TestFunctionBreakpointValidation` | Exact native `2d9b3d4c` proves twelve accepted names, including plain/type/fully qualified names, empty and typed parameter lists, unmatched `)` without `(` and deliberately unvalidated identifier/nesting forms. Twelve malformed names are rejected individually and between valid entries, both before launch and after binding. All 56 failures require exact diagnostics and per-entry name-before-hit-condition ordering. A real source stop after the first counted function hit proves rejected replacements preserve the bound set and count: the second hit, expression-conditioned function, empty-parameter method and int/string overloads still stop at their exact source lines; neither replacement-only method stops. The target then exits normally. |
| `TestExceptionInfoGetters` | Exact native `25ba8eb4` covers throwing `StackTrace`/`Source` getters, a throwing `Source` after a valid stack, non-string/non-reference and null member shadows, and an ordinary exception. Repeated `exceptionInfo` requests retain the base message, exact type/break mode and complete inner chains; throwing/null stack getters produce no exception-object text, while valid stacks retain the real throw frame. Managed getter-failure recovery was already equivalent; wrong-type/null `_message` shadows exposed a separate message-loss regression. Old native `089db8c9` fails the empty-stack assertion by printing `{System.InvalidOperationException}` on this fixture, not by crashing. |
| `TestLogpointCallStack` | Exact native `089db8c9` physical stacks cover generic signatures, collapsed adjacent non-user frames, async kickoff/builder filtering and a deterministically suspended async caller that must not appear as a logical continuation. Quoted/`nq` newlines, escaped compound strings, literal token text, repeated expansion, hit counts and restart are preserved. Disabling Just My Code and step filtering retains non-user methods and physical framework frames, but still excludes async kickoff/builder frames and logical continuations. Deep recursion proves the default 250-frame limit, zero/one/two and exact-limit boundaries, the unsigned maximum, malformed-setting diagnostics and reset, with counting before JMC collapse. A real EDI unhandled-handler logpoint requires foreign `[Exception]` frames; a caught-rethrow finally logpoint must omit them. `TestLogpoints` retains the exact upstream three-frame CALLSTACK assertion alongside every existing assertion. |
| `TestLogpointNames` | Native-proven `$FUNCTION` signatures for the entry method, a parameterized method, a concrete generic method and an async method after suspension. `$TNAME` checks default main, explicitly named main, unnamed worker and renamed worker, including quotes/backslashes and embedded token text that must not be rescanned. `$CALLER` checks generic and suspended-async callers, quoted/`nq` display, literal text, repeated substitution and hit counts. Quoted and escaped `nq` displays, mixed name/file-position concatenations with verbatim backslashes, repeated counts and the complete sequence after restart are exact. `TestLogpoints` retains every upstream function/thread/file-position assertion and the exact `3549d009` missing/Main/nested-caller assertions; `TestEvaluate` includes the upstream verbatim `text` cases alongside prior coverage. |
| `TestLogpointTokens` | All five tokens are required at each of two hits and repeated after restart: exact source path/line and quoted/no-quotes formatting, nonzero 16-digit native address and consistent repeated address, observed target/thread IDs, per-breakpoint count, adjacent substitutions and arithmetic. Exact native `25ba8eb4` also proves escaped `nq` backslashes/quotes in FILEPOS, address and numeric string compounds, plain local/verbatim strings and newline/tab display; quoted strings and literal-only messages retain their respective formatting. Tokens outside braces remain literal. Exact updated upstream `TestLogpoints` checks hit counts 1/2 and adjacent 11/22 alongside every existing malformed/logpoint assertion. `TestEvaluate` retains the exact upstream verbatim path and adds doubled-quote/raw-backslash/u8-U8 assertions. |
| `TestEnvironmentDiagnostics` | Twenty launch/attach/restart sessions require thirteen exact parse/min/max fallback messages in DAP `output` events with category `stderr` and exactly one terminating newline. Valid settings and recovered restart configurations emit none; every target still exits successfully. Existing inner-limit and remote-console matrices independently retain exact fallback behavior. Attach diagnostics emitted before entry are included without dropping earlier events. |
| `TestLaunchSettings` | Byte-identical upstream fixture/profile files check three real debugging sessions: explicit settings file with request arguments, default profile from request `cwd`, and a named profile whose `ASPNETCORE_URLS` environment entry overrides `applicationUrl`. Preserve every entry/source breakpoint, exact local/environment/argument value, out-of-range argument error and actual exit assertion. |
| `TestLaunchSettingsPrecedence` | Preserve the prior 43-case native profile matrix for ordered/default/named selection, explicit null/empty disable, request precedence, relative paths, string/array/empty/whitespace args, target-only profile environment, malformed/missing/unsuitable default fallback, exact type/selection errors and bare versus updated restart. File mutation must not affect a bare ordinary program restart. Ten additional URL cases prove exact string fallback, null/empty/non-string ignoring, request and profile environment precedence (including empty values), and non-string environment entries not suppressing the URL fallback. Eight malformed launch/updated-restart cases require correlated failed responses, nonempty diagnostics and successful recovery without reinitialize. Request `DNCDBG_CONSOLE` and target-only profile console controls retain exact profile errors where applicable, without prescribing JSON-library text or precedence between two invalid fields. These assertions pass exact native `2dcb490e` and unchanged managed `94f11ef5` on macOS arm64; no production fix was needed for this corner. The splitter's escaped-quote/empty/unmatched-quote/ASCII-whitespace cases remain independently native-proven. |
| `TestCrossThreadEvaluation` | Forty complete variables responses across two stops must preserve the unchanged `TestStruct5` values (`111`, `<error>`, `"text_333"`, `<error>`, `555.5`), types and ordering, with a successful explicit getter after every response. A synchronized worker dependency halfway through the first stop proves timeout recovery releases suspended peers, reports the failed getter, and leaves the same evaluation thread usable for later getters and repeated variables requests. No sleeps, retries or changed deadlines are used. |
| `TestHitConditionSyntax` | Across seven visits, `5` and `>=5` stop on visits 5, 6 and 7; `==5` and `%5` stop only on 5, with no removal diagnostics. All sequences are collected before comparison. Forty-four valid source/function requests and 56 invalid ones prove unsigned bounds, zero/modulo-zero, ASCII-space-only normalization, exact error messages, and rejection of expressions, signs, overflow, tabs/newlines, suffixes and NUL characters. |
| `TestBreakpointHitCondition` | Exact upstream source/function fixture covers all seven forms (`N`, `<N`, `<=N`, `==N`, `>N`, `>=N`, `%N`) and every expected source/frame stop over seven visits. Repeat all sequences after exit and restart to prove counter reset, then verify the final process exit. |
| `TestHitConditionLifecycle` | IDs and counts survive unchanged/equivalent/changed thresholds for source and function breakpoints; false expression conditions do not count; logpoint output is gated after counting. Invalid replacement requests preserve existing breakpoints; removal/re-addition resets the count. Repeat the complete sequence after post-exit replacement and restart. Exact stop sequences and expected output are required. |
| `TestContinueResponse` | Continue responses and matching continued events contain a Boolean `allThreadsContinued`, with the requested thread ID. All-thread resumes report `true`, and the tested single-thread resume with suspended peers reports `false`. A redundant `singleThread:true` continue while the target waits on stdin reports the actual `true` result and emits no extra continued event; stdin releases the target without a timing sleep. This tests observable reporting, not a general scheduling guarantee. |
| `TestSingleThreadExec` | Preserve all existing single-thread scheduling, source and stepping assertions; additionally require `allThreadsContinued:false` in each next/stepIn/stepOut event and the single-thread continue, then `true` after restoring all-thread execution. |
| `TestRemoteConsoleConfiguration` | Sixteen real TCP ready/input/echo cases cover `DNCDBG_REMOTECONSOLE_PORT`: custom/default ports, leading zeroes, 65535, invalid-number/range fallback to 22534, and the ignored former spelling. Also prove a changed-port restart, an occupied-port launch failure and a successful retry after release. Running the identical fixture with a valid debugger-process port override proves session precedence, invalid-override fallback to 22534 and reset to the inherited baseline. |
| `TestUnhandledExceptionCondition` | With `!System.InvalidOperationException` on `all` and `user-unhandled`, a caught exception does not stop execution. An uncaught exception of the same type still stops at the throw line, with `exceptionInfo.breakMode == "unhandled"`, `exceptionId == "CLR/System.InvalidOperationException"`, and the matching full type name. These two filters do not suppress the final unhandled stop. |
| `TestExceptionConditionMatching` | Thirty-seven cases cover per-token negation, negative-only conditions, positive/negative order, exclusion priority, ignored lone `!`, empty conditions, additive unconditional entries, duplicate filter options, comma/ASCII-whitespace separators, and wildcard prefixes. Wildcard cases include `*`, `!*`, positive/negative prefix conflicts across entries, ordinal case sensitivity, duplicate prefixes, zero-length suffixes, and literal internal/repeated asterisks and `?`. Every stop and its exact exception details/source line is collected before comparing the complete matrix. |
| `TestExceptionOptionTolerance` | Exact native `b5432b4e` proves 39 accepted raw-JSON configurations and 46 rejected replacements. Absent/empty/null/number/Boolean/array/object conditions and arbitrary extra fields retain actual exception/source stops. Recognized IDs, strict array/entry/ID validation, additive filters, duplicate order and exclusion priority are preserved. Each rejected replacement must leave the previous exact-type rule active, then a valid same-command recovery must stop on the other exact type. All 131 passes require exact source lines, exception type/break mode and normal target exit. The original 37 typed-condition cases remain unchanged. |
| `TestExceptionWildcardCondition` | The unchanged upstream fixture checks `*`, `System.*`, `!System.Null*, *`, broader negated prefixes overriding specific positive prefixes, and `!*` overriding a positive prefix. Exact exception source/type/break-mode assertions and both source-breakpoint checkpoints are preserved. |
| `TestUnhandledExceptionFilter` | Eighteen sessions cover the default unhandled stop, nonmatching positive conditions, matching/nonmatching exact and prefix exclusions, conflicting conditions in both orders, unconditional entries, lone `!`, and `!*`. Upstream scenarios include an exact nonmatching `!System.Exception` (no base-type matching) and a thrown stop followed by a suppressed final stop. Observed unhandled stops are continued through actual process exit rather than terminated by the test. Suppressed final stops require a nonzero exit code and both exited/terminated events. |
| `TestInnerExceptionLimit` | Preserve the upstream 65-node fixture's default first/last inner messages (`level 62`/`level 31`) and assert every node's type/message plus the exact 32-inner cutoff. All twenty-two original launch/attach configuration cases remain, including the 50-inner cutoff (`level 62`/`level 13`) and limit 100 exposing the complete finite chain (`level 62`/`innermost`). They prove zero, small and unsigned-maximum limits, invalid/overflow fallback, complete unsigned-decimal parsing, request overrides and reset to the default. Trailing characters (`2suffix`, `2 `) require the default 32 in both launch and attach. Five additional launches retain both upstream revisions: `1f7fe078` uses a 50-depth chain with first inner `level 48`, default 32 ending at `level 17`, limit 16 at `level 33`, and limit 100 at `innermost`; `740811da` returns to 64 depth with limit 16 ending at `level 47` and limit 128 at `innermost`. Every node has its exact type/message and zero/one-child count checked, including fully populated terminal nodes without empty placeholders. The same assertions pass exact native `740811da` and managed; the JSON move-semantics change needs no managed production fix. |

The unchanged function-name fixture was first proven against exact native
`2d9b3d4c`, then failed against actual unchanged managed main `c77cc22e` on the
first malformed `()` request, and passed with the rebased fix. The seven focused
breakpoint/function/hit-condition fixtures passed on both adapters with SDK
10.0.404/runtime 10.0.15. These are local preparation results; final qualification
still requires the evaluator's pinned SDK 10.0.401/runtime 10.0.14 and full gates.

The launch rejection/recovery additions were first qualified against unchanged
managed main `94f11ef5`. After updating onto main `e79a5523`, the identical
`TestLaunchSettingsPrecedence` and `TestLaunchSettings` fixtures passed 2/2
against preserved exact native `2dcb490e` first, then current-CI native `2d9b3d4c`,
then archive-built unchanged main `e79a5523`. Each adapter passed all eight new
correlated failures and immediate same-session recoveries with exact profile
values, zero exit and termination. Both managed baselines were already correct;
no production fix, manufactured red or native-pin update was needed. These are
bounded macOS arm64 results with SDK 10.0.404/runtime 10.0.15, not the evaluator's
pinned SDK/runtime or full cross-platform qualification.

The exception-option fixture passed exact archive-built native `b5432b4e` first,
then failed against archive-built unchanged managed main `9d150bb5` when a numeric
optional condition was rejected as a string conversion error, and passed with the
shared input-boundary fix. The original 37-case fixture passed on all three.
These are local macOS arm64 results with SDK 10.0.404/runtime 10.0.15; they do not
replace final CI's full native/managed, platform or artifact gates. Portable
remote DAP contracts additionally check typed outgoing rules, unsupported-feature
rejection and no private request on invalid input; they are not device/runtime
proof.

These are bounded scenarios, not full coverage of hit conditions or exception handling.
`TestBreakpointWithoutStop` uses target stdin as a synchronization gate: after the
entry continue, both source and function breakpoints are installed while the
target is running, then a running-target evaluate request releases stdin. It
still requires the exact source stop, function-entry stop and successful exit
with the existing receive deadline. The former 15-second target sleep and
5-second controller sleep raced that 10-second deadline; no debugger callback
could guarantee arriving before it. This fixture uses no timing sleeps or
retries and runs unchanged against the native and managed debuggers.

The evaluation recovery contract is proven against preserved native master
`fc643dfc`. `.github/workflows/evaluation.yml` pins the same Linux .NET 10.0.14
container/runtime for native and managed debugging, proves the immutable pre-fix
managed baseline fails, and preserves runtime/DBI/DAC hashes, raw protocol and
engine logs. It requires five native and managed focused/unchanged-suite runs
under CPU load, followed by full current-native (`2d9b3d4c`), managed and smoke suites.
Normal evaluations suspend peer threads. As in native, timeout recovery may
temporarily run those peers so dependencies can complete before abort settlement;
their original frozen state is restored afterwards. Cross-thread notification
abort does not itself complete an evaluation: the real terminal callback must
arrive before the next getter starts.

The remote-console configuration and complete-number inner-limit contracts
are proven against the exact upstream `9e970bf0` native reference. To exercise
the inherited console-port baseline as well as request overrides:

```powershell
$env:DNCDBG_REMOTECONSOLE_PORT = "33218"
pwsh ./test-suite/run_tests.ps1 TestRemoteConsoleConfiguration
Remove-Item Env:DNCDBG_REMOTECONSOLE_PORT
```

The continue-response/event and single-thread stepping-event assertions are
proven against the exact upstream `dc120e19` native reference, including the
already-running request whose result differs from the requested mode.
The hit-count fixtures and updated upstream breakpoint/logpoint fixtures are
proven against the exact upstream `2feb374c` native reference. They intentionally
replace the old evaluator-based hit-condition contract with parsed counts;
retired expression scenarios are covered by request-time rejection assertions,
not weakened or accepted as expressions. Other existing acceptance assertions
remain unchanged. The historical native
checkpoint predates per-type negation, wildcard prefixes, and the configurable
`unhandled` filter. See [exception conditions](../docs/exception_conditions.md)
for the supported syntax.
Exception-filter helpers use the upstream `WithConditions` naming; DAP
`filterOptions` remains the protocol field name.
`TestFormatSpecifiersAc` also verifies that a loaded type without a static
constructor can expose its default static field with implicit evaluation
disabled, alongside its existing initialized/uninitialized constructor checks.
This does not establish initialization observation for symbol-free/ReadyToRun
types or constructors that completed before attach.

# How to add a new test

- move to the test-suite directory;

- create a project whose directory and project file names both start with
  `Test`, for example:
```
    $ dotnet new console -o TestNewFeature
```

- add a reference to the DbgTest library and `Context.cs` file to the `ItemGroup` section by editing `TestNewFeature.csproj`, for example:
```
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\DbgTest\DbgTest.csproj" />
    <Compile Include="..\ScriptContext\Context.cs" />
  </ItemGroup>

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework Condition="'$(DNCDBG_TARGET_FRAMEWORK)' != ''">$(DNCDBG_TARGET_FRAMEWORK)</TargetFramework>
    <TargetFramework Condition="'$(DNCDBG_TARGET_FRAMEWORK)' == ''">net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

The runner discovers the new project automatically; no test manifest needs to
be updated.
