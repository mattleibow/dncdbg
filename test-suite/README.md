# How to launch tests locally

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

Run the additional cross-platform CLI/protocol smoke checks from the repository root:

```sh
dotnet run --project tests/DncDbg.SmokeTests -- bin/dncdbg-dap
```

These checks do not modify the existing Runner, ScriptContext, fixtures, or
assertions. Their diagnostic logs are written under `build/validation/cli-smoke`.

The smoke runner also exercises startup/unregistration barriers, rejection of
late startup callbacks, cancellation of a delayed external launcher, a 1 MiB
stdin write to a non-reading target followed by disconnect, detached-child
survival beyond disposal timeouts, and pause during function evaluation followed
by a heartbeat after exactly one continue. They also arm static-initializer and
user breakpoints, detach, verify internal breakpoint cleanup, dispose the
debugger, and let the detached target complete its watched initializer.
Run these safety checks alone with:

```sh
dotnet run --project tests/DncDbg.SmokeTests -- --safety bin/dncdbg-dap
```

The self-hosted safety fixtures are separate from the unchanged acceptance
suite. A surviving detached fixture is explicitly cleaned up by the smoke runner.

## Focused native migration contracts

Run the same tests against either adapter without changing the debuggee or assertions:

```powershell
pwsh ./test-suite/run_tests.ps1 TestHitConditionSyntax TestContinueResponse TestUnhandledExceptionCondition TestExceptionConditionMatching TestUnhandledExceptionFilter
$env:DNCDBG = "/absolute/path/to/another/debugger"
pwsh ./test-suite/run_tests.ps1 TestHitConditionSyntax TestContinueResponse TestUnhandledExceptionCondition TestExceptionConditionMatching TestUnhandledExceptionFilter
```

These tests preserve observable native behavior, not assumptions about DAP syntax:

| Test | Exact contract |
| --- | --- |
| `TestHitConditionSyntax` | Across seven visits, `5` and `>=5` stop on visits 5, 6 and 7; `==5` and `%5` stop only on 5, with no removal diagnostics. All sequences are collected before comparison. Forty-four valid source/function requests and 56 invalid ones prove unsigned bounds, zero/modulo-zero, ASCII-space-only normalization, exact error messages, and rejection of expressions, signs, overflow, tabs/newlines, suffixes and NUL characters. |
| `TestBreakpointHitCondition` | Exact upstream source/function fixture covers all seven forms (`N`, `<N`, `<=N`, `==N`, `>N`, `>=N`, `%N`) and every expected source/frame stop over seven visits, plus process exit. |
| `TestHitConditionLifecycle` | IDs and counts survive unchanged/equivalent/changed thresholds for source and function breakpoints; false expression conditions do not count; logpoint output is gated after counting. Invalid replacement requests preserve existing breakpoints; removal/re-addition resets the count. Exact stop sequences and expected output are required. |
| `TestContinueResponse` | The continue response body contains a Boolean `allThreadsContinued`: `true` for explicit `singleThread:false` and for an omitted `singleThread`, and `false` for `singleThread:true`. This tests the response, not multi-thread scheduling. All threads are resumed before process shutdown. |
| `TestUnhandledExceptionCondition` | With `!System.InvalidOperationException` on `all` and `user-unhandled`, a caught exception does not stop execution. An uncaught exception of the same type still stops at the throw line, with `exceptionInfo.breakMode == "unhandled"`, `exceptionId == "CLR/System.InvalidOperationException"`, and the matching full type name. These two filters do not suppress the final unhandled stop. |
| `TestExceptionConditionMatching` | Thirty-seven cases cover per-token negation, negative-only conditions, positive/negative order, exclusion priority, ignored lone `!`, empty conditions, additive unconditional entries, duplicate filter options, comma/ASCII-whitespace separators, and wildcard prefixes. Wildcard cases include `*`, `!*`, positive/negative prefix conflicts across entries, ordinal case sensitivity, duplicate prefixes, zero-length suffixes, and literal internal/repeated asterisks and `?`. Every stop and its exact exception details/source line is collected before comparing the complete matrix. |
| `TestExceptionWildcardCondition` | The unchanged upstream fixture checks `*`, `System.*`, `!System.Null*, *`, broader negated prefixes overriding specific positive prefixes, and `!*` overriding a positive prefix. Exact exception source/type/break-mode assertions and both source-breakpoint checkpoints are preserved. |
| `TestUnhandledExceptionFilter` | Eighteen sessions cover the default unhandled stop, nonmatching positive conditions, matching/nonmatching exact and prefix exclusions, conflicting conditions in both orders, unconditional entries, lone `!`, and `!*`. Upstream scenarios include an exact nonmatching `!System.Exception` (no base-type matching) and a thrown stop followed by a suppressed final stop. Observed unhandled stops are continued through actual process exit rather than terminated by the test. Suppressed final stops require a nonzero exit code and both exited/terminated events. |
| `TestInnerExceptionLimit` | Preserve the upstream 65-node fixture's default first/last inner messages (`level 62`/`level 31`) and assert every node's type/message plus the exact 32-inner cutoff. Twenty additional launch/attach sessions prove zero, small and unsigned-maximum limits, invalid/overflow fallback, native numeric-prefix parsing, request overrides and reset to the default. These include the upstream 50-inner cutoff (`level 62`/`level 13`) and limit 100 exposing the complete finite chain (`level 62`/`innermost`). Fully populated terminal nodes are required without empty placeholders. |

These are bounded scenarios, not full coverage of hit conditions or exception handling.
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
