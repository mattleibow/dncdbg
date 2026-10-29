#if DEBUG
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace DncDbg.MauiTestApp;

internal sealed record StartupSnapshot(
    int ProcessId,
    string Runtime,
    string BaseDirectory,
    string CurrentDirectory,
    string TemporaryDirectory,
    string NativeLibraryDirectory,
    Dictionary<string, string?> EnvironmentVariables)
{
    public static StartupSnapshot Capture()
    {
        string nativeDirectory = AppContext.BaseDirectory;
#if ANDROID
        nativeDirectory = Android.App.Application.Context.ApplicationInfo?.NativeLibraryDir ?? nativeDirectory;
#endif
        string[] names =
        [
            "TMPDIR", "TMP", "TEMP",
            "DOTNET_EnableDiagnostics", "DOTNET_EnableDiagnostics_Debugger", "DOTNET_EnableDiagnostics_IPC",
            "DOTNET_DiagnosticPorts", "DOTNET_DefaultDiagnosticPortSuspend",
            "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH",
        ];
        return new(Environment.ProcessId, RuntimeInformation.FrameworkDescription, AppContext.BaseDirectory,
            Environment.CurrentDirectory, Path.GetTempPath(), nativeDirectory,
            names.ToDictionary(name => name, Environment.GetEnvironmentVariable));
    }
}

[JsonSerializable(typeof(StartupSnapshot))]
internal partial class StartupSnapshotJson : JsonSerializerContext;
#endif
