# How to launch tests locally

- On Linux and macOS:
```
    launch all tests:
    $ ./run_tests.sh
    or
    $ ./run_tests.sh <test-name> [<test-name>]
    or
    $ DNCDBG=<path-to-dncdbg> ./run_tests.sh <test-name> [<test-name>]
```

- On Windows:
```
    launch all tests:
    $ powershell.exe -executionpolicy bypass -File run_tests.ps1
    or
    $ powershell.exe -executionpolicy bypass -File run_tests.ps1 <test-name> [<test-name>]
```

The target framework can be overridden with the `DNCDBG_TARGET_FRAMEWORK` environment variable (for example, `net8.0`); by default, the tests target `net10.0`.

For the managed debugger, build or publish from the repository root first:

```sh
dotnet publish src/DncDbg.Adapter -c Release -o bin
cd test-suite
DNCDBG=../bin/dncdbg bash run_tests.sh TestBreakpoint TestEvaluate TestStackTrace TestMemoryReferences TestReadWriteMemory
```

Run the additional cross-platform CLI/protocol smoke checks from the repository root:

```sh
dotnet run --project tests/DncDbg.SmokeTests -- bin/dncdbg
```

These checks do not modify the existing Runner, ScriptContext, fixtures, or
assertions. Their diagnostic logs are written under `build/validation/cli-smoke`.

The smoke runner also exercises startup/unregistration barriers, rejection of
late startup callbacks, cancellation of a delayed external launcher, a 1 MiB
stdin write to a non-reading target followed by disconnect, detached-child
survival beyond disposal timeouts, and pause during function evaluation followed
by a heartbeat after exactly one continue. Run these safety checks alone with:

```sh
dotnet run --project tests/DncDbg.SmokeTests -- --safety bin/dncdbg
```

The self-hosted safety fixtures are separate from the unchanged acceptance
suite. A surviving detached fixture is explicitly cleaned up by the smoke runner.

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
