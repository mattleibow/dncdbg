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
`Test*.csproj`. Windows-only projects are included automatically on Windows and
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
| `TestHitConditionSyntax` | Across seven visits to each source breakpoint, `5` stops on visits 6 and 7. `==5`, `>=5`, and `%5` each stop on visits 1 through 7 and emit exactly one stderr diagnostic identifying the hit condition and saying it will be removed. All four stop sequences and diagnostic counts are collected before comparison. Native evaluates `hitCount > <hitCondition>`; this does not establish support for equality, greater-or-equal, or modulo syntax. |
| `TestContinueResponse` | The continue response body contains a Boolean `allThreadsContinued`: `true` for explicit `singleThread:false` and for an omitted `singleThread`, and `false` for `singleThread:true`. This tests the response, not multi-thread scheduling. All threads are resumed before process shutdown. |
| `TestUnhandledExceptionCondition` | With `!System.InvalidOperationException` on `all` and `user-unhandled`, a caught exception does not stop execution. An uncaught exception of the same type still stops at the throw line, with `exceptionInfo.breakMode == "unhandled"`, `exceptionId == "CLR/System.InvalidOperationException"`, and the matching full type name. These two filters do not suppress the final unhandled stop. |
| `TestExceptionConditionMatching` | Twelve cases cover per-token negation, negative-only conditions, positive/negative order, exclusion priority, ignored lone `!`, empty conditions, additive unconditional entries, duplicate filter options, and comma/whitespace separators. Every stop and its exact exception details/source line is collected before comparing the complete matrix. |
| `TestUnhandledExceptionFilter` | Eight sessions cover the default unhandled stop, nonmatching positive conditions, matching/nonmatching exclusions, conflicting conditions in both orders, unconditional entries, and lone `!`. The upstream exclusion scenario must exit without any stopped event, with a nonzero exit code and both exited/terminated events. |

These are bounded scenarios, not full coverage of hit conditions or exception handling.
The exception-condition matrices and updated upstream exception/stack-trace
fixtures are proven against upstream native `53715b0e`; the historical native
checkpoint predates per-type negation and the configurable `unhandled` filter.
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
