// Generated from contract.json by generate_contract.py. Do not edit.
#nullable enable
using System.Text.Json.Serialization;
namespace DncDbg.Remote;

public enum RemoteOperation
{
    Hello = 1,
    SetBreakpoints = 2,
    Start = 3,
    WaitForStop = 4,
    Stack = 5,
    Evaluate = 6,
    Continue = 7,
    Release = 8,
    Disconnect = 9,
    Configure = 10,
    Poll = 11,
    Threads = 12,
    Modules = 13,
    Scopes = 14,
    Variables = 15,
    Step = 16,
    Pause = 17,
    Exceptions = 18,
    ExceptionInfo = 19,
}

public static class RemoteContract { public const int ModelVersion = 2; }

public sealed record RemoteHello(string Token);
public sealed record RemoteCapabilities(string Runtime, string Platform, bool ManagedDebugging, string? Blocker, string? Bootstrap = null, PrivateBootstrapIdentity? SupportedBuild = null, string? Scope = null, int ModelVersion = 0, string? Provider = null, string[]? Features = null);
public sealed record RemoteEndpoint(int Port, string Token, string? SessionId = null, int ProcessId = 0);
public sealed record RemoteBreakpoints(string Source, int[] Lines, RemoteManagedLocation? Location = null, RemoteManagedLocation[]? Locations = null);
public sealed record RemoteManagedLocation(string Mvid, int MethodToken, int IlOffset, string Method, RemoteManagedVariable[] Variables);
public sealed record RemoteManagedVariable(string Name, int Index, bool Parameter, int StartOffset = 0, int EndOffset = 2147483647);
public sealed record RemoteBreakpoint(int Line, bool Verified, string? Message = null, string? Source = null);
public sealed record RemoteStop(long Id, string Reason, int ThreadId);
public sealed record RemoteFrame(long Handle, string Name, string? Source, int Line, long StopId = 0, string? Mvid = null, int MethodToken = 0, int IlOffset = 0);
public sealed record RemoteEvaluation(long Frame, string Expression);
public sealed record RemoteValue(string Value, string Type, long Handle = 0, string? Name = null, int? IndexedVariables = null, int? NamedVariables = null, string? EvaluateName = null);
public sealed record RemoteHandles(long[] Handles);
public sealed record RemoteId(long Id);
public sealed record RemoteEmpty;
public sealed record RemoteConfiguration(RemoteModuleSymbols[] Modules, bool Append = false);
public sealed record RemoteModuleSymbols(string Mvid, string Name, RemoteMethodSymbols[] Methods, RemoteTypeSymbols[] Types);
public sealed record RemoteMethodSymbols(int Token, string Name, RemoteSequencePoint[] Points, RemoteManagedVariable[] Variables);
public sealed record RemoteSequencePoint(int Offset, string Source, int Line, string Checksum, string Algorithm);
public sealed record RemoteTypeSymbols(int Token, string Name, RemoteFieldSymbols[] Fields);
public sealed record RemoteFieldSymbols(int Token, string Name);
public sealed record RemoteEvent(string Kind, long Generation, RemoteStop? Stop = null, RemoteThread? Thread = null, RemoteModule? Module = null, RemoteBreakpoint? Breakpoint = null, int? ExitCode = null);
public sealed record RemoteThread(int Id, string Name);
public sealed record RemoteModule(string Id, string Name, string Path, string Mvid, long Generation);
public sealed record RemoteScope(string Name, long Handle, int Count);
public sealed record RemoteVariables(long Handle, int Start = 0, int Count = 100, string? Filter = null);
public sealed record RemoteStep(long Stop, int ThreadId, string Kind);
public sealed record RemoteExceptions(string[] Filters, string[]? Types = null);
public sealed record RemoteException(string Type, string Description, string BreakMode);

