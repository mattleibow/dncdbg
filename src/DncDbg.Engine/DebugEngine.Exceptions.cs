using ClrDebug;
using DncDbg.Engine.Symbols;
using DncDbg.Engine.Values;

namespace DncDbg.Engine;

public sealed partial class DebugEngine
{
    private const string InnerExceptionWalkLimitVariable = "DNCDBG_INNER_EXCEPTION_WALK_LIMIT";
    private const uint DefaultInnerExceptionWalkLimit = 32;
    private uint? _initialInnerExceptionWalkLimit;
    private uint _innerExceptionWalkLimit = DefaultInnerExceptionWalkLimit;

    private void ConfigureExceptionDetails(IReadOnlyDictionary<string, string?>? environment)
    {
        _initialInnerExceptionWalkLimit ??= ParseInnerExceptionWalkLimit(Environment.GetEnvironmentVariable(InnerExceptionWalkLimitVariable));
        _innerExceptionWalkLimit = environment?.TryGetValue(InnerExceptionWalkLimitVariable, out string? value) == true
            ? ParseInnerExceptionWalkLimit(value)
            : _initialInnerExceptionWalkLimit.Value;
    }

    private uint ParseInnerExceptionWalkLimit(string? value)
        => ParseEnvironmentNumber(value, InnerExceptionWalkLimitVariable, DefaultInnerExceptionWalkLimit);

