# Third-Party Notices

The licenses below apply to the corresponding incorporated sources and runtime
dependencies. DNCDbg itself is licensed under [`LICENSE`](LICENSE).

## dotnet-debugger

- **Project:** [dotnet-debugger](https://github.com/dvislobokov/dotnet-debugger)
- **Included version:** `0.3.0`
- **Included in:** `src/DncDbg.Adapter`, `src/DncDbg.Engine`, and `src/DncDbg.Protocol` (adapted for DncDbg)
- **License:** MIT License
- **Copyright:** Copyright (c) 2026 dvislobokov and dotnet-debugger contributors
- **License text:** [`src/DncDbg.LICENSE`](src/DncDbg.LICENSE)

## NuGet dependencies

The managed build/publish includes the following packages and their native
runtime assets. Licenses are retained under [`src/licenses`](src/licenses/) and
copied to `licenses/` beside the published executable.

| Component | Version | License / notices |
| --- | --- | --- |
| [ClrDebug](https://github.com/lordmilko/ClrDebug) | 0.4.2 | MIT; Copyright (c) 2022 lordmilko. [`ClrDebug.LICENSE`](src/licenses/ClrDebug.LICENSE) |
| [Microsoft.CodeAnalysis.CSharp and Microsoft.CodeAnalysis.Common](https://github.com/dotnet/roslyn) | 5.9.0 | MIT; Microsoft Corporation. [`Microsoft.MIT.LICENSE`](src/licenses/Microsoft.MIT.LICENSE), [`Roslyn.ThirdPartyNotices.rtf`](src/licenses/Roslyn.ThirdPartyNotices.rtf) |
| [Microsoft.Diagnostics.DbgShim](https://github.com/dotnet/diagnostics) | 10.0.745401 | MIT; .NET Foundation and Contributors / Microsoft Corporation. [`Microsoft.MIT.LICENSE`](src/licenses/Microsoft.MIT.LICENSE) |
| [Microsoft.Diagnostics.NETCore.Client](https://github.com/dotnet/diagnostics) | 0.2.750501 | MIT; .NET Foundation and Contributors / Microsoft Corporation. [`Microsoft.MIT.LICENSE`](src/licenses/Microsoft.MIT.LICENSE) |
| Microsoft.Extensions.DependencyInjection.Abstractions / Microsoft.Extensions.Logging.Abstractions | 8.0.2 / 8.0.3 | MIT; .NET Foundation and Contributors / Microsoft Corporation. [`Microsoft.MIT.LICENSE`](src/licenses/Microsoft.MIT.LICENSE) |
| [Microsoft.DiaSymReader](https://github.com/dotnet/symreader) | 2.0.0 | MIT; Microsoft Corporation. [`Microsoft.MIT.LICENSE`](src/licenses/Microsoft.MIT.LICENSE) |
| Microsoft.DiaSymReader.Native | 17.0.0-beta1.21524.1 | Microsoft .NET Library license terms, preserved verbatim from the package: [`Microsoft.DiaSymReader.Native.LICENSE`](src/licenses/Microsoft.DiaSymReader.Native.LICENSE) |

The full authoritative license and notice texts are shipped at the paths above
and must remain with redistributed components. Native runtime dependencies are
still required; their implementation is supplied by these packages rather than
built from vendored C/C++ sources.

The former C++ implementation and its vendored dependency sources are no longer
distributed in this tree. Their original sources and notices remain in Git
history, including [checkpoint f19fe33a](https://github.com/mattleibow/dncdbg/tree/f19fe33a/THIRD-PARTY-NOTICES.md).

## Experimental mobile test dependencies and research

The separate `tests/DncDbg.MauiTestApp` fixture is generated from Microsoft's
MIT-licensed MAUI template, retargeted to .NET 11. It uses
`Microsoft.Maui.Controls` (workload version 11.0.0-rc.1.26451.6),
`Microsoft.Extensions.Logging.Debug` 10.0.0, and, **in Debug only**,
`Microsoft.Maui.DevFlow.Agent` 0.1.0-preview.12.26421.1 and its transitive DevFlow
packages. These Microsoft/.NET Foundation packages are MIT-licensed; see
[`Microsoft.MIT.LICENSE`](src/licenses/Microsoft.MIT.LICENSE) and the packages'
own licenses/notices. They are not dependencies of the published debugger.
The unmodified template Open Sans fonts retain their embedded Google Corporation
copyright notices and Apache-2.0 license. A complete copy is included in the
fixture's [`Resources/Raw/OpenSans-LICENSE.txt`](tests/DncDbg.MauiTestApp/Resources/Raw/OpenSans-LICENSE.txt)
and packaged as a MAUI asset.

The target-native bootstrap was informed by an audit of JaneySprings/clrdbg
`DotNet.Debugging.Remote` at `bb5e52b7d0fdef874288a46a4a2aa0f1643e37d7`
(MIT, Copyright (c) 2026 Nikita Romanov), itself attributing SharpDbg by
MattParkerDev at `e3f2298746c619b16a7d683d39f1fe01aa5ef8b8`
(MIT, Copyright (c) 2026 Matthew Parker). The native startup topology
in `src/DncDbg.Remote.Target/Bootstrap.cpp` now adapts its runtime discovery,
target-side `DllMain` / `CoreCLRCreateCordbObject3` startup and native-owner
approach. Both MIT notices are retained in
[`JaneySprings-clrdbg.LICENSE`](src/licenses/JaneySprings-clrdbg.LICENSE).
The generic machine-word/vtable transport is **not** incorporated; calls use
typed public COM signatures. `CorDebugAbi.h` uses the public `cordebug.idl`
signatures from dotnet/runtime (MIT, .NET Foundation and Contributors; see
[`Microsoft.MIT.LICENSE`](src/licenses/Microsoft.MIT.LICENSE)).

The optional native target helper builds against nlohmann/json **3.12.0** at
`55f93686c01528224f448c19128836e7df245f72`. The build downloads its single header
into ignored `build/native-private/deps` and verifies the SHA-256 recorded in
`src/DncDbg.Remote.Target/dependencies.json` before compiling. Its MIT license
(Copyright (c) 2013-2025 Niels Lohmann) is retained in
[`nlohmann-json.LICENSE`](src/licenses/nlohmann-json.LICENSE).
