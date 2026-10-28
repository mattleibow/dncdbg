using System.Runtime.InteropServices;
using System.Text.Json;
using ClrDebug;
using Microsoft.Diagnostics.NETCore.Client;

internal static class StartupProbe
{
    public static int LoadLibrary(string path)
    {
        try
        {
            nint library = NativeLibrary.Load(Path.GetFullPath(path));
            NativeLibrary.Free(library);
            Console.WriteLine(JsonSerializer.Serialize(new { Phase = "native-load", Success = true, Path = path }));
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Phase = "native-load", Success = false, Path = path, Error = error.GetType().Name, error.Message }));
            return 2;
        }
    }

    public static async Task<int> RunAsync(int pid, string runtimePath, string reportPath, string? libraryDirectory = null)
    {
        var phases = new List<object>();
        File.WriteAllText(reportPath + ".jsonl", "");
        void Record(object phase)
        {
            phases.Add(phase);
            File.AppendAllText(reportPath + ".jsonl", JsonSerializer.Serialize(phase) + Environment.NewLine);
        }
        Record(new
        {
            Phase = "host-environment",
            Runtime = RuntimeInformation.FrameworkDescription,
            RuntimeInformation.RuntimeIdentifier,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            CurrentDirectory = Environment.CurrentDirectory,
            TempDirectory = Path.GetTempPath(),
            TMPDIR = Environment.GetEnvironmentVariable("TMPDIR"),
        });
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            try
            {
                _ = await Task.Run(() => new DiagnosticsClient(pid).GetProcessEnvironment(), timeout.Token).WaitAsync(timeout.Token);
                Record(new { Phase = "diagnostic-environment-query", Success = true });
            }
            catch (Exception error)
            {
                Record(new { Phase = "diagnostic-environment-query", Success = false, Error = error.GetType().Name, error.Message });
            }
        }

        string file = OperatingSystem.IsMacOS() ? "libdbgshim.dylib" : OperatingSystem.IsWindows() ? "dbgshim.dll" : "libdbgshim.so";
        string shimPath = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", file);
        var shim = new DbgShim(NativeLibrary.Load(shimPath));
        Record(new { Phase = "enumerate-clrs-start" });
        HRESULT enumerationStatus = shim.TryEnumerateCLRs(pid, out EnumerateCLRsResult enumeration);
        Record(new { Phase = "enumerate-clrs", HResult = enumerationStatus.ToString(), Count = enumeration.Items?.Length ?? 0 });
        if (enumerationStatus == HRESULT.S_OK)
            shim.CloseCLREnumeration(enumeration);

        bool factorySucceeded = false;
        HRESULT versionStatus = shim.TryCreateVersionStringFromModule(pid, Path.GetFullPath(runtimePath), out string version);
        Record(new { Phase = "runtime-version", HResult = versionStatus.ToString(), RuntimePath = runtimePath, Version = version });
        if (versionStatus == HRESULT.S_OK)
        {
            HRESULT factoryStatus = shim.TryCreateDebuggingInterfaceFromVersionEx(CorDebugInterfaceVersion.CorDebugVersion_2_0, version, out CorDebug debugger);
            Record(new { Phase = "factory-default", HResult = factoryStatus.ToString(), InterfaceReturned = debugger != null });
            if (factoryStatus == HRESULT.S_OK && debugger != null)
            {
                try
                {
                    debugger.Initialize();
                    factorySucceeded = true;
                    Record(new { Phase = "initialize", Success = true });
                }
                catch (Exception error)
                {
                    Record(new { Phase = "initialize", Success = false, Error = error.GetType().Name, error.Message });
                }
                finally { debugger.Terminate(); }
            }
            if (libraryDirectory != null)
            {
                var provider = new StartupLibraryProvider(Path.GetFullPath(libraryDirectory));
                HRESULT providerStatus = shim.TryCreateDebuggingInterfaceFromVersion3(
                    CorDebugInterfaceVersion.CorDebugVersion_2_0, version, null!, provider, out CorDebug provided);
                Record(new { Phase = "factory-provider3", HResult = providerStatus.ToString(), InterfaceReturned = provided != null, ProviderRequests = provider.Requests });
                if (providerStatus == HRESULT.S_OK && provided != null)
                {
                    try
                    {
                        provided.Initialize();
                        factorySucceeded = true;
                        Record(new { Phase = "initialize-provider3", Success = true });
                    }
                    catch (Exception error)
                    {
                        Record(new { Phase = "initialize-provider3", Success = false, Error = error.GetType().Name, error.Message });
                    }
                    finally { provided.Terminate(); }
                }
                GC.KeepAlive(provider);
            }
        }
        var delivered = new TaskCompletionSource<(string Status, bool HasInterface)>(TaskCreationOptions.RunContinuationsAsynchronously);
        RuntimeStartupCallback callback = (debugger, _, status) =>
        {
            bool hasInterface = debugger != null;
            try
            {
                if (debugger != null)
                {
                    try { debugger.Initialize(); }
                    finally { debugger.Terminate(); }
                }
                delivered.TrySetResult((status.ToString(), hasInterface));
            }
            catch (Exception error)
            {
                delivered.TrySetResult(("CallbackFailure:" + error.GetType().Name, false));
            }
        };
        Record(new { Phase = "register-startup-start" });
        nint registration = 0;
        try
        {
            HRESULT registered = shim.TryRegisterForRuntimeStartup(pid, callback, 0, out registration);
            Record(new { Phase = "register-startup", HResult = registered.ToString(), TokenReturned = registration != 0 });
            if (registered == HRESULT.S_OK)
            {
                try
                {
                    var result = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Record(new { Phase = "startup-callback", Delivered = true, HResult = result.Status, InterfaceReturned = result.HasInterface });
                }
                catch (TimeoutException)
                {
                    Record(new { Phase = "startup-callback", Delivered = false, TimeoutSeconds = 5 });
                }
            }
        }
        finally
        {
            if (registration != 0)
            {
                Record(new { Phase = "unregister-startup-start" });
                shim.UnregisterForRuntimeStartup(registration);
                Record(new { Phase = "unregister-startup-complete" });
            }
            GC.KeepAlive(callback);
        }
        var report = new { ProcessId = pid, FactoryAvailable = factorySucceeded, BreakpointGate = false, Phases = phases };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(reportPath, json + Environment.NewLine);
        Console.WriteLine(json);
        GC.KeepAlive(shim);
        // Factory availability is only a phase gate; the separate mobile breakpoint gate remains false.
        return factorySucceeded ? 0 : 2;
    }
}