    private uint ParseEnvironmentNumber(string? value, string variable, uint defaultValue,
        uint minimum = 0, uint maximum = uint.MaxValue)
    {
        if (value == null)
            return defaultValue;
        if (value.Length > 0 && value.All(static c => c is >= '0' and <= '9') &&
            uint.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out uint number) &&
            number >= minimum && number <= maximum)
            return number;
        Log?.Invoke($"Invalid {variable} value '{value}'; expected an unsigned decimal number in range {minimum}-{maximum}, using the default value {defaultValue}.");
        return defaultValue;
    }

    public const string FilterAll = "all";
    public const string FilterUnhandled = "unhandled";
    public const string FilterUserUnhandled = "user-unhandled";

    // thread id -> break mode of the exception the thread is stopped at
    private readonly Dictionary<int, string> _exceptionStops = [];
    private readonly HashSet<int> _firstChanceReported = [];
    // threads whose exception in flight was thrown in, or has propagated through, user code
    private readonly HashSet<int> _passedUserCode = [];
    private bool _breakOnThrown;
    private bool _breakOnUserUnhandled;
    private bool _diedOfUnhandledException;

    public void SetExceptionFilters(IReadOnlyCollection<string> filters)
        => SetExceptionFilters(filters.Select(filter => new ExceptionFilterRequest(filter)).ToArray());

    private EventAction OnException(Exception2CorDebugManagedCallbackEventArgs e)
    {
        CorDebugThread thread = e.Thread;
        int threadId = thread.Id;
        switch (e.EventType)
        {
            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_FIRST_CHANCE:
            {
                _firstChanceReported.Remove(threadId);
                _passedUserCode.Remove(threadId);
                bool inUserCode = IsUserFrame(e.Frame);
                if (inUserCode)
                    _passedUserCode.Add(threadId);
                if (!_breakOnThrown || !MatchesExceptionCondition(FilterAll, thread))
                    return EventAction.Continue;
                _firstChanceReported.Add(threadId);
                return StopAtException(thread, "always");
            }

            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_USER_FIRST_CHANCE:
                _passedUserCode.Add(threadId);
                if (!_breakOnThrown || !MatchesExceptionCondition(FilterAll, thread) || !_firstChanceReported.Add(threadId))
                    return EventAction.Continue;
                return StopAtException(thread, "always");

            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_CATCH_HANDLER_FOUND:
            {
                // The stack is still intact here. Code the user cannot see is about to swallow an exception that came
                // out of the user's code: for the user that is as good as unhandled.
                bool userUnhandled = _breakOnUserUnhandled && _justMyCode && _passedUserCode.Contains(threadId)
                    && !IsUserFrame(e.Frame)
                    && MatchesExceptionCondition(FilterUserUnhandled, thread);
                _firstChanceReported.Remove(threadId);
                _passedUserCode.Remove(threadId);
                return userUnhandled ? StopAtException(thread, "userUnhandled") : EventAction.Continue;
            }

            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED:
                _firstChanceReported.Remove(threadId);
                _passedUserCode.Remove(threadId);
                _diedOfUnhandledException = true;
                // Unhandled exceptions stop by default; only a matching negated unhandled condition suppresses them.
                return MatchesExceptionCondition(FilterUnhandled, thread)
                    ? StopAtException(thread, "unhandled")
                    : EventAction.Continue;

            default:
                return EventAction.Continue;
        }
    }

    private bool IsUserFrame(CorDebugFrame? frame)
    {
        try
        {
            return frame != null && IsUserCode(frame.Function);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private EventAction StopAtException(CorDebugThread thread, string breakMode)
    {
        int threadId = thread.Id;
        _exceptionStops[threadId] = breakMode;
        ExceptionData? details = ReadException(() => thread.CurrentException, breakMode, null);
        string kind = breakMode switch { "unhandled" => "unhandled", "userUnhandled" => "user-unhandled", _ => "thrown" };
        string description = details == null ? "Exception" : $"Exception {kind}: {details.TypeName}";
        Post(() => Stopped?.Invoke(new StopInfo("exception", threadId, description, details?.Message)));
        return EventAction.Stop;
    }

    /// <param name="evalThreadId">Thread to run the StackTrace getter on; null to read fields only.</param>
    private ExceptionData? ReadException(Func<CorDebugValue?> getter, string breakMode, int? evalThreadId)
    {
        var details = new List<(string TypeName, string? Message, string? StackTrace)>();
        try
        {
            for (uint depth = 0; ; depth++)
            {
                CorDebugValue? exception = getter();
                if (exception == null || ValueInspector.Unwrap(exception, out bool isNull) == null || isNull)
                    break;
                Func<CorDebugValue?> stable = Stabilize(getter);
                string typeName = _values.GetTypeName(exception);
                string? message = _values.ReadString(_values.GetFieldByName(exception, "_message"));
                string? stackTrace = _values.ReadString(_values.GetFieldByName(stable()!, "_stackTraceString"));
                if (stackTrace == null && evalThreadId is { } threadId)
                    stackTrace = ReadStackTraceProperty(stable, threadId);
                details.Add((typeName, message, stackTrace));
                if (depth == _innerExceptionWalkLimit)
                    break;
                getter = () => _values.GetFieldByName(stable()!, "_innerException");
            }
        }
        catch (Exception e)
        {
            Log?.Invoke("Failed to read the current exception: " + e.Message);
        }
        ExceptionData? result = null;
        for (int i = details.Count - 1; i >= 0; i--)
            result = new ExceptionData(details[i].TypeName, details[i].Message, breakMode, details[i].StackTrace, result);
        return result;
    }

    private string? ReadStackTraceProperty(Func<CorDebugValue?> exception, int threadId)
    {
        try
        {
            CorDebugValue self = exception()!;
            foreach (ValueInspector.TypeLevel level in _values.GetTypeLevels(ValueInspector.Unwrap(self, out _)!))
            {
                if (level.Metadata.GetTypeName(level.Token) != "System.Exception")
                    continue;
                PropertyDescription? property = level.Metadata.GetProperties(level.Token).FirstOrDefault(p => p.Name == "StackTrace");
                if (property == null)
                    return null;
                CorDebugFunction getter = level.Type.Class.Module.GetFunctionFromToken(new mdMethodDef(property.GetterToken));
                return _values.ReadString(RunEval(threadId, eval => eval.CallFunction(getter.Raw, 1, [self.Raw])));
            }
        }
        catch (Exception e)
        {
            Log?.Invoke("Failed to evaluate Exception.StackTrace: " + e.Message);
        }
        return null;
    }

    /// <summary>Details of the exception <paramref name="threadId"/> is currently stopped at.</summary>
    public ExceptionData? GetExceptionDetails(int threadId)
    {
        lock (_lock)
        {
            RequireStopped();
            RequireThread(threadId); // ReadException swallows what goes wrong, an unknown thread included
            string breakMode = _exceptionStops.GetValueOrDefault(threadId, "always");
            return ReadException(() => RequireThread(threadId).CurrentException, breakMode, threadId);
        }
    }
}
