# Building on Linux

Install the stable .NET 10 SDK selected by [`global.json`](../global.json)
and your distribution's .NET runtime prerequisites. A C++ compiler and CMake
are not required. Run from the repository root:

```sh
dotnet build
dotnet publish src/DncDbg.Adapter -c Release -o bin
./bin/dncdbg --buildinfo
dotnet run --project tests/DncDbg.SmokeTests -- bin/dncdbg
pwsh ./test-suite/run_tests.ps1
```

The apphost requires .NET 10 unless published self-contained. Keep all published
assemblies, native runtime assets and licenses together. For example:

```sh
dotnet publish src/DncDbg.Adapter -c Release -r linux-x64 --self-contained true -o bin/linux-x64
```

Use matching debugger/target architectures and an RID supported by the CLR
debugging packages. CLR assets are restored from NuGet, not built locally.
Attaching to an existing process is subject to the OS ptrace/security policy.

The test suite defaults to `net10.0`; `DNCDBG_TARGET_FRAMEWORK` overrides the
fixture target when the corresponding SDK/runtime is installed.
See [local testing](../test-suite/README.md) for focused test selection.
