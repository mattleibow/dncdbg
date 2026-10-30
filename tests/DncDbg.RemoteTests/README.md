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
`test_mobile_processes.py`, `test_mobile_runner.py`, shared `process_tools.py`).
`--tunnel-contracts` additionally runs real socket fragmentation, half-close,
backpressure, reconnect, timeout and transactional listener ownership checks.
Run the whole portable host group
through the same documented console entrypoint used by CI:

```sh
dotnet run --project tests/DncDbg.MobileTests -c Release -- --platform contracts
```

The console entrypoint builds the adapter and this project before invoking
helpers. Python supplies the existing framed DAP test driver and POSIX process/
ADB fault harness; C++ host fakes never load a runtime or prove mobile execution.
These are not upstream parity projects and do not belong in `test-suite/Test*`.
The [canonical native-host architecture suite](../DncDbg.NativeHostTests/README.md)
is the required **actual-runtime** gate on macOS/Linux/Docker, not the older
managed `EngineBackend` desktop proof. See also
[real mobile proof](../DncDbg.MobileTests/README.md).
