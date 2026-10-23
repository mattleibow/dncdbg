# Building from Source Code on Windows OS

## Managed debugger (.NET 10 / C# 14)

Install the stable .NET SDK selected by `global.json`. From the repository root
in PowerShell:

```powershell
dotnet build
dotnet publish src/DncDbg.Adapter -c Release -o bin
.\bin\dncdbg.exe --buildinfo
$env:DNCDBG = (Resolve-Path .\bin\dncdbg.exe).Path
cd test-suite
.\run_tests.ps1
```

Use matching debugger/target architectures. NuGet supplies the CLR debugging and
native Windows PDB assets. The managed build does not require the Visual C++
toolchain or CMake.

## Legacy native reference build

The instructions below apply only to the retained C++ reference implementation
while managed compatibility work is in progress.

## Prerequisites

Download and install the latest **Microsoft Visual Studio** from here: https://visualstudio.microsoft.com/downloads

*During installation of Visual Studio, you should install all of the options required for C++ development on Windows. Plus, enable individual components: `cmake` and `git`.*


## Compiling

Start a VS PowerShell session and configure the build with the following commands given in the debugger's source tree:

```
C:\Users\localuser\dncdbg> md build
C:\Users\localuser\dncdbg> cd build
C:\Users\localuser\dncdbg\build> cmake .. -G "Visual Studio 18 2026"
```

Option `-G` specifies which instance of Visual Studio should build the project.
Note, the minimum requirement for the debugger's build is **Visual Studio 2019**.

If you want to run tests after a successful build, then you should add the following option:
`-DCMAKE_INSTALL_PREFIX="$PWD.Path\..\bin"`

Add your build type (`Release` or `Debug`), for example:
`-DCMAKE_BUILD_TYPE=Release`

To build with Address Sanitizer, add the option
`-DASAN=1`

To build with case-sensitive file name collision, add the option
`-DCASE_SENSITIVE_FILENAME_COLLISION=1`

To compile and install, use the following command:

```
C:\Users\localuser\dncdbg\build> cmake --build . --target install --parallel $env:NUMBER_OF_PROCESSORS --config Release
```

As an example, the complete build sequence for a VS PowerShell session might look like:

```
C:\Users\localuser\dncdbg> md build
C:\Users\localuser\dncdbg> cd build
C:\Users\localuser\dncdbg\build> cmake .. -G "Visual Studio 18 2026" -DCMAKE_INSTALL_PREFIX="$PWD.Path\..\bin" -DCMAKE_BUILD_TYPE=Release
C:\Users\localuser\dncdbg\build> cmake --build . --target install --parallel $env:NUMBER_OF_PROCESSORS --config Release
```