[JsonSerializable(typeof(RemoteEnvelope))]
[JsonSerializable(typeof(PrivateBootstrapIdentity))]
[JsonSerializable(typeof(RemoteHello))]
[JsonSerializable(typeof(RemoteHello[]))]
[JsonSerializable(typeof(RemoteCapabilities))]
[JsonSerializable(typeof(RemoteCapabilities[]))]
[JsonSerializable(typeof(RemoteEndpoint))]
[JsonSerializable(typeof(RemoteEndpoint[]))]
[JsonSerializable(typeof(RemoteBreakpoints))]
[JsonSerializable(typeof(RemoteBreakpoints[]))]
[JsonSerializable(typeof(RemoteManagedLocation))]
[JsonSerializable(typeof(RemoteManagedLocation[]))]
[JsonSerializable(typeof(RemoteManagedVariable))]
[JsonSerializable(typeof(RemoteManagedVariable[]))]
[JsonSerializable(typeof(RemoteBreakpoint))]
[JsonSerializable(typeof(RemoteBreakpoint[]))]
[JsonSerializable(typeof(RemoteStop))]
[JsonSerializable(typeof(RemoteStop[]))]
[JsonSerializable(typeof(RemoteFrame))]
[JsonSerializable(typeof(RemoteFrame[]))]
[JsonSerializable(typeof(RemoteEvaluation))]
[JsonSerializable(typeof(RemoteEvaluation[]))]
[JsonSerializable(typeof(RemoteValue))]
[JsonSerializable(typeof(RemoteValue[]))]
[JsonSerializable(typeof(RemoteHandles))]
[JsonSerializable(typeof(RemoteHandles[]))]
[JsonSerializable(typeof(RemoteId))]
[JsonSerializable(typeof(RemoteId[]))]
[JsonSerializable(typeof(RemoteEmpty))]
[JsonSerializable(typeof(RemoteEmpty[]))]
[JsonSerializable(typeof(RemoteConfiguration))]
[JsonSerializable(typeof(RemoteConfiguration[]))]
[JsonSerializable(typeof(RemoteModuleSymbols))]
[JsonSerializable(typeof(RemoteModuleSymbols[]))]
[JsonSerializable(typeof(RemoteMethodSymbols))]
[JsonSerializable(typeof(RemoteMethodSymbols[]))]
[JsonSerializable(typeof(RemoteSequencePoint))]
[JsonSerializable(typeof(RemoteSequencePoint[]))]
[JsonSerializable(typeof(RemoteTypeSymbols))]
[JsonSerializable(typeof(RemoteTypeSymbols[]))]
[JsonSerializable(typeof(RemoteFieldSymbols))]
[JsonSerializable(typeof(RemoteFieldSymbols[]))]
[JsonSerializable(typeof(RemoteEvent))]
[JsonSerializable(typeof(RemoteEvent[]))]
[JsonSerializable(typeof(RemoteThread))]
[JsonSerializable(typeof(RemoteThread[]))]
[JsonSerializable(typeof(RemoteModule))]
[JsonSerializable(typeof(RemoteModule[]))]
[JsonSerializable(typeof(RemoteScope))]
[JsonSerializable(typeof(RemoteScope[]))]
[JsonSerializable(typeof(RemoteVariables))]
[JsonSerializable(typeof(RemoteVariables[]))]
[JsonSerializable(typeof(RemoteStep))]
[JsonSerializable(typeof(RemoteStep[]))]
[JsonSerializable(typeof(RemoteExceptions))]
[JsonSerializable(typeof(RemoteExceptions[]))]
[JsonSerializable(typeof(RemoteException))]
[JsonSerializable(typeof(RemoteException[]))]
public partial class RemoteJson : JsonSerializerContext;

public interface IRemoteDebugModel : IAsyncDisposable
{
    RemoteCapabilities Capabilities { get; }
    Task<RemoteCapabilities> RequestHelloAsync(RemoteHello request, CancellationToken token);
    Task<RemoteBreakpoint[]> RequestSetBreakpointsAsync(RemoteBreakpoints request, CancellationToken token);
    Task<RemoteEmpty> RequestStartAsync(RemoteEmpty request, CancellationToken token);
    Task<RemoteStop> RequestWaitForStopAsync(RemoteEmpty request, CancellationToken token);
    Task<RemoteFrame[]> RequestStackAsync(RemoteId request, CancellationToken token);
    Task<RemoteValue> RequestEvaluateAsync(RemoteEvaluation request, CancellationToken token);
    Task<RemoteEmpty> RequestContinueAsync(RemoteId request, CancellationToken token);
    Task<RemoteEmpty> RequestReleaseAsync(RemoteHandles request, CancellationToken token);
    Task<RemoteEmpty> RequestDisconnectAsync(RemoteEmpty request, CancellationToken token);
    Task<RemoteEmpty> RequestConfigureAsync(RemoteConfiguration request, CancellationToken token);
    Task<RemoteEvent[]> RequestPollAsync(RemoteEmpty request, CancellationToken token);
    Task<RemoteThread[]> RequestThreadsAsync(RemoteEmpty request, CancellationToken token);
    Task<RemoteModule[]> RequestModulesAsync(RemoteEmpty request, CancellationToken token);
    Task<RemoteScope[]> RequestScopesAsync(RemoteId request, CancellationToken token);
    Task<RemoteValue[]> RequestVariablesAsync(RemoteVariables request, CancellationToken token);
    Task<RemoteEmpty> RequestStepAsync(RemoteStep request, CancellationToken token);
    Task<RemoteEmpty> RequestPauseAsync(RemoteEmpty request, CancellationToken token);
    Task<RemoteEmpty> RequestExceptionsAsync(RemoteExceptions request, CancellationToken token);
    Task<RemoteException> RequestExceptionInfoAsync(RemoteId request, CancellationToken token);
}

