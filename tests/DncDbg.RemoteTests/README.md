# Portable remote and orchestration contracts

This .NET 10 console project is built by `DncDbg.slnx`, just like SmokeTests and
DockerTests. It covers framing, credentials, capability/private ABI rejection,
lifecycle and a real **desktop** remote breakpoint. It needs no mobile workload:

```sh
dotnet run --project tests/DncDbg.RemoteTests -c Release
```

POSIX fake-device DAP/ADB contracts, native ownership fakes, process/listener
ownership, artifact-isolation and mobile-runner regressions live alongside this
project (`test_dap.py`, `fake_adb.py`, `NativeContracts.cpp`,
`test_mobile_processes.py`, `test_mobile_runner.py`). Run the whole host group
through the same documented console entrypoint used by CI:

```sh
dotnet run --project tests/DncDbg.MobileTests -c Release -- --platform contracts
```

The console entrypoint builds the adapter and this project before invoking
helpers. Python supplies the existing framed DAP test driver and POSIX process/
ADB fault harness; C++ host fakes never load a runtime or prove mobile execution.
These are not upstream parity projects and do not belong in `test-suite/Test*`.
See [real mobile proof](../DncDbg.MobileTests/README.md).
