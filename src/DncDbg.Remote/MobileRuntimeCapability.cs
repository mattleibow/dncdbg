using System.Runtime.InteropServices;

namespace DncDbg.Remote;

public enum RemoteRuntimeKind { Unspecified, Mono, CoreClr, NativeAot }

public static class MobileRuntimeCapability
{
    public static RemoteCapabilities Detect(RemoteRuntimeKind runtime = RemoteRuntimeKind.Unspecified)
    {
        string platform = OperatingSystem.IsIOS() ? "iOS" : OperatingSystem.IsAndroid() ? "Android" : "Desktop";
        string blocker = runtime == RemoteRuntimeKind.Mono
            ? "Mono uses the soft-debugger protocol, not ICorDebug. No public ICorDebug device backend is installed."
            : "No validated public in-process ICorDebug startup provider is installed. Private DBI exports are not permitted.";
        return new($"{RuntimeInformation.FrameworkDescription} ({runtime})", platform, false, blocker);
    }

    public static IRemoteDebugBackend CreateUnavailableBackend(RemoteRuntimeKind runtime = RemoteRuntimeKind.Unspecified) => new UnavailableBackend(runtime);

    private sealed class UnavailableBackend(RemoteRuntimeKind runtime) : IRemoteDebugBackend
    {
        public RemoteCapabilities Capabilities { get; } = Detect(runtime);
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
