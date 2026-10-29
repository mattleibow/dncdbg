using System.Reflection;
using ClrDebug;
using DncDbg.Engine;

internal static class EvaluationChecks
{
    public static int Run(params string[] scenarios)
    {
        int checks = 0;
        if (scenarios.Length == 0)
            scenarios = ["abort", "fallback", "queued-completion", "late-notification", "other-thread"];
        foreach (string scenario in scenarios)
        {
            var calls = new List<string>();
            using var engine = new DebugEngine();
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            var eval = new Eval(calls, scenario == "abort" ? HRESULT.S_OK : HRESULT.E_FAIL);
            Type pendingType = typeof(DebugEngine).GetNestedType("PendingEval", BindingFlags.NonPublic)!;
            object pending = Activator.CreateInstance(pendingType, new object[] { new CorDebugEval(eval) })!;
            pendingType.GetProperty("ThreadId")!.SetValue(pending, 42);
            pendingType.GetProperty("Completed")!.SetValue(pending, scenario == "late-notification");
            typeof(DebugEngine).GetField("_pendingEval", Private)!.SetValue(engine, pending);
            ICorDebugThread thread = CallbackProxy.Make<ICorDebugThread>((method, arguments) =>
            {
                if (method == "GetID")
                {
                    arguments![0] = scenario == "other-thread" ? 43 : 42;
                    return HRESULT.S_OK;
                }
                throw new NotSupportedException(method);
            });
            ICorDebugAppDomain domain = CallbackProxy.Make<ICorDebugAppDomain>((method, arguments) =>
            {
                if (method == "HasQueuedCallbacks")
                {
                    arguments![1] = scenario == "queued-completion";
                    return HRESULT.S_OK;
                }
                if (method == "Continue")
                {
                    calls.Add("Continue");
                    return HRESULT.S_OK;
                }
                throw new NotSupportedException(method);
            });
            CorDebugManagedCallbackEventArgs notification = scenario == "queued-completion"
                ? new EvalCompleteCorDebugManagedCallbackEventArgs(domain, thread, eval)
                : new CustomNotificationCorDebugManagedCallbackEventArgs(thread, domain);
            typeof(DebugEngine).GetMethod("OnManagedEvent", Private)!.Invoke(engine, [null, notification]);

            if (scenario == "queued-completion")
            {
                Check(calls.Count == 0, "owned completion does not continue to drain ordinary callbacks");
                Check((bool)pendingType.GetProperty("Completed")!.GetValue(pending)!,
                    "owned completion remains terminal even with queued callbacks");
                Check((bool)typeof(DebugEngine).GetField("_stopped", Private)!.GetValue(engine)!,
                    "owned completion keeps the debuggee synchronized");
            }
            else if (scenario is "late-notification" or "other-thread")
            {
                Check(calls.SequenceEqual(new[] { "Continue" }), "notification without an active owned eval cannot abort");
                Check(!(bool)pendingType.GetProperty("CrossThreadDependency")!.GetValue(pending)!,
                    "unowned notification cannot change the evaluation result");
            }
            else
            {
                string[] expected = scenario == "abort" ? ["Abort", "Continue"] : ["Abort", "RudeAbort", "Continue"];
                Check(calls.SequenceEqual(expected), "notification aborts with immediate failure fallback before continuing");
                Check((bool)pendingType.GetProperty("CrossThreadDependency")!.GetValue(pending)!,
                    "successful notification abort records cross-thread dependency");
                Check(!(bool)pendingType.GetProperty("Completed")!.GetValue(pending)!,
                    "issuing an abort never fabricates completion");
                Check(ReferenceEquals(typeof(DebugEngine).GetField("_pendingEval", Private)!.GetValue(engine), pending),
                    "the evaluation stays owned until its completion callback");
                Check(((System.Collections.ICollection)typeof(DebugEngine).GetField("_abandonedEvals", Private)!
                    .GetValue(engine)!).Count == 0, "notification does not abandon the evaluation thread");
            }

            void Check(bool condition, string name)
            {
                if (!condition)
                    throw new InvalidOperationException($"Evaluation check failed ({scenario}): {name}");
                checks++;
            }
        }
        return checks;
    }

    public class CallbackProxy : DispatchProxy
    {
        public Func<string, object?[]?, HRESULT> Action { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Action(method!.Name, args);
        public static T Make<T>(Func<string, object?[]?, HRESULT> action) where T : class
        {
            T proxy = Create<T, CallbackProxy>();
            ((CallbackProxy)(object)proxy).Action = action;
            return proxy;
        }
    }

    private sealed class Eval(List<string> calls, HRESULT abortResult) : ICorDebugEval, ICorDebugEval2
    {
        public HRESULT Abort() { calls.Add("Abort"); return abortResult; }
        public HRESULT RudeAbort() { calls.Add("RudeAbort"); return HRESULT.S_OK; }
        public HRESULT CallFunction(ICorDebugFunction f, int n, ICorDebugValue[] a) => throw new NotSupportedException();
        public HRESULT NewObject(ICorDebugFunction f, int n, ICorDebugValue[] a) => throw new NotSupportedException();
        public HRESULT NewObjectNoConstructor(ICorDebugClass c) => throw new NotSupportedException();
        public HRESULT NewString(string s) => throw new NotSupportedException();
        public HRESULT NewArray(CorElementType t, ICorDebugClass c, int rank, int[] dimensions, int[] bases) => throw new NotSupportedException();
        public HRESULT IsActive(out bool active) => throw new NotSupportedException();
        public HRESULT GetResult(out ICorDebugValue value) => throw new NotSupportedException();
        public HRESULT GetThread(out ICorDebugThread thread) => throw new NotSupportedException();
        public HRESULT CreateValue(CorElementType t, ICorDebugClass c, out ICorDebugValue value) => throw new NotSupportedException();
        public HRESULT CallParameterizedFunction(ICorDebugFunction f, int n, ICorDebugType[] t, int count, ICorDebugValue[] a) => throw new NotSupportedException();
        public HRESULT CreateValueForType(ICorDebugType t, out ICorDebugValue value) => throw new NotSupportedException();
        public HRESULT NewParameterizedObject(ICorDebugFunction f, int n, ICorDebugType[] t, int count, ICorDebugValue[] a) => throw new NotSupportedException();
        public HRESULT NewParameterizedObjectNoConstructor(ICorDebugClass c, int n, ICorDebugType[] t) => throw new NotSupportedException();
        public HRESULT NewParameterizedArray(ICorDebugType t, int rank, int[] dimensions, int[] bases) => throw new NotSupportedException();
        public HRESULT NewStringWithLength(string s, int length) => throw new NotSupportedException();
    }
}