public sealed partial class RemoteDebugClient : IRemoteDebugModel
{
    public Task<RemoteCapabilities> RequestHelloAsync(RemoteHello request, CancellationToken token) =>
        CallAsync<RemoteHello, RemoteCapabilities>(RemoteOperation.Hello, request, token);
    public Task<RemoteBreakpoint[]> RequestSetBreakpointsAsync(RemoteBreakpoints request, CancellationToken token) =>
        CallAsync<RemoteBreakpoints, RemoteBreakpoint[]>(RemoteOperation.SetBreakpoints, request, token);
    public Task<RemoteEmpty> RequestStartAsync(RemoteEmpty request, CancellationToken token) =>
        CallAsync<RemoteEmpty, RemoteEmpty>(RemoteOperation.Start, request, token);
    public Task<RemoteStop> RequestWaitForStopAsync(RemoteEmpty request, CancellationToken token) =>
        CallAsync<RemoteEmpty, RemoteStop>(RemoteOperation.WaitForStop, request, token);
    public Task<RemoteFrame[]> RequestStackAsync(RemoteId request, CancellationToken token) =>
        CallAsync<RemoteId, RemoteFrame[]>(RemoteOperation.Stack, request, token);
    public Task<RemoteValue> RequestEvaluateAsync(RemoteEvaluation request, CancellationToken token) =>
        CallAsync<RemoteEvaluation, RemoteValue>(RemoteOperation.Evaluate, request, token);
    public Task<RemoteEmpty> RequestContinueAsync(RemoteId request, CancellationToken token) =>
        CallAsync<RemoteId, RemoteEmpty>(RemoteOperation.Continue, request, token);
    public Task<RemoteEmpty> RequestReleaseAsync(RemoteHandles request, CancellationToken token) =>
        CallAsync<RemoteHandles, RemoteEmpty>(RemoteOperation.Release, request, token);
    public Task<RemoteEmpty> RequestDisconnectAsync(RemoteEmpty request, CancellationToken token) =>
        CallAsync<RemoteEmpty, RemoteEmpty>(RemoteOperation.Disconnect, request, token);
    public Task<RemoteEmpty> RequestConfigureAsync(RemoteConfiguration request, CancellationToken token) =>
        CallAsync<RemoteConfiguration, RemoteEmpty>(RemoteOperation.Configure, request, token);
    public Task<RemoteEvent[]> RequestPollAsync(RemoteEmpty request, CancellationToken token) =>
        CallAsync<RemoteEmpty, RemoteEvent[]>(RemoteOperation.Poll, request, token);
    public Task<RemoteThread[]> RequestThreadsAsync(RemoteEmpty request, CancellationToken token) =>
        CallAsync<RemoteEmpty, RemoteThread[]>(RemoteOperation.Threads, request, token);
    public Task<RemoteModule[]> RequestModulesAsync(RemoteEmpty request, CancellationToken token) =>
        CallAsync<RemoteEmpty, RemoteModule[]>(RemoteOperation.Modules, request, token);
    public Task<RemoteScope[]> RequestScopesAsync(RemoteId request, CancellationToken token) =>
        CallAsync<RemoteId, RemoteScope[]>(RemoteOperation.Scopes, request, token);
    public Task<RemoteValue[]> RequestVariablesAsync(RemoteVariables request, CancellationToken token) =>
        CallAsync<RemoteVariables, RemoteValue[]>(RemoteOperation.Variables, request, token);
    public Task<RemoteEmpty> RequestStepAsync(RemoteStep request, CancellationToken token) =>
        CallAsync<RemoteStep, RemoteEmpty>(RemoteOperation.Step, request, token);
    public Task<RemoteEmpty> RequestPauseAsync(RemoteEmpty request, CancellationToken token) =>
        CallAsync<RemoteEmpty, RemoteEmpty>(RemoteOperation.Pause, request, token);
    public Task<RemoteEmpty> RequestExceptionsAsync(RemoteExceptions request, CancellationToken token) =>
        CallAsync<RemoteExceptions, RemoteEmpty>(RemoteOperation.Exceptions, request, token);
    public Task<RemoteException> RequestExceptionInfoAsync(RemoteId request, CancellationToken token) =>
        CallAsync<RemoteId, RemoteException>(RemoteOperation.ExceptionInfo, request, token);
    public Task<RemoteEmpty> ConfigureAsync(RemoteConfiguration request, CancellationToken token) =>
        RequestConfigureAsync(request, token);
    public Task<RemoteEvent[]> PollAsync(RemoteEmpty request, CancellationToken token) =>
        RequestPollAsync(request, token);
    public Task<RemoteThread[]> ThreadsAsync(RemoteEmpty request, CancellationToken token) =>
        RequestThreadsAsync(request, token);
    public Task<RemoteModule[]> ModulesAsync(RemoteEmpty request, CancellationToken token) =>
        RequestModulesAsync(request, token);
    public Task<RemoteScope[]> ScopesAsync(RemoteId request, CancellationToken token) =>
        RequestScopesAsync(request, token);
    public Task<RemoteValue[]> VariablesAsync(RemoteVariables request, CancellationToken token) =>
        RequestVariablesAsync(request, token);
    public Task<RemoteEmpty> StepAsync(RemoteStep request, CancellationToken token) =>
        RequestStepAsync(request, token);
    public Task<RemoteEmpty> PauseAsync(RemoteEmpty request, CancellationToken token) =>
        RequestPauseAsync(request, token);
    public Task<RemoteEmpty> ExceptionsAsync(RemoteExceptions request, CancellationToken token) =>
        RequestExceptionsAsync(request, token);
    public Task<RemoteException> ExceptionInfoAsync(RemoteId request, CancellationToken token) =>
        RequestExceptionInfoAsync(request, token);
}
