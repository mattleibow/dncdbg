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
