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
