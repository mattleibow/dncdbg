using System.Runtime.InteropServices;

namespace DncDbg.Remote;

public enum RemoteRuntimeKind { Unspecified, Mono, CoreClr, NativeAot }

public static class RemoteRuntimeCapability
{
    public static RemoteCapabilities Detect(RemoteRuntimeKind runtime = RemoteRuntimeKind.Unspecified)
    {
        string platform = OperatingSystem.IsIOS() ? "iOS" : OperatingSystem.IsAndroid() ? "Android" : "Desktop";
        string blocker = runtime == RemoteRuntimeKind.Mono
            ? "Mono uses the soft-debugger protocol, not ICorDebug. No public ICorDebug device backend is installed."
            : "No qualified target agent is installed. Deploy the RID-specific agent package and its exact runtime/DBI/DAC manifest.";
        return new($"{RuntimeInformation.FrameworkDescription} ({runtime})", platform, false, blocker);
    }

    public static IRemoteDebugBackend CreateUnavailableBackend(RemoteRuntimeKind runtime = RemoteRuntimeKind.Unspecified,
        RemoteCapabilities? capabilities = null) => new UnavailableBackend(runtime, capabilities);

    private sealed class UnavailableBackend(RemoteRuntimeKind runtime, RemoteCapabilities? capabilities) : IRemoteDebugBackend
    {
        public RemoteCapabilities Capabilities { get; } = capabilities ?? Detect(runtime);
        private PlatformNotSupportedException Unavailable() => new(Capabilities.Blocker);
        public ValueTask<RemoteBreakpoint[]> SetBreakpointsAsync(RemoteBreakpoints request, CancellationToken cancellationToken) => throw Unavailable();
        public ValueTask StartAsync(CancellationToken cancellationToken) => throw Unavailable();
        public ValueTask<RemoteStop> WaitForStopAsync(CancellationToken cancellationToken) => throw Unavailable();
        public ValueTask<RemoteFrame[]> StackAsync(long stop, CancellationToken cancellationToken) => throw Unavailable();
        public ValueTask<RemoteValue> EvaluateAsync(RemoteEvaluation request, CancellationToken cancellationToken) => throw Unavailable();
        public ValueTask ContinueAsync(long stop, CancellationToken cancellationToken) => throw Unavailable();
        public ValueTask ReleaseAsync(long[] handles, CancellationToken cancellationToken) => throw Unavailable();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
