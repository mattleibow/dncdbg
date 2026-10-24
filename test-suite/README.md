# How to launch tests locally

- On Linux and macOS:
```
    launch all tests:
    $ ./run_tests.sh
    or
    $ ./run_tests.sh <test-name> [<test-name>]
    or
    $ DNCDBG=<path-to-netcoredbg> ./run_tests.sh <test-name> [<test-name>]
```

- On Windows:
```
    launch all tests:
    $ powershell.exe -executionpolicy bypass -File run_tests.ps1
    or
    $ powershell.exe -executionpolicy bypass -File run_tests.ps1 <test-name> [<test-name>]
```

The target framework can be overridden with the `DNCDBG_TARGET_FRAMEWORK` environment variable (for example, `net8.0`); by default, the tests target `net10.0`.

## Public native CI

The `Native CI` workflow on `mattleibow-native-parity-tests` builds and installs
the historical native debugger in Release on Ubuntu, macOS, and Windows using
the documented CMake flows and the public .NET 10 SDK. It runs every test in the
platform's historical runner manifest, including the Windows-only WinForms case,
the migration-contract tests, and `TestStringEscaping` copied unchanged from
managed `main`. Linux disables the Yama sibling-process attach restriction for
the attach tests.

The full build and DAP/test output is retained in the Actions job logs. The job
summary lists each test result and exact totals, or reports that setup/build
prevented tests from running. Matrix jobs do not cancel one another on failure.

## Focused native migration contracts

Run the same tests against either adapter without changing the debuggee or assertions:

```sh
./run_tests.sh TestHitConditionSyntax TestContinueResponse TestUnhandledExceptionCondition
DNCDBG=/absolute/path/to/managed/dncdbg-dap ./run_tests.sh TestHitConditionSyntax TestContinueResponse TestUnhandledExceptionCondition
```

These tests preserve observable native behavior, not assumptions about DAP syntax:

| Test | Exact contract |
| --- | --- |
| `TestHitConditionSyntax` | Across seven visits to each source breakpoint, `5` stops on visits 6 and 7. `==5`, `>=5`, and `%5` each stop on visits 1 through 7 and emit exactly one stderr diagnostic identifying the hit condition and saying it will be removed. All four stop sequences and diagnostic counts are collected before comparison. Native evaluates `hitCount > <hitCondition>`; this does not establish support for equality, greater-or-equal, or modulo syntax. |
| `TestContinueResponse` | The continue response body contains a Boolean `allThreadsContinued`: `true` for explicit `singleThread:false` and for an omitted `singleThread`, and `false` for `singleThread:true`. This tests the response, not multi-thread scheduling. All threads are resumed before process shutdown. |
| `TestUnhandledExceptionCondition` | With `!System.InvalidOperationException` on both native-supported exception filters (`all` and `user-unhandled`), a caught exception does not stop execution. An uncaught exception of the same type still stops at the throw line, with `exceptionInfo.breakMode == "unhandled"`, `exceptionId == "CLR/System.InvalidOperationException"`, and the matching full type name. Native does not expose a separately configurable `unhandled` filter. |

These are bounded scenarios, not full coverage of hit conditions or exception handling.

# How to add a new test

- move to the test-suite directory;

- create a new project in the test-suite folder:
```
    $ dotnet new console -o NewTest
```

- add a reference to the DbgTest library and `Context.cs` file to the `ItemGroup` section by editing the `NewTest.csproj` file, for example:
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

- add the test name to the ALL_TEST_NAMES list in the `run_tests.sh` and `run_tests.ps1` scripts.
